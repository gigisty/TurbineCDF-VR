using UnityEngine;

namespace BrazeFX.CFD
{
    /// <summary>
    /// Opts the <see cref="BrazeContainer"/> on this GameObject into the isolated CFD workflow.
    /// Removing or disabling this component restores the ordinary Fire solver path.
    /// </summary>
    [AddComponentMenu("BrazeFX/CFD/CFD Domain", 0)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(BrazeContainer))]
    [ExecuteAlways]
    [DefaultExecutionOrder(-100)]
    public sealed class BrazeCfdDomain : MonoBehaviour
    {
        [Header("Authoring")]
        [Tooltip("Reveal the underlying Braze Container inspector. Off keeps the simple three-component workflow.")]
        public bool showAdvancedContainer;

        [Header("Environment")]
        [Tooltip("Reference air temperature. CFD source temperatures are expressed relative to this value.")]
        public float ambientTemperatureC = 20f;
        [Tooltip("Temperature difference represented by one solver heat unit. A larger range allows hotter sources " +
                 "with less visual contrast.")]
        [Min(1f)] public float temperatureRangeC = 80f;
        [Tooltip("Approximate time for transported heat to cool by one e-fold. This is a believable realtime loss " +
                 "control, not a material conductivity model.")]
        [Min(0.1f)] public float thermalPersistenceSeconds = 30f;

        [Header("Airflow")]
        [Tooltip("Upward acceleration applied per normalized heat unit. Kept in the shared GPU buoyancy path.")]
        [Min(0f)] public float thermalBuoyancy = 1f;
        [Tooltip("Whole-domain air damping in 1/seconds.")]
        [Min(0f)] public float airDrag = 0.02f;
        [Tooltip("World-space velocity ceiling used to keep interactive inflows numerically stable.")]
        [Min(0.1f)] public float maxAirSpeed = 12f;

        [Header("Domain Boundaries")]
        public bool closedSides = true;
        public bool closedBottom = true;
        public bool closedTop;

        [Header("Quality")]
        [Range(10, 60), Tooltip("Fixed CFD solver frequency. Rendering remains uncapped and reuses the latest field " +
                                "between ticks. Thirty Hz is the realtime default for full-domain airflow.")]
        public int simulationRate = 30;
        [Tooltip("Limit CFD to one solver tick per rendered frame. This prevents the fixed-step clock from doubling " +
                 "an already expensive frame while the renderer is recovering.")]
        public bool limitToOneSolverStepPerFrame = true;
        [Tooltip("Run the pressure projection every solver tick. Recommended for airflow because pressure " +
                 "communication is global even where no smoke is visible.")]
        public bool pressureEveryStep = true;
        [Tooltip("Keep temperature-only bricks active. This is required for thermal flow after a source stops.")]
        public bool thermalOccupancy = true;
        [Tooltip("Resolution of the change-driven mesh SDF bake along the largest domain axis. Independent of the " +
                 "sim grid — it may exceed it for crisper collision walls (sub-cell coverage, wall normals, and " +
                 "the preview shell all sharpen) at the cost of bake-time memory and rebake cost only.")]
        [Range(32, 256)] public int sdfMaxResolution = 128;
        [Tooltip("Draw the CFD domain bounds in the Scene view.")]
        public bool showGizmo = true;
        [Tooltip("When inlet/outlet patches exist, solve every domain brick so pressure and mass flow communicate " +
                 "through the complete wind tunnel. This has no effect when no boundary patches are active.")]
        public bool fullDomainSolveWithBoundaryPatches = true;
        [Tooltip("Solve every domain brick even without inlet/outlet patches. Clear air is invisible to " +
                 "content-driven occupancy, so without this a patch-free study only simulates flow where dye or " +
                 "heat happens to be — wakes truncate at brick borders and recirculation never forms. Disable " +
                 "only for a deliberately localized thermal-plume study.")]
        public bool fullDomainSolve = true;
        [Tooltip("Reference air density used to convert measured volume flow into mass flow. This does not yet " +
                 "change solver viscosity or compressibility.")]
        [Min(0.001f)] public float referenceAirDensityKgPerM3 = 1.225f;

        [SerializeField, HideInInspector] BrazeContainer container;
        [System.NonSerialized] BrazeCfdHeatMap heatMap;
        [System.NonSerialized] BrazeCfdSurfacePressure surfacePressure;
        [System.NonSerialized] BrazeCfdSdfBaker sdfBaker;
        [System.NonSerialized] bool containerVisibilityOwned;

        /// <summary>The shared GPU container owned by this CFD domain.</summary>
        public BrazeContainer Container => container;
        /// <summary>The currently active heat-map view for this domain, if any.</summary>
        public BrazeCfdHeatMap HeatMap => heatMap != null && heatMap.isActiveAndEnabled && heatMap.Targets(this)
            ? heatMap : null;
        public BrazeCfdSurfacePressure SurfacePressure => surfacePressure != null &&
            surfacePressure.isActiveAndEnabled && surfacePressure.Targets(this) ? surfacePressure : null;
        public Vector3 RelativePressureLoadWorld => isActiveAndEnabled && container != null
            ? container.CfdRelativePressureLoadWorld : Vector3.zero;
        public float InletVolumeFlowM3PerSecond => container != null ? container.CfdInletVolumeFlowM3PerSecond : 0f;
        public float OutletVolumeFlowM3PerSecond => container != null ? container.CfdOutletVolumeFlowM3PerSecond : 0f;
        public float InletMassFlowKgPerSecond => InletVolumeFlowM3PerSecond * referenceAirDensityKgPerM3;
        public float OutletMassFlowKgPerSecond => OutletVolumeFlowM3PerSecond * referenceAirDensityKgPerM3;
        /// <summary>True after the GPU has measured at least one solved-air temperature sample.</summary>
        public bool TemperatureMeasurementValid => container != null && container.CfdTemperaturePeakValid;
        /// <summary>Maximum current presented fluid temperature. This excludes fully solid SDF cells and follows
        /// the render-conditioned field in 3D volume mode.</summary>
        public float MeasuredPeakTemperatureC => TemperatureMeasurementValid
            ? ambientTemperatureC + container.CfdTemperaturePeakHeatUnits / HeatUnitsPerDegree
            : ambientTemperatureC;
        public float MassImbalancePercent
        {
            get
            {
                float inlet = InletVolumeFlowM3PerSecond;
                return inlet > 1e-6f ? Mathf.Abs(inlet - OutletVolumeFlowM3PerSecond) / inlet * 100f : 0f;
            }
        }

        internal float HeatUnitsPerDegree => 1f / Mathf.Max(temperatureRangeC, 1f);
        internal float BuoyancyCoefficient => Mathf.Max(0f, thermalBuoyancy);

        void Reset() => ResolveContainer();

        void OnEnable()
        {
            ResolveContainer();
            ApplyContainerVisibility();
            container?.AttachCfdDomain(this);
            ResolveHeatMap();
            ResolveSurfacePressure();
        }

        void OnDisable()
        {
            container?.DetachCfdDomain(this);
            RestoreContainerVisibility();
            sdfBaker?.Dispose();
            sdfBaker = null;
        }

        void OnValidate()
        {
            temperatureRangeC = Mathf.Max(1f, temperatureRangeC);
            thermalPersistenceSeconds = Mathf.Max(0.1f, thermalPersistenceSeconds);
            thermalBuoyancy = Mathf.Max(0f, thermalBuoyancy);
            airDrag = Mathf.Max(0f, airDrag);
            maxAirSpeed = Mathf.Max(0.1f, maxAirSpeed);
            sdfMaxResolution = Mathf.Clamp(sdfMaxResolution, 32, 256);
            simulationRate = Mathf.Clamp(simulationRate, 10, 60);
            referenceAirDensityKgPerM3 = Mathf.Max(0.001f, referenceAirDensityKgPerM3);
            ResolveContainer();
            ApplyContainerVisibility();
            MarkSdfDirty();
            if (isActiveAndEnabled) container?.AttachCfdDomain(this);
        }

        void ResolveContainer()
        {
            if (container == null || container.gameObject != gameObject)
                container = GetComponent<BrazeContainer>();
        }

        internal void ApplyContainerVisibility()
        {
            if (container == null) return;
            if (showAdvancedContainer)
            {
                if (containerVisibilityOwned) container.hideFlags &= ~HideFlags.HideInInspector;
                containerVisibilityOwned = false;
            }
            else if ((container.hideFlags & HideFlags.HideInInspector) == 0)
            {
                container.hideFlags |= HideFlags.HideInInspector;
                containerVisibilityOwned = true;
            }
        }

        void RestoreContainerVisibility()
        {
            if (container != null && containerVisibilityOwned)
                container.hideFlags &= ~HideFlags.HideInInspector;
            containerVisibilityOwned = false;
        }

        internal void ApplyFramePolicy(ref BrazeSolver.FrameParams frame)
        {
            // CFD owns a deliberately small, non-pyro policy surface. These assignments happen only for a live
            // companion; the normal Fire path never calls this method.
            BrazeCfdWindTunnel tunnel = FindWindTunnel();
            BrazeCfdVisualizer advancedVisualizer = tunnel == null ? FindVisualizer() : null;
            IBrazeCfdVisualization visualization = tunnel != null ? tunnel : advancedVisualizer;
            Vector3 referenceVelocityWorld;
            bool hasReferenceVelocity;
            if (tunnel != null)
                hasReferenceVelocity = TryTunnelReferenceVelocity(tunnel, out referenceVelocityWorld);
            else if (advancedVisualizer != null)
                hasReferenceVelocity = advancedVisualizer.TryGetReferenceVelocity(out referenceVelocityWorld, out _);
            else
                hasReferenceVelocity = TryStrongestInletVelocity(out referenceVelocityWorld);
            float inletSpeedVox = hasReferenceVelocity ? InletSpeedVox(referenceVelocityWorld) : 0f;
            frame.ambientLife = 0f;
            // Preserve vortices generated by the inlet and body rather than injecting Fire's procedural motion.
            // The Curl/Confinement passes already run in the shared solver, so this adds no dispatch or grid.
            frame.confineScale = tunnel != null && tunnel.flowRegime == BrazeCfdFlowRegime.Turbulent
                ? tunnel.vorticityRetention * 8f * Mathf.Max(frame.resolutionScale, 0.01f) : 0f;
            frame.turbulence = 0f;
            frame.billow = 0f;
            frame.instability = 0f;
            frame.interfaceTurb = 0f;
            frame.cfdInletTurbulence = tunnel != null ? tunnel.TurbulenceIntensity : 0f;
            frame.cfdTurbulenceLengthVox = tunnel != null ? TurbulenceLengthVox(tunnel) : 1f;
            frame.cfdTurbulenceFrequency = tunnel != null
                ? Mathf.Clamp(tunnel.airSpeed / Mathf.Max(tunnel.TurbulenceLengthWorld, 0.01f) * 0.35f, 0.05f, 6f)
                : 0f;
            frame.cfdRenderSpeedInvReference = visualization != null && visualization.SmokeFlowSpeedColor
                ? RenderSpeedInverseReference(inletSpeedVox) : 0f;
            // Body Contact dye: no inlet injection (the boundary mask returns 0 for pattern 4); the solver
            // releases dye in the body's surface shell instead, normalized against the inlet speed.
            float contactDensity = visualization != null && visualization.SmokeEnabled
                ? ResolveBodyContactDyeDensity() : 0f;
            frame.cfdContactDyeDensity = inletSpeedVox > 0.0001f ? contactDensity : 0f;
            frame.cfdContactDyeSpeedInv = frame.cfdContactDyeDensity > 0f ? 1f / inletSpeedVox : 0f;
            // Dynamic head for Cp. Advanced patch studies derive the same reference from their strongest inlet.
            frame.cfdInletSpeedVox = inletSpeedVox;
            // Resolution-independent linear damping in literal 1/s. The Fire quadratic drag acts on VOXEL speed,
            // so the same authored value dragged harder every time the grid resolution rose.
            frame.cfdLinearDrag = airDrag;
            frame.drag = 0f;
            // Domain-wide thermal buoyancy. The per-emitter channel (velocity .w) only covers heat that entered
            // through a CFD source; inlet-patch temperature has no emitter and previously never rose at all.
            frame.cfdThermalBuoyancy = BuoyancyCoefficient * Mathf.Max(frame.resolutionScale, 0.01f);
            // CFD smoke is passive dye, not Fire smoke. Keep it transported without the wisp erosion used by
            // plumes, and let the active presentation facade own its intuitive persistence control.
            if (visualization != null && visualization.SmokeEnabled)
            {
                frame.smokeDecayMul = 1f / Mathf.Max(visualization.EffectiveSmokePersistenceSeconds, 0.1f);
                frame.smokeErode = 0f;
            }
            else if (visualization != null)
            {
                // Dye preview just turned off (analysis-slice mode): flush the residual dye fast so a later
                // switch back starts from a clean tunnel instead of stale remnants.
                frame.smokeDecayMul = 2f;
                frame.smokeErode = 0.5f;
            }
            // Solver temperature decay is DECAY_TEMP=2/3 times FireDecayMul. Compensate here so the CFD-facing
            // persistence value is the actual temperature e-fold time rather than the Fire channel's lifetime.
            frame.fireDecayMul = 1.5f / Mathf.Max(thermalPersistenceSeconds, 0.1f);
            if (pressureEveryStep)
            {
                frame.pressureCadence = 1;
                frame.pressureTrickle = 0;
            }
            // Sparse-path convergence floor. Full-domain CFD runs the multigrid V-cycle instead, where this value
            // only sets the fine smoothing budget; sparse domains still solve pure RBGS and Fire's 6-8 iteration
            // default cannot carry pressure across a domain-scale plume.
            frame.jacobiIters = Mathf.Max(frame.jacobiIters, 10);
            frame.closedFaces = (closedSides ? 1 | 2 | 16 | 32 : 0)
                              | (closedBottom ? 4 : 0)
                              | (closedTop ? 8 : 0);
            // A wind-tunnel study is a duct: an open ceiling is a p=0 vent that leaks mass upward and skews the
            // flow over the test body (the Fire-inherited closedTop=false default). Patches still punch through
            // sealed faces, so the inlet/outlet are unaffected.
            if (tunnel != null && tunnel.sealTunnelWalls) frame.closedFaces = 63;
        }

        float TurbulenceLengthVox(BrazeCfdWindTunnel tunnel)
        {
            if (container == null) return 1f;
            Vector3Int resolution = container.SolverRes;
            Vector3 size = container.WorldSize();
            float uVoxPerWorld, vVoxPerWorld;
            switch (tunnel.InletFace)
            {
                case BrazeCfdDomainFace.NegativeX:
                case BrazeCfdDomainFace.PositiveX:
                    uVoxPerWorld = resolution.y / Mathf.Max(Mathf.Abs(size.y), 0.001f);
                    vVoxPerWorld = resolution.z / Mathf.Max(Mathf.Abs(size.z), 0.001f);
                    break;
                case BrazeCfdDomainFace.NegativeY:
                case BrazeCfdDomainFace.PositiveY:
                    uVoxPerWorld = resolution.x / Mathf.Max(Mathf.Abs(size.x), 0.001f);
                    vVoxPerWorld = resolution.z / Mathf.Max(Mathf.Abs(size.z), 0.001f);
                    break;
                default:
                    uVoxPerWorld = resolution.x / Mathf.Max(Mathf.Abs(size.x), 0.001f);
                    vVoxPerWorld = resolution.y / Mathf.Max(Mathf.Abs(size.y), 0.001f);
                    break;
            }
            return Mathf.Max(1f, tunnel.TurbulenceLengthWorld * Mathf.Min(uVoxPerWorld, vVoxPerWorld));
        }

        static float RenderSpeedInverseReference(float inletSpeedVox)
        {
            if (inletSpeedVox <= 0.0001f) return 0f;
            // Put nominal freestream at the green midpoint. Wake deficits then occupy blue/cyan, while flow at
            // roughly 1.5x freestream reaches yellow/red. The former 1.5x denominator plus the default contrast
            // landed freestream exactly on yellow, making a healthy full-face tunnel look uniformly saturated.
            return 1f / (inletSpeedVox * 2f);
        }

        float InletSpeedVox(Vector3 worldVelocity)
        {
            if (container == null || worldVelocity.sqrMagnitude <= 1e-8f) return 0f;
            Vector3Int resolution = container.SolverRes;
            Vector3 size = container.WorldSize();
            Vector3 localVelocity = Quaternion.Inverse(container.transform.rotation) * worldVelocity;
            Vector3 velocityVox = new(
                localVelocity.x * resolution.x / Mathf.Max(Mathf.Abs(size.x), 0.001f),
                localVelocity.y * resolution.y / Mathf.Max(Mathf.Abs(size.y), 0.001f),
                localVelocity.z * resolution.z / Mathf.Max(Mathf.Abs(size.z), 0.001f));
            return velocityVox.magnitude;
        }

        static bool TryTunnelReferenceVelocity(BrazeCfdWindTunnel tunnel, out Vector3 worldVelocity)
        {
            worldVelocity = tunnel != null ? tunnel.WindDirectionWorld * tunnel.airSpeed : Vector3.zero;
            return worldVelocity.sqrMagnitude > 1e-8f;
        }

        bool TryStrongestInletVelocity(out Vector3 worldVelocity)
        {
            worldVelocity = Vector3.zero;
            if (container == null) return false;
            float strongest = 0f;
            for (int i = 0; i < BrazeCfdBoundaryPatch.Active.Count; i++)
            {
                BrazeCfdBoundaryPatch patch = BrazeCfdBoundaryPatch.Active[i];
                if (patch == null || !patch.isActiveAndEnabled || !patch.Targets(this) ||
                    patch.mode != BrazeCfdBoundaryPatchMode.VelocityInlet) continue;
                Vector3 outward = BrazeCfdBoundaryPatch.LocalOutwardNormal(patch.face);
                BrazeCfdBoundaryPatch.LocalTangentAxes(patch.face, out Vector3 u, out Vector3 v);
                Vector3 local = -outward * patch.normalSpeed + u * patch.tangentialVelocity.x +
                                v * patch.tangentialVelocity.y;
                if (local.sqrMagnitude <= strongest) continue;
                strongest = local.sqrMagnitude;
                worldVelocity = container.transform.rotation * local;
            }
            return strongest > 1e-8f;
        }

        float ResolveBodyContactDyeDensity()
        {
            float density = 0f;
            for (int i = 0; i < BrazeCfdBoundaryPatch.Active.Count; i++)
            {
                BrazeCfdBoundaryPatch patch = BrazeCfdBoundaryPatch.Active[i];
                if (patch == null || !patch.isActiveAndEnabled || !patch.Targets(this) ||
                    patch.mode != BrazeCfdBoundaryPatchMode.VelocityInlet || !patch.setInletSmoke ||
                    patch.inletSmokePattern != BrazeCfdSmokePattern.BodyContact) continue;
                density = Mathf.Max(density, patch.inletSmokeDensity);
            }
            return density;
        }

        internal void AttachHeatMap(BrazeCfdHeatMap candidate)
        {
            if (candidate != null && candidate.Targets(this)) heatMap = candidate;
        }

        BrazeCfdWindTunnel FindWindTunnel()
        {
            for (int i = 0; i < BrazeCfdWindTunnel.Active.Count; i++)
            {
                BrazeCfdWindTunnel candidate = BrazeCfdWindTunnel.Active[i];
                if (candidate != null && candidate.isActiveAndEnabled && candidate.domain == this) return candidate;
            }
            return null;
        }

        BrazeCfdVisualizer FindVisualizer()
        {
            for (int i = 0; i < BrazeCfdVisualizer.Active.Count; i++)
            {
                BrazeCfdVisualizer candidate = BrazeCfdVisualizer.Active[i];
                if (candidate != null && candidate.isActiveAndEnabled && candidate.domain == this) return candidate;
            }
            return null;
        }

        internal void DetachHeatMap(BrazeCfdHeatMap candidate)
        {
            if (heatMap != candidate) return;
            heatMap = null;
            ResolveHeatMap();
        }

        internal void AttachSurfacePressure(BrazeCfdSurfacePressure candidate)
        {
            if (candidate != null && candidate.Targets(this)) surfacePressure = candidate;
        }

        internal void DetachSurfacePressure(BrazeCfdSurfacePressure candidate)
        {
            if (surfacePressure != candidate) return;
            surfacePressure = null;
            ResolveSurfacePressure();
        }

        internal void MarkSdfDirty() => sdfBaker?.MarkDirty();

        /// <summary>Voxel AABB (+stencil margin) of the baked boundary geometry at solver resolution. Zero size
        /// means "unknown" — callers fall back to the whole grid.</summary>
        internal void GetBodyVoxelWindow(Vector3Int solverResolution, out Vector3Int min, out Vector3Int size)
        {
            min = Vector3Int.zero;
            size = Vector3Int.zero;
            if (sdfBaker == null || !sdfBaker.TryGetNormalizedBounds(out Vector3 lo, out Vector3 hi)) return;
            // Two voxels cover the SDF's one-cell coverage ramp plus the surface-load gradient stencil.
            min = new Vector3Int(Mathf.FloorToInt(lo.x * solverResolution.x) - 2,
                                 Mathf.FloorToInt(lo.y * solverResolution.y) - 2,
                                 Mathf.FloorToInt(lo.z * solverResolution.z) - 2);
            Vector3Int max = new(Mathf.CeilToInt(hi.x * solverResolution.x) + 2,
                                 Mathf.CeilToInt(hi.y * solverResolution.y) + 2,
                                 Mathf.CeilToInt(hi.z * solverResolution.z) + 2);
            min = Vector3Int.Max(min, Vector3Int.zero);
            max = Vector3Int.Min(max, solverResolution);
            size = Vector3Int.Max(max - min, Vector3Int.zero);
        }

        internal void EnsureSignedDistance(Vector3Int solverResolution, out RenderTexture texture, out int version)
        {
            if (!isActiveAndEnabled || container == null)
            {
                texture = null;
                version = 0;
                return;
            }
            sdfBaker ??= new BrazeCfdSdfBaker();
            sdfBaker.Ensure(this, container, solverResolution);
            texture = sdfBaker.Texture;
            version = sdfBaker.Version;
        }

        internal void BeginAnimatedSdfGather()
        {
            sdfBaker ??= new BrazeCfdSdfBaker();
            sdfBaker.BeginAnimatedGather();
        }

        internal void EndAnimatedSdfGather() => sdfBaker?.EndAnimatedGather();

        /// <summary>Mesh-local SDF for one animated (rigid-motion) boundary. Change-driven on mesh, settings, and
        /// authored scale only — rotation/translation never re-bake.</summary>
        internal bool EnsureAnimatedSdf(BrazeCfdSdfBoundary boundary, out RenderTexture texture,
                                        out Vector3 boundsMin, out Vector3 boundsSize)
        {
            texture = null;
            boundsMin = Vector3.zero;
            boundsSize = Vector3.one;
            if (!isActiveAndEnabled || container == null) return false;
            sdfBaker ??= new BrazeCfdSdfBaker();
            float cellWorldSize = Mathf.Abs(container.WorldSize().x) / Mathf.Max(1, container.SolverRes.x);
            // Bake voxel size is INDEPENDENT of the sim grid: largest domain axis / sdfMaxResolution, the same
            // metric the static domain bake uses, so body SDFs sharpen with the same dial.
            Vector3 worldSize = container.WorldSize();
            float largestWorld = Mathf.Max(Mathf.Abs(worldSize.x),
                                 Mathf.Max(Mathf.Abs(worldSize.y), Mathf.Abs(worldSize.z)));
            float bakeCellWorld = largestWorld / Mathf.Clamp(sdfMaxResolution, 32, 256);
            return sdfBaker.EnsureAnimatedBake(boundary, cellWorldSize, bakeCellWorld, sdfMaxResolution,
                out texture, out boundsMin, out boundsSize);
        }

        void ResolveHeatMap()
        {
            heatMap = null;
            for (int i = 0; i < BrazeCfdHeatMap.Active.Count; i++)
            {
                BrazeCfdHeatMap candidate = BrazeCfdHeatMap.Active[i];
                if (candidate != null && candidate.isActiveAndEnabled && candidate.Targets(this))
                {
                    heatMap = candidate;
                    break;
                }
            }
        }

        void ResolveSurfacePressure()
        {
            surfacePressure = null;
            for (int i = 0; i < BrazeCfdSurfacePressure.Active.Count; i++)
            {
                BrazeCfdSurfacePressure candidate = BrazeCfdSurfacePressure.Active[i];
                if (candidate != null && candidate.isActiveAndEnabled && candidate.Targets(this))
                {
                    surfacePressure = candidate;
                    break;
                }
            }
        }

        static readonly Color DomainColor = new(0.16f, 0.78f, 1f, 0.7f);

        void OnDrawGizmos()
        {
            if (!showGizmo || container == null) return;
            Matrix4x4 old = Gizmos.matrix;
            Color oldColor = Gizmos.color;
            Gizmos.matrix = Matrix4x4.TRS(container.transform.position, container.transform.rotation,
                                          container.WorldSize());
            Gizmos.color = DomainColor;
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
            Gizmos.matrix = old;
            Gizmos.color = oldColor;
        }
    }
}
