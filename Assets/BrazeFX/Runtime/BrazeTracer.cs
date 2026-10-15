using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace BrazeFX
{
    /// <summary>
    /// C4D-style Tracer for Braze Cloners. It consolidates current clone positions and generates connection or path
    /// topology entirely on the GPU, then feeds the same indirect ribbon contract as Braze Trail Renderer.
    /// </summary>
    [AddComponentMenu("BrazeFX/Motion Design/Braze Tracer", 12)]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class BrazeTracer : MonoBehaviour, IBrazeAnimatedTarget
    {
        public enum TracingMode { TracePaths, ConnectAllObjects, ConnectElements, Web, NearestPoints }
        public enum TraceSpace { Global, Local }
        public enum SplineType { Linear, Cubic, Akima, BSpline, Bezier }
        public enum LimitMode { None, FromStart, FromEnd }
        public enum ColorMode { Gradient, Constant }
        public enum BlendMode { Additive, Alpha, Multiply }

        const int MaxDispatchGroups = 65535;
        const int MaxHistorySamples = 512;
        const int MaxLineLimit = 2_000_000;
        static readonly string[] RequiredKernelNames =
        {
            "CopyClonerPositions",
            "ClearTraceHistory",
            "AppendTraceHistory",
            "BuildNearestPairs",
            "BuildConnectionNodes",
            "BuildNearestConnectionNodes",
            "BuildTraceNodes",
            "ClearLineArgs",
            "BuildVisibleLines",
        };

        public static readonly List<BrazeTracer> Active = new();

        [Header("Sources")]
        [Tooltip("Ordered Cloners whose live GPU clone positions are traced.")]
        public List<BrazeCloner> sources = new();

        [Header("Tracer")]
        [Tooltip("Trace Paths records clone motion. Connect All makes one continuous sequence across every source. " +
                 "Connect Elements keeps each Cloner as its own segment. Web builds multiple deterministic links per clone. " +
                 "Nearest Points connects every selected point to its closest neighbour across all sources.")]
        public TracingMode tracingMode = TracingMode.ConnectAllObjects;
        [Tooltip("Temporarily stop writing Trace Paths. Disabled samples become gaps in the generated path.")]
        public bool traceActive = true;
        [Tooltip("Global stores world positions. Local stores positions relative to this Tracer so moving it carries the result.")]
        public TraceSpace space = TraceSpace.Global;
        [Tooltip("Also include each source Cloner's own origin as an element.")]
        public bool includeCloner;
        [Tooltip("Reverse the generated point order.")]
        public bool reverseSequence;

        [Header("Connections")]
        [Tooltip("Join the last element back to the first. Connect Elements closes each source independently.")]
        public bool closed;
        [Range(0f, 1f), Tooltip("Evenly sample the available connections before rendering. Useful for very large Cloners.")]
        public float connectionRatio = 1f;
        [Min(1), Tooltip("Hard GPU safety ceiling for generated connection or path ribbons.")]
        public int maximumLines = 250_000;

        [Header("Web")]
        [Range(1, 8), Tooltip("Number of outgoing links generated for each clone.")]
        public int linksPerClone = 2;
        [Min(1), Tooltip("Index distance between deterministic web links.")]
        public int indexStep = 3;
        [Tooltip("Choose stable pseudo-random partners instead of regular index steps.")]
        public bool randomizeLinks = true;
        [Min(0f), BrazeAnimatable("CONNECTIONS/Maximum Distance", 0f, 1000f),
         Tooltip("Reject Web or Nearest Points links longer than this distance. Zero disables distance filtering.")]
        public float maximumDistance;
        public int seed = 12345;

        [Header("Trace Paths")]
        [Min(1), Tooltip("Record one position after this many rendered frames.")]
        public int sampleStep = 1;
        public LimitMode limit = LimitMode.FromEnd;
        [Min(2), Tooltip("Trace length measured in recorded samples.")]
        public int amount = 64;
        [Min(0), Tooltip("Stable random plus/minus variation in trace length per clone.")]
        public int variance;
        [Range(2, MaxHistorySamples), Tooltip("Maximum retained history when Limit is None, and safety cap for other limits.")]
        public int historyCapacity = 160;
        [Range(0f, 1f), Tooltip("Percentage of clone paths recorded. This directly controls history allocation.")]
        public float tracedCloneRatio = 1f;

        [Header("Spline")]
        public SplineType splineType = SplineType.Linear;
        [Range(1, 16), Tooltip("Curve subdivisions per live connection. Linear connections always need one segment.")]
        public int connectionSubdivisions = 4;
        [Range(8, 64), Tooltip("Rendered segments along every recorded Trace Path.")]
        public int pathSegments = 32;

        [Header("Shape")]
        [Min(0f), BrazeAnimatable("SHAPE/Width Min", 0f, 2f)] public float width = 0.025f;
        [Min(0f), BrazeAnimatable("SHAPE/Width Max", 0f, 2f)] public float widthMax = 0.025f;
        [Tooltip("Width multiplier from the start to the end of every connection/path.")]
        public AnimationCurve widthCurve = DefaultWidthCurve();

        [Header("Shading")]
        public ColorMode colorMode = ColorMode.Gradient;
        [GradientUsage(true)] public Gradient gradient = DefaultGradient();
        [ColorUsage(true, true)] public Color color = new(1f, 0.36f, 0.06f, 1f);
        [Min(0f), BrazeAnimatable("SHADING/Glow", 0f, 10f)] public float glow = 1f;
        [Range(0f, 1f), BrazeAnimatable("SHADING/Opacity", 0f, 1f)] public float opacity = 1f;
        [Tooltip("Opacity from the start to the end of every connection/path.")]
        public AnimationCurve opacityCurve = AnimationCurve.Linear(0f, 1f, 1f, 1f);
        [Range(0f, 0.5f), BrazeAnimatable("SHADING/Soft Edge", 0f, 0.5f)] public float softEdge = 0.12f;
        public BlendMode blendMode = BlendMode.Alpha;

        [StructLayout(LayoutKind.Sequential)]
        struct SourceRange
        {
            public uint start;
            public uint count;
            public uint edgeStart;
            public uint edgeCount;
        }

        sealed class SourceInput
        {
            public BrazeCloner cloner;
            public GraphicsBuffer basePositions;
            public GraphicsBuffer offsets;
            public int cloneCount;
            public int outputCount;
        }

        readonly List<SourceInput> inputs = new();
        readonly List<BrazeCloner> uniqueSources = new();
        SourceRange[] rangeData = Array.Empty<SourceRange>();

        ComputeShader compute;
        Material material;
        MaterialPropertyBlock properties;
        Texture2D gradientLut, opacityLut, widthLut;
        GraphicsBuffer positions, sourceRanges, nearestPairs, lineNodes, visibleLines, args, indices, history;
        int kCopy, kClearHistory, kAppendHistory, kBuildNearestPairs, kBuildConnections, kBuildNearestConnections,
            kBuildTrace, kClearArgs, kVisible;
        int allocatedPositions, allocatedSources, allocatedLines, allocatedNodes, allocatedHistoryLines,
            allocatedHistorySamples, allocatedNearestPairs, allocatedSegments = -1;
        int lineCount, ringHead, recordedSamples, lastSampleFrame = -1, historyKey;
        bool historyKeyValid, prepared, lutsDirty = true, runtimeRetryPending;
        double lastTime = -1.0;
        long gpuBytes;
        string status = "Drop a Braze Cloner into Sources.";

        static readonly int IdVisible = Shader.PropertyToID("_VisibleIndices");
        static readonly int IdNodes = Shader.PropertyToID("_LineNodes");
        static readonly int IdRamp = Shader.PropertyToID("_Ramp");
        static readonly int IdOpacity = Shader.PropertyToID("_LengthAlphaTex");
        static readonly int IdWidth = Shader.PropertyToID("_WidthCurveTex");

        public Material RenderMaterial => material;
        public GraphicsBuffer ArgsBuffer => args;
        public GraphicsBuffer IndexBuffer => indices;
        public MaterialPropertyBlock Properties => properties;
        public bool Ready => prepared && material != null && args != null && indices != null && lineCount > 0;
        public int GeneratedLines => lineCount;
        public long GpuBytes => gpuBytes;
        public string Status => status;

        public static int ResolveTrackedCount(int sourceCount, float ratio, int maximum)
        {
            if (sourceCount <= 0 || ratio <= 0f || maximum <= 0) return 0;
            long proportional = ratio >= 1f
                ? sourceCount
                : Math.Max(1L, (long)Math.Round(sourceCount * (double)Mathf.Clamp01(ratio)));
            return (int)Math.Min(proportional, Math.Min(maximum, int.MaxValue));
        }

        public static int ResolveConnectionCount(long available, float ratio, int maximum)
        {
            if (available <= 0L || ratio <= 0f || maximum <= 0) return 0;
            long proportional = ratio >= 1f
                ? available
                : Math.Max(1L, (long)Math.Round(available * (double)Mathf.Clamp01(ratio)));
            return (int)Math.Min(proportional, Math.Min(maximum, int.MaxValue));
        }

        void Reset()
        {
            if (TryGetComponent(out BrazeCloner local)) sources.Add(local);
        }

        void OnEnable()
        {
            BrazeRuntimeRegistry.AddUnique(Active, this);
            if (!EnsureRuntime() && !runtimeRetryPending) enabled = false;
        }

        void OnDisable()
        {
            BrazeRuntimeRegistry.Remove(Active, this);
            ReleaseRuntime();
        }

        void OnDestroy() => ReleaseRuntime();

        void OnValidate()
        {
            maximumLines = Mathf.Clamp(maximumLines, 1, MaxLineLimit);
            linksPerClone = Mathf.Clamp(linksPerClone, 1, 8);
            indexStep = Mathf.Max(1, indexStep);
            maximumDistance = Mathf.Max(0f, maximumDistance);
            sampleStep = Mathf.Max(1, sampleStep);
            amount = Mathf.Max(2, amount);
            variance = Mathf.Max(0, variance);
            historyCapacity = Mathf.Clamp(historyCapacity, 2, MaxHistorySamples);
            tracedCloneRatio = Mathf.Clamp01(tracedCloneRatio);
            connectionRatio = Mathf.Clamp01(connectionRatio);
            connectionSubdivisions = Mathf.Clamp(connectionSubdivisions, 1, 16);
            pathSegments = Mathf.Clamp(pathSegments, 8, 64);
            width = Mathf.Max(0f, width);
            widthMax = Mathf.Max(width, widthMax);
            glow = Mathf.Max(0f, glow);
            opacity = Mathf.Clamp01(opacity);
            softEdge = Mathf.Clamp(softEdge, 0f, 0.5f);
            widthCurve ??= DefaultWidthCurve();
            opacityCurve ??= AnimationCurve.Linear(0f, 1f, 1f, 1f);
            gradient ??= DefaultGradient();
            lutsDirty = true;
        }

        public void OnBrazeAnimated() => OnValidate();

        void LateUpdate()
        {
            prepared = false;
            if (!isActiveAndEnabled || !EnsureRuntime() || !GatherSources())
            {
                ZeroArgs();
                return;
            }

            BakeLuts();
            int totalPositions = 0;
            for (int i = 0; i < inputs.Count; i++) totalPositions += inputs[i].outputCount;
            if (totalPositions < 1)
            {
                status = "The listed Cloners contain no instances.";
                ZeroArgs();
                return;
            }

            EnsurePositionResources(totalPositions, inputs.Count);
            CopyPositions(totalPositions);
            if (tracingMode == TracingMode.TracePaths) BuildTracePaths(totalPositions);
            else BuildConnections(totalPositions);
        }

        bool EnsureRuntime()
        {
            runtimeRetryPending = false;
            if (compute != null && material != null && args != null) return true;
            ComputeShader computeAsset = Resources.Load<ComputeShader>("BrazeTracer");
            Shader shader = BrazeRuntimeShaderLibrary.Find("BrazeFX/Trail");
            if (computeAsset == null || shader == null)
            {
                Debug.LogError("[BrazeFX] Braze Tracer needs Resources/BrazeTracer.compute and BrazeFX/Trail.");
                return false;
            }

            for (int i = 0; i < RequiredKernelNames.Length; i++)
            {
                string kernelName = RequiredKernelNames[i];
                if (computeAsset.HasKernel(kernelName)) continue;
                // During an asset refresh Unity can reload this component's assembly before it reimports the changed
                // compute shader. Keep the Tracer alive and retry from LateUpdate instead of throwing from FindKernel.
                runtimeRetryPending = true;
                status = $"BrazeTracer.compute is not ready (missing {kernelName}). Waiting for shader import; " +
                         "reimport the asset if this persists.";
                return false;
            }

            compute = Instantiate(computeAsset);
            compute.hideFlags = HideFlags.HideAndDontSave;
            kCopy = compute.FindKernel("CopyClonerPositions");
            kClearHistory = compute.FindKernel("ClearTraceHistory");
            kAppendHistory = compute.FindKernel("AppendTraceHistory");
            kBuildNearestPairs = compute.FindKernel("BuildNearestPairs");
            kBuildConnections = compute.FindKernel("BuildConnectionNodes");
            kBuildNearestConnections = compute.FindKernel("BuildNearestConnectionNodes");
            kBuildTrace = compute.FindKernel("BuildTraceNodes");
            kClearArgs = compute.FindKernel("ClearLineArgs");
            kVisible = compute.FindKernel("BuildVisibleLines");
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            properties = new MaterialPropertyBlock();
            gradientLut = BrazeTransferFunction.CreateRampLut("BrazeTracerGradient");
            opacityLut = BrazeTransferFunction.CreateCurveLut("BrazeTracerOpacity");
            widthLut = BrazeTransferFunction.CreateCurveLut("BrazeTracerWidth");
            args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured, 5, sizeof(uint));
            args.SetData(new uint[5]);
            return true;
        }

        bool GatherSources()
        {
            inputs.Clear();
            uniqueSources.Clear();
            if (sources == null) return false;
            for (int i = 0; i < sources.Count; i++)
            {
                BrazeCloner cloner = sources[i];
                if (cloner == null || !cloner.isActiveAndEnabled || uniqueSources.Contains(cloner)) continue;
                uniqueSources.Add(cloner);
                if (!cloner.TryGetGpuPositions(out GraphicsBuffer basePositions,
                        out GraphicsBuffer offsets, out int cloneCount) || cloneCount <= 0)
                    continue;
                inputs.Add(new SourceInput
                {
                    cloner = cloner,
                    basePositions = basePositions,
                    offsets = offsets,
                    cloneCount = cloneCount,
                    outputCount = cloneCount + (includeCloner ? 1 : 0),
                });
            }
            if (inputs.Count > 0) return true;
            status = "Drop an enabled Cloner with at least one source mesh into Sources.";
            return false;
        }

        void EnsurePositionResources(int positionCount, int sourceCount)
        {
            if (positions == null || allocatedPositions != positionCount)
            {
                Release(ref positions);
                positions = NewBuffer(positionCount, 16);
                allocatedPositions = positionCount;
                historyKeyValid = false;
            }
            if (sourceRanges == null || allocatedSources != sourceCount)
            {
                Release(ref sourceRanges);
                sourceRanges = NewBuffer(sourceCount, 16);
                allocatedSources = sourceCount;
                rangeData = new SourceRange[sourceCount];
                historyKeyValid = false;
            }
        }

        void CopyPositions(int totalPositions)
        {
            int outputOffset = 0;
            Matrix4x4 worldToTracer = space == TraceSpace.Local ? transform.worldToLocalMatrix : Matrix4x4.identity;
            compute.SetMatrix("_TracerWorldToLocal", worldToTracer);
            for (int i = 0; i < inputs.Count; i++)
            {
                SourceInput input = inputs[i];
                compute.SetInt("_CopySourceCount", input.cloneCount);
                compute.SetInt("_CopyOutputOffset", outputOffset);
                compute.SetInt("_CopyIncludeOrigin", includeCloner ? 1 : 0);
                compute.SetVector("_CopyClonerOrigin", input.cloner.transform.position);
                compute.SetBuffer(kCopy, "_CloneBasePosition", input.basePositions);
                compute.SetBuffer(kCopy, "_ClonePositionOffset", input.offsets);
                compute.SetBuffer(kCopy, "_TracerPositions", positions);
                Dispatch(kCopy, input.outputCount);
                outputOffset += input.outputCount;
            }
            compute.SetInt("_TotalPositions", totalPositions);
            compute.SetInt("_SourceCount", inputs.Count);
        }

        void BuildConnections(int totalPositions)
        {
            long edgeCursor = 0L;
            int positionCursor = 0;
            int links = Mathf.Clamp(linksPerClone, 1, 8);
            for (int i = 0; i < inputs.Count; i++)
            {
                int count = inputs[i].outputCount;
                long sourceEdges = tracingMode switch
                {
                    TracingMode.ConnectElements => count > 1 ? count - 1L + (closed ? 1L : 0L) : 0L,
                    TracingMode.Web => count > 1 ? (long)count * links : 0L,
                    _ => 0L,
                };
                rangeData[i] = new SourceRange
                {
                    start = (uint)positionCursor,
                    count = (uint)count,
                    edgeStart = (uint)Math.Min(edgeCursor, uint.MaxValue),
                    edgeCount = (uint)Math.Min(sourceEdges, uint.MaxValue),
                };
                edgeCursor += sourceEdges;
                positionCursor += count;
            }
            sourceRanges.SetData(rangeData);

            long rawEdges = tracingMode switch
            {
                TracingMode.ConnectAllObjects => Math.Max(0L,
                    totalPositions - 1L + (closed && totalPositions > 1 ? 1L : 0L)),
                TracingMode.NearestPoints => totalPositions > 1 ? totalPositions : 0L,
                _ => edgeCursor,
            };
            if (rawEdges > uint.MaxValue) rawEdges = uint.MaxValue;
            int capacity = ResolveConnectionCount(rawEdges, connectionRatio, maximumLines);
            int segments = splineType == SplineType.Linear ? 1 : connectionSubdivisions;
            if (!EnsureLineResources(capacity, segments)) return;

            SetTopologyGlobals(totalPositions, capacity, segments);
            compute.SetInt("_RawEdgeCount", (int)rawEdges);
            compute.SetInt("_TracingMode", (int)tracingMode);
            compute.SetInt("_Closed", closed ? 1 : 0);
            compute.SetInt("_WebLinks", links);
            compute.SetInt("_WebStep", indexStep);
            compute.SetInt("_WebRandom", randomizeLinks ? 1 : 0);
            compute.SetFloat("_MaxDistance", maximumDistance);
            if (tracingMode == TracingMode.NearestPoints)
            {
                EnsureNearestPairResources(capacity);
                compute.SetBuffer(kBuildNearestPairs, "_TracerPositions", positions);
                compute.SetBuffer(kBuildNearestPairs, "_NearestPairs", nearestPairs);
                Dispatch(kBuildNearestPairs, capacity);
                compute.SetBuffer(kBuildNearestConnections, "_TracerPositions", positions);
                compute.SetBuffer(kBuildNearestConnections, "_NearestPairs", nearestPairs);
                compute.SetBuffer(kBuildNearestConnections, "_LineNodes", lineNodes);
                Dispatch(kBuildNearestConnections, capacity * (segments + 1));
            }
            else
            {
                Release(ref nearestPairs);
                allocatedNearestPairs = 0;
                compute.SetBuffer(kBuildConnections, "_TracerPositions", positions);
                compute.SetBuffer(kBuildConnections, "_SourceRanges", sourceRanges);
                compute.SetBuffer(kBuildConnections, "_LineNodes", lineNodes);
                Dispatch(kBuildConnections, capacity * (segments + 1));
            }
            FinishLines(capacity, segments);
            lineCount = capacity;
            status = $"{capacity:n0} of {rawEdges:n0} connections generated on the GPU.";
            BindMaterial(segments);
            CalculateGpuBytes();
            prepared = capacity > 0;
        }

        void BuildTracePaths(int totalPositions)
        {
            int traceLines = ResolveTrackedCount(totalPositions, tracedCloneRatio, maximumLines);
            int samples = limit == LimitMode.None
                ? historyCapacity
                : Mathf.Clamp(amount + variance, 2, historyCapacity);
            int segments = pathSegments;
            if (!EnsureLineResources(traceLines, segments) || !EnsureHistory(traceLines, samples)) return;

            int key = TraceHistoryKey(totalPositions, traceLines, samples);
            double now = Application.isPlaying ? Time.timeAsDouble : Time.realtimeSinceStartupAsDouble;
            if (!historyKeyValid || key != historyKey || (Application.isPlaying && lastTime >= 0.0 && now + 1e-6 < lastTime))
            {
                historyKey = key;
                historyKeyValid = true;
                ResetHistory(traceLines, samples);
            }
            lastTime = now;

            bool canRecord = limit != LimitMode.FromStart || recordedSamples < samples;
            bool sampleDue = lastSampleFrame < 0 || Time.frameCount < lastSampleFrame ||
                             Time.frameCount - lastSampleFrame >= sampleStep;
            if (canRecord && sampleDue)
            {
                if (recordedSamples > 0) ringHead = (ringHead + 1) % samples;
                compute.SetInt("_TotalPositions", totalPositions);
                compute.SetInt("_LineCapacity", traceLines);
                compute.SetInt("_HistorySamples", samples);
                compute.SetInt("_RingHead", ringHead);
                compute.SetInt("_TraceActive", traceActive ? 1 : 0);
                compute.SetBuffer(kAppendHistory, "_TracerPositions", positions);
                compute.SetBuffer(kAppendHistory, "_TraceHistory", history);
                Dispatch(kAppendHistory, traceLines);
                recordedSamples = Mathf.Min(samples, recordedSamples + 1);
                lastSampleFrame = Time.frameCount;
            }

            SetTopologyGlobals(totalPositions, traceLines, segments);
            compute.SetInt("_HistorySamples", samples);
            compute.SetInt("_RingHead", ringHead);
            compute.SetInt("_RecordedSamples", recordedSamples);
            compute.SetInt("_LimitMode", (int)limit);
            compute.SetInt("_LimitAmount", amount);
            compute.SetInt("_LimitVariance", variance);
            compute.SetBuffer(kBuildTrace, "_TraceHistory", history);
            compute.SetBuffer(kBuildTrace, "_LineNodes", lineNodes);
            Dispatch(kBuildTrace, traceLines * (segments + 1));
            FinishLines(traceLines, segments);
            lineCount = traceLines;
            status = $"{traceLines:n0} of {totalPositions:n0} clone paths recorded on the GPU ({recordedSamples}/{samples} samples).";
            BindMaterial(segments);
            CalculateGpuBytes();
            prepared = traceLines > 0 && recordedSamples >= 2;
        }

        void SetTopologyGlobals(int totalPositions, int capacity, int segments)
        {
            compute.SetInt("_TotalPositions", totalPositions);
            compute.SetInt("_SourceCount", inputs.Count);
            compute.SetInt("_LineCapacity", capacity);
            compute.SetInt("_NodeCount", segments + 1);
            compute.SetInt("_Segments", segments);
            compute.SetInt("_SplineType", (int)splineType);
            compute.SetInt("_Reverse", reverseSequence ? 1 : 0);
            compute.SetInt("_Seed", seed);
        }

        bool EnsureLineResources(int capacity, int segments)
        {
            if (capacity <= 0)
            {
                status = "No lines are available with the current source and ratio settings.";
                ZeroArgs();
                return false;
            }
            long nodeTotal = (long)capacity * (segments + 1L);
            if (nodeTotal > int.MaxValue)
            {
                status = "The requested line/detail combination exceeds the GPU buffer address limit.";
                ZeroArgs();
                return false;
            }
            if (lineNodes == null || allocatedLines != capacity || allocatedNodes != segments + 1)
            {
                Release(ref lineNodes);
                Release(ref visibleLines);
                lineNodes = NewBuffer((int)nodeTotal, 48);
                visibleLines = NewBuffer(capacity, sizeof(uint));
                allocatedLines = capacity;
                allocatedNodes = segments + 1;
            }
            EnsureIndices(segments);
            return true;
        }

        bool EnsureHistory(int lines, int samples)
        {
            long count = (long)lines * samples;
            if (count > int.MaxValue)
            {
                status = "Trace history exceeds the GPU buffer address limit. Lower Traced Clones or History Capacity.";
                ZeroArgs();
                return false;
            }
            if (history != null && allocatedHistoryLines == lines && allocatedHistorySamples == samples) return true;
            Release(ref history);
            history = NewBuffer((int)count, 16);
            allocatedHistoryLines = lines;
            allocatedHistorySamples = samples;
            historyKeyValid = false;
            return true;
        }

        void EnsureNearestPairResources(int capacity)
        {
            if (nearestPairs != null && allocatedNearestPairs == capacity) return;
            Release(ref nearestPairs);
            nearestPairs = NewBuffer(capacity, sizeof(uint) * 2);
            allocatedNearestPairs = capacity;
        }

        void ResetHistory(int lines, int samples)
        {
            ringHead = 0;
            recordedSamples = 0;
            lastSampleFrame = -1;
            compute.SetInt("_LineCapacity", lines);
            compute.SetInt("_HistorySamples", samples);
            compute.SetBuffer(kClearHistory, "_TraceHistory", history);
            Dispatch(kClearHistory, lines * samples);
        }

        int TraceHistoryKey(int totalPositions, int lines, int samples)
        {
            unchecked
            {
                int hash = totalPositions;
                hash = hash * 31 + lines;
                hash = hash * 31 + samples;
                hash = hash * 31 + (int)space;
                hash = hash * 31 + (includeCloner ? 1 : 0);
                for (int i = 0; i < inputs.Count; i++)
                {
                    hash = hash * 31 + inputs[i].cloner.BrazeInstanceId();
                    hash = hash * 31 + inputs[i].cloneCount;
                }
                return hash;
            }
        }

        void FinishLines(int capacity, int segments)
        {
            compute.SetInt("_Segments", segments);
            compute.SetBuffer(kClearArgs, "_LineArgs", args);
            compute.Dispatch(kClearArgs, 1, 1, 1);
            compute.SetInt("_LineCapacity", capacity);
            compute.SetInt("_NodeCount", segments + 1);
            compute.SetBuffer(kVisible, "_LineNodes", lineNodes);
            compute.SetBuffer(kVisible, "_VisibleLines", visibleLines);
            compute.SetBuffer(kVisible, "_LineArgs", args);
            Dispatch(kVisible, capacity);
        }

        void EnsureIndices(int segments)
        {
            if (indices != null && allocatedSegments == segments) return;
            Release(ref indices);
            var data = new uint[segments * 6];
            for (int i = 0; i < segments; i++)
            {
                uint a0 = (uint)(i * 2), a1 = a0 + 1u, b0 = a0 + 2u, b1 = a0 + 3u;
                int o = i * 6;
                data[o] = a0; data[o + 1] = b0; data[o + 2] = a1;
                data[o + 3] = a1; data[o + 4] = b0; data[o + 5] = b1;
            }
            indices = new GraphicsBuffer(GraphicsBuffer.Target.Index, data.Length, sizeof(uint));
            indices.SetData(data);
            allocatedSegments = segments;
        }

        void BindMaterial(int segments)
        {
            Matrix4x4 model = space == TraceSpace.Local ? transform.localToWorldMatrix : Matrix4x4.identity;
            properties.SetBuffer(IdVisible, visibleLines);
            properties.SetBuffer(IdNodes, lineNodes);
            properties.SetMatrix("_Model", model);
            properties.SetInt("_Segments", segments);
            properties.SetInt("_NodeCount", segments + 1);
            properties.SetFloat("_WidthMin", Mathf.Min(width, widthMax));
            properties.SetFloat("_WidthMax", Mathf.Max(width, widthMax));
            properties.SetTexture(IdWidth, widthLut);
            properties.SetFloat("_ColorMode", colorMode == ColorMode.Gradient ? 1f : 3f);
            properties.SetTexture(IdRamp, gradientLut);
            properties.SetColor("_ConstColor", color);
            properties.SetColor("_FireColor", color);
            properties.SetColor("_SmokeColor", color);
            properties.SetFloat("_ColorMid", 0.5f);
            // In the shared ribbon shader 1 is neutral brightness, so Tracer's user-facing Glow can start at zero.
            properties.SetFloat("_Emissive", 1f + glow);
            properties.SetFloat("_Opacity", opacity);
            properties.SetTexture(IdOpacity, opacityLut);
            properties.SetFloat("_UseOpacityLife", 0f);
            properties.SetFloat("_SoftEdge", softEdge);
            properties.SetFloat("_SelfShadow", 0f);
            properties.SetFloat("_FireAmount", 0f);
            properties.SetFloat("_SmokeAmount", 0f);
            properties.SetFloat("_Multiply", blendMode == BlendMode.Multiply ? 1f : 0f);

            int src = (int)UnityEngine.Rendering.BlendMode.One;
            int dst = blendMode switch
            {
                BlendMode.Alpha => (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha,
                BlendMode.Multiply => (int)UnityEngine.Rendering.BlendMode.Zero,
                _ => (int)UnityEngine.Rendering.BlendMode.One,
            };
            if (blendMode == BlendMode.Multiply) src = (int)UnityEngine.Rendering.BlendMode.DstColor;
            material.SetFloat("_BlendSrc", src);
            material.SetFloat("_BlendDst", dst);
            material.SetFloat("_ZTest", (float)CompareFunction.LessEqual);
        }

        void BakeLuts()
        {
            if (!lutsDirty) return;
            BrazeTransferFunction.BakeRamp(gradientLut, gradient);
            BrazeTransferFunction.BakeCurve1DSmooth(opacityLut, opacityCurve);
            BrazeTransferFunction.BakeCurve1DUnclampedSmooth(widthLut, widthCurve);
            lutsDirty = false;
        }

        void Dispatch(int kernel, int threads)
        {
            if (threads <= 0) return;
            int groups = Mathf.CeilToInt(threads / 64f);
            int x = groups <= MaxDispatchGroups ? groups : Mathf.Min(MaxDispatchGroups, Mathf.CeilToInt(Mathf.Sqrt(groups)));
            int y = Mathf.CeilToInt((float)groups / x);
            compute.SetInt("_RowThreads", x * 64);
            compute.Dispatch(kernel, x, y, 1);
        }

        void ZeroArgs()
        {
            prepared = false;
            lineCount = 0;
            if (args != null) args.SetData(new uint[5]);
        }

        void CalculateGpuBytes()
        {
            gpuBytes = Bytes(positions) + Bytes(sourceRanges) + Bytes(nearestPairs) + Bytes(lineNodes) + Bytes(visibleLines) +
                       Bytes(args) + Bytes(indices) + Bytes(history);
            if (gradientLut != null) gpuBytes += gradientLut.width * 8L;
            if (opacityLut != null) gpuBytes += opacityLut.width * 2L;
            if (widthLut != null) gpuBytes += widthLut.width * 2L;
        }

        void ReleaseRuntime()
        {
            Release(ref positions); Release(ref sourceRanges); Release(ref nearestPairs); Release(ref lineNodes);
            Release(ref visibleLines);
            Release(ref args); Release(ref indices); Release(ref history);
            if (material != null) DestroyOwned(material); material = null;
            if (compute != null) DestroyOwned(compute); compute = null;
            if (gradientLut != null) DestroyOwned(gradientLut); gradientLut = null;
            if (opacityLut != null) DestroyOwned(opacityLut); opacityLut = null;
            if (widthLut != null) DestroyOwned(widthLut); widthLut = null;
            properties = null;
            allocatedPositions = allocatedSources = allocatedLines = allocatedNodes = 0;
            allocatedHistoryLines = allocatedHistorySamples = allocatedNearestPairs = 0;
            allocatedSegments = -1;
            historyKeyValid = prepared = false;
            gpuBytes = 0L;
        }

        static GraphicsBuffer NewBuffer(int count, int stride)
            => new(GraphicsBuffer.Target.Structured, Mathf.Max(1, count), stride);
        static long Bytes(GraphicsBuffer buffer) => buffer != null ? (long)buffer.count * buffer.stride : 0L;
        static void Release(ref GraphicsBuffer buffer) { buffer?.Release(); buffer = null; }
        static void DestroyOwned(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }

        static AnimationCurve DefaultWidthCurve() => AnimationCurve.Linear(0f, 1f, 1f, 1f);

        static Gradient DefaultGradient()
        {
            var value = new Gradient();
            value.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(1f, 0.2f, 0.03f), 0f),
                    new GradientColorKey(new Color(1f, 0.75f, 0.15f), 0.5f),
                    new GradientColorKey(new Color(0.15f, 0.65f, 1f), 1f),
                },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return value;
        }
    }
}
