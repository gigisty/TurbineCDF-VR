using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace BrazeFX
{
    /// <summary>
    /// Fully GPU-resident mesh cloner. Source hierarchies are reduced to indirect draw batches once; clone
    /// transforms, the Braze effector stack, camera culling, LOD selection and instance counts stay on the GPU.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("BrazeFX/Motion Design/Braze Cloner", 40)]
    public sealed class BrazeCloner : MonoBehaviour, IBrazeAnimatedTarget
    {
        public static readonly List<BrazeCloner> Active = new();

        // Append-only: distribution values are serialized by integer.
        public enum Distribution { Grid, Linear, Radial, Mesh, Spline }
        public enum MeshSampling { Surface, Vertices }
        public enum RadialPlane { XY, XZ, YZ }
        public enum SourceOrder { Sequential, Random }
        public enum CloneAlignment { Default, None, Path, Outward, Inward }
        public enum AlignmentAxis { PositiveZ, NegativeZ, PositiveY, NegativeY, PositiveX, NegativeX }
        public enum AlignmentUp { Automatic, X, Y, Z }
        public enum AffectedColorMode { Solid, Gradient, Speed }

        [Serializable]
        public sealed class Source
        {
            [Tooltip("Prefab or scene hierarchy to clone. MeshRenderer children and its first LODGroup are supported.")]
            public GameObject gameObject;
            [Tooltip("Optional material used for every submesh in this source. Empty preserves each renderer material.")]
            public Material materialOverride;
            public bool enabled = true;
        }

        [Header("SOURCES")]
        public List<Source> sources = new();
        public SourceOrder sourceOrder = SourceOrder.Random;
        public int seed = 12345;

        [Header("DISTRIBUTION")]
        public Distribution distribution = Distribution.Grid;
        public Vector3Int gridCount = new(10, 10, 10);
        public Vector3 gridSpacing = Vector3.one;
        public bool center = true;
        [Min(1)] public int count = 100;
        public Vector3 linearOffset = Vector3.right;
        public Vector3 linearRotation;
        public Vector3 linearScale = Vector3.one;
        [Tooltip("Renderer whose mesh defines clone positions. MeshRenderer and SkinnedMeshRenderer shared meshes are supported; the input Transform is followed.")]
        public Renderer meshInput;
        [Tooltip("Surface uses deterministic area-weighted face sampling. Vertices walks the mesh vertex set, wrapping when Count exceeds it.")]
        public MeshSampling meshSampling = MeshSampling.Surface;
        [Tooltip("Braze Spline that defines the clone path. Clones are spaced evenly by arc length; the spline Transform is followed live.")]
        public BrazeSpline splineInput;
        [Tooltip("Portion of the path that receives clones, as start/end fractions of its length. A reversed range walks the path backwards.")]
        public Vector2 splineRange = new(0f, 1f);
        [Tooltip("Shifts every clone along the path. Wraps on closed splines — animate it to flow clones around the loop."),
         BrazeAnimatable("DISTRIBUTION/SPLINE/Offset", -1f, 1f)]
        public float splineOffset;
        [Min(1), Tooltip("Number of polar layers. Count is evaluated per ring.")]
        public int radialRings = 1;
        [Min(0), Tooltip("Additional clones added to each successive ring.")]
        public int radialCountStep;
        [Min(0f)] public float radialRadius = 5f;
        [Tooltip("Radius added to each successive ring.")]
        public float radialRadiusStep = 1f;
        public Vector2 radialArc = new(0f, 360f);
        [Tooltip("Angular offset added to each successive ring, in degrees.")]
        public float radialRingTwist;
        public RadialPlane radialPlane = RadialPlane.XZ;
        [Tooltip("Distance between rings along the selected plane's normal axis.")]
        public float radialAxisStep;
        [Tooltip("Centres the complete ring stack and axial path rise around the Cloner origin.")]
        public bool radialCenterAxis = true;
        [Tooltip("Radius gained from the beginning to the end of every ring path.")]
        public float radialSpiralRadius;
        [Tooltip("Axial distance travelled from the beginning to the end of every ring path.")]
        public float radialSpiralAxis;
        [Min(0f), Tooltip("Number of wave cycles along every ring path.")]
        public float radialWaveCycles = 1f;
        [Tooltip("Wave displacement along the ring radius.")]
        public float radialWaveRadius;
        [Tooltip("Wave displacement along the selected plane's normal axis.")]
        public float radialWaveAxis;
        [Tooltip("Starting phase of the wave, in degrees.")]
        public float radialWavePhase;
        [Tooltip("Phase offset between radial and axial waves, in degrees. 90 creates a corkscrew wave.")]
        public float radialWaveAxialPhase = 90f;
        [Tooltip("Wave phase added to each successive ring, in degrees.")]
        public float radialWaveRingPhase;
        [Tooltip("Local mesh rotation applied after distribution alignment.")]
        public Vector3 radialRotationOffset;
        [Tooltip("Additional local mesh rotation per ring.")]
        public Vector3 radialRotationStep;
        [Tooltip("Uniform scale added per ring.")]
        public float radialScaleStep;
        [Tooltip("Uniform scale modulation driven by the radial wave.")]
        public float radialScaleWave;

        [Header("TRANSFORM")]
        [Tooltip("Default preserves Grid/Linear orientation, follows the Radial path, and follows Mesh surface normals.")]
        public CloneAlignment alignment = CloneAlignment.Default;
        [Tooltip("Local source axis aimed along the selected alignment direction.")]
        public AlignmentAxis alignmentAxis = AlignmentAxis.PositiveX;
        [Tooltip("Roll reference used to stabilize clone orientation. Automatic follows the radial plane and selects a stable axis elsewhere.")]
        public AlignmentUp alignmentUp = AlignmentUp.Automatic;
        [Range(-1f, 1f), Tooltip("Uniform multiplier for every clone. Zero collapses clones; negative values mirror them.")]
        public float uniformScale = 1f;

        [SerializeField, HideInInspector, FormerlySerializedAs("radialAlign")]
        bool _legacyRadialAlign = true;
        [SerializeField, HideInInspector]
        int _serializationVersion;

        [Header("EFFECTORS")]
        [Tooltip("Ordered Braze transform-effector stack. Evaluation matches Braze Voronoi Fracture.")]
        public List<BrazeFractureEffector> effectors = new();

        [Header("APPEARANCE")]
        [Tooltip("Enable per-clone styling from the combined effector field and final clone motion.")]
        public bool appearanceEnabled;

        [ColorUsage(true, true), Tooltip("Colour applied to clones outside the affected area.")]
        public Color unaffectedColor = Color.white;
        [Min(0f), Tooltip("Emission strength applied only to the unaffected region, using its Colour."),
         BrazeAnimatable("APPEARANCE/UNAFFECTED/Glow", 0f, 10f)]
        public float unaffectedGlow;

        [Tooltip("Solid uses one colour. Gradient spans the complete clone order. Speed maps final clone motion.")]
        public AffectedColorMode affectedColorMode = AffectedColorMode.Solid;
        [ColorUsage(true, true), Tooltip("Colour used by Solid mode.")]
        public Color affectedColor = new(1f, 0.28f, 0.035f, 1f);
        [GradientUsage(true), Tooltip("Colour ramp used by Gradient and Speed modes.")]
        public Gradient affectedGradient = DefaultAffectedGradient();
        [Tooltip("World units per second mapped to the left and right ends of the Speed gradient.")]
        public Vector2 affectedSpeedRange = new(0f, 5f);
        [Range(0f, 1f), Tooltip("GPU temporal smoothing in seconds for Speed colour. Zero reacts immediately; higher values remove velocity flicker.")]
        public float affectedSpeedSmoothing = 0.12f;
        [Min(0.01f), Tooltip("Scales the Gradient / Speed ramp coordinate.")]
        public float affectedGradientScale = 1f;
        [Tooltip("Offsets the Gradient / Speed ramp coordinate before it is clamped.")]
        public float affectedGradientOffset;
        [Min(0f), Tooltip("Emission strength applied only to the affected region, using the active Solid, Gradient, or Speed colour."),
         BrazeAnimatable("APPEARANCE/AFFECTED/Glow", 0f, 10f)]
        public float affectedGlow;

        [Range(0f, 1f), Tooltip("Minimum combined effector influence considered affected.")]
        public float affectedThreshold = 0.01f;
        [Range(0f, 1f), Tooltip("Soft transition width between unaffected and affected clones.")]
        public float affectedSoftness = 0.1f;
        [Tooltip("Swap the affected and unaffected regions without changing the effectors.")]
        public bool invertAffected;

        [Header("RENDERING")]
        [Range(0.1f, 4f)] public float lodBias = 1f;
        [Min(0f), Tooltip("Zero disables distance culling.")]
        public float maxDrawDistance = 0f;
        [Min(0f), Tooltip("Extra world-space radius used by indirect draw bounds and GPU frustum culling.")]
        public float boundsPadding = 1f;
        public ShadowCastingMode shadowCasting = ShadowCastingMode.On;
        public bool receiveShadows = true;
        public bool renderInSceneView = true;
        [Tooltip("Draw this cloner's own clones. Off keeps the cloner evaluating as a nested source for another Braze Cloner without drawing the original.")]
        public bool renderClones = true;

        public int InstanceCount => ResolveInstanceCount();
        public int ActiveSourceCount => _sourceRuntimes.Count;
        public int DrawCallCount => _draws.Count;
        public long GpuBytes => _gpuBytes;
        public string Status => _status;

        /// <summary>
        /// Prepares and exposes the world-space clone position stream for GPU consumers such as the Braze Trail
        /// Renderer. The final clone centre is <c>basePositions[index] + positionOffsets[index]</c>. No CPU readback
        /// is performed, and repeated consumers share the transform preparation already used by indirect rendering.
        /// </summary>
        public bool TryGetGpuPositions(out GraphicsBuffer basePositions, out GraphicsBuffer positionOffsets,
            out int instanceCount)
        {
            return TryGetGpuTransforms(out basePositions, out positionOffsets, out _, out _, out instanceCount);
        }

        /// <summary>
        /// Prepares and exposes the complete world-space clone transform stream for GPU consumers. Final position is
        /// <c>basePositions[index] + positionOffsets[index]</c>; final rotation is
        /// <c>rotationOffsets[index] * baseRotations[index]</c>. Buffers are shared with indirect rendering and never
        /// read back to the CPU.
        /// </summary>
        public bool TryGetGpuTransforms(out GraphicsBuffer basePositions, out GraphicsBuffer positionOffsets,
            out GraphicsBuffer baseRotations, out GraphicsBuffer rotationOffsets, out int instanceCount)
        {
            basePositions = null;
            positionOffsets = null;
            baseRotations = null;
            rotationOffsets = null;
            instanceCount = 0;

            if (!isActiveAndEnabled || !gameObject.activeInHierarchy || !SystemInfo.supportsComputeShaders ||
                _inPrepare || !EnsureCompute())
                return false;

            // The re-entry guard keeps cyclic nested-source graphs (A clones B while B clones A) finite:
            // the inner request sees the outer cloner mid-prepare and simply skips it for this frame.
            _inPrepare = true;
            try
            {
                // The source hash walks hierarchies; once per frame is enough change detection in play mode
                // (multi-camera setups would otherwise pay it per camera). Edit mode keeps per-camera checks
                // so authoring edits reflect immediately in every view.
                if (!Application.isPlaying || _sourcesDirty || _sourceHashFrame != Time.frameCount)
                {
                    _sourceHashFrame = Time.frameCount;
                    int currentSourceHash = ComputeSourceHash();
                    if (_sourcesDirty || currentSourceHash != _sourceHash)
                    {
                        _sourceHash = currentSourceHash;
                        RebuildSources();
                    }
                }
                PrepareNestedChildren();
                EnsureDraws();
                if (_sourceRuntimes.Count == 0 || _draws.Count == 0) return false;
                RefreshAdaptedMaterials();

                int instances = ResolveInstanceCount();
                if (instances <= 0 || !EnsureBuffers(instances)) return false;
                if (!EnsureMeshDistributionSamples(instances)) return false;
                PrepareTransforms(instances);
                PrepareAppearanceSpeed(instances);

                basePositions = _basePosition;
                positionOffsets = _effectorPosition;
                baseRotations = _baseRotation;
                rotationOffsets = _effectorRotation;
                instanceCount = instances;
                return basePositions != null && positionOffsets != null &&
                       baseRotations != null && rotationOffsets != null;
            }
            finally
            {
                _inPrepare = false;
            }
        }

        /// <summary>
        /// Prepares this cloner so another Braze Cloner can compose its clone transform stream. Returns false
        /// while this cloner is inactive, mid-prepare (cycle), or has nothing to draw.
        /// </summary>
        internal bool PrepareForComposition()
            => !_inPrepare && TryGetGpuTransforms(out _, out _, out _, out _, out _);

        void PrepareNestedChildren()
        {
            for (int i = 0; i < _nestedRuntimes.Count; i++)
            {
                NestedRuntime nested = _nestedRuntimes[i];
                nested.ready = nested.child != null && nested.child != this && nested.sourceRoot != null &&
                               nested.child.PrepareForComposition();
                nested.childInstances = nested.ready ? nested.child._allocatedInstances : 0;
                nested.childSourceCount = nested.ready ? nested.child._sourceRuntimes.Count : 0;
                if (nested.childInstances <= 0 || nested.childSourceCount <= 0) nested.ready = false;
            }
        }

        const int MaxLods = 4;
        const int TransformStride = 16;
        const int EffectorTransformStride = 16;
        const int SourceStride = 32;
        const int MeshSampleStride = 32;
        const int Threads = 64;
        const int MaxDispatchX = 65535;

        [StructLayout(LayoutKind.Sequential)]
        struct GpuSource
        {
            public float radius;
            public uint lodCount;
            public Vector2 padding;
            public Vector4 thresholds;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct GpuMeshSample
        {
            public Vector3 position;
            public float positionPadding;
            public Vector3 normal;
            public float normalPadding;
        }

        sealed class Part
        {
            public Mesh mesh;
            public int submesh;
            public Material sourceMaterial;
            public Matrix4x4 matrix;
        }

        sealed class LodRuntime
        {
            public float transitionHeight;
            public readonly List<Part> parts = new();
        }

        sealed class SourceRuntime
        {
            public readonly List<LodRuntime> lods = new();
            public readonly List<NestedRuntime> nested = new();
            public float radius;
        }

        /// <summary>
        /// A Braze Cloner discovered inside a source hierarchy. Its clone transforms stay in its own GPU
        /// buffers; this cloner composes them per (parent clone, child clone) pair during culling and in the
        /// vertex shader, so composed transforms are never stored and the child evaluates exactly once.
        /// </summary>
        sealed class NestedRuntime
        {
            public BrazeCloner child;
            public Transform sourceRoot;
            public int parentSourceIndex;
            public bool ready;
            public int childInstances;
            public int childSourceCount;
            public int bucketBase;
            public int capacity;
            public long visibleOffset;
        }

        sealed class DrawRuntime
        {
            public Mesh mesh;
            public int submesh;
            public Material material;
            public MaterialPropertyBlock properties;
            public int bucket; // global bucket for own draws; local (childSource * MaxLods + lod) for nested draws
            public NestedRuntime nested;
            public bool ownsMaterial;
            public CullMode baseCull;
        }

        readonly List<SourceRuntime> _sourceRuntimes = new();
        readonly List<NestedRuntime> _nestedRuntimes = new();
        readonly List<DrawRuntime> _draws = new();
        static readonly HashSet<int> SourceHashVisiting = new();
        // Per-instance scratch for the zero-alloc source hash. Instance (not static) so nested-cloner
        // recursion fills the child's own lists without clobbering the parent's iteration state.
        readonly List<Renderer> _hashRenderers = new();
        readonly List<Material> _hashMaterials = new();
        readonly List<BrazeCloner> _hashChildCloners = new();
        int _sourceHashFrame = -1;
        readonly List<BrazeFractureEffector> _effectorScratch = new();
        readonly List<Vector4> _effectorAuxScratch = new();
        readonly BrazeDelayGpuStateCollection _delayStates = new();
        readonly BrazePushApartGpuStateCollection _pushApartStates = new();
        readonly Dictionary<int, Material> _materialCache = new();
        readonly Dictionary<int, Material> _materialSources = new();
        readonly Dictionary<int, int> _materialContentHashes = new();

        ComputeShader _clonerCompute;
        ComputeShader _effectorCompute;
        int _kBuild = -1, _kClearBuckets = -1, _kCull = -1, _kCullComposed = -1, _kWriteArgs = -1,
            _kClearPushApartRadius = -1, _kMeasurePushApartRadius = -1, _kClearPushApartGrid = -1,
            _kBuildPushApartGrid = -1, _kCapturePushApartBakeInput = -1, _kApplyPushApartBake = -1,
            _kPushApart = -1, _kStorePushApartBake = -1, _kMeasureSpeed = -1;
        int _kClearEffectors = -1, _kApplyEffector = -1, _kApplyDelayEffector = -1,
            _kApplyDelaySpringEffector = -1;

        GraphicsBuffer _basePosition;
        GraphicsBuffer _baseRotation;
        GraphicsBuffer _baseScale;
        GraphicsBuffer _effectorPosition;
        GraphicsBuffer _effectorPositionScratch;
        GraphicsBuffer _effectorRotation;
        GraphicsBuffer _effectorScale;
        GraphicsBuffer _visibleIndices;
        GraphicsBuffer _bucketCounts;
        GraphicsBuffer _sourceBuffer;
        GraphicsBuffer _argsBuffer;
        GraphicsBuffer _drawBucketBuffer;
        GraphicsBuffer _effectorBuffer;
        GraphicsBuffer _effectorFalloffBuffer;
        GraphicsBuffer _effectorFunctionBuffer;
        GraphicsBuffer _effectorAuxBuffer;
        GraphicsBuffer _appearanceSpeed;
        GraphicsBuffer _appearancePreviousPosition;
        GraphicsBuffer _appearanceSpeedDummy;
        GraphicsBuffer _meshSampleBuffer;
        GraphicsBuffer _pushGridHeads;
        GraphicsBuffer _pushGridNext;
        GraphicsBuffer _pushGridCells;
        GraphicsBuffer _pushRadii;
        GraphicsBuffer _pushMaxRadius;
        GraphicsBuffer _pushInputPosition;

        Texture2D _appearanceRamp;
        bool _appearanceRampDirty = true;
        bool _appearanceSpeedHasPrevious;
        int _appearanceSpeedFrame = -1;
        int _materialSyncFrame = -1;
        double _appearanceSpeedTime = -1.0;

        GpuFractureEffector[] _effectorData = new GpuFractureEffector[4];
        Vector4[] _effectorAuxData = new Vector4[16];
        float[] _falloffData = new float[256];
        float[] _functionData = new float[256];
        GpuMeshSample[] _meshSampleData = Array.Empty<GpuMeshSample>();
        Vector3[] _splineDense;
        float[] _splineDenseLengths;
        readonly List<Vector3> _meshVertices = new();
        readonly List<Vector3> _meshNormals = new();
        readonly List<int> _meshTriangles = new();
        readonly List<int> _meshSubmeshTriangles = new();
        readonly List<float> _meshTriangleCdf = new();
        int _falloffHash, _functionHash, _effectorUploadHash;
        int _meshSampleHash = int.MinValue;
        bool _effectorUploadHashValid;

        int _allocatedInstances;
        int _sourceCapacity;
        int _bucketCount;
        int _pushGridSize;
        int _sourceHash;
        int _preparedHash;
        int _preparedFrame = -1;
        int _nestedLayoutHash;
        int _nestedDrawHash;
        bool _sourcesDirty = true;
        bool _buffersDirty = true;
        bool _drawsDirty = true;
        bool _inPrepare;
        bool _boundsVisiting;
        long _gpuBytes;
        string _status = "Add at least one source GameObject.";
        Bounds _localDistributionBounds;
        Plane[] _frustumPlanes = new Plane[6];
        readonly Vector4[] _planeVectors = new Vector4[6];
#if UNITY_EDITOR
        const double EditorEffectorInterval = 1.0 / 60.0;
        double _nextEditorEffectorTick;
#endif

        static readonly int IdBasePosition = Shader.PropertyToID("_BrazeClonerBasePosition");
        static readonly int IdBaseRotation = Shader.PropertyToID("_BrazeClonerBaseRotation");
        static readonly int IdBaseScale = Shader.PropertyToID("_BrazeClonerBaseScale");
        static readonly int IdEffectorPosition = Shader.PropertyToID("_BrazeClonerEffectorPosition");
        static readonly int IdEffectorRotation = Shader.PropertyToID("_BrazeClonerEffectorRotation");
        static readonly int IdEffectorScale = Shader.PropertyToID("_BrazeClonerEffectorScale");
        static readonly int IdVisible = Shader.PropertyToID("_BrazeClonerVisibleIndices");
        static readonly int IdBucketOffset = Shader.PropertyToID("_BrazeClonerBucketOffset");
        static readonly int IdPartMatrix = Shader.PropertyToID("_BrazeClonerPartMatrix");
        static readonly int IdPartNormal = Shader.PropertyToID("_BrazeClonerPartNormalMatrix");
        static readonly int IdAppearanceSpeed = Shader.PropertyToID("_BrazeClonerAppearanceSpeed");
        static readonly int IdAppearanceEnabled = Shader.PropertyToID("_BrazeClonerAppearanceEnabled");
        static readonly int IdAppearanceColorMode = Shader.PropertyToID("_BrazeClonerAppearanceColorMode");
        static readonly int IdUnaffectedColor = Shader.PropertyToID("_BrazeClonerUnaffectedColor");
        static readonly int IdAffectedColor = Shader.PropertyToID("_BrazeClonerAffectedColor");
        static readonly int IdAppearanceMask = Shader.PropertyToID("_BrazeClonerAppearanceMask");
        static readonly int IdAppearanceMap = Shader.PropertyToID("_BrazeClonerAppearanceMap");
        static readonly int IdAppearanceGlow = Shader.PropertyToID("_BrazeClonerAppearanceGlow");
        static readonly int IdColorRamp = Shader.PropertyToID("_BrazeClonerColorRamp");
        static readonly int IdNestedChildCount = Shader.PropertyToID("_BrazeClonerNestedChildCount");
        static readonly int IdNestedRootInvRotation = Shader.PropertyToID("_BrazeClonerNestedRootInvRotation");
        static readonly int IdNestedRootPosition = Shader.PropertyToID("_BrazeClonerNestedRootPosition");
        static readonly int IdNestedRootInvScale = Shader.PropertyToID("_BrazeClonerNestedRootInvScale");
        static readonly int IdChildBasePosition = Shader.PropertyToID("_BrazeClonerChildBasePosition");
        static readonly int IdChildBaseRotation = Shader.PropertyToID("_BrazeClonerChildBaseRotation");
        static readonly int IdChildBaseScale = Shader.PropertyToID("_BrazeClonerChildBaseScale");
        static readonly int IdChildEffectorPosition = Shader.PropertyToID("_BrazeClonerChildEffectorPosition");
        static readonly int IdChildEffectorRotation = Shader.PropertyToID("_BrazeClonerChildEffectorRotation");
        static readonly int IdChildEffectorScale = Shader.PropertyToID("_BrazeClonerChildEffectorScale");

        void OnEnable()
        {
            BrazeRuntimeRegistry.AddUnique(Active, this);
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            Camera.onPreCull += OnBuiltinCameraPreCull;
            _sourcesDirty = _buffersDirty = true;
            _appearanceRampDirty = true;
#if UNITY_EDITOR
            _nextEditorEffectorTick = 0.0;
            UnityEditor.EditorApplication.update += EditorEffectorTick;
#endif
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorEffectorTick;
#endif
            BrazeRuntimeRegistry.Remove(Active, this);
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            Camera.onPreCull -= OnBuiltinCameraPreCull;
            ReleaseRuntime();
        }

        void OnDestroy() => ReleaseRuntime();

        void Reset()
        {
            alignment = CloneAlignment.Default;
            alignmentAxis = AlignmentAxis.PositiveX;
            alignmentUp = AlignmentUp.Automatic;
            uniformScale = 1f;
            _serializationVersion = 1;
        }

        void OnValidate()
        {
            if (_serializationVersion < 1)
            {
                alignment = _legacyRadialAlign ? CloneAlignment.Default : CloneAlignment.None;
                _serializationVersion = 1;
            }
            ValidateAuthoringValues();
            // Source and transform content hashes invalidate only what actually changed. Keeping appearance edits
            // out of the allocation path makes colour/gradient scrubbing safe even on million-clone setups.
            _effectorUploadHashValid = false;
            _appearanceRampDirty = true;
            if (!appearanceEnabled || affectedColorMode != AffectedColorMode.Speed)
            {
                _appearanceSpeedHasPrevious = false;
                _appearanceSpeedFrame = -1;
            }
        }

        void ValidateAuthoringValues()
        {
            gridCount = new Vector3Int(Mathf.Max(1, gridCount.x), Mathf.Max(1, gridCount.y), Mathf.Max(1, gridCount.z));
            count = Mathf.Max(1, count);
            radialRings = Mathf.Max(1, radialRings);
            radialCountStep = Mathf.Max(0, radialCountStep);
            radialRadius = Mathf.Max(0f, radialRadius);
            radialWaveCycles = Mathf.Max(0f, radialWaveCycles);
            splineRange = new Vector2(Mathf.Clamp01(splineRange.x), Mathf.Clamp01(splineRange.y));
            uniformScale = Mathf.Clamp(uniformScale, -1f, 1f);
            lodBias = Mathf.Clamp(lodBias, 0.1f, 4f);
            maxDrawDistance = Mathf.Max(0f, maxDrawDistance);
            boundsPadding = Mathf.Max(0f, boundsPadding);
            affectedSpeedRange = new Vector2(
                Mathf.Min(affectedSpeedRange.x, affectedSpeedRange.y),
                Mathf.Max(affectedSpeedRange.x, affectedSpeedRange.y));
            affectedGradientScale = Mathf.Max(0.01f, affectedGradientScale);
            affectedSpeedSmoothing = Mathf.Clamp01(affectedSpeedSmoothing);
            unaffectedGlow = Mathf.Max(0f, unaffectedGlow);
            affectedGlow = Mathf.Max(0f, affectedGlow);
            affectedThreshold = Mathf.Clamp01(affectedThreshold);
            affectedSoftness = Mathf.Clamp01(affectedSoftness);
            affectedGradient ??= DefaultAffectedGradient();
        }

        public void OnBrazeAnimated()
        {
            // Timeline changes are already detected by TransformContentHash / EnsureBuffers. Avoid treating every
            // animated sample as a full authoring rebuild so Speed colour retains its previous-frame history.
            ValidateAuthoringValues();
        }

#if UNITY_EDITOR
        void EditorEffectorTick()
        {
            if (this == null || Application.isPlaying || !isActiveAndEnabled || !HasAnimatingEffector()) return;
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            if (now < _nextEditorEffectorTick) return;
            _nextEditorEffectorTick = now + EditorEffectorInterval;
            UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
            UnityEditor.SceneView.RepaintAll();
        }

        bool HasAnimatingEffector()
        {
            if (effectors == null) return false;
            for (int i = 0; i < effectors.Count; i++)
            {
                BrazeFractureEffector effector = effectors[i];
                if (effector != null && effector.isActiveAndEnabled && effector.Animating) return true;
            }
            return false;
        }
#endif

        void OnBeginCameraRendering(ScriptableRenderContext _, Camera camera)
        {
            if (this == null) { Unsubscribe(); return; }
            if (GraphicsSettings.currentRenderPipeline != null) Render(camera);
        }

        void OnBuiltinCameraPreCull(Camera camera)
        {
            if (this == null) { Unsubscribe(); return; }
            if (GraphicsSettings.currentRenderPipeline == null) Render(camera);
        }

        // Self-heals a destroyed-but-subscribed cloner: if this component was torn down on a path where
        // OnDisable never ran, the first camera callback removes the stale registrations instead of
        // throwing MissingReferenceException on every render.
        void Unsubscribe()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            Camera.onPreCull -= OnBuiltinCameraPreCull;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorEffectorTick;
#endif
        }

        bool CameraAllowed(Camera camera)
        {
            if (camera == null || !isActiveAndEnabled || !gameObject.activeInHierarchy) return false;
            if (!renderInSceneView && camera.cameraType == CameraType.SceneView) return false;
            return camera.cameraType != CameraType.Preview;
        }

        void Render(Camera camera)
        {
            if (!renderClones || !CameraAllowed(camera) ||
                !TryGetGpuPositions(out _, out _, out int instances)) return;
            CullAndBuildArgs(camera, instances);
            SubmitDraws(camera);
        }

        bool EnsureCompute()
        {
            if (_clonerCompute == null)
            {
                ComputeShader asset = Resources.Load<ComputeShader>("BrazeCloner");
                if (asset == null) { _status = "Resources/BrazeCloner.compute is missing."; return false; }
                _clonerCompute = Instantiate(asset);
                _clonerCompute.hideFlags = HideFlags.HideAndDontSave;
                _kBuild = _clonerCompute.FindKernel("BuildClones");
                _kClearBuckets = _clonerCompute.FindKernel("ClearBuckets");
                _kCull = _clonerCompute.FindKernel("CullAndBucket");
                _kCullComposed = _clonerCompute.FindKernel("CullAndBucketComposed");
                _kWriteArgs = _clonerCompute.FindKernel("WriteDrawArgs");
                _kClearPushApartRadius = _clonerCompute.FindKernel("ClearPushApartRadius");
                _kMeasurePushApartRadius = _clonerCompute.FindKernel("MeasurePushApartRadius");
                _kClearPushApartGrid = _clonerCompute.FindKernel("ClearPushApartGrid");
                _kBuildPushApartGrid = _clonerCompute.FindKernel("BuildPushApartGrid");
                _kCapturePushApartBakeInput = _clonerCompute.FindKernel("CapturePushApartBakeInput");
                _kApplyPushApartBake = _clonerCompute.FindKernel("ApplyPushApartBake");
                _kPushApart = _clonerCompute.FindKernel("PushApart");
                _kStorePushApartBake = _clonerCompute.FindKernel("StorePushApartBake");
                _kMeasureSpeed = _clonerCompute.FindKernel("MeasureCloneSpeed");
            }
            if (_effectorCompute == null)
            {
                ComputeShader asset = Resources.Load<ComputeShader>("BrazeVoronoi");
                if (asset == null) { _status = "Resources/BrazeVoronoi.compute is missing."; return false; }
                _effectorCompute = Instantiate(asset);
                _effectorCompute.hideFlags = HideFlags.HideAndDontSave;
                _kClearEffectors = _effectorCompute.FindKernel("ClearEffectorTransforms");
                _kApplyEffector = _effectorCompute.FindKernel("ApplySingleEffector");
                _kApplyDelayEffector = _effectorCompute.FindKernel("ApplyDelayEffector");
                _kApplyDelaySpringEffector = _effectorCompute.FindKernel("ApplyDelaySpringEffector");
            }
            return _kBuild >= 0 && _kCull >= 0 && _kCullComposed >= 0 && _kClearPushApartRadius >= 0 &&
                   _kMeasurePushApartRadius >= 0 && _kClearPushApartGrid >= 0 &&
                   _kBuildPushApartGrid >= 0 && _kCapturePushApartBakeInput >= 0 &&
                   _kApplyPushApartBake >= 0 && _kPushApart >= 0 && _kStorePushApartBake >= 0 &&
                   _kMeasureSpeed >= 0 && _kApplyEffector >= 0 &&
                   _kApplyDelayEffector >= 0 && _kApplyDelaySpringEffector >= 0;
        }

        int ResolveInstanceCount()
        {
            if (distribution == Distribution.Linear || distribution == Distribution.Mesh ||
                distribution == Distribution.Spline) return Mathf.Max(1, count);
            long total;
            if (distribution == Distribution.Radial)
            {
                long rings = Mathf.Max(1, radialRings);
                total = rings * Mathf.Max(1, count) + (long)Mathf.Max(0, radialCountStep) * rings * (rings - 1L) / 2L;
            }
            else
            {
                total = (long)Mathf.Max(1, gridCount.x) * Mathf.Max(1, gridCount.y) * Mathf.Max(1, gridCount.z);
            }
            return (int)Math.Min(total, int.MaxValue);
        }

        int ComputeSourceHash()
        {
            // Nested sources hash recursively; the visiting set turns source cycles into a stable marker
            // instead of unbounded recursion.
            int instanceId = this.BrazeInstanceId();
            if (!SourceHashVisiting.Add(instanceId)) return instanceId;
            try
            {
                unchecked
                {
                    int hash = sources != null ? sources.Count : 0;
                    if (sources == null) return hash;
                    for (int i = 0; i < sources.Count; i++)
                    {
                        Source source = sources[i];
                        hash = hash * 31 + (source?.enabled == true ? 1 : 0);
                        hash = hash * 31 + (source?.gameObject != null ? source.gameObject.BrazeInstanceId() : 0);
                        hash = hash * 31 + (source?.materialOverride != null ? source.materialOverride.BrazeInstanceId() : 0);
                        if (source?.gameObject == null) continue;
                        source.gameObject.GetComponentsInChildren(true, _hashRenderers);
                        hash = hash * 31 + _hashRenderers.Count;
                        for (int r = 0; r < _hashRenderers.Count; r++)
                        {
                            Renderer renderer = _hashRenderers[r];
                            Mesh mesh = MeshFor(renderer);
                            hash = hash * 31 + (mesh != null ? mesh.BrazeInstanceId() : 0);
                            hash = hash * 31 + (mesh != null ? mesh.vertexCount : 0);
                            hash = hash * 31 + (mesh != null ? mesh.bounds.GetHashCode() : 0);
                            hash = hash * 31 + renderer.transform.localToWorldMatrix.GetHashCode();
                            renderer.GetSharedMaterials(_hashMaterials);
                            for (int m = 0; m < _hashMaterials.Count; m++)
                                hash = hash * 31 + (_hashMaterials[m] != null ? _hashMaterials[m].BrazeInstanceId() : 0);
                        }
                        LODGroup group = source.gameObject.GetComponentInChildren<LODGroup>(true);
                        if (group != null)
                        {
                            LOD[] lods = group.GetLODs();
                            hash = hash * 31 + lods.Length;
                            for (int l = 0; l < lods.Length; l++) hash = hash * 31 + lods[l].screenRelativeTransitionHeight.GetHashCode();
                        }
                        source.gameObject.GetComponentsInChildren(true, _hashChildCloners);
                        for (int n = 0; n < _hashChildCloners.Count; n++)
                        {
                            BrazeCloner child = _hashChildCloners[n];
                            if (child == this) continue;
                            hash = hash * 31 + child.BrazeInstanceId();
                            hash = hash * 31 + (child.isActiveAndEnabled ? 1 : 0);
                            hash = hash * 31 + child.ComputeSourceHash();
                        }
                    }
                    return hash;
                }
            }
            finally
            {
                SourceHashVisiting.Remove(instanceId);
            }
        }

        static Mesh MeshFor(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skinned) return skinned.sharedMesh;
            return renderer != null && renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null;
        }

        public Mesh DistributionMesh => MeshFor(meshInput);

        void RebuildSources()
        {
            ReleaseDraws();
            _sourceRuntimes.Clear();
            _nestedRuntimes.Clear();
            if (sources != null)
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    Source source = sources[i];
                    if (source == null || !source.enabled || source.gameObject == null) continue;
                    SourceRuntime runtime = BuildSource(source);
                    if (runtime == null || (runtime.lods.Count == 0 && runtime.nested.Count == 0)) continue;
                    int sourceIndex = _sourceRuntimes.Count;
                    for (int n = 0; n < runtime.nested.Count; n++)
                    {
                        runtime.nested[n].parentSourceIndex = sourceIndex;
                        _nestedRuntimes.Add(runtime.nested[n]);
                    }
                    _sourceRuntimes.Add(runtime);
                }
            }

            _sourcesDirty = false;
            _drawsDirty = true;
            _buffersDirty = true;
            _preparedHash = 0;
            _preparedFrame = -1;
            if (_sourceRuntimes.Count == 0)
                _status = "No supported MeshRenderer or nested Braze Cloner was found in the enabled sources.";
        }

        SourceRuntime BuildSource(Source source)
        {
            var runtime = new SourceRuntime();
            Transform root = source.gameObject.transform;

            List<Transform> excluded = null;
            BrazeCloner[] childCloners = source.gameObject.GetComponentsInChildren<BrazeCloner>(true);
            for (int i = 0; i < childCloners.Length; i++)
            {
                BrazeCloner child = childCloners[i];
                if (child == this) continue;
                runtime.nested.Add(new NestedRuntime { child = child, sourceRoot = root });
                excluded ??= new List<Transform>();
                excluded.Add(child.transform);
                if (child.sources == null) continue;
                // The nested cloner's source templates are its cloning content, not static parts of the
                // composition; templates living inside this hierarchy would otherwise be drawn twice.
                for (int s = 0; s < child.sources.Count; s++)
                {
                    Transform template = child.sources[s]?.gameObject != null
                        ? child.sources[s].gameObject.transform : null;
                    if (template != null && template.IsChildOf(root)) excluded.Add(template);
                }
            }

            LODGroup group = source.gameObject.GetComponentInChildren<LODGroup>(true);
            if (group != null)
            {
                LOD[] lods = group.GetLODs();
                int lodCount = Mathf.Min(MaxLods, lods.Length);
                for (int l = 0; l < lodCount; l++)
                {
                    var lod = new LodRuntime { transitionHeight = lods[l].screenRelativeTransitionHeight };
                    Renderer[] renderers = lods[l].renderers;
                    for (int r = 0; r < renderers.Length; r++) AddRendererParts(lod.parts, renderers[r], root, source.materialOverride, excluded);
                    if (lod.parts.Count > 0) runtime.lods.Add(lod);
                }
            }
            else
            {
                var lod = new LodRuntime { transitionHeight = 0f };
                Renderer[] renderers = source.gameObject.GetComponentsInChildren<Renderer>(true);
                for (int r = 0; r < renderers.Length; r++) AddRendererParts(lod.parts, renderers[r], root, source.materialOverride, excluded);
                if (lod.parts.Count > 0) runtime.lods.Add(lod);
            }
            runtime.radius = CalculateSourceRadius(runtime);
            return runtime;
        }

        static bool IsExcluded(Transform transform, List<Transform> excluded)
        {
            if (excluded == null) return false;
            for (int i = 0; i < excluded.Count; i++)
                if (transform.IsChildOf(excluded[i])) return true;
            return false;
        }

        static void AddRendererParts(List<Part> parts, Renderer renderer, Transform root, Material materialOverride,
            List<Transform> excluded)
        {
            if (renderer == null || IsExcluded(renderer.transform, excluded)) return;
            Mesh mesh = MeshFor(renderer);
            if (mesh == null || mesh.subMeshCount == 0) return;
            Matrix4x4 partMatrix = root.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
            Material[] materials = renderer.sharedMaterials;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetIndexCount(s) == 0) continue;
                Material material = materialOverride != null ? materialOverride
                    : materials.Length > 0 ? materials[Mathf.Min(s, materials.Length - 1)] : null;
                parts.Add(new Part { mesh = mesh, submesh = s, sourceMaterial = material, matrix = partMatrix });
            }
        }

        static float CalculateSourceRadius(SourceRuntime source)
        {
            float radius = 0.01f;
            var vertices = new List<Vector3>();
            var evaluated = new HashSet<(int mesh, int matrix)>();
            for (int l = 0; l < source.lods.Count; l++)
            for (int p = 0; p < source.lods[l].parts.Count; p++)
            {
                Part part = source.lods[l].parts[p];
                if (!evaluated.Add((part.mesh.BrazeInstanceId(), part.matrix.GetHashCode()))) continue;
                if (part.mesh.isReadable)
                {
                    vertices.Clear();
                    part.mesh.GetVertices(vertices);
                    for (int vertex = 0; vertex < vertices.Count; vertex++)
                        radius = Mathf.Max(radius, part.matrix.MultiplyPoint3x4(vertices[vertex]).magnitude);
                    if (vertices.Count > 0) continue;
                }

                // Non-readable inputs fall back to their conservative authored bounds.
                Bounds b = part.mesh.bounds;
                Vector3 c = b.center, e = b.extents;
                for (int mask = 0; mask < 8; mask++)
                {
                    Vector3 corner = c + Vector3.Scale(e, new Vector3((mask & 1) == 0 ? -1 : 1,
                        (mask & 2) == 0 ? -1 : 1, (mask & 4) == 0 ? -1 : 1));
                    radius = Mathf.Max(radius, part.matrix.MultiplyPoint3x4(corner).magnitude);
                }
            }
            return radius;
        }

        void EnsureDraws()
        {
            int nestedDrawHash = 17;
            unchecked
            {
                for (int i = 0; i < _nestedRuntimes.Count; i++)
                {
                    NestedRuntime nested = _nestedRuntimes[i];
                    nestedDrawHash = nestedDrawHash * 31 + (nested.child != null ? nested.child.BrazeInstanceId() : 0);
                    nestedDrawHash = nestedDrawHash * 31 + (nested.ready ? 1 : 0);
                    nestedDrawHash = nestedDrawHash * 31 + (nested.ready ? nested.child._sourceHash : 0);
                }
            }
            if (!_drawsDirty && nestedDrawHash == _nestedDrawHash) return;

            ReleaseDraws();
            BuildDraws();
            for (int i = 0; i < _nestedRuntimes.Count; i++)
                if (_nestedRuntimes[i].ready) BuildNestedDraws(_nestedRuntimes[i]);
            _drawsDirty = false;
            _nestedDrawHash = nestedDrawHash;
            _buffersDirty = true;
            if (_draws.Count == 0 && _nestedRuntimes.Count > 0)
                _status = "Nested Braze Cloner sources are inactive or empty.";
        }

        void BuildNestedDraws(NestedRuntime nested)
        {
            Shader shader = BrazeRuntimeShaderLibrary.Find("BrazeFX/Cloner/Lit");
            if (shader == null) return;
            List<SourceRuntime> childSources = nested.child._sourceRuntimes;
            for (int s = 0; s < childSources.Count; s++)
            {
                SourceRuntime source = childSources[s];
                for (int l = 0; l < source.lods.Count; l++)
                for (int p = 0; p < source.lods[l].parts.Count; p++)
                {
                    Part part = source.lods[l].parts[p];
                    Material material = AdaptMaterial(part.sourceMaterial, shader, out bool ownsMaterial);
                    var properties = new MaterialPropertyBlock();
                    properties.SetMatrix(IdPartMatrix, part.matrix);
                    properties.SetMatrix(IdPartNormal, part.matrix.inverse.transpose);
                    _draws.Add(new DrawRuntime
                    {
                        mesh = part.mesh,
                        submesh = part.submesh,
                        material = material,
                        properties = properties,
                        bucket = s * MaxLods + l,
                        nested = nested,
                        ownsMaterial = ownsMaterial,
                        baseCull = material.HasProperty("_Cull")
                            ? (CullMode)Mathf.Clamp(Mathf.RoundToInt(material.GetFloat("_Cull")), 0, 2)
                            : CullMode.Back,
                    });
                }
            }
        }

        void BuildDraws()
        {
            Shader shader = BrazeRuntimeShaderLibrary.Find("BrazeFX/Cloner/Lit");
            if (shader == null) { _status = "BrazeFX/Cloner/Lit shader is missing."; return; }
            for (int s = 0; s < _sourceRuntimes.Count; s++)
            {
                SourceRuntime source = _sourceRuntimes[s];
                for (int l = 0; l < source.lods.Count; l++)
                for (int p = 0; p < source.lods[l].parts.Count; p++)
                {
                    Part part = source.lods[l].parts[p];
                    Material material = AdaptMaterial(part.sourceMaterial, shader, out bool ownsMaterial);
                    var properties = new MaterialPropertyBlock();
                    properties.SetInt(IdBucketOffset, (s * MaxLods + l)); // multiplied by sourceCapacity at submit time
                    properties.SetMatrix(IdPartMatrix, part.matrix);
                    properties.SetMatrix(IdPartNormal, part.matrix.inverse.transpose);
                    _draws.Add(new DrawRuntime
                    {
                        mesh = part.mesh,
                        submesh = part.submesh,
                        material = material,
                        properties = properties,
                        bucket = s * MaxLods + l,
                        ownsMaterial = ownsMaterial,
                        baseCull = material.HasProperty("_Cull")
                            ? (CullMode)Mathf.Clamp(Mathf.RoundToInt(material.GetFloat("_Cull")), 0, 2)
                            : CullMode.Back,
                    });
                }
            }
        }

        Material AdaptMaterial(Material original, Shader shader, out bool ownsMaterial)
        {
            int key = original != null ? original.BrazeInstanceId() : 0;
            if (_materialCache.TryGetValue(key, out Material cached) && cached != null)
            {
                ownsMaterial = false;
                return cached;
            }
            var material = new Material(shader) { name = (original != null ? original.name : "Default") + " (Braze Cloner)" };
            material.hideFlags = HideFlags.HideAndDontSave;
            SynchronizeAdaptedMaterial(original, material);
            _materialCache[key] = material;
            _materialSources[key] = original;
            _materialContentHashes[key] = MaterialContentHash(original);
            ownsMaterial = true;
            return material;
        }

        void RefreshAdaptedMaterials()
        {
            int frame = Time.frameCount;
            if (Application.isPlaying && _materialSyncFrame == frame) return;
            _materialSyncFrame = frame;

            bool changed = false;
            foreach (KeyValuePair<int, Material> pair in _materialSources)
            {
                Material source = pair.Value;
                if (source == null || !_materialCache.TryGetValue(pair.Key, out Material adapted) || adapted == null)
                    continue;
                int contentHash = MaterialContentHash(source);
                if (_materialContentHashes.TryGetValue(pair.Key, out int previousHash) && previousHash == contentHash)
                    continue;
                SynchronizeAdaptedMaterial(source, adapted);
                _materialContentHashes[pair.Key] = contentHash;
                changed = true;
            }
            if (!changed) return;
            for (int i = 0; i < _draws.Count; i++)
            {
                Material material = _draws[i].material;
                _draws[i].baseCull = material != null && material.HasProperty("_Cull")
                    ? (CullMode)Mathf.Clamp(Mathf.RoundToInt(material.GetFloat("_Cull")), 0, 2)
                    : CullMode.Back;
            }
        }

        static int MaterialContentHash(Material material)
        {
            if (material == null) return 0;
            unchecked
            {
                int hash = material.ComputeCRC();
                hash = hash * 31 + material.renderQueue;
                hash = hash * 31 + (material.shader != null ? material.shader.BrazeInstanceId() : 0);
                hash = hash * 31 + material.GetTag("RenderType", false, string.Empty).GetHashCode();
                return hash;
            }
        }

        internal static void SynchronizeAdaptedMaterial(Material source, Material adapted)
        {
            if (source == null || adapted == null) return;

            adapted.CopyPropertiesFromMaterial(source);
            adapted.name = source.name + " (Braze Cloner)";
            if (source.HasProperty("_Color") && !source.HasProperty("_BaseColor"))
                adapted.SetColor("_BaseColor", source.GetColor("_Color"));
            if (source.HasProperty("_MainTex") && !source.HasProperty("_BaseMap"))
            {
                adapted.SetTexture("_BaseMap", source.GetTexture("_MainTex"));
                adapted.SetTextureScale("_BaseMap", source.GetTextureScale("_MainTex"));
                adapted.SetTextureOffset("_BaseMap", source.GetTextureOffset("_MainTex"));
            }
            if (source.HasProperty("_Glossiness") && !source.HasProperty("_Smoothness"))
                adapted.SetFloat("_Smoothness", source.GetFloat("_Glossiness"));

            bool alphaClip = source.IsKeywordEnabled("_ALPHATEST_ON") ||
                             source.HasProperty("_AlphaClip") && source.GetFloat("_AlphaClip") > 0.5f;
            string renderType = source.GetTag("RenderType", false, string.Empty);
            bool transparent = source.HasProperty("_Surface")
                ? source.GetFloat("_Surface") > 0.5f
                : source.renderQueue >= (int)RenderQueue.Transparent || renderType == "Transparent";
            if (adapted.HasProperty("_Surface")) adapted.SetFloat("_Surface", transparent ? 1f : 0f);
            if (adapted.HasProperty("_AlphaClip")) adapted.SetFloat("_AlphaClip", alphaClip ? 1f : 0f);
            if (adapted.HasProperty("_ZWrite") && !source.HasProperty("_ZWrite"))
                adapted.SetFloat("_ZWrite", transparent ? 0f : 1f);
            if (transparent && !source.HasProperty("_SrcBlend"))
            {
                adapted.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                adapted.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                adapted.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
                adapted.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            }

            adapted.shaderKeywords = Array.Empty<string>();
            CopyMaterialKeyword(source, adapted, "_NORMALMAP",
                source.HasProperty("_BumpMap") && source.GetTexture("_BumpMap") != null);
            CopyMaterialKeyword(source, adapted, "_METALLICSPECGLOSSMAP");
            CopyMaterialKeyword(source, adapted, "_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A");
            CopyMaterialKeyword(source, adapted, "_SPECULAR_SETUP");
            CopyMaterialKeyword(source, adapted, "_RECEIVE_SHADOWS_OFF");
            CopyMaterialKeyword(source, adapted, "_SPECULARHIGHLIGHTS_OFF");
            CopyMaterialKeyword(source, adapted, "_ENVIRONMENTREFLECTIONS_OFF");
            SetMaterialKeyword(adapted, "_ALPHATEST_ON", alphaClip);
            SetMaterialKeyword(adapted, "_SURFACE_TYPE_TRANSPARENT", transparent);
            CopyMaterialKeyword(source, adapted, "_ALPHAPREMULTIPLY_ON");
            CopyMaterialKeyword(source, adapted, "_ALPHAMODULATE_ON");

            adapted.renderQueue = source.renderQueue;
            adapted.SetOverrideTag("RenderType", transparent ? "Transparent" : alphaClip ? "TransparentCutout" : "Opaque");
            adapted.doubleSidedGI = source.doubleSidedGI;
            adapted.globalIlluminationFlags = source.globalIlluminationFlags;
            adapted.SetShaderPassEnabled("ShadowCaster", source.GetShaderPassEnabled("ShadowCaster"));
            adapted.SetShaderPassEnabled("DepthOnly", source.GetShaderPassEnabled("DepthOnly"));
            adapted.SetShaderPassEnabled("DepthNormals", source.GetShaderPassEnabled("DepthNormals"));
        }

        static void CopyMaterialKeyword(Material source, Material adapted, string keyword, bool fallback = false)
            => SetMaterialKeyword(adapted, keyword, source.IsKeywordEnabled(keyword) || fallback);

        static void SetMaterialKeyword(Material material, string keyword, bool enabled)
        {
            if (enabled) material.EnableKeyword(keyword); else material.DisableKeyword(keyword);
        }

        bool EnsureBuffers(int instances)
        {
            int sourceCount = _sourceRuntimes.Count;
            int sourceCapacity = Mathf.CeilToInt(instances / (float)Mathf.Max(1, sourceCount));
            int bucketCount = sourceCount * MaxLods;
            long visibleCount = (long)sourceCapacity * bucketCount;

            // Nested regions are appended after this cloner's own buckets. Each ready child gets its own
            // bucket range and visible-index stride so composed instances never contend with parent slots.
            int nestedLayoutHash = 17;
            unchecked
            {
                for (int i = 0; i < _nestedRuntimes.Count; i++)
                {
                    NestedRuntime nested = _nestedRuntimes[i];
                    nested.bucketBase = 0;
                    nested.capacity = 0;
                    nested.visibleOffset = 0;
                    if (nested.ready)
                    {
                        long childPerSource = (nested.childInstances + nested.childSourceCount - 1L) /
                                              nested.childSourceCount;
                        long capacity = (long)sourceCapacity * childPerSource;
                        long composed = (long)instances * nested.childInstances;
                        if (capacity > int.MaxValue || composed > int.MaxValue)
                        {
                            nested.ready = false;
                            _status = $"Nested cloner '{nested.child.name}' exceeds the composed instance limit.";
                        }
                        else
                        {
                            nested.bucketBase = bucketCount;
                            nested.capacity = (int)capacity;
                            nested.visibleOffset = visibleCount;
                            bucketCount += nested.childSourceCount * MaxLods;
                            visibleCount += capacity * nested.childSourceCount * MaxLods;
                        }
                    }
                    nestedLayoutHash = (((nestedLayoutHash * 31 + nested.bucketBase) * 31 + nested.capacity) * 31 +
                                        (int)nested.visibleOffset) * 31 + nested.childInstances;
                }
            }

            bool resize = _buffersDirty || _allocatedInstances != instances || _sourceCapacity != sourceCapacity ||
                          _bucketCount != bucketCount || _nestedLayoutHash != nestedLayoutHash ||
                          _argsBuffer == null || _argsBuffer.count != _draws.Count;
            if (!resize) return true;

            ReleaseBuffers();
            try
            {
                _basePosition = NewBuffer(instances, TransformStride);
                _baseRotation = NewBuffer(instances, TransformStride);
                _baseScale = NewBuffer(instances, TransformStride);
                _effectorPosition = NewBuffer(instances, EffectorTransformStride);
                _effectorPositionScratch = NewBuffer(instances, EffectorTransformStride);
                _effectorRotation = NewBuffer(instances, EffectorTransformStride);
                _effectorScale = NewBuffer(instances, EffectorTransformStride);
                if (visibleCount > int.MaxValue) throw new InvalidOperationException("Visible-index storage exceeds Unity's buffer element limit.");
                _visibleIndices = NewBuffer((int)visibleCount, sizeof(uint));
                _bucketCounts = NewBuffer(bucketCount, sizeof(uint));
                _sourceBuffer = NewBuffer(sourceCount, SourceStride);
                _drawBucketBuffer = NewBuffer(Mathf.Max(1, _draws.Count), sizeof(uint) * 2);
                _argsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured,
                    Mathf.Max(1, _draws.Count), GraphicsBuffer.IndirectDrawIndexedArgs.size);

                UploadSourceData();
                UploadDrawData(sourceCapacity);
                _allocatedInstances = instances;
                _sourceCapacity = sourceCapacity;
                _bucketCount = bucketCount;
                _nestedLayoutHash = nestedLayoutHash;
                _buffersDirty = false;
                _preparedHash = 0;
                _preparedFrame = -1;
                CalculateGpuBytes();
                UpdateStatus(instances);
                return true;
            }
            catch (Exception exception)
            {
                ReleaseBuffers();
                _status = "GPU allocation failed: " + exception.Message;
                Debug.LogError("[BrazeFX Cloner] " + _status, this);
                return false;
            }
        }

        static GraphicsBuffer NewBuffer(int count, int stride)
            => new(GraphicsBuffer.Target.Structured, Mathf.Max(1, count), stride);

        bool EnsurePushApartBuffers(int instances)
        {
            long desired = Math.Max(64L, (long)instances * 2L);
            if (desired > 1L << 30)
            {
                _status = "Push Apart spatial grid exceeds Unity's buffer element limit.";
                return false;
            }

            int gridSize = 64;
            while (gridSize < desired) gridSize <<= 1;
            if (_pushGridHeads != null && _pushGridSize == gridSize &&
                _pushGridNext != null && _pushGridNext.count == instances &&
                _pushGridCells != null && _pushRadii != null && _pushMaxRadius != null &&
                _pushInputPosition != null && _pushInputPosition.count == instances)
                return true;

            Release(ref _pushGridHeads);
            Release(ref _pushGridNext);
            Release(ref _pushGridCells);
            Release(ref _pushRadii);
            Release(ref _pushMaxRadius);
            Release(ref _pushInputPosition);
            _pushGridSize = 0;
            try
            {
                _pushGridHeads = NewBuffer(gridSize, sizeof(int));
                _pushGridNext = NewBuffer(instances, sizeof(int));
                _pushGridCells = NewBuffer(instances, sizeof(int) * 4);
                _pushRadii = NewBuffer(instances, sizeof(float));
                _pushMaxRadius = NewBuffer(1, sizeof(uint));
                _pushInputPosition = NewBuffer(instances, EffectorTransformStride);
                _pushGridSize = gridSize;
                CalculateGpuBytes();
                return true;
            }
            catch (Exception exception)
            {
                Release(ref _pushGridHeads);
                Release(ref _pushGridNext);
                Release(ref _pushGridCells);
                Release(ref _pushRadii);
                Release(ref _pushMaxRadius);
                Release(ref _pushInputPosition);
                _pushGridSize = 0;
                _status = "Push Apart GPU allocation failed: " + exception.Message;
                Debug.LogError("[BrazeFX Cloner] " + _status, this);
                return false;
            }
        }

        bool EnsureMeshDistributionSamples(int instances)
        {
            if (distribution == Distribution.Spline) return EnsureSplineDistributionSamples(instances);
            if (distribution != Distribution.Mesh)
            {
                if (_meshSampleBuffer == null || _meshSampleBuffer.count != 1)
                {
                    Release(ref _meshSampleBuffer);
                    _meshSampleBuffer = NewBuffer(1, MeshSampleStride);
                    _meshSampleBuffer.SetData(new[]
                    {
                        new GpuMeshSample { normal = Vector3.up }
                    });
                    CalculateGpuBytes();
                    // Status only when something actually changed: this branch runs per camera per frame,
                    // and unconditional UpdateStatus here built garbage strings on every Grid/Linear/Radial
                    // prepare. Instance-count changes refresh the status through EnsureBuffers instead.
                    UpdateStatus(instances);
                }
                _meshSampleHash = int.MinValue;
                return true;
            }

            Mesh mesh = DistributionMesh;
            if (meshInput == null || mesh == null)
            {
                _status = "Mesh distribution needs a Mesh Input renderer with a valid mesh.";
                return false;
            }
            if (!mesh.isReadable)
            {
                _status = $"Mesh distribution cannot read '{mesh.name}'. Enable Read/Write on its import settings.";
                return false;
            }

            int sampleHash;
            unchecked
            {
                sampleHash = mesh.BrazeInstanceId();
                sampleHash = sampleHash * 31 + mesh.vertexCount;
                sampleHash = sampleHash * 31 + mesh.subMeshCount;
                sampleHash = sampleHash * 31 + mesh.bounds.GetHashCode();
                sampleHash = sampleHash * 31 + instances;
                sampleHash = sampleHash * 31 + seed;
                sampleHash = sampleHash * 31 + (int)meshSampling;
                if (meshSampling == MeshSampling.Surface)
                {
                    Matrix4x4 matrix = meshInput.transform.localToWorldMatrix;
                    sampleHash = sampleHash * 31 + new Vector3(matrix.m00, matrix.m10, matrix.m20).GetHashCode();
                    sampleHash = sampleHash * 31 + new Vector3(matrix.m01, matrix.m11, matrix.m21).GetHashCode();
                    sampleHash = sampleHash * 31 + new Vector3(matrix.m02, matrix.m12, matrix.m22).GetHashCode();
                }
            }
            if (_meshSampleBuffer != null && _meshSampleBuffer.count == instances &&
                _meshSampleHash == sampleHash)
                return true;

            _meshVertices.Clear();
            _meshNormals.Clear();
            mesh.GetVertices(_meshVertices);
            mesh.GetNormals(_meshNormals);
            int vertexCount = _meshVertices.Count;
            if (vertexCount == 0)
            {
                _status = $"Mesh distribution input '{mesh.name}' has no vertices.";
                return false;
            }

            if (_meshSampleData.Length != instances) _meshSampleData = new GpuMeshSample[instances];
            bool hasNormals = _meshNormals.Count == vertexCount;
            if (meshSampling == MeshSampling.Vertices)
            {
                int start = PositiveModulo(seed, vertexCount);
                for (int i = 0; i < instances; i++)
                {
                    int vertex = (start + i) % vertexCount;
                    Vector3 position = _meshVertices[vertex];
                    Vector3 normal = hasNormals ? _meshNormals[vertex].normalized : FallbackMeshNormal(position);
                    if (normal.sqrMagnitude <= 1e-10f) normal = FallbackMeshNormal(position);
                    _meshSampleData[i] = new GpuMeshSample { position = position, normal = normal };
                }
            }
            else
            {
                if (!GatherMeshTriangles(mesh))
                {
                    _status = $"Mesh distribution input '{mesh.name}' has no triangle surface.";
                    return false;
                }

                _meshTriangleCdf.Clear();
                float totalArea = 0f;
                int triangleCount = _meshTriangles.Count / 3;
                int validIndexCount = 0;
                Matrix4x4 surfaceMatrix = meshInput.transform.localToWorldMatrix;
                for (int triangle = 0; triangle < triangleCount; triangle++)
                {
                    int index = triangle * 3;
                    int ia = _meshTriangles[index];
                    int ib = _meshTriangles[index + 1];
                    int ic = _meshTriangles[index + 2];
                    Vector3 a = _meshVertices[ia];
                    Vector3 b = _meshVertices[ib];
                    Vector3 c = _meshVertices[ic];
                    Vector3 ab = surfaceMatrix.MultiplyVector(b - a);
                    Vector3 ac = surfaceMatrix.MultiplyVector(c - a);
                    float area = Vector3.Cross(ab, ac).magnitude * 0.5f;
                    if (!(area > 1e-8f) || float.IsNaN(area) || float.IsInfinity(area)) continue;
                    _meshTriangles[validIndexCount++] = ia;
                    _meshTriangles[validIndexCount++] = ib;
                    _meshTriangles[validIndexCount++] = ic;
                    totalArea += area;
                    _meshTriangleCdf.Add(totalArea);
                }
                if (validIndexCount < _meshTriangles.Count)
                    _meshTriangles.RemoveRange(validIndexCount, _meshTriangles.Count - validIndexCount);
                if (_meshTriangleCdf.Count == 0)
                {
                    _status = $"Mesh distribution input '{mesh.name}' has zero surface area.";
                    return false;
                }

                var random = new System.Random(seed ^ (instances * 73856093));
                for (int i = 0; i < instances; i++)
                {
                    int triangle = UpperBound(_meshTriangleCdf, (float)random.NextDouble() * totalArea);
                    int index = triangle * 3;
                    int ia = _meshTriangles[index];
                    int ib = _meshTriangles[index + 1];
                    int ic = _meshTriangles[index + 2];
                    float root = Mathf.Sqrt((float)random.NextDouble());
                    float randomB = (float)random.NextDouble();
                    float wa = 1f - root;
                    float wb = root * (1f - randomB);
                    float wc = root * randomB;
                    Vector3 a = _meshVertices[ia];
                    Vector3 b = _meshVertices[ib];
                    Vector3 c = _meshVertices[ic];
                    Vector3 position = a * wa + b * wb + c * wc;
                    Vector3 faceNormal = Vector3.Cross(b - a, c - a).normalized;
                    Vector3 normal = hasNormals
                        ? (_meshNormals[ia] * wa + _meshNormals[ib] * wb + _meshNormals[ic] * wc).normalized
                        : faceNormal;
                    if (normal.sqrMagnitude <= 1e-10f) normal = FallbackMeshNormal(position);
                    _meshSampleData[i] = new GpuMeshSample { position = position, normal = normal };
                }
            }

            Release(ref _meshSampleBuffer);
            _meshSampleBuffer = NewBuffer(instances, MeshSampleStride);
            _meshSampleBuffer.SetData(_meshSampleData);
            _meshSampleHash = sampleHash;
            CalculateGpuBytes();
            UpdateStatus(instances);
            return true;
        }

        /// <summary>
        /// Bakes an arc-length-uniform position/tangent LUT from the spline, in spline-local space, into the
        /// shared distribution sample buffer. LUT resolution is independent of the clone count, so count,
        /// range, and offset animate as pure GPU uniforms; only spline edits (version) trigger a rebake.
        /// </summary>
        bool EnsureSplineDistributionSamples(int instances)
        {
            BrazeSpline spline = splineInput;
            if (spline == null || spline.points == null || spline.points.Count < 2)
            {
                _status = "Spline distribution needs a Braze Spline input with at least two points.";
                return false;
            }

            int lutCount = Mathf.Clamp(spline.SampleCount * 2, 64, 2048);
            int sampleHash;
            unchecked
            {
                sampleHash = spline.BrazeInstanceId();
                sampleHash = sampleHash * 31 + spline.version;
                sampleHash = sampleHash * 31 + lutCount;
                sampleHash = sampleHash * 31 + (int)Distribution.Spline * 397;
            }
            if (_meshSampleBuffer != null && _meshSampleBuffer.count == lutCount && _meshSampleHash == sampleHash)
                return true;

            int denseCount = Mathf.Clamp(lutCount * 2, 128, 4096);
            if (_splineDense == null || _splineDense.Length < denseCount)
            {
                _splineDense = new Vector3[Mathf.NextPowerOfTwo(denseCount)];
                _splineDenseLengths = new float[_splineDense.Length];
            }
            _splineDense[0] = spline.EvaluateLocal(0f);
            _splineDenseLengths[0] = 0f;
            for (int i = 1; i < denseCount; i++)
            {
                _splineDense[i] = spline.EvaluateLocal(i / (float)(denseCount - 1));
                _splineDenseLengths[i] = _splineDenseLengths[i - 1] + Vector3.Distance(_splineDense[i - 1], _splineDense[i]);
            }
            float totalLength = _splineDenseLengths[denseCount - 1];

            if (_meshSampleData.Length != lutCount) _meshSampleData = new GpuMeshSample[lutCount];
            if (totalLength <= 1e-6f)
            {
                for (int i = 0; i < lutCount; i++)
                    _meshSampleData[i] = new GpuMeshSample { position = _splineDense[0], normal = Vector3.forward };
            }
            else
            {
                int cursor = 1;
                for (int i = 0; i < lutCount; i++)
                {
                    float target = totalLength * i / (lutCount - 1f);
                    while (cursor < denseCount - 1 && _splineDenseLengths[cursor] < target) cursor++;
                    float segment = _splineDenseLengths[cursor] - _splineDenseLengths[cursor - 1];
                    float t = segment > 1e-8f ? Mathf.Clamp01((target - _splineDenseLengths[cursor - 1]) / segment) : 0f;
                    _meshSampleData[i] = new GpuMeshSample
                    {
                        position = Vector3.LerpUnclamped(_splineDense[cursor - 1], _splineDense[cursor], t),
                    };
                }
                for (int i = 0; i < lutCount; i++)
                {
                    // Central difference over the resampled points; a closed spline shares its first and last
                    // sample, so the seam tangent reads across the wrap instead of collapsing to zero.
                    int previous = i > 0 ? i - 1 : (spline.closed ? lutCount - 2 : 0);
                    int next = i < lutCount - 1 ? i + 1 : (spline.closed ? 1 : lutCount - 1);
                    Vector3 tangent = _meshSampleData[next].position - _meshSampleData[previous].position;
                    _meshSampleData[i].normal = tangent.sqrMagnitude > 1e-12f ? tangent.normalized : Vector3.forward;
                }
            }

            Release(ref _meshSampleBuffer);
            _meshSampleBuffer = NewBuffer(lutCount, MeshSampleStride);
            _meshSampleBuffer.SetData(_meshSampleData, 0, 0, lutCount);
            _meshSampleHash = sampleHash;
            CalculateGpuBytes();
            UpdateStatus(instances);
            return true;
        }

        bool GatherMeshTriangles(Mesh mesh)
        {
            _meshTriangles.Clear();
            _meshSubmeshTriangles.Clear();
            for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
            {
                if (mesh.GetTopology(submesh) != MeshTopology.Triangles) continue;
                _meshSubmeshTriangles.Clear();
                mesh.GetTriangles(_meshSubmeshTriangles, submesh);
                for (int i = 0; i < _meshSubmeshTriangles.Count; i++)
                {
                    int vertex = _meshSubmeshTriangles[i];
                    if (vertex < 0 || vertex >= _meshVertices.Count) return false;
                    _meshTriangles.Add(vertex);
                }
            }
            return _meshTriangles.Count >= 3;
        }

        static int UpperBound(List<float> cumulative, float value)
        {
            int low = 0, high = cumulative.Count - 1;
            while (low < high)
            {
                int middle = (low + high) >> 1;
                if (value <= cumulative[middle]) high = middle; else low = middle + 1;
            }
            return low;
        }

        static int PositiveModulo(int value, int divisor)
        {
            int result = value % divisor;
            return result < 0 ? result + divisor : result;
        }

        static Vector3 FallbackMeshNormal(Vector3 position)
            => position.sqrMagnitude > 1e-10f ? position.normalized : Vector3.up;

        void UploadSourceData()
        {
            var data = new GpuSource[_sourceRuntimes.Count];
            for (int s = 0; s < data.Length; s++)
            {
                SourceRuntime source = _sourceRuntimes[s];
                Vector4 thresholds = Vector4.zero;
                for (int l = 0; l < Mathf.Min(MaxLods, source.lods.Count); l++) thresholds[l] = source.lods[l].transitionHeight;
                data[s] = new GpuSource { radius = source.radius, lodCount = (uint)source.lods.Count, thresholds = thresholds };
            }
            _sourceBuffer.SetData(data);
        }

        void UploadDrawData(int sourceCapacity)
        {
            var args = new GraphicsBuffer.IndirectDrawIndexedArgs[Mathf.Max(1, _draws.Count)];
            var buckets = new uint[Mathf.Max(1, _draws.Count) * 2];
            for (int i = 0; i < _draws.Count; i++)
            {
                Mesh mesh = _draws[i].mesh;
                int submesh = _draws[i].submesh;
                args[i] = new GraphicsBuffer.IndirectDrawIndexedArgs
                {
                    indexCountPerInstance = mesh.GetIndexCount(submesh),
                    instanceCount = 0,
                    startIndex = mesh.GetIndexStart(submesh),
                    baseVertexIndex = (uint)mesh.GetBaseVertex(submesh),
                    startInstance = 0,
                };
                NestedRuntime nested = _draws[i].nested;
                buckets[i * 2] = (uint)(nested == null ? _draws[i].bucket : nested.bucketBase + _draws[i].bucket);
                buckets[i * 2 + 1] = (uint)(nested == null ? sourceCapacity : nested.capacity);
            }
            _argsBuffer.SetData(args);
            _drawBucketBuffer.SetData(buckets);
        }

        void PrepareTransforms(int instances)
        {
            int contentHash = TransformContentHash(instances);
            // Static cloners retain their GPU transforms indefinitely. Animating effectors fold Time.frameCount into
            // contentHash, which still evaluates exactly once for all cameras in that frame.
            if (_preparedFrame >= 0 && _preparedHash == contentHash) return;
            _preparedHash = contentHash;
            _preparedFrame = Time.frameCount;

            _localDistributionBounds = CalculateLocalDistributionBounds();
            BindBuildParameters(instances);
            _clonerCompute.SetBuffer(_kBuild, "_BasePosition", _basePosition);
            _clonerCompute.SetBuffer(_kBuild, "_BaseRotation", _baseRotation);
            _clonerCompute.SetBuffer(_kBuild, "_BaseScale", _baseScale);
            _clonerCompute.SetBuffer(_kBuild, "_MeshSamples", _meshSampleBuffer);
            DispatchInstances(_clonerCompute, _kBuild, instances);

            int effectorCount = GatherGpuEffectors();
            BindEffectorGlobals(effectorCount, instances);
            _effectorCompute.SetBuffer(_kClearEffectors, "_EffectorPosition", _effectorPosition);
            _effectorCompute.SetBuffer(_kClearEffectors, "_EffectorRotation", _effectorRotation);
            _effectorCompute.SetBuffer(_kClearEffectors, "_EffectorScale", _effectorScale);
            DispatchInstances(_effectorCompute, _kClearEffectors, instances);

            _delayStates.BeginEvaluation();
            _pushApartStates.BeginEvaluation();
            for (int i = 0; i < effectorCount; i++)
            {
                if (_effectorScratch[i] is BrazePushApartEffector pushApart)
                {
                    if (!EnsurePushApartBuffers(instances))
                    {
                        _preparedHash = int.MinValue;
                        _preparedFrame = -1;
                        continue;
                    }

                    _clonerCompute.SetInt("_EffectorIndex", i);
                    _clonerCompute.SetInt("_EffectorCount", effectorCount);
                    _clonerCompute.SetInt("_InstanceCount", instances);
                    _clonerCompute.SetInt("_SourceCount", _sourceRuntimes.Count);
                    _clonerCompute.SetInt("_SourceOrder", (int)sourceOrder);
                    _clonerCompute.SetInt("_Seed", seed);
                    _clonerCompute.SetInt("_PushGridSize", _pushGridSize);
                    _clonerCompute.SetInt("_PushGridMask", _pushGridSize - 1);
                    _clonerCompute.SetInt("_PushUseCloneBounds", pushApart.useCloneBounds ? 1 : 0);
                    _clonerCompute.SetFloat("_PushExtraGap", Mathf.Max(0f, pushApart.separationRadius));

                    BrazePushApartGpuState pushState = _pushApartStates.Use(
                        pushApart, instances, i, PushApartBakeHash(pushApart, _sourceHash, sourceOrder, seed));
                    if (pushState.HasBake)
                    {
                        _clonerCompute.SetBuffer(
                            _kApplyPushApartBake, "_EffectorPositionRead", _effectorPosition);
                        _clonerCompute.SetBuffer(
                            _kApplyPushApartBake, "_EffectorPositionWrite", _effectorPositionScratch);
                        _clonerCompute.SetBuffer(
                            _kApplyPushApartBake, "_PushBakeRead", pushState.bakedCorrection);
                        DispatchInstances(_clonerCompute, _kApplyPushApartBake, instances);
                        (_effectorPosition, _effectorPositionScratch) =
                            (_effectorPositionScratch, _effectorPosition);
                        continue;
                    }

                    _clonerCompute.SetBuffer(
                        _kCapturePushApartBakeInput, "_EffectorPositionRead", _effectorPosition);
                    _clonerCompute.SetBuffer(
                        _kCapturePushApartBakeInput, "_PushInputPosition", _pushInputPosition);
                    DispatchInstances(_clonerCompute, _kCapturePushApartBakeInput, instances);

                    _clonerCompute.SetBuffer(_kClearPushApartRadius, "_PushMaxRadius", _pushMaxRadius);
                    _clonerCompute.Dispatch(_kClearPushApartRadius, 1, 1, 1);
                    _clonerCompute.SetBuffer(_kMeasurePushApartRadius, "_Sources", _sourceBuffer);
                    _clonerCompute.SetBuffer(_kMeasurePushApartRadius, "_BaseScaleRead", _baseScale);
                    _clonerCompute.SetBuffer(_kMeasurePushApartRadius, "_EffectorScaleRead", _effectorScale);
                    _clonerCompute.SetBuffer(_kMeasurePushApartRadius, "_PushRadii", _pushRadii);
                    _clonerCompute.SetBuffer(_kMeasurePushApartRadius, "_PushMaxRadius", _pushMaxRadius);
                    DispatchInstances(_clonerCompute, _kMeasurePushApartRadius, instances);

                    _clonerCompute.SetBuffer(_kPushApart, "_Effectors", _effectorBuffer);
                    _clonerCompute.SetBuffer(_kPushApart, "_EffectorFalloff", _effectorFalloffBuffer);
                    _clonerCompute.SetBuffer(_kPushApart, "_BasePositionRead", _basePosition);
                    _clonerCompute.SetBuffer(_kPushApart, "_PushGridHeads", _pushGridHeads);
                    _clonerCompute.SetBuffer(_kPushApart, "_PushGridNext", _pushGridNext);
                    _clonerCompute.SetBuffer(_kPushApart, "_PushGridCells", _pushGridCells);
                    _clonerCompute.SetBuffer(_kPushApart, "_PushRadii", _pushRadii);
                    _clonerCompute.SetBuffer(_kPushApart, "_PushMaxRadius", _pushMaxRadius);
                    _clonerCompute.SetBuffer(_kClearPushApartGrid, "_PushGridHeads", _pushGridHeads);
                    _clonerCompute.SetBuffer(_kBuildPushApartGrid, "_BasePositionRead", _basePosition);
                    _clonerCompute.SetBuffer(_kBuildPushApartGrid, "_PushGridHeads", _pushGridHeads);
                    _clonerCompute.SetBuffer(_kBuildPushApartGrid, "_PushGridNext", _pushGridNext);
                    _clonerCompute.SetBuffer(_kBuildPushApartGrid, "_PushGridCells", _pushGridCells);
                    _clonerCompute.SetBuffer(_kBuildPushApartGrid, "_PushMaxRadius", _pushMaxRadius);
                    for (int pass = 0; pass < Mathf.Clamp(pushApart.iterations, 1, 32); pass++)
                    {
                        DispatchLinear(_clonerCompute, _kClearPushApartGrid, _pushGridSize);
                        _clonerCompute.SetBuffer(_kBuildPushApartGrid, "_EffectorPositionRead", _effectorPosition);
                        DispatchInstances(_clonerCompute, _kBuildPushApartGrid, instances);
                        _clonerCompute.SetBuffer(_kPushApart, "_EffectorPositionRead", _effectorPosition);
                        _clonerCompute.SetBuffer(_kPushApart, "_EffectorPositionWrite", _effectorPositionScratch);
                        DispatchInstances(_clonerCompute, _kPushApart, instances);
                        (_effectorPosition, _effectorPositionScratch) = (_effectorPositionScratch, _effectorPosition);
                    }

                    _clonerCompute.SetBuffer(
                        _kStorePushApartBake, "_EffectorPositionRead", _effectorPosition);
                    _clonerCompute.SetBuffer(
                        _kStorePushApartBake, "_PushInputPosition", _pushInputPosition);
                    _clonerCompute.SetBuffer(
                        _kStorePushApartBake, "_PushBakeWrite", pushState.bakedCorrection);
                    DispatchInstances(_clonerCompute, _kStorePushApartBake, instances);
                    pushState.CommitBake();
                }
                else if (_effectorScratch[i] is BrazeDelayEffector delay)
                {
                    ApplyDelayEffector(delay, i, effectorCount, instances);
                }
                else
                {
                    _effectorCompute.SetInt("_EffectorIndex", i);
                    BindSingleEffector();
                    DispatchInstances(_effectorCompute, _kApplyEffector, instances);
                }
            }
            _delayStates.EndEvaluation();
            _pushApartStates.EndEvaluation();
            CalculateGpuBytes();
        }

        void PrepareAppearanceSpeed(int instances)
        {
            if (!appearanceEnabled || affectedColorMode != AffectedColorMode.Speed)
            {
                _appearanceSpeedHasPrevious = false;
                _appearanceSpeedFrame = -1;
                return;
            }

            if (_appearanceSpeed == null || _appearanceSpeed.count != instances ||
                _appearancePreviousPosition == null || _appearancePreviousPosition.count != instances)
            {
                Release(ref _appearanceSpeed);
                Release(ref _appearancePreviousPosition);
                _appearanceSpeed = NewBuffer(instances, sizeof(float));
                _appearancePreviousPosition = NewBuffer(instances, sizeof(float) * 4);
                _appearanceSpeedHasPrevious = false;
                _appearanceSpeedFrame = -1;
                _appearanceSpeedTime = -1.0;
                CalculateGpuBytes();
            }

            int frame = Time.frameCount;
            if (_appearanceSpeedFrame == frame) return; // particle, trail, and every camera share one measurement

            double now = Time.realtimeSinceStartupAsDouble;
            float deltaTime = _appearanceSpeedHasPrevious
                ? Mathf.Max((float)(now - _appearanceSpeedTime), 1f / 240f)
                : 1f;
            float smoothingBlend = !_appearanceSpeedHasPrevious || affectedSpeedSmoothing <= 1e-4f
                ? 1f
                : 1f - Mathf.Pow(0.5f, deltaTime / affectedSpeedSmoothing);
            _clonerCompute.SetInt("_InstanceCount", instances);
            _clonerCompute.SetInt("_AppearanceHasPrevious", _appearanceSpeedHasPrevious ? 1 : 0);
            _clonerCompute.SetFloat("_AppearanceInvDeltaTime", 1f / deltaTime);
            _clonerCompute.SetFloat("_AppearanceSpeedBlend", smoothingBlend);
            _clonerCompute.SetBuffer(_kMeasureSpeed, "_BasePositionRead", _basePosition);
            _clonerCompute.SetBuffer(_kMeasureSpeed, "_EffectorPositionRead", _effectorPosition);
            _clonerCompute.SetBuffer(_kMeasureSpeed, "_AppearanceSpeed", _appearanceSpeed);
            _clonerCompute.SetBuffer(_kMeasureSpeed, "_AppearancePreviousPosition", _appearancePreviousPosition);
            DispatchInstances(_clonerCompute, _kMeasureSpeed, instances);

            _appearanceSpeedHasPrevious = true;
            _appearanceSpeedFrame = frame;
            _appearanceSpeedTime = now;
        }

        GraphicsBuffer AppearanceSpeedBuffer()
        {
            if (_appearanceSpeed != null) return _appearanceSpeed;
            if (_appearanceSpeedDummy == null)
            {
                _appearanceSpeedDummy = NewBuffer(1, sizeof(float));
                _appearanceSpeedDummy.SetData(new[] { 0f });
            }
            return _appearanceSpeedDummy;
        }

        Texture AppearanceRamp()
        {
            if (!appearanceEnabled || affectedColorMode == AffectedColorMode.Solid)
                return Texture2D.whiteTexture;
            if (_appearanceRamp == null)
                _appearanceRamp = BrazeTransferFunction.CreateRampLut("Braze Cloner Appearance Ramp");
            if (_appearanceRampDirty)
            {
                BrazeTransferFunction.BakeRamp(_appearanceRamp, affectedGradient);
                _appearanceRampDirty = false;
            }
            return _appearanceRamp;
        }

        void BindAppearance(MaterialPropertyBlock properties)
        {
            float speedSpan = Mathf.Max(affectedSpeedRange.y - affectedSpeedRange.x, 1e-4f);
            properties.SetInt(IdAppearanceEnabled, appearanceEnabled ? 1 : 0);
            properties.SetInt(IdAppearanceColorMode, (int)affectedColorMode);
            properties.SetColor(IdUnaffectedColor, unaffectedColor);
            properties.SetColor(IdAffectedColor, affectedColor);
            properties.SetVector(IdAppearanceMask, new Vector4(
                affectedThreshold, affectedSoftness, invertAffected ? 1f : 0f,
                CloneOrderRampStep(InstanceCount)));
            properties.SetVector(IdAppearanceMap, new Vector4(
                affectedGradientScale, affectedGradientOffset, affectedSpeedRange.x, 1f / speedSpan));
            properties.SetVector(IdAppearanceGlow, new Vector4(unaffectedGlow, affectedGlow, 0f, 0f));
            properties.SetTexture(IdColorRamp, AppearanceRamp());
            properties.SetBuffer(IdAppearanceSpeed, AppearanceSpeedBuffer());
        }

        internal static float CloneOrderRampStep(int instanceCount)
            => instanceCount > 1 ? 1f / (instanceCount - 1f) : 0f;

        internal static int PushApartBakeHash(
            BrazePushApartEffector pushApart, int sourceHash, SourceOrder order, int sourceSeed)
        {
            unchecked
            {
                int hash = pushApart != null ? pushApart.ContentHash() : 0;
                hash = hash * 31 + sourceHash;
                hash = hash * 31 + (int)order;
                hash = hash * 31 + sourceSeed;
                return hash;
            }
        }

        void BindBuildParameters(int instances)
        {
            Transform t = transform;
            Quaternion rotation = t.rotation;
            _clonerCompute.SetInt("_InstanceCount", instances);
            _clonerCompute.SetInt("_Distribution", (int)distribution);
            _clonerCompute.SetInts("_GridCount", gridCount.x, gridCount.y, gridCount.z);
            _clonerCompute.SetVector("_GridSpacing", gridSpacing);
            _clonerCompute.SetInt("_Center", center ? 1 : 0);
            _clonerCompute.SetVector("_LinearOffset", linearOffset);
            _clonerCompute.SetVector("_LinearRotation", linearRotation * Mathf.Deg2Rad);
            _clonerCompute.SetVector("_LinearScale", linearScale);
            // The distribution-input matrix is a per-prepare uniform, so moving the mesh/spline input or the
            // cloner itself never rebakes the sample buffer.
            Matrix4x4 meshLocalToCloner = Matrix4x4.identity;
            if (distribution == Distribution.Spline && splineInput != null)
                meshLocalToCloner = transform.worldToLocalMatrix * splineInput.transform.localToWorldMatrix;
            else if (meshInput != null)
                meshLocalToCloner = transform.worldToLocalMatrix * meshInput.transform.localToWorldMatrix;
            _clonerCompute.SetMatrix("_MeshLocalToCloner", meshLocalToCloner);
            _clonerCompute.SetMatrix("_MeshNormalToCloner", meshLocalToCloner.inverse.transpose);
            bool splineClosed = splineInput != null && splineInput.closed;
            _clonerCompute.SetVector("_SplineParams", new Vector4(
                splineOffset, splineRange.x, splineRange.y, splineClosed ? 1f : 0f));
            Vector3 splineUp = splineInput != null ? splineInput.PlaneNormal() : Vector3.up;
            if (splineUp.sqrMagnitude < 1e-6f) splineUp = Vector3.up;
            _clonerCompute.SetVector("_SplineUpLocal", splineUp);
            _clonerCompute.SetInt("_SplineSampleCount",
                distribution == Distribution.Spline && _meshSampleBuffer != null ? _meshSampleBuffer.count : 2);
            _clonerCompute.SetInt("_RadialBaseCount", Mathf.Max(1, count));
            _clonerCompute.SetInt("_RadialRingCount", Mathf.Max(1, radialRings));
            _clonerCompute.SetInt("_RadialCountStep", Mathf.Max(0, radialCountStep));
            _clonerCompute.SetFloat("_RadialRadius", radialRadius);
            _clonerCompute.SetFloat("_RadialRadiusStep", radialRadiusStep);
            _clonerCompute.SetVector("_RadialArc", radialArc * Mathf.Deg2Rad);
            _clonerCompute.SetFloat("_RadialRingTwist", radialRingTwist * Mathf.Deg2Rad);
            _clonerCompute.SetInt("_RadialPlane", (int)radialPlane);
            _clonerCompute.SetFloat("_RadialAxisStep", radialAxisStep);
            _clonerCompute.SetInt("_RadialCenterAxis", radialCenterAxis ? 1 : 0);
            _clonerCompute.SetVector("_RadialSpiral", new Vector2(radialSpiralRadius, radialSpiralAxis));
            _clonerCompute.SetVector("_RadialWave", new Vector4(radialWaveRadius, radialWaveAxis,
                radialWaveCycles, radialWavePhase * Mathf.Deg2Rad));
            _clonerCompute.SetFloat("_RadialWaveAxialPhase", radialWaveAxialPhase * Mathf.Deg2Rad);
            _clonerCompute.SetFloat("_RadialWaveRingPhase", radialWaveRingPhase * Mathf.Deg2Rad);
            _clonerCompute.SetVector("_RadialRotationOffset", radialRotationOffset * Mathf.Deg2Rad);
            _clonerCompute.SetVector("_RadialRotationStep", radialRotationStep * Mathf.Deg2Rad);
            _clonerCompute.SetFloat("_RadialScaleStep", radialScaleStep);
            _clonerCompute.SetFloat("_RadialScaleWave", radialScaleWave);
            _clonerCompute.SetInt("_Alignment", (int)alignment);
            _clonerCompute.SetInt("_AlignmentAxis", (int)alignmentAxis);
            _clonerCompute.SetInt("_AlignmentUp", (int)alignmentUp);
            _clonerCompute.SetFloat("_UniformScale", uniformScale);
            _clonerCompute.SetInt("_Seed", seed);
            _clonerCompute.SetVector("_RootPosition", t.position);
            _clonerCompute.SetVector("_RootRotation", new Vector4(rotation.x, rotation.y, rotation.z, rotation.w));
            _clonerCompute.SetVector("_RootScale", t.lossyScale);
        }

        int TransformContentHash(int instances, bool includeAnimationFrame = true)
        {
            unchecked
            {
                int hash = instances;
                hash = hash * 31 + transform.localToWorldMatrix.GetHashCode();
                hash = hash * 31 + (int)distribution;
                hash = hash * 31 + gridCount.GetHashCode();
                hash = hash * 31 + gridSpacing.GetHashCode();
                hash = hash * 31 + (center ? 1 : 0);
                hash = hash * 31 + linearOffset.GetHashCode();
                hash = hash * 31 + linearRotation.GetHashCode();
                hash = hash * 31 + linearScale.GetHashCode();
                hash = hash * 31 + (meshInput != null ? meshInput.BrazeInstanceId() : 0);
                hash = hash * 31 + (meshInput != null ? meshInput.transform.localToWorldMatrix.GetHashCode() : 0);
                Mesh distributionMesh = DistributionMesh;
                hash = hash * 31 + (distributionMesh != null ? distributionMesh.BrazeInstanceId() : 0);
                hash = hash * 31 + (distributionMesh != null ? distributionMesh.vertexCount : 0);
                hash = hash * 31 + (distributionMesh != null ? distributionMesh.bounds.GetHashCode() : 0);
                hash = hash * 31 + (int)meshSampling;
                hash = hash * 31 + _meshSampleHash;
                hash = hash * 31 + (splineInput != null ? splineInput.BrazeInstanceId() : 0);
                hash = hash * 31 + (splineInput != null ? splineInput.transform.localToWorldMatrix.GetHashCode() : 0);
                hash = hash * 31 + (splineInput != null ? splineInput.version : 0);
                hash = hash * 31 + splineRange.GetHashCode();
                hash = hash * 31 + splineOffset.GetHashCode();
                hash = hash * 31 + radialRings;
                hash = hash * 31 + radialCountStep;
                hash = hash * 31 + radialRadius.GetHashCode();
                hash = hash * 31 + radialRadiusStep.GetHashCode();
                hash = hash * 31 + radialArc.GetHashCode();
                hash = hash * 31 + radialRingTwist.GetHashCode();
                hash = hash * 31 + (int)radialPlane;
                hash = hash * 31 + radialAxisStep.GetHashCode();
                hash = hash * 31 + (radialCenterAxis ? 1 : 0);
                hash = hash * 31 + radialSpiralRadius.GetHashCode();
                hash = hash * 31 + radialSpiralAxis.GetHashCode();
                hash = hash * 31 + radialWaveCycles.GetHashCode();
                hash = hash * 31 + radialWaveRadius.GetHashCode();
                hash = hash * 31 + radialWaveAxis.GetHashCode();
                hash = hash * 31 + radialWavePhase.GetHashCode();
                hash = hash * 31 + radialWaveAxialPhase.GetHashCode();
                hash = hash * 31 + radialWaveRingPhase.GetHashCode();
                hash = hash * 31 + radialRotationOffset.GetHashCode();
                hash = hash * 31 + radialRotationStep.GetHashCode();
                hash = hash * 31 + radialScaleStep.GetHashCode();
                hash = hash * 31 + radialScaleWave.GetHashCode();
                hash = hash * 31 + (int)alignment;
                hash = hash * 31 + (int)alignmentAxis;
                hash = hash * 31 + (int)alignmentUp;
                hash = hash * 31 + uniformScale.GetHashCode();
                hash = hash * 31 + seed;
                GatherEffectorReferences();
                for (int i = 0; i < _effectorScratch.Count; i++)
                {
                    hash = hash * 31 + _effectorScratch[i].BrazeInstanceId();
                    hash = hash * 31 + _effectorScratch[i].ContentHash();
                    if (includeAnimationFrame && _effectorScratch[i].Animating) hash = hash * 31 + Time.frameCount;
                }
                return hash;
            }
        }

        void GatherEffectorReferences()
        {
            _effectorScratch.Clear();
            if (effectors == null) return;
            for (int i = 0; i < effectors.Count; i++)
            {
                BrazeFractureEffector effector = effectors[i];
                if (effector != null && effector.isActiveAndEnabled && !_effectorScratch.Contains(effector))
                    _effectorScratch.Add(effector);
            }
        }

        int GatherGpuEffectors()
        {
            GatherEffectorReferences();
            int count = _effectorScratch.Count;
            int hash = 17;
            unchecked
            {
                for (int i = 0; i < count; i++) hash = (hash * 31 + _effectorScratch[i].BrazeInstanceId()) * 31 + _effectorScratch[i].ContentHash();
            }
            if (_effectorUploadHashValid && hash == _effectorUploadHash && _effectorBuffer != null) return count;
            _effectorUploadHash = hash;
            _effectorUploadHashValid = true;
            int uploadCount = Mathf.Max(1, count);

            if (_effectorData.Length < uploadCount) _effectorData = new GpuFractureEffector[Mathf.NextPowerOfTwo(uploadCount)];
            _effectorAuxScratch.Clear();
            if (count == 0) _effectorData[0] = default;
            for (int i = 0; i < count; i++)
            {
                _effectorData[i] = _effectorScratch[i].ToGpu(i);
                _effectorScratch[i].AppendGpuData(ref _effectorData[i], _effectorAuxScratch);
            }

            ResizeBuffer(ref _effectorBuffer, uploadCount, GpuFractureEffector.Stride);
            _effectorBuffer.SetData(_effectorData, 0, 0, uploadCount);
            int auxCount = Mathf.Max(1, _effectorAuxScratch.Count);
            if (_effectorAuxData.Length < auxCount) _effectorAuxData = new Vector4[Mathf.NextPowerOfTwo(auxCount)];
            if (_effectorAuxScratch.Count == 0) _effectorAuxData[0] = Vector4.zero;
            else _effectorAuxScratch.CopyTo(_effectorAuxData, 0);
            ResizeBuffer(ref _effectorAuxBuffer, auxCount, sizeof(float) * 4);
            _effectorAuxBuffer.SetData(_effectorAuxData, 0, 0, auxCount);

            int floats = uploadCount * 256;
            bool falloffResize = ResizeBuffer(ref _effectorFalloffBuffer, floats, sizeof(float));
            if (_falloffData.Length < floats) _falloffData = new float[floats];
            int falloffHash = BrazeFractureEffector.FalloffAtlasHash(_effectorScratch, count);
            if (falloffResize || falloffHash != _falloffHash)
            {
                for (int row = 0; row < uploadCount; row++)
                for (int x = 0; x < 256; x++)
                    _falloffData[row * 256 + x] = row < count && _effectorScratch[row].falloff != null
                        ? _effectorScratch[row].falloff.Evaluate(x / 255f) : 0f;
                _effectorFalloffBuffer.SetData(_falloffData, 0, 0, floats);
                _falloffHash = falloffHash;
            }

            bool functionResize = ResizeBuffer(ref _effectorFunctionBuffer, floats, sizeof(float));
            if (_functionData.Length < floats) _functionData = new float[floats];
            int functionHash = BrazeFractureEffector.FunctionAtlasHash(_effectorScratch, count);
            if (functionResize || functionHash != _functionHash)
            {
                for (int row = 0; row < uploadCount; row++)
                for (int x = 0; x < 256; x++)
                {
                    float u = x / 255f;
                    AnimationCurve curve = row < count ? _effectorScratch[row].FunctionCurve : null;
                    _functionData[row * 256 + x] = curve != null ? curve.Evaluate(u) : u;
                }
                _effectorFunctionBuffer.SetData(_functionData, 0, 0, floats);
                _functionHash = functionHash;
            }
            CalculateGpuBytes();
            return count;
        }

        static bool ResizeBuffer(ref GraphicsBuffer buffer, int count, int stride)
        {
            int size = Mathf.NextPowerOfTwo(Mathf.Max(1, count));
            if (buffer != null && buffer.count >= count && buffer.stride == stride) return false;
            buffer?.Release();
            buffer = NewBuffer(size, stride);
            return true;
        }

        void BindEffectorGlobals(int count, int instances)
        {
            _effectorCompute.SetInt("_EffectorCount", count);
            _effectorCompute.SetInt("_EffectorClumpCount", instances);
            _effectorCompute.SetFloat("_EffectorTime", Application.isPlaying ? Time.time : Time.realtimeSinceStartup);
            _effectorCompute.SetMatrix("_EffectorObjectW2L", transform.worldToLocalMatrix);
            Quaternion q = transform.rotation;
            _effectorCompute.SetVector("_EffectorObjectRotation", new Vector4(q.x, q.y, q.z, q.w));
            _effectorCompute.SetVector("_EffectorBoundsMin", _localDistributionBounds.min);
            _effectorCompute.SetVector("_EffectorBoundsMax", _localDistributionBounds.max);
        }

        void BindSingleEffector()
        {
            _effectorCompute.SetBuffer(_kApplyEffector, "_Effectors", _effectorBuffer);
            _effectorCompute.SetBuffer(_kApplyEffector, "_EffectorFalloff", _effectorFalloffBuffer);
            _effectorCompute.SetBuffer(_kApplyEffector, "_EffectorFunctionLut", _effectorFunctionBuffer);
            _effectorCompute.SetBuffer(_kApplyEffector, "_EffectorAux", _effectorAuxBuffer);
            _effectorCompute.SetBuffer(_kApplyEffector, "_ClumpCentroidE", _basePosition);
            _effectorCompute.SetBuffer(_kApplyEffector, "_ClumpPosStateE", _basePosition);
            _effectorCompute.SetBuffer(_kApplyEffector, "_ClumpRotE", _baseRotation);
            _effectorCompute.SetBuffer(_kApplyEffector, "_EffectorPosition", _effectorPosition);
            _effectorCompute.SetBuffer(_kApplyEffector, "_EffectorRotation", _effectorRotation);
            _effectorCompute.SetBuffer(_kApplyEffector, "_EffectorScale", _effectorScale);
        }

        void ApplyDelayEffector(BrazeDelayEffector delay, int stackIndex, int effectorCount, int instances)
        {
            BrazeDelayGpuState state = _delayStates.Use(delay, instances, stackIndex);
            BrazeDelayGpuSample sample = state.BeginSample();
            bool spring = delay.mode == BrazeDelayEffector.DelayMode.Spring;
            int kernel = spring ? _kApplyDelaySpringEffector : _kApplyDelayEffector;

            _effectorCompute.SetInt("_EffectorIndex", stackIndex);
            _effectorCompute.SetInt("_EffectorCount", effectorCount);
            _effectorCompute.SetInt("_DelayHasHistory", sample.hasHistory ? 1 : 0);
            _effectorCompute.SetFloat("_DelayDeltaTime", sample.deltaTime);
            _effectorCompute.SetBuffer(kernel, "_Effectors", _effectorBuffer);
            _effectorCompute.SetBuffer(kernel, "_EffectorFalloff", _effectorFalloffBuffer);
            _effectorCompute.SetBuffer(kernel, "_ClumpCentroidE", _basePosition);
            _effectorCompute.SetBuffer(kernel, "_ClumpPosStateE", _basePosition);
            _effectorCompute.SetBuffer(kernel, "_ClumpRotE", _baseRotation);
            _effectorCompute.SetBuffer(kernel, "_EffectorPosition", _effectorPosition);
            _effectorCompute.SetBuffer(kernel, "_EffectorRotation", _effectorRotation);
            _effectorCompute.SetBuffer(kernel, "_EffectorScale", _effectorScale);
            _effectorCompute.SetBuffer(kernel, "_DelayPositionState", state.position);
            _effectorCompute.SetBuffer(kernel, "_DelayRotationState", state.rotation);
            _effectorCompute.SetBuffer(kernel, "_DelayScaleState", state.scale);
            if (spring)
            {
                _effectorCompute.SetBuffer(kernel, "_DelayVelocity", state.velocity);
            }
            DispatchInstances(_effectorCompute, kernel, instances);
            state.CommitSample();
        }

        void CullAndBuildArgs(Camera camera, int instances)
        {
            GeometryUtility.CalculateFrustumPlanes(camera, _frustumPlanes);
            for (int i = 0; i < 6; i++)
            {
                Plane p = _frustumPlanes[i];
                _planeVectors[i] = new Vector4(p.normal.x, p.normal.y, p.normal.z, p.distance);
            }

            _clonerCompute.SetInt("_BucketCount", _bucketCount);
            _clonerCompute.SetBuffer(_kClearBuckets, "_BucketCounts", _bucketCounts);
            DispatchLinear(_clonerCompute, _kClearBuckets, _bucketCount);

            _clonerCompute.SetInt("_InstanceCount", instances);
            _clonerCompute.SetInt("_SourceCount", _sourceRuntimes.Count);
            _clonerCompute.SetInt("_SourceCapacity", _sourceCapacity);
            _clonerCompute.SetInt("_SourceOrder", (int)sourceOrder);
            _clonerCompute.SetInt("_Seed", seed);
            _clonerCompute.SetVectorArray("_FrustumPlanes", _planeVectors);
            _clonerCompute.SetVector("_CameraPosition", camera.transform.position);
            _clonerCompute.SetFloat("_TanHalfFov", camera.orthographic ? 1f : Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f));
            _clonerCompute.SetFloat("_OrthoSize", camera.orthographicSize);
            _clonerCompute.SetInt("_Orthographic", camera.orthographic ? 1 : 0);
            _clonerCompute.SetFloat("_LodBias", lodBias * QualitySettings.lodBias);
            _clonerCompute.SetFloat("_MaxDrawDistance", maxDrawDistance);
            _clonerCompute.SetFloat("_BoundsPadding", boundsPadding);
            _clonerCompute.SetBuffer(_kCull, "_BasePositionRead", _basePosition);
            _clonerCompute.SetBuffer(_kCull, "_BaseScaleRead", _baseScale);
            _clonerCompute.SetBuffer(_kCull, "_EffectorPositionRead", _effectorPosition);
            _clonerCompute.SetBuffer(_kCull, "_EffectorScaleRead", _effectorScale);
            _clonerCompute.SetBuffer(_kCull, "_Sources", _sourceBuffer);
            _clonerCompute.SetBuffer(_kCull, "_VisibleIndices", _visibleIndices);
            _clonerCompute.SetBuffer(_kCull, "_BucketCounts", _bucketCounts);
            DispatchInstances(_clonerCompute, _kCull, instances);

            for (int i = 0; i < _nestedRuntimes.Count; i++)
            {
                NestedRuntime nested = _nestedRuntimes[i];
                if (!nested.ready || nested.capacity == 0 || nested.child == null || nested.sourceRoot == null)
                    continue;
                BrazeCloner child = nested.child;
                if (child._basePosition == null || child._sourceBuffer == null) continue;

                Transform root = nested.sourceRoot;
                Quaternion inverseRotation = Quaternion.Inverse(root.rotation);
                Vector3 lossyScale = root.lossyScale;
                int composed = (int)Math.Min((long)instances * nested.childInstances, int.MaxValue);
                _clonerCompute.SetInt("_ComposedCount", composed);
                _clonerCompute.SetInt("_ComposedChildCount", nested.childInstances);
                _clonerCompute.SetInt("_ComposedParentSource", nested.parentSourceIndex);
                _clonerCompute.SetInt("_ComposedBucketBase", nested.bucketBase);
                _clonerCompute.SetInt("_ComposedCapacity", nested.capacity);
                _clonerCompute.SetInt("_ComposedVisibleOffset", (int)nested.visibleOffset);
                _clonerCompute.SetInt("_NestedSourceCount", nested.childSourceCount);
                _clonerCompute.SetInt("_NestedSourceOrder", (int)child.sourceOrder);
                _clonerCompute.SetInt("_NestedSeed", child.seed);
                _clonerCompute.SetVector("_NestedRootInvRotation", new Vector4(
                    inverseRotation.x, inverseRotation.y, inverseRotation.z, inverseRotation.w));
                _clonerCompute.SetVector("_NestedRootPosition", root.position);
                _clonerCompute.SetVector("_NestedRootInvScale", new Vector3(
                    1f / SafeScaleComponent(lossyScale.x),
                    1f / SafeScaleComponent(lossyScale.y),
                    1f / SafeScaleComponent(lossyScale.z)));
                _clonerCompute.SetBuffer(_kCullComposed, "_BasePositionRead", _basePosition);
                _clonerCompute.SetBuffer(_kCullComposed, "_BaseRotationRead", _baseRotation);
                _clonerCompute.SetBuffer(_kCullComposed, "_BaseScaleRead", _baseScale);
                _clonerCompute.SetBuffer(_kCullComposed, "_EffectorPositionRead", _effectorPosition);
                _clonerCompute.SetBuffer(_kCullComposed, "_EffectorRotationRead", _effectorRotation);
                _clonerCompute.SetBuffer(_kCullComposed, "_EffectorScaleRead", _effectorScale);
                _clonerCompute.SetBuffer(_kCullComposed, "_NestedBasePosition", child._basePosition);
                _clonerCompute.SetBuffer(_kCullComposed, "_NestedBaseScale", child._baseScale);
                _clonerCompute.SetBuffer(_kCullComposed, "_NestedEffectorPosition", child._effectorPosition);
                _clonerCompute.SetBuffer(_kCullComposed, "_NestedEffectorScale", child._effectorScale);
                _clonerCompute.SetBuffer(_kCullComposed, "_NestedSources", child._sourceBuffer);
                _clonerCompute.SetBuffer(_kCullComposed, "_VisibleIndices", _visibleIndices);
                _clonerCompute.SetBuffer(_kCullComposed, "_BucketCounts", _bucketCounts);
                DispatchInstances(_clonerCompute, _kCullComposed, composed);
            }

            _clonerCompute.SetInt("_DrawCount", _draws.Count);
            _clonerCompute.SetBuffer(_kWriteArgs, "_BucketCountsRead", _bucketCounts);
            _clonerCompute.SetBuffer(_kWriteArgs, "_DrawBuckets", _drawBucketBuffer);
            _clonerCompute.SetBuffer(_kWriteArgs, "_DrawArgs", _argsBuffer);
            DispatchLinear(_clonerCompute, _kWriteArgs, _draws.Count);
        }

        void SubmitDraws(Camera camera)
        {
            Bounds bounds = WorldBounds();
            for (int i = 0; i < _draws.Count; i++)
            {
                DrawRuntime draw = _draws[i];
                NestedRuntime nested = draw.nested;
                if (nested != null && (!nested.ready || nested.capacity == 0 || nested.child == null ||
                    nested.sourceRoot == null || nested.child._basePosition == null)) continue;

                bool mirrored = nested == null
                    ? uniformScale < 0f
                    : uniformScale < 0f != nested.child.uniformScale < 0f;
                CullMode cull = mirrored ? FlipCull(draw.baseCull) : draw.baseCull;
                if (draw.material.HasProperty("_Cull") &&
                    Mathf.RoundToInt(draw.material.GetFloat("_Cull")) != (int)cull)
                    draw.material.SetFloat("_Cull", (float)cull);
                MaterialPropertyBlock properties = draw.properties;
                properties.SetBuffer(IdBasePosition, _basePosition);
                properties.SetBuffer(IdBaseRotation, _baseRotation);
                properties.SetBuffer(IdBaseScale, _baseScale);
                properties.SetBuffer(IdEffectorPosition, _effectorPosition);
                properties.SetBuffer(IdEffectorRotation, _effectorRotation);
                properties.SetBuffer(IdEffectorScale, _effectorScale);
                properties.SetBuffer(IdVisible, _visibleIndices);
                if (nested != null)
                {
                    BrazeCloner child = nested.child;
                    Transform root = nested.sourceRoot;
                    Quaternion inverseRotation = Quaternion.Inverse(root.rotation);
                    Vector3 lossyScale = root.lossyScale;
                    properties.SetInt(IdNestedChildCount, nested.childInstances);
                    properties.SetVector(IdNestedRootInvRotation, new Vector4(
                        inverseRotation.x, inverseRotation.y, inverseRotation.z, inverseRotation.w));
                    properties.SetVector(IdNestedRootPosition, root.position);
                    properties.SetVector(IdNestedRootInvScale, new Vector4(
                        1f / SafeScaleComponent(lossyScale.x),
                        1f / SafeScaleComponent(lossyScale.y),
                        1f / SafeScaleComponent(lossyScale.z), 0f));
                    properties.SetBuffer(IdChildBasePosition, child._basePosition);
                    properties.SetBuffer(IdChildBaseRotation, child._baseRotation);
                    properties.SetBuffer(IdChildBaseScale, child._baseScale);
                    properties.SetBuffer(IdChildEffectorPosition, child._effectorPosition);
                    properties.SetBuffer(IdChildEffectorRotation, child._effectorRotation);
                    properties.SetBuffer(IdChildEffectorScale, child._effectorScale);
                    properties.SetInt(IdBucketOffset, (int)(nested.visibleOffset + (long)draw.bucket * nested.capacity));
                    // Composed draws style with the child's appearance setup: the appearance index and
                    // effector influence are child-clone quantities, so the parent's settings do not apply.
                    child.BindAppearance(properties);
                }
                else
                {
                    properties.SetInt(IdNestedChildCount, 0);
                    properties.SetBuffer(IdChildBasePosition, _basePosition);
                    properties.SetBuffer(IdChildBaseRotation, _baseRotation);
                    properties.SetBuffer(IdChildBaseScale, _baseScale);
                    properties.SetBuffer(IdChildEffectorPosition, _effectorPosition);
                    properties.SetBuffer(IdChildEffectorRotation, _effectorRotation);
                    properties.SetBuffer(IdChildEffectorScale, _effectorScale);
                    properties.SetInt(IdBucketOffset, draw.bucket * _sourceCapacity);
                    BindAppearance(properties);
                }

                var renderParams = new RenderParams(draw.material)
                {
                    worldBounds = bounds,
                    shadowCastingMode = shadowCasting,
                    receiveShadows = receiveShadows,
                    layer = gameObject.layer,
                    camera = camera,
                    matProps = properties,
                };
                Graphics.RenderMeshIndirect(renderParams, draw.mesh, _argsBuffer, 1, i);
            }
        }

        internal Bounds CalculateLocalDistributionBounds()
        {
            int n = ResolveInstanceCount();
            if (distribution == Distribution.Grid)
            {
                Vector3 size = Vector3.Scale(new Vector3(Mathf.Max(0, gridCount.x - 1), Mathf.Max(0, gridCount.y - 1),
                    Mathf.Max(0, gridCount.z - 1)), Abs(gridSpacing));
                Vector3 centre = center ? Vector3.zero : Vector3.Scale((Vector3)(gridCount - Vector3Int.one), gridSpacing) * 0.5f;
                return new Bounds(centre, size + Vector3.one * 0.001f);
            }
            if (distribution == Distribution.Linear)
            {
                Vector3 end = linearOffset * Mathf.Max(0, n - 1);
                return new Bounds(end * 0.5f, Abs(end) + Vector3.one * 0.001f);
            }
            if (distribution == Distribution.Mesh) return CalculateMeshBounds();
            if (distribution == Distribution.Spline) return CalculateSplineBounds();
            return CalculateRadialBounds();
        }

        Bounds CalculateSplineBounds()
        {
            if (splineInput == null || splineInput.points == null || splineInput.points.Count == 0)
                return new Bounds(Vector3.zero, Vector3.one * 0.001f);
            Matrix4x4 matrix = transform.worldToLocalMatrix * splineInput.transform.localToWorldMatrix;
            const int samples = 48;
            var bounds = new Bounds(matrix.MultiplyPoint3x4(splineInput.EvaluateLocal(0f)), Vector3.one * 0.001f);
            for (int i = 1; i < samples; i++)
                bounds.Encapsulate(matrix.MultiplyPoint3x4(splineInput.EvaluateLocal(i / (samples - 1f))));
            return bounds;
        }

        Bounds CalculateMeshBounds()
        {
            Mesh mesh = DistributionMesh;
            if (meshInput == null || mesh == null) return new Bounds(Vector3.zero, Vector3.one * 0.001f);
            Bounds sourceBounds = meshInput is SkinnedMeshRenderer skinned ? skinned.localBounds : mesh.bounds;
            Matrix4x4 matrix = transform.worldToLocalMatrix * meshInput.transform.localToWorldMatrix;
            Vector3 centre = matrix.MultiplyPoint3x4(sourceBounds.center);
            Vector3 extents = sourceBounds.extents;
            Vector3 transformedExtents = new(
                Mathf.Abs(matrix.m00) * extents.x + Mathf.Abs(matrix.m01) * extents.y + Mathf.Abs(matrix.m02) * extents.z,
                Mathf.Abs(matrix.m10) * extents.x + Mathf.Abs(matrix.m11) * extents.y + Mathf.Abs(matrix.m12) * extents.z,
                Mathf.Abs(matrix.m20) * extents.x + Mathf.Abs(matrix.m21) * extents.y + Mathf.Abs(matrix.m22) * extents.z);
            transformedExtents = new Vector3(Mathf.Max(0.0005f, transformedExtents.x),
                Mathf.Max(0.0005f, transformedExtents.y), Mathf.Max(0.0005f, transformedExtents.z));
            return new Bounds(centre, transformedExtents * 2f);
        }

        Bounds CalculateRadialBounds()
        {
            float lastRing = Mathf.Max(0, radialRings - 1);
            float radius0 = radialRadius;
            float radius1 = radialRadius + lastRing * radialRadiusStep;
            float radialExtent = Mathf.Max(Mathf.Abs(radius0), Mathf.Abs(radius0 + radialSpiralRadius),
                Mathf.Abs(radius1), Mathf.Abs(radius1 + radialSpiralRadius)) + Mathf.Abs(radialWaveRadius);

            float ringCoordinate0 = radialCenterAxis ? -lastRing * 0.5f : 0f;
            float ringCoordinate1 = radialCenterAxis ? lastRing * 0.5f : lastRing;
            float ringAxis0 = ringCoordinate0 * radialAxisStep;
            float ringAxis1 = ringCoordinate1 * radialAxisStep;
            float pathAxis0 = radialCenterAxis ? radialSpiralAxis * -0.5f : 0f;
            float pathAxis1 = radialCenterAxis ? radialSpiralAxis * 0.5f : radialSpiralAxis;
            float axisMin = Mathf.Min(ringAxis0, ringAxis1) + Mathf.Min(pathAxis0, pathAxis1) - Mathf.Abs(radialWaveAxis);
            float axisMax = Mathf.Max(ringAxis0, ringAxis1) + Mathf.Max(pathAxis0, pathAxis1) + Mathf.Abs(radialWaveAxis);

            Vector3 min;
            Vector3 max;
            if (radialPlane == RadialPlane.XY)
            {
                min = new Vector3(-radialExtent, -radialExtent, axisMin);
                max = new Vector3(radialExtent, radialExtent, axisMax);
            }
            else if (radialPlane == RadialPlane.XZ)
            {
                min = new Vector3(-radialExtent, axisMin, -radialExtent);
                max = new Vector3(radialExtent, axisMax, radialExtent);
            }
            else
            {
                min = new Vector3(axisMin, -radialExtent, -radialExtent);
                max = new Vector3(axisMax, radialExtent, radialExtent);
            }

            Vector3 size = max - min;
            size = new Vector3(Mathf.Max(0.001f, size.x), Mathf.Max(0.001f, size.y), Mathf.Max(0.001f, size.z));
            return new Bounds((min + max) * 0.5f, size);
        }

        Bounds WorldBounds()
        {
            Bounds local = _localDistributionBounds;
            Vector3 centre = transform.TransformPoint(local.center);
            Vector3 extent = Vector3.Scale(local.extents, Abs(transform.lossyScale));
            float sourceRadius = 0f;
            for (int i = 0; i < _sourceRuntimes.Count; i++) sourceRadius = Mathf.Max(sourceRadius, _sourceRuntimes[i].radius);
            if (!_boundsVisiting)
            {
                _boundsVisiting = true;
                try
                {
                    for (int i = 0; i < _nestedRuntimes.Count; i++)
                    {
                        NestedRuntime nested = _nestedRuntimes[i];
                        if (!nested.ready || nested.capacity == 0 || nested.sourceRoot == null) continue;
                        // Conservative source-local radius covering the child's entire clone distribution.
                        Bounds childBounds = nested.child.WorldBounds();
                        Vector3 localCenter = nested.sourceRoot.InverseTransformPoint(childBounds.center);
                        float inverseScale = 1f / SafeScaleComponent(MinAbs(nested.sourceRoot.lossyScale));
                        sourceRadius = Mathf.Max(sourceRadius,
                            localCenter.magnitude + childBounds.extents.magnitude * Mathf.Abs(inverseScale));
                    }
                }
                finally
                {
                    _boundsVisiting = false;
                }
            }
            float pad = boundsPadding + sourceRadius * MaxAbs(transform.lossyScale) * MaximumDistributionScale();
            return new Bounds(centre, (extent + Vector3.one * pad) * 2f);
        }

        internal float MaximumDistributionScale()
        {
            float result = 1f;
            if (distribution == Distribution.Linear)
                result = Mathf.Max(1f, MaxAbs(linearScale));
            else if (distribution == Distribution.Radial)
            {
                float lastRingScale = 1f + Mathf.Max(0, radialRings - 1) * radialScaleStep;
                result = Mathf.Max(0.001f, Mathf.Max(1f, lastRingScale) + Mathf.Abs(radialScaleWave));
            }
            return result * Mathf.Abs(uniformScale);
        }

        static CullMode FlipCull(CullMode value) => value == CullMode.Back ? CullMode.Front
            : value == CullMode.Front ? CullMode.Back : CullMode.Off;

        static Vector3 Abs(Vector3 value) => new(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));
        static float MaxAbs(Vector3 value) => Mathf.Max(Mathf.Abs(value.x), Mathf.Max(Mathf.Abs(value.y), Mathf.Abs(value.z)));
        static float MinAbs(Vector3 value) => Mathf.Min(Mathf.Abs(value.x), Mathf.Min(Mathf.Abs(value.y), Mathf.Abs(value.z)));

        static float SafeScaleComponent(float value)
        {
            float magnitude = Mathf.Max(Mathf.Abs(value), 1e-4f);
            return value < 0f ? -magnitude : magnitude;
        }

        int RadialCountAtRing(int ring)
        {
            long value = (long)Mathf.Max(1, count) + (long)Mathf.Max(0, radialCountStep) * Mathf.Max(0, ring);
            return (int)Math.Min(value, int.MaxValue);
        }

        bool RadialArcIsClosed => Mathf.Abs(Mathf.Abs(radialArc.y - radialArc.x) - 360f) <= 0.01f;

        Vector3 RadialLocalPoint(int ring, float pathU)
        {
            float angle = Mathf.LerpUnclamped(radialArc.x, radialArc.y, pathU) * Mathf.Deg2Rad
                + ring * radialRingTwist * Mathf.Deg2Rad;
            float wavePhase = pathU * radialWaveCycles * Mathf.PI * 2f
                + (radialWavePhase + ring * radialWaveRingPhase) * Mathf.Deg2Rad;
            float radiusValue = radialRadius + ring * radialRadiusStep + pathU * radialSpiralRadius
                + Mathf.Sin(wavePhase) * radialWaveRadius;
            float ringCoordinate = radialCenterAxis ? ring - (Mathf.Max(1, radialRings) - 1) * 0.5f : ring;
            float pathCoordinate = radialCenterAxis ? pathU - 0.5f : pathU;
            float axisValue = ringCoordinate * radialAxisStep + pathCoordinate * radialSpiralAxis
                + Mathf.Sin(wavePhase + radialWaveAxialPhase * Mathf.Deg2Rad) * radialWaveAxis;
            float cosine = Mathf.Cos(angle);
            float sine = Mathf.Sin(angle);
            return radialPlane == RadialPlane.XY ? new Vector3(cosine * radiusValue, sine * radiusValue, axisValue)
                : radialPlane == RadialPlane.XZ ? new Vector3(cosine * radiusValue, axisValue, sine * radiusValue)
                                               : new Vector3(axisValue, cosine * radiusValue, sine * radiusValue);
        }

        static void DispatchInstances(ComputeShader shader, int kernel, int count)
        {
            int groups = Mathf.CeilToInt(count / (float)Threads);
            int x = Mathf.Min(MaxDispatchX, groups);
            int y = Mathf.CeilToInt(groups / (float)x);
            shader.SetInt("_DispatchThreadsX", x * Threads);
            shader.Dispatch(kernel, x, y, 1);
        }

        static void DispatchLinear(ComputeShader shader, int kernel, int count)
        {
            if (count <= 0) return;
            shader.Dispatch(kernel, Mathf.CeilToInt(count / (float)Threads), 1, 1);
        }

        void CalculateGpuBytes()
        {
            _gpuBytes = 0;
            AddBytes(_basePosition); AddBytes(_baseRotation); AddBytes(_baseScale);
            AddBytes(_effectorPosition); AddBytes(_effectorPositionScratch); AddBytes(_effectorRotation); AddBytes(_effectorScale);
            AddBytes(_visibleIndices); AddBytes(_bucketCounts); AddBytes(_sourceBuffer); AddBytes(_argsBuffer); AddBytes(_drawBucketBuffer);
            AddBytes(_effectorBuffer); AddBytes(_effectorFalloffBuffer); AddBytes(_effectorFunctionBuffer); AddBytes(_effectorAuxBuffer);
            AddBytes(_appearanceSpeed); AddBytes(_appearancePreviousPosition); AddBytes(_appearanceSpeedDummy);
            AddBytes(_meshSampleBuffer);
            AddBytes(_pushGridHeads); AddBytes(_pushGridNext); AddBytes(_pushGridCells);
            AddBytes(_pushRadii); AddBytes(_pushMaxRadius); AddBytes(_pushInputPosition);
            _gpuBytes += _delayStates.GpuBytes;
            _gpuBytes += _pushApartStates.GpuBytes;
            if (_appearanceRamp != null) _gpuBytes += (long)_appearanceRamp.width * _appearanceRamp.height * 8L;
        }

        void AddBytes(GraphicsBuffer buffer) { if (buffer != null) _gpuBytes += (long)buffer.count * buffer.stride; }
        static string FormatBytes(long bytes) => bytes >= 1024L * 1024L
            ? $"{bytes / (1024f * 1024f):0.0} MB GPU" : $"{bytes / 1024f:0.0} KB GPU";

        void UpdateStatus(int instances)
        {
            long composed = 0;
            bool nestedWaiting = false;
            for (int i = 0; i < _nestedRuntimes.Count; i++)
            {
                NestedRuntime nested = _nestedRuntimes[i];
                if (nested.ready && nested.capacity > 0)
                    composed += (long)(instances / Mathf.Max(1, _sourceRuntimes.Count)) * nested.childInstances;
                else
                    nestedWaiting = true;
            }
            string composedText = composed > 0 ? $" (+{composed:n0} composed)" : string.Empty;
            string waitingText = nestedWaiting ? " · a nested cloner is inactive" : string.Empty;
            _status = $"{instances:n0} GPU instances{composedText} · {_sourceRuntimes.Count} sources · " +
                      $"{_draws.Count} indirect draws · {FormatBytes(_gpuBytes)}{waitingText}";
        }

        static Gradient DefaultAffectedGradient()
        {
            var gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.05f, 0.55f, 1f), 0f),
                    new GradientColorKey(new Color(1f, 0.32f, 0.04f), 0.55f),
                    new GradientColorKey(new Color(1f, 0.92f, 0.45f), 1f),
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return gradient;
        }

        void ReleaseRuntime()
        {
            ReleaseBuffers();
            ReleaseDraws();
            _sourceRuntimes.Clear();
            Release(ref _appearanceSpeedDummy);
            if (_appearanceRamp != null) DestroyOwned(_appearanceRamp);
            _appearanceRamp = null;
            _appearanceRampDirty = true;
            if (_clonerCompute != null) DestroyOwned(_clonerCompute);
            if (_effectorCompute != null) DestroyOwned(_effectorCompute);
            _clonerCompute = _effectorCompute = null;
        }

        void ReleaseBuffers()
        {
            Release(ref _basePosition); Release(ref _baseRotation); Release(ref _baseScale);
            Release(ref _effectorPosition); Release(ref _effectorPositionScratch); Release(ref _effectorRotation); Release(ref _effectorScale);
            Release(ref _visibleIndices); Release(ref _bucketCounts); Release(ref _sourceBuffer); Release(ref _argsBuffer); Release(ref _drawBucketBuffer);
            Release(ref _effectorBuffer); Release(ref _effectorFalloffBuffer); Release(ref _effectorFunctionBuffer); Release(ref _effectorAuxBuffer);
            Release(ref _appearanceSpeed); Release(ref _appearancePreviousPosition);
            Release(ref _meshSampleBuffer);
            Release(ref _pushGridHeads); Release(ref _pushGridNext); Release(ref _pushGridCells);
            Release(ref _pushRadii); Release(ref _pushMaxRadius); Release(ref _pushInputPosition);
            _delayStates.Release();
            _pushApartStates.Release();
            _allocatedInstances = _sourceCapacity = _bucketCount = _pushGridSize = 0;
            _nestedLayoutHash = 0;
            _gpuBytes = 0;
            _buffersDirty = true;
            _effectorUploadHashValid = false;
            _appearanceSpeedHasPrevious = false;
            _appearanceSpeedFrame = -1;
            _appearanceSpeedTime = -1.0;
            _meshSampleHash = int.MinValue;
        }

        void ReleaseDraws()
        {
            foreach (Material material in _materialCache.Values) if (material != null) DestroyOwned(material);
            _materialCache.Clear();
            _materialSources.Clear();
            _materialContentHashes.Clear();
            _materialSyncFrame = -1;
            _draws.Clear();
        }

        static void Release(ref GraphicsBuffer buffer) { buffer?.Release(); buffer = null; }
        static void DestroyOwned(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }

        void OnDrawGizmosSelected()
        {
            Bounds b = CalculateLocalDistributionBounds();
            Color previousColor = Gizmos.color;
            Matrix4x4 previousMatrix = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            if (distribution == Distribution.Radial) DrawRadialGuides();
            Gizmos.color = new Color(1f, 0.45f, 0.14f, 0.3f);
            Gizmos.DrawWireCube(b.center, b.size);
            Gizmos.matrix = previousMatrix;
            Gizmos.color = previousColor;
        }

        void DrawRadialGuides()
        {
            int rings = Mathf.Max(1, radialRings);
            int displayedRings = Mathf.Min(rings, 64);
            bool closed = RadialArcIsClosed;
            Gizmos.color = new Color(1f, 0.45f, 0.14f, 0.8f);
            int previousRing = -1;
            for (int display = 0; display < displayedRings; display++)
            {
                int ring = displayedRings == 1 ? 0 : Mathf.RoundToInt(display * (rings - 1f) / (displayedRings - 1f));
                if (ring == previousRing) continue;
                previousRing = ring;
                int cloneCount = RadialCountAtRing(ring);
                if (cloneCount <= 1)
                {
                    Gizmos.DrawWireSphere(RadialLocalPoint(ring, 0f), 0.05f);
                    continue;
                }

                int samples = Mathf.Clamp(Mathf.Max(Mathf.Min(cloneCount, 128),
                    Mathf.CeilToInt(radialWaveCycles * 16f)), 8, 256);
                Vector3 first = RadialLocalPoint(ring, 0f);
                Vector3 previous = first;
                for (int sample = 1; sample < samples; sample++)
                {
                    float u = closed ? sample / (float)samples : sample / (float)(samples - 1);
                    Vector3 current = RadialLocalPoint(ring, u);
                    Gizmos.DrawLine(previous, current);
                    previous = current;
                }
                if (closed) Gizmos.DrawLine(previous, first);
            }
        }
    }
}
