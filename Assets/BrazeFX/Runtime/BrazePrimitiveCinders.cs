using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BrazeFX
{
    /// <summary>
    /// Cheap fire/smoke overlay on generated primitive meshes. This does not simulate pyro; it is a procedural
    /// surface shader for places where full dynamic fire would be too expensive.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(10050)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    [AddComponentMenu("BrazeFX/Fire/Primitive Cinders", 6)]
    public sealed class BrazePrimitiveCinders : MonoBehaviour, IBrazeAnimatedTarget
    {
        public enum PrimitiveShape { Cube, Plane, Sphere, Torus, Capsule }
        public enum Axis { X, Y, Z }
        public enum BlendMode { Additive, Alpha, Multiply }

        [Header("Shape")]
        [Tooltip("Generated primitive used as the cheap cinder/fire surface.")]
        public PrimitiveShape primitive = PrimitiveShape.Cube;
        [Tooltip("Local primitive size. Cube uses XYZ, Plane uses XZ, Sphere/Capsule/Torus use this as their local bounds.")]
        public Vector3 size = new Vector3(1f, 1f, 1f);
        [Tooltip("Curved primitive tessellation. Cube and Plane ignore this.")]
        [Range(8, 96)] public int resolution = 32;
        [Tooltip("Torus tube thickness as a fraction of the smaller horizontal radius.")]
        [Range(0.05f, 0.45f)] public float torusTube = 0.22f;

        [Header("Mapping")]
        [Tooltip("Local axis the procedural fire flows along.")]
        public Axis flowAxis = Axis.Y;
        [Tooltip("Compensates transform stretching before sampling the fire field. 1 = pattern keeps roughly world-space density on stretched objects.")]
        [Range(0f, 1f)] public float deStretch = 1f;
        [Tooltip("Extra per-axis scaling for the procedural fire domain after de-stretching.")]
        public Vector3 domainStretch = Vector3.one;
        [Tooltip("Global procedural fire scale. Higher values create smaller, denser detail.")]
        [Range(0.05f, 16f)] public float scale = 3f;

        [Header("Look")]
        [ColorUsage(true, true)] public Color fireColor = new Color(1f, 0.42f, 0.12f, 1f);
        [ColorUsage(true, false)] public Color smokeColor = new Color(0.14f, 0.12f, 0.10f, 0.65f);
        [Range(0f, 8f)] public float intensity = 2.5f;
        [Range(0f, 1f)] public float opacity = 0.85f;
        public BlendMode blendMode = BlendMode.Alpha;

        [Header("Cinders")]
        [Tooltip("Visible flame length along Flow Axis. The primitive Size along that axis is still the maximum physical height.")]
        [Range(0.1f, 2.5f)] public float flameHeight = 1f;
        [Tooltip("How much of the primitive is allowed to burn along Flow Axis. 1 = full primitive.")]
        [Range(0.05f, 1f)] public float coverage = 1f;
        [Range(0f, 8f)] public float speed = 1.2f;
        [Range(1, 6)] public int detail = 4;
        [Range(0.2f, 4f)] public float sharpness = 1.35f;
        [Range(0f, 1f)] public float edgeErosion = 0.25f;
        [Range(0f, 4f)] public float heat = 1.25f;
        [Range(0f, 1f)] public float smoke = 0.35f;
        [Range(0f, 1f)] public float rimFade = 0.15f;
        [Range(0f, 1000f)] public float seed = 0f;

        [Header("Motion")]
        [Tooltip("Makes the procedural fire lag and shear against this transform's world velocity.")]
        public bool motionReactive = true;
        [Tooltip("How strongly source velocity bends the procedural fire. 0 = off.")]
        [Range(0f, 2f)] public float motionInfluence = 0.45f;
        [Tooltip("How quickly the fire catches up to velocity changes.")]
        [Range(0f, 30f)] public float motionResponse = 8f;
        [Tooltip("Caps source speed before it reaches the shader. 0 = no cap.")]
        [Range(0f, 50f)] public float motionMaxSpeed = 12f;
        [Tooltip("Extra lengthening of the procedural flow while the source is moving.")]
        [Range(0f, 2f)] public float motionStretch = 0.4f;

        MeshFilter meshFilter;
        MeshRenderer meshRenderer;
        Mesh mesh;
        Material mat;

        PrimitiveShape lastPrimitive;
        Vector3 lastSize;
        int lastResolution;
        float lastTorusTube;

        Vector3 previousMotionPosition;
        float previousMotionTime;
        Vector3 smoothedDomainVelocity;
        bool hasMotionSample;

        const HideFlags InternalComponentFlags = HideFlags.HideInInspector;
        const HideFlags RuntimeObjectFlags = HideFlags.HideAndDontSave;

        static readonly List<Vector3> Verts = new();
        static readonly List<Vector3> Normals = new();
        static readonly List<Vector2> Uvs = new();
        static readonly List<int> Tris = new();

        static readonly int BoundsSizeId = Shader.PropertyToID("_BoundsSize");
        static readonly int ObjectScaleAbsId = Shader.PropertyToID("_ObjectScaleAbs");
        static readonly int FlowAxisId = Shader.PropertyToID("_FlowAxis");
        static readonly int DeStretchId = Shader.PropertyToID("_DeStretch");
        static readonly int DomainStretchId = Shader.PropertyToID("_DomainStretch");
        static readonly int ScaleId = Shader.PropertyToID("_Scale");
        static readonly int FireColorId = Shader.PropertyToID("_FireColor");
        static readonly int SmokeColorId = Shader.PropertyToID("_SmokeColor");
        static readonly int IntensityId = Shader.PropertyToID("_Intensity");
        static readonly int OpacityId = Shader.PropertyToID("_Opacity");
        static readonly int FlameHeightId = Shader.PropertyToID("_FlameHeight");
        static readonly int CoverageId = Shader.PropertyToID("_Coverage");
        static readonly int SpeedId = Shader.PropertyToID("_Speed");
        static readonly int DetailId = Shader.PropertyToID("_Detail");
        static readonly int SharpnessId = Shader.PropertyToID("_Sharpness");
        static readonly int EdgeId = Shader.PropertyToID("_Edge");
        static readonly int HeatId = Shader.PropertyToID("_Heat");
        static readonly int SmokeId = Shader.PropertyToID("_Smoke");
        static readonly int RimFadeId = Shader.PropertyToID("_RimFade");
        static readonly int SeedId = Shader.PropertyToID("_Seed");
        static readonly int TimeParamId = Shader.PropertyToID("_TimeParam");
        static readonly int MotionVectorId = Shader.PropertyToID("_MotionVector");
        static readonly int MotionAmountId = Shader.PropertyToID("_MotionAmount");
        static readonly int MotionStretchId = Shader.PropertyToID("_MotionStretch");
        static readonly int BlendSrcId = Shader.PropertyToID("_BlendSrc");
        static readonly int BlendDstId = Shader.PropertyToID("_BlendDst");
        static readonly int MultiplyId = Shader.PropertyToID("_Multiply");
        static readonly int ZTestId = Shader.PropertyToID("_ZTest");

        void OnEnable()
        {
            EnsureRuntimeObjects();
            BuildMesh();
            ResetMotionState();
            ApplyMaterial();
#if UNITY_EDITOR
            if (!Application.isPlaying) UnityEditor.EditorApplication.update += EditorTick;
#endif
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorTick;
#endif
            if (meshFilter != null && meshFilter.sharedMesh == mesh) meshFilter.sharedMesh = null;
            DestroyObj(mesh); mesh = null;
            DestroyObj(mat); mat = null;
        }

#if UNITY_EDITOR
        void EditorTick()
        {
            if (this == null || Application.isPlaying || speed <= 0f) return;
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
            UnityEditor.SceneView.RepaintAll();
        }
#endif

        void OnValidate()
        {
            size.x = Mathf.Max(0.001f, size.x);
            size.y = Mathf.Max(0.001f, size.y);
            size.z = Mathf.Max(0.001f, size.z);
            resolution = Mathf.Clamp(resolution, 8, 96);
            torusTube = Mathf.Clamp(torusTube, 0.05f, 0.45f);

            if (primitive == PrimitiveShape.Capsule)
            {
                float lateralDiameter = Mathf.Max(size.x, size.z);
                if (size.y <= lateralDiameter + 0.0001f)
                    size.y = lateralDiameter * 2f;
            }

            deStretch = Mathf.Clamp01(deStretch);
            domainStretch.x = Mathf.Max(0.001f, domainStretch.x);
            domainStretch.y = Mathf.Max(0.001f, domainStretch.y);
            domainStretch.z = Mathf.Max(0.001f, domainStretch.z);
            scale = Mathf.Max(0.05f, scale);
            intensity = Mathf.Max(0f, intensity);
            opacity = Mathf.Clamp01(opacity);
            flameHeight = Mathf.Clamp(flameHeight, 0.1f, 2.5f);
            coverage = Mathf.Clamp(coverage, 0.05f, 1f);
            speed = Mathf.Max(0f, speed);
            detail = Mathf.Clamp(detail, 1, 6);
            sharpness = Mathf.Max(0.05f, sharpness);
            edgeErosion = Mathf.Clamp01(edgeErosion);
            heat = Mathf.Max(0f, heat);
            smoke = Mathf.Clamp01(smoke);
            rimFade = Mathf.Clamp01(rimFade);
            seed = Mathf.Max(0f, seed);
            motionInfluence = Mathf.Clamp(motionInfluence, 0f, 2f);
            motionResponse = Mathf.Clamp(motionResponse, 0f, 30f);
            motionMaxSpeed = Mathf.Clamp(motionMaxSpeed, 0f, 50f);
            motionStretch = Mathf.Clamp(motionStretch, 0f, 2f);

            if (isActiveAndEnabled)
            {
                EnsureRuntimeObjects();
                if (ShapeDirty()) BuildMesh();
                ApplyMaterial();
            }
        }

        public void OnBrazeAnimated() => OnValidate();

        void LateUpdate()
        {
            EnsureRuntimeObjects();
            if (ShapeDirty()) BuildMesh();
            UpdateMotion();
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
                mesh = new Mesh { name = "BrazePrimitiveCinders", hideFlags = RuntimeObjectFlags };
                mesh.MarkDynamic();
                meshFilter.sharedMesh = mesh;
            }
            else mesh.hideFlags = RuntimeObjectFlags;

            if (mat == null)
            {
                var shader = BrazeRuntimeShaderLibrary.Find("BrazeFX/Primitive Cinders");
                if (shader == null)
                {
                    Debug.LogError("[BrazeFX] Shader 'BrazeFX/Primitive Cinders' not found.");
                    enabled = false;
                    return;
                }
                mat = new Material(shader) { hideFlags = RuntimeObjectFlags };
                meshRenderer.sharedMaterial = mat;
                meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
                meshRenderer.receiveShadows = false;
            }
            else mat.hideFlags = RuntimeObjectFlags;
        }

        bool ShapeDirty() =>
            primitive != lastPrimitive ||
            size != lastSize ||
            resolution != lastResolution ||
            !Mathf.Approximately(torusTube, lastTorusTube);

        void BuildMesh()
        {
            if (mesh == null) return;

            Verts.Clear();
            Normals.Clear();
            Uvs.Clear();
            Tris.Clear();

            Vector3 s = new Vector3(Mathf.Max(0.001f, size.x), Mathf.Max(0.001f, size.y), Mathf.Max(0.001f, size.z));
            int seg = Mathf.Clamp(resolution, 8, 96);

            switch (primitive)
            {
                case PrimitiveShape.Plane:
                    BuildPlane(s);
                    break;
                case PrimitiveShape.Sphere:
                    BuildSphere(s, seg);
                    break;
                case PrimitiveShape.Torus:
                    BuildTorus(s, seg, torusTube);
                    break;
                case PrimitiveShape.Capsule:
                    BuildCapsule(s, seg);
                    break;
                default:
                    BuildCube(s);
                    break;
            }

            mesh.Clear(false);
            mesh.SetVertices(Verts);
            mesh.SetNormals(Normals);
            mesh.SetUVs(0, Uvs);
            mesh.SetTriangles(Tris, 0, true);
            mesh.RecalculateBounds();

            lastPrimitive = primitive;
            lastSize = size;
            lastResolution = resolution;
            lastTorusTube = torusTube;
        }

        void ResetMotionState()
        {
            previousMotionPosition = transform.position;
            previousMotionTime = MotionTime();
            smoothedDomainVelocity = Vector3.zero;
            hasMotionSample = true;
        }

        void UpdateMotion()
        {
            Vector3 currentPosition = transform.position;
            float now = MotionTime();

            if (!hasMotionSample)
            {
                previousMotionPosition = currentPosition;
                previousMotionTime = now;
                smoothedDomainVelocity = Vector3.zero;
                hasMotionSample = true;
                return;
            }

            float dt = Mathf.Max(now - previousMotionTime, 0.0001f);

            if (!motionReactive || motionInfluence <= 0f || motionResponse <= 0f)
            {
                smoothedDomainVelocity = Vector3.zero;
                previousMotionPosition = currentPosition;
                previousMotionTime = now;
                return;
            }

            Vector3 worldVelocity = (currentPosition - previousMotionPosition) / dt;
            if (motionMaxSpeed > 0f)
                worldVelocity = Vector3.ClampMagnitude(worldVelocity, motionMaxSpeed);

            Vector3 localVelocity = transform.InverseTransformVector(worldVelocity);
            Vector3 lossy = transform.lossyScale;
            Vector3 objectScale = Vector3.Lerp(
                Vector3.one,
                new Vector3(Mathf.Abs(lossy.x), Mathf.Abs(lossy.y), Mathf.Abs(lossy.z)),
                Mathf.Clamp01(deStretch));
            Vector3 stretch = new Vector3(
                Mathf.Max(0.001f, domainStretch.x),
                Mathf.Max(0.001f, domainStretch.y),
                Mathf.Max(0.001f, domainStretch.z));
            float domainScale = Mathf.Max(0.05f, scale);
            Vector3 domainVelocity = Vector3.Scale(Vector3.Scale(localVelocity, objectScale), stretch) * domainScale;

            float blend = 1f - Mathf.Exp(-motionResponse * dt);
            smoothedDomainVelocity = Vector3.Lerp(smoothedDomainVelocity, domainVelocity, blend);
            previousMotionPosition = currentPosition;
            previousMotionTime = now;
        }

        static float MotionTime() => Application.isPlaying ? Time.time : Time.realtimeSinceStartup;

        void ApplyMaterial()
        {
            if (mat == null) return;

            Vector3 boundsSize = mesh != null ? mesh.bounds.size : size;
            mat.SetVector(BoundsSizeId, new Vector4(
                Mathf.Max(0.001f, boundsSize.x),
                Mathf.Max(0.001f, boundsSize.y),
                Mathf.Max(0.001f, boundsSize.z),
                0f));
            Vector3 lossy = transform.lossyScale;
            mat.SetVector(ObjectScaleAbsId, new Vector4(Mathf.Abs(lossy.x), Mathf.Abs(lossy.y), Mathf.Abs(lossy.z), 0f));
            mat.SetFloat(FlowAxisId, (float)flowAxis);
            mat.SetFloat(DeStretchId, Mathf.Clamp01(deStretch));
            mat.SetVector(DomainStretchId, new Vector4(
                Mathf.Max(0.001f, domainStretch.x),
                Mathf.Max(0.001f, domainStretch.y),
                Mathf.Max(0.001f, domainStretch.z),
                0f));
            mat.SetFloat(ScaleId, Mathf.Max(0.05f, scale));
            mat.SetColor(FireColorId, fireColor);
            mat.SetColor(SmokeColorId, smokeColor);
            mat.SetFloat(IntensityId, Mathf.Max(0f, intensity));
            mat.SetFloat(OpacityId, Mathf.Clamp01(opacity));
            mat.SetFloat(FlameHeightId, Mathf.Clamp(flameHeight, 0.1f, 2.5f));
            mat.SetFloat(CoverageId, Mathf.Clamp(coverage, 0.05f, 1f));
            mat.SetFloat(SpeedId, Mathf.Max(0f, speed));
            mat.SetFloat(DetailId, Mathf.Clamp(detail, 1, 6));
            mat.SetFloat(SharpnessId, Mathf.Max(0.05f, sharpness));
            mat.SetFloat(EdgeId, Mathf.Clamp01(edgeErosion));
            mat.SetFloat(HeatId, Mathf.Max(0f, heat));
            mat.SetFloat(SmokeId, Mathf.Clamp01(smoke));
            mat.SetFloat(RimFadeId, Mathf.Clamp01(rimFade));
            mat.SetFloat(SeedId, Mathf.Max(0f, seed));
            mat.SetFloat(TimeParamId, Application.isPlaying ? Time.time : Time.realtimeSinceStartup);
            mat.SetVector(MotionVectorId, new Vector4(smoothedDomainVelocity.x, smoothedDomainVelocity.y, smoothedDomainVelocity.z, 0f));
            mat.SetFloat(MotionAmountId, motionReactive ? Mathf.Clamp(motionInfluence, 0f, 2f) : 0f);
            mat.SetFloat(MotionStretchId, Mathf.Clamp(motionStretch, 0f, 2f));

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

            mat.SetFloat(BlendSrcId, src);
            mat.SetFloat(BlendDstId, dst);
            mat.SetFloat(MultiplyId, mul);
            mat.SetFloat(ZTestId, (float)CompareFunction.LessEqual);
        }

        static void BuildPlane(Vector3 size)
        {
            float x = size.x * 0.5f;
            float z = size.z * 0.5f;
            int i = Verts.Count;
            Verts.Add(new Vector3(-x, 0f, -z));
            Verts.Add(new Vector3(x, 0f, -z));
            Verts.Add(new Vector3(-x, 0f, z));
            Verts.Add(new Vector3(x, 0f, z));
            for (int n = 0; n < 4; n++) Normals.Add(Vector3.up);
            Uvs.Add(new Vector2(0f, 0f));
            Uvs.Add(new Vector2(1f, 0f));
            Uvs.Add(new Vector2(0f, 1f));
            Uvs.Add(new Vector2(1f, 1f));
            Tris.Add(i + 0); Tris.Add(i + 2); Tris.Add(i + 1);
            Tris.Add(i + 1); Tris.Add(i + 2); Tris.Add(i + 3);
        }

        static void BuildCube(Vector3 size)
        {
            Vector3 h = size * 0.5f;
            AddFace(new Vector3(-h.x, -h.y, h.z), new Vector3(h.x, -h.y, h.z), new Vector3(-h.x, h.y, h.z), new Vector3(h.x, h.y, h.z), Vector3.forward);
            AddFace(new Vector3(h.x, -h.y, -h.z), new Vector3(-h.x, -h.y, -h.z), new Vector3(h.x, h.y, -h.z), new Vector3(-h.x, h.y, -h.z), Vector3.back);
            AddFace(new Vector3(-h.x, -h.y, -h.z), new Vector3(-h.x, -h.y, h.z), new Vector3(-h.x, h.y, -h.z), new Vector3(-h.x, h.y, h.z), Vector3.left);
            AddFace(new Vector3(h.x, -h.y, h.z), new Vector3(h.x, -h.y, -h.z), new Vector3(h.x, h.y, h.z), new Vector3(h.x, h.y, -h.z), Vector3.right);
            AddFace(new Vector3(-h.x, h.y, h.z), new Vector3(h.x, h.y, h.z), new Vector3(-h.x, h.y, -h.z), new Vector3(h.x, h.y, -h.z), Vector3.up);
            AddFace(new Vector3(-h.x, -h.y, -h.z), new Vector3(h.x, -h.y, -h.z), new Vector3(-h.x, -h.y, h.z), new Vector3(h.x, -h.y, h.z), Vector3.down);
        }

        static void AddFace(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
        {
            int i = Verts.Count;
            Verts.Add(a); Verts.Add(b); Verts.Add(c); Verts.Add(d);
            Normals.Add(normal); Normals.Add(normal); Normals.Add(normal); Normals.Add(normal);
            Uvs.Add(new Vector2(0f, 0f));
            Uvs.Add(new Vector2(1f, 0f));
            Uvs.Add(new Vector2(0f, 1f));
            Uvs.Add(new Vector2(1f, 1f));
            Tris.Add(i + 0); Tris.Add(i + 2); Tris.Add(i + 1);
            Tris.Add(i + 1); Tris.Add(i + 2); Tris.Add(i + 3);
        }

        static void BuildSphere(Vector3 size, int seg)
        {
            int lon = Mathf.Max(8, seg);
            int lat = Mathf.Max(4, seg / 2);
            Vector3 r = size * 0.5f;
            for (int y = 0; y <= lat; y++)
            {
                float v = y / (float)lat;
                float phi = Mathf.Lerp(-Mathf.PI * 0.5f, Mathf.PI * 0.5f, v);
                float cp = Mathf.Cos(phi);
                float sp = Mathf.Sin(phi);
                for (int x = 0; x <= lon; x++)
                {
                    float u = x / (float)lon;
                    float th = u * Mathf.PI * 2f;
                    Vector3 n = new Vector3(Mathf.Cos(th) * cp, sp, Mathf.Sin(th) * cp);
                    Verts.Add(new Vector3(n.x * r.x, n.y * r.y, n.z * r.z));
                    Normals.Add(n.normalized);
                    Uvs.Add(new Vector2(u, v));
                }
            }
            AddGridTriangles(lon, lat);
        }

        static void BuildTorus(Vector3 size, int seg, float tubeRatio)
        {
            int majorSeg = Mathf.Max(16, seg);
            int tubeSeg = Mathf.Max(10, seg / 2);
            float outerX = Mathf.Max(0.001f, size.x * 0.5f);
            float outerZ = Mathf.Max(0.001f, size.z * 0.5f);
            float minOuter = Mathf.Min(outerX, outerZ);
            float tubeHorizontal = Mathf.Max(0.001f, minOuter * Mathf.Clamp(tubeRatio, 0.05f, 0.45f));
            float tubeVertical = Mathf.Max(0.001f, Mathf.Min(size.y * 0.5f, tubeHorizontal));
            float majorX = Mathf.Max(0.001f, outerX - tubeHorizontal);
            float majorZ = Mathf.Max(0.001f, outerZ - tubeHorizontal);

            for (int u = 0; u <= majorSeg; u++)
            {
                float uf = u / (float)majorSeg;
                float a = uf * Mathf.PI * 2f;
                float ca = Mathf.Cos(a);
                float sa = Mathf.Sin(a);
                Vector3 radial = new Vector3(ca, 0f, sa);
                Vector3 center = new Vector3(majorX * ca, 0f, majorZ * sa);
                for (int v = 0; v <= tubeSeg; v++)
                {
                    float vf = v / (float)tubeSeg;
                    float b = vf * Mathf.PI * 2f;
                    float cb = Mathf.Cos(b);
                    float sb = Mathf.Sin(b);

                    Verts.Add(center + new Vector3(radial.x * cb * tubeHorizontal, sb * tubeVertical, radial.z * cb * tubeHorizontal));
                    Normals.Add(new Vector3(
                        radial.x * cb / tubeHorizontal,
                        sb / tubeVertical,
                        radial.z * cb / tubeHorizontal).normalized);
                    Uvs.Add(new Vector2(uf, vf));
                }
            }
            AddGridTriangles(tubeSeg, majorSeg);
        }

        static void BuildCapsule(Vector3 size, int seg)
        {
            int lon = Mathf.Max(8, seg);
            int hemi = Mathf.Max(6, seg / 3);
            int cyl = Mathf.Max(2, seg / 6);
            float rx = size.x * 0.5f;
            float rz = size.z * 0.5f;
            float radiusY = Mathf.Min(size.y * 0.5f, Mathf.Min(rx, rz));
            float cylHalf = Mathf.Max(0f, size.y * 0.5f - radiusY);

            var rings = new List<CapsuleRing>();
            for (int i = 0; i <= hemi; i++)
            {
                float t = i / (float)hemi;
                float a = Mathf.Lerp(-Mathf.PI * 0.5f, 0f, t);
                rings.Add(new CapsuleRing(-cylHalf + Mathf.Sin(a) * radiusY, Mathf.Cos(a), Mathf.Cos(a), Mathf.Sin(a)));
            }
            for (int i = 1; i < cyl; i++)
            {
                float t = i / (float)cyl;
                rings.Add(new CapsuleRing(Mathf.Lerp(-cylHalf, cylHalf, t), 1f, 1f, 0f));
            }
            for (int i = 0; i <= hemi; i++)
            {
                float t = i / (float)hemi;
                float a = Mathf.Lerp(0f, Mathf.PI * 0.5f, t);
                rings.Add(new CapsuleRing(cylHalf + Mathf.Sin(a) * radiusY, Mathf.Cos(a), Mathf.Cos(a), Mathf.Sin(a)));
            }

            int rows = rings.Count - 1;
            for (int r = 0; r < rings.Count; r++)
            {
                float v = rows > 0 ? r / (float)rows : 0f;
                var ring = rings[r];
                for (int x = 0; x <= lon; x++)
                {
                    float u = x / (float)lon;
                    float th = u * Mathf.PI * 2f;
                    float c = Mathf.Cos(th);
                    float s = Mathf.Sin(th);
                    Verts.Add(new Vector3(c * rx * ring.sx, ring.y, s * rz * ring.sz));
                    Normals.Add(new Vector3(
                        c * ring.sx / Mathf.Max(rx, 0.001f),
                        ring.ny / Mathf.Max(radiusY, 0.001f),
                        s * ring.sz / Mathf.Max(rz, 0.001f)).normalized);
                    Uvs.Add(new Vector2(u, v));
                }
            }
            AddGridTriangles(lon, rows);
        }

        readonly struct CapsuleRing
        {
            public readonly float y;
            public readonly float sx;
            public readonly float sz;
            public readonly float ny;

            public CapsuleRing(float y, float sx, float sz, float ny)
            {
                this.y = y;
                this.sx = sx;
                this.sz = sz;
                this.ny = ny;
            }
        }

        static void AddGridTriangles(int cols, int rows)
        {
            int rowStride = cols + 1;
            int start = Verts.Count - rowStride * (rows + 1);
            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    int i00 = start + y * rowStride + x;
                    int i01 = i00 + 1;
                    int i10 = i00 + rowStride;
                    int i11 = i10 + 1;
                    Tris.Add(i00); Tris.Add(i10); Tris.Add(i01);
                    Tris.Add(i01); Tris.Add(i10); Tris.Add(i11);
                }
            }
        }

        static void DestroyObj(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o);
            else DestroyImmediate(o);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.45f, 0.14f, 0.85f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(Vector3.zero, size);
        }
    }
}
