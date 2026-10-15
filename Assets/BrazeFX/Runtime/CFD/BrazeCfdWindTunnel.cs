using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX.CFD
{
    public enum BrazeCfdFlowRegime
    {
        Laminar = 0,
        Turbulent = 1,
    }

    public enum BrazeCfdWindPreview
    {
        None = 0,
        ScalarSlice = 1,
        VectorField = 2,
        ScalarAndVectors = 3,
    }

    public enum BrazeCfdSmokePreset
    {
        Off = 0,
        Blue = 1,
        Green = 2,
        Red = 3,
        Custom = 4,
        Temperature = 5,
        FlowSpeed = 6,
    }

    /// <summary>Wind-tunnel inlet cross-section. Full Face reproduces the classic tunnel; the shaped options
    /// blow a sized, positionable jet from the inlet face — Circle/Ellipse for round ducts and fans, Ring for a
    /// fan annulus with a hub, Slot for a rounded-cap vent. Values are serialized — append only.</summary>
    public enum BrazeCfdInletShape
    {
        FullFace = 0,
        Rectangle = 1,
        Ellipse = 2,
        Ring = 3,
        Circle = 4,
        Slot = 5,
    }

    /// <summary>
    /// Simple-authoring facade for a complete wind-tunnel condition. It owns hidden inlet/outlet and visualization
    /// adapters while the existing advanced components remain the runtime implementation and migration surface.
    /// </summary>
    [AddComponentMenu("BrazeFX/CFD/Wind Tunnel", 1)]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-90)]
    public sealed class BrazeCfdWindTunnel : MonoBehaviour, IBrazeCfdVisualization
    {
        public static readonly List<BrazeCfdWindTunnel> Active = new();

        [Header("Study")]
        [Tooltip("Domain containing this wind-tunnel study. When empty, the nearest parent domain is used.")]
        public BrazeCfdDomain domain;
        [Min(0f), Tooltip("Air speed in world units per second. Wind follows this Transform's local +Z direction.")]
        public float airSpeed = 5f;
        [Tooltip("Inlet cross-section. Full Face is the classic tunnel; shaped inlets blow a sized jet — " +
                 "Circle/Ellipse for round ducts/fans, Ring for a fan annulus with a hub, Slot for a rounded-cap " +
                 "vent. The outlet always spans its full face so outflow is never blocked.")]
        public BrazeCfdInletShape inletShape = BrazeCfdInletShape.FullFace;
        [Tooltip("Rectangle, Ellipse, and Slot inlets: width and height on the inlet face, in world units.")]
        public Vector2 inletSize = new(2f, 2f);
        [Min(0.005f), Tooltip("Circle and Ring inlets: outer radius in world units.")]
        public float inletRadius = 1f;
        [Tooltip("Shaped inlets: centre offset across the inlet face, in world units.")]
        public Vector2 inletOffset;
        [Range(0f, 0.95f), Tooltip("Ring inlet: hub cutout radius as a fraction of the outer radius.")]
        public float inletHubFraction = 0.35f;
        [Tooltip("Incoming air temperature in degrees Celsius.")]
        public float airTemperatureC = 20f;
        [Tooltip("Laminar preserves a clean inlet. Turbulent adds divergence-free inlet fluctuations and retains " +
                 "resolved wake vorticity without enabling Fire's artistic noise forces.")]
        public BrazeCfdFlowRegime flowRegime = BrazeCfdFlowRegime.Turbulent;
        [Range(0f, 30f), Tooltip("RMS inlet velocity fluctuation as a percentage of mean air speed.")]
        public float turbulenceIntensityPercent = 7.5f;
        [Range(0.03f, 0.5f), Tooltip("Dominant inlet eddy size as a fraction of the shorter tunnel cross-section.")]
        public float turbulenceEddyScale = 0.14f;
        [Range(0f, 1f), Tooltip("Counteracts numerical diffusion of vortices already present in the solved flow. " +
                                "This does not add procedural Fire turbulence inside the domain.")]
        public float vorticityRetention = 0.45f;
        [Tooltip("Dimensionless outlet gauge pressure. Zero is the stable ambient/reference outlet.")]
        public float outletGaugePressure;
        [Tooltip("Prevent air from returning through the pressure outlet.")]
        public bool preventOutletBackflow = true;
        [Tooltip("Treat the domain as a closed duct: every face without an inlet/outlet patch becomes a wall. " +
                 "The Fire-inherited open ceiling is a zero-pressure vent that leaks mass upward and skews flow " +
                 "over the test body. Disable for an open-test-section study.")]
        public bool sealTunnelWalls = true;

        [Header("Visualization")]
        [Tooltip("Show the movable GPU analysis plane. The Display dropdown below only chooses its contents.")]
        public bool showAnalysisSlice = true;
        public BrazeCfdWindPreview preview = BrazeCfdWindPreview.ScalarAndVectors;
        public BrazeCfdSlicePlane slicePlane = BrazeCfdSlicePlane.Vertical;
        public BrazeCfdSliceQuantity sliceQuantity = BrazeCfdSliceQuantity.VelocityMagnitude;
        public BrazeCfdSliceColorMap sliceColorMap = BrazeCfdSliceColorMap.CfdRainbow;
        [Range(-1f, 1f), Tooltip("Move the analysis plane across the CFD domain.")]
        public float sliceOffset;
        [Tooltip("Choose a useful range from the tunnel conditions. Disable for explicit comparison ranges.")]
        public bool automaticSliceRange = true;
        public float sliceMinimum;
        public float sliceMaximum = 5f;
        [Range(4, 80)] public int vectorColumns = 18;
        [Range(0f, 1f)] public float sliceOpacity = 0.92f;
        [Tooltip("Passive CFD dye rendered by BrazeFX's GPU volume renderer. This is visualization only and does " +
                 "not add mass, heat, buoyancy, or another simulation.")]
        public bool showSmoke = true;
        public BrazeCfdSmokePreset smokePreset = BrazeCfdSmokePreset.FlowSpeed;
        [Tooltip("Turbulent Volume is the presentation default. Tracer Rake is a diagnostic for following separate " +
                 "inlet paths; Sheet and Full provide continuous laminar injection shapes. Body Contact keeps the " +
                 "tunnel clear and releases dye only where moving air meets the test body, so the boundary layer, " +
                 "separation, and wake visualize themselves.")]
        public BrazeCfdSmokePattern smokePattern = BrazeCfdSmokePattern.TurbulentVolume;
        [ColorUsage(false, true)] public Color customSmokeColor = new(0.08f, 0.42f, 1f, 1f);
        [Range(0.01f, 1f), Tooltip("Concentration of passive dye injected at the inlet.")]
        public float smokeDensity = 0.12f;
        [Range(0f, 1f)] public float smokeOpacity = 0.7f;
        [Range(0.5f, 3f), Tooltip("Lighting-independent visibility multiplier for CFD smoke.")]
        public float smokeBrightness = 1.65f;
        [Range(0.5f, 6f), Tooltip("Separates neighbouring false-colour bands. Higher values make velocity or " +
                                  "temperature differences read more crisply.")]
        public float smokeColorContrast = 1.5f;
        [Range(0f, 1f), Tooltip("Thins dye where flow sits at nominal freestream speed so wake and acceleration " +
                                "structure keeps its full colour instead of averaging into the surrounding green. " +
                                "0 keeps the classic uniform dye; 1 shows only the disturbed flow.")]
        public float smokeFlowEmphasis = 0.35f;
        [Tooltip("Match dye lifetime to the tunnel's air-exchange time (~1.5 inlet→outlet transits). This is how " +
                 "a real smoke tunnel stays clear: dye survives its pass through the test section and fades " +
                 "before recirculation can accumulate it into a filled box. Disable to author the lifetime.")]
        public bool automaticDyePersistence = true;
        [Min(0.1f), Tooltip("Approximate e-fold time for passive dye to disappear.")]
        public float smokePersistenceSeconds = 24f;
        [Tooltip("Draw the analysis plane and wind direction in the Scene view.")]
        public bool showGizmo = true;

        [SerializeField, HideInInspector] BrazeCfdBoundaryPatch inlet;
        [SerializeField, HideInInspector] BrazeCfdBoundaryPatch outlet;
        [SerializeField, HideInInspector] BrazeCfdAnalysisSlice analysisSlice;
        [SerializeField, HideInInspector] int visualizationUiVersion;

        int configurationHash;
        bool synchronizing;
#if UNITY_EDITOR
        bool editorSyncQueued;
#endif
        Texture2D smokePreint;
        int smokeLutHash;
        string status = "Assign a CFD Domain.";

        public string Status => status;
        BrazeCfdDomain IBrazeCfdVisualization.Domain => domain;
        float IBrazeCfdVisualization.SmokeColorContrast => smokeColorContrast;
        float IBrazeCfdVisualization.SmokeFlowEmphasis => smokeFlowEmphasis;
        float IBrazeCfdVisualization.SmokeOpacity => smokeOpacity;
        float IBrazeCfdVisualization.SmokeBrightness => smokeBrightness;
        public Vector3 WindDirectionWorld => transform.forward.sqrMagnitude > 1e-6f
            ? transform.forward.normalized : Vector3.forward;
        public BrazeCfdDomainFace InletFace => ResolveFaces(domain, WindDirectionWorld, out var inletFace, out _)
            ? inletFace : BrazeCfdDomainFace.NegativeZ;
        public BrazeCfdDomainFace OutletFace => ResolveFaces(domain, WindDirectionWorld, out _, out var outletFace)
            ? outletFace : BrazeCfdDomainFace.PositiveZ;
        public BrazeCfdBoundaryPatch ManagedInlet => inlet;
        public BrazeCfdBoundaryPatch ManagedOutlet => outlet;
        public BrazeCfdAnalysisSlice ManagedAnalysisSlice => analysisSlice;
        public bool SmokeEnabled => showSmoke && smokePreset != BrazeCfdSmokePreset.Off && smokeDensity > 0.0001f;
        public Texture2D SmokePreint
        {
            get
            {
                if (!SmokeEnabled) return null;
                EnsureSmokeLut();
                return smokePreint;
            }
        }
        public bool CoolingRequested => domain != null && airTemperatureC < domain.ambientTemperatureC - 0.01f;
        public bool TurbulenceEnabled => flowRegime == BrazeCfdFlowRegime.Turbulent &&
                                         turbulenceIntensityPercent > 0.001f;
        public float TurbulenceIntensity => TurbulenceEnabled
            ? Mathf.Clamp(turbulenceIntensityPercent * 0.01f, 0f, 0.3f) : 0f;
        public float TurbulenceLengthWorld
        {
            get
            {
                if (domain == null || domain.Container == null) return Mathf.Max(0.01f, turbulenceEddyScale);
                // Eddy size scales with the EFFECTIVE inlet cross-section, so a small shaped duct gets
                // proportionally finer synthetic turbulence than the full tunnel face.
                Vector2 crossSection = inletShape switch
                {
                    BrazeCfdInletShape.FullFace => FaceSize(domain.Container.WorldSize(), InletFace),
                    BrazeCfdInletShape.Circle or BrazeCfdInletShape.Ring => Vector2.one * (inletRadius * 2f),
                    _ => inletSize,
                };
                return Mathf.Max(0.01f, Mathf.Min(Mathf.Abs(crossSection.x), Mathf.Abs(crossSection.y)) *
                                       Mathf.Clamp(turbulenceEddyScale, 0.03f, 0.5f));
            }
        }
        public bool SmokeTemperatureColor => SmokeEnabled && smokePreset == BrazeCfdSmokePreset.Temperature;
        public bool SmokeFlowSpeedColor => SmokeEnabled && smokePreset == BrazeCfdSmokePreset.FlowSpeed;
        bool IBrazeCfdVisualization.TemperatureAutoRangeEnabled => false;
        public bool SmokeBodyContact => SmokeEnabled && smokePattern == BrazeCfdSmokePattern.BodyContact;
        /// <summary>Dye e-fold lifetime the solver actually uses. Automatic mode derives it from the tunnel's
        /// transit time (length along the wind axis / air speed), clamped to a sane authoring range.</summary>
        public float EffectiveSmokePersistenceSeconds
        {
            get
            {
                if (!automaticDyePersistence) return smokePersistenceSeconds;
                if (domain == null || domain.Container == null || airSpeed <= 0.01f)
                    return Mathf.Min(smokePersistenceSeconds, 6f);
                Vector3 size = domain.Container.WorldSize();
                float lengthAlongWind = InletFace switch
                {
                    BrazeCfdDomainFace.NegativeX or BrazeCfdDomainFace.PositiveX => Mathf.Abs(size.x),
                    BrazeCfdDomainFace.NegativeY or BrazeCfdDomainFace.PositiveY => Mathf.Abs(size.y),
                    _ => Mathf.Abs(size.z),
                };
                return Mathf.Clamp(lengthAlongWind / airSpeed * 1.5f, 0.5f, 30f);
            }
        }

        public float SmokeHeatMinimum => 0f;
        public float SmokeHeatInverseRange => domain != null
            ? 1f / Mathf.Max((Mathf.Max(airTemperatureC, domain.ambientTemperatureC + domain.temperatureRangeC * 0.5f) -
                                  domain.ambientTemperatureC) * domain.HeatUnitsPerDegree, 0.001f)
            : 1f;

        void Reset()
        {
            MigrateVisualizationUi();
            ResolveDomain();
            if (domain != null) airTemperatureC = domain.ambientTemperatureC;
            SyncManagedComponents(true);
        }

        void OnEnable()
        {
            MigrateVisualizationUi();
            ResolveDomain();
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
            DestroyManaged(ref inlet);
            DestroyManaged(ref outlet);
            DestroyManaged(ref analysisSlice);
            ReleaseSmokeLut();
        }

        void OnValidate()
        {
            MigrateVisualizationUi();
            airSpeed = Mathf.Max(0f, airSpeed);
            inletSize = new Vector2(Mathf.Max(0.01f, inletSize.x), Mathf.Max(0.01f, inletSize.y));
            inletRadius = Mathf.Max(0.005f, inletRadius);
            inletHubFraction = Mathf.Clamp(inletHubFraction, 0f, 0.95f);
            turbulenceIntensityPercent = Mathf.Clamp(turbulenceIntensityPercent, 0f, 30f);
            turbulenceEddyScale = Mathf.Clamp(turbulenceEddyScale, 0.03f, 0.5f);
            vorticityRetention = Mathf.Clamp01(vorticityRetention);
            sliceOffset = Mathf.Clamp(sliceOffset, -1f, 1f);
            sliceMaximum = Mathf.Max(sliceMinimum + 0.0001f, sliceMaximum);
            vectorColumns = Mathf.Clamp(vectorColumns, 4, 80);
            sliceOpacity = Mathf.Clamp01(sliceOpacity);
            smokeDensity = Mathf.Clamp01(smokeDensity);
            smokeOpacity = Mathf.Clamp01(smokeOpacity);
            smokeBrightness = Mathf.Clamp(smokeBrightness, 0.5f, 3f);
            smokeColorContrast = Mathf.Clamp(smokeColorContrast, 0.5f, 6f);
            smokeFlowEmphasis = Mathf.Clamp01(smokeFlowEmphasis);
            smokePersistenceSeconds = Mathf.Max(0.1f, smokePersistenceSeconds);
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
            int hash = ComputeConfigurationHash();
            if (hash != configurationHash) SyncManagedComponents(true);
            ConstrainToInletFace();
        }

        // With a shaped inlet the Transform is the jet's on-wall handle. A handle that silently ignores its
        // normal axis reads as a bug, so pin the position onto the inlet face plane and keep the in-face
        // components inside the face. Full Face inlets don't use the position at all — never touch those
        // transforms (existing scenes park the facade anywhere).
        void ConstrainToInletFace()
        {
            if (inletShape == BrazeCfdInletShape.FullFace) return;
            if (domain == null || domain.Container == null) return;
            if (!ResolveFaces(domain, WindDirectionWorld, out BrazeCfdDomainFace face, out _)) return;
            Transform containerTransform = domain.Container.transform;
            Vector3 worldSize = domain.Container.WorldSize();
            Vector3 half = new(Mathf.Abs(worldSize.x) * 0.5f, Mathf.Abs(worldSize.y) * 0.5f,
                               Mathf.Abs(worldSize.z) * 0.5f);
            Vector3 local = Quaternion.Inverse(containerTransform.rotation) *
                            (transform.position - containerTransform.position);
            Vector3 outward = BrazeCfdBoundaryPatch.LocalOutwardNormal(face);
            Vector3 constrained = new(
                Mathf.Abs(outward.x) > 0.5f ? outward.x * half.x : Mathf.Clamp(local.x, -half.x, half.x),
                Mathf.Abs(outward.y) > 0.5f ? outward.y * half.y : Mathf.Clamp(local.y, -half.y, half.y),
                Mathf.Abs(outward.z) > 0.5f ? outward.z * half.z : Mathf.Clamp(local.z, -half.z, half.z));
            if ((constrained - local).sqrMagnitude < 1e-8f) return;   // epsilon-guarded: no per-frame dirtying
            transform.position = containerTransform.position + containerTransform.rotation * constrained;
        }

        [System.NonSerialized] bool exclusivePreviewValid;
        [System.NonSerialized] bool previousShowAnalysisSlice;
        [System.NonSerialized] bool previousShowSmoke;

        // The analysis slice and the dye volume are alternative READINGS of the same study; mixed they occlude
        // each other and read as a bug. The most recently enabled view wins; with no history (both flipped by
        // script in one frame) the dye volume wins as the presentation default.
        void EnforceExclusivePreview()
        {
            if (showAnalysisSlice && showSmoke)
            {
                if (exclusivePreviewValid && !previousShowAnalysisSlice) showSmoke = false;
                else showAnalysisSlice = false;
            }
            previousShowAnalysisSlice = showAnalysisSlice;
            previousShowSmoke = showSmoke;
            exclusivePreviewValid = true;
        }

        internal void SyncManagedComponents(bool createMissing)
        {
            if (synchronizing) return;
            synchronizing = true;
            try
            {
                EnforceExclusivePreview();
                ResolveDomain();
                if (domain == null || domain.Container == null)
                {
                    SetManagedEnabled(false);
                    status = "Assign a CFD Domain.";
                    configurationHash = ComputeConfigurationHash();
                    return;
                }

                if (createMissing)
                {
                    inlet = EnsureBoundary(inlet, BrazeCfdBoundaryPatchMode.VelocityInlet);
                    outlet = EnsureBoundary(outlet, BrazeCfdBoundaryPatchMode.PressureOutlet);
                    if (WantsAnalysisSlice) analysisSlice = EnsureAnalysisSlice(analysisSlice);
                }

                ConfigureBoundaries();
                ConfigureVisualizations();
                if (SmokeEnabled) EnsureSmokeLut();
                else ReleaseSmokeLut();
                configurationHash = ComputeConfigurationHash();
                status = CoolingRequested
                    ? "Airflow is active. Cooling below ambient is staged for the signed-temperature solver pass; " +
                      "the current inlet uses ambient temperature."
                    : $"Wind tunnel active: {airSpeed:0.##} units/s from {InletFace} to {OutletFace}. " +
                      (TurbulenceEnabled ? $"Turbulent inlet ({turbulenceIntensityPercent:0.#}%) is active. " : "") +
                      (WantsAnalysisSlice ? $"{sliceQuantity} analysis slice is active. " : "") +
                      (SmokeEnabled ? $"{smokePreset} passive dye is using the GPU volume renderer." : "");
            }
            finally { synchronizing = false; }
        }

        bool WantsAnalysisSlice => showAnalysisSlice && preview != BrazeCfdWindPreview.None;
        bool WantsScalar => preview == BrazeCfdWindPreview.ScalarSlice ||
                            preview == BrazeCfdWindPreview.ScalarAndVectors;
        bool WantsVectors => preview == BrazeCfdWindPreview.VectorField ||
                             preview == BrazeCfdWindPreview.ScalarAndVectors;

        BrazeCfdBoundaryPatch EnsureBoundary(BrazeCfdBoundaryPatch current, BrazeCfdBoundaryPatchMode mode)
        {
            if (current == null || current.gameObject != gameObject || current.managedByWindTunnel != this)
            {
                var candidates = GetComponents<BrazeCfdBoundaryPatch>();
                current = null;
                for (int i = 0; i < candidates.Length; i++)
                    if (candidates[i] != null && candidates[i].managedByWindTunnel == this && candidates[i].mode == mode)
                    { current = candidates[i]; break; }
                if (current == null) current = gameObject.AddComponent<BrazeCfdBoundaryPatch>();
            }
            current.managedByWindTunnel = this;
            current.hideFlags |= HideFlags.HideInInspector;
            current.mode = mode;
            return current;
        }

        BrazeCfdAnalysisSlice EnsureAnalysisSlice(BrazeCfdAnalysisSlice current)
        {
            if (current == null || current.gameObject != gameObject || current.managedByWindTunnel != this)
            {
                var candidates = GetComponents<BrazeCfdAnalysisSlice>();
                current = null;
                for (int i = 0; i < candidates.Length; i++)
                    if (candidates[i] != null && candidates[i].managedByWindTunnel == this)
                    { current = candidates[i]; break; }
                if (current == null) current = gameObject.AddComponent<BrazeCfdAnalysisSlice>();
            }
            current.managedByWindTunnel = this;
            current.hideFlags |= HideFlags.HideInInspector;
            return current;
        }

        void ConfigureBoundaries()
        {
            if (!ResolveFaces(domain, WindDirectionWorld, out var inletFace, out var outletFace)) return;
            Vector3 localWind = Quaternion.Inverse(domain.Container.transform.rotation) * WindDirectionWorld;
            Vector3 localVelocity = localWind.normalized * airSpeed;
            ConfigurePatch(inlet, inletFace, BrazeCfdBoundaryPatchMode.VelocityInlet, localVelocity);
            ConfigurePatch(outlet, outletFace, BrazeCfdBoundaryPatchMode.PressureOutlet, localVelocity);
        }

        void ConfigurePatch(BrazeCfdBoundaryPatch patch, BrazeCfdDomainFace face,
                            BrazeCfdBoundaryPatchMode mode, Vector3 localVelocity)
        {
            if (patch == null) return;
            patch.domain = domain;
            patch.mode = mode;
            patch.face = face;
            patch.size = FaceSize(domain.Container.WorldSize(), face);
            patch.shape = BrazeCfdPatchShape.Rectangle;
            patch.faceOffset = Vector2.zero;
            patch.faceRotation = 0f;
            // The facade used to hide its managed patch gizmos entirely; with shaped inlets the outline IS the
            // authoring feedback, so the tunnel's own Show Gizmo toggle drives them (cyan inlet, orange outlet).
            patch.showGizmo = showGizmo;
            patch.managedCenterOnDomain = true;
            patch.enabled = isActiveAndEnabled;
            if (mode == BrazeCfdBoundaryPatchMode.VelocityInlet)
            {
                if (inletShape != BrazeCfdInletShape.FullFace)
                {
                    patch.size = new Vector2(Mathf.Max(0.01f, inletSize.x), Mathf.Max(0.01f, inletSize.y));
                    patch.radius = Mathf.Max(0.005f, inletRadius);
                    patch.shape = inletShape switch
                    {
                        BrazeCfdInletShape.Ellipse => BrazeCfdPatchShape.Ellipse,
                        BrazeCfdInletShape.Ring => BrazeCfdPatchShape.Ring,
                        BrazeCfdInletShape.Circle => BrazeCfdPatchShape.Circle,
                        BrazeCfdInletShape.Slot => BrazeCfdPatchShape.Slot,
                        _ => BrazeCfdPatchShape.Rectangle,
                    };
                    patch.faceOffset = inletOffset;
                    patch.ringInnerFraction = inletHubFraction;
                    // The shaped inlet follows this Transform: its position (projected onto the inlet face,
                    // read per solver tick — the managed patch lives on this GameObject) plus its ROLL about
                    // the wind axis, so moving/rotating the tunnel moves and spins the jet live.
                    patch.managedCenterOnDomain = false;
                    BrazeCfdBoundaryPatch.LocalTangentAxes(face, out Vector3 rollU, out Vector3 rollV);
                    Vector3 localUp = Quaternion.Inverse(domain.Container.transform.rotation) * transform.up;
                    Vector3 outwardAxis = BrazeCfdBoundaryPatch.LocalOutwardNormal(face);
                    Vector3 planarUp = localUp - outwardAxis * Vector3.Dot(localUp, outwardAxis);
                    patch.faceRotation = planarUp.sqrMagnitude > 1e-6f
                        ? Mathf.Atan2(Vector3.Dot(planarUp, rollU), Vector3.Dot(planarUp, rollV)) * Mathf.Rad2Deg
                        : 0f;
                }
                Vector3 outward = BrazeCfdBoundaryPatch.LocalOutwardNormal(face);
                BrazeCfdBoundaryPatch.LocalTangentAxes(face, out Vector3 axisU, out Vector3 axisV);
                patch.normalSpeed = Mathf.Max(0f, Vector3.Dot(localVelocity, -outward));
                patch.tangentialVelocity = new Vector2(Vector3.Dot(localVelocity, axisU),
                                                        Vector3.Dot(localVelocity, axisV));
                patch.setInletTemperature = true;
                patch.inletTemperatureC = CoolingRequested ? domain.ambientTemperatureC : airTemperatureC;
                patch.setInletSmoke = SmokeEnabled;
                patch.inletSmokePattern = smokePattern;
                patch.inletSmokeDensity = smokeDensity;
            }
            else
            {
                patch.gaugePressure = outletGaugePressure;
                patch.preventBackflow = preventOutletBackflow;
            }
        }

        void ConfigureVisualizations()
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
            // The slice plane gizmo belongs to the slice VIEW: never draw it while the dye view owns the preview.
            analysisSlice.showGizmo = showGizmo && WantsAnalysisSlice;
            ResolveSliceRange(out analysisSlice.scalarMinimum, out analysisSlice.scalarMaximum);
            analysisSlice.enabled = isActiveAndEnabled && WantsAnalysisSlice;
        }

        void ResolveSliceRange(out float minimum, out float maximum)
        {
            minimum = sliceMinimum;
            maximum = sliceMaximum;
            if (automaticSliceRange)
            {
                switch (sliceQuantity)
                {
                    case BrazeCfdSliceQuantity.Pressure:
                        minimum = -1f; maximum = 1f;
                        break;
                    case BrazeCfdSliceQuantity.Temperature:
                        minimum = domain.ambientTemperatureC;
                        maximum = Mathf.Max(airTemperatureC, domain.ambientTemperatureC + domain.temperatureRangeC * 0.5f);
                        break;
                    case BrazeCfdSliceQuantity.Vorticity:
                        Vector3Int grid = domain.Container.SolverRes;
                        Vector3 size = domain.Container.WorldSize();
                        float cell = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) /
                                     Mathf.Max(1, Mathf.Max(grid.x, Mathf.Max(grid.y, grid.z)));
                        minimum = 0f; maximum = Mathf.Max(1f, airSpeed / Mathf.Max(cell, 0.001f));
                        break;
                    default:
                        minimum = 0f; maximum = Mathf.Max(airSpeed * 1.25f, 0.1f);
                        break;
                }
            }
            if (sliceQuantity == BrazeCfdSliceQuantity.Temperature)
            {
                minimum = (minimum - domain.ambientTemperatureC) * domain.HeatUnitsPerDegree;
                maximum = (maximum - domain.ambientTemperatureC) * domain.HeatUnitsPerDegree;
            }
            maximum = Mathf.Max(minimum + 0.0001f, maximum);
        }

        void SetManagedEnabled(bool value)
        {
            if (inlet != null && inlet.managedByWindTunnel == this) inlet.enabled = value;
            if (outlet != null && outlet.managedByWindTunnel == this) outlet.enabled = value;
            if (analysisSlice != null && analysisSlice.managedByWindTunnel == this)
                analysisSlice.enabled = value && WantsAnalysisSlice;
        }

        void EnsureSmokeLut()
        {
            Color color = ResolveSmokeColor();
            int hash = color.GetHashCode();
            if (smokePreint != null && smokeLutHash == hash) return;
            if (smokePreint == null) smokePreint = BrazeTransferFunction.CreateLut("BrazeCfdSmokePreint");
            var gradient = new Gradient();
            // CFD dye should read as a bright diagnostic medium, not soot. Keep even dense samples saturated;
            // temperature colouring can replace this hue per raymarch sample without inheriting a black tail.
            // Preserve chroma through the complete density range. The former white blend turned blue dye into the
            // pale cyan fog seen in filled wind tunnels before any scene compositing occurred.
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

        void MigrateVisualizationUi()
        {
            if (visualizationUiVersion >= 4) return;
            // None/Off used to live inside the mode and colour dropdowns. Preserve their serialized meaning once,
            // then keep useful choices behind explicit top-level visibility switches.
            if (visualizationUiVersion < 1)
            {
                showAnalysisSlice = preview != BrazeCfdWindPreview.None;
                showSmoke = smokePreset != BrazeCfdSmokePreset.Off;
                if (preview == BrazeCfdWindPreview.None) preview = BrazeCfdWindPreview.ScalarAndVectors;
                if (smokePreset == BrazeCfdSmokePreset.Off) smokePreset = BrazeCfdSmokePreset.Blue;
            }
            if (visualizationUiVersion < 2)
            {
                // These controls did not exist in v1, so initialize every existing facade explicitly rather than
                // depending on Unity's missing-field deserialization behaviour. Advanced boundary patches remain
                // untouched because their exact inlet condition is expert-authored rather than facade-owned.
                flowRegime = BrazeCfdFlowRegime.Turbulent;
                turbulenceIntensityPercent = 7.5f;
                turbulenceEddyScale = 0.14f;
                vorticityRetention = 0.45f;
                if (smokePattern == BrazeCfdSmokePattern.Rake)
                    smokePattern = BrazeCfdSmokePattern.TurbulentVolume;
            }
            if (visualizationUiVersion < 3)
            {
                // Blue and Temperature were the two facade defaults. A uniform ambient-temperature tunnel cannot
                // produce a useful temperature palette, so move those defaults to the field-readable speed spectrum.
                // Preserve deliberately selected Green, Red, and Custom presentation colours.
                if (smokePreset == BrazeCfdSmokePreset.Blue ||
                    smokePreset == BrazeCfdSmokePreset.Temperature)
                    smokePreset = BrazeCfdSmokePreset.FlowSpeed;
                smokeColorContrast = 1.5f;
            }
            if (visualizationUiVersion < 4)
            {
                // Flow Emphasis did not exist before v4; Unity deserializes the missing field to 0, which keeps
                // the old flat-opacity dye. Move existing facades onto the new default so wakes read immediately.
                smokeFlowEmphasis = 0.35f;
            }
            visualizationUiVersion = 4;
        }

        void ReleaseSmokeLut()
        {
            if (smokePreint == null) return;
            if (Application.isPlaying) Destroy(smokePreint); else DestroyImmediate(smokePreint);
            smokePreint = null;
            smokeLutHash = 0;
        }

        internal static bool ResolveFaces(BrazeCfdDomain targetDomain, Vector3 worldDirection,
                                          out BrazeCfdDomainFace inletFace,
                                          out BrazeCfdDomainFace outletFace)
        {
            inletFace = BrazeCfdDomainFace.NegativeZ;
            outletFace = BrazeCfdDomainFace.PositiveZ;
            if (targetDomain == null || targetDomain.Container == null || worldDirection.sqrMagnitude < 1e-8f)
                return false;
            Vector3 d = Quaternion.Inverse(targetDomain.Container.transform.rotation) * worldDirection.normalized;
            Vector3 a = new(Mathf.Abs(d.x), Mathf.Abs(d.y), Mathf.Abs(d.z));
            if (a.x >= a.y && a.x >= a.z)
            {
                inletFace = d.x >= 0f ? BrazeCfdDomainFace.NegativeX : BrazeCfdDomainFace.PositiveX;
                outletFace = d.x >= 0f ? BrazeCfdDomainFace.PositiveX : BrazeCfdDomainFace.NegativeX;
            }
            else if (a.y >= a.z)
            {
                inletFace = d.y >= 0f ? BrazeCfdDomainFace.NegativeY : BrazeCfdDomainFace.PositiveY;
                outletFace = d.y >= 0f ? BrazeCfdDomainFace.PositiveY : BrazeCfdDomainFace.NegativeY;
            }
            else
            {
                inletFace = d.z >= 0f ? BrazeCfdDomainFace.NegativeZ : BrazeCfdDomainFace.PositiveZ;
                outletFace = d.z >= 0f ? BrazeCfdDomainFace.PositiveZ : BrazeCfdDomainFace.NegativeZ;
            }
            return true;
        }

        static Vector2 FaceSize(Vector3 size, BrazeCfdDomainFace face) => face switch
        {
            BrazeCfdDomainFace.NegativeX or BrazeCfdDomainFace.PositiveX =>
                new Vector2(Mathf.Abs(size.y), Mathf.Abs(size.z)),
            BrazeCfdDomainFace.NegativeY or BrazeCfdDomainFace.PositiveY =>
                new Vector2(Mathf.Abs(size.x), Mathf.Abs(size.z)),
            _ => new Vector2(Mathf.Abs(size.x), Mathf.Abs(size.y)),
        };

        int ComputeConfigurationHash()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (domain != null ? domain.BrazeInstanceId() : 0);
                h = h * 31 + transform.rotation.GetHashCode();
                h = h * 31 + airSpeed.GetHashCode();
                h = h * 31 + (int)inletShape;
                h = h * 31 + inletSize.GetHashCode();
                h = h * 31 + inletRadius.GetHashCode();
                h = h * 31 + inletOffset.GetHashCode();
                h = h * 31 + inletHubFraction.GetHashCode();
                h = h * 31 + airTemperatureC.GetHashCode();
                h = h * 31 + (int)flowRegime;
                h = h * 31 + turbulenceIntensityPercent.GetHashCode();
                h = h * 31 + turbulenceEddyScale.GetHashCode();
                h = h * 31 + vorticityRetention.GetHashCode();
                h = h * 31 + outletGaugePressure.GetHashCode();
                h = h * 31 + preventOutletBackflow.GetHashCode();
                h = h * 31 + sealTunnelWalls.GetHashCode();
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
                h = h * 31 + (int)smokePattern;
                h = h * 31 + customSmokeColor.GetHashCode();
                h = h * 31 + smokeDensity.GetHashCode();
                h = h * 31 + smokeOpacity.GetHashCode();
                h = h * 31 + smokeBrightness.GetHashCode();
                h = h * 31 + smokeColorContrast.GetHashCode();
                h = h * 31 + smokeFlowEmphasis.GetHashCode();
                h = h * 31 + smokePersistenceSeconds.GetHashCode();
                h = h * 31 + automaticDyePersistence.GetHashCode();
                h = h * 31 + showGizmo.GetHashCode();
                if (domain != null && domain.Container != null)
                {
                    h = h * 31 + domain.Container.transform.position.GetHashCode();
                    h = h * 31 + domain.Container.transform.rotation.GetHashCode();
                    h = h * 31 + domain.Container.WorldSize().GetHashCode();
                }
                return h;
            }
        }

        void ResolveDomain()
        {
            if (domain == null) domain = GetComponentInParent<BrazeCfdDomain>();
        }

        void DestroyManaged(ref BrazeCfdBoundaryPatch component)
        {
            if (component == null) return;
            if (component.managedByWindTunnel == this) DestroyOwned(component);
            component = null;
        }

        void DestroyManaged(ref BrazeCfdAnalysisSlice component)
        {
            if (component == null) return;
            if (component.managedByWindTunnel == this) DestroyOwned(component);
            component = null;
        }

        static void DestroyOwned(Behaviour component)
        {
            component.enabled = false;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                // OnDestroy also runs while the whole GameObject is being destroyed. Deferring lets Unity finish
                // that operation first; a helper that survives (component-only removal) is then cleaned safely.
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
            if (!showGizmo || domain == null || domain.Container == null) return;
            Color oldColor = Gizmos.color;
            Vector3 center = domain.Container.transform.position;
            float length = Mathf.Max(0.25f, domain.Container.WorldSize().magnitude * 0.12f);
            Vector3 tip = center + WindDirectionWorld * length;
            Vector3 side = Vector3.Cross(WindDirectionWorld, domain.Container.transform.up).normalized * length * 0.12f;
            Gizmos.color = new Color(0.08f, 0.82f, 1f, 0.9f);
            Gizmos.DrawLine(center, tip);
            Gizmos.DrawLine(tip, tip - WindDirectionWorld * length * 0.2f + side);
            Gizmos.DrawLine(tip, tip - WindDirectionWorld * length * 0.2f - side);
            Gizmos.color = oldColor;
        }
    }
}
