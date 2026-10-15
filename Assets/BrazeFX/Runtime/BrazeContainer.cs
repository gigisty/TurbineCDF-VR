using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace BrazeFX
{
    static class BrazeRuntimeRegistry
    {
        public static void AddUnique<T>(List<T> list, T item) where T : Object
        {
            Remove(list, item);
            list.Add(item);
        }

        public static void Remove<T>(List<T> list, T item) where T : Object
        {
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i] == null || list[i] == item)
                    list.RemoveAt(i);
        }

        // OnDisable normally unregisters components, but domain-reload-disabled sessions, failed imports and
        // destruction during render callbacks can leave Unity fake-null wrappers behind. Consumers that iterate a
        // static registry should prune before capturing entries for deferred GPU/render work.
        public static void PruneNulls<T>(List<T> list) where T : Object
        {
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i] == null)
                    list.RemoveAt(i);
        }
    }

    /// <summary>
    /// The simulation domain + orchestrator. Owns the GPU solver and the live slice renderer; its transform defines
    /// the grid bounds (a unit cube in local space, scaled by <see cref="size"/>).
    ///
    /// Holds GRID + DETAIL + DYNAMICS (global fluid forces). Legacy render-look fields remain serialized for
    /// cache/baked-volume compatibility, but live container drawing is first-class on the container. Per-source
    /// emitters/forces are
    /// <see cref="BrazePyroEmitter"/>/<see cref="BrazePyroForce"/> components on CHILD objects, gathered each frame.
    /// Play-mode only (no ExecuteAlways) for now.
    /// </summary>
    [AddComponentMenu("BrazeFX/Fire/Braze Container", 0)]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10010)]   // sample animated CFD bodies after BrazeMotion / animation LateUpdate
    public sealed partial class BrazeContainer : MonoBehaviour
    {
        [Tooltip("Voxels along the LONGEST axis (base sim); other axes match the world voxel pitch. Live-rebuilds on " +
                 "change. 64–192 is the realtime sweet spot; 256–512 is production/offline territory — cost is VRAM " +
                 "(grids are dense-allocated, ~res³), not compute (sparse). Watch the VRAM readout in the Stats HUD. " +
                 "Capped at 512 (≈16 GB at 512³ → a 24 GB GPU).")]
        public int resolution = 128;

        public enum QualityMode { Mobile, Realtime, Production, Custom }
        [Tooltip("Simulation/workflow preset. Selecting it in the Container inspector applies visible grid, solver, " +
                 "detail-generation, and shadow defaults, then enforces simulation and memory ceilings. Final " +
                 "camera-space volume quality is owned exclusively by Braze Render Feature.")]
        public QualityMode qualityMode = QualityMode.Realtime;
        [SerializeField, HideInInspector] bool qualityModeMigrated;
        [Tooltip("Resolution at which the authored voxel-space motion controls were tuned. Existing scenes capture their " +
                 "current resolution automatically. Changing Grid Resolution then preserves the same world-space motion.")]
        [Min(8)] public int physicsReferenceResolution = 128;
        [Tooltip("Automatically add advection substeps as resolution rises so a finer grid does not trace across more " +
                 "cells per step and become blurrier. Device modes cap the added work.")]
        public bool autoSubsteps = true;
        [Tooltip("Custom mode: maximum advection substeps the realtime budget may schedule.")]
        [Range(1, 32)] public int customMaxSubsteps = 4;
        [Tooltip("Custom/Production mode CFL target: maximum source-driven travel in voxels per advection substep.")]
        [Range(0.5f, 8f)] public float cflTargetVoxels = 2f;
        [Tooltip("Custom mode: enforce the absolute CFL target in addition to preserving the reference-resolution look.")]
        public bool customStrictCfl = false;
        [Tooltip("Legacy serialized compatibility value. Final live/cache ray sampling is owned by Braze Render Feature.")]
        [Range(8, 512)] public int customRenderStepBudget = 256;
        [Tooltip("Custom mode: hard estimated GPU allocation ceiling for this container.")]
        [Range(64, 24576)] public int customMemoryBudgetMB = 2048;
        [Tooltip("Grid box (world units). Final box = size × Transform scale, and the two are fully " +
                 "interchangeable — both resize the box at the same voxel resolution, and emitters/forces " +
                 "keep their literal world radius either way, so the effect looks identical however you size it.")]
        public Vector3 size = Vector3.one;
        [Tooltip("Treat the bottom face as a solid wall during pressure projection. On by default — fire usually sits on the ground.")]
        public bool closedBottom = true;
        [Tooltip("Treat the top face as a solid wall during pressure projection. Off leaves it as an open outflow.")]
        public bool closedTop = false;
        [Tooltip("Treat the X/Z side faces as solid walls during pressure projection. Off leaves them as open outflow.")]
        public bool closedSides = false;

        [Tooltip("Draw the per-voxel cell grid on the container's -Z wall, so you can read the real world " +
                 "cell size while tuning for physical accuracy.")]
        public bool showCellGrid = true;
        [Tooltip("Opacity of the cell-grid wall preview.")]
        [Range(0f, 1f)] public float cellGridOpacity = 0.5f;
        [Tooltip("Draw a dimension ruler (in feet) along the container's front edges, so you can read how tall " +
                 "and wide it is. Respects both the Size vector and the Transform scale.")]
        public bool showRuler = true;
        [Tooltip("Opacity of the dimension ruler (lines + labels).")]
        [Range(0f, 1f)] public float rulerOpacity = 0.95f;

        [Tooltip("PERF LEVER. Skip simulating regions whose smoke density is below this. Advection/decay smears " +
                 "invisible trace smoke across the grid; without this it marks most bricks 'active' (watch " +
                 "'Active Bricks %' in the HUD) and wastes the pressure solve — the cause of FPS sliding " +
                 "as the sim runs. Fire is never culled. Raise it if occupancy stays high for a small effect; " +
                 "lower it if faint wisps vanish.")]
        [Range(0.001f, 0.1f)] public float cullThreshold = 0.01f;

        // One public frame-rate control. The technical fields below are kept for scene/API compatibility, but
        // are now derived from this preset so the inspector only has one performance clock decision.
        public enum FpsLimit { ProjectSettings, VSync, Uncapped, Fps30, Fps45, Fps60, Fps90, Fps120, Fps144, Fps240, Custom }
        [Tooltip("One control for app frame pacing + BrazeFX solver pacing. Project Settings leaves Unity's " +
                 "application frame clock alone, while BrazeFX still caps its solver internally.")]
        public FpsLimit fpsLimit = FpsLimit.VSync;
        [Tooltip("Custom frame rate (Hz) when FPS Limit = Custom. Enforced system-wide (display + solver + bake).")]
        [Range(10, 240)] public int customFps = 60;
        [SerializeField, HideInInspector] bool fpsLimitMigrated;

        // FPS GOVERNOR — legacy/internal fields derived from FPS Limit. A fast (e.g. 4ms) frame left uncapped pins the GPU
        // at 100% power/heat rendering frames the display can't show; capping lets it idle the rest of the frame.
        public enum FrameGovernor { Off, Uncapped, VSync, TargetFps }
        [Tooltip("Take ownership of the APPLICATION-WIDE frame clock so the GPU isn't left running flat-out. " +
                 "Off = leave Unity/project settings untouched. Uncapped = explicitly release the cap (max FPS, " +
                 "GPU runs hot). VSync = lock to the display refresh. Target FPS = cap to the value below. " +
                 "NOTE: this drives the whole application's frame rate, not just this container.")]
        [HideInInspector] public FrameGovernor frameGovernor = FrameGovernor.Off;
        [Tooltip("Frame cap applied when Frame Governor = Target FPS.")]
        [HideInInspector, Range(24, 240)] public int targetFps = 60;

        // SOLVER-RATE GOVERNOR — decouple the expensive solve from the render rate. The solver normally steps
        // once PER RENDERED FRAME, so 246fps = 246 full solves/sec (the real GPU cost). Capping the sim to a
        // fixed rate skips the solve on most frames when render > sim; the slice renderer draws the latest field
        // until the next solver tick.
        [Tooltip("Run the solver at a FIXED rate, decoupled from render. The solver otherwise steps once per " +
                 "rendered frame (246fps = 246 solves/sec). Capping to 30–60 Hz lets the GPU skip the solve on " +
                 "most render frames; the slice renderer draws the latest solved field between ticks.")]
        [HideInInspector] public bool capSimRate = true;
        [Tooltip("THIS container's solver clock (Hz) — fully decoupled from the display/app frame rate. The " +
                 "solver ticks at this rate and the slice renderer draws the latest solved field between ticks. " +
                 "Fast jets/blasts want 60+. Cache baking " +
                 "captures at this rate.")]
        [HideInInspector, Range(10, 240)] public int simRate = 60;

        // The frame governor writes APPLICATION-WIDE state (Application.targetFrameRate, QualitySettings.vSyncCount).
        // We snapshot whatever was in effect BEFORE this container first overrode it, so OnDisable can put it back — a
        // disabled container must NOT keep governing the app's frame rate (otherwise it silently caps FPS for the whole
        // scene, e.g. a cache playback scene with the source container suppressed, making the real cost unmeasurable).
        // Any still-active container simply re-asserts its own governor on its next frame (ApplyFrameClock runs every frame).
        bool frameClockOverridden;
        int savedVSyncCount;
        int savedTargetFrameRate;

        [Tooltip("Show a live FPS / grid / occupancy HUD.")]
        public bool showStats = false;

        [Tooltip("DEBUG (Play mode). Draw the sparse-solver occupancy bricks in the Scene view: orange = raw " +
                 "content, blue = halo padding. Shows exactly what the solver pays for. Editor-only, off for shipping.")]
        public bool showBricks = false;
        [Tooltip("Opacity of the brick-visualizer cubes.")]
        [Range(0.02f, 0.6f)] public float brickOpacity = 0.15f;

        // ── DYNAMICS — shared transport and stability. Artistic motion is source-local on BrazePyroEmitter. ──────
        [Tooltip("Smoke lifetime in SECONDS — the e-fold time of the smoke density (it's ~63% gone after this long, " +
                 "and visually mostly cleared after ~3–4×). e.g. 2 = a 2-second fade. Higher = lingers longer.")]
        [Range(0.1f, 60f)] [BrazeAnimatable("Dynamics/Smoke Life")] public float smokeLife = 2f;
        [Tooltip("Fire lifetime in SECONDS — the e-fold time of the visible FLAME. Heat persists ~1.5× this (so the " +
                 "plume keeps rising as it turns to smoke) and leftover fuel ~3×. e.g. 1 = a ~1-second flame.")]
        [Range(0.1f, 60f)] [BrazeAnimatable("Dynamics/Fire Life")] public float fireLife = 1f;
        [Tooltip("Wisp Fade — subtractive density erosion. Smoke Life dims everything proportionally (a blob stays a " +
                 "blob, just fainter); this instead trims the thin, diffused haze faster than dense cores, so smoke " +
                 "fades by breaking into wisps and the soft 'wool' look clears — both stopped and moving. 0 = off. " +
                 "Raise until the haze breaks up but the plume cores survive.")]
        [Range(0f, 1f)] [BrazeAnimatable("Dynamics/Wisp Fade")] public float wispFade = 0.15f;
        [Tooltip("Ambient Life — anti-stasis. The woolly blob forms when smoke goes TOTALLY static; this keeps a gentle, " +
                 "large-scale, slow drift alive in the smoke so it never fully freezes. Coherent (not the high-frequency " +
                 "chaos of Turbulence). 0 = smoke can come to a dead stop; raise for a touch of perpetual life.")]
        [Range(0f, 1f)] [BrazeAnimatable("Dynamics/Ambient Life")] public float ambientLife = 0.1f;
        [Tooltip("Swirl — vorticity confinement. Amplifies the fluid's own vortices so the smoke rolls and curls " +
                 "(global, the whole plume). This also feeds Render Detail's vorticity, so raising it brings back " +
                 "the fine detail. 0 = smooth/laminar.")]
        [Range(0f, 12f)] [BrazeAnimatable("Dynamics/Swirl")] public float swirl = 10.44f;
        [Tooltip("Turbulence — global chaotic break-up: how wispy and turbulent the whole plume gets.")]
        [Range(0f, 60f)] [BrazeAnimatable("Dynamics/Turbulence")] public float turbulence = 18.6f;
        [Tooltip("Turbulence detail size (LOWER = bigger rolls, higher = finer wisps).")]
        [Range(0.02f, 0.5f)] [BrazeAnimatable("Dynamics/Turbulence Scale")] public float turbulenceScale = 0.25f;
        [Tooltip("Billows — a LARGE-scale turbulence octave for big rolling cauliflower forms, independent of the " +
                 "fine Turbulence. 0 = off.")]
        [Range(0f, 60f)] [BrazeAnimatable("Dynamics/Billows")] public float billowStrength = 0f;
        [Tooltip("Billow size (LOWER = bigger rolls). Keep well below Turbulence Scale so billows read as big forms.")]
        [Range(0.01f, 0.2f)] [BrazeAnimatable("Dynamics/Billow Scale")] public float billowScale = 0.06f;

        [Tooltip("INSTABILITY (base solver definition). Perturbs the buoyant lift spatially so the rising front breaks " +
                 "into lumps and ROLLS UP into mushroom caps (Rayleigh–Taylor) instead of rising as one smooth dome. " +
                 "This is the seed a clean symmetric blast is missing. 0 = off (laminar). ~0.3–0.8 for a defined blast.")]
        [Range(0f, 1f)] [BrazeAnimatable("Dynamics/Instability", 0f, 1f)] public float instability = 0f;
        [Tooltip("Instability lump size (LOWER = bigger mushroom lobes, higher = finer fingering). Drives the buoyancy " +
                 "perturbation noise frequency.")]
        [Range(0.02f, 0.5f)] [BrazeAnimatable("Dynamics/Instability Scale")] public float instabilityScale = 0.12f;
        [Tooltip("INTERFACE TURBULENCE (cauliflower). High-frequency curl-noise concentrated on the smoke↔air BOUNDARY " +
                 "(via the density gradient), so the leading edge curdles into cauliflower florets instead of churning the " +
                 "smooth interior. Needs a sharp-ish edge to bite. 0 = off. Pairs with Instability + Swirl.")]
        [Range(0f, 60f)] [BrazeAnimatable("Dynamics/Interface Turbulence", 0f, 60f)] public float interfaceTurbulence = 0f;
        [Tooltip("Cauliflower floret size (HIGHER = finer florets). Keep above Turbulence Scale so the edge detail reads " +
                 "finer than the body churn — note the base grid resolution caps how fine this can resolve.")]
        [Range(0.05f, 1f)] [BrazeAnimatable("Dynamics/Interface Turb Scale")] public float interfaceTurbScale = 0.45f;
        [Tooltip("Max motion speed at the Physics Reference Resolution (reference voxels/sec). Braze scales this with grid " +
                 "resolution so changing resolution preserves world-space motion. Low values damp motion hard (0 freezes it). " +
                 "Fast motion may require more substeps; the selected Quality Mode supplies the safety budget.")]
        [Range(0f, 600f)] [BrazeAnimatable("Dynamics/Max Speed", 0f, 600f)] public float maxSpeed = 43f;
        [Tooltip("Motion damping (1 = none). Lower = settles faster.")]
        [Range(0.9f, 1f)] [BrazeAnimatable("Dynamics/Velocity Dissipation")] public float velocityDissipation = 0.995f;
        [Tooltip("PERF LEVER. Pressure (Red-Black Gauss-Seidel) sweeps/frame; 6 is plenty for fire/smoke (each " +
                 "sweep = 2 indirect dispatches over every active brick). Raise only if motion looks too divergent.")]
        [Range(1, 40)] public int pressureIterations = 6;
        [Tooltip("PERF LEVER (cadence). Run the FULL pressure solve only every Nth step; the warm-started Trickle " +
                 "iterations carry the between-steps. 2 = the shipped default — roughly halves the hottest kernel " +
                 "block with the trickle keeping motion coupled; drop to 1 for maximum-fidelity fast turbulence. " +
                 "A full solve run less often beats a weak solve run constantly (the live twin of cache keyframing).")]
        [Range(1, 8)] public int pressureCadence = 2;
        [Tooltip("Warm-started pressure iterations on the between-solve frames (when Cadence > 1). Keeps the flow " +
                 "continuously near-divergence-free so buoyancy couples into motion smoothly instead of being lost in " +
                 "bursts — recovers the 'drive' that pure cadence drops. 0 = pure skip (max FPS, less motion); 2–3 = the " +
                 "smart middle. Only the heavy full solve still runs every Nth frame.")]
        [Range(0, 6)] public int pressureTrickle = 2;
        [Tooltip("Advection sub-steps per frame. A fast jet/blast moves many voxels per frame and SMEARS with one step; " +
                 "splitting advection into N steps of dt/N keeps it crisp (CFL). Cost ≈ +1 advection pass per extra step. " +
                 "1 = off. Raise to 2–4 for high Jet / fast motion.")]
        [Range(1, 8)] public int substeps = 2;
        // (The Sim Frame Skip cluster — skip N, motion-comp warp, front recovery, adaptive skip — was removed:
        // skip is just a worse spelling of a lower Sim Rate, the warp/front-recovery knobs had no consumer since
        // the legacy renderer, and the adaptive idea lives on below as Adaptive Rate on the solver clock itself.)
        [Tooltip("Auto-raise the solver clock toward Fast Sim Rate while emitters inject fast flow (a jet/blast), " +
                 "settling back to Sim Rate on calm smoke. Fast fronts need more solved frames than slow smoke.")]
        public bool adaptiveRate = true;
        [Tooltip("Solver Hz while the flow is fast (Adaptive Rate). Clamped to at least Sim Rate.")]
        [Range(24, 120)] public int fastSimRate = 60;
        [Tooltip("Speed (voxels/sec) at or above which Adaptive Rate runs the solver at the full Fast Sim Rate. " +
                 "Scales between ~⅛ of this (calm → Sim Rate) and this (fast → Fast Sim Rate).")]
        [Range(2f, 80f)] public float adaptiveFastSpeed = 18f;

        // ── LIVE RENDERER / LEGACY LOOK ─────────────────────────────────────────────────────────────────────
        // This toggle now controls the container-owned live slice renderer. The other legacy look fields in this
        // block remain serialized for cache/generated-volume compatibility.
        [Tooltip("Show the live container volume. Containers render through BrazeRenderFeature's live slice path.")]
        public bool renderVolume = true;
        [Tooltip("Render the SMOKE channel. Off = smoke hidden (fire still draws). The sim is unaffected.")]
        public bool renderSmoke = true;
        [Tooltip("Render the FIRE channel. Off = fire hidden (smoke still draws). The sim is unaffected.")]
        public bool renderFire = true;
        // ── Smoke ──
        [Tooltip("Smoke optical density (opacity magnitude). The opacity CURVE shapes how density maps to opacity.")]
        [BrazeAnimatable("Smoke/Density Scale", 0f, 20f)] public float densityScale = 5f;   // = reference smokeDensityScale
        [Tooltip("Detail: 0 = smooth, 1 = crisp wisps sculpted by the fine (high-res) density. Same lever as fire.")]
        [Range(0f, 1f)] [BrazeAnimatable("Smoke/Detail")] public float smokeDetail = 0.7f;
        [Tooltip("Overall smoke brightness multiplier.")]
        [BrazeAnimatable("Smoke/Brightness", 0f, 4f)] public float smokeBrightness = 1f;
        [Tooltip("Contrast / punch — deepens the self-shadowed cores for more 3D form and depth. 1 = neutral.")]
        [Range(1f, 4f)] [BrazeAnimatable("Smoke/Contrast")] public float smokeContrast = 1.5f;
        [Tooltip("Crispen: conforming, tap-free edge sharpen — contrast-expands density around the pivot so soft " +
                 "edges snap to a crisp boundary without depositing extra field content. 0 = off.")]
        [Range(0f, 1f)] [BrazeAnimatable("Smoke/Crispen")] public float crispen = 0f;
        [Tooltip("Crispen edge slope — higher steepens the soft falloff into a crisper surface.")]
        [Range(1f, 12f)] [BrazeAnimatable("Smoke/Crispen Sharpness")] public float crispenSharp = 4f;
        [Tooltip("Crispen pivot — the normalised density level that becomes the crisp boundary.")]
        [Range(0f, 1f)] [BrazeAnimatable("Smoke/Crispen Pivot")] public float crispenPivot = 0.35f;
        [Tooltip("Smooth Voxels: bakes an approximating cubic B-spline over the visual field ONCE per voxel " +
                 "(compute-side, the render march stays one tap), removing the coarse-grid voxel tiles that " +
                 "trilinear/quintic sampling preserves. Slight uniform softening by design — the minimum that " +
                 "de-voxels; Contrast/Crispen then sharpen the LOOK, not the grid. 0 = raw voxels.")]
        [Range(0f, 1f)] public float smoothVoxels = 1f;
        [Tooltip("Overall volume opacity.")]
        [Range(0f, 1f)] [BrazeAnimatable("Smoke/Opacity")] public float opacity = 0.95f;
        [Tooltip("Smoke albedo by density (thin -> dense). Pre-integrated into the smoke transfer LUT.")]
        public Gradient smokeAlbedoGradient;
        [Tooltip("Smoke opacity curve over density. Pre-integrated into the smoke transfer LUT.")]
        public AnimationCurve smokeOpacityCurve;
        [Tooltip("Legacy serialized compatibility. Final live/cache steps are controlled by Braze Render Feature.")]
        [Range(8, 192)] public int raymarchSteps = 96;
        [Tooltip("Legacy serialized compatibility. Final live/cache sampling density is controlled by Braze Render Feature.")]
        public bool adaptiveSteps = true;
        [Tooltip("Legacy serialized compatibility. Final live/cache height cap is controlled by Braze Render Feature.")]
        [Min(0)] public int maxRaymarchHeight = 0;

        // ── Fire ──
        [Tooltip("Brightness — the master (and ONLY) knob for how bright the fire emits. Range is now stable " +
                 "regardless of how hot the sim runs (the combustion amount is normalised). Heat changes colour, " +
                 "not brightness.")]
        [Range(0f, 10f)] [BrazeAnimatable("Fire/Brightness")] public float fireScale = 3f;   // = reference fireIntensity
        [Tooltip("Fire opacity / thickness — fire's OWN density (from burn), independent of smoke (reference Fire Opacity).")]
        [BrazeAnimatable("Fire/Density Scale", 0f, 6f)] public float fireDensityScale = 1.4f;   // = reference fireOpacityScale
        [Tooltip("Heat — COLOUR only: 0 = deep red embers, 0.5 = orange, 1 = white-hot. Luminance-normalised, so " +
                 "it shifts hue WITHOUT changing brightness (that's Intensity's job). A pure look knob.")]
        [Range(0f, 1f)] [BrazeAnimatable("Fire/Heat")] public float fireTempScale = 0.5f;
        [Tooltip("Detail: 0 = coarse base-res fire, 1 = flame structure sculpted by the fine (high-res) density " +
                 "for crisp, wispy edges. The cure for base-grid blockiness.")]
        [Range(0f, 1f)] [BrazeAnimatable("Fire/Detail")] public float fireDetail = 0.7f;
        [Tooltip("Fire Curve — remaps the fire AMOUNT (x = normalised 0..1 → y = shaped). Crush the toe to cut " +
                 "thin/noisy fire and de-blocky the fringe; lift the shoulder for a hotter core. A pure art curve " +
                 "on the normalised amount — no physics units.")]
        public AnimationCurve fireEmissionCurve;
        public enum FireColorMode { Blackbody, Gradient, Blend }
        [Tooltip("Blackbody = physical Planck locus (real fire colour). Gradient = artist-authored. Blend mixes both for the live slice renderer.")]
        public FireColorMode fireColorMode = FireColorMode.Blackbody;
        [Tooltip("Fire colour ramp when mode = Gradient (Blackbody ignores this).")]
        public Gradient fireGradient;
        // fireLightColor: the warm smoke-glow BASE colour (multiplied by the Fire Light tint at bind time); its
        // strength now folds into Fire Light Intensity. HideInInspector for serialization compat.
        [HideInInspector, ColorUsage(false, true)] public Color fireLightColor = new Color(1f, 0.45f, 0.12f, 1f);

        // ── Lighting ──
        [Tooltip("Key light. If empty, a default top-down direction + warm white is used.")]
        public Light sun;
        [Tooltip("Master toggle for the grid-space lighting bake (self-shadow + multi-scatter + fire-glow-on-smoke). " +
                 "Off = flat/ambient-only smoke (and skips the bake → cheaper): use it to A/B what the lighting adds.")]
        public bool bakeLighting = true;
        [Tooltip("Overall in-scatter / illumination strength (reference Illumination).")]
        [Range(0f, 2f)] [BrazeAnimatable("Lighting/Illumination")] public float illumination = 1f;
        [Tooltip("Multiple-scattering radiance amount — soft fill in shadowed cores (reference Scattering).")]
        [Range(0f, 2f)] [BrazeAnimatable("Lighting/Scattering")] public float scattering = 0.6f;
        [Tooltip("How deep the self-shadows get (reference Shadow Strength).")]
        [Range(0f, 8f)] [BrazeAnimatable("Lighting/Shadow Strength")] public float shadowStrength = 4.5f;
        public Color ambientColor = new Color(0.16f, 0.20f, 0.27f, 1f);
        [Tooltip("Ambient light intensity multiplier on the smoke.")]
        [Range(0f, 4f)] [BrazeAnimatable("Lighting/Ambient Intensity")] public float ambientIntensity = 1f;
        [Tooltip("Gradient-based shading strength -> sculpted/crisp billows (reference Definition).")]
        [Range(0f, 2f)] [BrazeAnimatable("Lighting/Definition")] public float definition = 1f;
        [Tooltip("Self-shadow march step length in base voxels.")]
        [Range(0.5f, 4f)] public float gridLightStepVox = 1.4f;
        [Tooltip("Self-shadow march steps (more = deeper self-shadow, costlier bake).")]
        [Range(2, 48)] public int gridLightSteps = 28;
        [Tooltip("Light Resolution: bake the (diffuse) illumination/scatter/fire-light at base ÷ N. 2 = half-res " +
                 "(≈8× less VRAM + lighting compute, slightly softer shadows). 1 = full res. The light march keeps " +
                 "its full-res reach — only the stored field is coarser.")]
        [Range(1, 4)] public int lightingDownsample = 2;
        [Tooltip("Rebake the light volumes every N sim steps (1 = every step). Lighting is diffuse and moves at " +
                 "smoke speed, not sim rate — 2 is visually imperceptible and halves the bake cost; the packed " +
                 "light volume is simply reused between bakes.")]
        [Range(1, 4)] public int lightingInterval = 2;
        [Tooltip("Soft depth — how far the volume fades as it meets solid surfaces (fraction of the box). 0 = hard cutout.")]
        [Range(0f, 0.4f)] [BrazeAnimatable("Lighting/Depth Feather")] public float depthFeather = 0.1f;
        [Tooltip("Edge Falloff — softens the container WALLS so smoke/fire that reaches a face dissolves into the " +
                 "boundary instead of being sliced flat (the boxy 'smoke in a box' look). Fraction of the box faded " +
                 "in from each face. 0 = hard box cut. Free: a few math ops per sample, no extra taps.")]
        [Range(0f, 0.4f)] [BrazeAnimatable("Lighting/Edge Falloff")] public float edgeFade = 0.06f;

        // ── Fire Light — the fire as a real LIGHT. ONE Intensity + Colour drive it; two toggles pick what it lights:
        // the SMOKE (cheap in-volume baked glow, folds in the old Fire Glow) and/or the SCENE (walls/characters/floor,
        // via a screen-space volumetric march toward the live flame). Both stay coherent off the same controls.
        [Tooltip("Master brightness of the fire light (drives both the smoke glow and the scene light).")]
        [Range(0f, 8f)] [BrazeAnimatable("Fire Light/Intensity", 0f, 8f)] public float fireLightIntensity = 1.5f;
        [Tooltip("Fire light colour (white = the fire's natural warm colour). Tints both the smoke glow and the " +
                 "scene light together; HDR.")]
        [ColorUsage(false, true)] public Color fireLightTint = Color.white;
        [Tooltip("Light the SMOKE volume — the warm glow the fire casts into the plume (baked, diffused, occluded). " +
                 "Cheap. The old standalone 'Fire Light' glow now lives here, driven by the Intensity above.")]
        public bool fireLightSmoke = true;
        [Tooltip("Light the SCENE around the fire — walls, characters, the floor — not just the smoke. A screen-space " +
                 "volumetric pass marches from each surface toward the live flame (a dragon in a dark room lights up " +
                 "when it breathes fire). Costs a fullscreen march; off = no scene light.")]
        public bool fireLight = false;
        [Tooltip("Scene light: how far it reaches (world units). Surfaces beyond this stay dark.")]
        [Range(0.1f, 50f)] [BrazeAnimatable("Fire Light/Range", 0.1f, 50f)] public float fireLightRange = 8f;
        [Tooltip("Scene light: source softness — larger spreads the falloff near the flame so it isn't a hot pinpoint (fraction of Range).")]
        [Range(0.01f, 1f)] public float fireLightSoftness = 0.25f;
        [Tooltip("Scene light: how strongly smoke BETWEEN a surface and the flame shadows the cast light (volume " +
                 "self-occlusion). 0 = smoke doesn't block the light.")]
        [Range(0f, 8f)] [BrazeAnimatable("Fire Light/Smoke Shadow", 0f, 8f)] public float fireLightShadow = 2f;
        [Tooltip("Scene light: march samples. More = smoother / longer-range, costlier. 8–16 is plenty.")]
        [Range(4, 32)] public int fireLightSteps = 12;

        // ── Cast Shadows — the smoke/fire density blocks the key light onto SCENE geometry (ground + 3D objects) ──
        // The inverse of Fire Light: a screen-space pass marches each opaque surface TOWARD the key light through
        // the density field and multiplies the scene colour by the resulting transmittance. Uses the same Sun the
        // volume's own lighting does. Slab-clipped to the grid, so it's cheap away from the plume.
        [Tooltip("Let the smoke/fire cast a SHADOW on the scene — darkens the ground and 3D objects under the plume " +
                 "by blocking the key light. Screen-space march toward the light through the density; uses the same " +
                 "Sun/key light as the volume's own lighting. Needs the volume rendering + a Depth Texture on the renderer.")]
        public bool castShadows = false;
        [Tooltip("Shadow darkness. 1 = full extinction shadow, 0 = none.")]
        [Range(0f, 1f)] [BrazeAnimatable("Cast Shadows/Strength", 0f, 1f)] public float castShadowStrength = 0.85f;
        [Tooltip("How strongly smoke density blocks the key light (extinction). Higher = darker, denser shadows.")]
        [Range(0f, 8f)] [BrazeAnimatable("Cast Shadows/Density", 0f, 8f)] public float castShadowDensity = 3f;
        [Tooltip("Shadow march samples. More = smoother, costlier. 8–16 is plenty.")]
        [Range(4, 32)] public int castShadowSteps = 12;

        // ── Render Detail — old warp-volume module ──
        // Bakes a vector displacement volume and domain-warps the existing render field. This is the older
        // "worked like charm" path: it upreses the look without drawing a second density/burn voxel layer.
        [Tooltip("Render-time detail: bakes a flow-carried warp volume and bends the existing smoke/fire render " +
                 "field through it. This restores the old Render Detail behavior: visual-only upres without a " +
                 "separate fine density/burn voxel layer.")]
        public bool renderDetail = false;
        public enum RenderDetailUpres { X2 = 2, X4 = 4 }
        [HideInInspector, Tooltip("Legacy transported fine-field upres. Live slice Render Detail now uses Detail Resolution.")]
        public RenderDetailUpres detailUpres = RenderDetailUpres.X2;
        [Tooltip("Render Detail strength. This scales the coordinate displacement, not density/burn creation.")]
        [Range(0f, 2f)] [BrazeAnimatable("Render Detail/Boost", 0f, 2f)] public float detailBoost = 0.6f;
        [Tooltip("Render Detail frequency in cycles across the volume. Higher = finer wisps.")]
        [Range(2f, 32f)] [BrazeAnimatable("Render Detail/Scale", 2f, 32f)] public float detailScale = 10f;
        [Tooltip("Stretch the warp along the accumulated flow so details form filaments instead of round blobs.")]
        [Range(0f, 4f)] [BrazeAnimatable("Render Detail/Stretch", 0f, 4f)] public float detailStretch = 1.5f;
        [Tooltip("Adds a finer fractal octave into the warp.")]
        [Range(0f, 1f)] [BrazeAnimatable("Render Detail/Roughness", 0f, 1f)] public float detailRoughness = 0.5f;
        [Tooltip("Strength of the tiny high-frequency displacement band.")]
        [Range(0f, 1f)] [BrazeAnimatable("Render Detail/Fine Detail", 0f, 1f)] public float detailFine = 1f;
        [Tooltip("Rotates the fine grain around local vorticity so it reads as flow instead of flat ripple.")]
        [Range(0f, 1f)] [BrazeAnimatable("Render Detail/Grain Curl", 0f, 1f)] public float detailGrainCurl = 0f;
        [Tooltip("Sharpens the displacement so wisps and gaps separate more clearly.")]
        [Range(1f, 3f)] [BrazeAnimatable("Render Detail/Contrast", 1f, 3f)] public float detailContrast = 1.5f;
        [Tooltip("Adds extra detail in turbulent/shear regions on top of the global baseline.")]
        [Range(1f, 4f)] [BrazeAnimatable("Render Detail/Edge", 1f, 4f)] public float detailEdge = 1f;
        [Tooltip("Rotates the warp around the sim's real vorticity axis.")]
        [Range(0f, 1f)] [BrazeAnimatable("Render Detail/Curl", 0f, 1f)] public float detailCurl = 0.5f;
        [Tooltip("Large soft displacement octave that keeps big rolling forms alive under the fine warp.")]
        [Range(0f, 1.5f)] [BrazeAnimatable("Render Detail/Billow", 0f, 1.5f)] public float detailBillow = 0.7f;
        [Tooltip("Resolution multiplier for the baked warp volume. Cost is CUBIC: 2× = 8× warp voxels, " +
                 "4× = 64×. Realtime default is 1×; the memory budget below automatically downgrades unsafe values.")]
        [Range(1, 4)] public int detailRenderScale = 1;
        [Tooltip("Maximum GPU memory for Render Detail's flow + warp volumes. If the requested Detail Resolution " +
                 "does not fit, BrazeFX automatically uses the highest smaller scale that does. If even 1× does " +
                 "not fit, Render Detail is disabled for this grid.")]
        [Range(64, 2048)] public int detailMemoryBudgetMB = 512;
        [Tooltip("Rebake the expensive procedural warp every N solver ticks. Motion is phase-smoothed between " +
                 "bakes and each sparse update is blended in-place, so higher values trade response for bake cost " +
                 "instead of visibly holding frames. 2 is the realtime default; 1 is maximum temporal fidelity.")]
        [Range(1, 8)] public int detailBakeInterval = 2;
        [HideInInspector] public float detailSharpness = 0.5f;
        [HideInInspector] public float detailQuality = 0.55f;
        [HideInInspector] public float detailMotion = 1f;
        [HideInInspector] public float detailResponse = 1f;
        [HideInInspector] public float detailPersistence = 0.6f;
        [HideInInspector] public float detailSpeed = 1f;
        [HideInInspector] public float detailCrisp = 0.4f;
        [HideInInspector] public float detailGrainMin = 2.5f;
        [HideInInspector] public float detailGrainMax = 9f;
        [Tooltip("Debug overlay: color where the fine field DIFFERS from the base — i.e. the detail actually created. " +
                 "Visual only.")]
        public bool detailDebugCells = false;
        [Tooltip("Debug overlay: hide the base smoke/fire fume and render only the created high-frequency detail band.")]
        public bool detailDebugCellsOnly = false;
        [Tooltip("Debug overlay cutoff. Raise to isolate only the strongest created detail; lower to reveal subtler folds.")]
        [Range(0f, 1f)] public float detailDebugCutoff = 0.55f;

        // Registry of live containers. Live containers draw through the live slice path; cache/generated-volume
        // renderers feed BrazeRenderFeature's generated-volume path.
        public static readonly List<BrazeContainer> Active = new();
        public static int RenderSettingsVersion { get; private set; }
        public static bool AnyVolumeRendering() => false;
        public static bool AnyRenderDetailRendering() => false;

        /// <summary>Generated-volume fire-light injection is disabled for live containers.</summary>
        public bool FireLightActive => false;
        /// <summary>Generated-volume fire-light injection material. Live containers do not provide one.</summary>
        public Material FireLightMaterial => null;
        /// <summary>Generated-volume fire-light injection is disabled for live containers.</summary>
        public static bool AnyFireLighting() => false;

        /// <summary>Generated-volume scene-shadow injection is disabled for live containers.</summary>
        public bool ShadowCastActive => false;
        /// <summary>Generated-volume scene-shadow injection is disabled for live containers.</summary>
        public static bool AnyShadowCasting() => false;

        /// <summary>Legacy shared raymarch height cap. Live containers are rendered by the container-owned slice renderer.</summary>
        public static int EffectiveMaxRaymarchHeight() => 0;

        // ── Fluid-follow exposure: let GPU particle systems sample this container's live velocity field ──────
        /// <summary>The solver's live velocity field (VOXELS/sec) — null until the solver is built.</summary>
        public RenderTexture FluidVelocity => solver != null ? solver.Velocity : null;
        /// <summary>Raw projection potential in grid-velocity units. Its gauge level is arbitrary; do not map
        /// this texture directly to a fixed pressure color range.</summary>
        public RenderTexture FluidPressure => solver != null ? solver.Pressure : null;
        /// <summary>Ambient-referenced, spatially/temporally conditioned, stagnation-calibrated Cp-like field.
        /// Null until the CFD surface-pressure diagnostic is active.</summary>
        public RenderTexture CfdPressureCoefficient => solver != null ? solver.CfdPressureVisualization : null;
        /// <summary>Compatibility alias for <see cref="CfdPressureCoefficient"/>.</summary>
        public RenderTexture CfdSmoothedPressure => solver != null ? solver.CfdPressureVisualization : null;
        /// <summary>Dilated occupancy / content mask — where fluid actually exists (gates the follow).</summary>
        public RenderTexture FluidOccupancy => solver != null ? solver.Occupancy : null;

        /// <summary>The solver's render-visible field (density/temp/fuel/burn, post de-voxel bake) —
        /// what the container-owned slice renderer should sample. Null until built.</summary>
        public RenderTexture FluidRenderField => solver != null ? solver.RenderField : null;

        /// <summary>The solver's render-side empty-space-skip volume (binary 0/1 per 4-voxel brick), rebuilt
        /// INSIDE every sim step right after the render field — always frame-coherent with
        /// <see cref="FluidRenderField"/>. External renderers must use this for skipping instead of baking
        /// their own from the field (an out-of-band bake can lag a frame and clip freshly ignited bricks).</summary>
        public RenderTexture FluidRenderOccupancy => solver != null ? solver.RenderOccupancy : null;
        public bool FluidReady => solver != null && solver.Velocity != null && solver.Occupancy != null;

        /// <summary>World position → velocity-texture UVW [0,1]. Mirrors WorldToVoxel but normalized (÷ Res).</summary>
        public Matrix4x4 WorldToFluidUVW()
        {
            Vector3 ws = WorldSize();
            Vector3 inv = new Vector3(1f / Mathf.Max(ws.x, 1e-4f), 1f / Mathf.Max(ws.y, 1e-4f), 1f / Mathf.Max(ws.z, 1e-4f));
            return Matrix4x4.Translate(new Vector3(0.5f, 0.5f, 0.5f))
                 * Matrix4x4.Scale(inv)
                 * Matrix4x4.Rotate(Quaternion.Inverse(transform.rotation))
                 * Matrix4x4.Translate(-transform.position);
        }

        /// <summary>Voxel/sec velocity → world/sec: rotate into world and scale each axis by the cell size.</summary>
        public Matrix4x4 FluidVoxelToWorldVelocity()
        {
            Vector3Int r = solver != null ? solver.Res : Vector3Int.one;
            Vector3 ws = WorldSize();
            Vector3 perVox = new Vector3(ws.x / Mathf.Max(r.x, 1), ws.y / Mathf.Max(r.y, 1), ws.z / Mathf.Max(r.z, 1));
            return Matrix4x4.Rotate(transform.rotation) * Matrix4x4.Scale(perVox);
        }

        /// <summary>Is this world point inside the container's (rotated) world box?</summary>
        public bool ContainsWorld(Vector3 p)
        {
            Vector3 ws = WorldSize();
            Vector3 rel = Quaternion.Inverse(transform.rotation) * (p - transform.position);
            return Mathf.Abs(rel.x) <= ws.x * 0.5f && Mathf.Abs(rel.y) <= ws.y * 0.5f && Mathf.Abs(rel.z) <= ws.z * 0.5f;
        }

        /// <summary>Pick the container that should drive a particle at this world point: the first active one
        /// whose box contains it, else the first active container with a built solver (single-container default).</summary>
        public static BrazeContainer ResolveFor(Vector3 worldPos)
        {
            BrazeContainer fallback = null;
            for (int i = 0; i < Active.Count; i++)
            {
                var c = Active[i];
                if (c == null || !c.isActiveAndEnabled || !c.FluidReady) continue;
                if (fallback == null) fallback = c;
                if (c.ContainsWorld(worldPos)) return c;
            }
            return fallback;
        }

        static void TouchRenderSettings()
        {
            unchecked { RenderSettingsVersion++; }
        }

        BrazeSolver solver;
        ComputeShader solverShader;
        float fps;
        GUIStyle hudStyle;
        string hudText = "";
        int hudTextFrame = -1;
        float simTime;
        float simAccum;   // fixed-rate solver clock accumulator (decoupled sim rate)
        int detailBakePhase;
        float detailLastBakeTime;
        Vector3Int lastRes3;
        Vector3Int failedRes3;

        // Reused buffers to keep per-frame allocation low. Sources come from the static registries
        // (BrazePyroEmitter/Force/Obstacle.Active), so they can live anywhere in the scene hierarchy.
        // Emitter Drag relayed as a whole-plume uniform (Buoyancy is now per-emitter, carried in _Vel.w).
        float gatheredDrag;       // max Motion/Drag across emitters → whole-plume velocity settle (1/sec)
        float gatheredJet;        // max emitter Jet speed (grid vox/s) → auto-raises the Max Speed clamp so the jet isn't capped
        BrazeSolver.GpuEmitter[] emitterData = new BrazeSolver.GpuEmitter[8];
        BrazeSolver.GpuForce[] forceData = new BrazeSolver.GpuForce[8];
        BrazeSolver.GpuObstacle[] obstacleData = new BrazeSolver.GpuObstacle[8];
        // Spline Vortex centreline/profile staging. Allocated only when one is active, then grown geometrically and
        // reused — no per-step managed garbage. One float4 per sample: voxel xyz + lathed profile radius.
        Vector4[] splineVortexSampleData;
        int gatheredSplineVortexSampleCount;

        // Point-set emission from BrazeSpread coverage masks (deduped per grid cell on the CPU → no atomics).
        BrazeSolver.PointSource[] pointData = new BrazeSolver.PointSource[64];
        // Per-cell point-source accumulation: summed channels (mass adds) + momentum-weighted velocity (so a
        // velocity is a mass-weighted average across the clumps in a cell, not a sum).
        struct PointAccum { public Vector3 channels; public Vector3 velWeighted; public float weight; public float blendWeighted; public float buoyWeighted; public float buoyBlendWeighted; public int count; public bool average; }
        readonly Dictionary<int, PointAccum> pointCells = new();
        readonly List<Vector3> coveredScratch = new();
        readonly List<Vector3> channelScratch = new();   // per-point (density,temp,fuel) for disintegrator clump emission
        readonly List<Vector3> velScratch = new();        // per-point world velocity (clump motion) for inheritance
        readonly List<BrazeObstacle.Prim> primBuf = new();
        bool emitterWarningChecked;

        void OnEnable()
        {
            MigrateQualityModeIfNeeded();
            MigrateFpsLimitIfNeeded();
            if (!cacheDriven) ApplyFrameClock();   // honour the governor from the first frame of play (a cache-host container never governs)
            emitterWarningChecked = false;

            if (!SystemInfo.supportsComputeShaders)
            {
                Debug.LogError("[BrazeFX] Compute shaders are not supported on this device.");
                enabled = false;
                return;
            }

            BrazeRuntimeRegistry.AddUnique(Active, this);

#if UNITY_EDITOR
            // Editor play-mode entry is sensitive to synchronous GPU setup. Defer regular live containers until
            // LateUpdate so entering play is not blocked by 3D texture allocation/initial clears. Cache hosts and
            // explicit bake/playback calls still build synchronously through their public entry points.
            if (!Application.isPlaying || cacheDriven)
#endif
            {
                EnsureRuntimeResources();
            }
        }

        void OnDisable()
        {
#if UNITY_EDITOR
            // A container disabled or deleted mid edit-sim must drop its reload hook itself: the timeline
            // window's EditorSimStop call is skipped for destroyed (fake-null) containers, which would leave
            // a dangling AssemblyReloadEvents subscription and EditorSimulating stuck true.
            EditorSimulating = false;
            editorSimRenderTime = 0f;
            AssemblyReloadEvents.beforeAssemblyReload -= EditorSimStop;
#endif
            RestoreFrameClock();   // stop governing the app's frame rate once this container is off
            BrazeRuntimeRegistry.Remove(Active, this);
            DisposeSolverSafely(solver);
            solver = null;
            ReleaseSliceRenderer();
            DestroyLegacyRendererObjects();
        }

        bool EnsureRuntimeResources()
        {
            if (solver != null) return true;
            if (!SystemInfo.supportsComputeShaders) return false;
            if (solverShader == null) solverShader = Resources.Load<ComputeShader>("BrazeSolver");
            if (solverShader == null)
            {
                Debug.LogError("[BrazeFX] Could not load 'BrazeSolver' compute shader (needs a Resources/ folder).");
                enabled = false;
                return false;
            }
            if (!CreateSolver()) return false;
            InitializeSliceRenderer();
            DestroyLegacyRendererObjects();
            return solver != null;
        }

        void CheckMissingEmitterWarning()
        {
            if (cacheDriven || emitterWarningChecked) return;
            emitterWarningChecked = true;
            // CFD and surface/fracture sources deliberately stay outside the PyroEmitter registry. Requiring that
            // one component specifically produced a false warning while those paths were visibly emitting.
            if (CfdActive) return;
            if (HasActiveFluidEmissionSource()) return;
            Debug.LogWarning("[BrazeFX] No active fluid emission source was found — nothing will enter this " +
                             "container. Add a Braze Pyro Emitter, or enable fluid emission on a Surface Effect, " +
                             "Surface Disintegrator, or Fracture Impact Source.");
        }

        internal bool HasActiveFluidEmissionSource()
        {
            if (HasActivePyroEmitter()) return true;

            for (int i = BrazeSurfaceEffect.Active.Count - 1; i >= 0; i--)
                if (IsActiveFluidEmissionSource(BrazeSurfaceEffect.Active[i], this)) return true;

            for (int i = BrazeSurfaceDisintegrator.Active.Count - 1; i >= 0; i--)
                if (IsActiveFluidEmissionSource(BrazeSurfaceDisintegrator.Active[i], this)) return true;

            for (int i = BrazeFractureImpactSource.Active.Count - 1; i >= 0; i--)
                if (IsActiveFluidEmissionSource(BrazeFractureImpactSource.Active[i], this)) return true;

            // An impact can continue depositing after its projectile/source has disabled itself. Keep recognizing
            // that live GPU deposit for its authored duration instead of warning midway through the effect.
            for (int i = BrazeVoronoiFracture.Active.Count - 1; i >= 0; i--)
            {
                BrazeVoronoiFracture fracture = BrazeVoronoiFracture.Active[i];
                if (fracture != null && fracture.TryGetImpactFluidGpuSource(this, out _)) return true;
            }
            return false;
        }

        internal static bool IsActiveFluidEmissionSource(Behaviour source, BrazeContainer receiver)
        {
            if (source == null || !source.isActiveAndEnabled) return false;
            if (source is BrazePyroEmitter) return true;
            if (source is BrazeSurfaceEffect surface) return surface.emitToFluid;
            if (source is BrazeSurfaceDisintegrator disintegrator)
                return disintegrator.EmitToFluid && (disintegrator.emitTrail || disintegrator.emitBurst);
            if (source is BrazeFractureImpactSource impact)
                return impact.depositToFluid && (impact.fluidContainer == null || impact.fluidContainer == receiver);
            return false;
        }

        static bool HasActivePyroEmitter()
        {
            for (int i = BrazePyroEmitter.Active.Count - 1; i >= 0; i--)
            {
                var e = BrazePyroEmitter.Active[i];
                if (e == null)
                {
                    BrazePyroEmitter.Active.RemoveAt(i);
                    continue;
                }
                if (e.isActiveAndEnabled) return true;
            }

#if UNITY_2023_1_OR_NEWER
#if UNITY_6000_5_OR_NEWER
            var sceneEmitters = Object.FindObjectsByType<BrazePyroEmitter>(FindObjectsInactive.Exclude);
#else
            var sceneEmitters = Object.FindObjectsByType<BrazePyroEmitter>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#endif
#else
            var sceneEmitters = Object.FindObjectsOfType<BrazePyroEmitter>();
#endif
            for (int i = 0; i < sceneEmitters.Length; i++)
                if (sceneEmitters[i] != null && sceneEmitters[i].isActiveAndEnabled)
                    return true;
            return false;
        }

        // Edit-safe destroy (Destroy() warns / no-ops in edit mode → use DestroyImmediate there).
        static void DestroyObj(Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        // Sweeps stale hidden "BrazeFX Render Proxy" children left in scenes saved before the slice renderer took over.
        void DestroyLegacyRendererObjects()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                Transform child = transform.GetChild(i);
                if (child != null && child.name == "BrazeFX Render Proxy")
                    DestroyObj(child.gameObject);
            }
        }

#if UNITY_EDITOR
        // ── Edit-mode simulation ─────────────────────────────────────────────────────────────────
        // Lets the timeline window run the solver in EDIT MODE (no Play): it ticks EditorSimStep each
        // editor frame and the URP volume composites in the Scene/Game view like normal. We deliberately
        // do NOT use [ExecuteAlways] — that would run the whole play-mode lifecycle (frame governor, etc.)
        // in the editor. This is an explicit, opt-in, self-disposing path instead.
        public bool EditorSimulating { get; private set; }
        float editorSimRenderTime;

        // Allocate the solver WITHOUT touching the frame governor (which is global and
        // would hijack the whole editor's frame rate). Mirrors the resource half of OnEnable only.
        void EnsureEditorSimResources()
        {
            if (solver != null) return;
            if (!SystemInfo.supportsComputeShaders) return;
            if (solverShader == null) solverShader = Resources.Load<ComputeShader>("BrazeSolver");
            if (solverShader == null) return;
            if (!CreateSolver()) return;
            InitializeSliceRenderer();
            DestroyLegacyRendererObjects();
            BrazeRuntimeRegistry.AddUnique(Active, this);
        }

        /// <summary>Advance the sim one editor frame by dt seconds (called by the timeline window).</summary>
        public void EditorSimStep(float dt)
        {
            if (Application.isPlaying || !isActiveAndEnabled) return;
            EnsureEditorSimResources();
            if (solver == null) return;

            if (!EditorSimulating)
            {
                EditorSimulating = true;
                editorSimRenderTime = 0f;
                detailLastBakeTime = 0f;
                AssemblyReloadEvents.beforeAssemblyReload += EditorSimStop;   // dispose GPU resources before a recompile
            }

            // Spike guard only (compile hitch, breakpoint, window drag). Must stay generous: a tight ceiling
            // (formerly 1/20 s) discards real wall-clock time whenever the editor loop runs slower than the
            // ceiling's rate, which played the whole edit-mode sim in slow motion. AdvanceSolverClock's
            // fixed-rate accumulator (≤ 2 catch-up steps, backlog capped at one interval) handles large honest
            // deltas the same way play mode handles a slow render frame. Keep in sync with
            // BrazeTimelineWindow.MaxEditorFrameDelta so playhead and solver absorb the same time per tick.
            dt = Mathf.Clamp(dt, 0f, 0.25f);
            editorSimRenderTime += dt;

            // mirror the live-rebuild + resize bits of LateUpdate
            Vector3Int desiredRes = ComputeRes();
            if (desiredRes != lastRes3 && desiredRes != failedRes3)
            {
                BrazeSolver old = solver;
                if (CreateSolver()) DisposeSolverSafely(old);
            }

            // Same decoupled fixed-rate clock as play mode — not a raw per-frame StepSimulation.
            AdvanceSolverClock(dt);
            UpdateSliceRenderer();

            DestroyLegacyRendererObjects();
        }

        /// <summary>Stop the edit-mode sim and release all GPU resources (idempotent).</summary>
        public void EditorSimStop()
        {
            EditorSimulating = false;
            editorSimRenderTime = 0f;
            AssemblyReloadEvents.beforeAssemblyReload -= EditorSimStop;
            if (Application.isPlaying) return;   // Play owns the lifecycle; nothing to do here

            BrazeRuntimeRegistry.Remove(Active, this);
            DisposeSolverSafely(solver);
            solver = null;
            ReleaseSliceRenderer();
            DestroyLegacyRendererObjects();
        }
#endif

        /// <summary>Clock for render-only animation. Edit-mode Timeline simulation has no advancing Time.time,
        /// so it supplies the same scaled wall time that drives EditorSimStep; every other path keeps Unity time.</summary>
        internal float SliceAnimationTime
        {
            get
            {
#if UNITY_EDITOR
                if (!Application.isPlaying && EditorSimulating) return editorSimRenderTime;
#endif
                return Time.time;
            }
        }

        void OnValidate()
        {
            resolution = Mathf.Clamp(resolution, 8, 512);   // no [Range] slider now → clamp the typed value here
            MigrateQualityModeIfNeeded();
            physicsReferenceResolution = Mathf.Clamp(physicsReferenceResolution, 8, 512);
            customMaxSubsteps = Mathf.Clamp(customMaxSubsteps, 1, 32);
            cflTargetVoxels = Mathf.Clamp(cflTargetVoxels, 0.5f, 8f);
            customRenderStepBudget = Mathf.Clamp(customRenderStepBudget, 8, 512);
            customMemoryBudgetMB = Mathf.Clamp(customMemoryBudgetMB, 64, 24576);
            failedRes3 = default; // a budget/mode edit may make the previously-rejected grid valid
            ValidateSliceRendererSettings();
            MigrateFpsLimitIfNeeded();
            ConfigureFpsLimit();
            TouchRenderSettings();
        }

        void Reset()
        {
            // Reset means "new Realtime preset", not merely changing the enum while stale Production/Mobile
            // numbers remain visible elsewhere in the inspector.
            resolution = 128;
            autoSubsteps = true;
            pressureIterations = 6;
            pressureCadence = 2;
            pressureTrickle = 2;
            substeps = 2;
            simRate = 60;
            fastSimRate = 60;
            cullThreshold = 0.01f;
            detailRenderScale = 1;
            detailMemoryBudgetMB = 512;
            detailBakeInterval = 2;
            sliceShadowResolution = SliceShadowResolution.Half;
            sliceShadowSteps = 16;
            sliceShadowFrameInterval = 2;
            fireGradient = BrazeBlackbody.DefaultFireGradient();
            smokeAlbedoGradient = BrazeTransferFunction.DefaultSmokeAlbedoGradient();
            smokeOpacityCurve = BrazeTransferFunction.DefaultSmokeOpacityCurve();
            fireEmissionCurve = BrazeTransferFunction.DefaultFireEmissionCurve();
            qualityMode = QualityMode.Realtime;
            qualityModeMigrated = true;
            physicsReferenceResolution = 128;
            fpsLimit = FpsLimit.VSync;
            fpsLimitMigrated = true;
            ConfigureFpsLimit();
        }

        void MigrateQualityModeIfNeeded()
        {
            if (qualityModeMigrated) return;

            // Existing scenes were authored before device modes existed. Preserve every existing budget knob by
            // placing them in Custom, and capture the CURRENT grid as the physical calibration point. From this point
            // onward a resolution edit scales motion/substeps relative to that baseline instead of changing the look.
            qualityMode = QualityMode.Custom;
            physicsReferenceResolution = Mathf.Clamp(resolution, 8, 512);
            customMaxSubsteps = Mathf.Max(4, Mathf.Clamp(substeps, 1, 8));
            customRenderStepBudget = 256;   // the former project-wide live-slice ceiling
            customMemoryBudgetMB = Mathf.Max(2048, detailMemoryBudgetMB);
            customStrictCfl = false;
            qualityModeMigrated = true;
        }

        bool CreateSolver()
        {
            MarkCreateSolver.Begin();
            try
            {
                // Cache playback forces the solver to the cache's BAKED resolution (so the uploaded frames map
                // 1:1), independent of the authored Resolution; otherwise size from the box as usual.
                Vector3Int r = cachePlaybackActive ? playbackRes : ComputeRes();
                if (!ValidateResourceBudget(r, out string rejection))
                {
                    failedRes3 = r;
                    ResourceStatus = $"Simulation not started — {rejection}";
                    Debug.LogWarning($"[BrazeFX] {ResourceStatus} This is a configured safety limit, not a " +
                                     "solver failure.", this);
                    return false;
                }

                BrazeSolver next;
                try
                {
                    next = new BrazeSolver(solverShader, r, cachePlaybackActive, cachePlaybackHasWarp,
                                           cachePlaybackDetailFlow, playbackWarpRes,
                                           cachePlaybackVdbReconstruction.enabled,
                                           cachePlaybackHasVelocity);
                }
                catch (System.Exception e)
                {
                    failedRes3 = r;
                    ResourceStatus = $"GPU allocation failed for {r.x}×{r.y}×{r.z}: {e.Message}";
                    Debug.LogError($"[BrazeFX] {ResourceStatus}", this);
                    return false;
                }

                solver = next;
                lastRes3 = r;
                failedRes3 = default;
                ResourceStatus = $"Ready — {r.x}×{r.y}×{r.z}, {FormatBytes(EstimateVramBytes())}";
                simTime = 0f;
                simAccum = 0f;
                detailBakePhase = 0;
                detailLastBakeTime = SliceAnimationTime * 0.15f;
                // A fresh solver = clean fields, but the slice bakes (occupancy/shadow/sculpt) still hold the OLD
                // sim's content — and their dirty keys still match, so nothing would ever rebake them: the empty-
                // space skip then reads ghost occupancy over empty air, which un-hides stale fine-detail bricks as
                // frozen smoke remnants. Drop the bakes (keys reset with them) so the first frame rebakes fresh.
                ReleaseSliceBakes();
                sliceFieldVersion++;
                return true;
            }
            finally
            {
                MarkCreateSolver.End();
            }
        }

        /// <summary>Clear the fluid state and restart the solver clock, keeping authored settings and sources intact.</summary>
        [BrazeTimelineEvent("SIMULATION/Restart")]
        public void RestartSimulation()
        {
            if (!isActiveAndEnabled) return;
            if (!SystemInfo.supportsComputeShaders) return;
            if (solverShader == null) solverShader = Resources.Load<ComputeShader>("BrazeSolver");
            if (solverShader == null) return;

            BrazeSolver old = solver;
            if (!CreateSolver()) return;
            InitializeSliceRenderer();
            DestroyLegacyRendererObjects();
            DisposeSolverSafely(old);

            // The sim clock just reset to 0 — rewind every emitter's burst window so one-shot/looping
            // bursts re-fire in sync with the fresh simulation (e.g. on a timeline loop restart).
            BrazePyroEmitter.ResetAllBursts();
        }

        // ── Cache hooks (BrazeCacheBaker records these; BrazeCachePlayer drives playback) ─────────────
        bool cachePlaybackActive;
        bool cachePlaybackHasWarp;
        bool cachePlaybackHasVelocity;
        bool cachePlaybackDetailFlow;   // DetailFlow clip: channels are the flow anchor; re-bake the warp per frame
        BrazeVdbReconstructionSettings cachePlaybackVdbReconstruction;
        int cacheVdbMotionFrame = -1, cacheVdbMotionNeighbour = -2, cacheVdbMotionDirection;
        int cacheVdbMotionSettingsHash;
        // Temporal-coherence continuity, tracked separately from the dedupe state above: the dedupe state is wiped
        // on every scatter, while history must survive exactly across sequential frame steps and die on seeks.
        int vdbMotionHistoryFrame = -1, vdbMotionHistoryNeighbour = -2, vdbMotionHistoryDirection;
        int vdbMotionHistorySettingsHash;
        Vector3Int playbackRes;
        Vector3Int playbackWarpRes;
        // Set by BrazeCachePlayer on a hidden playback container BEFORE it is enabled: this container only hosts cache
        // playback, so it must not warn about missing emitters or govern the app-wide frame clock.
        bool cacheDriven;
        bool cacheRealtimeMode;
        float cacheSamplesPerVoxel;
        bool cacheKeyLightOverride;
        Vector3 cacheKeyLightDirection;
        Color cacheKeyLightColor;

        /// <summary>Mark a runtime-created container as a cache-playback host (no emitter warning, no frame-clock
        /// governance). Call after AddComponent and BEFORE the GameObject is activated so OnEnable sees it.</summary>
        public void ConfigureAsCacheHost() => cacheDriven = true;

        /// <summary>Configure a hidden cache host completely before activation, so OnEnable constructs exactly one
        /// playback-only solver at the baked resolution instead of constructing and immediately replacing a live one.
        /// <paramref name="detailFlow"/> is reserved for compatibility caches that hold a flow anchor and request a
        /// Render Detail rebuild. Production final-look caches pass false and scatter their baked warp directly.</summary>
        public void ConfigureAsCacheHost(Vector3Int baseRes, bool hasWarp = false, bool detailFlow = false,
                                         Vector3Int warpRes = default,
                                         BrazeVdbReconstructionSettings vdbReconstruction = default,
                                         bool hasVelocity = false)
        {
            cacheDriven = true;
            cachePlaybackActive = true;
            cachePlaybackHasWarp = hasWarp;
            cachePlaybackHasVelocity = hasVelocity;
            cachePlaybackDetailFlow = hasWarp && detailFlow;
            cachePlaybackVdbReconstruction = vdbReconstruction.Validated();
            playbackRes = new Vector3Int(
                Mathf.Clamp(baseRes.x, 8, 512),
                Mathf.Clamp(baseRes.y, 8, 512),
                Mathf.Clamp(baseRes.z, 8, 512));
            playbackWarpRes = hasWarp ? (warpRes.x > 0 && warpRes.y > 0 && warpRes.z > 0 ? warpRes : playbackRes)
                                      : Vector3Int.zero;
        }

        /// <summary>Update the VDB-only renderer reconstruction. Enabling/disabling it changes playback resource
        /// ownership, so an already-created cache solver is rebuilt once; quality-only edits apply on the next scatter.</summary>
        public void ConfigureVdbPlaybackReconstruction(BrazeVdbReconstructionSettings settings)
        {
            settings = settings.Validated();
            bool wantsScalarFallback = settings.enabled && !cachePlaybackHasVelocity;
            bool rebuild = cachePlaybackActive && solver != null &&
                           solver.PlaybackMotionReconstruction != wantsScalarFallback;
            cachePlaybackVdbReconstruction = settings;
            InvalidateVdbMotionReconstruction();
            // Deliberately NOT invalidating the temporal history here: animated look knobs re-push settings every
            // frame through this path, and quality edits are already caught by the settings hash in the sequential
            // continuity check. Only a solver rebuild (below / BeginCachePlayback) recreates the history texture.
            if (rebuild)
                BeginCachePlayback(playbackRes, cachePlaybackHasWarp, cachePlaybackDetailFlow,
                                   playbackWarpRes, settings, cachePlaybackHasVelocity);
        }

        void InvalidateVdbMotionReconstruction()
        {
            cacheVdbMotionFrame = -1;
            cacheVdbMotionNeighbour = -2;
            cacheVdbMotionDirection = 0;
            cacheVdbMotionSettingsHash = 0;
        }

        void InvalidateVdbMotionHistory()
        {
            vdbMotionHistoryFrame = -1;
            vdbMotionHistoryNeighbour = -2;
            vdbMotionHistoryDirection = 0;
            vdbMotionHistorySettingsHash = 0;
        }

        /// <summary>True while a BrazeCachePlayer drives this container from a baked cache (solver sim suspended).</summary>
        public bool CachePlayback => cachePlaybackActive;
        /// <summary>Solver ready (textures allocated).</summary>
        public bool SolverReady => solver != null;
        public Vector3Int SolverRes => solver != null ? solver.Res : ComputeRes();
        public Vector3Int CacheFinalLookWarpRes
        {
            get
            {
                if (!renderDetail) return Vector3Int.zero;
                Vector3Int r = SolverRes;
                int scale = EffectiveDetailRenderScale(r);
                return scale > 0 ? r * scale : Vector3Int.zero;
            }
        }
        public int SolverBrickSize => solver != null ? solver.BrickSize : 4;
        public Vector3Int SolverOccRes => solver != null ? solver.OccRes : Vector3Int.one;
        public float CacheCullThreshold => cullThreshold;
        /// <summary>The rate a cache should be baked at = the container's own sim rate (set by the FPS Limit). Baking
        /// at the sim rate captures every distinct simulation step — no duplicates, no skips.</summary>
        public int CacheBakeFps => Mathf.Clamp(simRate, 10, 240);
        /// <summary>Bake readback: GPU-pack the base field into a flat uint buffer (reliable readback vs 3D texture).</summary>
        public ComputeBuffer PackBaseField() => solver != null ? solver.PackBaseField() : null;
        /// <summary>Bake readback: GPU-cull and compact active cache bricks before any voxel data crosses to the CPU.
        /// <paramref name="packBaseVelocity"/> stores raw simulation velocity in the base bricks' vector halfs
        /// (otherwise zero padding) for OpenVDB `vel` interchange.</summary>
        public int PackCacheSparse(float cull, out ComputeBuffer brickIndices, out ComputeBuffer packedBlocks,
                                   bool packBaseVelocity = false, bool packRenderDetail = true,
                                   bool velocityBounds = true)
        {
            if (solver != null) return solver.PackCacheSparse(cull, out brickIndices, out packedBlocks,
                packBaseVelocity, packRenderDetail, velocityBounds);
            brickIndices = null;
            packedBlocks = null;
            return 0;
        }

        /// <summary>Snapshot this container's full APPEARANCE (smoke/fire/lighting/detail) into a portable
        /// <see cref="BrazeRenderSettings"/>. The cache baker stamps it onto the baked clip so a container-free
        /// BrazeCachePlayer renders the same look. Read-only — does not touch the sim or render state.</summary>
        public BrazeRenderSettings CaptureRenderSettings() => new BrazeRenderSettings
        {
            rendererStateVersion = 1,
            qualityMode = qualityMode,
            customRenderStepBudget = customRenderStepBudget,
            customMemoryBudgetMB = customMemoryBudgetMB,

            renderSmoke = renderSmoke,
            renderFire = renderFire,

            densityScale = densityScale,
            smokeDetail = smokeDetail,
            smokeBrightness = smokeBrightness,
            smokeContrast = smokeContrast,
            crispen = crispen,
            crispenSharp = crispenSharp,
            crispenPivot = crispenPivot,
            smoothVoxels = smoothVoxels,
            opacity = opacity,
            raymarchSteps = raymarchSteps,
            adaptiveSteps = adaptiveSteps,
            maxRaymarchHeight = maxRaymarchHeight,
            smokeAlbedoGradient = smokeAlbedoGradient,
            smokeOpacityCurve = smokeOpacityCurve,

            fireScale = fireScale,
            fireDensityScale = fireDensityScale,
            fireTempScale = fireTempScale,
            fireDetail = fireDetail,
            fireEmissionCurve = fireEmissionCurve,
            fireColorMode = fireColorMode,
            fireGradient = fireGradient,
            fireLightColor = fireLightColor,

            bakeLighting = bakeLighting,
            illumination = illumination,
            scattering = scattering,
            shadowStrength = shadowStrength,
            ambientColor = ambientColor,
            ambientIntensity = ambientIntensity,
            definition = definition,
            gridLightStepVox = gridLightStepVox,
            gridLightSteps = gridLightSteps,
            lightingDownsample = lightingDownsample,
            lightingInterval = lightingInterval,
            depthFeather = depthFeather,
            edgeFade = edgeFade,

            fireLightIntensity = fireLightIntensity,
            fireLightTint = fireLightTint,
            fireLightSmoke = fireLightSmoke,
            fireLight = fireLight,
            fireLightRange = fireLightRange,
            fireLightSoftness = fireLightSoftness,
            fireLightShadow = fireLightShadow,
            fireLightSteps = fireLightSteps,

            castShadows = castShadows,
            castShadowStrength = castShadowStrength,
            castShadowDensity = castShadowDensity,
            castShadowSteps = castShadowSteps,

            renderDetail = renderDetail,
            detailBoost = detailBoost,          // Amount
            detailScale = detailScale,          // Feature Size
            detailSharpness = detailSharpness,  // Sharpness
            detailQuality = detailQuality,      // Detail Quality
            detailContrast = detailContrast,    // Warp contrast / generated sharpness
            detailRoughness = detailRoughness,  // Warp roughness / generated grain
            detailCurl = detailCurl,            // Flow Coherence
            detailMotion = detailMotion,        // Motion
            detailSpeed = detailSpeed,          // Speed
            detailResponse = detailResponse,    // Response
            detailPersistence = detailPersistence, // Persistence
            detailCrisp = detailCrisp,          // Crisp grain
            detailGrainMin = detailGrainMin,    // Grain Size Min
            detailGrainMax = detailGrainMax,    // Grain Size Max
            detailDebugCells = detailDebugCells,
            detailDebugCellsOnly = detailDebugCellsOnly,
            detailDebugCutoff = detailDebugCutoff,
            detailStretch = detailStretch,
            detailFine = detailFine,
            detailGrainCurl = detailGrainCurl,
            detailEdge = detailEdge,
            detailBillow = detailBillow,
            detailRenderScale = detailRenderScale,
            detailMemoryBudgetMB = detailMemoryBudgetMB,
            detailBakeInterval = detailBakeInterval,
            slice = CaptureSliceRenderSettings(),
        };

        /// <summary>Apply a portable baked/player look to this container. This is the inverse of
        /// <see cref="CaptureRenderSettings"/> and is shared by cache playback so fields never silently fall back to
        /// unrelated component defaults.</summary>
        public void ApplyRenderSettings(in BrazeRenderSettings s)
        {
            if (s.rendererStateVersion > 0)
            {
                qualityMode = s.qualityMode;
                customRenderStepBudget = Mathf.Clamp(s.customRenderStepBudget, 8, 512);
                customMemoryBudgetMB = Mathf.Clamp(s.customMemoryBudgetMB, 64, 24576);
            }
            renderSmoke = s.renderSmoke;
            renderFire = s.renderFire;
            densityScale = s.densityScale;
            smokeDetail = s.smokeDetail;
            smokeBrightness = s.smokeBrightness;
            smokeContrast = s.smokeContrast;
            crispen = s.crispen;
            crispenSharp = s.crispenSharp;
            crispenPivot = s.crispenPivot;
            smoothVoxels = s.smoothVoxels;
            opacity = s.opacity;
            raymarchSteps = s.raymarchSteps;
            adaptiveSteps = s.adaptiveSteps;
            maxRaymarchHeight = s.maxRaymarchHeight;
            smokeAlbedoGradient = s.smokeAlbedoGradient;
            smokeOpacityCurve = s.smokeOpacityCurve;

            fireScale = s.fireScale;
            fireDensityScale = s.fireDensityScale;
            fireTempScale = s.fireTempScale;
            fireDetail = s.fireDetail;
            fireEmissionCurve = s.fireEmissionCurve;
            fireColorMode = s.fireColorMode;
            fireGradient = s.fireGradient;
            fireLightColor = s.fireLightColor;

            bakeLighting = s.bakeLighting;
            illumination = s.illumination;
            scattering = s.scattering;
            shadowStrength = s.shadowStrength;
            ambientColor = s.ambientColor;
            ambientIntensity = s.ambientIntensity;
            definition = s.definition;
            gridLightStepVox = s.gridLightStepVox;
            gridLightSteps = s.gridLightSteps;
            lightingDownsample = s.lightingDownsample;
            lightingInterval = s.lightingInterval;
            depthFeather = s.depthFeather;
            edgeFade = s.edgeFade;

            fireLightIntensity = s.fireLightIntensity;
            fireLightTint = s.fireLightTint;
            fireLightSmoke = s.fireLightSmoke;
            fireLight = s.fireLight;
            fireLightRange = s.fireLightRange;
            fireLightSoftness = s.fireLightSoftness;
            fireLightShadow = s.fireLightShadow;
            fireLightSteps = s.fireLightSteps;
            castShadows = s.castShadows;
            castShadowStrength = s.castShadowStrength;
            castShadowDensity = s.castShadowDensity;
            castShadowSteps = s.castShadowSteps;

            renderDetail = s.renderDetail;
            detailBoost = s.detailBoost;
            detailScale = s.detailScale;
            detailSharpness = s.detailSharpness;
            detailQuality = s.detailQuality;
            detailContrast = s.detailContrast;
            detailRoughness = s.detailRoughness;
            detailCurl = s.detailCurl;
            detailMotion = s.detailMotion;
            detailSpeed = s.detailSpeed;
            detailResponse = s.detailResponse;
            detailPersistence = s.detailPersistence;
            detailCrisp = s.detailCrisp;
            detailGrainMin = s.detailGrainMin;
            detailGrainMax = s.detailGrainMax;
            detailDebugCells = s.detailDebugCells;
            detailDebugCellsOnly = s.detailDebugCellsOnly;
            detailDebugCutoff = s.detailDebugCutoff;
            if (s.slice.version > 0)
            {
                detailStretch = s.detailStretch;
                detailFine = s.detailFine;
                detailGrainCurl = s.detailGrainCurl;
                detailEdge = s.detailEdge;
                detailBillow = s.detailBillow;
                detailRenderScale = s.detailRenderScale;
                detailMemoryBudgetMB = s.detailMemoryBudgetMB;
                detailBakeInterval = s.detailBakeInterval;
                ApplySliceRenderSettings(s.slice);
            }
            else
            {
                // Best-effort legacy mapping for caches baked before the slice payload existed.
                sliceCastShadows = s.castShadows;
                sliceCastShadowStrength = s.castShadowStrength;
                sliceEdgeFade = s.edgeFade;
            }

            ValidateSliceRendererSettings();
            ReleaseSliceBakes();
            sliceTransferHash = 0;
            sliceFieldVersion++;
            TouchRenderSettings();
        }

        /// <summary>Reset the sim to frame 0 for a deterministic bake (keeps authored settings + sources).</summary>
        public void BakeBegin()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying) EnsureEditorSimResources();
#endif
            if (solver == null && Application.isPlaying && !EnsureRuntimeResources()) return;
            RestartSimulation();
        }

        /// <summary>Advance the sim exactly one fixed step (no decoupled-clock accumulator) for a frame-accurate bake.</summary>
        public void BakeStep(float dt)
        {
            if (solver == null) return;
            StepSimulation(Mathf.Max(1e-4f, dt));
        }

        /// <summary>Enter cache playback: rebuild the solver at the cache's BAKED resolution and suspend the
        /// sim. The BrazeCachePlayer then uploads frames each LateUpdate via <see cref="UploadCacheFrame"/>.</summary>
        public void BeginCachePlayback(Vector3Int baseRes, bool hasWarp = false, bool detailFlow = false,
                                       Vector3Int warpRes = default,
                                       BrazeVdbReconstructionSettings vdbReconstruction = default,
                                       bool hasVelocity = false)
        {
            if (solverShader == null) solverShader = Resources.Load<ComputeShader>("BrazeSolver");
            if (solverShader == null) return;
            bool wasPlayback = cachePlaybackActive;
            bool wasWarp = cachePlaybackHasWarp;
            bool wasVelocity = cachePlaybackHasVelocity;
            bool wasFlow = cachePlaybackDetailFlow;
            BrazeVdbReconstructionSettings wasVdbReconstruction = cachePlaybackVdbReconstruction;
            Vector3Int wasRes = playbackRes;
            Vector3Int wasWarpRes = playbackWarpRes;
            cachePlaybackActive = true;
            cachePlaybackHasWarp = hasWarp;
            cachePlaybackHasVelocity = hasVelocity;
            cachePlaybackDetailFlow = hasWarp && detailFlow;
            cachePlaybackVdbReconstruction = vdbReconstruction.Validated();
            playbackRes = new Vector3Int(Mathf.Clamp(baseRes.x, 8, 512), Mathf.Clamp(baseRes.y, 8, 512), Mathf.Clamp(baseRes.z, 8, 512));
            playbackWarpRes = hasWarp ? (warpRes.x > 0 && warpRes.y > 0 && warpRes.z > 0 ? warpRes : playbackRes)
                                      : Vector3Int.zero;
            bool warpMatches = !hasWarp || (solver != null && solver.WarpField != null &&
                solver.WarpField.width == playbackWarpRes.x && solver.WarpField.height == playbackWarpRes.y &&
                solver.WarpField.volumeDepth == playbackWarpRes.z);
            bool wantsScalarFallback = cachePlaybackVdbReconstruction.enabled && !hasVelocity;
            bool reconstructionMatches = solver != null &&
                                         solver.PlaybackMotionReconstruction == wantsScalarFallback;
            bool velocityMatches = solver != null && solver.PlaybackAuthoritativeVelocity == hasVelocity;
            if (solver != null && solver.Res == playbackRes && warpMatches && reconstructionMatches && velocityMatches) return;
            BrazeSolver old = solver;
            if (!CreateSolver())
            {
                cachePlaybackActive = wasPlayback;
                cachePlaybackHasWarp = wasWarp;
                cachePlaybackHasVelocity = wasVelocity;
                cachePlaybackDetailFlow = wasFlow;
                cachePlaybackVdbReconstruction = wasVdbReconstruction;
                playbackRes = wasRes;
                playbackWarpRes = wasWarpRes;
                return;
            }
            InitializeSliceRenderer();
            DestroyLegacyRendererObjects();
            DisposeSolverSafely(old);
            InvalidateVdbMotionReconstruction();
            InvalidateVdbMotionHistory();   // solver rebuild recreated (and cleared) the GPU history texture
        }

        /// <summary>Leave cache playback and rebuild the live solver from the authored settings.</summary>
        public void EndCachePlayback()
        {
            if (!cachePlaybackActive) return;
            bool wasWarp = cachePlaybackHasWarp;
            bool wasVelocity = cachePlaybackHasVelocity;
            bool wasFlow = cachePlaybackDetailFlow;
            Vector3Int wasWarpRes = playbackWarpRes;
            cachePlaybackActive = false;
            cachePlaybackHasWarp = false;
            cachePlaybackHasVelocity = false;
            cachePlaybackDetailFlow = false;
            playbackWarpRes = Vector3Int.zero;
            if (solverShader == null)
            {
                cachePlaybackActive = true;
                cachePlaybackHasWarp = wasWarp;
                cachePlaybackHasVelocity = wasVelocity;
                cachePlaybackDetailFlow = wasFlow;
                playbackWarpRes = wasWarpRes;
                return;
            }
            BrazeSolver old = solver;
            if (!CreateSolver())
            {
                cachePlaybackActive = true;
                cachePlaybackHasWarp = wasWarp;
                cachePlaybackHasVelocity = wasVelocity;
                cachePlaybackDetailFlow = wasFlow;
                playbackWarpRes = wasWarpRes;
                return;
            }
            InitializeSliceRenderer();
            DestroyLegacyRendererObjects();
            DisposeSolverSafely(old);
        }

        /// <summary>Cache playback: scatter one baked frame's render-ready sparse bricks directly into the renderer
        /// field and rebuild only its occupancy — no sim, conditioning, or solver-light bake. DetailFlow clips then
        /// re-bake the Render Detail warp at full scale from the scattered flow anchor. Returns FALSE if
        /// the frame isn't GPU-resident yet (streaming still loading it) so the
        /// caller can hold the previously scattered frame instead of showing garbage.</summary>
        public bool UploadCacheFrame(IBrazeCacheSource src, int frame, int motionNeighbour = -1,
                                     int motionDirection = 1)
        {
            if (solver == null || src == null) return false;
            if (!src.TryGetFrameSlice(frame, out int brickOffset, out int brickCount)) return false;
            // A classic scatter writes the display field directly; any motion-smoothing state is now stale.
            cacheBlendKeyFrame = -1;
            cacheBlendNeighbourFrame = -2;
            cacheBlendLastT = -1f;

            // The live BuildRenderField pass derives its four-channel skip/adaptive statistics with these exact
            // renderer gains. Playback reconstructs that support texture from the cached final scalar field.
            solver.RenderOccDensityGain = renderSmoke
                ? CalculateRenderOccupancyDensityGain(sliceDensityGain, densityScale)
                : 0f;
            solver.RenderOccFireGain = renderFire ? sliceFireGain : 0f;
            solver.UploadCacheFrameSparse(
                src.Idx, src.Blocks, brickOffset, brickCount,
                src.WarpQuant, src.HasWarp, src.HasVelocity, src.BlockId, src.Dedup, src.ScalarQuant,
                src.HasWarp && !src.DetailFlow);

            InvalidateVdbMotionReconstruction();

            // DetailFlow clip: the channels just scattered the base-res flow anchor — re-run the SAME warp
            // synthesis the live path uses, at this device's full Render Detail scale. This is what restores
            // the sub-voxel detail a base-res cache format cannot carry. Stateless per frame (the anchor comes
            // whole from the file), so scrubbing and looping stay exact. Detail time advances at the live rate
            // (0.15/s of clip time) from the frame index, so playback speed and seeks stay deterministic.
            if (cachePlaybackDetailFlow)
            {
                int scale = EffectiveDetailRenderScale(solver.Res);
                if (scale > 0)
                {
                    float fps = Mathf.Max(1f, src.Fps);
                    solver.BakePlaybackDetailCurl(fps);
                    solver.BakeWarp(scale, detailBoost, detailScale, frame / fps * 0.15f, detailStretch,
                                    detailRoughness, detailContrast, detailEdge, detailCurl, detailBillow,
                                    detailFine, detailGrainCurl);
                }
                else
                    solver.ReleasePlaybackWarp();   // module off / over budget → draw the unwarped base field
            }
            else if (src.HasVelocity)
            {
                // The imported VDB vector grid is authoritative. Preserve it for particles/temporal consumers even
                // when renderer reconstruction is disabled, and derive curl/flow without overwriting it with the
                // adjacent-scalar optical-flow fallback.
                BrazeVdbReconstructionSettings settings = cachePlaybackVdbReconstruction.Validated();
                solver.FinalizeCacheVelocity(src.Fps, settings.flowMemoryFrames);
                InvalidateVdbMotionHistory();
                RebuildCacheVelocityDetail(src, frame);
            }
            else if (cachePlaybackVdbReconstruction.enabled)
            {
                // The current scalar frame is already visible. Build a static high-frequency warp immediately, then
                // replace its zero motion with adjacent-frame optical flow as soon as the neighbour is GPU-resident.
                ReconstructVdbPlaybackFrame(src, frame, motionNeighbour, motionDirection);
            }

            sliceFieldVersion++;   // a new cached frame landed → slice bakes must re-run
            return true;
        }

        // A final-look cache already carries its exact native warp. Lean baked caches and imported VDB velocity
        // caches store scalars + velocity only; Render Detail is re-synthesized from the velocity-derived flow
        // anchor whenever the look requests the module. This is the standard path for Cache Baker clips.
        void RebuildCacheVelocityDetail(IBrazeCacheSource src, int frame)
        {
            if (!cachePlaybackHasWarp && renderDetail)
            {
                int scale = EffectiveDetailRenderScale(solver.Res);
                if (scale > 0)
                {
                    float fps = Mathf.Max(1f, src.Fps);
                    solver.BakeWarp(scale, detailBoost, detailScale, frame / fps * 0.15f, detailStretch,
                                    detailRoughness, detailContrast, detailEdge, detailCurl, detailBillow,
                                    detailFine, detailGrainCurl);
                }
                else solver.ReleasePlaybackWarp();
            }
            else if (!cachePlaybackHasWarp) solver.ReleasePlaybackWarp();
        }

        int cacheBlendKeyFrame = -1, cacheBlendNeighbourFrame = -2;
        float cacheBlendLastT = -1f;

        /// <summary>Motion-smoothed playback upload for velocity clips: the KEY stored frame and its NEIGHBOUR are
        /// scattered into dedicated blend fields (lazily allocated), and a single base-res compute pass writes the
        /// motion-compensated interpolation into the display field — the raymarch stays one-state-tap, so this
        /// costs one cheap pass per frame instead of the old per-sample two-volume interpolation. Falls back to
        /// the classic exact scatter for clips without velocity. Returns FALSE while the key frame is not yet
        /// GPU-resident; a missing neighbour degrades to holding the key frame (no strobe, no garbage).</summary>
        public bool UploadCacheFrameBlended(IBrazeCacheSource src, int keyFrame, int neighbourFrame, float blendT)
        {
            if (solver == null || src == null) return false;
            if (!src.HasVelocity || cachePlaybackHasWarp || cachePlaybackDetailFlow ||
                !solver.EnsureCacheBlendTargets())
                return UploadCacheFrame(src, keyFrame);
            if (!src.TryGetFrameSlice(keyFrame, out int keyOffset, out int keyCount)) return false;

            int neighbourOffset = 0, neighbourCount = 0;
            bool hasNeighbour = neighbourFrame >= 0 &&
                                src.TryGetFrameSlice(neighbourFrame, out neighbourOffset, out neighbourCount);
            int resolvedNeighbour = hasNeighbour ? neighbourFrame : -1;
            float t = hasNeighbour ? Mathf.Clamp01(blendT) : 0f;
            bool keyChanged = keyFrame != cacheBlendKeyFrame;
            bool neighbourChanged = resolvedNeighbour != cacheBlendNeighbourFrame;
            CacheBlendDisplayChanged = false;
            if (!keyChanged && !neighbourChanged && Mathf.Approximately(t, cacheBlendLastT))
                return true;   // settled playhead (paused, or a capped blend sub-step) — nothing to recompute

            if (keyChanged)
            {
                solver.RenderOccDensityGain = renderSmoke
                    ? CalculateRenderOccupancyDensityGain(sliceDensityGain, densityScale)
                    : 0f;
                solver.RenderOccFireGain = renderFire ? sliceFireGain : 0f;
                solver.UploadCacheFrameSparse(
                    src.Idx, src.Blocks, keyOffset, keyCount,
                    src.WarpQuant, src.HasWarp, src.HasVelocity, src.BlockId, src.Dedup, src.ScalarQuant,
                    splitWarp: false, fieldsTarget: solver.CacheBlendKeyField, skipOccupancy: true);
                InvalidateVdbMotionReconstruction();
                BrazeVdbReconstructionSettings settings = cachePlaybackVdbReconstruction.Validated();
                solver.FinalizeCacheVelocity(src.Fps, settings.flowMemoryFrames);
                InvalidateVdbMotionHistory();
            }
            if (keyChanged || neighbourChanged)
            {
                if (hasNeighbour)
                    solver.UploadPlaybackNeighbourSparse(src.Idx, src.Blocks, neighbourOffset, neighbourCount,
                        src.BlockId, src.Dedup, src.ScalarQuant, hasVel: true, warpQuant: src.WarpQuant);
                else
                    solver.ClearCacheBlendNeighbour();
                solver.RebuildCacheOccupancyPair(src.Idx, keyOffset, keyCount,
                                                 neighbourOffset, hasNeighbour ? neighbourCount : 0);
            }

            solver.CacheMotionBlend(t, 1f / Mathf.Max(1e-3f, src.Fps), hasNeighbour);
            if (keyChanged) RebuildCacheVelocityDetail(src, keyFrame);   // warp reads the freshly blended field

            cacheBlendKeyFrame = keyFrame;
            cacheBlendNeighbourFrame = resolvedNeighbour;
            cacheBlendLastT = t;
            // The raymarch samples the display field DIRECTLY, so a blend-only update is visible with no bake
            // invalidation at all. Bump the version — which re-runs the shadow march and Definition bake — only
            // when a stored frame actually changed; smoothly-moving density under stored-cadence shadows is
            // visually indistinguishable, and bumping per display frame is what tanked smoothed playback.
            if (keyChanged || neighbourChanged) sliceFieldVersion++;
            CacheBlendDisplayChanged = true;
            return true;
        }

        /// <summary>True when the LAST <see cref="UploadCacheFrameBlended"/> call actually rewrote the display
        /// field (false on its settled-playhead early-out). Lets Edit Mode preview refresh the renderer only
        /// when something changed instead of every editor tick.</summary>
        public bool CacheBlendDisplayChanged { get; private set; }

        /// <summary>Retry the adjacent-frame VDB solve without re-scattering the visible field. Streaming playback
        /// calls this while the neighbour is still arriving; resident playback normally hits on the first attempt.</summary>
        public bool RefreshVdbPlaybackReconstruction(IBrazeCacheSource src, int frame, int motionNeighbour,
                                                     int motionDirection)
        {
            if (!cachePlaybackActive || !cachePlaybackVdbReconstruction.enabled || solver == null || src == null ||
                src.HasVelocity)
                return false;
            return ReconstructVdbPlaybackFrame(src, frame, motionNeighbour, motionDirection);
        }

        bool ReconstructVdbPlaybackFrame(IBrazeCacheSource src, int frame, int motionNeighbour, int motionDirection)
        {
            if (src.HasVelocity) return true;
            BrazeVdbReconstructionSettings settings = cachePlaybackVdbReconstruction.Validated();
            int settingsHash = VdbSettingsHash(settings);
            int neighbourOffset = 0, neighbourCount = 0;
            bool hasNeighbour = motionNeighbour >= 0 &&
                                src.TryGetFrameSlice(motionNeighbour, out neighbourOffset, out neighbourCount);
            int resolvedNeighbour = hasNeighbour ? motionNeighbour : -1;
            if (cacheVdbMotionFrame == frame && cacheVdbMotionNeighbour == resolvedNeighbour &&
                cacheVdbMotionDirection == motionDirection && cacheVdbMotionSettingsHash == settingsHash)
                return hasNeighbour || motionNeighbour < 0;

            if (hasNeighbour)
            {
                // The previous solve's motion may seed this one only when this frame IS that solve's neighbour
                // (covers f→f+1 and the loop wrap last→0), in the same temporal direction with identical settings.
                // Seeks, scrubs, frame skips, and direction flips fall back to the exact history-free solve.
                bool temporalHistoryValid = vdbMotionHistoryFrame >= 0 &&
                                            frame == vdbMotionHistoryNeighbour &&
                                            motionDirection == vdbMotionHistoryDirection &&
                                            settingsHash == vdbMotionHistorySettingsHash;
                solver.UploadPlaybackNeighbourSparse(src.Idx, src.Blocks, neighbourOffset, neighbourCount,
                                                     src.BlockId, src.Dedup, src.ScalarQuant);
                solver.ReconstructVdbPlaybackMotion(src.Fps, motionDirection, settings, temporalHistoryValid);
                vdbMotionHistoryFrame = frame;
                vdbMotionHistoryNeighbour = resolvedNeighbour;
                vdbMotionHistoryDirection = motionDirection;
                vdbMotionHistorySettingsHash = settingsHash;
            }
            else
            {
                // Still VDBs and streaming misses get the full spatial detail field with a neutral flow anchor.
                solver.ClearVdbPlaybackMotion();
                InvalidateVdbMotionHistory();
            }

            if (renderDetail)
            {
                int scale = EffectiveDetailRenderScale(solver.Res);
                if (scale > 0)
                {
                    float fps = Mathf.Max(1f, src.Fps);
                    solver.BakeWarp(scale, detailBoost, detailScale, frame / fps * 0.15f, detailStretch,
                                    detailRoughness, detailContrast, detailEdge, detailCurl, detailBillow,
                                    detailFine, detailGrainCurl);
                }
                else solver.ReleasePlaybackWarp();
            }
            else solver.ReleasePlaybackWarp();

            cacheVdbMotionFrame = frame;
            cacheVdbMotionNeighbour = resolvedNeighbour;
            cacheVdbMotionDirection = motionDirection;
            cacheVdbMotionSettingsHash = settingsHash;
            return hasNeighbour || motionNeighbour < 0;
        }

        static int VdbSettingsHash(BrazeVdbReconstructionSettings s)
        {
            unchecked
            {
                int h = s.iterations;
                h = h * 31 + s.smoothness.GetHashCode();
                h = h * 31 + s.maxMotionVoxelsPerFrame.GetHashCode();
                h = h * 31 + s.flowMemoryFrames.GetHashCode();
                h = h * 31 + s.temporalSmoothing.GetHashCode();
                h = h * 31 + (s.fullResolutionMotion ? 1 : 0);
                return h;
            }
        }

        /// <summary>Refresh renderer-owned support textures after an Edit Mode cache scatter. Runtime cache hosts do
        /// this from LateUpdate; Export Studio has no MonoBehaviour update callback for its hidden host.</summary>
        internal void RefreshCachePlaybackRenderer()
        {
            if (cachePlaybackActive) UpdateSliceRenderer();
        }

        /// <summary>Retain legacy cache-playback state without overriding feature-owned camera quality.</summary>
        public void ConfigureCachePlaybackQuality(bool realtime, int maxSteps, float samplesPerVoxel, int maxHeight)
        {
            cacheRealtimeMode = realtime;
            cacheSamplesPerVoxel = realtime ? Mathf.Clamp(samplesPerVoxel, 0.25f, 1f) : 0f;
            // Velocity caches rebuild Render Detail from their stored motion; scalar-only VDB clips reconstruct
            // flow/warp via the optical-flow fallback. Only a pure scalar cache with neither input loses the module —
            // final-look and DetailFlow caches already carry the detail representation they need.
            if (!cachePlaybackDetailFlow && !cachePlaybackHasWarp && !cachePlaybackHasVelocity &&
                !cachePlaybackVdbReconstruction.enabled)
                renderDetail = false;
            // maxSteps/samplesPerVoxel/maxHeight remain accepted for source and binary compatibility. The active
            // Braze Render Feature is now the sole owner of live/cache steps, sampling density, and output size.
        }

        internal bool CacheRealtimeMode => cachePlaybackActive && cacheRealtimeMode;
        internal float CacheSamplesPerVoxel => cacheSamplesPerVoxel;

        /// <summary>Match an Edit Mode cache host to the source container's renderer scheduling. This is the
        /// compatibility path for v9 cache assets baked before renderer quality joined the portable look payload.</summary>
        internal void MatchCacheRendererState(BrazeContainer source)
        {
            if (source == null) return;
            qualityMode = source.qualityMode;
            customRenderStepBudget = source.customRenderStepBudget;
            customMemoryBudgetMB = source.customMemoryBudgetMB;
        }

        public void ConfigureCacheKeyLight(bool enabled, Vector3 direction, Color linearColor)
        {
            cacheKeyLightOverride = enabled;
            cacheKeyLightDirection = direction.sqrMagnitude > 1e-8f ? direction.normalized : Vector3.up;
            cacheKeyLightColor = linearColor;
        }

        internal bool TryGetCacheKeyLight(out Vector3 direction, out Color linearColor)
        {
            direction = cacheKeyLightDirection;
            linearColor = cacheKeyLightColor;
            return cachePlaybackActive && cacheKeyLightOverride;
        }


        // Core solver transport/stability stays container-wide; artistic motion is source-local on emitters.
        void WriteTransport(ref BrazeSolver.FrameParams fp)
        {
            float resolutionScale = PhysicsResolutionScale(solver.Res);
            fp.resolutionScale = resolutionScale;
            // Auto-raise the velocity clamp to fit the fastest emitter Jet (×1.25 headroom) so a high Jet isn't capped
            // by Max Speed — the jet sets the floor; the Max Speed knob still governs the rest of the field if it's higher.
            fp.maxSpeed = Mathf.Max(Mathf.Max(0f, maxSpeed) * resolutionScale, gatheredJet * 1.25f);
            fp.velDis = Mathf.Clamp(velocityDissipation, 0.9f, 1f);
            fp.smokeDecayMul = 1f / Mathf.Max(0.01f, smokeLife);   // life is a persistence multiplier: decay = 1/life
            fp.fireDecayMul = 1f / Mathf.Max(0.01f, fireLife);
            fp.smokeErode = Mathf.Max(0f, wispFade) * 0.5f;        // 0..1 knob → subtractive density erosion (per second)
            fp.ambientLife = Mathf.Max(0f, ambientLife) * 25f * resolutionScale; // voxel acceleration; preserve world-space drift
            // Drag is EMITTER-owned, relayed whole-plume. (Buoyancy is per-emitter, carried in _Vel.w — not here.)
            fp.drag = Mathf.Max(0f, gatheredDrag);
            fp.substeps = CalculateAdaptiveSubsteps(
                Mathf.Max(1, substeps), resolutionScale, fp.maxSpeed, fp.dt,
                cflTargetVoxels, EffectiveSubstepBudget, autoSubsteps, StrictCfl,
                out int requiredSubsteps);
            RequiredCflSubsteps = requiredSubsteps;
            ScheduledSubsteps = fp.substeps;
            // Container-wide global dynamics (the proven look layer).
            fp.confineScale = Mathf.Max(0f, swirl) * resolutionScale;
            fp.turbulence = Mathf.Max(0f, turbulence) * resolutionScale;
            fp.turbScale = turbulenceScale / Mathf.Max(resolutionScale, 1e-4f);
            fp.billow = Mathf.Max(0f, billowStrength) * resolutionScale;
            fp.billowScale = billowScale / Mathf.Max(resolutionScale, 1e-4f);
            // Definition seeds (base solver): baroclinic buoyancy perturbation + interface-gradient turbulence.
            fp.instability = Mathf.Max(0f, instability);
            fp.instabScale = instabilityScale / Mathf.Max(resolutionScale, 1e-4f);
            fp.interfaceTurb = Mathf.Max(0f, interfaceTurbulence) * resolutionScale;
            fp.interfaceTurbScale = interfaceTurbScale / Mathf.Max(resolutionScale, 1e-4f);
            // Live Render Detail uses the restored warp-volume module after solver.Step(). Do not allocate the newer
            // transported fine density/burn field here; that path is what exposed literal voxels in the slice renderer.
            fp.fineAmount = 0f;
            fp.fineUpres = (int)detailUpres;
            fp.fineFeature = 0.5f;
            fp.fineSharp = Mathf.Clamp01(detailSharpness);
            fp.fineFlow = Mathf.Clamp01(detailCurl);
            fp.fineQuality = Mathf.Clamp01(detailQuality);
            fp.fineMotion = Mathf.Clamp(detailMotion, 0f, 2f);
            fp.fineResponse = Mathf.Clamp(detailResponse, 0f, 2f);
            fp.finePersist = Mathf.Clamp01(detailPersistence);
            fp.fineSpeed = Mathf.Clamp(detailSpeed, 0f, 3f);
            fp.fineCrisp = 0f;
            fp.fineGrainMin = Mathf.Max(1f, detailScale * 0.25f);
            fp.fineGrainMax = Mathf.Max(2f, detailScale);
            fp.jacobiIters = Mathf.Clamp(pressureIterations, 1, EffectivePressureIterationBudget);
            fp.pressureCadence = Mathf.Max(1, pressureCadence);
            fp.pressureTrickle = Mathf.Max(0, pressureTrickle);
            fp.closedFaces = ClosedFaceMask();
        }

        int ClosedFaceMask()
        {
            int sides = closedSides ? 1 | 2 | 16 | 32 : 0; // -X, +X, -Z, +Z
            return sides | (closedBottom ? 4 : 0) | (closedTop ? 8 : 0);
        }

        // Absolute Transform (lossy) scale — the coarse size lever.
        Vector3 AbsLossy()
        {
            Vector3 s = transform.lossyScale;
            return new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
        }

        // The grid's actual world dimensions = base `size` × Transform scale. Scaling the GameObject
        // grows/shrinks the whole grid (e.g. a mile-wide volcano plume) at the SAME voxel resolution —
        // bigger voxels, not more of them. Emitter/force positions + footprints stay scale-invariant
        // (mapped against base `size`), so scaling is a clean uniform resize of the whole effect.
        public Vector3 WorldSize() => Vector3.Scale(size, AbsLossy());

        // Per-axis grid resolution from box aspect. The longest axis gets `resolution`; the others are
        // sized to the SAME world-space voxel pitch so voxels stay (near-)cubic. Uses the scaled world
        // box so non-uniform Transform scale redistributes voxels correctly (uniform scale → unchanged).
        Vector3Int ComputeRes()
        {
            Vector3 ws = WorldSize();
            float m = Mathf.Max(ws.x, Mathf.Max(ws.y, ws.z));
            if (m < 1e-4f) m = 1f;
            float voxel = m / Mathf.Max(resolution, 1);   // world units per voxel (uniform → cubic)
            return new Vector3Int(AxisRes(ws.x, voxel), AxisRes(ws.y, voxel), AxisRes(ws.z, voxel));
        }

        int AxisRes(float worldSize, float voxel)
            => Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(worldSize) / voxel), 8, 512);

        static long Voxels(Vector3Int r) => (long)r.x * r.y * r.z;

        /// <summary>Scale from the resolution at which voxel-space motion knobs were authored to the current grid.</summary>
        public float PhysicsResolutionScale(Vector3Int r)
        {
            int current = Mathf.Max(r.x, Mathf.Max(r.y, r.z));
            return current / (float)Mathf.Max(8, physicsReferenceResolution);
        }

        public int EffectiveSubstepBudget => qualityMode switch
        {
            QualityMode.Mobile => 2,
            QualityMode.Realtime => 4,
            QualityMode.Production => 32,
            _ => Mathf.Clamp(customMaxSubsteps, 1, 32),
        };

        public int EffectivePressureIterationBudget => qualityMode switch
        {
            QualityMode.Mobile => 4,
            QualityMode.Realtime => 8,
            _ => 40,
        };

        public int ModeRenderStepBudget => qualityMode switch
        {
            QualityMode.Mobile => 96,
            QualityMode.Realtime => 256,
            QualityMode.Production => 512,
            _ => Mathf.Clamp(customRenderStepBudget, 8, 512),
        };

        public long EffectiveMemoryBudgetBytes
        {
            get
            {
                int mb = qualityMode switch
                {
                    QualityMode.Mobile => 256,
                    QualityMode.Realtime => 2048,
                    QualityMode.Production => 16384,
                    _ => Mathf.Clamp(customMemoryBudgetMB, 64, 24576),
                };
                return (long)mb * 1024L * 1024L;
            }
        }

        /// <summary>Compatibility helper. Renderer-feature quality is authoritative for every container.</summary>
        internal int EffectiveSliceStepBudget(int featureBudget, bool sceneView)
            => Mathf.Clamp(featureBudget, 8, 512);

        internal float EffectiveSlicesPerVoxel(float featureValue)
            => Mathf.Clamp(featureValue, 0.25f, 2f);

        internal int EffectiveSliceDownsample(int featureValue)
            => Mathf.Clamp(featureValue, 1, 4);

        // Legacy compatibility property retained for diagnostics/reflection. The feature owns the active height cap.
        internal int EffectiveRaymarchHeightBudget => 0;

        internal static int CalculateAdaptiveSubsteps(
            int authored, float resolutionScale, float velocityVoxPerSecond, float dt,
            float cflTarget, int budget, bool auto, bool strictCfl, out int required)
        {
            authored = Mathf.Max(1, authored);
            budget = Mathf.Max(1, budget);
            int preserveReference = auto
                ? Mathf.CeilToInt(authored * Mathf.Max(1f, resolutionScale))
                : authored;
            int absoluteCfl = strictCfl
                ? Mathf.CeilToInt(Mathf.Max(0f, velocityVoxPerSecond) * Mathf.Max(0f, dt) /
                                  Mathf.Max(0.5f, cflTarget))
                : 1;
            required = Mathf.Max(authored, Mathf.Max(preserveReference, absoluteCfl));
            return Mathf.Clamp(required, 1, budget);
        }

        // The slice shader maps raw density through State Density Gain, applies a fixed 3× density remap, then
        // multiplies the transfer-function opacity by Density Scale. Keep the render occupancy oracle at least as
        // conservative as the old state-only mapping, while including that final extinction multiplier.
        internal static float CalculateRenderOccupancyDensityGain(float stateDensityGain, float smokeDensityScale)
        {
            float stateGain = Mathf.Max(0f, stateDensityGain);
            float extinctionGain = 3f * Mathf.Max(0f, smokeDensityScale);
            return stateGain * Mathf.Max(1f, extinctionGain);
        }

        internal bool StrictCfl => qualityMode == QualityMode.Production ||
                                   (qualityMode == QualityMode.Custom && customStrictCfl);

        public int RequiredCflSubsteps { get; private set; } = 1;
        public int ScheduledSubsteps { get; private set; } = 1;
        public bool CflBudgetLimited => RequiredCflSubsteps > ScheduledSubsteps;
        public string ResourceStatus { get; private set; } = "Ready";
        public Vector3Int AuthoredGridRes => ComputeRes();

        /// <summary>Highest requested Render Detail scale that fits the configured byte budget and the device's
        /// 3D-texture axis limit. Returns 0 when even a 1× flow+warp pair is unsafe.</summary>
        public int EffectiveDetailRenderScale(Vector3Int r)
        {
            if (!renderVolume || !renderDetail) return 0;
            int requested = Mathf.Clamp(detailRenderScale, 1, 4);
            long budget = (long)Mathf.Clamp(detailMemoryBudgetMB, 64, 2048) * 1024L * 1024L;
            int max3D = Mathf.Max(1, SystemInfo.maxTexture3DSize);
            long flowBytes = Voxels(r) * 8L;   // base-res ARGBHalf carrier
            for (int scale = requested; scale >= 1; scale--)
            {
                if ((long)r.x * scale > max3D || (long)r.y * scale > max3D || (long)r.z * scale > max3D)
                    continue;
                long warpBytes = Voxels(r) * scale * scale * scale * 8L;   // ARGBHalf warp
                if (flowBytes + warpBytes <= budget) return scale;
            }
            return 0;
        }

        long ExpectedSolverBytes(Vector3Int r)
        {
            long r3 = Voxels(r);
            long occ3 = (long)Mathf.CeilToInt(r.x / (float)BrazeSolver.OccDownscale)
                      * Mathf.CeilToInt(r.y / (float)BrazeSolver.OccDownscale)
                      * Mathf.CeilToInt(r.z / (float)BrazeSolver.OccDownscale);
            long bytes = r3 * 8L * 8L       // fields ×3, render ×2, velocity ×2, curl
                       + r3 * 4L * 2L       // divergence + pressure
                       + occ3 * 4L;         // two R8 solver masks + continuous RHalf render mask

            int detailScale = EffectiveDetailRenderScale(r);
            if (detailScale > 0)
                bytes += r3 * 8L + r3 * detailScale * detailScale * detailScale * 8L;

            // The real solid mask is lazy, but an authored active obstacle means the next solver tick allocates it.
            for (int i = 0; i < BrazeObstacle.Active.Count; i++)
                if (BrazeObstacle.Active[i] != null && BrazeObstacle.Active[i].isActiveAndEnabled)
                { bytes += r3 * 2L; break; }

            // Mesh/fracture emission uses a sparse hash sized from source samples (not resolution³). Surface-effect
            // coverage still uses its dense channel accumulator and remains part of the grid-scaled preflight.
            bytes += ExpectedMeshEmissionHashBytes();
            for (int i = 0; i < BrazeSurfaceEffect.Active.Count; i++)
                if (BrazeSurfaceEffect.Active[i] != null && BrazeSurfaceEffect.Active[i].isActiveAndEnabled &&
                    BrazeSurfaceEffect.Active[i].emitToFluid)
                { bytes += r3 * 3L * sizeof(uint); break; }
            return bytes;
        }

        long ExpectedMeshEmissionHashBytes()
        {
            long entries = 0;
            for (int i = 0; i < BrazePyroEmitter.Active.Count; i++)
            {
                BrazePyroEmitter e = BrazePyroEmitter.Active[i];
                if (e == null || !e.isActiveAndEnabled || e.shape != BrazePyroEmitter.Shape.Mesh) continue;
                entries += Mathf.Max(0, e.EstimatedGpuFluidPointCapacity);
            }
            if (entries <= 0) return 0;
            long needed = System.Math.Max(256L, entries * 2L);   // same <= 0.5 load factor as BrazeSolver
            long capacity = 256L;
            while (capacity < needed && capacity < (1L << 27)) capacity <<= 1;
            return capacity * 13L * sizeof(int);
        }

        long ExpectedSliceBytes(Vector3Int r)
        {
            long bytes = 0;
            if (renderVolume && ((bakeLighting && sliceSelfShadows) || sliceCastShadows))
            {
                Vector3Int sr = sliceShadowResolution == SliceShadowResolution.Full
                    ? r
                    : Vector3Int.Max(new Vector3Int(8, 8, 8), Vector3Int.CeilToInt((Vector3)r * 0.5f));
                bytes += Voxels(sr) * 2L;
            }
            if (renderVolume && bakeLighting && definition > 1e-4f)
                bytes += Voxels(r) * 4L;   // RGHalf sculpt
            return bytes;
        }

        // Estimated TOTAL container GPU memory, including optional detail/support resources. When running, compare
        // the authored expectation with the solver's actual allocations so source accumulators/history are counted too.
        public long EstimateVramBytes()
        {
            Vector3Int r = (solver != null) ? solver.Res : ComputeRes();
            long solverBytes = ExpectedSolverBytes(r);
            if (solver != null) solverBytes = System.Math.Max(solverBytes, solver.EstimateAllocatedBytes());
            return solverBytes + ExpectedSliceBytes(r);
        }

        /// <summary>Validate the authored allocation before constructing any GPU resources.</summary>
        public bool ValidateResourceBudget(Vector3Int r, out string reason)
        {
            int max3D = Mathf.Max(1, SystemInfo.maxTexture3DSize);
            int maxAxis = Mathf.Max(r.x, Mathf.Max(r.y, r.z));
            if (maxAxis > max3D)
            {
                reason = $"Grid {r.x}×{r.y}×{r.z} exceeds this device's {max3D}-voxel 3D-texture axis limit.";
                return false;
            }

            if (!cachePlaybackActive)
            {
                long expected = ExpectedSolverBytes(r) + ExpectedSliceBytes(r);
                long budget = EffectiveMemoryBudgetBytes;
                if (expected > budget)
                {
                    reason = $"Grid {r.x}×{r.y}×{r.z} needs about {FormatBytes(expected)}, above the " +
                             $"{qualityMode} budget of {FormatBytes(budget)}. Select Production, raise the Custom " +
                             "memory budget, or lower Grid/Detail/Shadow resolution.";
                    return false;
                }

                long maxBuffer = SystemInfo.maxGraphicsBufferSize;
                long meshHashBytes = ExpectedMeshEmissionHashBytes();
                if (meshHashBytes > maxBuffer)
                {
                    reason = $"Mesh emission needs {FormatBytes(meshHashBytes)} in one sparse accumulation buffer, " +
                             $"above this device's {FormatBytes(maxBuffer)} graphics-buffer limit.";
                    return false;
                }

                long cells = Voxels(r);
                bool hasSurfaceSource = false;
                for (int i = 0; i < BrazeSurfaceEffect.Active.Count; i++)
                {
                    BrazeSurfaceEffect s = BrazeSurfaceEffect.Active[i];
                    if (s != null && s.isActiveAndEnabled && s.emitToFluid)
                    { hasSurfaceSource = true; break; }
                }
                if (hasSurfaceSource && cells * 3L * sizeof(uint) > maxBuffer)
                {
                    reason = $"Surface emission needs {FormatBytes(cells * 3L * sizeof(uint))} in one accumulation buffer, " +
                             $"above this device's {FormatBytes(maxBuffer)} graphics-buffer limit.";
                    return false;
                }
            }

            reason = null;
            return true;
        }

        /// <summary>"123 MB" / "2.0 GB" — for HUD/inspector memory readouts.</summary>
        public static string FormatBytes(long b)
        {
            float mb = b / (1024f * 1024f);
            return mb >= 1024f ? $"{mb / 1024f:0.0} GB" : $"{mb:0} MB";
        }

        // LateUpdate (not Update): the Animator writes this frame's poses AFTER Update() and BEFORE
        // LateUpdate(), so reading emitter/force/obstacle transforms here picks up animated motion the
        // same frame instead of trailing it by one. Fixes animated emitters not tracking the plume.
        // Managed frame clock derived from FPS Limit. Called every frame so live inspector changes take effect.
        // Compare-before-write: these run every frame so live inspector changes apply, but rewriting the
        // app-global clock state with unchanged values every frame can churn native-side plumbing.
        static void WriteVSync(int v) { if (QualitySettings.vSyncCount != v) QualitySettings.vSyncCount = v; }
        static void WriteTargetFps(int v) { if (Application.targetFrameRate != v) Application.targetFrameRate = v; }

        void ApplyFrameClock()
        {
            ConfigureFpsLimit();
            if (Time.captureFramerate > 0)
            {
                CaptureFrameClockOnce();
                WriteVSync(0);
                WriteTargetFps(Time.captureFramerate);
                return;
            }

            switch (frameGovernor)
            {
                case FrameGovernor.Uncapped:
                    CaptureFrameClockOnce();
                    WriteVSync(0);
                    WriteTargetFps(-1);
                    break;
                case FrameGovernor.VSync:
                    CaptureFrameClockOnce();
                    WriteVSync(1);       // targetFrameRate is ignored while vSync drives the clock
                    break;
                case FrameGovernor.TargetFps:
                    CaptureFrameClockOnce();
                    WriteVSync(0);       // targetFrameRate only applies when vSync is off
                    WriteTargetFps(Mathf.Max(1, targetFps));
                    break;
                // FrameGovernor.Off → deliberately leave QualitySettings / targetFrameRate untouched (and never claim
                // ownership, so a container set to Off never restores anything on disable either).
            }
        }

        // Snapshot the app-wide frame clock the first time we're about to override it, so OnDisable can restore it.
        void CaptureFrameClockOnce()
        {
            if (frameClockOverridden) return;
            savedVSyncCount = QualitySettings.vSyncCount;
            savedTargetFrameRate = Application.targetFrameRate;
            frameClockOverridden = true;
        }

        // Hand the app-wide frame clock back to whatever set it before us. A disabled container must not keep governing
        // the whole scene's FPS. Still-active containers re-assert their own governor on the next frame.
        void RestoreFrameClock()
        {
            if (!frameClockOverridden) return;
            QualitySettings.vSyncCount = savedVSyncCount;
            Application.targetFrameRate = savedTargetFrameRate;
            frameClockOverridden = false;
        }

        void MigrateFpsLimitIfNeeded()
        {
            if (fpsLimitMigrated) return;
            fpsLimit = FpsLimitFromLegacy(frameGovernor, targetFps);
            fpsLimitMigrated = true;
        }

        void ConfigureFpsLimit()
        {
            // FPS Limit is the DISPLAY governor only (Application.targetFrameRate / vSync). The solver runs on
            // its OWN clock (Sim Rate), so the two rates are deliberately independent. Bake/playback consistency
            // doesn't need a shared display cadence: the
            // cache captures at the SOLVER rate (Time.captureFramerate = CacheBakeFps = simRate), so the clip's
            // speed is correct whatever the display does.
            switch (fpsLimit)
            {
                case FpsLimit.ProjectSettings:
                    frameGovernor = FrameGovernor.Off;
                    targetFps = 60;
                    break;
                case FpsLimit.VSync:
                    frameGovernor = FrameGovernor.VSync;
                    targetFps = 60;
                    break;
                case FpsLimit.Uncapped:
                    frameGovernor = FrameGovernor.Uncapped;
                    targetFps = 60;
                    break;
                default:
                    int fps = Mathf.Clamp(fpsLimit == FpsLimit.Custom ? customFps : FpsValue(fpsLimit), 24, 240);
                    frameGovernor = FrameGovernor.TargetFps;
                    targetFps = fps;
                    break;
            }

            capSimRate = true;
        }

        static int FpsValue(FpsLimit limit)
        {
            switch (limit)
            {
                case FpsLimit.Fps30: return 30;
                case FpsLimit.Fps45: return 45;
                case FpsLimit.Fps60: return 60;
                case FpsLimit.Fps90: return 90;
                case FpsLimit.Fps120: return 120;
                case FpsLimit.Fps144: return 144;
                case FpsLimit.Fps240: return 240;
                default: return 60;
            }
        }

        static FpsLimit FpsLimitFromLegacy(FrameGovernor governor, int fps)
        {
            if (governor == FrameGovernor.VSync) return FpsLimit.VSync;
            if (governor == FrameGovernor.Uncapped) return FpsLimit.Uncapped;
            if (governor != FrameGovernor.TargetFps) return FpsLimit.ProjectSettings;

            FpsLimit best = FpsLimit.Fps60;
            int bestDelta = int.MaxValue;
            FpsLimit[] presets =
            {
                FpsLimit.Fps30, FpsLimit.Fps45, FpsLimit.Fps60, FpsLimit.Fps90,
                FpsLimit.Fps120, FpsLimit.Fps144, FpsLimit.Fps240
            };
            for (int i = 0; i < presets.Length; i++)
            {
                int delta = Mathf.Abs(FpsValue(presets[i]) - fps);
                if (delta >= bestDelta) continue;
                best = presets[i];
                bestDelta = delta;
            }
            return best;
        }

        static void DisposeSolverSafely(BrazeSolver doomed)
        {
            if (doomed == null) return;
#if UNITY_EDITOR
            if (editorExitingPlayMode)
            {
                doomed.Dispose();
                return;
            }
#endif
            if (!Application.isPlaying)
            {
                doomed.Dispose();
                return;
            }
            SolverDisposer.Enqueue(doomed);
        }

#if UNITY_EDITOR
        static bool editorExitingPlayMode;

        [InitializeOnLoadMethod]
        static void RegisterPlayModeDisposalHook()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
                editorExitingPlayMode = true;
            else if (state == PlayModeStateChange.EnteredEditMode || state == PlayModeStateChange.EnteredPlayMode)
                editorExitingPlayMode = false;
        }
#endif

        sealed class SolverDisposer : MonoBehaviour
        {
            static SolverDisposer instance;

            public static void Enqueue(BrazeSolver doomed)
            {
                if (doomed == null) return;
                Ensure().StartCoroutine(DisposeWhenGpuIsIdle(doomed));
            }

            static SolverDisposer Ensure()
            {
                if (instance != null) return instance;
                var go = new GameObject("BrazeFX Solver Disposer")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                DontDestroyOnLoad(go);
                instance = go.AddComponent<SolverDisposer>();
                return instance;
            }

            static System.Collections.IEnumerator DisposeWhenGpuIsIdle(BrazeSolver doomed)
            {
                // RenderGraph/Metal can still reference old solver textures after a live setting rebuild.
                // Release them only after the current frame drains and pending GPU readbacks return.
                yield return new WaitForEndOfFrame();
                for (int i = 0; i < 8 && doomed.HasPendingReadbacks; i++)
                    yield return null;
                yield return null;
                doomed.Dispose();
            }
        }

        void LateUpdate()
        {
            if (!cacheDriven) ApplyFrameClock();
            if (solver == null && !EnsureRuntimeResources()) return;
            CheckMissingEmitterWarning();

            fps = Mathf.Lerp(fps, 1f / Mathf.Max(Time.unscaledDeltaTime, 1e-5f), 0.1f);

            // Cache playback: the BrazeCachePlayer uploaded this frame's fields at an earlier execution order;
            // suspend the live sim + the resolution live-rebuild (which would resize the playback solver).
            if (cachePlaybackActive)
            {
                UpdateSliceRenderer();
                return;
            }

            // Live rebuild when the resolution changes.
            Vector3Int desiredRes = ComputeRes();
            if (desiredRes != lastRes3 && desiredRes != failedRes3)
            {
                BrazeSolver oldSolver = solver;
                if (CreateSolver()) DisposeSolverSafely(oldSolver);
            }

            // ── Solver clock ── decoupled fixed-rate, or the legacy once-per-rendered-frame.
            AdvanceSolverClock(Time.deltaTime);
            UpdateSliceRenderer();

            if (showBricks) solver.RequestBrickDebug();   // DEBUG: async occupancy snapshot for the scene-view draw
        }

        // Decoupled solver clock — advance the sim by a render frame of length frameDt. Shared by play-mode
        // LateUpdate and editor-preview EditorSimStep so both paths use the same fixed-rate stepping.
        void AdvanceSolverClock(float frameDt)
        {
            if (capSimRate)
            {
                // Fixed-timestep accumulator: step the solver at the resolved clock rate regardless of render
                // fps. When render is faster than the clock, most frames do ZERO steps and the slice renderer draws
                // the latest solved field. Catch-up is clamped to 2 steps so a hitch can't spiral.
                float interval = 1f / Mathf.Max(ResolveSimRate(), 1);
                simAccum += frameDt;
                int steps = 0;
                int maxStepsThisFrame = CfdActive && cfdDomain.limitToOneSolverStepPerFrame ? 1 : 2;
                while (simAccum >= interval && steps < maxStepsThisFrame)
                {
                    StepSimulation(interval);
                    simAccum -= interval;
                    steps++;
                }
                simAccum = Mathf.Min(simAccum, interval);   // never bank more than one pending step
            }
            else
            {
                simAccum = 0f;
                StepSimulation(Mathf.Min(frameDt, 1f / 30f));   // legacy: one solve per rendered frame
            }
        }

        // The solver clock after the adaptive raise. When render frames outnumber solver ticks, fast directional
        // source motion can strobe because the field is held between ticks. While emitters inject fast flow
        // (gatheredJet, grid vox/s — a zero-cost CPU-side proxy), the clock rises toward Fast Sim Rate and settles
        // back to the authored Sim Rate on calm smoke. Buoyancy-only fast plumes aren't captured (would need a GPU
        // peak-vel readback — a later follow-up).
        int ResolveSimRate()
        {
            if (CfdActive) return Mathf.Clamp(cfdDomain.simulationRate, 10, 60);
            int baseRate = Mathf.Clamp(simRate, 10, 240);
            if (!adaptiveRate) return baseRate;
            int fastRate = Mathf.Max(baseRate, fastSimRate);
            float fast = Mathf.Max(0.1f, adaptiveFastSpeed);
            float referenceJet = gatheredJet / Mathf.Max(PhysicsResolutionScale(solver != null ? solver.Res : ComputeRes()), 1e-4f);
            // 0 at slow ambient (≤ ⅛ fast) → authored rate; 1 at/above fast → full fast rate.
            // Compare at the authored reference resolution so merely increasing spatial resolution cannot also
            // change the temporal preset's threshold.
            float t = Mathf.Clamp01(Mathf.InverseLerp(fast * 0.125f, fast, referenceJet));
            return Mathf.RoundToInt(Mathf.Lerp(baseRate, fastRate, t));
        }

        // One fixed-dt simulation step: gather sources, write transport/dynamics, bake lighting, run the solver.
        // Separated from LateUpdate so the solver clock (above) can step it 0, 1, or 2 times per rendered frame.
        // Permanent cost attribution: one Profiler glance separates CPU source-gathering from the
        // solver dispatch. Guards against the invisible-CPU-tax class of regression ever hiding again.
        static readonly ProfilerMarker MarkGather = new("BrazeFX.GatherSources");
        static readonly ProfilerMarker MarkStep = new("BrazeFX.SolverStep");
        static readonly ProfilerMarker MarkDetail = new("BrazeFX.RenderDetail");
        static readonly ProfilerMarker MarkCreateSolver = new("BrazeFX.CreateSolver");

        void StepSimulation(float dt)
        {
            simTime += dt;
            sliceFieldVersion++;   // any step changes the render field (advect/decay) → slice bakes must re-run
            solver.DiagnosticsEnabled = showStats;   // brick-count readback only exists for the HUD

            MarkGather.Begin();
            // CFD owns a scoped source set. Its opt-in path deliberately ignores the global Fire registries so
            // adding a CFD domain cannot accidentally mix two authoring models in one grid.
            int emitterCount = CfdActive ? GatherCfdSources(dt) : GatherEmitters();
            int forceCount = CfdActive ? 0 : GatherForces();
            int obstacleCount = CfdActive ? 0 : GatherObstacles();
            MarkGather.End();

            var fp = new BrazeSolver.FrameParams
            {
                dt = dt,
                time = simTime,
                smokeDecayMul = 1f,   // reference decay if Dynamics is disabled (else 0 → exp(0)=1 → never dies)
                fireDecayMul = 1f,
                occThreshold = cullThreshold,   // container-owned brick cull (NOT overridden by Dynamics.Write)
            };
            WriteTransport(ref fp);                         // advection/projection/decay stay active for source-driven motion
            ApplyCfdFramePolicy(ref fp);
            ApplyCfdSolverPolicy(dt);
            // Render-field conditioning is consumed inside solver.Step by BuildRenderField. Keep it driven by the
            // container itself so the slice renderer path does not depend on the legacy volume material binder.
            solver.RenderDeVoxel = smoothVoxels;
            // Empty-space skipping must use the same visibility scale as the final smoke extinction. Otherwise a
            // high Density Scale makes trace dust visible after the occupancy oracle has already rejected its whole
            // 4³ brick (large rectangular holes on low-resolution grids). This changes only the tiny render skip
            // mask; simulation occupancy, dispatch cost, and the rendered density field remain untouched.
            solver.RenderOccDensityGain = renderSmoke
                ? CalculateRenderOccupancyDensityGain(sliceDensityGain, densityScale)
                : 0f;
            solver.RenderOccFireGain = renderFire ? sliceFireGain : 0f;
            MarkGather.Begin();
            int pointCount = CfdActive ? 0 : GatherPointSources(); // CFD never joins Fire surface/point registries
            solver.SetPointSources(pointData, pointCount);   // disintegrator/mesh-emitter points (surface effects are GPU-splatted)
            MarkGather.End();

            // Live rendering now belongs to this container's slice renderer. Keep the solver producing a smoothed RenderField,
            // but do not run the legacy grid-light/detail/fire-injection bakes that only BrazeFX/PyroVolume used.
            solver.LightingEnabled = false;
            solver.GridFireLightEnabled = false;
            MarkStep.Begin();
            try
            {
                solver.Step(fp, emitterData, emitterCount, forceData, forceCount, obstacleData, obstacleCount,
                    splineVortexSampleData, gatheredSplineVortexSampleCount);
            }
            catch (System.Exception e)
            {
                ResourceStatus = $"Solver GPU work failed: {e.Message}";
                Debug.LogError($"[BrazeFX] {ResourceStatus}. The container has been disabled to avoid repeated " +
                               "allocation/dispatch failures; lower its budget or select a cheaper Quality Mode.", this);
                enabled = false;
                return;
            }
            finally
            {
                MarkStep.End();
            }

            if (renderVolume && renderDetail && !CfdVolumeSuppressed)
            {
                using var detailScope = MarkDetail.Auto();
                int effectiveScale = EffectiveDetailRenderScale(solver.Res);
                if (effectiveScale > 0)
                {
                    solver.StepFlowMaps(dt * 1.5f, 0.96f);
                    int interval = Mathf.Max(1, detailBakeInterval);
                    bool needsAllocation = solver.WarpField == null || solver.WarpScale != effectiveScale;
                    if (needsAllocation || detailBakePhase % interval == 0)
                    {
                        float detailTime = SliceAnimationTime * 0.15f;
                        solver.BakeWarp(effectiveScale, detailBoost, detailScale, detailTime, detailStretch,
                                        detailRoughness, detailContrast, detailEdge, detailCurl, detailBillow,
                                        detailFine, detailGrainCurl,
                                        CalculateWarpTemporalBlend(interval, needsAllocation));
                        detailLastBakeTime = detailTime;
                    }
                    else
                    {
                        // Cadence may skip the expensive bake, but retired bricks must become neutral immediately;
                        // otherwise stale displacement blocks keep warping the render-history tail at old positions.
                        solver.ClearRetiredWarp();
                    }
                }
                else
                    solver.ReleaseRenderWarp();
                detailBakePhase++;
            }
            else
            {
                solver.ReleaseRenderWarp();
                detailBakePhase = 0;
                detailLastBakeTime = SliceAnimationTime * 0.15f;
            }

            // The container-owned slice renderer owns self-shadowing and cast shadows. The old fullscreen fire-light/shadow
            // injection path is intentionally not updated here.
        }

        internal static float CalculateWarpTemporalBlend(int bakeInterval, bool freshAllocation)
            => freshAllocation ? 1f : 1f / Mathf.Sqrt(Mathf.Max(1, bakeInterval));

        // Voxels per ACTUAL world unit (size × Transform scale) — so emitter/force radii are a literal
        // world distance, exactly like obstacles (GatherObstacles). Both sizing levers map the same way,
        // so `size` and Transform scale are fully interchangeable: resizing the box keeps sources at their
        // physical radius (a bigger box → relatively tighter source), and the look stays identical between
        // the two methods.
        float VoxPerWorld() => solver.Res.x / Mathf.Max(Mathf.Abs(WorldSize().x), 1e-4f);

        // World point -> voxel coords. Divides by the SCALED world box so children (which move with the
        // Transform) land on the right voxel regardless of Transform scale.
        Vector3 WorldToVoxel(Vector3 worldPos)
        {
            Vector3Int r = solver.Res;
            Vector3 ws = WorldSize();
            Vector3 rel = Quaternion.Inverse(transform.rotation) * (worldPos - transform.position);
            Vector3 nrm = new Vector3(
                rel.x / Mathf.Max(ws.x, 1e-4f),
                rel.y / Mathf.Max(ws.y, 1e-4f),
                rel.z / Mathf.Max(ws.z, 1e-4f));
            return new Vector3((nrm.x + 0.5f) * r.x, (nrm.y + 0.5f) * r.y, (nrm.z + 0.5f) * r.z);
        }

        // Quaternion → float4 for the GPU (xyzw). The shader rotates a sample point by the conjugate to bring it
        // into the emitter's local frame, so non-sphere source shapes follow the emitter's Transform rotation.
        static Vector4 QuatVec4(Quaternion q) => new Vector4(q.x, q.y, q.z, q.w);

        // Collect covered surface points from all active BrazeSpread masks, map each to its grid cell, and
        // DEDUPE per cell (accumulating channels) so each GPU thread writes a distinct cell (no atomics).
        readonly List<BrazeSolver.SurfaceSourceGpu> gpuSurfScratch = new();
        readonly List<BrazeSolver.MeshSourceGpu> gpuMeshScratch = new();
        readonly List<BrazeSolver.FragmentSourceGpu> gpuFragmentScratch = new();

        int GatherPointSources()
        {
            pointCells.Clear();
            gpuSurfScratch.Clear();
            var spreads = BrazeSurfaceEffect.Active;   // Spread + Surface Mapper both register here
            Vector3Int r = solver.Res;
            float resolutionScale = PhysicsResolutionScale(r);
            for (int s = 0; s < spreads.Count; s++)
            {
                var sp = spreads[s];
                if (sp == null || !sp.isActiveAndEnabled || !sp.emitToFluid) continue;

                // GPU-native: coverage test + splat run entirely on the GPU from the live (skinned)
                // vertex buffer — no CPU vertex walk, and emission tracks animation at full rate
                // between gathers. An effect that can't expose a raw vertex buffer warns once and
                // doesn't emit (the CPU walk it would need cost multiple ms/step and was removed).
                if (sp.TryGetGpuEmission(out var gpuSrc))
                    gpuSurfScratch.Add(gpuSrc);
            }
            // Publish the GPU set (persists in the solver until the next gather; splat/fold run every step).
            Matrix4x4 worldToVoxel = Matrix4x4.Scale(new Vector3(r.x, r.y, r.z)) * WorldToFluidUVW();
            solver.SetSurfaceSources(gpuSurfScratch, worldToVoxel);

            // Surface Disintegrators: released clumps inject smoke/fire as MOVING point sources — channels + the
            // clump's velocity (so the emitted fume inherits its motion instead of being born dead).
            var disis = BrazeSurfaceDisintegrator.Active;
            float voxPerWorld = VoxPerWorld();
            Vector3 ws = WorldSize();
            Quaternion worldToGrid = Quaternion.Inverse(transform.rotation);
            Vector3 gridOrigin = transform.position;   // hoisted with ws/worldToGrid: CellOf used to re-query
                                                       // position/rotation/lossyScale PER POINT (up to 20k/step)
            for (int s = 0; s < disis.Count; s++)
            {
                var di = disis[s];
                if (di == null || !di.EmitToFluid) continue;
                coveredScratch.Clear(); channelScratch.Clear(); velScratch.Clear();
                di.CollectFluidPoints(coveredScratch, channelScratch, velScratch);
                float inherit = di.EmitVelocityInherit;
                for (int i = 0; i < coveredScratch.Count; i++)
                {
                    int cell = CellOf(coveredScratch[i], r, gridOrigin, worldToGrid, ws);
                    if (cell < 0) continue;
                    // World velocity → grid (voxels/sec), per-axis (matches WorldToVoxel scaling).
                    Vector3 lv = worldToGrid * velScratch[i];
                    Vector3 gridVel = new Vector3(lv.x / Mathf.Max(ws.x, 1e-4f) * r.x,
                                                  lv.y / Mathf.Max(ws.y, 1e-4f) * r.y,
                                                  lv.z / Mathf.Max(ws.z, 1e-4f) * r.z);
                    AccumPoint(cell, channelScratch[i], gridVel, inherit);
                }
            }

            // Mesh-shape emitters have no analytic SDF, so they are fed to the solver as GPU mesh/fragment sources.
            // Voronoi sources hand their live simulation buffers straight to FragmentSplat once release is armed;
            // intact/skinned meshes use MeshSplat. Neither path reads source positions back to the CPU.
            gpuMeshScratch.Clear();
            gpuFragmentScratch.Clear();
            var emitters = BrazePyroEmitter.Active;
            for (int s = 0; s < emitters.Count; s++)
            {
                var e = emitters[s];
                if (e == null || !e.isActiveAndEnabled || e.shape != BrazePyroEmitter.Shape.Mesh) continue;
                Vector3 gridJet = WorldVelToGrid(e.transform.forward * e.jetSpeed, r, worldToGrid, ws);
                Vector3 gridSourceVelocity = WorldVelToGrid(e.transform.rotation * e.sourceVelocity, r, worldToGrid, ws);
                float sourceTurbulenceVox = Mathf.Max(0f, e.sourceTurbulence) * voxPerWorld;
                Vector3 pushAccel = (worldToGrid * e.transform.forward).normalized * (e.motionForward * resolutionScale);

                if (e.TryGetGpuFragmentFluidEmission(out var gpuFragments))
                {
                    gpuFragments.jetVelocity = gridJet;
                    gpuFragments.sourceVelocity = gridSourceVelocity;
                    gpuFragments.sourceVelocityBlend = e.sourceVelocityBlend;
                    gpuFragments.sourceTurbulence = sourceTurbulenceVox;
                    gpuFragments.pushAccel = pushAccel;
                    gpuFragmentScratch.Add(gpuFragments);
                    continue;
                }
                if (e.TryGetGpuFluidEmission(out var gpuMesh))
                {
                    gpuMesh.jetVelocity = gridJet;
                    gpuMesh.sourceVelocity = gridSourceVelocity;
                    gpuMesh.sourceVelocityBlend = e.sourceVelocityBlend;
                    gpuMesh.sourceTurbulence = sourceTurbulenceVox;
                    gpuMesh.pushAccel = pushAccel;
                    gpuMeshScratch.Add(gpuMesh);
                }
            }

            // Impact-triggered fracture emission: the fracture owns a per-fragment GPU timer/rate mask written by
            // TagImpactFluid. Feed its live clump buffers directly into the established sparse fragment hash;
            // no impacted-id list or centroid ever crosses back to the CPU.
            var fractures = BrazeVoronoiFracture.Active;
            for (int s = 0; s < fractures.Count; s++)
            {
                var fracture = fractures[s];
                if (fracture == null || !fracture.TryGetImpactFluidGpuSource(this, out var gpuFragments))
                    continue;
                gpuFragments.impactBuoyancyScale = resolutionScale;
                gpuFragmentScratch.Add(gpuFragments);
            }

            // Always called — empty lists clear previously-set sources when emitters stop or switch paths.
            solver.SetMeshSources(gpuMeshScratch, gpuFragmentScratch, worldToVoxel);

            int count = pointCells.Count;
            if (count == 0) return 0;
            if (pointData.Length < count) pointData = new BrazeSolver.PointSource[Mathf.NextPowerOfTwo(count)];
            int idx = 0;
            int rxy = r.x * r.y;
            foreach (var kv in pointCells)
            {
                int cell = kv.Key, cz = cell / rxy, rem = cell - cz * rxy, cy = rem / r.x, cx = rem - cy * r.x;
                PointAccum a = kv.Value;
                float w = a.weight;
                // Mesh emission averages (one covered voxel = the authored rate, spacing-independent); other point
                // sources sum (more clumps = more fume).
                Vector3 channels = a.average && a.count > 0 ? a.channels / a.count : a.channels;
                pointData[idx++] = new BrazeSolver.PointSource
                {
                    posVox = new Vector3(cx + 0.5f, cy + 0.5f, cz + 0.5f),
                    channels = channels,
                    vel = w > 1e-6f ? a.velWeighted / w : Vector3.zero,        // mass-weighted average velocity
                    velBlend = w > 1e-6f ? a.blendWeighted / w : 0f,
                    buoyancy = w > 1e-6f ? a.buoyWeighted / w : 0f,            // mass-weighted per-source lift
                    buoyBlend = w > 1e-6f ? a.buoyBlendWeighted / w : 0f,
                };
            }
            return count;
        }

        // Map a world point to a flat grid cell index, or -1 if outside the grid. The transform state is
        // hoisted by the caller (same values/ops as WorldToVoxel, minus the per-point native transform
        // queries — the transform cannot change mid-gather).
        static int CellOf(Vector3 world, Vector3Int r, Vector3 origin, Quaternion invRot, Vector3 ws)
        {
            Vector3 rel = invRot * (world - origin);
            float vx = (rel.x / Mathf.Max(ws.x, 1e-4f) + 0.5f) * r.x;
            float vy = (rel.y / Mathf.Max(ws.y, 1e-4f) + 0.5f) * r.y;
            float vz = (rel.z / Mathf.Max(ws.z, 1e-4f) + 0.5f) * r.z;
            int cx = Mathf.FloorToInt(vx), cy = Mathf.FloorToInt(vy), cz = Mathf.FloorToInt(vz);
            if (cx < 0 || cy < 0 || cz < 0 || cx >= r.x || cy >= r.y || cz >= r.z) return -1;
            return cx + cy * r.x + cz * r.x * r.y;
        }

        // Accumulate a point's channels + velocity (momentum-weighted by emitted gas) into a cell.
        // buoyancy/buoyBlend let mesh emitters carry a per-source lift coefficient (see EmitPoints); static surface
        // masks and disintegrator clumps pass 0 → .w in the velocity field is left untouched (current behaviour).
        // average: when set, the cell emits the MEAN of its points' channels instead of the sum — so a mesh that
        // over-samples a voxel (spacing < voxel size) emits the authored rate regardless of how many sample points
        // land there. Spacing then controls coverage only, not intensity. Disintegrator/surface paths sum (false).
        void AccumPoint(int cell, Vector3 channels, Vector3 gridVel, float inherit, float buoyancy = 0f, float buoyBlend = 0f, bool average = false)
        {
            pointCells.TryGetValue(cell, out var a);
            a.channels += channels;
            float w = Mathf.Max(0f, channels.x + channels.y);   // gas mass (density + heat) as the velocity weight
            a.velWeighted += gridVel * w;
            a.blendWeighted += inherit * w;
            a.buoyWeighted += buoyancy * w;
            a.buoyBlendWeighted += buoyBlend * w;
            a.weight += w;
            a.count++;
            a.average |= average;
            pointCells[cell] = a;
        }

        // World-space velocity (m/s) → grid velocity (voxels/sec), per-axis — same mapping the point-source path uses.
        Vector3 WorldVelToGrid(Vector3 worldVel)
            => WorldVelToGrid(worldVel, solver.Res, Quaternion.Inverse(transform.rotation), WorldSize());

        static Vector3 WorldVelToGrid(Vector3 worldVel, Vector3Int r, Quaternion worldToGrid, Vector3 ws)
        {
            Vector3 lv = worldToGrid * worldVel;
            return new Vector3(lv.x / Mathf.Max(ws.x, 1e-4f) * r.x,
                               lv.y / Mathf.Max(ws.y, 1e-4f) * r.y,
                               lv.z / Mathf.Max(ws.z, 1e-4f) * r.z);
        }

        int GatherEmitters()
        {
            var emitters = BrazePyroEmitter.Active;
            Vector3Int r = solver.Res;
            float voxPerWorld = VoxPerWorld();
            float resolutionScale = PhysicsResolutionScale(r);
            Vector3 ws = WorldSize();
            Quaternion worldToGrid = Quaternion.Inverse(transform.rotation);
            gatheredDrag = 0f;
            gatheredJet = 0f;
            int count = 0;
            for (int i = 0; i < emitters.Count; i++)
            {
                var e = emitters[i];
                if (e == null || !e.isActiveAndEnabled) continue;

                e.Tick();   // advance burst window

                // Buoyancy is now PER-EMITTER (carried in _Vel.w with each emitter's gas — see Emit/Force), so it's
                // no longer relayed as a global uniform. Drag is still a whole-plume uniform (max across emitters).
                gatheredDrag = Mathf.Max(gatheredDrag, e.motionDrag);
                Vector3 gridJet = WorldVelToGrid(e.transform.forward * e.jetSpeed, r, worldToGrid, ws);   // inflow jet (world m/s → grid vox/s)
                Vector3 gridSourceVelocity = WorldVelToGrid(e.transform.rotation * e.sourceVelocity, r, worldToGrid, ws);
                float sourceTurbulenceVox = Mathf.Max(0f, e.sourceTurbulence) * voxPerWorld;
                gatheredJet = Mathf.Max(gatheredJet, Mathf.Max(gridJet.magnitude, gridSourceVelocity.magnitude + sourceTurbulenceVox * 1.75f));

                // Mesh emitters have no analytic SDF → they emit via GPU mesh sources (GatherPointSources),
                // not this dense Emit buffer. Tick + drag/max-speed tracking above still apply.
                if (e.shape == BrazePyroEmitter.Shape.Mesh) continue;

                Vector3 posVox = WorldToVoxel(e.transform.position);

                float radiusVox;
                Vector3 halfExtentVox;
                if (e.shape == BrazePyroEmitter.Shape.Box)
                {
                    // Box size = emitter Transform scale, as a fraction of the grid, in voxels. Divide by
                    // the SCALED world box so the container's Transform scale cancels (the emitter's
                    // lossyScale already includes it) → footprint scales with the effect, not against it.
                    Vector3 es = e.transform.lossyScale;
                    halfExtentVox = new Vector3(
                        es.x * 0.5f / Mathf.Max(ws.x, 1e-4f) * r.x,
                        es.y * 0.5f / Mathf.Max(ws.y, 1e-4f) * r.y,
                        es.z * 0.5f / Mathf.Max(ws.z, 1e-4f) * r.z);
                    float minExt = Mathf.Min(halfExtentVox.x, Mathf.Min(halfExtentVox.y, halfExtentVox.z));
                    radiusVox = Mathf.Max(1.5f, minExt * 0.3f);   // soft edge band
                }
                else
                {
                    radiusVox = Mathf.Max(e.radius * voxPerWorld, 0.5f);
                    float heightVox = Mathf.Max(e.height * voxPerWorld, 0.5f);
                    float tubeVox = Mathf.Max(e.tubeRadius * voxPerWorld, 0.5f);
                    halfExtentVox = e.shape switch
                    {
                        BrazePyroEmitter.Shape.Cylinder => new Vector3(radiusVox, heightVox * 0.5f, radiusVox),
                        BrazePyroEmitter.Shape.Cone => new Vector3(radiusVox, heightVox * 0.5f, radiusVox),
                        BrazePyroEmitter.Shape.Capsule => new Vector3(radiusVox, heightVox * 0.5f, radiusVox),
                        BrazePyroEmitter.Shape.Torus => new Vector3(radiusVox, tubeVox, radiusVox),
                        _ => Vector3.one * radiusVox,
                    };
                }

                if (count >= emitterData.Length)
                    System.Array.Resize(ref emitterData, emitterData.Length * 2);

                Vector3 channels = new Vector3(e.density, e.temperature, e.fuel);
                float emissionScale = e.EmissionScale;
                // Channels (what's emitted) follow the burst/emission window; MOTION + GAS do NOT. They act on the
                // hot smoke the emitter has already made — and on any other fluid in their footprint — independent
                // of whether this frame is an emitting frame, so a one-shot puff still stirs/lifts what it created
                // and a zero-channel emitter can be a pure air-mover. The gas burst self-gates on combustion anyway.
                emitterData[count++] = new BrazeSolver.GpuEmitter
                {
                    posVox = posVox,
                    radiusVox = radiusVox,
                    channels = channels * emissionScale,
                    shape = (float)e.shape,
                    halfExtentVox = halfExtentVox,
                    motionBuoyancy = e.motionBuoyancy * resolutionScale, // voxel accel → same world lift at every res
                    motionDir = (worldToGrid * e.transform.forward).normalized,
                    motionForward = e.motionForward * resolutionScale,
                    motionJet = gridJet,
                    sourceTurbulence = sourceTurbulenceVox,
                    sourceVelocity = gridSourceVelocity,
                    sourceVelocityBlend = e.sourceVelocityBlend,
                    motionRadial = e.motionRadial,
                    gasPressureBurst = e.gasPressureBurst,
                    gasSourceSpread = e.gasSourceSpread,
                    gasSourceSharpness = e.gasSourceSharpness,
                    rot = QuatVec4(worldToGrid * e.transform.rotation * e.AxisRotation()),   // shape follows the emitter's rotation + chosen shape axis (grid-relative)
                };
            }
            return count;
        }

        int GatherForces()
        {
            var forces = BrazePyroForce.Active;
            float voxPerWorld = VoxPerWorld();
            float resolutionScale = PhysicsResolutionScale(solver.Res);
            gatheredSplineVortexSampleCount = 0;
            int count = 0;
            for (int i = 0; i < forces.Count; i++)
            {
                var f = forces[i];
                if (f == null || !f.isActiveAndEnabled) continue;

                bool isDisc = f.type == BrazePyroForce.ForceType.Disc;
                bool isCauli = f.type == BrazePyroForce.ForceType.CauliBillows;
                bool isSpinner = f.type == BrazePyroForce.ForceType.Spinner;
                Vector3 posVox = WorldToVoxel(f.transform.position);
                Vector3 forceDir = isCauli ? Vector3.up : f.Direction;
                Vector3 dirLocal = (Quaternion.Inverse(transform.rotation) * forceDir).normalized;

                if (count >= forceData.Length)
                    System.Array.Resize(ref forceData, forceData.Length * 2);
                // Disc / Hold are inherently localized → always use their Radius (Infinite is ignored). A non-Sphere
                // falloff shape (Cylinder / Torus / Disc) is also inherently localized — it needs a finite radius to exist.
                bool nonSphere = !isDisc && f.shape != BrazePyroForce.FalloffShape.Sphere;
                bool localized = isDisc || isCauli || isSpinner || f.type == BrazePyroForce.ForceType.Hold || nonSphere;
                float radiusVox = Mathf.Max(0.01f, f.radius) * voxPerWorld;
                // The Disc's art knobs are normalized 0..1; convert to the raw physics gains the kernel expects here,
                // so the inspector stays clean. Other force types keep using `strength` directly.
                float strength = isDisc ? f.discSpread * BrazePyroForce.DiscSpreadGain * resolutionScale
                    : isSpinner ? f.spinnerSpeed * (Mathf.PI * 2f)
                    : f.type == BrazePyroForce.ForceType.Vortex ? f.strength
                    : f.type == BrazePyroForce.ForceType.Hold ? f.strength
                    : f.strength * resolutionScale;
                forceData[count++] = new BrazeSolver.GpuForce
                {
                    posVox = posVox,
                    type = (float)f.type,
                    dir = dirLocal,
                    // Spinner: Spin Speed is in turns/sec → the kernel wants angular rate in rad/sec.
                    strength = strength,
                    radiusVox = (f.infinite && !localized) ? 0f : radiusVox,
                    thickness = Mathf.Max(0.05f, f.discThickness) * radiusVox,   // fraction of radius
                    // scale: turbulence frequency normally; Lift/Jet reuse it as their approach Response (1/s).
                    scale = isDisc ? Mathf.Lerp(0.03f, 0.35f, Mathf.Clamp01(f.discDistortScale)) / resolutionScale
                                   : (f.type == BrazePyroForce.ForceType.Lift || f.type == BrazePyroForce.ForceType.Jet)
                                       ? f.targetResponse : f.turbulenceScale / resolutionScale,
                    // swirl slot: Disc twist normally; Spinner reuses it for the in-plane Flatten rate.
                    swirl = isSpinner ? f.spinnerFlatten * BrazePyroForce.SpinnerFlattenGain
                                      : f.discSwirl * BrazePyroForce.DiscSwirlGain * resolutionScale,
                    flatten = f.discFlatten * BrazePyroForce.DiscFlattenGain,
                    distort = f.discDistort * BrazePyroForce.DiscDistortGain * resolutionScale,
                    roll = f.discRoll * BrazePyroForce.DiscRollGain * resolutionScale,
                    hold = f.discHold * BrazePyroForce.DiscHoldGain,
                    dome = f.discDome,
                    power = f.discPower,
                    seal = f.discSeal,
                    layers = f.discLayers,
                    layerSpacing = Mathf.Max(0.1f, f.discLayerSpacing) * radiusVox,   // fraction of radius
                    taper = f.discTaper,
                    forcePad = isDisc
                        ? new Vector2(f.discCoreOpening, f.discCrownAttraction)
                        : isCauli
                            ? new Vector2(Mathf.Clamp(f.billowSmokeDeposit, 0f, 4f),
                                          Mathf.Clamp(f.billowFireDeposit, 0f, 4f))
                            : isSpinner
                                // x = Grip rate, y = Containment rate (both 1/s) — the spin lock + radial hold.
                                ? new Vector2(Mathf.Clamp01(f.spinnerGrip) * BrazePyroForce.SpinnerGripGain,
                                              Mathf.Clamp01(f.spinnerContain) * BrazePyroForce.SpinnerContainGain)
                                : Vector2.zero,
                    // Generic force-field shaping (Wind/Vortex/Turbulence/Gravity/Attract). Disc uses its own
                    // geometry, so it keeps the neutral defaults (inner 0, power 2 → the classic r² falloff).
                    falloffInner = isDisc ? 0f : f.falloffInner,
                    falloffPower = isDisc ? 2f : f.falloffPower,
                    billowLines  = isCauli ? Mathf.Clamp01(f.billowLines) : 0f,
                    billowMode   = isCauli ? f.BillowModeSign : 0f,             // +1 up / 0 in-place / -1 down
                    shape        = isDisc ? 0f : (float)f.shape,                 // 0 sphere / 1 cylinder / 2 torus / 3 disc
                    hole         = isDisc ? 0f : f.hole,
                    fieldHeight  = (isDisc ? 0f : Mathf.Max(0.01f, f.fieldHeight)) * voxPerWorld,
                    shapePad     = 0f,
                    // Cauli Billows (this force type only): other types → 0 strength so the kernel skips the boil.
                    billowStrength = isCauli ? f.billowStrength * BrazePyroForce.BillowGain * resolutionScale : 0f,
                    billowOrigin   = Mathf.Clamp01(f.billowOrigin),
                    // Cauli Size is literal lobe diameter in VOXELS. Speed is literal turnover rate.
                    billowScale    = f.BillowSizeVoxels * resolutionScale,
                    billowSpeed    = Mathf.Max(0f, f.billowSpeed),
                    // Vortex modes + axial column control (this force type only). Orbit radius / Lift are world-space
                    // speeds & distances → voxels. Grip is a normalized art knob converted to a velocity response.
                    vortexMode      = f.type == BrazePyroForce.ForceType.Vortex ? (float)f.vortexMotion : 0f,
                    vortexHold      = Mathf.Max(0f, f.vortexHold),
                    vortexOrbitR    = Mathf.Max(0f, f.vortexOrbitRadius) * voxPerWorld,
                    vortexLift      = f.vortexLift * voxPerWorld,
                    vortexAxialDamp = Mathf.Clamp01(f.vortexAxialDamp),
                    vortexLiftVar   = Mathf.Clamp01(f.vortexLiftVariation),
                    vortexGrip      = f.type == BrazePyroForce.ForceType.Vortex
                        ? Mathf.Clamp01(f.vortexGrip) * BrazePyroForce.VortexGripGain
                        : 0f,
                    // Rounded — timeline animation can hand us fractional counts, and a fractional harmonic
                    // has a discontinuity seam at θ = ±π.
                    vortexBlades    = f.type == BrazePyroForce.ForceType.Vortex
                        ? Mathf.Round(Mathf.Clamp(f.vortexBlades, 0f, 6f))
                        : 0f,
                };
            }

            // Spline Vortex is packed as one normal sparse force plus a compact float4 centreline, then evaluated at
            // the tail of the existing global-dynamics dispatch so conformation follows all chaotic motion. The tight
            // voxel AABB rejects almost every unrelated active cell before the short segment scan.
            var splineVortices = BrazeSplineVortex.Active;
            for (int i = splineVortices.Count - 1; i >= 0; i--)
                if (splineVortices[i] == null) splineVortices.RemoveAt(i);

            for (int i = 0; i < splineVortices.Count; i++)
            {
                BrazeSplineVortex vortex = splineVortices[i];
                if (vortex == null || !vortex.isActiveAndEnabled) continue;
                int requested = vortex.ResolvedSampleCount;
                if (requested < 2) continue;

                int sampleOffset = gatheredSplineVortexSampleCount;
                int required = sampleOffset + requested;
                if (splineVortexSampleData == null || splineVortexSampleData.Length < required)
                    System.Array.Resize(ref splineVortexSampleData,
                        Mathf.NextPowerOfTwo(Mathf.Max(required, 8)));

                int written = vortex.WriteWorldSamples(splineVortexSampleData, sampleOffset);
                if (written < 2) continue;

                Vector3 pathMin = new(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                Vector3 pathMax = new(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                float maxProfileRadius = 0f;
                for (int j = 0; j < written; j++)
                {
                    Vector4 sample = splineVortexSampleData[sampleOffset + j];
                    Vector3 p = WorldToVoxel(new Vector3(sample.x, sample.y, sample.z));
                    float radius = Mathf.Max(0f, sample.w) * voxPerWorld;
                    splineVortexSampleData[sampleOffset + j] = new Vector4(p.x, p.y, p.z, radius);
                    pathMin = Vector3.Min(pathMin, p);
                    pathMax = Vector3.Max(pathMax, p);
                    maxProfileRadius = Mathf.Max(maxProfileRadius, radius);
                }

                float captureVox = Mathf.Max(0.01f, vortex.captureMargin) * voxPerWorld;
                float wallHalfVox = vortex.conformMode == BrazeSplineVortex.ConformMode.Wall
                    ? Mathf.Max(0.01f, vortex.wallThickness) * voxPerWorld * 0.5f
                    : 0f;
                Vector3 boundCentre = (pathMin + pathMax) * 0.5f;
                Vector3 boundExtents = (pathMax - pathMin) * 0.5f
                                     + Vector3.one * (maxProfileRadius + wallHalfVox + captureVox + 1f);
                if (!SplineVortexBoundsIntersectsGrid(boundCentre, boundExtents, solver.Res))
                    continue;   // leave sample count unchanged: the next vortex overwrites this staging range

                if (count >= forceData.Length)
                    System.Array.Resize(ref forceData, forceData.Length * 2);

                forceData[count++] = new BrazeSolver.GpuForce
                {
                    posVox = boundCentre,
                    type = 11f,
                    dir = boundExtents,                              // tight AABB early rejection in the guide
                    strength = vortex.spinSpeed * (Mathf.PI * 2f),  // signed radians/sec
                    radiusVox = 0f,                                 // bypass generic spherical falloff
                    thickness = captureVox,
                    scale = Mathf.Clamp01(vortex.conform),
                    swirl = vortex.flowSpeed * voxPerWorld,
                    flatten = Mathf.Clamp01(vortex.spinGrip),
                    distort = Mathf.Clamp01(vortex.turbulence),
                    roll = 1f / Mathf.Max(vortex.turbulenceScale * voxPerWorld, 0.01f),
                    hold = Mathf.Clamp01(vortex.containment),
                    dome = Mathf.Max(0f, vortex.eyeOutflow) * voxPerWorld,
                    power = Mathf.Clamp01(vortex.flowGrip),
                    seal = Mathf.Max(0f, vortex.tailSuction) * voxPerWorld,
                    layers = sampleOffset,
                    layerSpacing = written,
                    taper = vortex.reversePath ? 1f : 0f,
                    forcePad = new Vector2(Mathf.Clamp01(vortex.eyeOpening), Mathf.Clamp(vortex.eyeLength, 0.01f, 1f)),
                    falloffInner = (float)vortex.conformMode,
                    falloffPower = Mathf.Clamp01(vortex.armStrength),
                    billowLines = Mathf.Clamp(vortex.spiralArms, 0, 8),
                    billowMode = Mathf.Clamp(vortex.armTwist, -8f, 8f),
                    shape = Mathf.Clamp01(vortex.eyeFlow),          // terminal axial-flow retention at the eye
                    hole = Mathf.Clamp(vortex.tailLength, 0.01f, 1f),
                    fieldHeight = Mathf.Max(0.01f, vortex.wallThickness) * voxPerWorld,
                };
                gatheredSplineVortexSampleCount += written;
            }
            return count;
        }

        /// <summary>Tight conservative CPU cull used before uploading a Spline Vortex to this container.</summary>
        internal static bool SplineVortexBoundsIntersectsGrid(Vector3 centre, Vector3 extents, Vector3Int resolution)
        {
            Vector3 lo = centre - extents;
            Vector3 hi = centre + extents;
            return hi.x >= 0f && hi.y >= 0f && hi.z >= 0f
                && lo.x <= resolution.x && lo.y <= resolution.y && lo.z <= resolution.z;
        }

        int GatherObstacles()
        {
            var obstacles = BrazeObstacle.Active;
            Vector3Int r = solver.Res;
            Vector3 ws = WorldSize();
            // Obstacles are real world geometry → map their ACTUAL world size into voxels (cubic).
            float voxPerWorld = r.x / Mathf.Max(ws.x, 1e-4f);
            int count = 0;
            for (int i = 0; i < obstacles.Count; i++)
            {
                var o = obstacles[i];
                if (o == null || !o.isActiveAndEnabled) continue;

                primBuf.Clear();
                o.CollectPrimitives(primBuf);   // compound colliders, or a single mesh/manual shape
                for (int j = 0; j < primBuf.Count; j++)
                {
                    var prim = primBuf[j];
                    Vector3 posVox = WorldToVoxel(prim.center);
                    // All non-spheres are oriented in their primitive-local frame. The grid has cubic voxels, so
                    // one uniform pitch converts radii and half-extents without expanding the 48-byte GPU record.
                    float radiusVox = prim.shape == BrazeObstacle.Shape.Box
                        ? 0f : Mathf.Max(prim.radius * voxPerWorld, 0.5f);
                    Vector3 halfExtentVox = prim.half * voxPerWorld;
                    if (prim.shape == BrazeObstacle.Shape.Sphere)
                        halfExtentVox = Vector3.one * radiusVox;
                    else if (prim.shape == BrazeObstacle.Shape.Capsule)
                        halfExtentVox.y = Mathf.Max(halfExtentVox.y, radiusVox);
                    else if (prim.shape == BrazeObstacle.Shape.Cylinder ||
                             prim.shape == BrazeObstacle.Shape.Cone)
                        halfExtentVox.y = Mathf.Max(halfExtentVox.y, 0.5f);
                    else if (prim.shape == BrazeObstacle.Shape.Torus)
                    {
                        // Torus: radiusVox = major radius, halfExtentVox.x = tube/minor radius.
                        radiusVox = Mathf.Max(prim.radius * voxPerWorld, 0f);
                        float minor = Mathf.Max(prim.half.x * voxPerWorld, 0.5f);
                        halfExtentVox = Vector3.one * minor;
                    }

                    if (count >= obstacleData.Length)
                        System.Array.Resize(ref obstacleData, obstacleData.Length * 2);

                    obstacleData[count++] = new BrazeSolver.GpuObstacle
                    {
                        posVox = posVox,
                        radiusVox = radiusVox,
                        halfExtentVox = halfExtentVox,
                        shape = (float)prim.shape,
                        rot = new Vector4(prim.rot.x, prim.rot.y, prim.rot.z, prim.rot.w),
                    };
                }
            }
            return count;
        }

        // Container bounds outline — a unit cube in local space, scaled by the transform.
        static readonly Color OutlineColor = new Color(1f, 0.6f, 0.2f, 0.5f);
        static readonly Color OutlineSelected = new Color(1f, 0.65f, 0.25f, 1f);

        void OnGUI()
        {
            if (!showStats || solver == null) return;
            if (hudStyle == null)
                hudStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 22,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = Color.white },
                };

            // Build the stats string once per frame: OnGUI fires for EVERY IMGUI event (Layout + Repaint ≥ 2×
            // per frame), and this interpolation chain was ~1.5–2 KB of GC per event with the HUD on by default.
            if (hudTextFrame != Time.frameCount)
            {
                hudTextFrame = Time.frameCount;
                int act = solver.ActiveBricks, raw = solver.RawBricks, tot = Mathf.Max(1, solver.TotalBricks);
                Vector3Int r = solver.Res;
                string detail = renderDetail ? "Render Detail" : "None";
                Vector3 cw = Vector3.Scale(WorldSize(), new Vector3(1f / r.x, 1f / r.y, 1f / r.z));
                hudText = $"BrazeFX\n" +
                          $"FPS {fps:0}   ({1000f / Mathf.Max(fps, 1e-3f):0.0} ms)\n" +
                          $"Grid {r.x}×{r.y}×{r.z}\n" +
                          $"Mode {qualityMode}   Substeps {ScheduledSubsteps}/{RequiredCflSubsteps}" +
                          (CflBudgetLimited ? "  LIMITED\n" : "\n") +
                          $"Cell {CellLabel(cw)}\n" +
                          $"Detail {detail}\n" +
                          $"VRAM ~{FormatBytes(EstimateVramBytes())}\n" +
                          $"Raw bricks {raw}/{tot}  ({100f * raw / tot:0}%)\n" +
                          $"Active + halo {act}/{tot}  ({100f * act / tot:0}%)";
            }

            GUI.Box(new Rect(10, 10, 430, 282), GUIContent.none);
            GUI.Label(new Rect(22, 18, 410, 270), hudText, hudStyle);
        }

        static readonly Color CellColor = new Color(0.35f, 0.8f, 1f, 1f);   // cell-grid overlay lines (alpha set live)
        static readonly Color RulerColor = new Color(1f, 0.85f, 0.3f, 0.95f);   // dimension ruler (feet)

        static readonly Color BrickRawColor = new Color(1f, 0.45f, 0.12f, 1f);   // raw content (alpha set live)
        static readonly Color BrickHaloColor = new Color(0.25f, 0.6f, 1f, 1f);   // halo padding (alpha set live)

        void OnDrawGizmos()
        {
            DrawOutline(OutlineColor);
            DrawCellGrid();
            DrawBricks();
            DrawRuler();
        }

        // DEBUG: draw the sparse-solver occupancy bricks aligned to the sim domain — orange = raw (above-
        // threshold content), blue = halo padding (dilation, where the plume can advect next). This is the
        // proof tool for the indirect-dispatch / selective-detail work: you SEE what the solver pays for.
        void DrawBricks()
        {
            if (!showBricks || solver == null || !solver.BrickDebugValid) return;
            uint[] flags = solver.BrickFlags;
            if (flags == null) return;

            Vector3Int o = solver.OccRes, r = solver.Res;
            int bs = solver.BrickSize;
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, WorldSize());
            Color rawFill = new Color(BrickRawColor.r, BrickRawColor.g, BrickRawColor.b, brickOpacity);
            Color haloFill = new Color(BrickHaloColor.r, BrickHaloColor.g, BrickHaloColor.b, brickOpacity * 0.55f);

            int idx = 0;
            for (int z = 0; z < o.z; z++)
            for (int y = 0; y < o.y; y++)
            for (int x = 0; x < o.x; x++, idx++)
            {
                uint f = flags[idx];
                if ((f & 2u) == 0u) continue;              // not active → empty space → not drawn (that's the point)
                bool isRaw = (f & 1u) != 0u;

                // Brick AABB in the box's normalized local space (last brick clamps to the grid edge).
                Vector3 lo = new Vector3((x * bs) / (float)r.x, (y * bs) / (float)r.y, (z * bs) / (float)r.z) - Vector3.one * 0.5f;
                Vector3 hi = new Vector3(Mathf.Min((x + 1) * bs, r.x) / (float)r.x,
                                         Mathf.Min((y + 1) * bs, r.y) / (float)r.y,
                                         Mathf.Min((z + 1) * bs, r.z) / (float)r.z) - Vector3.one * 0.5f;
                Vector3 c = 0.5f * (lo + hi), sz = hi - lo;

                Gizmos.color = isRaw ? rawFill : haloFill;
                Gizmos.DrawCube(c, isRaw ? sz : sz * 0.92f);   // halo slightly inset so raw reads on top
                Gizmos.color = isRaw ? BrickRawColor : new Color(BrickHaloColor.r, BrickHaloColor.g, BrickHaloColor.b, 0.5f);
                Gizmos.DrawWireCube(c, sz);
            }
        }

        void OnDrawGizmosSelected()
        {
            DrawOutline(OutlineSelected);
            DrawCellGrid();
        }

        // 2D cell-grid preview on the container's -Z grid wall (x across, y up): one quad per simulation
        // voxel, so you can read the real world cell size. Lives in normalized local space (unit cube), so
        // the matrix scale by WorldSize gives the true cell dimensions. `size` and Transform scale feed the
        // same WorldSize/Res, so the grid is identical whichever lever you use to resize.
        void DrawCellGrid()
        {
            if (!showCellGrid || cellGridOpacity <= 0f) return;

            Matrix4x4 trs = Matrix4x4.TRS(transform.position, transform.rotation, WorldSize());
            Vector3Int r = (solver != null) ? solver.Res : ComputeRes();
            Gizmos.matrix = trs;
            Gizmos.color = new Color(CellColor.r, CellColor.g, CellColor.b, cellGridOpacity);
            const float z = -0.5f;
            for (int i = 0; i <= r.x; i++)
            {
                float x = -0.5f + (float)i / r.x;
                Gizmos.DrawLine(new Vector3(x, -0.5f, z), new Vector3(x, 0.5f, z));
            }
            for (int j = 0; j <= r.y; j++)
            {
                float y = -0.5f + (float)j / r.y;
                Gizmos.DrawLine(new Vector3(-0.5f, y, z), new Vector3(0.5f, y, z));
            }
        }

        void DrawOutline(Color outline)
        {
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, WorldSize());
            Gizmos.color = outline;
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
        }

        // Dimension ruler (FEET) along the container's front (−Z) bottom + left edges, so you can read how tall and
        // how wide the box is at a glance. World-space (Handles), so the tick spacing stays uniform regardless of the
        // box's (possibly non-uniform) Size/scale. WorldSize() already folds in BOTH the Size vector AND the
        // Transform scale, so the readout is the true world height/width.
        void DrawRuler()
        {
#if UNITY_EDITOR
            if (!showRuler || rulerOpacity <= 0f) return;
            const float M_TO_FT = 3.28084f;
            Matrix4x4 m = Matrix4x4.TRS(transform.position, transform.rotation, WorldSize());

            Vector3 origin = m.MultiplyPoint3x4(new Vector3(-0.5f, -0.5f, -0.5f));   // bottom-left-front (shared)
            Vector3 right  = m.MultiplyPoint3x4(new Vector3( 0.5f, -0.5f, -0.5f));   // bottom-right-front
            Vector3 top    = m.MultiplyPoint3x4(new Vector3(-0.5f,  0.5f, -0.5f));   // top-left-front

            Vector3 widthDir = right - origin, heightDir = top - origin;
            Vector3 rightN = widthDir.normalized, upN = heightDir.normalized;
            float tickLen = 0.04f * Mathf.Max(widthDir.magnitude, heightDir.magnitude);

            Color rc = RulerColor; rc.a *= rulerOpacity;
            Handles.color = rc;
            // Label styles carry their own colour, so fade them via the style (Handles.color doesn't affect labels).
            EnsureRulerStyles();
            Color tcol = rulerTickStyle.normal.textColor;  tcol.a = rulerOpacity; rulerTickStyle.normal.textColor  = tcol;
            Color bcol = rulerTotalStyle.normal.textColor; bcol.a = rulerOpacity; rulerTotalStyle.normal.textColor = bcol;

            DrawAxisRuler(origin, right, -upN,    widthDir.magnitude  * M_TO_FT, tickLen, "W", rulerTickStyle, rulerTotalStyle); // width: bottom, ticks down
            DrawAxisRuler(origin, top,   -rightN, heightDir.magnitude * M_TO_FT, tickLen, "H", rulerTickStyle, rulerTotalStyle); // height: left, ticks left
#endif
        }

#if UNITY_EDITOR
        GUIStyle rulerTickStyle, rulerTotalStyle;
        bool rulerStylesReady;

        // Build the ruler label styles ONLY when a GUI skin is available. EditorStyles.* is a NAMED-style lookup
        // that throws "Unable to use a named GUIStyle without a current skin" when touched with no skin set — and
        // OnDrawGizmos (our caller) can run in exactly that state. Until a skin exists we hand back safe unnamed
        // placeholders (rulerStylesReady stays false), then upgrade to the real EditorStyles on a later repaint.
        void EnsureRulerStyles()
        {
            if (rulerStylesReady) return;
            if (GUI.skin == null)
            {
                rulerTickStyle ??= new GUIStyle();
                rulerTotalStyle ??= new GUIStyle();
                return;
            }
            rulerTickStyle = new GUIStyle(EditorStyles.whiteMiniLabel);
            rulerTotalStyle = new GUIStyle(EditorStyles.whiteBoldLabel);
            rulerStylesReady = true;
        }

        // One ruler along [a→b]: the spine line, round-foot ticks, per-tick labels, and a bold total at the far end.
        void DrawAxisRuler(Vector3 a, Vector3 b, Vector3 outDir, float totalFt, float tickLen, string tag,
                           GUIStyle tickStyle, GUIStyle totalStyle)
        {
            Handles.DrawLine(a, b);
            float step = NiceFootStep(totalFt);
            int n = Mathf.FloorToInt(totalFt / step + 1e-3f);
            for (int i = 0; i <= n; i++)
            {
                float ft = i * step;
                Vector3 p = Vector3.Lerp(a, b, ft / Mathf.Max(totalFt, 1e-4f));
                Handles.DrawLine(p, p + outDir * tickLen);
                if (ft > 1e-3f && ft < totalFt - step * 0.5f)
                    Handles.Label(p + outDir * (tickLen * 1.15f), $"{ft:0.#}", tickStyle);
            }
            Handles.Label(b + outDir * (tickLen * 1.3f), $"{tag} {totalFt:0.#} ft", totalStyle);
        }
#endif

        // Compact world-unit cell label for the stats HUD — single figure when (near-)cubic, else all three axes.
        static string CellLabel(Vector3 cw)
        {
            float mn = Mathf.Min(cw.x, Mathf.Min(cw.y, cw.z));
            float mx = Mathf.Max(cw.x, Mathf.Max(cw.y, cw.z));
            return (mx - mn) <= mx * 0.02f
                ? $"{cw.x:0.###} m"
                : $"{cw.x:0.###}×{cw.y:0.###}×{cw.z:0.###} m";
        }

#if UNITY_EDITOR

        // Round foot step on a 1·2·5·10ⁿ ladder that keeps the ruler to ~5–10 divisions (sub-foot for tiny boxes).
        static float NiceFootStep(float totalFt)
        {
            float target = Mathf.Max(totalFt, 1e-3f) / 8f;
            float mag = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(target)));
            float norm = target / mag;
            float step = norm <= 1f ? 1f : norm <= 2f ? 2f : norm <= 5f ? 5f : 10f;
            return step * mag;
        }
#endif
    }
}
