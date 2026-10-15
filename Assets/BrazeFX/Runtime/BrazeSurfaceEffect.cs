using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Shared base for the Mesh Effects that live ON a mesh surface — SPREAD (particle-seeded growing front)
    /// and SURFACE MAPPER (selection-driven map). It owns the common engine: a welded geodesic surface graph,
    /// multi-source Dijkstra coverage, deform-follow (skinned BakeMesh each frame), the swappable LOOK overlay
    /// (Frost/Corruption/Paint + cubic triplanar noise), and fire/smoke emission into a BrazeFX fluid Container.
    ///
    /// A subclass only decides WHERE coverage comes from by filling <see cref="reach"/> + <see cref="coverFront"/>
    /// in <see cref="UpdateCoverage"/>: Spread seeds from impacts; Surface Mapper seeds from an authored selection.
    /// Everything else (graph build, overlay render, fluid emission, gizmos) is provided here.
    /// </summary>
    public abstract class BrazeSurfaceEffect : MonoBehaviour, IBrazeAnimatedTarget
    {
        public enum Look { Frost, Corruption, Paint, Ants, Custom }

        [Header("Target")]
        [Tooltip("Mesh / skinned renderer to live on. Empty = auto-find the nearest skinned renderer.")]
        public Renderer target;

        [Header("Look")]
        [Tooltip("Material look for the covered region. Switch to Custom to hand-tune the colours below.")]
        public Look look = Look.Frost;
        [Tooltip("Draw the covered surface as a tinted overlay on the body (the actual effect).")]
        public bool renderSurface = true;
        [Tooltip("Fill colour behind the front (rgb) and its opacity (a).")]
        public Color fillColor = new(0.75f, 0.88f, 1f, 0.8f);
        [Tooltip("Bright colour of the advancing leading edge.")]
        public Color edgeColor = new(1f, 1f, 1f, 1f);
        [Tooltip("World-space width of the bright leading band.")]
        [BrazeAnimatable("LOOK/Edge Width", 0f, 0.5f)]
        [Min(0f)] public float edgeWidth = 0.04f;
        [Tooltip("World-space distance behind the front over which the fill fades in.")]
        [BrazeAnimatable("LOOK/Fade Behind", 1e-4f, 0.5f)]
        [Min(1e-4f)] public float fadeBehind = 0.06f;
        [Tooltip("Optional noise texture mixed into the fill to break up flat patches and add depth. " +
                 "Projected CUBICALLY (object-space triplanar) so it never stretches on bad UVs and stays " +
                 "attached to the body as it moves. Leave empty for a flat fill.")]
        public Texture2D noiseTexture;
        [Tooltip("Cubic projection tiling — cycles per local unit. Higher = finer grain.")]
        [BrazeAnimatable("LOOK/Noise Tiling", 0f, 32f)]
        [Min(0f)] public float noiseTiling = 4f;
        [Tooltip("Triplanar blend sharpness — higher = crisper seams between the three projection axes.")]
        [BrazeAnimatable("LOOK/Noise Blend", 1f, 16f)]
        [Range(1f, 16f)] public float noiseBlend = 4f;
        [Tooltip("How strongly the noise darkens / brightens the fill (0 = off, flat fill).")]
        [BrazeAnimatable("LOOK/Noise Strength", 0f, 1f)]
        [Range(0f, 1f)] public float noiseStrength = 0.5f;
        [Tooltip("Levels — input black point. Noise below this clamps to 0.")]
        [BrazeAnimatable("LOOK/Noise Low", 0f, 1f)]
        [Range(0f, 1f)] public float noiseLow = 0f;
        [Tooltip("Levels — input white point. Noise above this clamps to 1.")]
        [BrazeAnimatable("LOOK/Noise High", 0f, 1f)]
        [Range(0f, 1f)] public float noiseHigh = 1f;
        [Tooltip("Levels — midtone / gamma. <0.5 brightens mids, >0.5 darkens them.")]
        [BrazeAnimatable("LOOK/Noise Mid", 0.02f, 0.98f)]
        [Range(0.02f, 0.98f)] public float noiseMid = 0.5f;
        [Tooltip("Contrast around the midpoint — 1 = unchanged, >1 = punchier.")]
        [BrazeAnimatable("LOOK/Noise Contrast", 0f, 4f)]
        [Range(0f, 4f)] public float noiseContrast = 1f;

        [Header("Viewer (debug)")]
        [Tooltip("Draw the sampled surface points as gizmo dots — to verify sampling in the Scene view.")]
        public bool showDots = false;
        [Min(1)] public int maxDots = 4000;
        [Min(0f)] public float dotSize = 0.012f;

        [Header("Fire / Smoke")]
        [Tooltip("Inject density / heat into a BrazeFX fluid Container wherever this effect covers the surface " +
                 "(the covered mask becomes a fire/smoke emission source).")]
        public bool emitToFluid = false;
        [Tooltip("Soot / smoke injected at each covered cell.")]
        [BrazeAnimatable("FIRE SMOKE/Density", 0f, 10f)]
        [Min(0f)] public float emitDensity = 1f;
        [Tooltip("Heat injected (drives ignition + buoyancy).")]
        [BrazeAnimatable("FIRE SMOKE/Temperature", 0f, 10f)]
        [Min(0f)] public float emitTemperature = 4f;
        [Tooltip("Combustible fuel injected (becomes fire).")]
        [BrazeAnimatable("FIRE SMOKE/Fuel", 0f, 10f)]
        [Min(0f)] public float emitFuel = 3f;
        [Tooltip("Overall multiplier on the injected channels.")]
        [BrazeAnimatable("FIRE SMOKE/Strength", 0f, 4f)]
        [Min(0f)] public float emitStrength = 1f;
        [Tooltip("Emit only from the advancing front edge instead of the whole covered area.")]
        public bool emitFrontOnly = false;

        /// <summary>Active surface effects — a fluid Container gathers these to inject their covered mask.</summary>
        public static readonly List<BrazeSurfaceEffect> Active = new();
        /// <summary>Per-cell injected channels (density, temperature, fuel) × overall strength.</summary>
        public Vector3 EmitChannels => new Vector3(emitDensity, emitTemperature, emitFuel) * Mathf.Max(0f, emitStrength);

        // ── runtime: target + meshes ───────────────────────────────────────────────────
        SkinnedMeshRenderer smr;
        MeshFilter mf;
        Mesh sourceMesh;                       // sharedMesh whose topology we welded + graphed
        Mesh work;                             // mesh we draw/colour (skinned: rebaked each frame; static: a copy)
        Matrix4x4 currentL2W = Matrix4x4.identity;
        readonly List<Vector3> localScratch = new();
        readonly List<Vector3> localNrmScratch = new();
        Color[] colorScratch;                  // per RAW vertex coverage colour for the overlay
        readonly List<Vector3> restRawList = new();  // rest-pose pos per RAW vertex → stable triplanar coord
        Material overlayMat;
        int lastBakeFrame = -1;                // BakeWorld is idempotent per frame (shared by Update/LateUpdate/gizmo)

        static readonly int IdNoiseTex = Shader.PropertyToID("_NoiseTex");
        static readonly int IdNoiseTiling = Shader.PropertyToID("_NoiseTiling");
        static readonly int IdNoiseBlend = Shader.PropertyToID("_NoiseBlend");
        static readonly int IdNoiseStrength = Shader.PropertyToID("_NoiseStrength");
        static readonly int IdNoiseLow = Shader.PropertyToID("_NoiseLow");
        static readonly int IdNoiseHigh = Shader.PropertyToID("_NoiseHigh");
        static readonly int IdNoiseGamma = Shader.PropertyToID("_NoiseGamma");
        static readonly int IdNoiseContrast = Shader.PropertyToID("_NoiseContrast");

        // ── runtime: coverage graph (protected → subclasses fill `reach`) ──────────────
        protected int weldedCount;
        protected int[] rawToWelded;           // raw vertex → welded id (welds seams so the front isn't blocked)
        protected int[] weldRep;               // welded id → a representative raw vertex (world-pos lookup)
        protected Vector3[] weldRest;          // welded id → rest-pose position (stable geodesic edge weights)
        protected int[] adjStart;              // CSR offsets, length weldedCount+1
        protected int[] adj;                   // CSR neighbour welded ids
        protected float[] reach;               // per vertex: value at which it becomes covered (+inf = unreached)
        protected Vector3[] worldVerts;        // current world position per RAW vertex (dots + selection + emission)
        protected Vector3[] worldNrm;          // current world normal per RAW vertex (mesh-shape particle emission)
        Vector3[] worldVel;                    // current world velocity per RAW vertex (particle motion inheritance)
        Vector3[] prevWorldVerts;              // last frame's positions → per-vertex velocity
        bool prevWorldValid;
        protected float coverFront;            // coverage threshold: a vertex is covered when reach[w] <= coverFront

        Look appliedLook = Look.Frost;         // last preset applied (so manual tweaks aren't clobbered)

        // ── subclass hooks ─────────────────────────────────────────────────────────────
        /// <summary>Fill <see cref="reach"/> and set <see cref="coverFront"/> for this frame's coverage.</summary>
        protected abstract void UpdateCoverage();
        /// <summary>Called once after the surface graph (re)builds — reset subclass state here.</summary>
        protected virtual void OnBuilt() { }
        /// <summary>False = nothing to draw/emit yet (skips the per-frame work).</summary>
        protected virtual bool HasCoverage => true;
        protected virtual void OnEffectEnable() { }
        protected virtual void OnEffectValidate() { }
        /// <summary>Extra scene gizmos (e.g. a selection volume) — drawn every repaint, before the dots.</summary>
        protected virtual void OnEffectGizmos() { }

        // ── lifecycle (protected so Unity reliably invokes them on subclasses) ──────────
        protected virtual void OnEnable()
        {
            ResolveTarget();
            if (overlayMat == null)
            {
                var sh = BrazeRuntimeShaderLibrary.Find("BrazeFX/SpreadSurface");
                if (sh != null) overlayMat = new Material(sh) { hideFlags = HideFlags.DontSave };
                else Debug.LogError("[BrazeFX] Shader 'BrazeFX/SpreadSurface' not found.");
            }
            OnEffectEnable();
            BrazeRuntimeRegistry.AddUnique(Active, this);
        }

        protected virtual void OnDisable()
        {
            BrazeRuntimeRegistry.Remove(Active, this);
            DestroyTemp(ref work);
            if (overlayMat != null) { DestroyImmediateSafe(overlayMat); overlayMat = null; }
            ReleaseGpuEmission();
        }

        protected virtual void OnValidate()
        {
            if (look != Look.Custom && look != appliedLook) { ApplyLook(look); appliedLook = look; }
            OnEffectValidate();
        }

        public void OnBrazeAnimated() => OnValidate();

        protected virtual void Update()
        {
            if (!EnsureSetup()) return;
            UpdateCoverage();
        }

        protected virtual void LateUpdate()
        {
            if (!HasCoverage || !EnsureSetup()) return;
            bool draw = renderSurface && overlayMat != null;
            if (!draw && !emitToFluid) return;
            BakeWorld();                          // current pose → worldVerts (overlay AND fluid emission)
            if (!draw) return;
            WriteColors();
            // Rest-pose position per vertex (UV1) = the triplanar coordinate. Re-set each frame because
            // BakeMesh rewrites all channels; this is what locks the noise to the surface across animation.
            if (restRawList.Count == work.vertexCount) work.SetUVs(1, restRawList);
            work.SetColors(colorScratch);
            PushNoise();
            int layer = target != null ? target.gameObject.layer : gameObject.layer;
            for (int s = 0, sc = work.subMeshCount; s < sc; s++)
                Graphics.DrawMesh(work, currentL2W, overlayMat, layer, null, s);
        }

        protected virtual void OnDrawGizmos()
        {
            OnEffectGizmos();                                       // subclass extras (e.g. selection volume) — always
            if (!showDots || !EnsureSetup()) return;
            BakeWorld();
            if (!Application.isPlaying) UpdateCoverage();           // edit-mode preview of coverage
            int n = worldVerts.Length;
            int stride = Mathf.Max(1, Mathf.CeilToInt(n / (float)Mathf.Max(1, maxDots)));
            float band = Mathf.Max(dotSize * 2f, 0.04f);
            Vector3 size = Vector3.one * dotSize;
            for (int i = 0; i < n; i += stride)
            {
                float r = reach[rawToWelded[i]];
                Gizmos.color = (float.IsInfinity(r) || r > coverFront) ? new Color(0.25f, 0.5f, 1f, 0.5f)
                             : (r > coverFront - band) ? new Color(1f, 0.55f, 0.1f, 1f)
                             : new Color(0.4f, 0.85f, 0.3f, 1f);
                Gizmos.DrawCube(worldVerts[i], size);
            }
        }

        // ── look preset ────────────────────────────────────────────────────────────────
        /// <summary>Artist preset — sets the covered-surface colours/edge for a recognisable material look.</summary>
        public void ApplyLook(Look l)
        {
            switch (l)
            {
                case Look.Frost:                  // icy white-blue, sharp bright crystalline edge
                    fillColor = new(0.75f, 0.88f, 1f, 0.80f); edgeColor = new(1f, 1f, 1f, 1f); edgeWidth = 0.04f; fadeBehind = 0.06f; break;
                case Look.Corruption:             // dark veiny fill, toxic glowing edge
                    fillColor = new(0.12f, 0.04f, 0.16f, 0.85f); edgeColor = new(0.70f, 1f, 0.20f, 1f); edgeWidth = 0.05f; fadeBehind = 0.05f; break;
                case Look.Paint:                  // solid wet colour, tight soft edge
                    fillColor = new(0.85f, 0.15f, 0.15f, 0.95f); edgeColor = new(1f, 0.55f, 0.55f, 1f); edgeWidth = 0.02f; fadeBehind = 0.02f; break;
                case Look.Ants:                   // faint trail only
                    fillColor = new(0.05f, 0.05f, 0.06f, 0.18f); edgeColor = new(0.10f, 0.10f, 0.12f, 0.40f); edgeWidth = 0.03f; fadeBehind = 0.04f; break;
            }
        }

        // ── fluid emission ─────────────────────────────────────────────────────────────
        /// <summary>Append the covered surface points + world normals + world velocities, regardless of fluid
        /// emission — for a particle system to spawn from this effect's mask (along the normal, inheriting the
        /// surface motion). Uses the last-baked pose; velocities are per-vertex so they're stable as the
        /// selection changes.</summary>
        public void CollectCoveredPoints(List<Vector3> worldOut, List<Vector3> normalOut, List<Vector3> velOut)
        {
            if (reach == null || worldVerts == null || worldNrm == null || worldVel == null || weldRep == null) return;
            float band = Mathf.Max(1e-3f, edgeWidth + fadeBehind);
            for (int w = 0; w < weldedCount; w++)
            {
                float r = reach[w];
                if (float.IsInfinity(r) || r > coverFront) continue;      // uncovered
                if (emitFrontOnly && r < coverFront - band) continue;     // behind the leading edge
                int raw = weldRep[w];
                worldOut.Add(worldVerts[raw]);
                normalOut.Add(worldNrm[raw]);
                velOut.Add(worldVel[raw]);
            }
        }

        // (CollectCovered — the CPU walk over every covered vertex for fluid emission — was removed:
        // fluid emission is GPU-native now, see TryGetGpuEmission below. CollectCoveredPoints above
        // stays: the particle system spawns from it and clump counts are small.)

        // ── GPU emission export (no CPU vertex walk) ───────────────────────────────────
        // Hands the solver everything it needs to run the coverage test + splat on the GPU: the LIVE
        // vertex buffer (GPU-skinned for a SkinnedMeshRenderer — tracks animation at full rate even
        // between descriptor refreshes), the welded→raw index map, and the reach field. Replaces the
        // CPU CollectCovered→AccumPoint walk that cost multiple ms/step on dense skinned meshes.
        ComputeBuffer gpuWeldRep, gpuReach;
        GraphicsBuffer gpuVerts;
        int gpuWeldRepCount = -1;
        bool warnedNoGpuEmission;
        // reach[] mutation counter → the GPU copy re-uploads only when coverage actually changed.
        // All mutation sites live in THIS class (RunDijkstra / ClearReach / graph rebuild).
        int reachVersion;
        int reachUploadedVersion = -1;

        /// <summary>Fill a GPU surface-source descriptor. Returns false (the effect does not emit) when
        /// there's nothing to emit or the platform/mesh can't expose a raw vertex buffer.</summary>
        public bool TryGetGpuEmission(out BrazeSolver.SurfaceSourceGpu src)
        {
            src = default;
            if (!emitToFluid || weldedCount <= 0 || weldRep == null || reach == null) return false;
            Mesh topo = TopologyMesh();
            if (topo == null) return false;

            // Position must live in vertex stream 0 for the raw-buffer fetch (true for standard meshes;
            // the skinned output buffer carries stream-0 attributes).
            if (topo.GetVertexAttributeStream(UnityEngine.Rendering.VertexAttribute.Position) != 0)
                return FailNoGpu("vertex position is not in stream 0");

            // CACHED vertex-buffer handle: GetVertexBuffer() allocates a new GraphicsBuffer wrapper
            // every call (driver + GC churn at 60Hz). The underlying buffer is stable — GPU skinning
            // updates it in place — so fetch once and re-fetch only if the handle goes invalid
            // (mesh swap / reskin realloc). Worst case with renderer double-buffering, splats read a
            // one-frame-old pose: irrelevant for emission.
            if (gpuVerts == null || !gpuVerts.IsValid())
            {
                gpuVerts?.Dispose(); gpuVerts = null;
                if (smr != null)
                {
                    smr.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
                    gpuVerts = smr.GetVertexBuffer();             // GPU-skinned positions, renderer-local
                }
                else if (mf != null && mf.sharedMesh != null)
                {
                    topo.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
                    gpuVerts = topo.GetVertexBuffer(0);           // static mesh, mesh-local
                }
                if (gpuVerts == null)
                    return FailNoGpu("the mesh has no GPU vertex buffer (is GPU skinning enabled in Project Settings?)");
            }
            currentL2W = smr != null ? smr.transform.localToWorldMatrix
                       : mf != null ? mf.transform.localToWorldMatrix : currentL2W;

            if (gpuWeldRep == null || gpuWeldRepCount != weldedCount)
            {
                gpuWeldRep?.Release();
                gpuReach?.Release();
                gpuWeldRep = new ComputeBuffer(weldedCount, sizeof(int));
                gpuReach = new ComputeBuffer(weldedCount, sizeof(float));
                gpuWeldRep.SetData(weldRep, 0, 0, weldedCount);
                gpuWeldRepCount = weldedCount;
                reachUploadedVersion = -1;                        // force the first reach upload
            }
            // Upload reach only when the coverage actually changed (the spread front moving is a
            // UNIFORM — reach itself only changes when Dijkstra reruns), not every step.
            if (reachUploadedVersion != reachVersion)
            {
                gpuReach.SetData(reach, 0, 0, weldedCount);
                reachUploadedVersion = reachVersion;
            }

            float band = Mathf.Max(1e-3f, edgeWidth + fadeBehind);
            src = new BrazeSolver.SurfaceSourceGpu
            {
                verts = gpuVerts,
                stride = topo.GetVertexBufferStride(0),
                posOffset = topo.GetVertexAttributeOffset(UnityEngine.Rendering.VertexAttribute.Position),
                weldedCount = weldedCount,
                weldRep = gpuWeldRep,
                reach = gpuReach,
                front = coverFront,
                bandMin = emitFrontOnly ? coverFront - band : -1f,
                channels = EmitChannels,
                localToWorld = currentL2W,
            };
            return true;
        }

        bool FailNoGpu(string reason)
        {
            if (!warnedNoGpuEmission)
            {
                warnedNoGpuEmission = true;
                Debug.LogWarning($"[BrazeFX] '{name}' can't emit into the fluid: {reason}. " +
                                 "Fluid emission is GPU-native (the CPU per-vertex path was removed).", this);
            }
            return false;
        }

        void ReleaseGpuEmission()
        {
            gpuVerts?.Dispose(); gpuVerts = null;
            gpuWeldRep?.Release(); gpuWeldRep = null;
            gpuReach?.Release(); gpuReach = null;
            gpuWeldRepCount = -1;
        }

        // ── geodesic coverage helper (multi-source Dijkstra over rest-pose edge lengths) ─
        /// <summary>Flood reach[] outward from `srcSeeds` (init distance per seed, or 0 when srcInit is null).</summary>
        protected void RunDijkstra(IReadOnlyList<int> srcSeeds, IReadOnlyList<float> srcInit)
        {
            for (int i = 0; i < weldedCount; i++) reach[i] = float.PositiveInfinity;
            var heap = new MinHeap(weldedCount + 8);
            for (int k = 0; k < srcSeeds.Count; k++)
            {
                int s = srcSeeds[k];
                if ((uint)s >= (uint)weldedCount) continue;
                float init = srcInit != null ? srcInit[k] : 0f;
                if (init < reach[s]) { reach[s] = init; heap.Push(init, s); }
            }
            while (heap.Count > 0)
            {
                heap.Pop(out float d, out int u);
                if (d > reach[u]) continue;                       // stale heap entry
                for (int e = adjStart[u]; e < adjStart[u + 1]; e++)
                {
                    int v = adj[e];
                    float nd = d + Vector3.Distance(weldRest[u], weldRest[v]);
                    if (nd < reach[v]) { reach[v] = nd; heap.Push(nd, v); }
                }
            }
            reachVersion++;   // GPU reach copy is stale → re-upload on next gather
        }

        /// <summary>Reset reach[] to all-uncovered (+inf).</summary>
        protected void ClearReach()
        {
            if (reach == null) return;
            for (int i = 0; i < reach.Length; i++) reach[i] = float.PositiveInfinity;
            reachVersion++;   // GPU reach copy is stale → re-upload on next gather
        }

        // ── setup: weld + adjacency graph (one-time per topology) ──────────────────────
        protected void ResolveTarget()
        {
            if (target == null)
                target = GetComponentInChildren<SkinnedMeshRenderer>() ?? GetComponentInParent<SkinnedMeshRenderer>();
            if (target == null) target = FindAnyObjectByType<SkinnedMeshRenderer>();
            smr = target as SkinnedMeshRenderer;
            mf = target != null ? target.GetComponent<MeshFilter>() : null;
        }

        Mesh TopologyMesh() => smr != null ? smr.sharedMesh : (mf != null ? mf.sharedMesh : null);

        protected bool EnsureSetup()
        {
            if (smr == null && mf == null) ResolveTarget();
            Mesh topo = TopologyMesh();
            if (topo == null) return false;
            if (topo == sourceMesh && adj != null) return true;
            sourceMesh = topo;
            DestroyTemp(ref work);                // topology changed → rebuild the draw mesh too
            Build(topo);
            return adj != null;
        }

        void Build(Mesh mesh)
        {
            Vector3[] rest = mesh.vertices;
            int n = rest.Length;
            int[] tris = mesh.triangles;

            // Weld by position so the front crosses UV / normal seams (which split verts at one point).
            float eps = Mathf.Max(1e-5f, mesh.bounds.size.magnitude * 1e-4f);
            float invEps = 1f / eps;
            var map = new Dictionary<Vector3Int, int>(n);
            rawToWelded = new int[n];
            var repList = new List<int>();
            var restList = new List<Vector3>();
            for (int i = 0; i < n; i++)
            {
                var key = new Vector3Int(Mathf.RoundToInt(rest[i].x * invEps),
                                         Mathf.RoundToInt(rest[i].y * invEps),
                                         Mathf.RoundToInt(rest[i].z * invEps));
                if (!map.TryGetValue(key, out int id))
                {
                    id = restList.Count; map[key] = id; restList.Add(rest[i]); repList.Add(i);
                }
                rawToWelded[i] = id;
            }
            weldedCount = restList.Count;
            weldRest = restList.ToArray();
            weldRep = repList.ToArray();

            var seen = new HashSet<long>();
            var lists = new List<int>[weldedCount];
            for (int i = 0; i < weldedCount; i++) lists[i] = new List<int>();
            void Edge(int a, int b)
            {
                if (a == b) return;
                int u = Mathf.Min(a, b), v = Mathf.Max(a, b);
                if (seen.Add((long)u * weldedCount + v)) { lists[a].Add(b); lists[b].Add(a); }
            }
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int a = rawToWelded[tris[t]], b = rawToWelded[tris[t + 1]], c = rawToWelded[tris[t + 2]];
                Edge(a, b); Edge(b, c); Edge(c, a);
            }

            adjStart = new int[weldedCount + 1];
            int tot = 0;
            for (int i = 0; i < weldedCount; i++) { adjStart[i] = tot; tot += lists[i].Count; }
            adjStart[weldedCount] = tot;
            adj = new int[tot];
            for (int i = 0; i < weldedCount; i++)
            {
                var li = lists[i];
                for (int k = 0; k < li.Count; k++) adj[adjStart[i] + k] = li[k];
            }

            reach = new float[weldedCount];
            for (int i = 0; i < weldedCount; i++) reach[i] = float.PositiveInfinity;
            reachVersion++;   // fresh graph → GPU reach copy is stale
            worldVerts = new Vector3[n];
            worldNrm = new Vector3[n];
            worldVel = new Vector3[n];
            prevWorldVerts = new Vector3[n];
            prevWorldValid = false;
            colorScratch = new Color[n];
            restRawList.Clear();
            restRawList.AddRange(rest);          // bind the noise to the rest pose so it deforms WITH the surface
            lastBakeFrame = -1;
            coverFront = 0f;
            OnBuilt();
        }

        // ── per-vertex overlay colour from coverage state ──────────────────────────────
        void WriteColors()
        {
            float ew = Mathf.Max(1e-4f, edgeWidth);
            float fb = Mathf.Max(1e-4f, fadeBehind);
            int n = colorScratch.Length;
            for (int i = 0; i < n; i++)
            {
                float r = reach[rawToWelded[i]];
                float behind = coverFront - r;                    // >0 = covered, distance behind the front
                if (float.IsInfinity(r) || behind <= 0f) { colorScratch[i] = default; continue; }
                Color c = Color.Lerp(edgeColor, fillColor, Mathf.Clamp01(behind / ew));   // bright edge → fill
                c.a = Mathf.Lerp(edgeColor.a, fillColor.a, Mathf.Clamp01(behind / fb));
                colorScratch[i] = c;
            }
        }

        void PushNoise()
        {
            // Noise mixed into the fill, cubic (object-space triplanar) projection so it never stretches.
            // Strength is forced to 0 with no texture so the default white texture can't wash the fill flat.
            overlayMat.SetTexture(IdNoiseTex, noiseTexture);
            overlayMat.SetFloat(IdNoiseTiling, Mathf.Max(0f, noiseTiling));
            overlayMat.SetFloat(IdNoiseBlend, Mathf.Max(1f, noiseBlend));
            overlayMat.SetFloat(IdNoiseStrength, noiseTexture != null ? noiseStrength : 0f);
            overlayMat.SetFloat(IdNoiseLow, noiseLow);
            overlayMat.SetFloat(IdNoiseHigh, noiseHigh);
            // mid → gamma so a slider at 0.5 = linear (gamma 1); <0.5 brightens, >0.5 darkens.
            overlayMat.SetFloat(IdNoiseGamma, Mathf.Log(0.5f) / Mathf.Log(Mathf.Clamp(noiseMid, 0.02f, 0.98f)));
            overlayMat.SetFloat(IdNoiseContrast, Mathf.Max(0f, noiseContrast));
        }

        protected int NearestWelded(Vector3 worldPos)
        {
            if (worldVerts == null || weldRep == null) return -1;
            int best = -1; float bd = float.MaxValue;
            for (int w = 0; w < weldedCount; w++)
            {
                float dd = (worldVerts[weldRep[w]] - worldPos).sqrMagnitude;
                if (dd < bd) { bd = dd; best = w; }
            }
            return best;
        }

        // Bake the current deformed pose into `work`, cache its world matrix, and fill worldVerts.
        // Idempotent per frame so Update/LateUpdate/gizmo callers share one bake.
        protected void BakeWorld()
        {
            if (worldVerts == null) return;
            if (lastBakeFrame == Time.frameCount && work != null) return;
            if (smr != null)
            {
                if (work == null) work = new Mesh { hideFlags = HideFlags.DontSave };
                smr.BakeMesh(work, true);                          // skin + blendshapes applied, renderer-local
                currentL2W = smr.transform.localToWorldMatrix;
            }
            else if (mf != null && mf.sharedMesh != null)
            {
                if (work == null) { work = Instantiate(mf.sharedMesh); work.hideFlags = HideFlags.DontSave; }
                currentL2W = mf.transform.localToWorldMatrix;
            }
            else return;
            lastBakeFrame = Time.frameCount;
            work.GetVertices(localScratch);
            int n = Mathf.Min(localScratch.Count, worldVerts.Length);
            for (int i = 0; i < n; i++) worldVerts[i] = currentL2W.MultiplyPoint3x4(localScratch[i]);
            // Guard: worldNrm may be null if the graph was built by an older code version still live in memory
            // (Enter-Play-Mode-without-domain-reload keeps runtime arrays across a recompile).
            if (worldNrm == null || worldNrm.Length != worldVerts.Length) worldNrm = new Vector3[worldVerts.Length];
            work.GetNormals(localNrmScratch);
            if (localNrmScratch.Count >= n)
            {
                Matrix4x4 n2w = currentL2W.inverse.transpose;   // correct normal transform (handles non-uniform scale)
                for (int i = 0; i < n; i++) worldNrm[i] = n2w.MultiplyVector(localNrmScratch[i]).normalized;
            }
            else for (int i = 0; i < n; i++) worldNrm[i] = Vector3.up;

            // Per-vertex world velocity from the previous frame (animation + rigid motion) — keyed by vertex id, so
            // particle motion inheritance is correct regardless of which verts a selection picks. BakeWorld runs
            // once per frame, so Time.deltaTime is the right interval.
            int N = worldVerts.Length;
            if (worldVel == null || worldVel.Length != N) worldVel = new Vector3[N];
            if (prevWorldVerts == null || prevWorldVerts.Length != N) { prevWorldVerts = new Vector3[N]; prevWorldValid = false; }
            float vdt = Mathf.Max(Time.deltaTime, 1e-5f);
            if (prevWorldValid) for (int i = 0; i < n; i++) worldVel[i] = (worldVerts[i] - prevWorldVerts[i]) / vdt;
            else for (int i = 0; i < N; i++) worldVel[i] = Vector3.zero;
            System.Array.Copy(worldVerts, prevWorldVerts, n);
            prevWorldValid = true;
        }

        static void DestroyTemp(ref Mesh m)
        {
            if (m == null) return;
            DestroyImmediateSafe(m);
            m = null;
        }

        static void DestroyImmediateSafe(Object o)
        {
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        // Tiny binary min-heap (float key, int value) — avoids depending on .NET PriorityQueue availability.
        protected sealed class MinHeap
        {
            float[] k; int[] v; int n;
            public MinHeap(int cap) { cap = Mathf.Max(16, cap); k = new float[cap]; v = new int[cap]; }
            public int Count => n;
            public void Push(float key, int val)
            {
                if (n == k.Length) { System.Array.Resize(ref k, n * 2); System.Array.Resize(ref v, n * 2); }
                int i = n++; k[i] = key; v[i] = val;
                while (i > 0) { int p = (i - 1) >> 1; if (k[p] <= k[i]) break; Swap(p, i); i = p; }
            }
            public void Pop(out float key, out int val)
            {
                key = k[0]; val = v[0]; n--;
                if (n > 0)
                {
                    k[0] = k[n]; v[0] = v[n]; int i = 0;
                    while (true)
                    {
                        int l = 2 * i + 1, r = 2 * i + 2, m = i;
                        if (l < n && k[l] < k[m]) m = l;
                        if (r < n && k[r] < k[m]) m = r;
                        if (m == i) break;
                        Swap(m, i); i = m;
                    }
                }
            }
            void Swap(int a, int b) { (k[a], k[b]) = (k[b], k[a]); (v[a], v[b]) = (v[b], v[a]); }
        }
    }
}
