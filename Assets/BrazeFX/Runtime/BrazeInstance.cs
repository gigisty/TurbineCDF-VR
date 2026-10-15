using UnityEngine;
using UnityEngine.Rendering;

namespace BrazeFX
{
    /// <summary>
    /// A single GPU instance of another mesh hierarchy or composition — the Cinema 4D Instance object.
    /// Internally drives a hidden one-clone Braze Cloner, so the source renders through the same indirect,
    /// culled, LOD-aware GPU path as cloned geometry and may itself contain live Braze Cloners.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("BrazeFX/Motion Design/Braze Instance", 41)]
    public sealed class BrazeInstance : MonoBehaviour
    {
        [Tooltip("Mesh hierarchy, group, or composition (may contain Braze Cloners) drawn once at this Transform.")]
        public GameObject source;
        [Tooltip("Optional material used for every submesh of the source. Empty preserves each renderer material.")]
        public Material materialOverride;

        [Header("RENDERING")]
        [Range(0.1f, 4f)] public float lodBias = 1f;
        [Min(0f), Tooltip("Zero disables distance culling.")]
        public float maxDrawDistance = 0f;
        [Min(0f), Tooltip("Extra world-space radius used by indirect draw bounds and GPU frustum culling.")]
        public float boundsPadding = 1f;
        public ShadowCastingMode shadowCasting = ShadowCastingMode.On;
        public bool receiveShadows = true;
        public bool renderInSceneView = true;

        public string Status => _engine != null ? _engine.Status : _status;
        public int DrawCallCount => _engine != null ? _engine.DrawCallCount : 0;
        public long GpuBytes => _engine != null ? _engine.GpuBytes : 0;
        internal BrazeCloner Engine => _engine;

        // Serialized on purpose: a DontSave engine survives scene transitions on paths where its OnDisable
        // never runs, leaving destroyed-but-subscribed render callbacks. A regular hidden component gets
        // the standard Unity lifecycle, and duplicates/prefabs remap the reference correctly.
        [SerializeField, HideInInspector] BrazeCloner _engine;
        string _status = "Assign a source GameObject.";

        void OnEnable()
        {
            AcquireEngine();
            if (_engine != null) _engine.enabled = true;
            SyncEngine();
        }

        // Only suspend here: Unity forbids destroying a sibling component while the GameObject is being
        // activated or deactivated. Disabling the engine releases its GPU resources through its own
        // OnDisable; the component itself is destroyed in OnDestroy.
        void OnDisable()
        {
            if (_engine != null) _engine.enabled = false;
        }

        void OnDestroy() => ReleaseEngine();
        void OnValidate() => SyncEngine();
        void LateUpdate() => SyncEngine();

        void AcquireEngine()
        {
            if (_engine == null)
            {
                BrazeCloner[] cloners = GetComponents<BrazeCloner>();
                for (int i = 0; i < cloners.Length; i++)
                {
                    if ((cloners[i].hideFlags & HideFlags.HideInInspector) != 0)
                    {
                        _engine = cloners[i];
                        break;
                    }
                }
                if (_engine == null && cloners.Length > 0)
                {
                    // BrazeCloner is DisallowMultipleComponent; a user-authored cloner blocks the hidden engine.
                    _status = "Remove the Braze Cloner on this GameObject — Braze Instance hosts its own hidden cloner engine.";
                    return;
                }
                if (_engine == null) _engine = gameObject.AddComponent<BrazeCloner>();
            }
            // Also migrates engines created by earlier builds that carried HideFlags.DontSave.
            _engine.hideFlags = HideFlags.HideInInspector;
        }

        void ReleaseEngine()
        {
            BrazeCloner engine = _engine;
            _engine = null;
            if (engine == null) return;
            if (Application.isPlaying)
            {
                Destroy(engine);
                return;
            }
#if UNITY_EDITOR
            // Synchronous DestroyImmediate is illegal while the GameObject is deactivating or being torn
            // down, and OnDestroy can run inside both. Deferring is safe in every path: when the whole
            // GameObject went down with us, the reference is already null by the time this runs.
            UnityEditor.EditorApplication.delayCall += () =>
            {
                if (engine != null) DestroyImmediate(engine);
            };
#endif
        }

        /// <summary>
        /// Pushes the authored fields into the hidden engine. Runs on enable, inspector edits, and every
        /// LateUpdate; the engine's own content hashing keeps repeated identical pushes free.
        /// </summary>
        internal void SyncEngine()
        {
            if (_engine == null) return;
            if (_engine.sources.Count != 1 || _engine.sources[0] == null)
            {
                _engine.sources.Clear();
                _engine.sources.Add(new BrazeCloner.Source());
            }
            BrazeCloner.Source engineSource = _engine.sources[0];
            engineSource.gameObject = source;
            engineSource.materialOverride = materialOverride;
            engineSource.enabled = true;

            // One centred grid clone with no alignment places the copy exactly at this Transform.
            _engine.distribution = BrazeCloner.Distribution.Grid;
            _engine.gridCount = Vector3Int.one;
            _engine.gridSpacing = Vector3.zero;
            _engine.center = true;
            _engine.alignment = BrazeCloner.CloneAlignment.None;
            _engine.uniformScale = 1f;
            _engine.appearanceEnabled = false;
            _engine.lodBias = lodBias;
            _engine.maxDrawDistance = maxDrawDistance;
            _engine.boundsPadding = boundsPadding;
            _engine.shadowCasting = shadowCasting;
            _engine.receiveShadows = receiveShadows;
            _engine.renderInSceneView = renderInSceneView;
            _engine.renderClones = true;
        }
    }
}
