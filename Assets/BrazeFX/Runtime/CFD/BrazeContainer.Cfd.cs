using UnityEngine;
using BrazeFX.CFD;

namespace BrazeFX
{
    public sealed partial class BrazeContainer
    {
        BrazeCfdDomain cfdDomain;
        BrazeSolver.GpuCfdBoundary[] cfdBoundaryData;

        /// <summary>The active CFD companion, or null while this is an ordinary Fire container.</summary>
        public BrazeCfdDomain CfdDomain => CfdActive ? cfdDomain : null;
        /// <summary>CFD-only renderer view. Null keeps the ordinary Fire render path byte-for-byte selected.</summary>
        public BrazeCfdHeatMap CfdHeatMap => CfdActive ? cfdDomain.HeatMap : null;
        /// <summary>The simple-authoring tunnel targeting this domain. Queried from the tiny active registry so
        /// disabling/removing the facade immediately restores the ordinary volume-render path.</summary>
        public BrazeCfdWindTunnel CfdWindTunnel
        {
            get
            {
                if (!CfdActive) return null;
                for (int i = 0; i < BrazeCfdWindTunnel.Active.Count; i++)
                {
                    BrazeCfdWindTunnel tunnel = BrazeCfdWindTunnel.Active[i];
                    if (tunnel != null && tunnel.isActiveAndEnabled && tunnel.domain == cfdDomain) return tunnel;
                }
                return null;
            }
        }
        /// <summary>The active CFD presentation facade. Wind Tunnel wins if both facades target one domain so an
        /// accidental advanced Visualizer can never override the simple facade's boundary-coupled presentation.</summary>
        internal IBrazeCfdVisualization CfdVisualization
        {
            get
            {
                BrazeCfdWindTunnel tunnel = CfdWindTunnel;
                if (tunnel != null) return tunnel;
                if (!CfdActive) return null;
                for (int i = 0; i < BrazeCfdVisualizer.Active.Count; i++)
                {
                    BrazeCfdVisualizer visualizer = BrazeCfdVisualizer.Active[i];
                    if (visualizer != null && visualizer.isActiveAndEnabled && visualizer.domain == cfdDomain)
                        return visualizer;
                }
                return null;
            }
        }
        internal bool CfdActive => cfdDomain != null && cfdDomain.isActiveAndEnabled;
        public float CfdInletVolumeFlowM3PerSecond => solver != null ? solver.CfdInletVolumeFlowM3PerSecond : 0f;
        public float CfdOutletVolumeFlowM3PerSecond => solver != null ? solver.CfdOutletVolumeFlowM3PerSecond : 0f;
        public Vector3 CfdRelativePressureLoadWorld => solver != null
            ? transform.rotation * solver.CfdRelativePressureLoadLocal : Vector3.zero;
        internal bool CfdTemperaturePeakValid => solver != null && solver.CfdTemperaturePeakValid;
        internal float CfdTemperaturePeakHeatUnits => solver != null ? solver.CfdTemperaturePeakHeatUnits : 0f;

        internal void AttachCfdDomain(BrazeCfdDomain candidate)
        {
            if (candidate != null && candidate.Container == this) cfdDomain = candidate;
        }

        internal void DetachCfdDomain(BrazeCfdDomain candidate)
        {
            if (cfdDomain != candidate) return;
            cfdDomain = null;
            if (solver != null)
            {
                solver.CfdThermalOccupancy = false;
                solver.RenderFieldEnabled = true;
                solver.SetCfdSignedDistance(null, 0, 0f);
                solver.SetCfdBoundaries(null, 0, false);
                solver.SetCfdAnimatedBodies(null, 0);
                solver.SetCfdSurfaceDiagnostics(false, Vector3.zero);
                solver.CfdTemperatureDiagnosticsEnabled = false;
            }
        }

        int GatherCfdSources(float dt)
        {
            if (!CfdActive) return 0;
            Vector3Int r = solver.Res;
            Vector3 ws = WorldSize();
            Quaternion worldToGrid = Quaternion.Inverse(transform.rotation);
            float voxPerWorld = VoxPerWorld();
            // NB: no gatheredDrag here — CFD damping goes through frame.cfdLinearDrag (resolution-independent);
            // the frame policy zeroes the quadratic Fire drag channel explicitly.
            gatheredJet = 0f;
            gatheredSplineVortexSampleCount = 0;
            int count = 0;

            var sources = BrazeCfdSource.Active;
            for (int i = 0; i < sources.Count; i++)
            {
                BrazeCfdSource source = sources[i];
                if (source == null || !source.isActiveAndEnabled || !source.Targets(cfdDomain)) continue;

                BrazeCfdSourceShape shape;
                float radius;
                float height;
                float heatRate;
                float passiveDyeRate = 0f;
                float signedSwirlSpeedWorld = 0f;
                BrazeCfdFlowDyePattern dyePattern = BrazeCfdFlowDyePattern.Full;
                int dyeStreakCount = 1;
                float dyeStreakWidth = 0.2f;
                float fluidShellThickness = 0f;
                Vector3 velocity = Vector3.zero;
                float velocityResponse = 0f;

                if (source is BrazeCfdFlowSource flow)
                {
                    shape = flow.shape;
                    radius = flow.radius;
                    height = flow.height;
                    heatRate = flow.heatRateCPerSecond;
                    passiveDyeRate = flow.PassiveDyeRate;
                    signedSwirlSpeedWorld = flow.ResolveSignedSwirlSpeed(dt);
                    dyePattern = flow.passiveDyePattern;
                    dyeStreakCount = flow.dyeStreakCount;
                    dyeStreakWidth = flow.dyeStreakWidth;
                    velocity = flow.transform.forward * flow.speed;
                    velocityResponse = flow.velocityResponse;
                }
                else if (source is BrazeCfdThermalSource thermal)
                {
                    shape = thermal.shape;
                    radius = thermal.radius;
                    height = thermal.height;
                    heatRate = thermal.heatRateCPerSecond;
                    fluidShellThickness = thermal.coupling == BrazeCfdThermalCoupling.SurfaceToFluid
                        ? Mathf.Max(thermal.fluidShellThickness,
                            Mathf.Max(Mathf.Abs(ws.x) / Mathf.Max(1, r.x),
                            Mathf.Max(Mathf.Abs(ws.y) / Mathf.Max(1, r.y),
                                      Mathf.Abs(ws.z) / Mathf.Max(1, r.z))))
                        : 0f;
                }
                else continue;

                if (count >= emitterData.Length)
                    System.Array.Resize(ref emitterData, emitterData.Length * 2);

                PackCfdGeometry(source.transform, shape, radius, height, fluidShellThickness, r, ws, voxPerWorld,
                                out Vector3 posVox, out float radiusVox, out Vector3 halfExtentVox);
                Vector3 gridVelocity = WorldVelToGrid(velocity, r, worldToGrid, ws);
                float gridSwirlSpeed = signedSwirlSpeedWorld * voxPerWorld;
                gatheredJet = Mathf.Max(gatheredJet, gridVelocity.magnitude + Mathf.Abs(gridSwirlSpeed));
                emitterData[count++] = new BrazeSolver.GpuEmitter
                {
                    posVox = posVox,
                    radiusVox = radiusVox,
                    // Density is a passive visualization scalar in CFD; it does not enter pressure, velocity,
                    // or thermal buoyancy. Unlike a boundary concentration, an internal source contributes a
                    // rate and the shared Emit kernel applies dt exactly once.
                    channels = new Vector3(Mathf.Max(0f, passiveDyeRate),
                        Mathf.Max(0f, heatRate) * cfdDomain.HeatUnitsPerDegree, 0f),
                    shape = (float)shape,
                    halfExtentVox = halfExtentVox,
                    // CFD buoyancy is DOMAIN-WIDE (frame.cfdThermalBuoyancy): all transported heat lifts,
                    // including inlet-patch heat that has no emitter. Packing it per-emitter as well would
                    // double-count source-emitted heat.
                    motionBuoyancy = 0f,
                    sourceVelocity = gridVelocity,
                    sourceVelocityBlend = Mathf.Max(0f, velocityResponse),
                    sourceSwirlAxis = (worldToGrid * source.transform.forward).normalized,
                    sourceSwirlSpeed = gridSwirlSpeed,
                    sourceDyePattern = passiveDyeRate > 0f ? (float)dyePattern : 0f,
                    sourceDyeStreakCount = Mathf.Clamp(dyeStreakCount, 1, 12),
                    sourceDyeStreakWidth = Mathf.Clamp(dyeStreakWidth, 0.05f, 0.9f),
                    rot = QuatVec4(worldToGrid * source.transform.rotation),
                };
            }
            return count;
        }

        void ApplyCfdFramePolicy(ref BrazeSolver.FrameParams frame)
        {
            if (!CfdActive) return;
            cfdDomain.ApplyFramePolicy(ref frame);
            frame.maxSpeed = Mathf.Max(frame.maxSpeed, cfdDomain.maxAirSpeed * VoxPerWorld());
        }

        void ApplyCfdSolverPolicy(float dt)
        {
            if (solver == null) return;
            solver.CfdThermalOccupancy = CfdActive && cfdDomain.thermalOccupancy;
            IBrazeCfdVisualization visualization = CfdVisualization;
            solver.CfdTemperatureDiagnosticsEnabled = CfdActive && visualization != null &&
                                                      visualization.TemperatureAutoRangeEnabled;
            // Slice mode never draws the volume: skip the per-tick render-field build (full-grid pass).
            solver.RenderFieldEnabled = !CfdVolumeSuppressed;
            if (CfdActive)
            {
                cfdDomain.EnsureSignedDistance(solver.Res, out RenderTexture sdf, out int version);
                float cellWorldSize = Mathf.Abs(WorldSize().x) / Mathf.Max(1, solver.Res.x);
                solver.SetCfdSignedDistance(sdf, version, cellWorldSize);
                int boundaryCount = GatherCfdBoundaries();
                // Full-domain policy: patches communicate through the whole tunnel; without patches, clear-air
                // flow is invisible to content occupancy and still needs the global solve unless the author
                // explicitly opts into a localized study.
                solver.SetCfdBoundaries(cfdBoundaryData, boundaryCount,
                    boundaryCount > 0 ? cfdDomain.fullDomainSolveWithBoundaryPatches : cfdDomain.fullDomainSolve);
                int animatedCount = GatherCfdAnimatedBodies(dt);
                solver.SetCfdAnimatedBodies(cfdAnimatedBodyData, animatedCount);
                Vector3 cell = new(Mathf.Abs(WorldSize().x) / Mathf.Max(1, solver.Res.x),
                                   Mathf.Abs(WorldSize().y) / Mathf.Max(1, solver.Res.y),
                                   Mathf.Abs(WorldSize().z) / Mathf.Max(1, solver.Res.z));
                cfdDomain.GetBodyVoxelWindow(solver.Res, out Vector3Int loadMin, out Vector3Int loadSize);
                // Surface diagnostics window: union of the static baked bounds and every animated body footprint.
                for (int i = 0; i < animatedCount; i++)
                {
                    BrazeSolver.CfdAnimatedBody body = cfdAnimatedBodyData[i];
                    if (loadSize.x <= 0 || loadSize.y <= 0 || loadSize.z <= 0)
                    {
                        loadMin = body.windowMin;
                        loadSize = body.windowSize;
                        continue;
                    }
                    Vector3Int max = Vector3Int.Max(loadMin + loadSize, body.windowMin + body.windowSize);
                    loadMin = Vector3Int.Min(loadMin, body.windowMin);
                    loadSize = max - loadMin;
                }
                solver.SetCfdSurfaceDiagnostics(cfdDomain.SurfacePressure != null,
                    new Vector3(cell.y * cell.z, cell.x * cell.z, cell.x * cell.y), loadMin, loadSize);
            }
            else
            {
                solver.SetCfdSignedDistance(null, 0, 0f);
                solver.SetCfdBoundaries(null, 0, false);
                solver.SetCfdAnimatedBodies(null, 0);
                solver.SetCfdSurfaceDiagnostics(false, Vector3.zero);
            }
        }

        BrazeSolver.CfdAnimatedBody[] cfdAnimatedBodyData;

        // One entry per animated (rigid-motion) boundary targeting this domain. All transforms are folded into a
        // single voxel→bake-uvw matrix per body, and velocities are converted to grid units so the solver kernels
        // never touch world space.
        int GatherCfdAnimatedBodies(float dt)
        {
            Vector3Int r = solver.Res;
            Vector3 ws = WorldSize();
            Quaternion worldToGrid = Quaternion.Inverse(transform.rotation);
            Matrix4x4 worldFromVoxel = Matrix4x4.TRS(transform.position, transform.rotation, ws)
                                     * Matrix4x4.Translate(new Vector3(-0.5f, -0.5f, -0.5f))
                                     * Matrix4x4.Scale(new Vector3(1f / r.x, 1f / r.y, 1f / r.z));
            int count = 0;
            cfdDomain.BeginAnimatedSdfGather();
            var boundaries = BrazeCfdSdfBoundary.Active;
            for (int i = 0; i < boundaries.Count; i++)
            {
                BrazeCfdSdfBoundary boundary = boundaries[i];
                if (boundary == null || !boundary.isActiveAndEnabled || !boundary.animated ||
                    !boundary.Targets(cfdDomain)) continue;
                if (!cfdDomain.EnsureAnimatedSdf(boundary, out RenderTexture sdf,
                        out Vector3 localMin, out Vector3 localSize)) continue;

                Transform meshTransform = boundary.meshFilter != null
                    ? boundary.meshFilter.transform : boundary.transform;
                // world → scaled-local (rotation/translation removed, authored scale kept — the bake's space).
                Matrix4x4 scaledLocalFromWorld = Matrix4x4.Scale(meshTransform.lossyScale)
                                               * meshTransform.worldToLocalMatrix;
                Matrix4x4 uvwFromWorld = Matrix4x4.Scale(new Vector3(1f / localSize.x, 1f / localSize.y,
                                                                     1f / localSize.z))
                                       * Matrix4x4.Translate(-localMin)
                                       * scaledLocalFromWorld;

                boundary.ComputeRigidMotion(dt, out Vector3 linearWorld, out Vector3 angularWorld);
                // Current world-voxel footprint from the scaled-local bounds' eight corners.
                Matrix4x4 worldFromScaledLocal = Matrix4x4.TRS(meshTransform.position, meshTransform.rotation,
                                                               Vector3.one);
                Vector3Int windowMin = new(int.MaxValue, int.MaxValue, int.MaxValue);
                Vector3Int windowMax = new(int.MinValue, int.MinValue, int.MinValue);
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 local = localMin + Vector3.Scale(localSize,
                        new Vector3(corner & 1, (corner >> 1) & 1, (corner >> 2) & 1));
                    Vector3 vox = WorldToVoxel(worldFromScaledLocal.MultiplyPoint3x4(local));
                    windowMin = Vector3Int.Min(windowMin, new Vector3Int(
                        Mathf.FloorToInt(vox.x), Mathf.FloorToInt(vox.y), Mathf.FloorToInt(vox.z)));
                    windowMax = Vector3Int.Max(windowMax, new Vector3Int(
                        Mathf.CeilToInt(vox.x), Mathf.CeilToInt(vox.y), Mathf.CeilToInt(vox.z)));
                }
                windowMin = Vector3Int.Max(windowMin - Vector3Int.one * 2, Vector3Int.zero);
                windowMax = Vector3Int.Min(windowMax + Vector3Int.one * 2, r);

                if (cfdAnimatedBodyData == null)
                    cfdAnimatedBodyData = new BrazeSolver.CfdAnimatedBody[4];
                else if (count >= cfdAnimatedBodyData.Length)
                    System.Array.Resize(ref cfdAnimatedBodyData, cfdAnimatedBodyData.Length * 2);
                cfdAnimatedBodyData[count++] = new BrazeSolver.CfdAnimatedBody
                {
                    sdf = sdf,
                    voxelToUvw = uvwFromWorld * worldFromVoxel,
                    linearVelVox = WorldVelToGrid(linearWorld, r, worldToGrid, ws),
                    angularVelGrid = worldToGrid * angularWorld,
                    pivotVox = WorldToVoxel(meshTransform.position),
                    windowMin = windowMin,
                    windowSize = Vector3Int.Max(windowMax - windowMin, Vector3Int.zero),
                };
            }
            cfdDomain.EndAnimatedSdfGather();
            return count;
        }

        int GatherCfdBoundaries()
        {
            Vector3Int r = solver.Res;
            Vector3 ws = WorldSize();
            Quaternion worldToGrid = Quaternion.Inverse(transform.rotation);
            float cellVolume = Mathf.Abs(ws.x * ws.y * ws.z) / Mathf.Max(1, r.x * r.y * r.z);
            int count = 0;
            var patches = BrazeCfdBoundaryPatch.Active;
            for (int i = 0; i < patches.Count; i++)
            {
                BrazeCfdBoundaryPatch patch = patches[i];
                if (patch == null || !patch.isActiveAndEnabled || !patch.Targets(cfdDomain)) continue;
                if (cfdBoundaryData == null)
                    cfdBoundaryData = new BrazeSolver.GpuCfdBoundary[4];
                else if (count >= cfdBoundaryData.Length)
                    System.Array.Resize(ref cfdBoundaryData, cfdBoundaryData.Length * 2);

                Vector3 center = patch.managedCenterOnDomain
                    ? WorldToVoxel(cfdDomain.Container.transform.position)
                    : WorldToVoxel(patch.transform.position);
                BrazeCfdBoundaryPatch.LocalTangentAxes(patch.face, out Vector3 axisU, out Vector3 axisV);
                // Authored face offset (world units along the tangent axes) → voxel units per axis.
                Vector3 localOffset = axisU * patch.faceOffset.x + axisV * patch.faceOffset.y;
                center += new Vector3(localOffset.x / Mathf.Max(Mathf.Abs(ws.x), 1e-4f) * r.x,
                                      localOffset.y / Mathf.Max(Mathf.Abs(ws.y), 1e-4f) * r.y,
                                      localOffset.z / Mathf.Max(Mathf.Abs(ws.z), 1e-4f) * r.z);
                Vector3 outward = BrazeCfdBoundaryPatch.LocalOutwardNormal(patch.face);
                Vector3 localVelocity = -outward * patch.normalSpeed
                                      + axisU * patch.tangentialVelocity.x
                                      + axisV * patch.tangentialVelocity.y;
                Vector3 gridVelocity = WorldVelToGrid(transform.rotation * localVelocity, r, worldToGrid, ws);

                Vector2 patchSize = patch.EffectiveSize;   // radius-driven shapes derive their bounding box
                float halfU, halfV;
                switch (patch.face)
                {
                    case BrazeCfdDomainFace.NegativeX:
                    case BrazeCfdDomainFace.PositiveX:
                        halfU = patchSize.x * 0.5f / Mathf.Max(ws.y, 1e-4f) * r.y;
                        halfV = patchSize.y * 0.5f / Mathf.Max(ws.z, 1e-4f) * r.z;
                        break;
                    case BrazeCfdDomainFace.NegativeY:
                    case BrazeCfdDomainFace.PositiveY:
                        halfU = patchSize.x * 0.5f / Mathf.Max(ws.x, 1e-4f) * r.x;
                        halfV = patchSize.y * 0.5f / Mathf.Max(ws.z, 1e-4f) * r.z;
                        break;
                    default:
                        halfU = patchSize.x * 0.5f / Mathf.Max(ws.x, 1e-4f) * r.x;
                        halfV = patchSize.y * 0.5f / Mathf.Max(ws.y, 1e-4f) * r.y;
                        break;
                }

                float inletTemperature = patch.setInletTemperature
                    ? Mathf.Max(0f, patch.inletTemperatureC - cfdDomain.ambientTemperatureC) *
                      cfdDomain.HeatUnitsPerDegree
                    : -1f;
                cfdBoundaryData[count++] = new BrazeSolver.GpuCfdBoundary
                {
                    centerVox = center,
                    face = (float)patch.face,
                    velocityVox = gridVelocity,
                    mode = (float)patch.mode,
                    halfSizeVox = new Vector2(Mathf.Max(0.5f, halfU), Mathf.Max(0.5f, halfV)),
                    gaugePressure = patch.gaugePressure,
                    inletTemperature = inletTemperature,
                    cellVolumeWorld = cellVolume,
                    preventBackflow = patch.preventBackflow ? 1f : 0f,
                    inletSmokeDensity = patch.setInletSmoke ? Mathf.Clamp01(patch.inletSmokeDensity) : -1f,
                    inletSmokePattern = (float)patch.inletSmokePattern,
                    shape = patch.GpuShape,
                    ringInner = Mathf.Clamp(patch.ringInnerFraction, 0f, 0.95f),
                    rotationRadians = patch.faceRotation * Mathf.Deg2Rad,
                };
            }
            return count;
        }

        void PackCfdGeometry(Transform source, BrazeCfdSourceShape shape, float radius, float height,
                             float fluidShellWorld,
                             Vector3Int r, Vector3 ws, float voxPerWorld,
                             out Vector3 posVox, out float radiusVox, out Vector3 halfExtentVox)
        {
            posVox = WorldToVoxel(source.position);
            Vector3 scale = source.lossyScale;
            Vector3 absScale = new(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            Vector3 shellVox = new(
                Mathf.Max(0f, fluidShellWorld) / Mathf.Max(Mathf.Abs(ws.x), 1e-4f) * r.x,
                Mathf.Max(0f, fluidShellWorld) / Mathf.Max(Mathf.Abs(ws.y), 1e-4f) * r.y,
                Mathf.Max(0f, fluidShellWorld) / Mathf.Max(Mathf.Abs(ws.z), 1e-4f) * r.z);
            if (shape == BrazeCfdSourceShape.Box)
            {
                halfExtentVox = new Vector3(absScale.x * 0.5f / Mathf.Max(ws.x, 1e-4f) * r.x,
                                            absScale.y * 0.5f / Mathf.Max(ws.y, 1e-4f) * r.y,
                                            absScale.z * 0.5f / Mathf.Max(ws.z, 1e-4f) * r.z) + shellVox;
                radiusVox = Mathf.Max(0.5f, Mathf.Min(halfExtentVox.x,
                                          Mathf.Min(halfExtentVox.y, halfExtentVox.z)) * 0.3f);
                return;
            }

            float radialScale = Mathf.Max(absScale.x, absScale.z);
            radiusVox = Mathf.Max(0.5f, radius * radialScale * voxPerWorld +
                                        Mathf.Max(0f, fluidShellWorld) * voxPerWorld);
            halfExtentVox = shape == BrazeCfdSourceShape.Cylinder
                ? new Vector3(radiusVox,
                    Mathf.Max(0.5f, height * absScale.y * 0.5f * voxPerWorld + shellVox.y), radiusVox)
                : Vector3.one * radiusVox;
        }
    }
}
