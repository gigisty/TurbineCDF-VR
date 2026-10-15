using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BrazeFX
{
    /// <summary>
    /// Standalone spline-to-surface wrapper. It owns no spline data; it reshapes a referenced
    /// <see cref="BrazeSpline"/> so the path rides on a referenced mesh surface.
    ///
    /// Fully GPU-bound. The wrap is an axis-based SPINNING RAYCASTER: picture a camera climbing the chosen axis
    /// (X/Y/Z) while orbiting the mesh, snapping a photo each step. Each "photo" is a ray fired from outside the
    /// mesh straight inward toward the axis; where it first hits the outer shell we drop a control point. One or
    /// more 360° orbits (set by Turns) from one end of the mesh to the other trace a helix on the surface. Those
    /// points feed the spline. All ray casts run on the GPU over the renderer's already-deformed vertex buffer
    /// (<see cref="BrazeDeformSource"/>) — no CPU BakeMesh, no CPU triangle work; the CPU only computes the cheap
    /// orbit schedule (angles + heights).
    ///
    ///   • Seed / wrap pass (GPU, occasional): the spinning raycast above. Runs only when authoring inputs
    ///     change — never per skinned-pose change.
    ///   • Track pass (GPU, per-frame, skinned only): re-snaps the already-placed points to the live deformed
    ///     surface via the shared hashed grid (<see cref="BrazeMeshGrid"/>) with a small query range, so the
    ///     wrap follows skinned animation cheaply. Results return via async readback.
    /// </summary>
    [AddComponentMenu("BrazeFX/Spline/Spline Surface")]
    [ExecuteAlways]
    public sealed class BrazeSplineSurface : MonoBehaviour
    {
        public enum SurfaceMode { GenerateWrap = 0, ProjectExisting = 1 }
        public enum WrapAxis { X = 0, Y = 1, Z = 2 }

        [Tooltip("Spline whose control points are written by this component. Empty = use a BrazeSpline on this object.")]
        public BrazeSpline spline;
        [Tooltip("Mesh or skinned mesh to wrap onto. Empty = use a Renderer on this object.")]
        public Renderer surfaceMesh;

        [Tooltip("Generate Wrap orbits a raycaster around the mesh to trace a helix. Project Existing keeps the current point count and casts each point onto the surface.")]
        public SurfaceMode mode = SurfaceMode.GenerateWrap;
        [Tooltip("Local axis of the target mesh the raycaster climbs (the orbit axis / wrap height).")]
        public WrapAxis axis = WrapAxis.Y;

        [Min(1), Tooltip("Number of 360° orbits from one end of the mesh to the other.")]
        public int turns = 8;
        [Range(6, 96), Tooltip("Raycast shots (control points) per orbit.")]
        public int pointsPerTurn = 16;
        [Min(0), Tooltip("Total raycast shots / control points. 0 = Turns x Points Per Turn. Values above 0 clamp to at least 8.")]
        public int stairCount = 0;
        [Range(0f, 0.45f), Tooltip("Fraction of the mesh height skipped at each end.")]
        public float heightPadding = 0.06f;
        [Min(0f), Tooltip("World-space distance to lift the spline above the mesh surface.")]
        public float surfaceOffset = 0.02f;
        [Range(0f, 0.5f), Tooltip("Breaks up the generated wrap so it feels hand-drawn instead of lathed.")]
        public float wobble = 0.08f;
        [Min(0.01f), Tooltip("Frequency of the generated wrap wobble.")]
        public float wobbleFrequency = 2.4f;
        [Range(0f, 1f), Tooltip("Stable per-point random variation in wrap angle and height.")]
        public float randomness = 0f;
        [Tooltip("Stable seed for generated randomness.")]
        public int randomSeed = 1;
        [Range(0f, 1f), Tooltip("Relaxes kinked hits, then re-casts the smoothed points back onto the surface.")]
        public float smoothness = 0.45f;
        [Tooltip("Angular phase of the generated wrap, in turns.")]
        public float phase;

        [Tooltip("In edit mode, automatically rebuild when inputs, transforms, or settings change.")]
        public bool liveUpdate = true;
        [Tooltip("Allow the GPU wrap pass to run automatically in Play Mode. Off = generated spline data is reused.")]
        public bool rebuildInPlayMode = false;
        [Tooltip("Track skinned mesh deformation in Play Mode. This projects on the GPU, then async-readbacks and writes CPU spline points.")]
        public bool trackSkinnedInPlayMode = false;
        [Tooltip("Draw the wrapped spline in white while this component is selected.")]
        public bool drawPreview = true;
        public Color previewColor = new Color(1f, 1f, 1f, 0.9f);

        [SerializeField, HideInInspector] string lastStatus;

        [NonSerialized] bool pendingRebuild = true;
        [NonSerialized] int lastSeedHash;
        [NonSerialized] int lastSplineVersion = int.MinValue;
#if UNITY_EDITOR
        [NonSerialized] double editorRebuildNotBefore;
        const double EditorRebuildDebounceSeconds = 0.25;
#endif

        readonly List<Vector3> generatedWorld = new List<Vector3>(256);

        // ── GPU state ──────────────────────────────────────────────────────────────────
        ComputeShader splineCS, gridCS;
        int kProject = -1, kCast = -1;
        BrazeDeformSource meshSource;
        Renderer meshSourceRenderer;
        BrazeMeshGrid meshGrid;   // per-frame skinned track pass
        BrazeMeshGrid projGrid;   // nearest-surface reprojection for on-surface smoothing (authoring)
        GraphicsBuffer inPtsBuf, dirBuf, outPtsBuf;
        Vector3[] uploadScratch, dirScratch;
        Vector4[] resultScratch;
        float trackRange;
        bool readbackInFlight;
        uint gpuGeneration;

        public BrazeSpline TargetSpline => spline != null ? spline : GetComponent<BrazeSpline>();
        public Renderer TargetSurface => surfaceMesh != null ? surfaceMesh : GetComponent<Renderer>();
        public string Status => lastStatus;

        void Reset()
        {
            spline = GetComponent<BrazeSpline>();
            surfaceMesh = GetComponent<Renderer>();
        }

        void OnEnable() => pendingRebuild = true;

        void OnDisable() => ReleaseGpu();

        void OnValidate()
        {
            turns = Mathf.Max(1, turns);
            pointsPerTurn = Mathf.Clamp(pointsPerTurn, 6, 96);
            stairCount = Mathf.Clamp(stairCount, 0, 4096);
            heightPadding = Mathf.Clamp(heightPadding, 0f, 0.45f);
            surfaceOffset = Mathf.Max(0f, surfaceOffset);
            wobble = Mathf.Clamp(wobble, 0f, 0.5f);
            wobbleFrequency = Mathf.Max(0.01f, wobbleFrequency);
            randomness = Mathf.Clamp01(randomness);
            smoothness = Mathf.Clamp01(smoothness);
            pendingRebuild = true;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                editorRebuildNotBefore = UnityEditor.EditorApplication.timeSinceStartup + EditorRebuildDebounceSeconds;
#endif
        }

        // Driven in LateUpdate so the renderer's animation/skinning for this frame has already evaluated
        // (BrazeDeformSource reads the post-deformation GPU vertex buffer).
        void LateUpdate()
        {
            if (!liveUpdate && !pendingRebuild) return;

            BrazeSpline s = TargetSpline;
            Renderer r = TargetSurface;
            if (s == null || r == null) return;

            bool skinned = r is SkinnedMeshRenderer;

            // Wrap pass: only when authoring inputs change (never per skinned-pose change — the GPU track owns that).
            int seedHash = BuildSeedHash(s, r, skinned);
            bool userEdited = mode == SurfaceMode.ProjectExisting && !skinned && s.version != lastSplineVersion;
            if (pendingRebuild || seedHash != lastSeedHash || userEdited)
            {
                if (Application.isPlaying && !rebuildInPlayMode)
                {
                    pendingRebuild = false;
                    lastSeedHash = seedHash;
                    lastSplineVersion = s.version;
                }
                else
                {
#if UNITY_EDITOR
                    if (!Application.isPlaying && pendingRebuild &&
                        UnityEditor.EditorApplication.timeSinceStartup < editorRebuildNotBefore)
                        return;
#endif
                    WrapNow();
                    lastSeedHash = BuildSeedHash(TargetSpline, TargetSurface, skinned);
                    if (TargetSpline != null) lastSplineVersion = TargetSpline.version;
                }
            }

            // Track pass: GPU query plus readback into CPU spline points. Keep runtime opt-in.
            if (skinned && liveUpdate && (!Application.isPlaying || trackSkinnedInPlayMode)) TrackFrame(s, r);
        }

        // ── Wrap pass (GPU spinning raycaster, occasional) ─────────────────────────────
        /// <summary>Full placement of the spline onto the (current) surface. Used by the inspector button and as
        /// the seed pass before GPU tracking. Everything runs on the GPU — the CPU only builds the orbit
        /// schedule and reads back the hit points.</summary>
        public bool WrapNow()
        {
            pendingRebuild = false;
            BrazeSpline s = TargetSpline;
            Renderer r = TargetSurface;
            if (s == null) return Fail("Spline Surface needs a BrazeSpline reference.");
            if (r == null) return Fail("Spline Surface needs a mesh Renderer reference.");

            generatedWorld.Clear();
            bool ok = mode == SurfaceMode.ProjectExisting
                ? ProjectExisting(s, r, generatedWorld)
                : GenerateWrap(r, generatedWorld);

            if (!ok || generatedWorld.Count == 0)
                return Fail("Spline Surface hit nothing. Check the mesh/skin is bound, the Axis, or raise Turns.");

            WriteSpline(s, generatedWorld);
            lastSplineVersion = s.version;
            lastStatus = $"Wrapped {generatedWorld.Count} point{(generatedWorld.Count == 1 ? "" : "s")} on {r.name}.";
            return true;
        }

        bool Fail(string message)
        {
            lastStatus = message;
            return false;
        }

        // The spinning raycaster. Climb the axis from one padded end to the other while orbiting; each step fires
        // one ray from outside the mesh straight in toward the axis and keeps the first (outer-shell) hit.
        bool GenerateWrap(Renderer r, List<Vector3> into)
        {
            Vector3 axisDir = AxisDirection(r);
            MakeBasis(axisDir, out Vector3 u, out Vector3 v);
            Bounds b = r.bounds;
            Vector3 center = b.center;

            float halfHeight = Mathf.Max(ProjectedExtent(b, axisDir), 0.001f);
            float pad = halfHeight * 2f * heightPadding;
            float minHeight = -halfHeight + pad;
            float maxHeight = halfHeight - pad;
            float heightRange = Mathf.Max(maxHeight - minHeight, 1e-4f);

            int loops = Mathf.Max(1, turns);
            int perLoop = Mathf.Max(3, pointsPerTurn);
            int steps = Mathf.Clamp(stairCount > 0 ? stairCount : loops * perLoop, 8, 4096);

            float ph = phase * Mathf.PI * 2f;
            float angleStep = loops * Mathf.PI * 2f / Mathf.Max(steps - 1, 1);
            float heightStep = heightRange / Mathf.Max(steps - 1, 1);

            float radialReach = Mathf.Max(ProjectedExtent(b, u), ProjectedExtent(b, v));
            float margin = Mathf.Max(b.size.magnitude * 0.025f, 0.02f);
            float castOut = radialReach + margin;            // origin distance from the axis
            float maxDist = castOut * 1.15f;                 // stop just past the axis — never reach the far shell

            EnsureArrays(steps);
            for (int i = 0; i < steps; i++)
            {
                float t = i / (float)(steps - 1);
                float wob = (Mathf.Sin((t * wobbleFrequency + phase * 0.37f) * Mathf.PI * 2f)
                           + Mathf.Sin((t * wobbleFrequency * 2.13f + phase * 0.71f + 0.19f) * Mathf.PI * 2f) * 0.35f) * wobble;
                bool end = i == 0 || i == steps - 1;
                float randAngle = end ? 0f : SignedRandom(i, randomSeed, 11u) * angleStep * 0.85f * randomness;
                float randHeight = end ? 0f : SignedRandom(i, randomSeed, 23u) * heightStep * 0.75f * randomness;
                float angle = ph + t * loops * Mathf.PI * 2f + wob * 0.45f + randAngle;
                float height = Mathf.Clamp(Mathf.Lerp(minHeight, maxHeight, t) + randHeight, minHeight, maxHeight);
                Vector3 radialDir = Mathf.Cos(angle) * u + Mathf.Sin(angle) * v;
                uploadScratch[i] = center + axisDir * height + radialDir * castOut;   // origin
                dirScratch[i] = -radialDir;                                           // inward
            }

            if (!CastGpu(r, steps, maxDist, surfaceOffset)) return false;

            // Keep the raw ray hits — they already lie on the surface. No axis/radius reconstruction (that
            // assumes every cross-section is centred on the axis, which breaks on off-axis features like the head).
            into.Clear();
            for (int i = 0; i < steps; i++)
                if (resultScratch[i].w > 0.5f)
                    into.Add(new Vector3(resultScratch[i].x, resultScratch[i].y, resultScratch[i].z));

            if (into.Count < 2) return false;

            // Smooth WITHOUT leaving the surface: gently relax, then re-snap to the nearest surface point.
            if (smoothness > 1e-4f && into.Count >= 3)
                SmoothOnSurface(r, into, smoothness);

            return into.Count >= 2;
        }

        bool ProjectExisting(BrazeSpline s, Renderer r, List<Vector3> into)
        {
            if (s.points == null || s.points.Count == 0) return false;

            int n = s.points.Count;
            var world = new Vector3[n];
            for (int i = 0; i < n; i++) world[i] = s.PointWorld(i);

            float range = surfaceOffset + r.bounds.size.magnitude * 0.15f;   // existing points may sit off-surface
            if (!ProjectPointsGpu(r, world, n, range)) return false;

            into.Clear();
            for (int i = 0; i < n; i++)
                into.Add(resultScratch[i].w > 0.5f
                    ? new Vector3(resultScratch[i].x, resultScratch[i].y, resultScratch[i].z)
                    : world[i]);   // miss → keep the original position rather than dropping the point

            return into.Count == n;
        }

        // Surface-constrained smoothing: relax the polyline toward its neighbour average, then RE-SNAP every
        // point to the nearest surface point on the GPU. The relaxation removes jitter; the re-snap keeps the
        // curve glued to the mesh — so it smooths ALONG the surface instead of lifting off it (no axis model,
        // no off-surface drift). Endpoints pinned. Each cycle is a small blend so points stay within range of
        // the surface for the re-snap.
        void SmoothOnSurface(Renderer r, List<Vector3> pts, float smoothness)
        {
            int n = pts.Count;
            if (n < 3) return;

            int cycles = Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(2f, 12f, smoothness)), 1, 20);
            float amount = Mathf.Lerp(0.35f, 0.7f, smoothness);
            float range = surfaceOffset + r.bounds.size.magnitude * 0.05f;
            var blended = new Vector3[n];

            for (int c = 0; c < cycles; c++)
            {
                blended[0] = pts[0];
                blended[n - 1] = pts[n - 1];
                for (int i = 1; i < n - 1; i++)
                    blended[i] = Vector3.Lerp(pts[i], (pts[i - 1] + pts[i + 1]) * 0.5f, amount);

                if (!ProjectPointsGpu(r, blended, n, range)) return;

                for (int i = 1; i < n - 1; i++)
                    pts[i] = resultScratch[i].w > 0.5f
                        ? new Vector3(resultScratch[i].x, resultScratch[i].y, resultScratch[i].z)
                        : blended[i];
            }
        }

        // Snap the first n of `pts` to the nearest point on the surface via the shared hashed grid; results land in
        // resultScratch (w = 1 on hit, else passthrough). Occasional authoring op → synchronous readback.
        bool ProjectPointsGpu(Renderer r, Vector3[] pts, int n, float range)
        {
            if (!RefreshMeshSource(r)) return false;
            projGrid ??= new BrazeMeshGrid(gridCS, 64);
            float cell = BrazeMeshGrid.SuggestCellSize(r.bounds, meshSource.TriangleCount);
            projGrid.Build(meshSource.Positions, meshSource.Normals, meshSource.Indices,
                           meshSource.TriangleCount, cell, range);
            if (!projGrid.Ready) return false;

            EnsureBuffers(n);
            EnsureResult(n);
            inPtsBuf.SetData(pts, 0, 0, n);
            splineCS.SetBuffer(kProject, "_InPoints", inPtsBuf);
            splineCS.SetBuffer(kProject, "_OutPoints", outPtsBuf);
            projGrid.BindQuery(splineCS, kProject);
            splineCS.SetInt("_PointCount", n);
            splineCS.SetFloat("_Range", range);
            splineCS.SetFloat("_SurfaceOffset", surfaceOffset);
            splineCS.Dispatch(kProject, Mathf.CeilToInt(n / 64f), 1, 1);
            outPtsBuf.GetData(resultScratch, 0, 0, n);
            return true;
        }

        // Dispatch the SpinCast kernel over uploadScratch (origins) + dirScratch (directions); results land in
        // resultScratch. Synchronous readback — this is an occasional authoring-time op, not a per-frame path.
        bool CastGpu(Renderer r, int count, float maxDist, float surfOffset)
        {
            if (!RefreshMeshSource(r)) return false;
            EnsureBuffers(count);
            EnsureResult(count);
            inPtsBuf.SetData(uploadScratch, 0, 0, count);
            dirBuf.SetData(dirScratch, 0, 0, count);

            splineCS.SetBuffer(kCast, "_InPoints", inPtsBuf);
            splineCS.SetBuffer(kCast, "_InDirs", dirBuf);
            splineCS.SetBuffer(kCast, "_OutPoints", outPtsBuf);
            splineCS.SetBuffer(kCast, "_CastPos", meshSource.Positions);
            splineCS.SetBuffer(kCast, "_CastNrm", meshSource.Normals);
            splineCS.SetBuffer(kCast, "_CastIdx", meshSource.Indices);
            splineCS.SetInt("_CastTriCount", meshSource.TriangleCount);
            splineCS.SetInt("_CastCount", count);
            splineCS.SetFloat("_CastMaxDist", maxDist);
            splineCS.SetFloat("_CastOffset", surfOffset);
            splineCS.Dispatch(kCast, Mathf.CeilToInt(count / 64f), 1, 1);
            outPtsBuf.GetData(resultScratch, 0, 0, count);
            return true;
        }

        // ── Track pass (GPU grid, per-frame skinned) ───────────────────────────────────
        void TrackFrame(BrazeSpline s, Renderer r)
        {
            if (s.points == null || s.points.Count < 1) return;
            if (readbackInFlight) return;                 // throttle to one in-flight readback (skinned follow lag is fine)
            if (!PrepareGrid(r)) return;

            int n = s.points.Count;
            EnsureBuffers(n);
            EnsureArrays(n);
            for (int i = 0; i < n; i++) uploadScratch[i] = s.PointWorld(i);
            inPtsBuf.SetData(uploadScratch, 0, 0, n);

            splineCS.SetBuffer(kProject, "_InPoints", inPtsBuf);
            splineCS.SetBuffer(kProject, "_OutPoints", outPtsBuf);
            meshGrid.BindQuery(splineCS, kProject);
            splineCS.SetInt("_PointCount", n);
            splineCS.SetFloat("_Range", trackRange);
            splineCS.SetFloat("_SurfaceOffset", surfaceOffset);
            splineCS.Dispatch(kProject, Mathf.CeilToInt(n / 64f), 1, 1);

            if (Application.isPlaying)
            {
                readbackInFlight = true;
                uint generation = gpuGeneration;
                AsyncGPUReadback.Request(outPtsBuf, req =>
                {
                    if (this == null || generation != gpuGeneration || !isActiveAndEnabled) return;
                    readbackInFlight = false;
                    if (req.hasError) return;
                    ApplyTrack(req.GetData<Vector4>(), n);
                });
            }
            else
            {
                outPtsBuf.GetData(resultScratch, 0, 0, n);
                ApplyTrackArray(resultScratch, n);
            }
        }

        void ApplyTrack(Unity.Collections.NativeArray<Vector4> data, int n)
        {
            BrazeSpline s = TargetSpline;
            if (s?.points == null || s.points.Count != n) return;   // points changed since dispatch → stale
            for (int i = 0; i < n; i++)
            {
                Vector4 d = data[i];
                s.points[i] = s.WorldToUnscaledLocal(new Vector3(d.x, d.y, d.z));
            }
            BumpSpline(s);
        }

        void ApplyTrackArray(Vector4[] data, int n)
        {
            BrazeSpline s = TargetSpline;
            if (s?.points == null || s.points.Count != n) return;
            for (int i = 0; i < n; i++)
                s.points[i] = s.WorldToUnscaledLocal(new Vector3(data[i].x, data[i].y, data[i].z));
            BumpSpline(s);
        }

        void BumpSpline(BrazeSpline s)
        {
            s.version++;
            lastSplineVersion = s.version;   // our own write — don't count it as a user edit
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.EditorUtility.SetDirty(s);
                UnityEditor.SceneView.RepaintAll();
            }
#endif
        }

        bool PrepareGrid(Renderer r)
        {
            if (!RefreshMeshSource(r)) return false;
            meshGrid ??= new BrazeMeshGrid(gridCS, 32);
            float cell = BrazeMeshGrid.SuggestCellSize(r.bounds, meshSource.TriangleCount);
            trackRange = surfaceOffset + cell * 2f;   // small reach: points are already on/near the surface
            meshGrid.Build(meshSource.Positions, meshSource.Normals, meshSource.Indices,
                           meshSource.TriangleCount, cell, trackRange);
            return meshGrid.Ready;
        }

        // ── GPU plumbing ───────────────────────────────────────────────────────────────
        bool RefreshMeshSource(Renderer r)
        {
            if (!EnsureGpu()) return false;
            EnsureMeshSource(r);
            if (meshSource == null || !meshSource.Valid) return false;

            meshSource.Refresh();
            return meshSource.ExtractedOnce && meshSource.TriangleCount > 0;
        }

        bool EnsureGpu()
        {
            if (splineCS == null)
            {
                var asset = Resources.Load<ComputeShader>("BrazeSplineSurface");
                if (asset == null) { return Fail("Spline Surface needs 'BrazeSplineSurface' compute in Resources."); }
                splineCS = Instantiate(asset);
                kProject = splineCS.FindKernel("ProjectPoints");
                kCast = splineCS.FindKernel("SpinCast");
            }
            if (gridCS == null)
            {
                var asset = Resources.Load<ComputeShader>("BrazeMeshGrid");
                if (asset == null) { return Fail("Spline Surface needs 'BrazeMeshGrid' compute in Resources."); }
                gridCS = Instantiate(asset);
            }
            return true;
        }

        void EnsureMeshSource(Renderer r)
        {
            if (meshSource != null && meshSourceRenderer == r) return;
            meshSource?.Dispose();
            meshSource = new BrazeDeformSource(r);
            meshSourceRenderer = r;
            meshGrid?.Dispose();
            meshGrid = null;
            projGrid?.Dispose();
            projGrid = null;
        }

        void EnsureBuffers(int n)
        {
            if (inPtsBuf != null && inPtsBuf.count == n) return;
            inPtsBuf?.Release();
            dirBuf?.Release();
            outPtsBuf?.Release();
            inPtsBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, sizeof(float) * 3);
            dirBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, sizeof(float) * 3);
            outPtsBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, n, sizeof(float) * 4);
        }

        void EnsureArrays(int n)
        {
            if (uploadScratch == null || uploadScratch.Length < n)
            {
                uploadScratch = new Vector3[n];
                dirScratch = new Vector3[n];
                resultScratch = new Vector4[n];
            }
        }

        // The readback array must exist independently of the origin/dir scratch, because ProjectExisting and the
        // smoothing reprojection call the GPU helpers WITHOUT going through EnsureArrays. Missing this made a GPU
        // readback throw inside WrapNow → the exception aborted LateUpdate before the skinned track/follow pass.
        void EnsureResult(int n)
        {
            if (resultScratch == null || resultScratch.Length < n) resultScratch = new Vector4[n];
        }

        void ReleaseGpu()
        {
            gpuGeneration++;
            meshGrid?.Dispose(); meshGrid = null;
            projGrid?.Dispose(); projGrid = null;
            meshSource?.Dispose(); meshSource = null;
            meshSourceRenderer = null;
            inPtsBuf?.Release(); inPtsBuf = null;
            dirBuf?.Release(); dirBuf = null;
            outPtsBuf?.Release(); outPtsBuf = null;
            if (splineCS != null) { if (Application.isPlaying) Destroy(splineCS); else DestroyImmediate(splineCS); splineCS = null; }
            if (gridCS != null) { if (Application.isPlaying) Destroy(gridCS); else DestroyImmediate(gridCS); gridCS = null; }
            kProject = -1; kCast = -1;
            uploadScratch = null; dirScratch = null; resultScratch = null;
            readbackInFlight = false;
        }

        void WriteSpline(BrazeSpline s, List<Vector3> worldPoints)
        {
            if (s.points == null) s.points = new List<Vector3>(worldPoints.Count);
            s.points.Clear();
            for (int i = 0; i < worldPoints.Count; i++)
                s.points.Add(s.WorldToUnscaledLocal(worldPoints[i]));

            s.closed = false;
            s.interpolation = BrazeSpline.Interp.CatmullRom;
            s.activePreset = -1;
            s.inHandles.Clear();
            s.outHandles.Clear();
            s.pointTypes.Clear();
            s.EnsureHandles();
            s.version++;

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.EditorUtility.SetDirty(s);
                UnityEditor.EditorUtility.SetDirty(this);
                UnityEditor.SceneView.RepaintAll();
            }
#endif
        }

        Vector3 AxisDirection(Renderer r)
        {
            Transform t = r != null ? r.transform : transform;
            Vector3 local = axis switch
            {
                WrapAxis.X => Vector3.right,
                WrapAxis.Z => Vector3.forward,
                _ => Vector3.up
            };
            Vector3 world = t.TransformDirection(local);
            return world.sqrMagnitude > 1e-8f ? world.normalized : Vector3.up;
        }

        static void MakeBasis(Vector3 axisDir, out Vector3 u, out Vector3 v)
        {
            Vector3 reference = Mathf.Abs(Vector3.Dot(axisDir, Vector3.up)) < 0.92f ? Vector3.up : Vector3.right;
            u = Vector3.Cross(reference, axisDir);
            if (u.sqrMagnitude < 1e-8f) u = Vector3.Cross(Vector3.forward, axisDir);
            u.Normalize();
            v = Vector3.Cross(axisDir, u).normalized;
        }

        static float ProjectedExtent(Bounds bounds, Vector3 dir)
        {
            dir = new Vector3(Mathf.Abs(dir.x), Mathf.Abs(dir.y), Mathf.Abs(dir.z));
            return bounds.extents.x * dir.x + bounds.extents.y * dir.y + bounds.extents.z * dir.z;
        }

        static float SignedRandom(int index, int seed, uint salt)
        {
            unchecked
            {
                uint h = (uint)seed;
                h ^= (uint)index * 0x9E3779B9u;
                h ^= salt * 0x85EBCA6Bu;
                h ^= h >> 16;
                h *= 0x7FEB352Du;
                h ^= h >> 15;
                h *= 0x846CA68Bu;
                h ^= h >> 16;
                return ((h & 0x00FFFFFFu) / 16777215f) * 2f - 1f;
            }
        }

        // Authoring-only hash: spline ref, surface ref, mode, axis, wrap params, topology, and the spline's
        // transform. Deliberately EXCLUDES the renderer's per-frame pose — the GPU track pass owns deformation,
        // so an animating skinned mesh does not re-trigger the wrap pass. Static meshes also fold in the renderer
        // transform/bounds so a moving rigid mesh re-wraps.
        int BuildSeedHash(BrazeSpline s, Renderer r, bool skinned)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (s != null ? s.BrazeInstanceId() : 0);
                h = h * 31 + (r != null ? r.BrazeInstanceId() : 0);
                h = h * 31 + (int)mode;
                h = h * 31 + (int)axis;
                h = h * 31 + turns;
                h = h * 31 + pointsPerTurn;
                h = h * 31 + stairCount;
                h = h * 31 + Q(heightPadding);
                h = h * 31 + Q(surfaceOffset);
                h = h * 31 + Q(wobble);
                h = h * 31 + Q(wobbleFrequency);
                h = h * 31 + Q(randomness);
                h = h * 31 + randomSeed;
                h = h * 31 + Q(smoothness);
                h = h * 31 + Q(phase);
                h = h * 31 + SharedMeshId(r);
                if (s != null) h = HashMatrix(h, s.transform.localToWorldMatrix);
                if (!skinned && r != null)
                {
                    h = HashMatrix(h, r.transform.localToWorldMatrix);
                    h = HashBounds(h, r.bounds);
                }
                return h;
            }
        }

        static int SharedMeshId(Renderer r)
        {
            if (r is SkinnedMeshRenderer skinned) return skinned.sharedMesh != null ? skinned.sharedMesh.BrazeInstanceId() : 0;
            if (r != null && r.TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null) return mf.sharedMesh.BrazeInstanceId();
            return 0;
        }

        static int HashBounds(int h, Bounds b)
        {
            h = HashVector(h, b.center);
            h = HashVector(h, b.extents);
            return h;
        }

        static int HashMatrix(int h, Matrix4x4 m)
        {
            for (int i = 0; i < 16; i++) h = h * 31 + Q(m[i]);
            return h;
        }

        static int HashVector(int h, Vector3 v)
        {
            h = h * 31 + Q(v.x);
            h = h * 31 + Q(v.y);
            h = h * 31 + Q(v.z);
            return h;
        }

        static int Q(float v) => Mathf.RoundToInt(v * 1000f);

        void OnDrawGizmosSelected()
        {
            if (!drawPreview) return;
            BrazeSpline s = TargetSpline;
            if (s == null || s.points == null || s.points.Count < 2) return;

            Gizmos.color = previewColor;
            // Sample at the SAME rate as BrazeSpline's own gizmo so the two previews trace identical polylines
            // (different counts visibly diverge on a dense wrap).
            int count = s.GizmoSampleCount;
            if (count < 2) return;
            Vector3 prev = s.EvaluateWorld(0f);
            for (int i = 1; i < count; i++)
            {
                Vector3 cur = s.EvaluateWorld(i / (float)(count - 1));
                Gizmos.DrawLine(prev, cur);
                prev = cur;
            }
        }
    }
}
