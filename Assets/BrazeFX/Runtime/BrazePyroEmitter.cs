using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace BrazeFX
{
    /// <summary>
    /// A fire/smoke source. Place it ANYWHERE in the scene — it registers itself and every
    /// <see cref="BrazeContainer"/> picks it up by world position (no parenting required).
    /// Sphere/Cylinder/Cone/Capsule/Torus use the authored dimensions; Box uses the GameObject's Transform SCALE.
    /// </summary>
    [AddComponentMenu("BrazeFX/Fire/Braze Pyro Emitter", 1)]
    [DefaultExecutionOrder(-100)]
    [BrazeTimelineCurated]
    [ExecuteAlways]   // self-register in edit mode too, so the timeline's edit-mode sim can emit
    public sealed class BrazePyroEmitter : MonoBehaviour, IBrazeAnimatedTarget, IBrazeAnimatableFilter
    {
        /// <summary>All enabled emitters in the scene (registry — no hierarchy constraint).</summary>
        public static readonly List<BrazePyroEmitter> Active = new();

        public enum Shape { Sphere = 0, Box = 1, Cylinder = 2, Cone = 3, Capsule = 4, Torus = 5, Mesh = 6 }

        /// <summary>Which LOCAL axis the long shapes (Cylinder/Cone/Capsule) extend along, and the Torus ring's
        /// normal points along. Purely reorients the source footprint — it does NOT touch the Push/Jet direction
        /// (those always aim along the Transform's forward). Sphere/Box ignore it.</summary>
        public enum Axis { X = 0, Y = 1, Z = 2 }

        public Shape shape = Shape.Sphere;

        [Tooltip("Which local axis the Cylinder / Cone / Capsule extend along (and the Torus ring lies perpendicular to). " +
                 "This only reorients the SHAPE — Push and Jet still aim along the emitter's forward arrow, so you can " +
                 "lay a cylinder on its side and still push it any direction. Sphere and Box ignore this.")]
        public Axis shapeAxis = Axis.Y;

        [Tooltip("Mesh shape: the STATIC object to emit from. The emitter samples this object's SURFACE into emission " +
                 "points that follow its Transform — move/rotate/scale it and the fume inherits that motion. " +
                 "For a deforming character, use Source Skinned Mesh instead. The mesh must have Read/Write enabled.")]
        public MeshFilter sourceMesh;
        [Tooltip("Mesh shape: a deforming SKINNED character to emit from. The deformed surface is read straight off the " +
                 "GPU (no per-frame CPU skinning) and the fume tracks the animated body. Read/Write must be enabled. " +
                 "Takes priority over Source Mesh when set.")]
        public SkinnedMeshRenderer sourceSkinnedMesh;
        [Tooltip("Mesh shape: world-space spacing between surface samples. Smaller = denser coverage but more " +
                 "points (capped). Aim near the container's voxel size.")]
        [Range(0.01f, 1f)] public float meshSpacing = 0.1f;
        [Tooltip("Mesh shape: push the emitted fume out along the mesh surface normals, in world units/sec. " +
                 "0 = none (the fume just rises by Buoyancy / drifts with the mesh's motion).")]
        [Range(0f, 20f)] public float meshNormalSpeed = 0f;
        [Tooltip("Mesh shape: maximum intact surface points used for connected particle spawn candidates each frame. " +
                 "A Surface Mask may reduce this count. Fractured fluid emission uses Emitting Fragments (%) instead.")]
        [Range(8, 4096)] public int meshParticleSpawnMaxPoints = 2048;

        [Tooltip("Mesh shape: when the source mesh has a Voronoi Fracture, emit from its FLYING fragments once it " +
                 "shatters (instead of the static surface — which would leave fire floating where the object was). " +
                 "Auto-detected; turn off to treat it as a plain mesh.")]
        public bool emitFromFragments = true;
        [Tooltip("Mesh shape (fractured): percentage of Voronoi fragments chosen as persistent fire/smoke emitters. " +
                 "The GPU selects an evenly distributed deterministic subset; lowering this also reduces fragment " +
                 "dispatch and sparse-hash memory. 0 = no fractured-piece emission, 100 = every fragment.")]
        [Range(0f, 100f)] public float fragmentEmissionPercent = 100f;
        [Tooltip("Mesh shape (fractured): how strongly the fume flies along with each fragment. 0 = stays put and " +
                 "just rises by Buoyancy; 1 = fully rides the piece's motion.")]
        [Range(0f, 1f)] public float fragmentVelocity = 1f;

        [Tooltip("Mesh shape: gate emission by a Braze Surface Mask so fire/smoke only emits from the masked region " +
                 "of the surface. Soft edges, growth and filters all carry through automatically because the pyro " +
                 "mesh gate evaluates the same GPU field as the preview.")]
        public bool useMask = false;
        [Tooltip("The Surface Mask to gate by. Leave empty to auto-use a Braze Surface Mask on the Source Mesh's object.")]
        public BrazeSurfaceMask mask;

        [Tooltip("World-space radius for sphere, cylinder, cone, capsule, and torus major radius. Box ignores this — it uses the Transform scale.")]
        [BrazeAnimatable("SHAPE/Radius", 0f, 2f)] public float radius = 0.12f;
        [Tooltip("World-space height for cylinder, cone, and capsule, measured along the chosen Axis. The cone's tip points toward the positive end of that axis.")]
        [BrazeAnimatable("SHAPE/Height", 0f, 8f)] public float height = 1f;
        [Tooltip("World-space tube radius for torus emitters. Radius is the ring radius.")]
        [BrazeAnimatable("SHAPE/Tube Radius", 0f, 2f)] public float tubeRadius = 0.04f;

        [Tooltip("Soot/smoke injected directly.")]
        [BrazeAnimatable("CHANNELS/Density", 0f, 8f)] public float density = 1f;
        [Tooltip("Heat — drives ignition and buoyancy.")]
        [BrazeAnimatable("CHANNELS/Temperature", 0f, 12f)] public float temperature = 4f;
        [Tooltip("Fuel — combustion converts it to heat, smoke and fire.")]
        [BrazeAnimatable("CHANNELS/Fuel", 0f, 12f)] public float fuel = 3f;

        [Tooltip("Emission intensity multiplier.")]
        [BrazeAnimatable("OUTPUT/Strength", 0f, 4f)] public float strength = 1f;

        [Tooltip("Buoyancy: how strongly the HEAT this emitter injects rises (reference voxels/sec² at full heat). " +
                 "Temperature-driven, so it lifts the fume wherever the heat travels — not just at the nozzle. " +
                 "Braze scales it with container resolution to preserve world motion. 0 = no lift. NEGATIVE = the heat " +
                 "SINKS instead (a downdraft / heavy-gas fall).")]
        [Range(-300f, 300f)] [BrazeAnimatable("MOTION/LIFT & SETTLE/Buoyancy", -300f, 300f)] public float motionBuoyancy = 12f;
        [Tooltip("Push: a directional push along this emitter's forward axis (rotate the emitter to AIM it), in " +
                 "reference voxels/sec². Braze scales it with container resolution. Applied post-projection so it stays " +
                 "crisp. Signed: + forward, − backward. This is the " +
                 "single merged directional knob (replaces the old Forward Force + gas Axis jet).")]
        [BrazeAnimatable("MOTION/PUSH & EXPANSION/Push", -600f, 600f)] public float motionForward = 0f;
        [Tooltip("Jet: INFLOW velocity along the emitter's forward axis (rotate to AIM), in world metres/sec. The gas is " +
                 "BORN moving — a directional jet (flamethrower / dragon fire), unlike Push which is a force field. " +
                 "Crank it for a long dragon breath; combine with Buoyancy so the jet shoots out then the heat lifts.")]
        [BrazeAnimatable("MOTION/PUSH & EXPANSION/Jet (m/s)", 0f, 300f)] public float jetSpeed = 0f;
        [Tooltip("Pyro-style initial gas velocity in this emitter's LOCAL axes, in world metres/sec. Unlike Jet, this is " +
                 "not limited to the forward arrow; it matches PyroEmitter.velocity and is transformed by the emitter rotation.")]
        [BrazeAnimatable("MOTION/PUSH & EXPANSION/Source Velocity", -300f, 300f)]
        public Vector3 sourceVelocity = new Vector3(0f, 2.5f, 0f);
        [Tooltip("How strongly the source pulls gas toward Source Velocity. Uses an exact exponential blend, so it stays consistent across sim rates.")]
        [Range(0f, 30f)] [BrazeAnimatable("MOTION/PUSH & EXPANSION/Source Blend", 0f, 30f)] public float sourceVelocityBlend = 8f;
        [Tooltip("Pyro-style random velocity jitter added at the source, in world metres/sec. Breaks up the base so it is not born as a smooth blob.")]
        [Range(0f, 3f)] [BrazeAnimatable("MOTION/PUSH & EXPANSION/Source Turbulence", 0f, 3f)] public float sourceTurbulence = 0.8f;
        [Tooltip("Source-local radial push in reference voxels/sec². Braze scales it with container resolution. " +
                 "Positive = outward from the emitter centre, negative = inward.")]
        [BrazeAnimatable("MOTION/PUSH & EXPANSION/Radial Force", -600f, 600f)] public float motionRadial = 0f;
        [Tooltip("Drag / Settle: speed-proportional drag (∝ v², like real air resistance). Tames FAST/wild motion " +
                 "(a blast spike) while barely touching gentle drift — so a settling plume stays alive, not inert. " +
                 "0 = momentum coasts freely. Animate UP to calm a blast; high values bring it toward a stop.")]
        [Range(0f, 3f)] [BrazeAnimatable("MOTION/LIFT & SETTLE/Drag (settle)", 0f, 3f)] public float motionDrag = 0f;

        [Tooltip("Emitter-local gas pressure burst. Adds outward pressure only inside this emitter's source footprint.")]
        [Range(0f, 60f)] [BrazeAnimatable("MOTION/PUSH & EXPANSION/Pressure Burst", 0f, 60f)] public float gasPressureBurst = 3f;
        [Tooltip("0 = gas push follows active combustion; 1 = also follows broader hot smoke in this emitter's footprint.")]
        [Range(0f, 1f)] [BrazeAnimatable("MOTION/PUSH & EXPANSION/Source Spread")] public float gasSourceSpread = 0f;
        [Tooltip("Shapes where this emitter's gas push is strongest. Lower = broad push, higher = tighter punch.")]
        [Range(0.25f, 4f)] [BrazeAnimatable("MOTION/PUSH & EXPANSION/Source Sharpness")] public float gasSourceSharpness = 1f;

        [Tooltip("SLAVE particle systems. They spawn ONLY while this emitter emits (burst windows and animated " +
                 "Strength gate them too) and spawn FROM this emitter's shape — their own Shape and duration " +
                 "window are ignored. They keep their own rate, lifetime, motion and rendering.")]
        public List<BrazeParticleSystem> connectedParticleEmitters = new();
        [Tooltip("Converts this fluid emitter's Motion knobs into connected-particle start velocity. " +
                 "0 = connected particles receive no emitter motion.")]
        [Min(0f)] public float particleMotionScale = 0.03f;
        [Tooltip("How strongly connected particles are CARRIED by the live fluid motion (advection) over their " +
                 "lifetime — they get swept by the smoke/fire's swirls and buoyant rise, not just a start nudge. " +
                 "0 = ignore the fluid (start-velocity inheritance only); 1 = track the fluid tightly. Free: it " +
                 "samples the container's existing velocity field.")]
        [Range(0f, 1f)] public float particleFluidFollow = 0f;
        [Tooltip("Multiplier on the fluid velocity the connected particles chase. 1 = move exactly with the fluid; " +
                 ">1 = EXAGGERATE — a small/slow flame can still fling its particles far (e.g. a tiny chimney fire " +
                 "whose embers shoot high). Direction is kept; only the speed is scaled. Needs Fluid Follow > 0.")]
        [Range(0f, 8f)] public float particleFluidFollowGain = 1f;
        [Tooltip("Per-particle random variation on Fluid Follow tightness. 0 = every particle locks to the fluid " +
                 "equally; 1 = wide spread (some lag heavily, some track tight) so the flow looks less uniform.")]
        [Range(0f, 1f)] public float particleFluidFollowRand = 0f;
        [Tooltip("Per-particle random ± variation on Follow Boost. 0 = all particles get the same boost; 1 = big " +
                 "spread of fling distances (a few embers shoot far past the rest). Needs Fluid Follow > 0.")]
        [Range(0f, 1f)] public float particleFluidFollowGainRand = 0f;

        [Tooltip("Emit only during a burst window (in frames), then stop. Off = always on.")]
        public bool burstMode = false;
        [Tooltip("Frame the burst starts (counted from when this emitter activates).")]
        public int startFrame = 0;
        [Tooltip("Fewest frames the burst emits for. Equal Min/Max = a fixed length.")]
        [FormerlySerializedAs("burstFrames")] public int burstFramesMin = 30;
        [Tooltip("Most frames the burst emits for. Each burst rolls a length between Min and Max.")]
        public int burstFramesMax = -1;   // -1 = "unset": scenes saved before the range existed collapse to Min
        [Tooltip("Repeat the burst window.")]
        public bool loop = false;
        [Tooltip("How many times the burst plays in total when Loop is on. 0 = forever.")]
        public int loopCount = 0;
        [Tooltip("Shortest silence between loops, in frames. Equal Min/Max = a fixed delay.")]
        [FormerlySerializedAs("loopDelay")] public int loopDelayMin = 30;
        [Tooltip("Longest silence between loops, in frames. Each loop rolls a delay between Min and Max.")]
        public int loopDelayMax = -1;   // -1 = "unset": scenes saved before the range existed collapse to Min

        [Tooltip("Draw the emission shape in the Scene view. Off = no gizmo at all (even when selected).")]
        public bool showGizmo = true;

        int frame;
        bool emitting = true;
        int loopsDone;     // completed burst windows since the last reset
        int cycleStart;    // local frame the current burst+delay cycle began on
        int cycleBurst;    // burst length rolled for the current cycle (cycles vary in length)
        int cycleDelay;    // delay rolled for the current cycle

        /// <summary>Channel multiplier this frame: strength while emitting, else 0.</summary>
        public float EmissionScale => emitting ? strength : 0f;

        void OnEnable() { ResetBurst(); BrazeRuntimeRegistry.AddUnique(Active, this); }
        void OnDisable()
        {
            BrazeRuntimeRegistry.Remove(Active, this);
            _deform?.Dispose(); _deform = null; _deformSrcId = int.MinValue;
            _meshFluidPrevPos?.Release(); _meshFluidPrevPos = null; _meshFluidPrevCount = -1; _meshFluidPrevKey = int.MinValue; _meshFluidHasPrev = false;
            _meshMaskRestPos?.Release(); _meshMaskRestPos = null; _meshMaskRestMeshId = int.MinValue; _meshMaskRestCount = -1;
            _meshGpuLocalPts?.Release(); _meshGpuLocalPts = null;
            _meshGpuLocalNrm?.Release(); _meshGpuLocalNrm = null;
            _meshGpuSampleVersion = -1; _meshGpuSampleCount = -1;
            _meshMaskPassIdx?.Release(); _meshMaskPassIdx = null;
            _meshMaskPassCount?.Release(); _meshMaskPassCount = null;
            _meshMaskPassBufferCount = -1; _meshMaskPassGate = null; _meshMaskPassSourceId = int.MinValue;
        }

        /// <summary>Rewind the burst window to its start. Call whenever the simulation clock resets to t=0
        /// (play-mode loop restart, editor-preview start/loop) so the burst re-fires in sync with the sim.</summary>
        [BrazeTimelineEvent("EMISSION/Reset Burst")]
        public void ResetBurst() { frame = 0; loopsDone = 0; cycleStart = 0; RollCycle(); emitting = !burstMode; }

        /// <summary>Roll this cycle's burst length and delay from their Min/Max ranges. Deterministic (hash of
        /// loop index + instance), so every sim restart replays the identical burst timing — matching the sim's
        /// own reset-and-replay behavior.</summary>
        void RollCycle()
        {
            cycleBurst = Roll(Mathf.Max(1, burstFramesMin), burstFramesMax, 0x9E3779);
            cycleDelay = Roll(Mathf.Max(0, loopDelayMin), loopDelayMax, 0x85EBCA);
        }

        int Roll(int min, int max, int salt)
        {
            max = Mathf.Max(min, max);   // Max below Min (incl. the -1 "unset" sentinel) = fixed value
            if (max == min) return min;
            uint h = (uint)this.BrazeInstanceId() * 747796405u ^ (uint)loopsDone * 2654435769u ^ (uint)salt * 3266489917u;
            h ^= h >> 16; h *= 2246822519u; h ^= h >> 13;
            return min + (int)(h % (uint)(max - min + 1));
        }

        /// <summary>Rewind every active emitter's burst window. The burst counter is sim-step-based, not
        /// playhead-based (the fluid is forward-integrated), so it must be re-zeroed at each sim restart.</summary>
        public static void ResetAllBursts()
        {
            BrazeRuntimeRegistry.PruneNulls(Active);
            for (int i = 0; i < Active.Count; i++)
                Active[i].ResetBurst();
        }

        void OnValidate()
        {
            particleMotionScale = Mathf.Max(0f, particleMotionScale);
            sourceVelocityBlend = Mathf.Max(0f, sourceVelocityBlend);
            sourceTurbulence = Mathf.Max(0f, sourceTurbulence);
            fragmentEmissionPercent = Mathf.Clamp(fragmentEmissionPercent, 0f, 100f);
            startFrame = Mathf.Max(0, startFrame);
            loopCount = Mathf.Max(0, loopCount);
            burstFramesMin = Mathf.Max(1, burstFramesMin);
            burstFramesMax = Mathf.Max(burstFramesMin, burstFramesMax);   // also resolves the -1 pre-range sentinel
            loopDelayMin = Mathf.Max(0, loopDelayMin);
            loopDelayMax = Mathf.Max(loopDelayMin, loopDelayMax);
            RollCycle();   // re-roll the in-flight cycle so range edits apply without waiting for a sim reset
            meshParticleSpawnMaxPoints = Mathf.Clamp(meshParticleSpawnMaxPoints, 8, 4096);
            PruneParticleEmitterList();
            cachedMeshId = int.MinValue;   // force a re-sample if the mesh/spacing changed in the inspector
        }

        // ── Mesh-shape emission ──────────────────────────────────────────────────────────────────────────────
        // A Mesh emitter has no analytic SDF, so it area-samples its source mesh surface into cached LOCAL points.
        // The solver consumes those GPU buffers directly and performs transforms, velocity and mask gating in compute.
        public void OnBrazeAnimated() => OnValidate();

        // Keep the Timeline picker aligned with the custom Inspector. Resource/topology controls such as Shape,
        // mesh sampling, connected-particle wiring and burst bookkeeping are intentionally not Timeline channels.
        public bool IsAnimatableMemberRelevant(string member)
        {
            string root = member;
            int channel = root.IndexOf('.');
            if (channel >= 0) root = root.Substring(0, channel);
            switch (root)
            {
                case "radius":
                    return shape != Shape.Box && shape != Shape.Mesh;
                case "height":
                    return shape == Shape.Cylinder || shape == Shape.Cone || shape == Shape.Capsule;
                case "tubeRadius":
                    return shape == Shape.Torus;
                case "motionRadial":
                case "gasPressureBurst":
                case "gasSourceSpread":
                case "gasSourceSharpness":
                    return shape != Shape.Mesh;
                default:
                    return true;
            }
        }

        readonly List<Vector3> meshLocalPts = new();   // REST-pose sample positions (mask gate + static spawn)
        readonly List<Vector3> meshLocalNrm = new();
        int cachedMeshId = int.MinValue;
        int cachedMaxPoints = -1;
        int meshSampleVersion;
        float cachedSpacing = -1f;
        const int MeshMaxPoints = 20000;

        // ── GPU-native mesh fluid emission: skinned sources splat BrazeDeformSource's live world verts, while static
        // sources splat cached mesh-local surface samples transformed in MeshSplat. Surface Mask gating also happens
        // in MeshSplat from rest positions. Unsupported/non-readable/warmup/fragment sources do not emit instead of
        // falling back to CPU point work. Buffers are owned here; the solver only reads them. ─────────────────────
        GraphicsBuffer _meshFluidPrevPos;
        GraphicsBuffer _meshMaskRestPos;
        GraphicsBuffer _meshGpuLocalPts, _meshGpuLocalNrm;
        GraphicsBuffer _meshMaskPassIdx, _meshMaskPassCount;
        int _meshFluidPrevCount = -1, _meshFluidPrevKey = int.MinValue;
        int _meshMaskRestMeshId = int.MinValue, _meshMaskRestCount = -1;
        int _meshGpuSampleVersion = -1, _meshGpuSampleCount = -1;
        int _meshMaskPassBufferCount = -1;
        BrazeSurfaceMask _meshMaskPassGate;
        int _meshMaskPassFieldVersion, _meshMaskPassSpaceVersion, _meshMaskPassSampleVersion;
        int _meshMaskPassSourceId = int.MinValue, _meshMaskPassCountCached = -1;
        bool _meshFluidHasPrev;
        int _gpuFluidFrame = -1;
        float _gpuFluidPrevRefreshTime = -1f, _gpuFluidRefreshDt = 1f / 60f;   // real interval between pose refreshes (velocity divisor)
        readonly List<Vector3> _meshMaskRestVerts = new();

        // ── Skinned source (GPU-deformed verts, no CPU readback) ────────────────────────────────────────────────
        BrazeDeformSource _deform;
        int _deformSrcId = int.MinValue;

        // If the source mesh's object is a Voronoi fracture, we hand emission off to its FLYING fragments once it
        // shatters (the static surface points would otherwise leave fire floating where the object used to be).
        BrazeVoronoiFracture sourceFracture;
        int fractureSourceId = int.MinValue;

        // Resolve (and cache) a Voronoi fracture on the source mesh's GameObject, once per source change (so a plain
        // mesh costs no per-frame GetComponent). A fracture added after assignment is picked up on the next reassign.
        void ResolveSourceFracture()
        {
            int id = sourceMesh != null ? sourceMesh.BrazeInstanceId() : 0;
            if (id == fractureSourceId) return;
            fractureSourceId = id;
            sourceFracture = sourceMesh != null ? sourceMesh.GetComponent<BrazeVoronoiFracture>() : null;
        }

        // The mask gating this emitter, or null. Explicit reference wins; otherwise auto-find one on the source mesh's
        // object (the mask typically lives on the very surface we emit from). Cached by source id like the fracture.
        // Source resolution: a skinned renderer (deforming) takes priority over the static MeshFilter.
        bool IsSkinnedSource => sourceSkinnedMesh != null;
        Component SourceComp => sourceSkinnedMesh != null ? sourceSkinnedMesh : (Component)sourceMesh;
        Mesh SourceSharedMesh => sourceSkinnedMesh != null ? sourceSkinnedMesh.sharedMesh : (sourceMesh != null ? sourceMesh.sharedMesh : null);
        Transform SourceTransform => sourceSkinnedMesh != null ? sourceSkinnedMesh.transform : (sourceMesh != null ? sourceMesh.transform : null);

        BrazeSurfaceMask resolvedAutoMask;
        int maskSourceId = int.MinValue;
        BrazeSurfaceMask ActiveMask()
        {
            if (!useMask) return null;
            if (mask != null) return mask;
            var src = SourceComp;
            int id = src != null ? src.BrazeInstanceId() : 0;
            if (id != maskSourceId) { maskSourceId = id; resolvedAutoMask = src != null ? src.GetComponent<BrazeSurfaceMask>() : null; }
            return resolvedAutoMask;
        }

        void EnsureMeshSampled(BrazeSurfaceMask gate = null)
        {
            Mesh m = SourceSharedMesh;
            int id = m != null ? m.BrazeInstanceId() : 0;
            float spacing = gate != null ? gate.SurfacePointSpacing : Mathf.Max(0.01f, meshSpacing);
            int maxPoints = gate != null ? gate.SurfacePointCap : MeshMaxPoints;
            if (id == cachedMeshId && cachedSpacing == spacing && cachedMaxPoints == maxPoints) return;   // already up to date
            cachedMeshId = id;
            cachedSpacing = spacing;
            cachedMaxPoints = maxPoints;
            meshLocalPts.Clear(); meshLocalNrm.Clear();
            meshSampleVersion++;
            if (m == null || !m.isReadable || SourceTransform == null) return;

            SampleMeshSurface(m.vertices, m.normals, m.triangles,
                              SourceTransform.lossyScale, spacing, maxPoints);
        }

        // Area-weighted surface sampling: each triangle gets ~ WORLD-area / spacing² points, so Sample Spacing is a
        // world-space density (a 1×1 mesh scaled to 20×20 gets full coverage, not 60 stray points). Deterministic RNG
        // so the set is stable frame-to-frame. Capped by the caller. Positions/normals stay in LOCAL space
        // (transformed per frame).
        void SampleMeshSurface(Vector3[] verts, Vector3[] norms, int[] tris, Vector3 scale, float spacing, int maxPoints)
        {
            bool hasN = norms != null && norms.Length == verts.Length;
            float cell = spacing * spacing;
            var rng = new System.Random(0x6B7A21);
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int ia = tris[t], ib = tris[t + 1], ic = tris[t + 2];
                Vector3 a = verts[ia], b = verts[ib], c = verts[ic];
                Vector3 ab = b - a, ac = c - a;
                Vector3 cross = Vector3.Cross(ab, ac);
                float localArea = cross.magnitude * 0.5f;
                if (localArea <= 0f) continue;
                Vector3 fn = hasN
                    ? (norms[ia] + norms[ib] + norms[ic]).normalized
                    : (cross.sqrMagnitude > 1e-12f ? cross.normalized : Vector3.up);

                // Count from WORLD area so density follows world spacing under any transform scale.
                float worldArea = Vector3.Cross(Vector3.Scale(ab, scale), Vector3.Scale(ac, scale)).magnitude * 0.5f;
                float expected = worldArea / Mathf.Max(cell, 1e-8f);
                int n = Mathf.FloorToInt(expected);
                if (rng.NextDouble() < expected - n) n++;
                for (int s = 0; s < n; s++)
                {
                    float u = (float)rng.NextDouble(), v = (float)rng.NextDouble();
                    if (u + v > 1f) { u = 1f - u; v = 1f - v; }
                    meshLocalPts.Add(a + ab * u + ac * v);   // REST position (mask gate + static spawn)
                    meshLocalNrm.Add(fn);
                    if (meshLocalPts.Count >= maxPoints) return;
                }
            }
        }

        /// <summary>Conservative input count used only for container VRAM preflight. Intact and fractured paths are
        /// mutually exclusive at runtime, so the larger one is the required sparse-hash capacity.</summary>
        internal int EstimatedGpuFluidPointCapacity
        {
            get
            {
                int intact = 0;
                if (sourceSkinnedMesh != null && sourceSkinnedMesh.sharedMesh != null)
                    intact = sourceSkinnedMesh.sharedMesh.vertexCount;
                else if (sourceMesh != null && sourceMesh.sharedMesh != null)
                {
                    BrazeSurfaceMask gate = useMask
                        ? (mask != null ? mask : sourceMesh.GetComponent<BrazeSurfaceMask>())
                        : null;
                    intact = gate != null ? gate.SurfacePointCap : MeshMaxPoints;
                }

                int fragments = 0;
                if (emitFromFragments && sourceMesh != null)
                {
                    var fracture = sourceMesh.GetComponent<BrazeVoronoiFracture>();
                    if (fracture != null)
                        fragments = FragmentEmissionCount(fracture.SeedCount);
                }
                return Mathf.Max(intact, fragments);
            }
        }

        internal int FragmentEmissionCount(int totalFragments)
        {
            int total = Mathf.Max(0, totalFragments);
            if (total == 0 || fragmentEmissionPercent <= 0f) return 0;
            return Mathf.Clamp(Mathf.CeilToInt(total * Mathf.Clamp01(fragmentEmissionPercent * 0.01f)), 1, total);
        }

        /// <summary>GPU-native fractured-mesh fluid emission. The fracture owns all buffers; the solver reads
        /// centroid/state, velocity and life directly and rejects anchored/dissolved pieces in compute.</summary>
        public bool TryGetGpuFragmentFluidEmission(out BrazeSolver.FragmentSourceGpu src)
        {
            src = default;
            if (shape != Shape.Mesh || EmissionScale <= 0f || IsSkinnedSource || !emitFromFragments) return false;
            ResolveSourceFracture();
            if (sourceFracture == null
                || !sourceFracture.TryGetFluidGpuBuffers(out var posState, out var velocity, out var life, out int count))
                return false;
            int emitCount = FragmentEmissionCount(count);
            if (emitCount <= 0) return false;

            src = new BrazeSolver.FragmentSourceGpu
            {
                posState = posState,
                velocity = velocity,
                life = life,
                count = count,
                emitCount = emitCount,
                channels = new Vector3(density, temperature, fuel) * EmissionScale,
                buoyancy = motionBuoyancy,
                velocityInherit = fragmentVelocity,
            };
            return true;
        }

        /// <summary>GPU-native Mesh emitter fluid emission. Skinned sources use the live GPU-deformed vertex buffer;
        /// static sources use cached mesh-local surface samples; optional Surface Masks are evaluated in compute.
        /// Returns false for unsupported/warmup/fractured cases; the caller skips this intact-surface path.</summary>
        public bool TryGetGpuFluidEmission(out BrazeSolver.MeshSourceGpu src)
        {
            src = default;
            if (shape != Shape.Mesh || EmissionScale <= 0f) return false;
            var gate = ActiveMask();
            if (!IsSkinnedSource) return TryGetStaticGpuFluidEmission(out src, gate);

            EnsureDeform();
            if (_deform == null) return false;
            // Refresh the live pose once per frame (multiple sim steps reuse it; intra-frame velocity is zero). The
            // pose advances per FRAME, so record the real interval between refreshes — that's the velocity divisor,
            // not the sim dt (which may differ from the frame rate).
            if (_gpuFluidFrame != Time.frameCount)
            {
                _gpuFluidFrame = Time.frameCount;
                _deform.Refresh();
                float now = Time.time;
                _gpuFluidRefreshDt = _gpuFluidPrevRefreshTime >= 0f
                    ? Mathf.Max(now - _gpuFluidPrevRefreshTime, 1e-4f) : Mathf.Max(Time.deltaTime, 1e-4f);
                _gpuFluidPrevRefreshTime = now;
            }
            if (!_deform.UsingGpuPath || !_deform.ExtractedOnce || _deform.Positions == null || _deform.VertexCount <= 0)
                return false;   // calibration warmup / unsupported path → skip mesh emission this frame

            int vc = _deform.VertexCount;
            int prevKey = sourceSkinnedMesh.BrazeInstanceId();
            if (_meshFluidPrevPos == null || _meshFluidPrevCount != vc || _meshFluidPrevKey != prevKey)
            {
                _meshFluidPrevPos?.Release();
                _meshFluidPrevPos = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vc, sizeof(float) * 3);
                _meshFluidPrevCount = vc;
                _meshFluidPrevKey = prevKey;
                _meshFluidHasPrev = false;   // first splat after (re)alloc emits zero velocity (no spike)
            }
            GraphicsBuffer restPos = null;
            Matrix4x4 restLocalToField = Matrix4x4.identity;
            GraphicsBuffer passIdx = null, passCount = null;
            bool passOn = false, passDirty = false;
            if (gate != null)
            {
                restPos = EnsureMaskRestBuffer(sourceSkinnedMesh.sharedMesh, vc);
                if (restPos == null) return false;
                restLocalToField = gate.SourceLocalToFieldMatrix(sourceSkinnedMesh.transform);
                passOn = ConfigureMaskPass(gate, sourceSkinnedMesh.transform, vc,
                    sourceSkinnedMesh.sharedMesh.BrazeInstanceId() ^ (vc * 397),
                    out passIdx, out passCount, out passDirty);
            }

            src = new BrazeSolver.MeshSourceGpu
            {
                positions = _deform.Positions,
                normals = _deform.Normals,
                prevPos = _meshFluidPrevPos,
                vertCount = vc,
                hasPrev = _meshFluidHasPrev,
                channels = new Vector3(density, temperature, fuel) * EmissionScale,
                buoyancy = motionBuoyancy,
                normalSpeed = meshNormalSpeed,
                invDt = 1f / _gpuFluidRefreshDt,
                mask = gate,
                maskRestLocal = restPos,
                maskRestLocalToField = restLocalToField,
                maskPassIdx = passIdx,
                maskPassCount = passCount,
                maskUsePassSet = passOn,
                maskPassDirty = passDirty,
            };
            _meshFluidHasPrev = true;
            return true;
        }

        bool TryGetStaticGpuFluidEmission(out BrazeSolver.MeshSourceGpu src, BrazeSurfaceMask gate)
        {
            src = default;
            if (sourceMesh == null || sourceMesh.sharedMesh == null || sourceMesh.transform == null) return false;
            ResolveSourceFracture();
            if (emitFromFragments && sourceFracture != null && sourceFracture.HasFluidFracture) return false;   // fragment emission is not a Surface Mask mesh source

            EnsureMeshSampled(gate);
            int count = meshLocalPts.Count;
            if (count <= 0 || !EnsureGpuSampleBuffers(count)) return false;

            int prevKey = sourceMesh.BrazeInstanceId() ^ (meshSampleVersion * 397);
            if (_meshFluidPrevPos == null || _meshFluidPrevCount != count || _meshFluidPrevKey != prevKey)
            {
                _meshFluidPrevPos?.Release();
                _meshFluidPrevPos = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 3);
                _meshFluidPrevCount = count;
                _meshFluidPrevKey = prevKey;
                _meshFluidHasPrev = false;
            }
            if (_gpuFluidFrame != Time.frameCount)
            {
                _gpuFluidFrame = Time.frameCount;
                float now = Time.time;
                _gpuFluidRefreshDt = _gpuFluidPrevRefreshTime >= 0f
                    ? Mathf.Max(now - _gpuFluidPrevRefreshTime, 1e-4f) : Mathf.Max(Time.deltaTime, 1e-4f);
                _gpuFluidPrevRefreshTime = now;
            }

            Matrix4x4 l2w = sourceMesh.transform.localToWorldMatrix;
            GraphicsBuffer passIdx = null, passCount = null;
            bool passOn = false, passDirty = false;
            if (gate != null)
                passOn = ConfigureMaskPass(gate, sourceMesh.transform, count, meshSampleVersion,
                    out passIdx, out passCount, out passDirty);
            src = new BrazeSolver.MeshSourceGpu
            {
                positions = _meshGpuLocalPts,
                normals = _meshGpuLocalNrm,
                prevPos = _meshFluidPrevPos,
                vertCount = count,
                hasPrev = _meshFluidHasPrev,
                channels = new Vector3(density, temperature, fuel) * EmissionScale,
                buoyancy = motionBuoyancy,
                normalSpeed = meshNormalSpeed,
                invDt = 1f / _gpuFluidRefreshDt,
                mask = gate,
                maskRestLocal = gate != null ? _meshGpuLocalPts : null,
                maskRestLocalToField = gate != null ? gate.SourceLocalToFieldMatrix(sourceMesh.transform) : Matrix4x4.identity,
                maskPassIdx = passIdx,
                maskPassCount = passCount,
                maskUsePassSet = passOn,
                maskPassDirty = passDirty,
                positionsAreLocal = true,
                localToWorld = l2w,
                normalLocalToWorld = l2w.inverse.transpose,
            };
            _meshFluidHasPrev = true;
            return true;
        }

        bool EnsureGpuSampleBuffers(int count)
        {
            if (_meshGpuLocalPts != null && _meshGpuLocalNrm != null
                && _meshGpuSampleVersion == meshSampleVersion && _meshGpuSampleCount == count)
                return true;
            if (meshLocalPts.Count < count || meshLocalNrm.Count < count) return false;
            _meshGpuLocalPts?.Release();
            _meshGpuLocalNrm?.Release();
            _meshGpuLocalPts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 3);
            _meshGpuLocalNrm = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 3);
            _meshGpuLocalPts.SetData(meshLocalPts, 0, 0, count);
            _meshGpuLocalNrm.SetData(meshLocalNrm, 0, 0, count);
            _meshGpuSampleVersion = meshSampleVersion;
            _meshGpuSampleCount = count;
            return true;
        }

        GraphicsBuffer EnsureMaskRestBuffer(Mesh mesh, int count)
        {
            if (mesh == null || !mesh.isReadable || count <= 0) return null;
            int id = mesh.BrazeInstanceId();
            if (_meshMaskRestPos != null && _meshMaskRestMeshId == id && _meshMaskRestCount == count)
                return _meshMaskRestPos;

            mesh.GetVertices(_meshMaskRestVerts);
            if (_meshMaskRestVerts.Count < count) return null;
            _meshMaskRestPos?.Release();
            _meshMaskRestPos = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 3);
            _meshMaskRestPos.SetData(_meshMaskRestVerts, 0, 0, count);
            _meshMaskRestMeshId = id;
            _meshMaskRestCount = count;
            return _meshMaskRestPos;
        }

        bool ConfigureMaskPass(BrazeSurfaceMask gate, Transform sourceTransform, int count, int sampleVersion,
                               out GraphicsBuffer passIdx, out GraphicsBuffer passCount, out bool dirty)
        {
            passIdx = null;
            passCount = null;
            dirty = false;
            if (gate == null || sourceTransform == null || count <= 0) return false;

            if (_meshMaskPassIdx == null || _meshMaskPassBufferCount < count)
            {
                _meshMaskPassIdx?.Release();
                int capacity = Mathf.NextPowerOfTwo(Mathf.Max(1, count));
                _meshMaskPassIdx = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    capacity, sizeof(uint));
                _meshMaskPassBufferCount = capacity;
                _meshMaskPassGate = null;
            }
            if (_meshMaskPassCount == null)
            {
                _meshMaskPassCount = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
                _meshMaskPassGate = null;
            }

            int fieldVersion = gate.FieldVersion;
            int spaceVersion = gate.SourceLocalToFieldVersion(sourceTransform);
            int sourceId = sourceTransform.BrazeInstanceId();
            dirty = gate != _meshMaskPassGate
                || fieldVersion != _meshMaskPassFieldVersion
                || spaceVersion != _meshMaskPassSpaceVersion
                || sampleVersion != _meshMaskPassSampleVersion
                || sourceId != _meshMaskPassSourceId
                || count != _meshMaskPassCountCached;

            if (dirty)
            {
                _meshMaskPassGate = gate;
                _meshMaskPassFieldVersion = fieldVersion;
                _meshMaskPassSpaceVersion = spaceVersion;
                _meshMaskPassSampleVersion = sampleVersion;
                _meshMaskPassSourceId = sourceId;
                _meshMaskPassCountCached = count;
            }

            passIdx = _meshMaskPassIdx;
            passCount = _meshMaskPassCount;
            return true;
        }

        public bool TryGetParticleSpawnMeshSource(out Renderer renderer, out BrazeSurfaceMask gate,
                                                  out int maxPoints, out float normalSpeed)
        {
            renderer = null;
            gate = null;
            maxPoints = Mathf.Max(1, meshParticleSpawnMaxPoints);
            normalSpeed = meshNormalSpeed;

            if (shape != Shape.Mesh || EmissionScale <= 0f || (sourceMesh == null && sourceSkinnedMesh == null))
                return false;

            if (!IsSkinnedSource)
            {
                ResolveSourceFracture();
                if (emitFromFragments && sourceFracture != null && sourceFracture.HasFluidFracture)
                    return false;   // fragments are not fed through mesh Surface Mask emission.
                if (sourceMesh == null || sourceMesh.sharedMesh == null) return false;
                renderer = sourceMesh.GetComponent<Renderer>();
            }
            else
            {
                if (sourceSkinnedMesh.sharedMesh == null) return false;
                renderer = sourceSkinnedMesh;
            }

            gate = ActiveMask();
            if (gate != null)
            {
                Mesh mesh = SourceSharedMesh;
                Transform sourceTransform = SourceTransform;
                maxPoints = mesh != null && sourceTransform != null
                    ? Mathf.Min(maxPoints, gate.ResolveSurfaceSampleCount(mesh, sourceTransform))
                    : Mathf.Min(maxPoints, gate.SurfacePointCap);
            }
            return renderer != null;
        }

        void EnsureDeform()
        {
            int id = sourceSkinnedMesh != null ? sourceSkinnedMesh.BrazeInstanceId() : 0;
            if (id == _deformSrcId && _deform != null) return;
            _deform?.Dispose(); _deform = null;
            _deformSrcId = id;
            if (sourceSkinnedMesh != null) _deform = new BrazeDeformSource(sourceSkinnedMesh);
        }

        public bool DrivesParticleEmitter(BrazeParticleSystem system)
        {
            if (system == null || connectedParticleEmitters == null) return false;
            for (int i = 0; i < connectedParticleEmitters.Count; i++)
                if (connectedParticleEmitters[i] == system) return true;
            return false;
        }

        /// <summary>The master driving <paramref name="system"/>. When the system is connected to SEVERAL pyro
        /// emitters, prefer one that is emitting RIGHT NOW — the slave's spawn gate reads this master's window,
        /// and locking onto a silent first-in-registry emitter would mute the slave while another master fires.</summary>
        public static bool TryGetParticleSource(BrazeParticleSystem system, out BrazePyroEmitter emitter)
        {
            emitter = null;
            if (system == null) return false;

            BrazeRuntimeRegistry.PruneNulls(Active);
            for (int i = 0; i < Active.Count; i++)
            {
                var e = Active[i];
                if (e == null || !e.isActiveAndEnabled) continue;
                if (!e.DrivesParticleEmitter(system)) continue;
                if (e.EmissionScale > 0f) { emitter = e; return true; }   // an emitting master wins outright
                if (emitter == null) emitter = e;                          // else keep the first connected
            }

            return emitter != null;
        }

        public static bool TryGetParticleMotionSource(BrazeParticleSystem system, out BrazePyroEmitter emitter)
        {
            if (!TryGetParticleSource(system, out emitter)) return false;
            return emitter != null && emitter.particleMotionScale > 0f;
        }

        void PruneParticleEmitterList()
        {
            connectedParticleEmitters ??= new List<BrazeParticleSystem>();
            for (int i = connectedParticleEmitters.Count - 1; i >= 0; i--)
            {
                var p = connectedParticleEmitters[i];
                if (p == null)
                {
                    connectedParticleEmitters.RemoveAt(i);
                    continue;
                }

                for (int j = 0; j < i; j++)
                {
                    if (connectedParticleEmitters[j] != p) continue;
                    connectedParticleEmitters.RemoveAt(i);
                    break;
                }
            }
        }

        /// <summary>Advance the burst window by one frame. Called by the container each frame.</summary>
        public void Tick()
        {
            if (!burstMode) { emitting = true; return; }

            int local = frame - startFrame;
            frame++;

            if (local < 0) { emitting = false; return; }          // before start
            if (!loop) { emitting = local < cycleBurst; return; } // one-shot (length rolled at reset)

            // Cycles vary in length (per-loop burst/delay rolls), so walk cycle boundaries instead of a modulo.
            while (local - cycleStart >= cycleBurst + cycleDelay)   // cycleBurst ≥ 1, so cycles always advance
            {
                cycleStart += cycleBurst + cycleDelay;
                loopsDone++;
                RollCycle();
            }
            if (loopCount > 0 && loopsDone >= loopCount) { emitting = false; return; } // finite loops spent
            emitting = local - cycleStart < cycleBurst;
        }

        /// <summary>Local rotation that reorients the Y-up shape footprint onto <see cref="shapeAxis"/>. Composed
        /// AFTER the emitter's Transform rotation when packing the GPU emitter (and into the gizmo matrix), so the
        /// SDF and halfExtent stay authored in their natural Y-up frame and this just turns them. Identity for Y.</summary>
        public Quaternion AxisRotation() => shapeAxis switch
        {
            Axis.X => Quaternion.AngleAxis(-90f, Vector3.forward),   // +Y → +X
            Axis.Z => Quaternion.AngleAxis(90f, Vector3.right),      // +Y → +Z
            _ => Quaternion.identity,                                // Y (default)
        };

        static readonly Color GizmoTint = new Color(1f, 0.5f, 0.1f);   // warm orange (emission)
        static readonly Color ForwardTint = new Color(0.25f, 0.7f, 1f); // cool blue (Push / forward direction)

        // Semi-transparent solid + wire, so the source shape/orientation reads clearly. Show Gizmo
        // gates ALL drawing — off means no gizmo even when selected.
        void OnDrawGizmos()
        {
            if (!showGizmo) return;

            // Mesh shape: outline the SOURCE mesh at its own transform (that's what we sample + emit from).
            if (shape == Shape.Mesh)
            {
                if (sourceMesh != null && sourceMesh.sharedMesh != null)
                {
                    Gizmos.color = new Color(GizmoTint.r, GizmoTint.g, GizmoTint.b, 0.7f);
                    Gizmos.DrawWireMesh(sourceMesh.sharedMesh, sourceMesh.transform.position,
                                        sourceMesh.transform.rotation, sourceMesh.transform.lossyScale);
                }
                DrawForwardArrow();
                return;
            }

            // All shapes follow the emitter's rotation now (emission does too — the GPU footprint rotates into the
            // emitter's local frame). Box also takes Transform SCALE (its size = scale); the rest use authored dims.
            Gizmos.matrix = shape == Shape.Box
                ? transform.localToWorldMatrix
                : Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one) * Matrix4x4.Rotate(AxisRotation());
            Gizmos.color = new Color(GizmoTint.r, GizmoTint.g, GizmoTint.b, 0.15f);
            switch (shape)
            {
                case Shape.Sphere:
                    Gizmos.DrawSphere(Vector3.zero, radius);
                    break;
                case Shape.Box:
                    Gizmos.DrawCube(Vector3.zero, Vector3.one);   // box = unit cube scaled by Transform
                    break;
            }

            Gizmos.color = new Color(GizmoTint.r, GizmoTint.g, GizmoTint.b, 0.7f);
            switch (shape)
            {
                case Shape.Sphere:   Gizmos.DrawWireSphere(Vector3.zero, radius); break;
                case Shape.Box:      Gizmos.DrawWireCube(Vector3.zero, Vector3.one); break;
                case Shape.Cylinder: DrawCylinder(radius, height); break;
                case Shape.Cone:     DrawCone(radius, height); break;
                case Shape.Capsule:  DrawCapsule(radius, height); break;
                case Shape.Torus:    DrawTorus(radius, tubeRadius); break;
            }
            Gizmos.matrix = Matrix4x4.identity;

            DrawForwardArrow();   // Z+ arrow = the direction "Push" fires (aim by rotating the emitter)
        }

        // When selected: dot the actual surface sample points so Sample Spacing is visible (and you can see whether
        // it's finer than the container's voxels — finer no longer over-emits, it just adds draw/dedup cost). Drawn
        // capped/strided so a dense mesh stays responsive. Count label shows the real point total.
        void OnDrawGizmosSelected()
        {
            if (!showGizmo || shape != Shape.Mesh || sourceMesh == null) return;
            var gate = ActiveMask();
            EnsureMeshSampled(gate);
            int total = meshLocalPts.Count;
            if (total == 0) return;

            Matrix4x4 mat = sourceMesh.transform.localToWorldMatrix;
            Gizmos.matrix = Matrix4x4.identity;
            int stride = Mathf.Max(1, Mathf.CeilToInt(total / 3000f));
            float spacing = gate != null ? gate.SurfacePointSpacing : Mathf.Max(0.01f, meshSpacing);
            float dot = Mathf.Max(spacing * 0.18f, 1e-3f);
            Gizmos.color = new Color(1f, 0.5f, 0.1f, 0.95f);
            for (int i = 0; i < total; i += stride)
                Gizmos.DrawCube(mat.MultiplyPoint3x4(meshLocalPts[i]), Vector3.one * dot);
#if UNITY_EDITOR
            UnityEditor.Handles.color = new Color(1f, 0.72f, 0.35f, 1f);
            UnityEditor.Handles.Label(sourceMesh.transform.position,
                $"{total} sample points  (spacing {spacing:0.###})");
#endif
        }

        // A small arrow along the emitter's local Z+ (Transform forward) — the axis the Push force fires along,
        // so it's obvious which way the directional push will go. Drawn in world space (after the matrix reset).
        void DrawForwardArrow()
        {
            Vector3 origin = transform.position;
            Vector3 dir = transform.forward;
            // Screen-constant length (like Unity's move-handle axes) so the Push indicator reads the SAME
            // regardless of the emitter's size or the camera zoom — it no longer scales with radius/height/scale.
#if UNITY_EDITOR
            float len = UnityEditor.HandleUtility.GetHandleSize(origin) * 1.5f;
#else
            float len = 0.9f;
#endif
            Vector3 tip = origin + dir * len;
            Gizmos.color = ForwardTint;
            Gizmos.DrawLine(origin, tip);
            // 4-prong arrowhead
            Vector3 back = -dir * (len * 0.28f);
            float head = len * 0.14f;
            Gizmos.DrawLine(tip, tip + back + transform.up * head);
            Gizmos.DrawLine(tip, tip + back - transform.up * head);
            Gizmos.DrawLine(tip, tip + back + transform.right * head);
            Gizmos.DrawLine(tip, tip + back - transform.right * head);
        }

        static void DrawCylinder(float r, float h)
        {
            h = Mathf.Max(h, 0f);
            float y0 = -h * 0.5f, y1 = h * 0.5f;
            CircleXZ(new Vector3(0f, y0, 0f), r);
            CircleXZ(new Vector3(0f, y1, 0f), r);
            for (int i = 0; i < 4; i++)
            {
                float a = i / 4f * Mathf.PI * 2f;
                Vector3 d = new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                Gizmos.DrawLine(d + Vector3.up * y0, d + Vector3.up * y1);
            }
        }

        static void DrawCone(float r, float h)
        {
            h = Mathf.Max(h, 0f);
            float y0 = -h * 0.5f, y1 = h * 0.5f;
            CircleXZ(new Vector3(0f, y0, 0f), r);
            Vector3 tip = new Vector3(0f, y1, 0f);
            for (int i = 0; i < 8; i++)
            {
                float a = i / 8f * Mathf.PI * 2f;
                Vector3 d = new Vector3(Mathf.Cos(a) * r, y0, Mathf.Sin(a) * r);
                Gizmos.DrawLine(d, tip);
            }
        }

        static void DrawCapsule(float r, float h)
        {
            float halfSegment = Mathf.Max(0f, h * 0.5f - r);
            CircleXZ(Vector3.up * halfSegment, r);
            CircleXZ(Vector3.down * halfSegment, r);
            Circle(Vector3.zero, Vector3.right, Vector3.up, r, 40);
            Circle(Vector3.zero, Vector3.forward, Vector3.up, r, 40);
            for (int i = 0; i < 4; i++)
            {
                float a = i / 4f * Mathf.PI * 2f;
                Vector3 d = new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                Gizmos.DrawLine(d + Vector3.down * halfSegment, d + Vector3.up * halfSegment);
            }
        }

        static void DrawTorus(float major, float tube)
        {
            CircleXZ(Vector3.zero, major + tube);
            CircleXZ(Vector3.zero, Mathf.Max(0f, major - tube));
            for (int i = 0; i < 8; i++)
            {
                float a = i / 8f * Mathf.PI * 2f;
                Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
                Circle(dir * major, dir, Vector3.up, tube, 16);
            }
        }

        static void CircleXZ(Vector3 c, float r) => Circle(c, Vector3.right, Vector3.forward, r, 40);

        static void Circle(Vector3 c, Vector3 u, Vector3 v, float r, int seg)
        {
            r = Mathf.Max(0f, r);
            Vector3 prev = c + u * r;
            for (int i = 1; i <= seg; i++)
            {
                float a = i / (float)seg * Mathf.PI * 2f;
                Vector3 cur = c + (u * Mathf.Cos(a) + v * Mathf.Sin(a)) * r;
                Gizmos.DrawLine(prev, cur);
                prev = cur;
            }
        }
    }
}
