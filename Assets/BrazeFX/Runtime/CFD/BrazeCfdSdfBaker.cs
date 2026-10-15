using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace BrazeFX.CFD
{
    /// <summary>
    /// Domain-owned, change-driven GPU SDF baker. Dense jump-flood scratch buffers exist only during a bake; the
    /// persistent result is one downsampled RHalf texture that is copied into the solver's existing solid mask.
    /// </summary>
    internal sealed class BrazeCfdSdfBaker : IDisposable
    {
        // Final jump-flood indices use low 25 bits; 256^3-1 is 0x00ffffff, safely below this sentinel.
        const uint InvalidJfaIndex = 0x01ffffffu;
        static readonly ProfilerMarker BakeMarker = new("BrazeFX.CFD.SdfBake");

        readonly List<Vector3> vertices = new();
        readonly List<int> indices = new();
        readonly List<Vector4> triangleSettings = new();
        readonly List<Vector3> meshVertices = new();
        readonly List<int> meshIndices = new();

        ComputeShader shader;
        RenderTexture signedDistance;
        Vector3Int bakeResolution;
        int inputHash;
        bool hashInitialized;
        int pendingHash;          // drag debounce: a changed hash must hold across a frame before rebaking
        int pendingHashFrame;     // frame the pending hash was first seen (Ensure runs several times per frame)
        bool pendingHashValid;
        int forcedRevision;
        int version;
        Vector3 boundsMinNormalized, boundsMaxNormalized;   // baked geometry AABB in domain 0..1 space
        bool boundsValid;

        int kClearSeeds = -1;
        int kVoxelizeTriangles = -1;
        int kInitSeeds = -1;
        int kJumpFlood = -1;
        int kFloodInit = -1;
        int kFloodSweep = -1;
        int kFinalizeSdf = -1;
        bool shaderValidationAttempted;
        bool shaderSupported;

        // Rounds of six directional flood sweeps. Each round follows several path turns, so this bounds cavity
        // "corridor complexity", not size; unconverged cells stay solid, which is the conservative failure.
        const int FloodSweepRounds = 12;

        public RenderTexture Texture => signedDistance;
        public int Version => version;
        public Vector3Int Resolution => bakeResolution;

        public void MarkDirty()
        {
            unchecked { forcedRevision++; }
            hashInitialized = false;
        }

        public void Ensure(BrazeCfdDomain domain, BrazeContainer container, Vector3Int solverResolution)
        {
            if (domain == null || container == null)
            {
                ClearResult();
                return;
            }

            Vector3Int wantedResolution = CalculateBakeResolution(solverResolution, domain.sdfMaxResolution);
            int hash = ComputeInputHash(domain, container, wantedResolution, out int eligibleCount);
            if (hashInitialized && inputHash == hash)
            {
                pendingHashValid = false;
                return;
            }
            // Drag debounce: while the authored inputs are still CHANGING (a transform being dragged re-hashes
            // every frame), postpone the voxelize + jump-flood + full-grid merge until the changed hash holds
            // across a frame boundary. Frame-aware because Ensure runs several times per frame (container policy
            // plus every active analysis slice). The very first bake (no cached result yet) stays immediate.
            if (signedDistance != null)
            {
                if (!pendingHashValid || pendingHash != hash)
                {
                    pendingHash = hash;
                    pendingHashFrame = Time.frameCount;
                    pendingHashValid = true;
                    return;
                }
                if (Time.frameCount == pendingHashFrame) return;
            }
            pendingHashValid = false;
            hashInitialized = true;
            inputHash = hash;

            if (eligibleCount == 0)
            {
                ClearResult();
                return;
            }

            CollectTriangles(domain, container, wantedResolution);
            if (indices.Count < 3)
            {
                ClearResult();
                return;
            }

            UpdateBounds(wantedResolution);
            if (!EnsureShader())
            {
                ClearResult();
                return;
            }
            EnsureTexture(wantedResolution);
            using (BakeMarker.Auto()) Bake(container);
            unchecked { version++; }
        }

        /// <summary>Baked-geometry AABB in normalized domain space, valid only while a bake result exists.</summary>
        public bool TryGetNormalizedBounds(out Vector3 min, out Vector3 max)
        {
            min = boundsMinNormalized;
            max = boundsMaxNormalized;
            return boundsValid && signedDistance != null;
        }

        void UpdateBounds(Vector3Int wantedResolution)
        {
            Vector3 lo = new(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 hi = new(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < vertices.Count; i++)
            {
                lo = Vector3.Min(lo, vertices[i]);
                hi = Vector3.Max(hi, vertices[i]);
            }
            boundsMinNormalized = new Vector3(lo.x / Mathf.Max(1, wantedResolution.x),
                                              lo.y / Mathf.Max(1, wantedResolution.y),
                                              lo.z / Mathf.Max(1, wantedResolution.z));
            boundsMaxNormalized = new Vector3(hi.x / Mathf.Max(1, wantedResolution.x),
                                              hi.y / Mathf.Max(1, wantedResolution.y),
                                              hi.z / Mathf.Max(1, wantedResolution.z));
            boundsValid = vertices.Count > 0;
        }

        internal static Vector3Int CalculateBakeResolution(Vector3Int solverResolution, int maxAxis)
        {
            int largest = Mathf.Max(solverResolution.x, Mathf.Max(solverResolution.y, solverResolution.z));
            // Aspect follows the domain (via the solver grid); magnitude follows sdfMaxResolution ALONE. The bake
            // is independent of sim resolution and may exceed it: the SDF stores world distances and is sampled
            // by UVW, so collision fidelity is a separate dial from sim cost. Finer-than-sim SDFs sharpen the
            // sub-cell coverage ramp, wall normals, and the preview shell.
            float scale = largest > 0 ? Mathf.Clamp(maxAxis, 32, 256) / (float)largest : 1f;
            return new Vector3Int(
                Mathf.Clamp(Mathf.RoundToInt(solverResolution.x * scale), 8, 256),
                Mathf.Clamp(Mathf.RoundToInt(solverResolution.y * scale), 8, 256),
                Mathf.Clamp(Mathf.RoundToInt(solverResolution.z * scale), 8, 256));
        }

        int ComputeInputHash(BrazeCfdDomain domain, BrazeContainer container, Vector3Int wantedResolution,
                             out int eligibleCount)
        {
            unchecked
            {
                int h = 17;
                eligibleCount = 0;
                h = h * 31 + forcedRevision;
                h = h * 31 + wantedResolution.GetHashCode();
                h = HashMatrix(h, container.WorldToFluidUVW());
                var active = BrazeCfdSdfBoundary.Active;
                BrazeRuntimeRegistry.PruneNulls(active);
                for (int i = 0; i < active.Count; i++)
                {
                    BrazeCfdSdfBoundary boundary = active[i];
                    if (boundary == null || !boundary.isActiveAndEnabled || !boundary.Targets(domain)) continue;
                    // Animated bodies live in their own local bakes (EnsureAnimatedBake) and never enter the
                    // static domain SDF — otherwise a spinning propeller would re-hash every frame and either
                    // starve the debounce or force a full-domain rebake per tick.
                    if (boundary.animated) continue;
                    eligibleCount++;
                    Mesh mesh = boundary.SourceMesh;
                    h = h * 31 + boundary.BrazeInstanceId();
                    h = h * 31 + boundary.Revision;
                    h = h * 31 + (int)boundary.mode;
                    h = h * 31 + boundary.wallThickness.GetHashCode();
                    h = h * 31 + boundary.surfaceOffset.GetHashCode();
                    h = h * 31 + (mesh != null ? mesh.BrazeInstanceId() : 0);
                    h = h * 31 + (mesh != null ? mesh.vertexCount : 0);
                    h = h * 31 + (mesh != null ? mesh.subMeshCount : 0);
                    h = h * 31 + (mesh != null && mesh.isReadable ? 1 : 0);
                    if (mesh != null)
                    {
                        h = h * 31 + mesh.bounds.center.GetHashCode();
                        h = h * 31 + mesh.bounds.extents.GetHashCode();
                        for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
                            h = h * 31 + mesh.GetIndexCount(subMesh).GetHashCode();
                    }
                    h = HashMatrix(h, boundary.meshFilter != null
                        ? boundary.meshFilter.transform.localToWorldMatrix : boundary.transform.localToWorldMatrix);
                }
                return h;
            }
        }

        void CollectTriangles(BrazeCfdDomain domain, BrazeContainer container, Vector3Int wantedResolution)
        {
            vertices.Clear();
            indices.Clear();
            triangleSettings.Clear();
            Matrix4x4 worldToVoxel = Matrix4x4.Scale((Vector3)wantedResolution) * container.WorldToFluidUVW();

            var active = BrazeCfdSdfBoundary.Active;
            for (int i = 0; i < active.Count; i++)
            {
                BrazeCfdSdfBoundary boundary = active[i];
                if (boundary == null || !boundary.isActiveAndEnabled || !boundary.Targets(domain)) continue;
                if (boundary.animated) continue;   // animated bodies bake mesh-local (EnsureAnimatedBake)
                AppendBoundaryTriangles(boundary,
                    worldToVoxel * (boundary.meshFilter != null
                        ? boundary.meshFilter.transform.localToWorldMatrix
                        : boundary.transform.localToWorldMatrix));
            }
        }

        // Shared triangle append for the static (domain-voxel) and animated (scaled-local voxel) bake paths.
        // Returns true when at least one non-degenerate triangle was added.
        bool AppendBoundaryTriangles(BrazeCfdSdfBoundary boundary, Matrix4x4 localToVoxel)
        {
            boundary.SetBakeStatus(null);
            Mesh mesh = boundary.SourceMesh;
            if (mesh == null)
            {
                boundary.SetBakeStatus("Assign a Mesh Filter with a triangle mesh.");
                return false;
            }
            if (!mesh.isReadable)
            {
                boundary.SetBakeStatus("Enable Read/Write on the mesh importer for the static SDF bake.");
                return false;
            }

            meshVertices.Clear();
            mesh.GetVertices(meshVertices);
            if (meshVertices.Count == 0)
            {
                boundary.SetBakeStatus("The mesh contains no vertices.");
                return false;
            }

            int vertexBase = vertices.Count;
            for (int v = 0; v < meshVertices.Count; v++)
                vertices.Add(localToVoxel.MultiplyPoint3x4(meshVertices[v]));

            bool mirrored = localToVoxel.determinant < 0f;
            int trianglesBefore = indices.Count / 3;
            for (int subMesh = 0; subMesh < mesh.subMeshCount; subMesh++)
            {
                if (mesh.GetTopology(subMesh) != MeshTopology.Triangles) continue;
                meshIndices.Clear();
                mesh.GetTriangles(meshIndices, subMesh, true);
                for (int t = 0; t + 2 < meshIndices.Count; t += 3)
                {
                    int a = meshIndices[t];
                    int b = meshIndices[t + (mirrored ? 2 : 1)];
                    int c = meshIndices[t + (mirrored ? 1 : 2)];
                    if ((uint)a >= (uint)meshVertices.Count || (uint)b >= (uint)meshVertices.Count ||
                        (uint)c >= (uint)meshVertices.Count) continue;
                    Vector3 va = vertices[vertexBase + a];
                    Vector3 vb = vertices[vertexBase + b];
                    Vector3 vc = vertices[vertexBase + c];
                    if (Vector3.Cross(vb - va, vc - va).sqrMagnitude <= 1e-12f) continue;
                    indices.Add(vertexBase + a);
                    indices.Add(vertexBase + b);
                    indices.Add(vertexBase + c);
                    triangleSettings.Add(new Vector4((float)boundary.mode,
                        Mathf.Max(0.001f, boundary.wallThickness), Mathf.Max(0f, boundary.surfaceOffset), 0f));
                }
            }
            if (indices.Count / 3 == trianglesBefore)
            {
                boundary.SetBakeStatus("The mesh contains no non-degenerate triangle faces.");
                return false;
            }
            return true;
        }

        bool EnsureShader()
        {
            if (shaderValidationAttempted) return shaderSupported;
            shaderValidationAttempted = true;
            shader = Resources.Load<ComputeShader>("BrazeCfdSdf");
            if (shader == null) throw new InvalidOperationException("BrazeCfdSdf.compute is missing from Resources.");
            kClearSeeds = shader.FindKernel("ClearSeeds");
            kVoxelizeTriangles = shader.FindKernel("VoxelizeTriangles");
            kInitSeeds = shader.FindKernel("InitSeeds");
            kJumpFlood = shader.FindKernel("JumpFlood");
            kFloodInit = shader.FindKernel("FloodInit");
            kFloodSweep = shader.FindKernel("FloodSweep");
            kFinalizeSdf = shader.FindKernel("FinalizeSdf");
            shaderSupported = shader.IsSupported(kClearSeeds)
                           && shader.IsSupported(kVoxelizeTriangles)
                           && shader.IsSupported(kInitSeeds)
                           && shader.IsSupported(kJumpFlood)
                           && shader.IsSupported(kFloodInit)
                           && shader.IsSupported(kFloodSweep)
                           && shader.IsSupported(kFinalizeSdf);
            if (!shaderSupported)
                Debug.LogError("[BrazeFX] One or more BrazeCfdSdf compute kernels are unsupported by the active " +
                               "graphics API. CFD mesh SDF baking is disabled to avoid repeated invalid dispatches.");
            return shaderSupported;
        }

        void EnsureTexture(Vector3Int wantedResolution)
        {
            if (signedDistance != null && bakeResolution == wantedResolution) return;
            ReleaseTexture(ref signedDistance);
            bakeResolution = wantedResolution;
            signedDistance = CreateSdfTexture(wantedResolution, "BrazeFX_CFD_SignedDistance");
        }

        static RenderTexture CreateSdfTexture(Vector3Int resolution, string textureName)
        {
            var texture = new RenderTexture(resolution.x, resolution.y, 0,
                RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear)
            {
                dimension = TextureDimension.Tex3D,
                volumeDepth = resolution.z,
                enableRandomWrite = true,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = textureName,
            };
            if (!texture.Create() || !texture.IsCreated())
            {
                RenderTexture failed = texture;
                ReleaseTexture(ref failed);
                throw new InvalidOperationException($"Could not create CFD SDF texture {resolution}.");
            }
            return texture;
        }

        void Bake(BrazeContainer container)
        {
            Vector3 worldSize = container.WorldSize();
            RunBakePipeline(signedDistance, bakeResolution, new Vector3(
                Mathf.Abs(worldSize.x) / Mathf.Max(1, bakeResolution.x),
                Mathf.Abs(worldSize.y) / Mathf.Max(1, bakeResolution.y),
                Mathf.Abs(worldSize.z) / Mathf.Max(1, bakeResolution.z)));
        }

        // Shared exact triangle-band → jump-flood extension → boundary-reachability sign → finalize pipeline.
        // `voxelToWorld` is the per-axis voxel size in world units (the bake grid may be anisotropic); the
        // collected triangle lists are already in `resolution` voxel space.
        void RunBakePipeline(RenderTexture target, Vector3Int resolution, Vector3 voxelToWorld)
        {
            int voxelCount = resolution.x * resolution.y * resolution.z;
            int triangleCount = indices.Count / 3;
            if (triangleCount > 0x400000)
                throw new InvalidOperationException("CFD SDF bakes support at most 4,194,304 triangles.");
            ComputeBuffer vertexBuffer = null;
            ComputeBuffer indexBuffer = null;
            ComputeBuffer settingBuffer = null;
            ComputeBuffer seedTriangles = null;
            ComputeBuffer seedA = null;
            ComputeBuffer seedB = null;
            try
            {
                vertexBuffer = new ComputeBuffer(vertices.Count, 12);
                indexBuffer = new ComputeBuffer(indices.Count, 4);
                settingBuffer = new ComputeBuffer(triangleSettings.Count, 16);
                seedTriangles = new ComputeBuffer(voxelCount, 4);
                seedA = new ComputeBuffer(voxelCount, 4);
                seedB = new ComputeBuffer(voxelCount, 4);
                vertexBuffer.SetData(vertices);
                indexBuffer.SetData(indices);
                settingBuffer.SetData(triangleSettings);

                shader.SetInts("_BakeRes", resolution.x, resolution.y, resolution.z);
                shader.SetInt("_VoxelCount", voxelCount);
                shader.SetInt("_TriangleCount", triangleCount);
                shader.SetInt("_InvalidSeed", unchecked((int)InvalidJfaIndex));
                // Set BEFORE any dispatch: JumpFlood compares seed distances under the world metric. Publishing
                // this only ahead of FinalizeSdf once left the flood running with a zero scale on the first bake
                // of a session — every candidate "tied" at distance 0 and the garbage sign field walled off whole
                // slabs of the domain as solid.
                shader.SetVector("_VoxelToWorld", new Vector4(voxelToWorld.x, voxelToWorld.y, voxelToWorld.z, 0f));
                float largestVoxel = Mathf.Max(voxelToWorld.x, Mathf.Max(voxelToWorld.y, voxelToWorld.z));
                float largestSurfaceReach = 0f;
                for (int i = 0; i < triangleSettings.Count; i++)
                {
                    Vector4 settings = triangleSettings[i];
                    largestSurfaceReach = Mathf.Max(largestSurfaceReach,
                        settings.x < 0.5f ? settings.z : settings.y * 0.5f);
                }
                // The publisher-owned sparse-flip prototype uses a 2.5-cell faithful band. Extend it by authored shell/offset
                // reach so those displaced zero crossings also remain exact rather than falling into JFA far field.
                shader.SetFloat("_NarrowBandWorld", largestVoxel * 2.5f + largestSurfaceReach);

                shader.SetBuffer(kClearSeeds, "_SeedTriangles", seedTriangles);
                shader.Dispatch(kClearSeeds, Mathf.CeilToInt(voxelCount / 256f), 1, 1);

                shader.SetBuffer(kVoxelizeTriangles, "_Vertices", vertexBuffer);
                shader.SetBuffer(kVoxelizeTriangles, "_Indices", indexBuffer);
                shader.SetBuffer(kVoxelizeTriangles, "_SeedTriangles", seedTriangles);
                shader.Dispatch(kVoxelizeTriangles, Mathf.CeilToInt(triangleCount / 64f), 1, 1);

                shader.SetBuffer(kInitSeeds, "_SeedTriangles", seedTriangles);
                shader.SetBuffer(kInitSeeds, "_SeedWrite", seedA);
                DispatchVolume(kInitSeeds, resolution);

                int largest = Mathf.Max(resolution.x, Mathf.Max(resolution.y, resolution.z));
                int step = Mathf.NextPowerOfTwo(largest) >> 1;
                ComputeBuffer read = seedA;
                ComputeBuffer write = seedB;
                while (step >= 1)
                {
                    shader.SetInt("_JumpStep", step);
                    shader.SetBuffer(kJumpFlood, "_SeedRead", read);
                    shader.SetBuffer(kJumpFlood, "_SeedWrite", write);
                    DispatchVolume(kJumpFlood, resolution);
                    (read, write) = (write, read);
                    step >>= 1;
                }

                // Sign by reachability (see the compute file's rationale): the now-dead JFA destination becomes
                // the flood-state volume. Walls are cells within the seal radius of any triangle; air sweeps in
                // 6-connected from the domain boundary; unreached = solid. No winding, parity, or manifold
                // assumption — ground-truth triangle soup (open sheets, T-junctions, overlapping components,
                // reversed faces) can only wall its own half-voxel neighbourhood, never act at a distance.
                // The seal must exceed half the largest voxel or air could slip between two cell centres
                // straddling a surface; 0.55 adds slack for anisotropic grids.
                shader.SetFloat("_FloodSealWorld", largestVoxel * 0.55f);
                shader.SetBuffer(kFloodInit, "_SeedTriangles", seedTriangles);
                shader.SetBuffer(kFloodInit, "_SeedWrite", write);
                DispatchVolume(kFloodInit, resolution);
                shader.SetBuffer(kFloodSweep, "_SeedWrite", write);
                for (int round = 0; round < FloodSweepRounds; round++)
                {
                    for (int axis = 0; axis < 3; axis++)
                    {
                        Vector2Int plane = axis == 0 ? new Vector2Int(resolution.y, resolution.z)
                                         : axis == 1 ? new Vector2Int(resolution.x, resolution.z)
                                                     : new Vector2Int(resolution.x, resolution.y);
                        shader.SetInt("_SweepAxis", axis);
                        for (int direction = 0; direction < 2; direction++)
                        {
                            shader.SetInt("_SweepDir", direction == 0 ? 1 : -1);
                            shader.Dispatch(kFloodSweep, Mathf.CeilToInt(plane.x / 8f),
                                Mathf.CeilToInt(plane.y / 8f), 1);
                        }
                    }
                }

                shader.SetBuffer(kFinalizeSdf, "_Vertices", vertexBuffer);
                shader.SetBuffer(kFinalizeSdf, "_Indices", indexBuffer);
                shader.SetBuffer(kFinalizeSdf, "_TriangleSettings", settingBuffer);
                shader.SetBuffer(kFinalizeSdf, "_SeedTriangles", seedTriangles);
                shader.SetBuffer(kFinalizeSdf, "_SeedRead", read);
                shader.SetBuffer(kFinalizeSdf, "_FloodState", write);
                shader.SetTexture(kFinalizeSdf, "_SignedDistance", target);
                DispatchVolume(kFinalizeSdf, resolution);
            }
            finally
            {
                vertexBuffer?.Release();
                indexBuffer?.Release();
                settingBuffer?.Release();
                seedTriangles?.Release();
                seedA?.Release();
                seedB?.Release();
            }
        }

        void DispatchVolume(int kernel) => DispatchVolume(kernel, bakeResolution);

        void DispatchVolume(int kernel, Vector3Int resolution)
        {
            shader.Dispatch(kernel, Mathf.CeilToInt(resolution.x / 4f),
                Mathf.CeilToInt(resolution.y / 4f), Mathf.CeilToInt(resolution.z / 4f));
        }

        // ── Animated (rigid-motion) bodies: one mesh-LOCAL bake per boundary ─────────────────────────────────
        // Baked in SCALED local space (lossyScale folded in, rotation/translation excluded), so a spinning or
        // moving body never re-bakes — the solver re-stamps the cached SDF through the current transform.
        sealed class LocalBake
        {
            public RenderTexture texture;
            public Vector3Int resolution;
            public Vector3 boundsMin, boundsSize;   // scaled-local space, world units
            public int hash;
            public bool hashValid;
            public int gatherStamp;
        }
        readonly Dictionary<BrazeCfdSdfBoundary, LocalBake> localBakes = new();
        readonly List<BrazeCfdSdfBoundary> localBakePruneScratch = new();
        int gatherStamp;

        public void BeginAnimatedGather() { unchecked { gatherStamp++; } }

        public void EndAnimatedGather()
        {
            localBakePruneScratch.Clear();
            foreach (var pair in localBakes)
                if (pair.Value.gatherStamp != gatherStamp || pair.Key == null)
                    localBakePruneScratch.Add(pair.Key);
            for (int i = 0; i < localBakePruneScratch.Count; i++)
            {
                if (localBakes.TryGetValue(localBakePruneScratch[i], out LocalBake stale))
                {
                    RenderTexture texture = stale.texture;
                    ReleaseTexture(ref texture);
                }
                localBakes.Remove(localBakePruneScratch[i]);
            }
        }

        /// <summary>Ensures a mesh-local SDF for one animated boundary. Change-driven on mesh/settings/scale only;
        /// transform rotation and translation never re-bake. `bakeCellWorld` sizes the bake voxels (independent of
        /// sim resolution, matching the static bake's voxel size); `solverCellWorld` only pads the margin, which
        /// must cover the solver-cell coverage ramp regardless of how fine the bake is.</summary>
        public bool EnsureAnimatedBake(BrazeCfdSdfBoundary boundary, float solverCellWorld, float bakeCellWorld,
                                       int maxAxisResolution,
                                       out RenderTexture texture, out Vector3 boundsMin, out Vector3 boundsSize)
        {
            texture = null;
            boundsMin = Vector3.zero;
            boundsSize = Vector3.one;
            Mesh mesh = boundary != null ? boundary.SourceMesh : null;
            if (mesh == null || !mesh.isReadable) return false;
            Transform meshTransform = boundary.meshFilter != null ? boundary.meshFilter.transform : boundary.transform;
            Vector3 lossyScale = meshTransform.lossyScale;

            if (!localBakes.TryGetValue(boundary, out LocalBake bake))
            {
                bake = new LocalBake();
                localBakes.Add(boundary, bake);
            }
            bake.gatherStamp = gatherStamp;

            int hash;
            unchecked
            {
                hash = 17;
                hash = hash * 31 + boundary.Revision;
                hash = hash * 31 + (int)boundary.mode;
                hash = hash * 31 + boundary.wallThickness.GetHashCode();
                hash = hash * 31 + boundary.surfaceOffset.GetHashCode();
                hash = hash * 31 + mesh.BrazeInstanceId();
                hash = hash * 31 + mesh.vertexCount;
                hash = hash * 31 + mesh.subMeshCount;
                hash = hash * 31 + mesh.bounds.center.GetHashCode();
                hash = hash * 31 + mesh.bounds.extents.GetHashCode();
                hash = hash * 31 + lossyScale.GetHashCode();
                hash = hash * 31 + solverCellWorld.GetHashCode();
                hash = hash * 31 + bakeCellWorld.GetHashCode();
                hash = hash * 31 + maxAxisResolution;
            }
            if (bake.hashValid && bake.hash == hash && bake.texture != null)
            {
                texture = bake.texture;
                boundsMin = bake.boundsMin;
                boundsSize = bake.boundsSize;
                return true;
            }

            // Collect triangles in SCALED local space, then shift/scale into the bake's voxel grid.
            vertices.Clear();
            indices.Clear();
            triangleSettings.Clear();
            Vector3 scaledMin = Vector3.positiveInfinity;
            Vector3 scaledMax = Vector3.negativeInfinity;
            meshVertices.Clear();
            mesh.GetVertices(meshVertices);
            for (int v = 0; v < meshVertices.Count; v++)
            {
                Vector3 scaled = Vector3.Scale(meshVertices[v], lossyScale);
                scaledMin = Vector3.Min(scaledMin, scaled);
                scaledMax = Vector3.Max(scaledMax, scaled);
            }
            if (meshVertices.Count == 0) return false;
            // Margin: the coverage ramp (one solver cell), authored inflation, and one cell of trilinear slack.
            float margin = solverCellWorld * 2f + Mathf.Max(
                boundary.mode == BrazeCfdSdfBoundaryMode.SurfaceShell ? boundary.wallThickness * 0.5f : 0f,
                boundary.surfaceOffset);
            boundsMin = scaledMin - Vector3.one * margin;
            boundsSize = Vector3.Max(scaledMax - scaledMin + Vector3.one * (margin * 2f), Vector3.one * 1e-3f);
            Vector3Int resolution = new(
                Mathf.Clamp(Mathf.CeilToInt(boundsSize.x / Mathf.Max(bakeCellWorld, 1e-4f)), 8, maxAxisResolution),
                Mathf.Clamp(Mathf.CeilToInt(boundsSize.y / Mathf.Max(bakeCellWorld, 1e-4f)), 8, maxAxisResolution),
                Mathf.Clamp(Mathf.CeilToInt(boundsSize.z / Mathf.Max(bakeCellWorld, 1e-4f)), 8, maxAxisResolution));

            Matrix4x4 localToVoxel = Matrix4x4.Scale(new Vector3(resolution.x / boundsSize.x,
                                                                 resolution.y / boundsSize.y,
                                                                 resolution.z / boundsSize.z))
                                   * Matrix4x4.Translate(-boundsMin)
                                   * Matrix4x4.Scale(lossyScale);
            if (!AppendBoundaryTriangles(boundary, localToVoxel)) return false;

            if (!EnsureShader())
            {
                boundary.SetBakeStatus("CFD mesh SDF compute kernels are unsupported by the active graphics API.");
                return false;
            }
            if (bake.texture == null || bake.resolution != resolution)
            {
                RenderTexture stale = bake.texture;
                ReleaseTexture(ref stale);
                bake.texture = CreateSdfTexture(resolution, "BrazeFX_CFD_BodySignedDistance");
                bake.resolution = resolution;
            }
            using (BakeMarker.Auto())
                RunBakePipeline(bake.texture, resolution, new Vector3(boundsSize.x / resolution.x,
                                                                      boundsSize.y / resolution.y,
                                                                      boundsSize.z / resolution.z));
            bake.boundsMin = boundsMin;
            bake.boundsSize = boundsSize;
            bake.hash = hash;
            bake.hashValid = true;
            texture = bake.texture;
            return true;
        }

        void ClearResult()
        {
            boundsValid = false;
            if (signedDistance == null) return;
            ReleaseTexture(ref signedDistance);
            bakeResolution = default;
            unchecked { version++; }
        }

        static int HashMatrix(int h, Matrix4x4 m)
        {
            unchecked
            {
                for (int i = 0; i < 16; i++) h = h * 31 + m[i].GetHashCode();
                return h;
            }
        }

        static void ReleaseTexture(ref RenderTexture texture)
        {
            if (texture == null) return;
            texture.Release();
            if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
            else UnityEngine.Object.DestroyImmediate(texture);
            texture = null;
        }

        public void Dispose()
        {
            ReleaseTexture(ref signedDistance);
            foreach (var pair in localBakes)
            {
                RenderTexture texture = pair.Value.texture;
                ReleaseTexture(ref texture);
            }
            localBakes.Clear();
            bakeResolution = default;
            hashInitialized = false;
            pendingHashValid = false;
            boundsValid = false;
        }
    }
}
