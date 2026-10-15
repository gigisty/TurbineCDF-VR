using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX.CFD
{
    /// <summary>
    /// Boundary-free presentation facade for advanced CFD studies. User-authored Boundary Patches remain the
    /// only physics conditions; this component owns only dye rendering, reference normalization, and one hidden
    /// Analysis Slice. Use Wind Tunnel instead when its automatic inlet/outlet are desired.
    /// </summary>
    [AddComponentMenu("BrazeFX/CFD/Advanced/Visualizer", 6)]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-89)]
    public sealed class BrazeCfdVisualizer : MonoBehaviour, IBrazeCfdVisualization
    {
        public static readonly List<BrazeCfdVisualizer> Active = new();

        [Header("Study")]
        [Tooltip("Domain whose solved fields are presented. This component never creates or modifies boundaries.")]
        public BrazeCfdDomain domain;
        [Tooltip("Derive velocity/pressure normalization and dye lifetime from the strongest active Velocity Inlet " +
                 "or internal Flow Source.")]
        public bool automaticReferenceSpeed = true;
        [Min(0.01f), Tooltip("Presentation reference in world units/second when automatic reference is disabled.")]
        public float referenceSpeed = 5f;

        [Header("Analysis Slice")]
        public bool showAnalysisSlice;
        public BrazeCfdWindPreview preview = BrazeCfdWindPreview.ScalarAndVectors;
        public BrazeCfdSlicePlane slicePlane = BrazeCfdSlicePlane.Vertical;
        public BrazeCfdSliceQuantity sliceQuantity = BrazeCfdSliceQuantity.VelocityMagnitude;
        public BrazeCfdSliceColorMap sliceColorMap = BrazeCfdSliceColorMap.CfdRainbow;
        [Range(-1f, 1f)] public float sliceOffset;
        public bool automaticSliceRange = true;
        public float sliceMinimum;
        public float sliceMaximum = 5f;
        [Range(4, 80)] public int vectorColumns = 18;
        [Range(0f, 1f)] public float sliceOpacity = 0.92f;

        [Header("Volume Presentation")]
        [Tooltip("Render passive diagnostic dye, or visualize the solved fluid-temperature volume when Color By is " +
                 "Temperature. This component does not alter boundary physics.")]
        public bool showSmoke = true;
        public BrazeCfdSmokePreset smokePreset = BrazeCfdSmokePreset.FlowSpeed;
        [ColorUsage(false, true)] public Color customSmokeColor = new(0.08f, 0.42f, 1f, 1f);
        [Range(0f, 1f)] public float smokeOpacity = 0.7f;
        [Range(0.5f, 3f)] public float smokeBrightness = 1.65f;
        [Range(0.5f, 6f)] public float smokeColorContrast = 1.5f;
        [Range(0f, 1f)] public float smokeFlowEmphasis = 0.35f;
        [Tooltip("Match dye lifetime to domain transit time using the automatic flow reference speed.")]
        public bool automaticDyePersistence = true;
        [Min(0.1f)] public float smokePersistenceSeconds = 6f;

        [Header("Temperature Scale")]
        [Tooltip("Range Temperature colour from the maximum temperature measured in the solved fluid field. " +
                 "Disable for a fixed Heat Map range when comparing separate runs.")]
        public bool automaticTemperatureRange = true;
        [Min(0.1f), Tooltip("Smallest Celsius span auto range will display. This prevents tiny numerical " +
                            "temperature differences from being exaggerated into a full blue-to-red map.")]
        public float minimumAutomaticTemperatureSpanC = 5f;
        [Min(0.05f), Tooltip("Seconds for the displayed maximum to follow the measured air temperature. Heating " +
                             "attacks faster; cooling releases more slowly to avoid colour pumping.")]
        public float temperatureRangeResponseSeconds = 0.75f;
        public bool showGizmo = true;

        [SerializeField, HideInInspector] BrazeCfdAnalysisSlice analysisSlice;

        int configurationHash;
        bool synchronizing;
#if UNITY_EDITOR
        bool editorSyncQueued;
#endif
        Texture2D smokePreint;
        int smokeLutHash;
        string status = "Assign a CFD Domain.";
        bool exclusivePreviewValid;
        bool previousShowAnalysisSlice;
        float displayedTemperatureMinimumC;
        float displayedTemperatureMaximumC;
        bool temperatureRangeInitialized;
        int temperatureRangeFrame = int.MinValue;
        int temperatureRangeStateHash;

        BrazeCfdDomain IBrazeCfdVisualization.Domain => domain;
        public string Status => status;
        public BrazeCfdAnalysisSlice ManagedAnalysisSlice => analysisSlice;
        public bool SmokeEnabled => showSmoke && smokePreset != BrazeCfdSmokePreset.Off;
        public bool SmokeTemperatureColor => SmokeEnabled && smokePreset == BrazeCfdSmokePreset.Temperature;
        public bool SmokeFlowSpeedColor => SmokeEnabled && smokePreset == BrazeCfdSmokePreset.FlowSpeed;
        public bool TemperatureAutoRangeEnabled => automaticTemperatureRange &&
            (SmokeTemperatureColor || (WantsAnalysisSlice && automaticSliceRange &&
                                       sliceQuantity == BrazeCfdSliceQuantity.Temperature));
        public float SmokeColorContrast => smokeColorContrast;
        public float SmokeFlowEmphasis => smokeFlowEmphasis;
        public float SmokeOpacity => smokeOpacity;
        public float SmokeBrightness => smokeBrightness;
        public float SmokeHeatMinimum => domain != null
            ? (ResolvedTemperatureMinimumC - domain.ambientTemperatureC) * domain.HeatUnitsPerDegree
            : 0f;
        public float SmokeHeatInverseRange => domain != null
            ? 1f / Mathf.Max((ResolvedTemperatureMaximumC - ResolvedTemperatureMinimumC) *
                             domain.HeatUnitsPerDegree, 0.001f)
            : 1f;
        public bool TemperatureMeasurementValid => domain != null && domain.TemperatureMeasurementValid;
        public float MeasuredPeakTemperatureC => domain != null
            ? domain.MeasuredPeakTemperatureC : 0f;
        public float DisplayedTemperatureMinimumC
        {
            get { RefreshTemperatureRange(); return displayedTemperatureMinimumC; }
        }
        public float DisplayedTemperatureMaximumC
        {
            get { RefreshTemperatureRange(); return displayedTemperatureMaximumC; }
        }
        public float ResolvedReferenceSpeed
        {
            get
            {
                if (!automaticReferenceSpeed) return Mathf.Max(referenceSpeed, 0.01f);
                return TryGetStrongestFlowDriver(out Vector3 velocity, out _) ? velocity.magnitude
                    : 0f;
            }
        }
        public int DyeInletCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < BrazeCfdBoundaryPatch.Active.Count; i++)
                {
                    BrazeCfdBoundaryPatch patch = BrazeCfdBoundaryPatch.Active[i];
                    if (patch != null && patch.isActiveAndEnabled && patch.Targets(domain) &&
                        patch.mode == BrazeCfdBoundaryPatchMode.VelocityInlet && patch.setInletSmoke &&
                        patch.inletSmokeDensity > 0.0001f) count++;
                }
                return count;
            }
        }
        public int DyeFlowSourceCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < BrazeCfdSource.Active.Count; i++)
                {
                    if (BrazeCfdSource.Active[i] is BrazeCfdFlowSource source &&
                        source != null && source.isActiveAndEnabled && source.Targets(domain) &&
                        source.InjectsPassiveDye) count++;
                }
                return count;
            }
        }
        public int DyeSourceCount => DyeInletCount + DyeFlowSourceCount;
        public float EffectiveSmokePersistenceSeconds
        {
            get
            {
                if (!automaticDyePersistence) return smokePersistenceSeconds;
                float speed = ResolvedReferenceSpeed;
                if (domain == null || domain.Container == null || speed <= 0.01f)
                    return Mathf.Min(smokePersistenceSeconds, 6f);
                TryGetReferenceVelocity(out _, out BrazeCfdDomainFace face);
                Vector3 size = domain.Container.WorldSize();
                float length = face switch
                {
                    BrazeCfdDomainFace.NegativeX or BrazeCfdDomainFace.PositiveX => Mathf.Abs(size.x),
                    BrazeCfdDomainFace.NegativeY or BrazeCfdDomainFace.PositiveY => Mathf.Abs(size.y),
                    _ => Mathf.Abs(size.z),
                };
                return Mathf.Clamp(length / speed * 1.5f, 0.5f, 30f);
            }
        }
        public Texture2D SmokePreint
        {
            get
            {
                if (!SmokeEnabled) return null;
                EnsureSmokeLut();
                return smokePreint;
            }
        }

        float ResolvedTemperatureMinimumC => DisplayedTemperatureMinimumC;
        float ResolvedTemperatureMaximumC => DisplayedTemperatureMaximumC;

        void Reset()
        {
            ResolveDomain();
            SyncManagedComponents(true);
        }

        void OnEnable()
        {
            ResolveDomain();
            ResetTemperatureRange();
            BrazeRuntimeRegistry.AddUnique(Active, this);
            SyncManagedComponents(true);
        }

        void OnDisable()
        {
            BrazeRuntimeRegistry.Remove(Active, this);
            SetManagedEnabled(false);
            ReleaseSmokeLut();
        }

        void OnDestroy()
        {
            BrazeRuntimeRegistry.Remove(Active, this);
            DestroyManagedSlice();
            ReleaseSmokeLut();
        }

        void OnValidate()
        {
            referenceSpeed = Mathf.Max(0.01f, referenceSpeed);
            sliceOffset = Mathf.Clamp(sliceOffset, -1f, 1f);
            sliceMaximum = Mathf.Max(sliceMinimum + 0.0001f, sliceMaximum);
            vectorColumns = Mathf.Clamp(vectorColumns, 4, 80);
            sliceOpacity = Mathf.Clamp01(sliceOpacity);
            smokeOpacity = Mathf.Clamp01(smokeOpacity);
            smokeBrightness = Mathf.Clamp(smokeBrightness, 0.5f, 3f);
            smokeColorContrast = Mathf.Clamp(smokeColorContrast, 0.5f, 6f);
            smokeFlowEmphasis = Mathf.Clamp01(smokeFlowEmphasis);
            smokePersistenceSeconds = Mathf.Max(0.1f, smokePersistenceSeconds);
            minimumAutomaticTemperatureSpanC = Mathf.Max(0.1f, minimumAutomaticTemperatureSpanC);
            temperatureRangeResponseSeconds = Mathf.Max(0.05f, temperatureRangeResponseSeconds);
            ResetTemperatureRange();
            ResolveDomain();
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
            RefreshTemperatureRange();
            if (analysisSlice != null && WantsAnalysisSlice && automaticSliceRange &&
                sliceQuantity == BrazeCfdSliceQuantity.Temperature)
                ConfigureAnalysisSlice();
            int hash = ComputeConfigurationHash();
            if (hash != configurationHash) SyncManagedComponents(true);
        }

        internal void SyncManagedComponents(bool createMissing)
        {
            if (synchronizing) return;
            synchronizing = true;
            try
            {
                EnforceExclusivePreview();
                ResolveDomain();
                BrazeCfdWindTunnel conflict = FindWindTunnel();
                if (domain == null || domain.Container == null)
                {
                    SetManagedEnabled(false);
                    status = "Assign a CFD Domain.";
                }
                else if (conflict != null)
                {
                    SetManagedEnabled(false);
                    ReleaseSmokeLut();
                    status = "Wind Tunnel already owns this Domain's presentation. Remove one of the two facades.";
                }
                else
                {
                    if (createMissing && WantsAnalysisSlice) analysisSlice = EnsureAnalysisSlice(analysisSlice);
                    ConfigureAnalysisSlice();
                    if (SmokeEnabled) EnsureSmokeLut(); else ReleaseSmokeLut();
                    if (WantsAnalysisSlice)
                        status = $"{sliceQuantity} analysis slice is active; custom boundary patches remain unchanged.";
                    else if (!SmokeEnabled)
                        status = "Presentation is disabled.";
                    else if (SmokeTemperatureColor)
                        status = "Solved-air Temperature volume is active; ambient air is suppressed so heated " +
                                 "plumes remain visible.";
                    else if (automaticReferenceSpeed && ResolvedReferenceSpeed <= 0.0001f)
                        status = "No moving Velocity Inlet or Flow Source was found for automatic reference normalization.";
                    else if (DyeSourceCount > 0)
                        status = $"{smokePreset} dye presentation is active for {DyeSourceCount} source(s).";
                    else
                        status = "Dye presentation is ready. Enable Inject Passive Dye on a Velocity Inlet or Flow Source.";
                }
                configurationHash = ComputeConfigurationHash();
            }
            finally { synchronizing = false; }
        }

        void EnforceExclusivePreview()
        {
            if (showAnalysisSlice && showSmoke)
            {
                if (exclusivePreviewValid && !previousShowAnalysisSlice) showSmoke = false;
                else showAnalysisSlice = false;
            }
            previousShowAnalysisSlice = showAnalysisSlice;
            exclusivePreviewValid = true;
        }

        bool WantsAnalysisSlice => showAnalysisSlice && preview != BrazeCfdWindPreview.None;
        bool WantsScalar => preview == BrazeCfdWindPreview.ScalarSlice ||
                            preview == BrazeCfdWindPreview.ScalarAndVectors;
        bool WantsVectors => preview == BrazeCfdWindPreview.VectorField ||
                             preview == BrazeCfdWindPreview.ScalarAndVectors;

        BrazeCfdAnalysisSlice EnsureAnalysisSlice(BrazeCfdAnalysisSlice current)
        {
            if (current == null || current.gameObject != gameObject || current.managedByVisualizer != this)
            {
                BrazeCfdAnalysisSlice[] candidates = GetComponents<BrazeCfdAnalysisSlice>();
                current = null;
                for (int i = 0; i < candidates.Length; i++)
                    if (candidates[i] != null && candidates[i].managedByVisualizer == this)
                    { current = candidates[i]; break; }
                if (current == null) current = gameObject.AddComponent<BrazeCfdAnalysisSlice>();
            }
            current.managedByVisualizer = this;
            current.hideFlags |= HideFlags.HideInInspector;
            return current;
        }

        void ConfigureAnalysisSlice()
        {
            if (analysisSlice == null) return;
            analysisSlice.domain = domain;
            analysisSlice.plane = slicePlane;
            analysisSlice.quantity = sliceQuantity;
            analysisSlice.colorMap = sliceColorMap;
            analysisSlice.offset = sliceOffset;
            analysisSlice.showScalar = WantsScalar;
            analysisSlice.showVectors = WantsVectors;
            analysisSlice.vectorColumns = vectorColumns;
            analysisSlice.opacity = sliceOpacity;
            analysisSlice.showGizmo = showGizmo && WantsAnalysisSlice;
            ResolveSliceRange(out analysisSlice.scalarMinimum, out analysisSlice.scalarMaximum);
            analysisSlice.enabled = isActiveAndEnabled && WantsAnalysisSlice && FindWindTunnel() == null;
        }

        void ResolveSliceRange(out float minimum, out float maximum)
        {
            minimum = sliceMinimum;
            maximum = sliceMaximum;
            if (automaticSliceRange && domain != null && domain.Container != null)
            {
                float speed = ResolvedReferenceSpeed;
                switch (sliceQuantity)
                {
                    case BrazeCfdSliceQuantity.Pressure:
                        minimum = -1f; maximum = 1f;
                        break;
                    case BrazeCfdSliceQuantity.Temperature:
                        minimum = ResolvedTemperatureMinimumC;
                        maximum = ResolvedTemperatureMaximumC;
                        break;
                    case BrazeCfdSliceQuantity.Vorticity:
                        Vector3Int grid = domain.Container.SolverRes;
                        Vector3 size = domain.Container.WorldSize();
                        float cell = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) /
                                     Mathf.Max(1, Mathf.Max(grid.x, Mathf.Max(grid.y, grid.z)));
                        minimum = 0f; maximum = Mathf.Max(1f, speed / Mathf.Max(cell, 0.001f));
                        break;
                    default:
                        minimum = 0f; maximum = Mathf.Max(speed * 1.25f, 0.1f);
                        break;
                }
            }
            if (sliceQuantity == BrazeCfdSliceQuantity.Temperature && domain != null)
            {
                minimum = (minimum - domain.ambientTemperatureC) * domain.HeatUnitsPerDegree;
                maximum = (maximum - domain.ambientTemperatureC) * domain.HeatUnitsPerDegree;
            }
            maximum = Mathf.Max(minimum + 0.0001f, maximum);
        }

        internal bool TryGetReferenceVelocity(out Vector3 worldVelocity, out BrazeCfdDomainFace face)
        {
            bool found = TryGetStrongestFlowDriver(out Vector3 driverVelocity, out face);
            if (automaticReferenceSpeed)
            {
                worldVelocity = driverVelocity;
                return found;
            }
            float speed = Mathf.Max(referenceSpeed, 0.01f);
            Vector3 direction = found && driverVelocity.sqrMagnitude > 1e-8f
                ? driverVelocity.normalized
                : domain != null && domain.Container != null ? domain.Container.transform.forward : Vector3.forward;
            worldVelocity = direction * speed;
            return true;
        }

        bool TryGetStrongestInlet(out Vector3 worldVelocity, out BrazeCfdDomainFace face)
        {
            worldVelocity = Vector3.zero;
            face = BrazeCfdDomainFace.NegativeZ;
            if (domain == null || domain.Container == null) return false;
            float strongest = 0f;
            for (int i = 0; i < BrazeCfdBoundaryPatch.Active.Count; i++)
            {
                BrazeCfdBoundaryPatch patch = BrazeCfdBoundaryPatch.Active[i];
                if (patch == null || !patch.isActiveAndEnabled || !patch.Targets(domain) ||
                    patch.mode != BrazeCfdBoundaryPatchMode.VelocityInlet) continue;
                Vector3 outward = BrazeCfdBoundaryPatch.LocalOutwardNormal(patch.face);
                BrazeCfdBoundaryPatch.LocalTangentAxes(patch.face, out Vector3 u, out Vector3 v);
                Vector3 local = -outward * patch.normalSpeed + u * patch.tangentialVelocity.x +
                                v * patch.tangentialVelocity.y;
                float magnitude = local.magnitude;
                if (magnitude <= strongest) continue;
                strongest = magnitude;
                face = patch.face;
                worldVelocity = domain.Container.transform.rotation * local;
            }
            return strongest > 0.0001f;
        }

        bool TryGetStrongestFlowDriver(out Vector3 worldVelocity, out BrazeCfdDomainFace face)
        {
            bool found = TryGetStrongestInlet(out worldVelocity, out face);
            float strongest = found ? worldVelocity.sqrMagnitude : 0f;
            if (domain == null || domain.Container == null) return found;

            for (int i = 0; i < BrazeCfdSource.Active.Count; i++)
            {
                if (BrazeCfdSource.Active[i] is not BrazeCfdFlowSource source ||
                    source == null || !source.isActiveAndEnabled || !source.Targets(domain)) continue;
                Vector3 candidate = source.transform.forward * source.speed;
                if (candidate.sqrMagnitude <= strongest) continue;
                strongest = candidate.sqrMagnitude;
                worldVelocity = candidate;
                face = ReferenceFaceForVelocity(candidate);
            }
            return strongest > 1e-8f;
        }

        BrazeCfdDomainFace ReferenceFaceForVelocity(Vector3 worldVelocity)
        {
            Vector3 local = Quaternion.Inverse(domain.Container.transform.rotation) * worldVelocity;
            Vector3 magnitude = new(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));
            if (magnitude.x >= magnitude.y && magnitude.x >= magnitude.z)
                return local.x >= 0f ? BrazeCfdDomainFace.NegativeX : BrazeCfdDomainFace.PositiveX;
            if (magnitude.y >= magnitude.z)
                return local.y >= 0f ? BrazeCfdDomainFace.NegativeY : BrazeCfdDomainFace.PositiveY;
            return local.z >= 0f ? BrazeCfdDomainFace.NegativeZ : BrazeCfdDomainFace.PositiveZ;
        }

        void SetManagedEnabled(bool value)
        {
            if (analysisSlice != null && analysisSlice.managedByVisualizer == this)
                analysisSlice.enabled = value && WantsAnalysisSlice;
        }

        void EnsureSmokeLut()
        {
            Color color = ResolveSmokeColor();
            int hash = color.GetHashCode();
            if (smokePreint != null && smokeLutHash == hash) return;
            if (smokePreint == null) smokePreint = BrazeTransferFunction.CreateLut("BrazeCfdVisualizerPreint");
            var gradient = new Gradient();
            Color thin = color * 0.92f;
            Color dense = color * 1.12f;
            gradient.SetKeys(
                new[] { new GradientColorKey(thin, 0f), new GradientColorKey(color, 0.45f),
                        new GradientColorKey(dense, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            AnimationCurve opacityCurve = BrazeTransferFunction.LinearCurve(
                new Vector2(0f, 0f), new Vector2(0.08f, 0.05f), new Vector2(0.45f, 0.55f),
                new Vector2(1f, 1f));
            BrazeTransferFunction.BakeSmoke(smokePreint, gradient, opacityCurve);
            smokeLutHash = hash;
        }

        Color ResolveSmokeColor() => smokePreset switch
        {
            BrazeCfdSmokePreset.Green => new Color(0.04f, 0.92f, 0.28f, 1f),
            BrazeCfdSmokePreset.Red => new Color(1f, 0.08f, 0.025f, 1f),
            BrazeCfdSmokePreset.Custom => customSmokeColor,
            BrazeCfdSmokePreset.Temperature or BrazeCfdSmokePreset.FlowSpeed => Color.white,
            _ => new Color(0.035f, 0.3f, 1f, 1f),
        };

        void ReleaseSmokeLut()
        {
            if (smokePreint == null) return;
            if (Application.isPlaying) Destroy(smokePreint); else DestroyImmediate(smokePreint);
            smokePreint = null;
            smokeLutHash = 0;
        }

        void ResetTemperatureRange()
        {
            temperatureRangeInitialized = false;
            temperatureRangeFrame = int.MinValue;
            temperatureRangeStateHash = 0;
        }

        void RefreshTemperatureRange()
        {
            int frame = Time.frameCount;
            int stateHash = ComputeTemperatureRangeStateHash();
            if (temperatureRangeFrame == frame && temperatureRangeStateHash == stateHash) return;
            temperatureRangeFrame = frame;
            temperatureRangeStateHash = stateHash;
            if (domain == null)
            {
                displayedTemperatureMinimumC = 0f;
                displayedTemperatureMaximumC = 1f;
                temperatureRangeInitialized = false;
                return;
            }

            float fixedMinimum = domain.HeatMap != null
                ? domain.HeatMap.minimumTemperatureC : domain.ambientTemperatureC;
            float fixedMaximum = domain.HeatMap != null
                ? domain.HeatMap.maximumTemperatureC
                : domain.ambientTemperatureC + domain.temperatureRangeC * 0.5f;
            if (!TemperatureAutoRangeEnabled)
            {
                displayedTemperatureMinimumC = fixedMinimum;
                displayedTemperatureMaximumC = Mathf.Max(fixedMinimum + 0.01f, fixedMaximum);
                temperatureRangeInitialized = false;
                return;
            }

            displayedTemperatureMinimumC = domain.ambientTemperatureC;
            float minimumMaximum = domain.ambientTemperatureC + minimumAutomaticTemperatureSpanC;
            if (!domain.TemperatureMeasurementValid)
            {
                // A real GPU result arrives asynchronously. Use the existing fixed range during that brief wait,
                // but do not smooth from it: the first measured sample should take authority immediately.
                displayedTemperatureMaximumC = Mathf.Max(minimumMaximum, fixedMaximum);
                temperatureRangeInitialized = false;
                return;
            }

            float target = Mathf.Max(minimumMaximum, domain.MeasuredPeakTemperatureC);
            if (!temperatureRangeInitialized || !Application.isPlaying)
            {
                displayedTemperatureMaximumC = target;
                temperatureRangeInitialized = true;
                return;
            }

            float response = target > displayedTemperatureMaximumC
                ? temperatureRangeResponseSeconds * 0.35f
                : temperatureRangeResponseSeconds * 2f;
            float dt = Mathf.Max(Time.unscaledDeltaTime, 1f / 240f);
            float blend = 1f - Mathf.Exp(-dt / Mathf.Max(response, 0.01f));
            displayedTemperatureMaximumC = Mathf.Lerp(displayedTemperatureMaximumC, target, blend);
            displayedTemperatureMaximumC = Mathf.Max(displayedTemperatureMaximumC, minimumMaximum);
        }

        int ComputeTemperatureRangeStateHash()
        {
            unchecked
            {
                int h = domain != null ? domain.BrazeInstanceId() : 0;
                h = h * 31 + automaticTemperatureRange.GetHashCode();
                h = h * 31 + TemperatureAutoRangeEnabled.GetHashCode();
                h = h * 31 + minimumAutomaticTemperatureSpanC.GetHashCode();
                if (domain == null) return h;
                h = h * 31 + domain.ambientTemperatureC.GetHashCode();
                h = h * 31 + domain.TemperatureMeasurementValid.GetHashCode();
                h = h * 31 + domain.MeasuredPeakTemperatureC.GetHashCode();
                BrazeCfdHeatMap map = domain.HeatMap;
                h = h * 31 + (map != null ? map.minimumTemperatureC.GetHashCode() : 0);
                h = h * 31 + (map != null ? map.maximumTemperatureC.GetHashCode() : 0);
                return h;
            }
        }

        int ComputeConfigurationHash()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (domain != null ? domain.BrazeInstanceId() : 0);
                h = h * 31 + automaticReferenceSpeed.GetHashCode();
                h = h * 31 + referenceSpeed.GetHashCode();
                h = h * 31 + ResolvedReferenceSpeed.GetHashCode();
                h = h * 31 + showAnalysisSlice.GetHashCode();
                h = h * 31 + (int)preview;
                h = h * 31 + (int)slicePlane;
                h = h * 31 + (int)sliceQuantity;
                h = h * 31 + (int)sliceColorMap;
                h = h * 31 + sliceOffset.GetHashCode();
                h = h * 31 + automaticSliceRange.GetHashCode();
                h = h * 31 + sliceMinimum.GetHashCode();
                h = h * 31 + sliceMaximum.GetHashCode();
                h = h * 31 + vectorColumns;
                h = h * 31 + sliceOpacity.GetHashCode();
                h = h * 31 + showSmoke.GetHashCode();
                h = h * 31 + (int)smokePreset;
                h = h * 31 + customSmokeColor.GetHashCode();
                h = h * 31 + smokeOpacity.GetHashCode();
                h = h * 31 + smokeBrightness.GetHashCode();
                h = h * 31 + smokeColorContrast.GetHashCode();
                h = h * 31 + smokeFlowEmphasis.GetHashCode();
                h = h * 31 + automaticDyePersistence.GetHashCode();
                h = h * 31 + smokePersistenceSeconds.GetHashCode();
                h = h * 31 + automaticTemperatureRange.GetHashCode();
                h = h * 31 + minimumAutomaticTemperatureSpanC.GetHashCode();
                h = h * 31 + temperatureRangeResponseSeconds.GetHashCode();
                h = h * 31 + showGizmo.GetHashCode();
                h = h * 31 + DyeSourceCount;
                BrazeCfdWindTunnel conflict = FindWindTunnel();
                h = h * 31 + (conflict != null ? conflict.BrazeInstanceId() : 0);
                if (domain != null && domain.Container != null)
                    h = h * 31 + domain.Container.WorldSize().GetHashCode();
                return h;
            }
        }

        void ResolveDomain()
        {
            if (domain == null) domain = GetComponentInParent<BrazeCfdDomain>();
        }

        BrazeCfdWindTunnel FindWindTunnel()
        {
            for (int i = 0; i < BrazeCfdWindTunnel.Active.Count; i++)
            {
                BrazeCfdWindTunnel tunnel = BrazeCfdWindTunnel.Active[i];
                if (tunnel != null && tunnel.isActiveAndEnabled && tunnel.domain == domain) return tunnel;
            }
            return null;
        }

        void DestroyManagedSlice()
        {
            if (analysisSlice == null) return;
            if (analysisSlice.managedByVisualizer != this) { analysisSlice = null; return; }
            analysisSlice.enabled = false;
            BrazeCfdAnalysisSlice owned = analysisSlice;
            analysisSlice = null;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.EditorApplication.delayCall += () =>
                {
                    if (owned != null) DestroyImmediate(owned);
                };
                return;
            }
#endif
            Destroy(owned);
        }
    }
}
