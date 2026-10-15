using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BrazeFX
{
    /// <summary>
    /// A spatial selection region — like a projector. It is the union of its seeds' influence in Braze mask space,
    /// landing on whatever geometry is under it, completely agnostic of mesh topology, faces, normals or authored UVs.
    /// It does not creep along a surface and it does not occlude: if a seed's influence reaches a sampled point, that
    /// point is in the mask.
    ///
    /// The field lives in one EVALUATION SPACE, chosen by <see cref="space"/>:
    ///   • World — legacy: the region lives in world space; the pattern swims as the object moves.
    ///   • Local (default) — the region + pattern are locked to a scale-independent mask space: the object's orientation
    ///     in world metres. This is Braze's own uniform coordinate system, not mesh UVs and not raw object space, so
    ///     non-uniform Transform stretching does not distort the mask. Skinned consumers sample each surface point's
    ///     rest position transformed into this same mask space.
    ///
    /// The one true primitive is the shared HLSL field in BrazeMaskField.hlsl. Consumers with mesh-local/rest points
    /// transform them into field space with <see cref="SourceLocalToFieldMatrix"/> and evaluate the field on GPU.
    /// Everything is a VIEW of it: the Scene preview paints the surface with the same per-pixel distance, an emitter gates
    /// which POINTS emit by the same weight, a Voronoi fracture gates which CELLS fracture by it. Because all evaluate the
    /// same function, the preview, the fracture and the emission are guaranteed to agree.
    ///
    /// The knobs are [BrazeAnimatable] and the field re-evaluates every frame, so animated masks (moving seeds, keyframed
    /// Reach, a growing front) need no restructuring. Creep-along-surface and light-style occlusion are deliberately left
    /// out — they are later FILTERS layered on this spatial fundamental.
    /// </summary>
    [AddComponentMenu("BrazeFX/Destruction/Braze Surface Mask", 43)]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class BrazeSurfaceMask : MonoBehaviour, IBrazeAnimatedTarget
    {
        /// <summary>The space the mask field is evaluated in. World = legacy world coordinates. Local = Braze mask space:
        /// the object's oriented world-metre frame, with source local/rest points transformed into that same frame.</summary>
        public enum MaskSpace { World, Local }
        public enum ProjectionMode { Planar2D, Volume3D }
        public enum ProjectionPlane { XY, XZ, YZ }
        public enum PreviewStyle { Grayscale, Color, Texture }

        [Tooltip("World = region lives in world space (pattern swims when the object moves). Local = region + pattern " +
                 "are locked to the object's rest pose; follows skinned-mesh deformation. Attach seeds under the " +
                 "character's bones to name body parts.")]
        public MaskSpace space = MaskSpace.Local;

        [Tooltip("Planar2D = decal/projector style: seed influence is measured on a uniform mask plane and projected onto " +
                 "whatever surface is below it. Volume3D = legacy spherical influence in full 3D.")]
        public ProjectionMode projectionMode = ProjectionMode.Planar2D;
        [Tooltip("The two axes used by Planar2D projection in the mask's local metric frame. XZ matches Unity plane-style surfaces.")]
        public ProjectionPlane projectionPlane = ProjectionPlane.XZ;

        /// <summary>True when the field is evaluated in Braze mask space. Consumers with mesh-local/rest points transform
        /// them through <see cref="SourceLocalToFieldMatrix"/> before evaluating the shared GPU field.</summary>
        public bool IsLocalSpace => space == MaskSpace.Local;

        [Tooltip("Seed points (empties, or real objects placed on/near the surface). The region is the UNION of each " +
                 "seed's influence. Only the seed's POSITION matters; Reach is the radius around it in the selected " +
                 "projection. In Local space, the seed is mapped into Braze's uniform mask space.")]
        public List<Transform> seeds = new();

        [Tooltip("Hide the seed objects' renderers during Play. Seeds are authoring handles only (the masked region is " +
                 "what ships), so this keeps a placement sphere from showing up in-game. Restored when Play stops.")]
        public bool hideSeedsInPlay = true;

        [BrazeAnimatable("MASK/Reach (m)", 0f, 30f)] public float reach = 1.5f;               // region radius around each seed (world m) — crank up to cover the whole mesh
        [BrazeAnimatable("MASK/Softness (m)", 0.01f, 5f)] public float softness = 0.4f;        // edge feather (world m)

        public bool invert = false;
        public bool showPreview = true;
        [Tooltip("Draw the preview during Play. Off by default so the mask remains an authoring/debug view, not a runtime " +
                 "rendering cost. Consumers still evaluate the mask on GPU when this is off.")]
        public bool showPreviewInPlay = false;
        [Tooltip("How the mask field is rendered. Grayscale keeps the old debug look; Color and Texture make it usable " +
                 "as an actual surface visual.")]
        public PreviewStyle previewStyle = PreviewStyle.Grayscale;
        [Tooltip("Tint for Color/Texture preview styles. Alpha controls rendered mask opacity before Preview Opacity.")]
        [ColorUsage(true, true)] public Color previewColor = Color.white;
        [Tooltip("Optional texture rendered through the mask. It is mapped in Braze mask space, not mesh UVs.")]
        public Texture2D previewTexture;
        [Tooltip("Texture tiling in Braze mask-space metres.")]
        public Vector2 previewTextureScale = Vector2.one;
        [Tooltip("Texture offset in Braze mask-space coordinates.")]
        public Vector2 previewTextureOffset = Vector2.zero;
        [Tooltip("How much the texture modulates the tint. 0 = flat color, 1 = full texture color.")]
        [Range(0f, 1f)] public float previewTextureStrength = 1f;
        [Tooltip("Preview overlay opacity — authoring aid only (does not affect the mask field or what consumers sample).")]
        [Range(0f, 1f)] public float previewOpacity = 1f;
        [Tooltip("Lift the preview this many metres off the surface (along normals) so it doesn't z-fight / intersect the " +
                 "source mesh. Authoring aid only — raise it slightly if you see the original mesh poking through.")]
        [Range(0f, 0.1f)] public float surfaceOffset = 0.005f;
        [Tooltip("Draw the preview as solid magenta + a Scene-view status label, to debug visibility.")]
        public bool debugPreview = false;

        [Tooltip("Resolution-independent surface spacing used by consumers that need actual spawn/emission points from " +
                 "this mask. Smaller = more points inside the procedural mask, independent of source mesh vertex density.")]
        [Range(0.005f, 1f)] public float surfacePointSpacing = 0.05f;
        [Tooltip("Hard cap for surface points generated from this mask for consumers. Prevents a tiny spacing on a large " +
                 "mesh from producing an excessive point set.")]
        [Range(16, 50000)] public int maxSurfacePoints = 8192;

        // ── Growth (procedural front animation; composes with the spatial fundamental — it only drives the front) ──
        [Tooltip("Procedurally animate the front from Reach Start to Reach End when triggered (e.g. an ignition " +
                 "spreading). OFF = the front sits at the MASK Reach (static / timeline-keyframable). When ON, the " +
                 "explicit Start/End below define the animation, so you get a predictable, bounded result.")]
        public bool growth = false;
        [Tooltip("Auto-trigger growth when this object is enabled in Play mode. Call Trigger() to (re)start it manually.")]
        public bool playOnEnable = true;
        [Tooltip("Reach (world metres) the front STARTS at when growth triggers.")]
        public float growthFrom = 0f;
        [Tooltip("Reach (world metres) the front ENDS at, and holds.")]
        public float growthTo = 1.5f;
        [Tooltip("Seconds for the front to travel from Reach Start to Reach End.")]
        public float growthDuration = 1f;
        [Tooltip("Shape of the animation over normalized time 0..1. Ease-in/out by default.")]
        public AnimationCurve growthCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        bool _triggered; float _growStartTime;

        // ── Filters (each independently toggled; reshape the region in a fixed pipeline) ──
        // ORGANIC — bacterial-colony edge.
        public bool organicEnabled = true;
        [Tooltip("Organic edge: perturb the boundary with world-space noise so the region spreads like a bacterial " +
                 "colony (lobes, fingers, islands) instead of a clean circle. Applies to the static mask and to " +
                 "growth, and is mirrored in the shader so the preview matches what consumers will sample.")]
        [BrazeAnimatable("MASK/Organic (filter)", 0f, 1f)] public float organicAmount = 0.35f;
        [Tooltip("Feature density of the organic edge: higher = finer crinkle, lower = big lobes (world cycles per metre).")]
        public float organicScale = 1f;
        [Tooltip("Boil speed — animates the organic pattern over time (0 = a static, locked-in-world pattern).")]
        public float organicSpeed = 0f;

        // ANISOTROPY — stretch into an ellipse / streak.
        public bool anisotropyEnabled = false;
        [Tooltip("Stretch the region along an axis → elliptical / wind-streaked instead of circular. 0 = round.")]
        public float anisotropyAmount = 1f;
        [Tooltip("World-space axis the region is stretched along (normalized internally).")]
        public Vector3 anisotropyAxis = Vector3.forward;

        // RINGS — concentric bands / ripple.
        public bool ringsEnabled = false;
        [Tooltip("Carve the filled disc into concentric bands → shockwave / ripple. 0 = solid disc.")]
        [BrazeAnimatable("MASK/Rings (filter)", 0f, 1f)] public float ringAmount = 1f;
        [Tooltip("Distance between rings, world metres.")]
        public float ringSpacing = 0.5f;
        [Tooltip("Ring band thickness as a fraction of the spacing (0 = thin lines, 1 = nearly solid).")]
        public float ringThickness = 0.5f;

        // EROSION — dissolve holes.
        public bool erodeEnabled = false;
        [Tooltip("High-frequency noise punches holes → porous / crumbly / ember edge. 0 = none, →1 = fully eaten.")]
        [BrazeAnimatable("MASK/Erosion (filter)", 0f, 1f)] public float erodeAmount = 0.5f;
        [Tooltip("Hole density of the erosion (world cycles per metre): higher = smaller, finer holes.")]
        public float erodeScale = 2f;

        // RESPONSE — contrast / bias of the 0..1 membership (how hard/soft the region is).
        public bool contrastEnabled = false;
        [Tooltip("Contrast of the membership falloff: 1 = neutral, >1 sharpens toward a hard cutoff, <1 softens/flattens.")]
        public float contrast = 1f;
        [Tooltip("Bias the membership up (grow coverage) or down (shrink). 0 = neutral.")]
        public float bias = 0f;

        const int MaxSeeds = 16;   // shader uniform-array cap used by preview and compute gates
        const int MinSurfacePoints = 16;

        // Resolved once per frame (world space): each seed is a POINT; the region is Reach metres around it.
        readonly List<Vector3> _seedPos = new();
        Vector4[] _seedVecs;                 // shader uniform: xyz = seed centre in EVAL space (w unused)
        Material _previewMat;
        int _evalMode;                       // shader eval space: 0 = world, 1 = world→field, 2 = rest UV3→field
        int _resolvedFrame = -1;
        readonly List<Vector3> _areaVerts = new();
        readonly List<int> _areaTris = new();
        readonly List<int> _areaSubTris = new();
        int _areaMeshId, _areaSampleCount = MinSurfacePoints, _areaPointCap = -1;
        float _areaSpacing = -1f;
        Vector3 _areaScale = new(float.NaN, float.NaN, float.NaN);

        int _dbgSeeds, _dbgNullSeeds, _lastDrawFrame = -1;
        string _dbgState = "idle", _drawState = "not drawn";
        bool _loggedMissingShader, _loggedUnsupportedShader;

        /// <summary>True when the region has at least one live seed (so consumers know there's something to sample).</summary>
        public bool HasMask { get { EnsureResolved(); return _seedPos.Count > 0; } }

        /// <summary>Live readout for the inspector / Scene-view debug label.</summary>
        public string DebugReport
        {
            get
            {
                string frame = _lastDrawFrame >= 0 ? _lastDrawFrame.ToString() : "never";
                int slots = seeds != null ? seeds.Count : 0;
                string growState = growth ? $" | grow {GrowthProgress01() * 100f:0}%" : "";
                return $"Mask {(_seedPos.Count > 0 ? "active" : "idle")} | {_dbgState}\n" +
                       $"seeds {_dbgSeeds}/{slots}/{MaxSeeds} | null {_dbgNullSeeds} | front {Front:0.##} m{growState}\n" +
                       $"draw {_drawState} | frame {frame}";
            }
        }

        /// <summary>The current region radius around each seed, in world metres — what preview and GPU consumers read.
        /// In EDIT mode (and with growth off) it is simply Reach. In PLAY mode with growth on it animates Reach Start
        /// → Reach End along the curve after a trigger, then holds at Reach End. Growth never
        /// changes anything in edit mode — the preview always shows the static Reach there.</summary>
        public float Front
        {
            get
            {
                if (!growth || !Application.isPlaying) return Mathf.Max(0f, reach);   // edit mode / no growth → static Reach
                float c = growthCurve != null ? growthCurve.Evaluate(GrowthProgress01()) : GrowthProgress01();
                return Mathf.Max(0f, Mathf.LerpUnclamped(growthFrom, growthTo, c));   // Start → End, holds at End
            }
        }

        /// <summary>0..1 along the growth: 0 before trigger, ramps over growthDuration, holds at 1. Dormant (0) outside
        /// Play mode and when growth is off — growth only runs in Play, so edit-mode previews show the static Reach.</summary>
        public float GrowthProgress01()
        {
            if (!growth || !Application.isPlaying) return 0f;
            if (!_triggered) return 0f;
            if (growthDuration <= 1e-4f) return 1f;
            return Mathf.Clamp01((Time.time - _growStartTime) / growthDuration);
        }

        /// <summary>(Re)start the growth from 0 now. No-op effect unless <see cref="growth"/> is on.</summary>
        [BrazeTimelineEvent("GROWTH/Trigger")]
        public void Trigger() { _triggered = true; _growStartTime = Time.time; }

        /// <summary>Snap the growth back to its un-triggered state (front at 0 until the next Trigger).</summary>
        [BrazeTimelineEvent("GROWTH/Reset")]
        public void ResetGrowth() { _triggered = false; }

        /// <summary>A cheap hash that changes whenever anything that affects the GPU field changes. Consumers use it to
        /// rebuild compact pass sets only when the field changes, instead of re-running the gate every sim step.</summary>
        public int FieldVersion
        {
            get
            {
                EnsureResolved();
                unchecked
                {
                    int h = 17;
                    h = h * 31 + HashFloat(Front);
                    h = h * 31 + HashFloat(softness);
                    h = h * 31 + (invert ? 1 : 0);
                    h = h * 31 + (int)projectionMode;
                    h = h * 31 + (int)projectionPlane;
                    if (organicEnabled && organicAmount > 1e-4f)
                    {
                        h = h * 31 + HashFloat(organicAmount);
                        h = h * 31 + HashFloat(organicScale);
                        if (Mathf.Abs(organicSpeed) > 1e-6f) h = h * 31 + HashFloat(organicSpeed * Time.time);   // boiling -> per-frame
                    }
                    if (anisotropyEnabled && anisotropyAmount > 1e-4f) { h = h * 31 + HashFloat(anisotropyAmount); h = h * 31 + HashVec3(anisotropyAxis); }
                    if (ringsEnabled && ringAmount > 1e-4f) { h = h * 31 + HashFloat(ringAmount); h = h * 31 + HashFloat(ringSpacing); h = h * 31 + HashFloat(ringThickness); }
                    if (erodeEnabled && erodeAmount > 1e-4f) { h = h * 31 + HashFloat(erodeAmount); h = h * 31 + HashFloat(erodeScale); }
                    if (contrastEnabled) { h = h * 31 + HashFloat(contrast); h = h * 31 + HashFloat(bias); }
                    for (int i = 0; i < _seedPos.Count; i++)
                    {
                        Vector3 p = _seedPos[i];
                        h = h * 31 + HashFloat(p.x);
                        h = h * 31 + HashFloat(p.y);
                        h = h * 31 + HashFloat(p.z);
                    }
                    return h;
                }
            }
        }

        /// <summary>Force a re-read of the seed transforms now (e.g. a consumer about to sample a batch this frame
        /// before this component's own Update has run).</summary>
        public void RefreshSeeds() { ResolveSeeds(); _resolvedFrame = Time.frameCount; }

        /// <summary>World → Braze mask space. Local masks use the object's orientation but ignore its scale, giving a
        /// uniform metre-based coordinate system that rides with the object without stretching with it.</summary>
        public Matrix4x4 WorldToFieldMatrix =>
            IsLocalSpace ? Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one).inverse : Matrix4x4.identity;

        /// <summary>Source mesh local/rest → Braze mask space. Consumers pass this matrix to compute kernels so cached
        /// rest/sample points can evaluate the shared GPU field.</summary>
        public Matrix4x4 SourceLocalToFieldMatrix(Transform sourceTransform)
        {
            if (sourceTransform == null) return Matrix4x4.identity;
            return WorldToFieldMatrix * sourceTransform.localToWorldMatrix;
        }

        public Vector3 WorldToFieldPoint(Vector3 worldPoint) => WorldToFieldMatrix.MultiplyPoint3x4(worldPoint);
        public Vector3 SourceLocalToFieldPoint(Vector3 localPoint, Transform sourceTransform) =>
            SourceLocalToFieldMatrix(sourceTransform).MultiplyPoint3x4(localPoint);

        public int SourceLocalToFieldVersion(Transform sourceTransform)
        {
            Matrix4x4 m = SourceLocalToFieldMatrix(sourceTransform);
            unchecked
            {
                int h = 17;
                h = h * 31 + HashFloat(m.m00); h = h * 31 + HashFloat(m.m01); h = h * 31 + HashFloat(m.m02); h = h * 31 + HashFloat(m.m03);
                h = h * 31 + HashFloat(m.m10); h = h * 31 + HashFloat(m.m11); h = h * 31 + HashFloat(m.m12); h = h * 31 + HashFloat(m.m13);
                h = h * 31 + HashFloat(m.m20); h = h * 31 + HashFloat(m.m21); h = h * 31 + HashFloat(m.m22); h = h * 31 + HashFloat(m.m23);
                h = h * 31 + HashFloat(m.m30); h = h * 31 + HashFloat(m.m31); h = h * 31 + HashFloat(m.m32); h = h * 31 + HashFloat(m.m33);
                return h;
            }
        }

        static int HashFloat(float v) => Mathf.RoundToInt(v * 10000f);
        static int HashVec3(Vector3 v)
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + HashFloat(v.x);
                h = h * 31 + HashFloat(v.y);
                h = h * 31 + HashFloat(v.z);
                return h;
            }
        }

        public float SurfacePointSpacing => Mathf.Max(0.005f, surfacePointSpacing);
        public int SurfacePointCap => Mathf.Clamp(maxSurfacePoints, MinSurfacePoints, 50000);

        /// <summary>Resolution-independent point budget for consumers that sample this mask on a mesh surface. The count is
        /// derived from world-space triangle area / spacing², then capped by Max Surface Points, so it is independent of
        /// source mesh vertex density.</summary>
        public int ResolveSurfaceSampleCount(Mesh mesh, Transform sourceTransform)
        {
            if (mesh == null || sourceTransform == null) return MinSurfacePoints;
            float spacing = SurfacePointSpacing;
            int cap = SurfacePointCap;
            Vector3 scale = sourceTransform.lossyScale;
            int meshId = mesh.BrazeInstanceId();
            if (_areaMeshId == meshId && _areaSpacing == spacing && _areaPointCap == cap && NearlySame(_areaScale, scale))
                return _areaSampleCount;

            float area = EstimateWorldSurfaceArea(mesh, scale);
            int count = area > 1e-8f ? Mathf.CeilToInt(area / Mathf.Max(spacing * spacing, 1e-8f)) : MinSurfacePoints;
            _areaSampleCount = Mathf.Clamp(count, MinSurfacePoints, cap);
            _areaMeshId = meshId;
            _areaSpacing = spacing;
            _areaPointCap = cap;
            _areaScale = scale;
            return _areaSampleCount;
        }

        float EstimateWorldSurfaceArea(Mesh mesh, Vector3 scale)
        {
            if (!mesh.isReadable) return 0f;
            mesh.GetVertices(_areaVerts);
            _areaTris.Clear();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                _areaSubTris.Clear();
                mesh.GetTriangles(_areaSubTris, s);
                _areaTris.AddRange(_areaSubTris);
            }

            float area = 0f;
            for (int t = 0; t + 2 < _areaTris.Count; t += 3)
            {
                int ia = _areaTris[t], ib = _areaTris[t + 1], ic = _areaTris[t + 2];
                if (ia >= _areaVerts.Count || ib >= _areaVerts.Count || ic >= _areaVerts.Count) continue;
                Vector3 a = Vector3.Scale(_areaVerts[ia], scale);
                Vector3 b = Vector3.Scale(_areaVerts[ib], scale);
                Vector3 c = Vector3.Scale(_areaVerts[ic], scale);
                area += Vector3.Cross(b - a, c - a).magnitude * 0.5f;
            }
            return area;
        }

        static bool NearlySame(Vector3 a, Vector3 b) => (a - b).sqrMagnitude <= 1e-10f;

        // ── Seed resolution (world space) ───────────────────────────────────────────────────────────────────────
        void EnsureResolved()
        {
            if (_resolvedFrame == Time.frameCount) return;
            ResolveSeeds();
            _resolvedFrame = Time.frameCount;
        }

        void ResolveSeeds()
        {
            _seedPos.Clear();
            _dbgNullSeeds = 0;
            var smr = IsLocalSpace ? GetComponent<SkinnedMeshRenderer>() : null;   // bone mapping only matters in Local
            if (seeds != null)
            {
                for (int i = 0; i < seeds.Count && _seedPos.Count < MaxSeeds; i++)
                {
                    var s = seeds[i];
                    if (s == null) { _dbgNullSeeds++; continue; }
                    _seedPos.Add(ResolveSeedPos(s, smr));   // into the field's eval space (world or Braze mask space)
                }
            }
            _dbgSeeds = _seedPos.Count;
            _dbgState = _seedPos.Count > 0 ? "active"
                      : (seeds != null && seeds.Count > 0 ? "seeds assigned but none live" : "no seeds");
        }

        /// <summary>A seed's position in the field's eval space. World → raw world position. Local + static mesh →
        /// scale-independent Braze mask space. Local + skinned mesh → the seed STICKS to the nearest rest vertex, then
        /// that rest position is transformed into the same Braze mask space.</summary>
        Vector3 ResolveSeedPos(Transform s, SkinnedMeshRenderer smr)
        {
            if (!IsLocalSpace) return s.position;                              // legacy world space
            if (smr != null && smr.sharedMesh != null) return ResolveSkinnedSeedRest(s, smr);
            return WorldToFieldPoint(s.position);                             // static/rigid: scale-independent mask space
        }

        // ── Skinned seed binding: glue each seed to the nearest REST vertex (NO skinning bake) ──────────────────────
        // A seed binds to the nearest vertex against the mesh's REST positions, then transforms that rest position into
        // Braze mask space as its field anchor. The preview and consumers evaluate rest points through the same matrix,
        // so the masked region stays glued to that patch through deformation. Author seeds with the mesh near its
        // rest/bind pose. Sticky in Play (bind once); re-binds in Edit as you drag a seed.
        struct SeedBind { public int vtx; public Vector3 bindWorld; public Vector3 restPos; }
        readonly Dictionary<Transform, SeedBind> _seedBinds = new();
        readonly List<Vector3> _restVerts = new();        // sharedMesh rest verts (mesh-local) — cached; never change
        int _restMeshId = 0;
        const float RebindEps2 = 1e-8f;                   // ~0.1 mm: below this a seed is considered un-moved

        Vector3 ResolveSkinnedSeedRest(Transform s, SkinnedMeshRenderer smr)
        {
            EnsureRestVerts(smr.sharedMesh);
            if (!_seedBinds.TryGetValue(s, out var bind)) bind.vtx = -1;
            bool have = bind.vtx >= 0 && bind.vtx < _restVerts.Count;
            bool rebind = !have || (!Application.isPlaying && (s.position - bind.bindWorld).sqrMagnitude > RebindEps2);
            if (rebind)
            {
                int idx = NearestRestVertex(WorldToFieldPoint(s.position));   // field space, no bake
                bind.bindWorld = s.position;
                if (idx >= 0) { bind.vtx = idx; bind.restPos = _restVerts[idx]; }
                else if (!have) { bind.vtx = 0; bind.restPos = transform.InverseTransformPoint(s.position); }
                _seedBinds[s] = bind;
            }
            return SourceLocalToFieldPoint(bind.restPos, transform);
        }

        // Cache the mesh's REST (bind-pose) vertices — constant; fetched only when the shared mesh changes.
        void EnsureRestVerts(Mesh mesh)
        {
            int id = mesh.BrazeInstanceId();
            if (id == _restMeshId && _restVerts.Count > 0) return;
            mesh.GetVertices(_restVerts);
            _restMeshId = id;
        }

        int NearestRestVertex(Vector3 fieldPoint)
        {
            int best = -1; float bestD = float.MaxValue;
            Matrix4x4 localToField = SourceLocalToFieldMatrix(transform);
            for (int i = 0; i < _restVerts.Count; i++)
            {
                float d = (localToField.MultiplyPoint3x4(_restVerts[i]) - fieldPoint).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        /// <summary>Drop all seed→vertex bindings so each seed re-binds to its nearest surface vertex on the next resolve.
        /// Called automatically on enable; also available to script a deliberate re-glue after re-posing in Play mode
        /// (where binding is otherwise sticky).</summary>
        public void RebindSeeds() { _seedBinds.Clear(); _resolvedFrame = -1; }

        // ── Preview (a view of the field — same per-pixel value the GPU gate uses) ───────────────────────────────────
        void Update()
        {
            EnsureResolved();
            bool previewAllowed = showPreview && (!Application.isPlaying || showPreviewInPlay);
            bool show = previewAllowed && _seedPos.Count > 0;
            // Local + skinned → a hidden GPU-skinned provider renders the overlay (no CPU bake). Else a queued DrawMesh.
            if (show && IsLocalSpace && TryGetComponent(out SkinnedMeshRenderer smr) && smr.sharedMesh != null)
                DrawPreviewSkinned(smr);
            else
            {
                DestroyPreviewProvider();
                if (show) DrawPreviewStatic();
                else _drawState = _seedPos.Count == 0 ? "no live seeds" : (Application.isPlaying && showPreview && !showPreviewInPlay ? "preview off in play" : "preview off");
            }
        }

        Mesh ResolveMesh()
        {
            if (TryGetComponent(out SkinnedMeshRenderer smr) && smr.sharedMesh != null) return smr.sharedMesh;
            if (TryGetComponent(out MeshFilter mf)) return mf.sharedMesh;
            return null;
        }

        // Skinned Local preview: a hidden SkinnedMeshRenderer renders a COPY of the mesh (uv3 = rest pos) with the
        // preview material — Unity GPU-skins it, so the overlay deforms with the body while the field is evaluated from
        // rest position transformed into Braze mask space (eval mode 2). No per-frame BakeMesh, no DrawMesh.
        void DrawPreviewSkinned(SkinnedMeshRenderer smr)
        {
            if (!EnsureMaterial()) return;
            if (!EnsurePreviewProvider(smr)) { _drawState = "preview provider failed"; return; }
            _evalMode = 2;
            ConfigureMaterial();                 // refresh the field uniforms on the shared preview material
            SyncPreviewProvider(smr);
            _drawState = debugPreview ? "skinned provider (magenta)" : "skinned provider";
            _lastDrawFrame = Time.frameCount;
        }

        // Static / world preview: queue a one-shot DrawMesh of the shared mesh (cheap; no skinning).
        void DrawPreviewStatic()
        {
            var mesh = ResolveMesh();
            if (mesh == null) { _drawState = "no mesh on this object"; return; }
            if (!EnsureMaterial()) return;
            _evalMode = IsLocalSpace ? 1 : 0;    // local-static → positionOS ; world → positionWS
            ConfigureMaterial();
#if UNITY_6000_5_OR_NEWER
            Graphics.DrawMesh(mesh, transform.localToWorldMatrix, _previewMat, gameObject.layer, null, 0, null,
                              ShadowCastingMode.Off, false, null, LightProbeUsage.Off);
#else
            Graphics.DrawMesh(mesh, transform.localToWorldMatrix, _previewMat, gameObject.layer, null, 0, null,
                              ShadowCastingMode.Off, false, null, LightProbeUsage.Off, null);
#endif
            _drawState = debugPreview ? "queued magenta" : "queued preview";
            _lastDrawFrame = Time.frameCount;
        }

        // ── Hidden GPU-skinned preview provider (no CPU bake) ────────────────────────────────────────────────────────
        GameObject _previewProviderGo;
        SkinnedMeshRenderer _previewProviderSmr;
        Mesh _previewProviderMesh;          // copy of sharedMesh with uv3 = rest positions
        int _previewProviderMeshId = int.MinValue;

        bool EnsurePreviewProvider(SkinnedMeshRenderer src)
        {
            int meshId = src.sharedMesh.BrazeInstanceId();
            bool rebuildMesh = _previewProviderMesh == null || meshId != _previewProviderMeshId;
            if (rebuildMesh)
            {
                if (_previewProviderMesh != null) DestroyImmediateSafe(_previewProviderMesh);
                _previewProviderMesh = Instantiate(src.sharedMesh);
                _previewProviderMesh.hideFlags = HideFlags.HideAndDontSave;
                EnsureRestVerts(src.sharedMesh);
                _previewProviderMesh.SetUVs(3, _restVerts);   // rest pos in uv3 → unchanged by GPU skinning → field eval
                _previewProviderMeshId = meshId;
            }
            if (_previewProviderGo == null)
            {
                _previewProviderGo = new GameObject("BrazeFX Mask Preview Provider") { hideFlags = HideFlags.HideAndDontSave, layer = gameObject.layer };
                _previewProviderSmr = _previewProviderGo.AddComponent<SkinnedMeshRenderer>();
                _previewProviderSmr.hideFlags = HideFlags.HideAndDontSave;
            }
            if (rebuildMesh || _previewProviderSmr.sharedMesh != _previewProviderMesh)
            {
                _previewProviderSmr.sharedMesh = _previewProviderMesh;
                _previewProviderSmr.bones = src.bones;
                _previewProviderSmr.rootBone = src.rootBone;
                _previewProviderSmr.quality = src.quality;
                _previewProviderSmr.updateWhenOffscreen = true;
                _previewProviderSmr.shadowCastingMode = ShadowCastingMode.Off;
                _previewProviderSmr.receiveShadows = false;
            }
            _previewProviderSmr.sharedMaterial = _previewMat;
            return true;
        }

        void SyncPreviewProvider(SkinnedMeshRenderer src)
        {
            var t = _previewProviderGo.transform;
            t.SetPositionAndRotation(src.transform.position, src.transform.rotation);
            t.localScale = src.transform.lossyScale;
            _previewProviderSmr.localBounds = src.localBounds;
            var mesh = src.sharedMesh;
            for (int i = 0; i < mesh.blendShapeCount; i++)
                _previewProviderSmr.SetBlendShapeWeight(i, src.GetBlendShapeWeight(i));
        }

        void DestroyPreviewProvider()
        {
            if (_previewProviderGo != null) { DestroyImmediateSafe(_previewProviderGo); _previewProviderGo = null; _previewProviderSmr = null; }
        }

        static void DestroyImmediateSafe(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        bool EnsureMaterial()
        {
            if (_previewMat != null)
            {
                if (_previewMat.shader != null && _previewMat.shader.isSupported) return true;
                _drawState = "preview shader unsupported";
                return false;
            }
            var sh = BrazeRuntimeShaderLibrary.Find("BrazeFX/MaskPreview");
            if (sh == null)
            {
                _drawState = "preview shader not found";
                if (!_loggedMissingShader) { Debug.LogError("[BrazeSurfaceMask] Shader 'BrazeFX/MaskPreview' not found."); _loggedMissingShader = true; }
                return false;
            }
            if (!sh.isSupported)
            {
                _drawState = "preview shader unsupported";
                if (!_loggedUnsupportedShader) { Debug.LogError("[BrazeSurfaceMask] Shader 'BrazeFX/MaskPreview' is not supported (see Console for compile errors)."); _loggedUnsupportedShader = true; }
                return false;
            }
            // Opaque-range queue (see the shader) so the fire/transparents draw OVER the mask, not behind it.
            _previewMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave, renderQueue = (int)RenderQueue.Geometry + 1 };
            return true;
        }

        void ConfigureMaterial()
        {
            BindField(_previewMat);                                           // the shared field uniforms (BrazeMaskField.hlsl)
            _previewMat.SetInt("_EvalMode", _evalMode);                       // 0 world / 1 world→field / 2 rest-UV3→field
            _previewMat.SetMatrix("_WorldToField", WorldToFieldMatrix);
            _previewMat.SetMatrix("_LocalToField", SourceLocalToFieldMatrix(transform));
            _previewMat.SetFloat("_Inflate", Mathf.Max(0f, surfaceOffset));   // lift the shell off the surface (anti z-fight)
            _previewMat.SetFloat("_DebugSolid", debugPreview ? 1f : 0f);
            _previewMat.SetFloat("_PreviewOpacity", Mathf.Clamp01(previewOpacity));
            _previewMat.SetInt("_PreviewStyle", (int)previewStyle);
            _previewMat.SetColor("_PreviewColor", previewColor);
            _previewMat.SetTexture("_MaskTex", previewTexture != null ? previewTexture : Texture2D.whiteTexture);
            _previewMat.SetVector("_MaskTex_ST", new Vector4(previewTextureScale.x, previewTextureScale.y,
                                                             previewTextureOffset.x, previewTextureOffset.y));
            _previewMat.SetFloat("_TextureStrength", Mathf.Clamp01(previewTextureStrength));
        }

        int EnsureSeedVecs()
        {
            EnsureResolved();
            if (_seedVecs == null) _seedVecs = new Vector4[MaxSeeds];
            int n = Mathf.Min(_seedPos.Count, MaxSeeds);
            for (int i = 0; i < n; i++) { Vector3 p = _seedPos[i]; _seedVecs[i] = new Vector4(p.x, p.y, p.z, 0f); }
            return n;
        }
        Vector3 FieldAxis() => anisotropyAxis.sqrMagnitude > 1e-12f ? anisotropyAxis.normalized : Vector3.forward;

        /// <summary>Set the shared mask-FIELD uniforms (the ones in BrazeMaskField.hlsl) on a preview material — the same
        /// values the GPU gate compute reads, so preview and spawn set agree. Each filter's toggle neutralizes its
        /// uniform when off (amount→0, or contrast→1/bias→0) so the field needs no extra branches.</summary>
        public void BindField(Material m)
        {
            int n = EnsureSeedVecs();
            m.SetVectorArray("_Seeds", _seedVecs);
            m.SetInt("_SeedCount", n);
            m.SetInt("_ProjectionMode", (int)projectionMode);
            m.SetInt("_ProjectionPlane", (int)projectionPlane);
            m.SetFloat("_Front", Front);
            m.SetFloat("_Soft", Mathf.Max(softness, 1e-3f));
            m.SetFloat("_Invert", invert ? 1f : 0f);
            m.SetFloat("_Organic", organicEnabled ? Mathf.Clamp01(organicAmount) : 0f);
            m.SetFloat("_OrganicScale", Mathf.Max(1e-3f, organicScale));
            m.SetFloat("_OrganicTime", organicSpeed * Time.time);
            m.SetFloat("_AnisoAmount", anisotropyEnabled ? Mathf.Max(0f, anisotropyAmount) : 0f);
            m.SetVector("_AnisoAxis", FieldAxis());
            m.SetFloat("_RingAmount", ringsEnabled ? Mathf.Clamp01(ringAmount) : 0f);
            m.SetFloat("_RingSpacing", Mathf.Max(1e-3f, ringSpacing));
            m.SetFloat("_RingThickness", Mathf.Clamp01(ringThickness));
            m.SetFloat("_ErodeAmount", erodeEnabled ? Mathf.Clamp01(erodeAmount) : 0f);
            m.SetFloat("_ErodeScale", Mathf.Max(1e-3f, erodeScale));
            m.SetFloat("_Contrast", contrastEnabled ? Mathf.Max(0f, contrast) : 1f);
            m.SetFloat("_Bias", contrastEnabled ? bias : 0f);
        }

        /// <summary>Same field uniforms, on a compute kernel — for the GPU gate (BrazeParticles.compute MaskGate).</summary>
        public void BindField(ComputeShader cs, int kernel)
        {
            int n = EnsureSeedVecs();
            cs.SetVectorArray("_Seeds", _seedVecs);
            cs.SetInt("_SeedCount", n);
            cs.SetInt("_ProjectionMode", (int)projectionMode);
            cs.SetInt("_ProjectionPlane", (int)projectionPlane);
            cs.SetFloat("_Front", Front);
            cs.SetFloat("_Soft", Mathf.Max(softness, 1e-3f));
            cs.SetFloat("_Invert", invert ? 1f : 0f);
            cs.SetFloat("_Organic", organicEnabled ? Mathf.Clamp01(organicAmount) : 0f);
            cs.SetFloat("_OrganicScale", Mathf.Max(1e-3f, organicScale));
            cs.SetFloat("_OrganicTime", organicSpeed * Time.time);
            cs.SetFloat("_AnisoAmount", anisotropyEnabled ? Mathf.Max(0f, anisotropyAmount) : 0f);
            cs.SetVector("_AnisoAxis", FieldAxis());
            cs.SetFloat("_RingAmount", ringsEnabled ? Mathf.Clamp01(ringAmount) : 0f);
            cs.SetFloat("_RingSpacing", Mathf.Max(1e-3f, ringSpacing));
            cs.SetFloat("_RingThickness", Mathf.Clamp01(ringThickness));
            cs.SetFloat("_ErodeAmount", erodeEnabled ? Mathf.Clamp01(erodeAmount) : 0f);
            cs.SetFloat("_ErodeScale", Mathf.Max(1e-3f, erodeScale));
            cs.SetFloat("_Contrast", contrastEnabled ? Mathf.Max(0f, contrast) : 1f);
            cs.SetFloat("_Bias", contrastEnabled ? bias : 0f);
        }

        void OnEnable()
        {
            RebindSeeds();   // auto-bind every seed to its nearest surface vertex against the current pose (no manual step)
            ApplySeedVisibility();
            if (growth && playOnEnable && Application.isPlaying) Trigger();
        }
        void OnValidate()
        {
            surfacePointSpacing = Mathf.Max(0.005f, surfacePointSpacing);
            maxSurfacePoints = Mathf.Clamp(maxSurfacePoints, MinSurfacePoints, 50000);
            previewTextureStrength = Mathf.Clamp01(previewTextureStrength);
        }
        public void OnBrazeAnimated() => OnValidate();
        void OnDisable() { RestoreSeedVisibility(); DestroyPreview(); _seedBinds.Clear(); }   // restore + clear binds → fresh sticky bind on re-enable

        // Seeds are authoring handles — hide their renderers in Play so a placement sphere doesn't show up in-game.
        readonly List<Renderer> _hiddenSeedRenderers = new();
        void ApplySeedVisibility()
        {
            RestoreSeedVisibility();
            if (!hideSeedsInPlay || !Application.isPlaying || seeds == null) return;
            for (int i = 0; i < seeds.Count; i++)
            {
                var s = seeds[i];
                if (s == null) continue;
                var rends = s.GetComponentsInChildren<Renderer>();
                for (int r = 0; r < rends.Length; r++)
                    if (rends[r] != null && rends[r].enabled) { rends[r].enabled = false; _hiddenSeedRenderers.Add(rends[r]); }
            }
        }
        void RestoreSeedVisibility()
        {
            for (int i = 0; i < _hiddenSeedRenderers.Count; i++)
                if (_hiddenSeedRenderers[i] != null) _hiddenSeedRenderers[i].enabled = true;
            _hiddenSeedRenderers.Clear();
        }
        void OnDestroy() => DestroyPreview();
        void DestroyPreview()
        {
            DestroyPreviewProvider();
            if (_previewProviderMesh != null) { DestroyImmediateSafe(_previewProviderMesh); _previewProviderMesh = null; _previewProviderMeshId = int.MinValue; }
            if (_previewMat != null) { if (Application.isPlaying) Destroy(_previewMat); else DestroyImmediate(_previewMat); _previewMat = null; }
        }

#if UNITY_EDITOR
        void OnDrawGizmos()
        {
            EnsureResolved();
            if (debugPreview)
            {
                DrawPreviewNow();
                DrawDebugLabel();
            }
            if (!showPreview || seeds == null) return;
            float front = Front;
            // Authoring gizmo at each seed's bound BIND-POSE location (skinned Local) or its own position. The live
            // on-body region is shown by the GPU preview overlay; this is just the placement marker (no skinning bake).
            bool skinnedLocal = IsLocalSpace && TryGetComponent(out SkinnedMeshRenderer smr) && smr.sharedMesh != null;
            for (int i = 0; i < seeds.Count; i++)
            {
                var s = seeds[i];
                if (s == null) continue;
                Vector3 c = s.position;
                if (skinnedLocal && _seedBinds.TryGetValue(s, out var bind) && bind.vtx >= 0)
                    c = transform.TransformPoint(bind.restPos);
                Gizmos.color = new Color(0.95f, 0.55f, 0.15f, 0.9f);
                Gizmos.DrawSphere(c, 0.03f);                 // a small dot so it's visible even at Reach 0
                Gizmos.color = new Color(0.2f, 0.85f, 1f, 0.5f);
                Gizmos.DrawWireSphere(c, front);             // the region radius (Reach, or the live growth front in Play)
            }
        }

        void DrawPreviewNow()
        {
            var mesh = ResolveMesh();
            if (mesh == null || _seedPos.Count == 0) { _drawState = "no mesh / no seeds"; return; }
            if (!EnsureMaterial()) return;
            _evalMode = !IsLocalSpace ? 0 : (TryGetComponent(out SkinnedMeshRenderer _) ? 2 : 1);
            ConfigureMaterial();
            if (!_previewMat.SetPass(0)) { _drawState = "SetPass failed"; return; }
            Graphics.DrawMeshNow(mesh, transform.localToWorldMatrix);   // bind-pose silhouette (debug solid only)
            _drawState = "drew magenta now";
            _lastDrawFrame = Time.frameCount;
        }

        void DrawDebugLabel()
        {
            var mesh = ResolveMesh();
            Bounds b = mesh != null ? mesh.bounds : new Bounds(Vector3.zero, Vector3.one);
            Vector3 up = transform.TransformVector(Vector3.up);
            if (up.sqrMagnitude < 1e-6f) up = Vector3.up;
            Vector3 labelPos = transform.TransformPoint(b.center) + up.normalized * (Mathf.Max(0.1f, b.extents.y) + 0.2f);

            // EditorStyles.helpBox is a NAMED-style lookup that throws "Unable to use a named GUIStyle without a
            // current skin" when touched with no skin — and OnDrawGizmos (our caller) can run in that state. Fall
            // back to a plain unnamed style when no skin is present so the debug label never throws.
            var style = UnityEngine.GUI.skin != null
                ? new UnityEngine.GUIStyle(UnityEditor.EditorStyles.helpBox) { fontSize = 11, richText = false }
                : new UnityEngine.GUIStyle { fontSize = 11, richText = false };
            style.normal.textColor = Color.white;
            UnityEditor.Handles.Label(labelPos, DebugReport, style);
        }
#endif
    }
}
