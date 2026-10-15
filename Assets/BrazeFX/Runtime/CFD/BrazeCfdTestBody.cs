using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX.CFD
{
    public enum BrazeCfdMaterialPreset
    {
        Custom = 0,
        Aluminum = 1,
        Steel = 2,
        Plastic = 3,
        Glass = 4,
        Wood = 5,
        Insulation = 6,
    }

    /// <summary>
    /// Simple-authoring facade for a mesh under test. It owns hidden SDF and surface-pressure adapters and stores
    /// thermal material intent for the upcoming solid heat-transfer phase without pretending it is active today.
    /// </summary>
    [AddComponentMenu("BrazeFX/CFD/Test Body", 2)]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10020)]   // submit the animated SDF preview after BrazeMotion and solver sampling
    public sealed class BrazeCfdTestBody : MonoBehaviour
    {
        public static readonly List<BrazeCfdTestBody> Active = new();

        [Header("Target")]
        public BrazeCfdDomain domain;
        [Tooltip("Static triangle mesh that collides with the wind and receives the result overlay.")]
        public MeshFilter meshSource;
        public BrazeCfdSdfBoundaryMode boundaryMode = BrazeCfdSdfBoundaryMode.SolidInterior;
        [Min(0.001f)] public float shellThickness = 0.05f;
        [Min(0f)] public float collisionOffset = 0.005f;
        [Tooltip("Rigid-motion animation (propellers, fans, doors): the collision SDF follows the Mesh Source " +
                 "transform (including animated parents) every solver tick and the surface's rigid velocity pushes " +
                 "and swirls the air. Deforming or skinned meshes are not supported.")]
        public bool animatedBody;

        [Header("Material Intent")]
        [Tooltip("Reference material stored with the study. Material heat response is enabled in the next phase.")]
        public BrazeCfdMaterialPreset material = BrazeCfdMaterialPreset.Aluminum;
        [Tooltip("Starting surface temperature recorded for the future solid thermal solve.")]
        public float initialTemperatureC = 20f;
        [Min(0.0001f), Tooltip("Representative material thickness recorded for future thermal response.")]
        public float materialThickness = 0.003f;

        [Header("Surface Results")]
        public bool showSurfacePressure = true;
        [Tooltip("Blue endpoint in inlet-speed-normalized comparative pressure units.")]
        public float suctionAt = -1f;
        [Tooltip("Red endpoint in inlet-speed-normalized comparative pressure units.")]
        public float pressureAt = 1f;
        [ColorUsage(false, true)] public Color suctionColor = new(0.03f, 0.22f, 1f, 1f);
        [ColorUsage(false, true)] public Color neutralColor = new(0.12f, 0.85f, 0.45f, 1f);
        [ColorUsage(false, true)] public Color pressureColor = new(1f, 0.08f, 0.02f, 1f);
        [Range(0f, 1f)] public float overlayOpacity = 0.85f;
        [Min(0f), Tooltip("World-space visual separation from the mesh. Field sampling is automatically moved far " +
                          "enough outside the solid for the current CFD cell size.")]
        public float overlayOffset = 0.01f;
        public bool showGizmo = true;

        [Header("Collision Preview")]
        [Tooltip("Raymarch the baked collision SDF as a translucent shell: exactly the triangle-sampled wall the solver " +
                 "collides with, including Collision Offset and Surface Shell settings. Expensive debug view — " +
                 "disable it for FPS captures and shipping demos.")]
        public bool showCollisionSdf;
        [ColorUsage(false, true)] public Color collisionSdfColor = new(1f, 0.45f, 0.05f, 1f);
        [Range(0.05f, 1f)] public float collisionSdfOpacity = 0.55f;

        [SerializeField, HideInInspector] BrazeCfdSdfBoundary sdfBoundary;
        [SerializeField, HideInInspector] BrazeCfdSurfacePressure surfacePressure;
        static Mesh sdfPreviewCube;
        Material sdfPreviewMaterial;
        MaterialPropertyBlock sdfPreviewProperties;

        bool synchronizing;
#if UNITY_EDITOR
        bool editorSyncQueued;
#endif
        int configurationHash;
        string status = "Assign a CFD Domain and Mesh Source.";

        public string Status => status;
        public BrazeCfdSdfBoundary ManagedSdfBoundary => sdfBoundary;
        public BrazeCfdSurfacePressure ManagedSurfacePressure => surfacePressure;
        public bool ThermalMaterialActive => false;
        public Vector3 RelativePressureLoadWorld => domain != null ? domain.RelativePressureLoadWorld : Vector3.zero;
        public Vector3 WindDirectionWorld
        {
            get
            {
                BrazeCfdWindTunnel tunnel = FindTunnel();
                return tunnel != null ? tunnel.WindDirectionWorld
                    : domain != null && domain.Container != null ? domain.Container.transform.forward : transform.forward;
            }
        }
        public float RelativeDragLoad => Vector3.Dot(RelativePressureLoadWorld, WindDirectionWorld.normalized);
        public float RelativeLiftLoad
        {
            get
            {
                Vector3 up = domain != null && domain.Container != null
                    ? domain.Container.transform.up : Vector3.up;
                return Vector3.Dot(RelativePressureLoadWorld, up);
            }
        }

        void Reset()
        {
            ResolveReferences();
            if (domain != null) initialTemperatureC = domain.ambientTemperatureC;
            SyncManagedComponents(true);
        }

        void OnEnable()
        {
            ResolveReferences();
            BrazeRuntimeRegistry.AddUnique(Active, this);
            SyncManagedComponents(true);
        }

        void OnDisable()
        {
            BrazeRuntimeRegistry.Remove(Active, this);
            if (sdfBoundary != null && sdfBoundary.managedByTestBody == this) sdfBoundary.enabled = false;
            if (surfacePressure != null && surfacePressure.managedByTestBody == this) surfacePressure.enabled = false;
            ReleaseSdfPreviewRuntime();
        }

        void OnDestroy()
        {
            BrazeRuntimeRegistry.Remove(Active, this);
            DestroyManaged(ref surfacePressure);
            DestroyManaged(ref sdfBoundary);
            ReleaseSdfPreviewRuntime();
        }

        void OnValidate()
        {
            shellThickness = Mathf.Max(0.001f, shellThickness);
            collisionOffset = Mathf.Max(0f, collisionOffset);
            materialThickness = Mathf.Max(0.0001f, materialThickness);
            pressureAt = Mathf.Max(suctionAt + 0.001f, pressureAt);
            overlayOpacity = Mathf.Clamp01(overlayOpacity);
            overlayOffset = Mathf.Max(0f, overlayOffset);
            ResolveReferences();
            if (isActiveAndEnabled)
            {
                BrazeRuntimeRegistry.AddUnique(Active, this);
                SyncManagedComponents(false);
#if UNITY_EDITOR
                QueueEditorSync();
#endif
            }
        }

#if UNITY_EDITOR
        void QueueEditorSync()
        {
            if (editorSyncQueued) return;
            editorSyncQueued = true;
            UnityEditor.EditorApplication.delayCall += () =>
            {
                editorSyncQueued = false;
                if (this != null && isActiveAndEnabled) SyncManagedComponents(true);
            };
        }
#endif

        void LateUpdate()
        {
            int hash = ComputeConfigurationHash();
            if (hash != configurationHash) SyncManagedComponents(true);
            UpdateSurfacePressureAxes();
            DrawCollisionPreview();
        }

        // Debug shell over the body's voxel window: sphere-traces the baked SDF's zero isosurface. Shows the wall
        // the SOLVER actually collides with — triangle distance, offset inflation, and shell thickness included — so a
        // mismatch against the authored mesh is visible at a glance.
        void DrawCollisionPreview()
        {
            if (!showCollisionSdf || domain == null || !domain.isActiveAndEnabled) return;
            BrazeContainer container = domain.Container;
            if (container == null) return;

            // Animated bodies live in their own mesh-local bake; preview it through the live transform so the
            // shell visibly follows (and rotates with) the body.
            if (animatedBody && sdfBoundary != null && sdfBoundary.animated)
            {
                if (!domain.EnsureAnimatedSdf(sdfBoundary, out RenderTexture bodySdf,
                        out Vector3 localMin, out Vector3 localSize) || !EnsureSdfPreviewRuntime()) return;
                Transform meshTransform = meshSource != null ? meshSource.transform : transform;
                Matrix4x4 uvwFromWorld = Matrix4x4.Scale(new Vector3(1f / localSize.x, 1f / localSize.y,
                                                                     1f / localSize.z))
                                       * Matrix4x4.Translate(-localMin)
                                       * (Matrix4x4.Scale(meshTransform.lossyScale) * meshTransform.worldToLocalMatrix);
                Matrix4x4 proxyMatrix = Matrix4x4.TRS(meshTransform.position, meshTransform.rotation, Vector3.one)
                                      * Matrix4x4.TRS(localMin + localSize * 0.5f, Quaternion.identity, localSize);
                float bodyCell = Mathf.Min(localSize.x / Mathf.Max(1, bodySdf.width),
                                 Mathf.Min(localSize.y / Mathf.Max(1, bodySdf.height),
                                           localSize.z / Mathf.Max(1, bodySdf.volumeDepth)));
                SubmitSdfPreview(bodySdf, uvwFromWorld, proxyMatrix, Vector3.zero, Vector3.one, bodyCell);
                return;
            }

            Vector3Int resolution = container.SolverRes;
            domain.EnsureSignedDistance(resolution, out RenderTexture sdf, out _);
            if (sdf == null || !EnsureSdfPreviewRuntime()) return;

            domain.GetBodyVoxelWindow(resolution, out Vector3Int windowMin, out Vector3Int windowSize);
            Vector3 uvwMin = Vector3.zero;
            Vector3 uvwMax = Vector3.one;
            if (windowSize.x > 0 && windowSize.y > 0 && windowSize.z > 0)
            {
                uvwMin = new Vector3((float)windowMin.x / resolution.x, (float)windowMin.y / resolution.y,
                                     (float)windowMin.z / resolution.z);
                uvwMax = new Vector3((float)(windowMin.x + windowSize.x) / resolution.x,
                                     (float)(windowMin.y + windowSize.y) / resolution.y,
                                     (float)(windowMin.z + windowSize.z) / resolution.z);
            }

            Vector3 worldSize = container.WorldSize();
            Matrix4x4 domainMatrix = Matrix4x4.TRS(container.transform.position, container.transform.rotation,
                                                   worldSize);
            Matrix4x4 matrix = domainMatrix * Matrix4x4.TRS((uvwMin + uvwMax) * 0.5f - Vector3.one * 0.5f,
                                                            Quaternion.identity, uvwMax - uvwMin);
            float bakeCell = Mathf.Min(Mathf.Abs(worldSize.x) / Mathf.Max(1, sdf.width),
                             Mathf.Min(Mathf.Abs(worldSize.y) / Mathf.Max(1, sdf.height),
                                       Mathf.Abs(worldSize.z) / Mathf.Max(1, sdf.volumeDepth)));
            SubmitSdfPreview(sdf, container.WorldToFluidUVW(), matrix, uvwMin, uvwMax, bakeCell);
        }

        void SubmitSdfPreview(RenderTexture sdf, Matrix4x4 worldToUvw, Matrix4x4 proxyMatrix,
                              Vector3 uvwMin, Vector3 uvwMax, float bakeCell)
        {
            sdfPreviewProperties.SetTexture("_SdfTex", sdf);
            sdfPreviewProperties.SetMatrix("_WorldToSdfUVW", worldToUvw);
            sdfPreviewProperties.SetVector("_BoxMinUVW", uvwMin);
            sdfPreviewProperties.SetVector("_BoxMaxUVW", uvwMax);
            sdfPreviewProperties.SetVector("_SdfInvRes", new Vector3(
                1f / Mathf.Max(1, sdf.width), 1f / Mathf.Max(1, sdf.height), 1f / Mathf.Max(1, sdf.volumeDepth)));
            sdfPreviewProperties.SetColor("_ShellColor", collisionSdfColor);
            sdfPreviewProperties.SetFloat("_ShellOpacity", collisionSdfOpacity);
            // Threshold and step floor must MATCH: any threshold below the floor leaves a gap grazing rays can
            // hop across, which rendered as hit/miss stripes along near-tangent faces. The shader fades near-hits
            // by approach distance, so the shared value adds a soft edge rather than visible padding.
            float previewStep = Mathf.Max(bakeCell * 0.35f, 1e-4f);
            sdfPreviewProperties.SetFloat("_MinStepWorld", previewStep);
            sdfPreviewProperties.SetFloat("_SurfaceEps", previewStep);
            Graphics.DrawMesh(SdfPreviewCube, proxyMatrix, sdfPreviewMaterial, gameObject.layer, null, 0,
                              sdfPreviewProperties, false, false);
        }

        bool EnsureSdfPreviewRuntime()
        {
            if (sdfPreviewMaterial != null && sdfPreviewProperties != null) return true;
            Shader shader = Resources.Load<Shader>("BrazeCfdSdfPreview");
            if (shader == null) shader = BrazeRuntimeShaderLibrary.Find("BrazeFX/CFD/SdfPreview");
            if (shader == null) return false;
            sdfPreviewMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            sdfPreviewProperties = new MaterialPropertyBlock();
            return true;
        }

        static Mesh SdfPreviewCube
        {
            get
            {
                if (sdfPreviewCube != null) return sdfPreviewCube;
                sdfPreviewCube = new Mesh { name = "BrazeFX_CFD_SdfPreviewCube", hideFlags = HideFlags.HideAndDontSave };
                sdfPreviewCube.vertices = new[]
                {
                    new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f),
                    new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
                    new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f),
                    new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f),
                };
                sdfPreviewCube.triangles = new[]
                {
                    0, 2, 1, 0, 3, 2,   // -Z
                    4, 5, 6, 4, 6, 7,   // +Z
                    0, 1, 5, 0, 5, 4,   // -Y
                    3, 6, 2, 3, 7, 6,   // +Y
                    0, 4, 7, 0, 7, 3,   // -X
                    1, 2, 6, 1, 6, 5,   // +X
                };
                sdfPreviewCube.RecalculateBounds();
                return sdfPreviewCube;
            }
        }

        void ReleaseSdfPreviewRuntime()
        {
            if (sdfPreviewMaterial != null)
            {
                if (Application.isPlaying) Destroy(sdfPreviewMaterial); else DestroyImmediate(sdfPreviewMaterial);
            }
            sdfPreviewMaterial = null;
            sdfPreviewProperties = null;
        }

        internal void SyncManagedComponents(bool createMissing)
        {
            if (synchronizing) return;
            synchronizing = true;
            try
            {
                ResolveReferences();
                if (domain == null || domain.Container == null || meshSource == null || meshSource.sharedMesh == null)
                {
                    if (sdfBoundary != null) sdfBoundary.enabled = false;
                    if (surfacePressure != null) surfacePressure.enabled = false;
                    status = "Assign a CFD Domain and Mesh Source.";
                    configurationHash = ComputeConfigurationHash();
                    return;
                }

                if (createMissing)
                {
                    sdfBoundary = EnsureSdf(sdfBoundary);
                    if (showSurfacePressure) surfacePressure = EnsureSurfacePressure(surfacePressure);
                }

                ConfigureSdf();
                ConfigureSurfacePressure();
                configurationHash = ComputeConfigurationHash();
                status = meshSource.sharedMesh.isReadable
                    ? "Airflow collision and comparative surface pressure are active. Material thermal response is staged."
                    : "Enable Read/Write on the Mesh Source importer so its SDF can be baked.";
            }
            finally { synchronizing = false; }
        }

        BrazeCfdSdfBoundary EnsureSdf(BrazeCfdSdfBoundary current)
        {
            if (current == null || current.gameObject != gameObject || current.managedByTestBody != this)
            {
                var candidates = GetComponents<BrazeCfdSdfBoundary>();
                current = null;
                for (int i = 0; i < candidates.Length; i++)
                    if (candidates[i] != null && candidates[i].managedByTestBody == this)
                    { current = candidates[i]; break; }
                if (current == null) current = gameObject.AddComponent<BrazeCfdSdfBoundary>();
            }
            current.managedByTestBody = this;
            current.hideFlags |= HideFlags.HideInInspector;
            return current;
        }

        BrazeCfdSurfacePressure EnsureSurfacePressure(BrazeCfdSurfacePressure current)
        {
            if (current == null || current.gameObject != gameObject || current.managedByTestBody != this)
            {
                var candidates = GetComponents<BrazeCfdSurfacePressure>();
                current = null;
                for (int i = 0; i < candidates.Length; i++)
                    if (candidates[i] != null && candidates[i].managedByTestBody == this)
                    { current = candidates[i]; break; }
                if (current == null) current = gameObject.AddComponent<BrazeCfdSurfacePressure>();
            }
            current.managedByTestBody = this;
            current.hideFlags |= HideFlags.HideInInspector;
            return current;
        }

        void ConfigureSdf()
        {
            if (sdfBoundary == null) return;
            bool changed = sdfBoundary.domain != domain || sdfBoundary.meshFilter != meshSource ||
                           sdfBoundary.mode != boundaryMode ||
                           sdfBoundary.animated != animatedBody ||
                           !Mathf.Approximately(sdfBoundary.wallThickness, shellThickness) ||
                           !Mathf.Approximately(sdfBoundary.surfaceOffset, collisionOffset);
            sdfBoundary.domain = domain;
            sdfBoundary.meshFilter = meshSource;
            sdfBoundary.mode = boundaryMode;
            sdfBoundary.animated = animatedBody;
            sdfBoundary.wallThickness = shellThickness;
            sdfBoundary.surfaceOffset = collisionOffset;
            sdfBoundary.showGizmo = false;
            sdfBoundary.enabled = isActiveAndEnabled;
            if (changed) sdfBoundary.MarkDirty();
        }

        void ConfigureSurfacePressure()
        {
            if (surfacePressure == null) return;
            bool retarget = surfacePressure.domain != domain;
            if (retarget && surfacePressure.enabled) surfacePressure.enabled = false;
            surfacePressure.domain = domain;
            surfacePressure.meshFilter = meshSource;
            surfacePressure.minimumPressure = suctionAt;
            surfacePressure.maximumPressure = pressureAt;
            surfacePressure.suctionColor = suctionColor;
            surfacePressure.neutralColor = neutralColor;
            surfacePressure.pressureColor = pressureColor;
            surfacePressure.opacity = overlayOpacity;
            surfacePressure.surfaceOffset = overlayOffset;
            UpdateSurfacePressureAxes();
            surfacePressure.enabled = isActiveAndEnabled && showSurfacePressure;
            if (surfacePressure.enabled) domain.AttachSurfacePressure(surfacePressure);
        }

        // A rigid transform is live simulation state, not authoring configuration. Keep comparative axes current
        // without running component discovery and adapter configuration on every animation frame.
        void UpdateSurfacePressureAxes()
        {
            if (surfacePressure == null || domain == null) return;
            surfacePressure.dragAxis = surfacePressure.transform
                .InverseTransformDirection(WindDirectionWorld).normalized;
            Vector3 lift = domain.Container != null ? domain.Container.transform.up : Vector3.up;
            surfacePressure.liftAxis = surfacePressure.transform.InverseTransformDirection(lift).normalized;
        }

        BrazeCfdWindTunnel FindTunnel()
        {
            for (int i = 0; i < BrazeCfdWindTunnel.Active.Count; i++)
            {
                BrazeCfdWindTunnel candidate = BrazeCfdWindTunnel.Active[i];
                if (candidate != null && candidate.isActiveAndEnabled && candidate.domain == domain) return candidate;
            }
            return null;
        }

        void ResolveReferences()
        {
            if (domain == null) domain = GetComponentInParent<BrazeCfdDomain>();
            if (meshSource == null) meshSource = GetComponent<MeshFilter>();
        }

        internal int ComputeConfigurationHash()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (domain != null ? domain.BrazeInstanceId() : 0);
                h = h * 31 + (meshSource != null ? meshSource.BrazeInstanceId() : 0);
                h = h * 31 + (meshSource != null && meshSource.sharedMesh != null
                    ? meshSource.sharedMesh.BrazeInstanceId() : 0);
                h = h * 31 + (int)boundaryMode;
                h = h * 31 + animatedBody.GetHashCode();
                h = h * 31 + shellThickness.GetHashCode();
                h = h * 31 + collisionOffset.GetHashCode();
                h = h * 31 + (int)material;
                h = h * 31 + initialTemperatureC.GetHashCode();
                h = h * 31 + materialThickness.GetHashCode();
                h = h * 31 + showSurfacePressure.GetHashCode();
                h = h * 31 + suctionAt.GetHashCode();
                h = h * 31 + pressureAt.GetHashCode();
                h = h * 31 + suctionColor.GetHashCode();
                h = h * 31 + neutralColor.GetHashCode();
                h = h * 31 + pressureColor.GetHashCode();
                h = h * 31 + overlayOpacity.GetHashCode();
                h = h * 31 + overlayOffset.GetHashCode();
                return h;
            }
        }

        void DestroyManaged(ref BrazeCfdSdfBoundary component)
        {
            if (component == null) return;
            if (component.managedByTestBody == this) DestroyOwned(component);
            component = null;
        }

        void DestroyManaged(ref BrazeCfdSurfacePressure component)
        {
            if (component == null) return;
            if (component.managedByTestBody == this) DestroyOwned(component);
            component = null;
        }

        static void DestroyOwned(Behaviour component)
        {
            component.enabled = false;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                // See Wind Tunnel: defer component-only cleanup so whole-GameObject teardown is never double-destroyed.
                UnityEditor.EditorApplication.delayCall += () =>
                {
                    if (component != null) DestroyImmediate(component);
                };
                return;
            }
#endif
            Destroy(component);
        }

        void OnDrawGizmosSelected()
        {
            if (!showGizmo || meshSource == null || meshSource.sharedMesh == null) return;
            Matrix4x4 oldMatrix = Gizmos.matrix;
            Color oldColor = Gizmos.color;
            Gizmos.matrix = meshSource.transform.localToWorldMatrix;
            Gizmos.color = new Color(0.18f, 0.9f, 0.72f, 0.85f);
            Gizmos.DrawWireMesh(meshSource.sharedMesh);
            Gizmos.matrix = oldMatrix;
            Gizmos.color = oldColor;
        }
    }
}
