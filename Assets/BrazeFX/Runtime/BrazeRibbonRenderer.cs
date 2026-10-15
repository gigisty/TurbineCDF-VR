using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BrazeFX
{
    /// <summary>
    /// Dedicated sword / weapon slash ribbon. It records a set of local control points over time and builds the
    /// swept surface directly from that transform history, independent of particles.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [AddComponentMenu("BrazeFX/Fire/Braze Ribbon Renderer", 4)]
    public sealed class BrazeRibbonRenderer : MonoBehaviour, IBrazeAnimatedTarget
    {
        public enum ColorMode { FireSmoke, Gradient, Constant }
        public enum BlendMode { Additive, Alpha, Multiply }

        [Header("Shape")]
        [Tooltip("Sword-local points swept through time. Use two points for blade base/tip; add more to shape a wider profile.")]
        public List<Vector3> localPoints = new List<Vector3>
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(0f, 0f, 1f),
        };
        [Tooltip("How many history rows make up the swept surface. Higher = smoother arcs.")]
        [Range(2, 128)] public int historySamples = 48;
        [Tooltip("Seconds of transform history shown behind the current sword pose. Longer values make longer ribbons; world length also depends on swing speed.")]
        [BrazeAnimatable("RIBBON/Time", 0.02f, 2f)]
        [Range(0.02f, 2f)] public float ribbonTime = 0.35f;
        [Tooltip("Scales the local point profile around its centreline. 1 = use the points exactly.")]
        [BrazeAnimatable("RIBBON/Width", 0f, 4f)]
        [Min(0f)] public float widthScale = 1f;
        [Tooltip("Profile scale over ribbon history: left = current sword pose, right = oldest tail.")]
        public AnimationCurve widthOverTime = DefaultWidthOverTime();

        [Header("Preview")]
        [Tooltip("Edit-mode preview card for tuning fire, colour, fade, and blend settings without swinging the weapon.")]
        public bool preview = false;
        [Tooltip("Edit-mode preview card depth in local units. 0 = auto from the Local Points length.")]
        [Min(0f)] public float previewLength = 0f;

        [Header("Shading")]
        public ColorMode colorMode = ColorMode.FireSmoke;
        [GradientUsage(true)] public Gradient ramp = DefaultRamp();
        [ColorUsage(true, true)] public Color constantColor = new Color(1f, 0.55f, 0.18f, 1f);
        [ColorUsage(true, true)] public Color fireColor = new Color(1f, 0.5f, 0.12f, 1f);
        [ColorUsage(true, true)] public Color smokeColor = new Color(0.18f, 0.18f, 0.20f, 1f);
        [Range(0f, 1f)] public float colorMid = 0.45f;
        [BrazeAnimatable("SHADING/Emissive", 0f, 8f)]
        [Min(0f)] public float emissive = 2f;
        [BrazeAnimatable("SHADING/Opacity", 0f, 1f)]
        [Range(0f, 1f)] public float opacity = 1f;
        [Tooltip("Alpha over ribbon history: left = current sword pose, right = oldest tail.")]
        public AnimationCurve lengthAlpha = DefaultLengthAlphaCurve();
        [Range(0f, 0.5f)] public float softEdge = 0.18f;
        public BlendMode blendMode = BlendMode.Additive;

        [Header("Flame")]
        [Range(0f, 1f)] public float fireAmount = 0.35f;
        [Range(0.25f, 16f)] public float fireScale = 4f;
        [Range(0f, 8f)] public float fireSpeed = 1.5f;
        [Range(1, 5)] public int fireDetail = 4;
        [Range(0.2f, 4f)] public float fireSharpness = 1.2f;
        [Range(0f, 1f)] public float fireEdge = 0.25f;
        [Range(0f, 4f)] public float fireHeat = 1.5f;

        MeshFilter meshFilter;
        MeshRenderer meshRenderer;
        Mesh mesh;
        Material mat;
        Texture2D rampLut;
        Texture2D lengthAlphaLut;
        bool rampDirty = true;
        bool lengthAlphaDirty = true;
        bool widthDirty = true;

        sealed class RibbonSample
        {
            public float time;
            public readonly Vector3[] points;

            public RibbonSample(float time, int pointCount)
            {
                this.time = time;
                points = new Vector3[pointCount];
            }
        }

        readonly List<RibbonSample> samples = new List<RibbonSample>();
        readonly List<Vector3> verts = new List<Vector3>();
        readonly List<Vector2> uvs = new List<Vector2>();
        readonly List<int> tris = new List<int>();
        float lastSampleTime;
        int lastPointCount;

        static readonly int IdRamp = Shader.PropertyToID("_Ramp");
        static readonly int IdLengthAlpha = Shader.PropertyToID("_LengthAlphaTex");
        const HideFlags InternalComponentFlags = HideFlags.HideInInspector;
        const HideFlags RuntimeObjectFlags = HideFlags.HideAndDontSave;

        void OnEnable()
        {
            BrazeNoiseTex.Bind();   // globals reset on domain reload; the material may survive — rebind always
            EnsureRuntimeObjects();
            ResetHistory();
        }

        void OnDisable()
        {
            // Pooled projectiles disable/enable this component every shot. Tearing the mesh, material and LUTs
            // down here churned four objects per round AND left the re-created LUTs unbaked. Keep them alive for
            // the component's lifetime; OnDestroy owns the teardown.
            ClearMesh();
            samples.Clear();
        }

        void OnDestroy()
        {
            if (meshFilter != null && meshFilter.sharedMesh == mesh) meshFilter.sharedMesh = null;
            DestroyObj(mesh); mesh = null;
            DestroyObj(mat); mat = null;
            DestroyObj(rampLut); rampLut = null;
            DestroyObj(lengthAlphaLut); lengthAlphaLut = null;
        }

        void OnValidate()
        {
            historySamples = Mathf.Clamp(historySamples, 2, 128);
            ribbonTime = Mathf.Clamp(ribbonTime, 0.02f, 2f);
            widthScale = Mathf.Max(0f, widthScale);
            previewLength = Mathf.Max(0f, previewLength);
            if (widthOverTime == null) widthOverTime = DefaultWidthOverTime();
            if (lengthAlpha == null) lengthAlpha = DefaultLengthAlphaCurve();
            fireDetail = Mathf.Clamp(fireDetail, 1, 5);
            rampDirty = true;
            lengthAlphaDirty = true;
            widthDirty = true;
        }

        public void OnBrazeAnimated() => OnValidate();

        [BrazeTimelineEvent("RIBBON/Reset History")]
        public void ResetHistory()
        {
            samples.Clear();
            float now = CurrentTime();
            lastSampleTime = now;
            lastPointCount = Mathf.Max(0, localPoints != null ? localPoints.Count : 0);
            if (lastPointCount >= 2)
            {
                samples.Add(CaptureSample(now));
                samples.Add(CaptureSample(now));
            }
        }

        void LateUpdate()
        {
            EnsureRuntimeObjects();
            int pointCount = localPoints != null ? localPoints.Count : 0;
            if (pointCount < 2)
            {
                ClearMesh();
                return;
            }

            if (pointCount != lastPointCount || widthDirty)
            {
                ResetHistory();
                widthDirty = false;
            }

            BakeLutsIfNeeded();

            if (!Application.isPlaying)
            {
                if (preview) BuildPreviewMesh();
                else
                {
                    ResetHistory();
                    ClearMesh();
                }
                ApplyMaterial();
                return;
            }
            else
            {
                float now = CurrentTime();
                float interval = SampleInterval();
                if (samples.Count == 0)
                {
                    samples.Add(CaptureSample(now));
                    lastSampleTime = now;
                }

                if (now - lastSampleTime >= interval)
                {
                    samples.Insert(0, CaptureSample(now));
                    lastSampleTime = now;
                }
                else
                {
                    samples[0].time = now;
                    CaptureWorldPoints(samples[0].points);
                }

                TrimHistory(now);
            }

            BuildMesh();
            ApplyMaterial();
        }

        void EnsureRuntimeObjects()
        {
            if (meshFilter == null) meshFilter = GetComponent<MeshFilter>();
            if (meshRenderer == null) meshRenderer = GetComponent<MeshRenderer>();

            meshFilter.hideFlags = InternalComponentFlags;
            meshRenderer.hideFlags = InternalComponentFlags;

            if (mesh == null)
            {
                mesh = new Mesh { name = "BrazeRibbon", hideFlags = RuntimeObjectFlags };
                mesh.MarkDynamic();
                meshFilter.sharedMesh = mesh;
            }
            else mesh.hideFlags = RuntimeObjectFlags;

            if (mat == null)
            {
                var shader = BrazeRuntimeShaderLibrary.Find("BrazeFX/Ribbon");
                if (shader == null)
                {
                    Debug.LogError("[BrazeFX] Shader 'BrazeFX/Ribbon' not found.");
                    enabled = false;
                    return;
                }
                mat = new Material(shader) { hideFlags = RuntimeObjectFlags };
                BrazeNoiseTex.Bind();   // baked base noise for the fire fbm (global texture)
                meshRenderer.sharedMaterial = mat;
                meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
                meshRenderer.receiveShadows = false;
            }
            else mat.hideFlags = RuntimeObjectFlags;

            // CreateRampLut/CreateCurveLut hand back uninitialised texture memory, so a fresh LUT is only valid
            // once BakeLutsIfNeeded has run over it. Dirty on creation, never on re-enable.
            if (rampLut == null)
            {
                rampLut = BrazeTransferFunction.CreateRampLut("BrazeRibbonRamp");
                rampDirty = true;
            }
            if (lengthAlphaLut == null)
            {
                lengthAlphaLut = BrazeTransferFunction.CreateCurveLut("BrazeRibbonLengthAlpha");
                lengthAlphaDirty = true;
            }
        }

        float CurrentTime()
        {
            return Application.isPlaying ? Time.time : Time.realtimeSinceStartup;
        }

        float SampleInterval()
        {
            return ribbonTime / Mathf.Max(1, historySamples - 1);
        }

        RibbonSample CaptureSample(float time)
        {
            var sample = new RibbonSample(time, localPoints.Count);
            CaptureWorldPoints(sample.points);
            return sample;
        }

        void TrimHistory(float now)
        {
            while (samples.Count > historySamples)
                samples.RemoveAt(samples.Count - 1);

            float oldest = now - ribbonTime;
            while (samples.Count > 1 && samples[samples.Count - 1].time < oldest)
                samples.RemoveAt(samples.Count - 1);

            if (samples.Count == 1)
                samples.Add(CaptureSample(samples[0].time));
        }

        void CaptureWorldPoints(Vector3[] dst)
        {
            int n = Mathf.Min(dst.Length, localPoints.Count);
            for (int i = 0; i < n; i++)
                dst[i] = transform.TransformPoint(localPoints[i]);
        }

        void BuildMesh()
        {
            int rows = samples.Count;
            int cols = localPoints.Count;
            if (rows < 2 || cols < 2)
            {
                ClearMesh();
                return;
            }

            verts.Clear();
            uvs.Clear();
            tris.Clear();

            float now = CurrentTime();
            for (int r = 0; r < rows; r++)
            {
                float t = Application.isPlaying
                    ? Mathf.Clamp01((now - samples[r].time) / Mathf.Max(0.001f, ribbonTime))
                    : r / (float)Mathf.Max(1, rows - 1);
                Vector3 centre = Vector3.zero;
                var row = samples[r].points;
                for (int c = 0; c < cols; c++)
                    centre += transform.InverseTransformPoint(row[c]);
                centre /= cols;

                float profile = widthScale * Mathf.Max(0f, widthOverTime.Evaluate(t));
                for (int c = 0; c < cols; c++)
                {
                    Vector3 local = transform.InverseTransformPoint(row[c]);
                    verts.Add(centre + (local - centre) * profile);
                    uvs.Add(new Vector2(t, c / (float)(cols - 1)));
                }
            }

            for (int r = 0; r < rows - 1; r++)
            {
                for (int c = 0; c < cols - 1; c++)
                {
                    int i00 = r * cols + c;
                    int i01 = i00 + 1;
                    int i10 = (r + 1) * cols + c;
                    int i11 = i10 + 1;
                    tris.Add(i00); tris.Add(i10); tris.Add(i01);
                    tris.Add(i01); tris.Add(i10); tris.Add(i11);
                }
            }

            mesh.Clear(false);
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0, true);
            mesh.RecalculateBounds();
            if (meshRenderer != null) meshRenderer.enabled = true;
        }

        void BuildPreviewMesh()
        {
            int cols = localPoints.Count;
            int rows = Mathf.Clamp(historySamples, 2, 128);
            if (cols < 2)
            {
                ClearMesh();
                return;
            }

            verts.Clear();
            uvs.Clear();
            tris.Clear();

            float depth = PreviewDepth();
            Vector3 sweep = PreviewSweepDirection() * depth;
            for (int r = 0; r < rows; r++)
            {
                float t = r / (float)Mathf.Max(1, rows - 1);
                Vector3 rowOffset = sweep * t;
                Vector3 centre = Vector3.zero;
                for (int c = 0; c < cols; c++)
                    centre += localPoints[c] + rowOffset;
                centre /= cols;

                float profile = widthScale * Mathf.Max(0f, widthOverTime.Evaluate(t));
                for (int c = 0; c < cols; c++)
                {
                    Vector3 local = localPoints[c] + rowOffset;
                    verts.Add(centre + (local - centre) * profile);
                    uvs.Add(new Vector2(t, c / (float)(cols - 1)));
                }
            }

            for (int r = 0; r < rows - 1; r++)
            {
                for (int c = 0; c < cols - 1; c++)
                {
                    int i00 = r * cols + c;
                    int i01 = i00 + 1;
                    int i10 = (r + 1) * cols + c;
                    int i11 = i10 + 1;
                    tris.Add(i00); tris.Add(i10); tris.Add(i01);
                    tris.Add(i01); tris.Add(i10); tris.Add(i11);
                }
            }

            mesh.Clear(false);
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0, true);
            mesh.RecalculateBounds();
            if (meshRenderer != null) meshRenderer.enabled = true;
        }

        float PreviewDepth()
        {
            if (previewLength > 1e-4f) return previewLength;

            float len = 0f;
            for (int i = 1; i < localPoints.Count; i++)
                len += Vector3.Distance(localPoints[i - 1], localPoints[i]);
            return Mathf.Max(0.1f, len);
        }

        Vector3 PreviewSweepDirection()
        {
            Vector3 profile = localPoints[localPoints.Count - 1] - localPoints[0];
            if (profile.sqrMagnitude < 1e-6f) profile = Vector3.forward;
            profile.Normalize();

            Vector3 best = Vector3.right;
            float bestDot = Mathf.Abs(Vector3.Dot(profile, best));
            float dot = Mathf.Abs(Vector3.Dot(profile, Vector3.up));
            if (dot < bestDot)
            {
                best = Vector3.up;
                bestDot = dot;
            }
            dot = Mathf.Abs(Vector3.Dot(profile, Vector3.forward));
            if (dot < bestDot) best = Vector3.forward;
            return -best;
        }

        void ClearMesh()
        {
            if (mesh != null) mesh.Clear(false);
            if (meshRenderer != null) meshRenderer.enabled = false;
        }

        void BakeLutsIfNeeded()
        {
            if (rampDirty && rampLut != null)
            {
                BrazeTransferFunction.BakeRamp(rampLut, ramp);
                rampDirty = false;
            }
            if (lengthAlphaDirty && lengthAlphaLut != null)
            {
                BrazeTransferFunction.BakeCurve1D(lengthAlphaLut, lengthAlpha);
                lengthAlphaDirty = false;
            }
        }

        void ApplyMaterial()
        {
            if (mat == null) return;
            mat.SetFloat("_ColorMode", (float)colorMode);
            if (rampLut != null) mat.SetTexture(IdRamp, rampLut);
            if (lengthAlphaLut != null) mat.SetTexture(IdLengthAlpha, lengthAlphaLut);
            mat.SetColor("_ConstColor", constantColor);
            mat.SetColor("_FireColor", fireColor);
            mat.SetColor("_SmokeColor", smokeColor);
            mat.SetFloat("_ColorMid", colorMid);
            mat.SetFloat("_Emissive", Mathf.Max(0f, emissive));
            mat.SetFloat("_Opacity", Mathf.Clamp01(opacity));
            mat.SetFloat("_SoftEdge", softEdge);
            mat.SetFloat("_FireAmount", Mathf.Clamp01(fireAmount));
            mat.SetFloat("_FireScale", Mathf.Max(0.05f, fireScale));
            mat.SetFloat("_FireSpeed", Mathf.Max(0f, fireSpeed));
            mat.SetFloat("_FireDetail", Mathf.Clamp(fireDetail, 1, 5));
            mat.SetFloat("_FireSharp", Mathf.Max(0.05f, fireSharpness));
            mat.SetFloat("_FireEdge", Mathf.Max(0f, fireEdge));
            mat.SetFloat("_FireHeat", Mathf.Max(0f, fireHeat));
            mat.SetFloat("_FireTime", Application.isPlaying ? Time.time : Time.realtimeSinceStartup);
            mat.SetFloat("_FireSeed", StableUnitHash(this.BrazeInstanceId()));

            int src, dst, mul;
            switch (blendMode)
            {
                case BlendMode.Alpha:
                    src = (int)UnityEngine.Rendering.BlendMode.One;
                    dst = (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha;
                    mul = 0;
                    break;
                case BlendMode.Multiply:
                    src = (int)UnityEngine.Rendering.BlendMode.DstColor;
                    dst = (int)UnityEngine.Rendering.BlendMode.Zero;
                    mul = 1;
                    break;
                default:
                    src = (int)UnityEngine.Rendering.BlendMode.One;
                    dst = (int)UnityEngine.Rendering.BlendMode.One;
                    mul = 0;
                    break;
            }
            mat.SetFloat("_BlendSrc", src);
            mat.SetFloat("_BlendDst", dst);
            mat.SetFloat("_ZTest", (float)CompareFunction.LessEqual);
            mat.SetFloat("_Multiply", mul);
        }

        static Gradient DefaultRamp()
        {
            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1.0f, 0.82f, 0.38f), 0.0f),
                    new GradientColorKey(new Color(1.0f, 0.35f, 0.08f), 0.45f),
                    new GradientColorKey(new Color(0.16f, 0.16f, 0.18f), 1.0f),
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return g;
        }

        static AnimationCurve DefaultWidthOverTime() =>
            BrazeTransferFunction.LinearCurve(
                new Vector2(0f, 1f),
                new Vector2(0.72f, 0.85f),
                new Vector2(1f, 0.15f));

        static AnimationCurve DefaultLengthAlphaCurve() =>
            BrazeTransferFunction.LinearCurve(
                new Vector2(0f, 1f),
                new Vector2(0.58f, 0.75f),
                new Vector2(0.86f, 0.25f),
                new Vector2(1f, 0f));

        static float StableUnitHash(int seed)
        {
            uint h = unchecked((uint)seed) ^ 0x9E3779B9u;
            h ^= h >> 16;
            h *= 0x7FEB352Du;
            h ^= h >> 15;
            h *= 0x846CA68Bu;
            h ^= h >> 16;
            return (h & 0x00FFFFFFu) / 16777216f;
        }

        static void DestroyObj(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        void OnDrawGizmosSelected()
        {
            if (localPoints == null || localPoints.Count == 0) return;
            Gizmos.color = new Color(1f, 0.45f, 0.14f, 0.9f);
            for (int i = 0; i < localPoints.Count; i++)
            {
                Vector3 p = transform.TransformPoint(localPoints[i]);
                Gizmos.DrawSphere(p, 0.025f);
                if (i > 0) Gizmos.DrawLine(transform.TransformPoint(localPoints[i - 1]), p);
            }
        }
    }
}
