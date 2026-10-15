using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>Implemented by the dedicated authoring components that lock the shared pyro-force engine to one type.</summary>
    public interface IBrazeFixedPyroForce
    {
        BrazePyroForce.ForceType FixedForceType { get; }
    }

    /// <summary>
    /// A spatial FLUID/pyro force field that shapes the simulated smoke & fire (wind / vortex / turbulence /
    /// gravity / attract). Place it ANYWHERE in the scene — it registers itself and every <see cref="BrazeContainer"/>
    /// picks it up by world position, applying its push within the falloff radius. Direction usually comes from this
    /// transform's forward axis; Cauli Billows uses vertical Up/Down. (Distinct from the PARTICLE forces, which steer
    /// the particle system — see <see cref="BrazeParticleForce"/>.)
    /// </summary>
    [AddComponentMenu("BrazeFX/Fire/Forces/Advanced/Generic Pyro Force", 99)]
    [ExecuteAlways]   // self-register in edit mode too, so the timeline's edit-mode sim sees it
    public class BrazePyroForce : MonoBehaviour, IBrazeAnimatableFilter
    {
        /// <summary>All enabled pyro forces in the scene (registry — no hierarchy constraint).</summary>
        public static readonly List<BrazePyroForce> Active = new();

        public enum ForceType
        {
            Wind = 0,        // uniform push along this transform's forward
            Vortex = 1,      // swirl around the forward axis
            Turbulence = 2,  // curl-noise churn
            Gravity = 3,     // uniform push along forward (point it down for gravity)
            Attract = 4,     // pull toward the centre (negative strength = repel)
            Disc = 5,        // push smoke OUTWARD in the plane ⊥ forward + flatten it → shapes a cap/disc
            Hold = 6,        // damp velocity inside the radius → settle/hold a region in place
            CauliBillows = 7,// boil whatever fluid is in the field into rising/sinking/in-place cauliflower lobes
            Lift = 8,        // raise whatever fluid is in the field UP to a target rise speed (an updraft; − = downdraft)
            Jet = 9,         // drive fluid in the field to a target speed ALONG this transform's forward axis (aimable)
            Spinner = 10,    // localized solid-body rotation: grip the fume and spin it about the forward axis like a tyre
        }

        /// <summary>Vortex motion modes (parity with the particle <see cref="BrazeVortex"/>).</summary>
        public enum VortexMotion
        {
            Swirl = 0,       // spin the smoke around the axis at the angular Spin Speed (Strength) — stable circling
            Inward = 1,      // swirl + suck the smoke toward the axis (a tightening tornado)
            Orbit = 2,       // swirl + hold the smoke at a target radius and flatten the column
        }

        /// <summary>Which way the Cauli Billows transport the fluid as they boil.</summary>
        public enum BillowMotion
        {
            Up = 0,          // lobes climb (a rising boil — mushroom cap / pyrocumulus)
            InPlace = 1,     // pure churn, no net transport (a roiling body that stays put)
            Down = 2,        // lobes sink (a descending / collapsing boil)
        }

        /// <summary>Falloff-field volume for the localized forces (usually symmetric about the forward axis; Cauli
        /// Billows uses the vertical axis). A central Hole turns Sphere→shell and Cylinder→tube, letting a force hit only the
        /// OUTER region (e.g. a plume's edges); the Torus is inherently holed by its ring geometry.</summary>
        public enum FalloffShape
        {
            Sphere = 0,      // a ball (Hole → a spherical shell)
            Cylinder = 1,    // a column along forward (Hole → an annular tube / pipe)
            Torus = 2,       // a ring/donut in the plane ⊥ forward (Hole = the donut opening)
            Disc = 3,        // a flat oblate pancake: wide ⊥ forward, thin along it (Height); softer than a cylinder
        }

        public ForceType type = ForceType.Wind;
        [Tooltip("Force magnitude at the container's Physics Reference Resolution. Acceleration-style forces scale with " +
                 "grid resolution to preserve world motion. Negative on Attract = repel. On Hold it's the settle rate.")]
        [BrazeAnimatable("Force/Strength", -50f, 50f)] public float strength = 4f;

        [Tooltip("If true, the force fills the whole container (no radial falloff). Turn OFF for Disc / Hold — they " +
                 "are meant to be localized regions.")]
        public bool infinite = true;
        [Tooltip("Falloff radius in world units (used when not infinite, or for point forces).")]
        [BrazeAnimatable("Force/Radius", 0.1f, 30f)] public float radius = 0.5f;

        [Tooltip("Spatial frequency for the Turbulence type.")]
        [BrazeAnimatable("Turbulence/Scale", 0.1f, 10f)] public float turbulenceScale = 3f;

        // ── VORTEX controls (only used when Type = Vortex) ───────────────────────────────────────────────
        // Parity with the particle Vortex: three motions + an axial column control. Swirl drives localized fume
        // toward angular rotation about the axis (stable circling, no centrifugal fling-out); Inward and Orbit add
        // radial pull. Lift / Axial Damp drive and shape the column height along the axis.
        [Tooltip("VORTEX — Motion: Swirl = spin the smoke around the axis at the Spin Speed (Strength); " +
                 "Inward = swirl + suck toward the axis; Orbit = swirl + hold the smoke at a target radius and flatten it.")]
        public VortexMotion vortexMotion = VortexMotion.Swirl;
        [Tooltip("VORTEX — Hold: how firmly Inward/Orbit guide smoke to the axis / target radius. Higher = snappier.")]
        [Range(0f, 20f)] [BrazeAnimatable("Vortex/Hold", 0f, 20f)] public float vortexHold = 4f;
        [Tooltip("VORTEX — Grip: in a finite vortex field, clips outward escape near the edge without pulling inward " +
                 "or damping spin. 0 = free, 1 = stronger boundary containment.")]
        [Range(0f, 1f)] [BrazeAnimatable("Vortex/Grip")] public float vortexGrip = 0.35f;
        [Tooltip("VORTEX — Blades: stamps this many lumps into the swirl that ride around with it, so the rotation " +
                 "reads like a wheel/propeller. Without them, evenly mixed smoke spinning in place looks static — " +
                 "circular streamlines advect a uniform ring onto itself. 0 = smooth (off). Swirl motion, finite field.")]
        [Range(0f, 6f)] [BrazeAnimatable("Vortex/Blades", 0f, 6f)] public float vortexBlades = 3f;
        [Tooltip("VORTEX — Orbit Radius: the radius the smoke is held at in Orbit mode (world units). Clamped to " +
                 "stay inside a localized field area.")]
        [Min(0f)] [BrazeAnimatable("Vortex/Orbit Radius", 0f, 20f)] public float vortexOrbitRadius = 1f;
        [Tooltip("VORTEX — Lift: rise/sink speed along the swirl axis (world units/sec; + along the arrow, − opposite). " +
                 "Drives the column on its own. 0 = no driven axial flow (a free column).")]
        [BrazeAnimatable("Vortex/Lift", -20f, 20f)] public float vortexLift = 0f;
        [Tooltip("VORTEX — Axial Damp: how firmly the along-axis speed is held to Lift, and how strongly a free " +
                 "column's drift is settled. 0 = free tall column; higher = snappier. With Lift = 0 and Damp > 0 " +
                 "the column flattens to a disc.")]
        [Range(0f, 1f)] [BrazeAnimatable("Vortex/Axial Damp")] public float vortexAxialDamp = 0f;
        [Tooltip("VORTEX — Lift Variation: spatial randomness on Lift so different columns rise at different rates — " +
                 "a softer, organic column instead of a flat lockstep one. 0 = uniform.")]
        [Range(0f, 1f)] [BrazeAnimatable("Vortex/Lift Variation")] public float vortexLiftVariation = 0f;

        [Tooltip("LIFT / JET — Response: how quickly fluid in the field is driven to the target speed (Strength). " +
                 "Higher snaps the smoke to speed fast; lower eases it. It only ever drives toward the target in the " +
                 "force's direction — fluid already moving faster that way is left alone.")]
        [Range(0.5f, 20f)] [BrazeAnimatable("Force/Response", 0.5f, 20f)] public float targetResponse = 6f;

        // ── FORCE FIELD controls (Wind / Vortex / Turbulence / Gravity / Attract — the falloff-field forces) ──
        // These shape how the force fades across its Radius and whether it contains the fume it acts on.
        [Tooltip("FORCE FIELD — Shape: the volume the force acts in. Sphere = ball, Cylinder = column along forward, " +
                 "Torus = ring/donut. Cylinder/Torus auto-localize (need a finite Radius).")]
        public FalloffShape shape = FalloffShape.Sphere;
        [Tooltip("FORCE FIELD — Hole: carves the CENTRE out so the force only hits the outer region (Sphere → shell, " +
                 "Cylinder → tube/pipe, Torus → the donut opening). 0 = solid. Great for affecting a plume's edges " +
                 "while leaving its core free.")]
        [Range(0f, 0.95f)] [BrazeAnimatable("Field/Hole")] public float hole = 0f;
        [Tooltip("FORCE FIELD — Height: the Cylinder's half-height along forward, world units (unused by Sphere/Torus).")]
        [Min(0.01f)] [BrazeAnimatable("Field/Height", 0.1f, 30f)] public float fieldHeight = 1f;
        [Tooltip("FORCE FIELD — Core: fraction of the Radius held at FULL strength before the falloff begins. " +
                 "0 = peaks only at the centre; 1 = flat-top field that drops sharply at the edge.")]
        [Range(0f, 1f)] [BrazeAnimatable("Field/Core")] public float falloffInner = 0f;
        [Tooltip("FORCE FIELD — Falloff Curve: how strength fades from the core to the edge. 1 = linear, " +
                 "2 = smooth (default), higher = strength stays high then drops off near the rim.")]
        [Range(0.25f, 4f)] [BrazeAnimatable("Field/Falloff Curve")] public float falloffPower = 2f;

        // ── DISC force controls (only used when Type = Disc) ──────────────────────────────────────────
        // All disc knobs are normalized 0..1 (signed -1..1 for Roll/Swirl). The raw physics gains are derived
        // from these in BrazeContainer.GatherForces, so the artist only sees clean values.
        [Tooltip("DISC FORCE — Spread: outward force along the disc shelves. 0 = no flare, 1 = strong cap widening.")]
        [Range(0f, 1f)] [BrazeAnimatable("Disc/Spread")] public float discSpread = 0.3f;
        [Tooltip("DISC FORCE — Conform Strength: damps vertical/up-down motion in the disc band so fume settles into " +
                 "the shelves instead of rising through them.")]
        [Range(0f, 1f)] [BrazeAnimatable("Disc/Conform")] public float discFlatten = 0.8f;
        [Tooltip("DISC FORCE — Chimney/Crown Suction: one-way guide force that pulls broad fume inward/up through " +
                 "the central opening, then out into the curved crown. It never pushes fume downward from above.")]
        [Range(0f, 1f)] [BrazeAnimatable("Disc/Suction")] public float discCrownAttraction = 0.65f;
        [Tooltip("DISC FORCE — Inert Hold: extra velocity damping inside the cap. Lets smoke/fire settle into " +
                 "the disc shape without adding up/down force. 0 = free.")]
        [Range(0f, 1f)] [BrazeAnimatable("Disc/Hold")] public float discHold = 0.35f;
        [Tooltip("DISC FORCE — Seal: top lid + rim wall containment. 0 = open/leaky, 1 = contained.")]
        [Range(0f, 1f)] [BrazeAnimatable("Disc/Seal")] public float discSeal = 1f;

        [Tooltip("DISC FORCE — Rim Roll: curls the rim flow in the disc plane. Signed. 0 = none.")]
        [Range(-1f, 1f)] [BrazeAnimatable("Disc/Roll")] public float discRoll = 0.25f;
        [Tooltip("DISC FORCE — Twist: rotation around the disc axis. Signed. 0 = none.")]
        [Range(-1f, 1f)] [BrazeAnimatable("Disc/Twist")] public float discSwirl = 0f;

        // ── DISC shape carving (gizmo/target shape) ───────────────────────────────────────────────────
        [Tooltip("DISC SHAPE — Dome Height: flat disc (0) -> tall rounded mushroom-cap top (1). Cap height scales " +
                 "with Radius.")]
        [Range(-1f, 1.5f)] [BrazeAnimatable("Disc/Dome")] public float discDome = 0.7f;
        [Tooltip("DISC — Layers: stack this many disc sheets to build a tiered 'wedding-cake' cap (top sheets dome " +
                 "more → a rounded crown). 1 = a single disc.")]
        [Range(1, 8)] public int discLayers = 1;
        [Tooltip("DISC — Layer Spacing: gap between stacked sheets, as a fraction of Radius. Only used when Layers > 1.")]
        [Range(0.1f, 1.5f)] [BrazeAnimatable("Disc/Layer Spacing")] public float discLayerSpacing = 0.4f;
        [Tooltip("DISC — Taper: how much narrower the TOP sheets are vs the bottom → the wide-base, round-crown " +
                 "mushroom silhouette. 0 = a straight stack, 1 = crown pinches to a point. Only used when Layers > 1.")]
        [Range(0f, 1f)] [BrazeAnimatable("Disc/Taper")] public float discTaper = 0.5f;
        [Tooltip("DISC — Core Opening: open central chimney as a fraction of Radius. Lets fire/smoke rise through " +
                 "the middle of stacked torus-like discs before the top dome curves it outward.")]
        [Range(0f, 0.8f)] [BrazeAnimatable("Disc/Core Opening")] public float discCoreOpening = 0.3f;

        [Tooltip("DISC (advanced) — Thickness: disc half-height as a fraction of Radius. Thin = a crisp pancake.")]
        [Range(0.05f, 1f)] [BrazeAnimatable("Disc/Thickness")] public float discThickness = 0.2f;

        // ── DISC global/detail controls ────────────────────────────────────────────────────────────────
        [Tooltip("DISC FORCE — Power: master force multiplier over the whole disc. 1 = normal.")]
        [Range(0f, 2f)] [BrazeAnimatable("Disc/Power")] public float discPower = 1f;
        [Tooltip("DISC DETAIL — Distort: curl-noise for ragged, organic edges. 0 = smooth.")]
        [Range(0f, 1f)] [BrazeAnimatable("Disc/Distort")] public float discDistort = 0.1f;
        [Tooltip("DISC DETAIL — Edge Frequency: normalized curl-noise size for the Disc edge. Low = broad soft " +
                 "detail, high = finer roughness. Kept separate from raw Turbulence Scale to avoid voxel stutter.")]
        [Range(0f, 1f)] [BrazeAnimatable("Disc/Edge Freq")] public float discDistortScale = 0.25f;

        // ── CAULI BILLOWS controls (only used when Type = Cauli Billows) ─────────────────────────────────
        // A dedicated boil force: it rolls whatever fluid sits inside its field area (Sphere / Disc / Torus)
        // into rising, sinking, or in-place cauliflower lobes, with optional gated smoke/fire deposition.
        [Tooltip("CAULI BILLOWS — Motion: Up = lobes climb vertically (a rising mushroom boil), In Place = roils " +
                 "without moving, Down = lobes sink/collapse.")]
        public BillowMotion billowMotion = BillowMotion.Up;
        [Tooltip("CAULI — Strength: how hard the fluid boils / churns. 0 = still, 1 = strongly boiling.")]
        [Range(0f, 1f)] [BrazeAnimatable("Cauli/Strength")] public float billowStrength = 0.5f;
        [Tooltip("CAULI — Origin: where the boil is emphasised. 0 = the centre of the field, 1 = the outer rim.")]
        [Range(0f, 1f)] [BrazeAnimatable("Cauli/Origin")] public float billowOrigin = 0.5f;
        [Tooltip("CAULI — Size: lobe diameter in VOXELS. This is literal: 8 = tight churn, 32 = large explosion " +
                 "cauliflower, 64+ = very broad pyrocumulus rolls.")]
        [Range(2f, 96f)] [BrazeAnimatable("Cauli/Size", 2f, 96f)] public float billowScale = 32f;
        [SerializeField, HideInInspector] float billowSizeMin = 4f;   // legacy 0..1 Size remap endpoint
        [SerializeField, HideInInspector] float billowSizeMax = 32f;  // legacy 0..1 Size remap endpoint
        [Tooltip("CAULI — Speed: lobe turnover rate. 0 = frozen, 1 = natural boil, 4 = violent rapid churn.")]
        [Range(0f, 4f)] [BrazeAnimatable("Cauli/Speed", 0f, 4f)] public float billowSpeed = 1f;
        [Tooltip("CAULI — Lines: mask the boil into discrete vertical striations (the radial fingers of a mushroom " +
                 "underside), with some knocked out + wobbled. 0 = smooth sheet, 1 = strong lines.")]
        [Range(0f, 1f)] [BrazeAnimatable("Cauli/Lines")] public float billowLines = 0.5f;
        [Tooltip("CAULI — Deposit Smoke: adds density only inside the affected existing fume. 0 = pure motion.")]
        [Range(0f, 4f)] [BrazeAnimatable("Cauli/Deposit Smoke", 0f, 4f)] public float billowSmokeDeposit = 0f;
        [Tooltip("CAULI — Deposit Fire: adds hot fuel/burn inside the affected existing fume. 0 = pure motion.")]
        [Range(0f, 4f)] [BrazeAnimatable("Cauli/Deposit Fire", 0f, 4f)] public float billowFireDeposit = 0f;

        // ── SPINNER controls (only used when Type = Spinner) ─────────────────────────────────────────────
        // A localized SOLID-BODY rotation. Unlike Vortex (which ADDS a constant tangential push, so the fume
        // keeps accelerating, flings outward and forms ever-larger escaping swirls), the Spinner DRIVES the
        // fume's velocity toward a rigid wheel field (v = ω × r): the rim moves fast, the hub barely turns, and
        // gripped fume circles in place. Pair with a finite Radius + a Disc/Cylinder field shape for a tyre.
        [Tooltip("SPINNER — Spin Speed: how fast the wheel turns, in turns/sec. Signed (negative reverses). " +
                 "The rim moves faster than the hub, like a solid wheel; outer fume gets a faster spin.")]
        [Range(-10f, 10f)] [BrazeAnimatable("Spinner/Spin Speed", -10f, 10f)] public float spinnerSpeed = 1f;
        [Tooltip("SPINNER — Grip: how tightly the fume locks to the wheel's rotation. 0 = loose (smoke slips & " +
                 "lags behind), 1 = rigid tyre lock (smoke spins exactly with the wheel).")]
        [Range(0f, 1f)] [BrazeAnimatable("Spinner/Grip")] public float spinnerGrip = 0.7f;
        [Tooltip("SPINNER — Containment: holds captured fume in the wheel by damping outward drift so it can't " +
                 "fling out. 0 = free to spread, 1 = tightly localized to the wheel.")]
        [Range(0f, 1f)] [BrazeAnimatable("Spinner/Containment")] public float spinnerContain = 0.5f;
        [Tooltip("SPINNER — Flatten: keeps the spin in the wheel's plane by damping motion along the axis. " +
                 "0 = free (can puff along the axle), 1 = a crisp flat spinning disc.")]
        [Range(0f, 1f)] [BrazeAnimatable("Spinner/Flatten")] public float spinnerFlatten = 0.3f;

        /// <summary>Signed axial bias for the GPU: Up → +1, In Place → 0, Down → −1.</summary>
        public float BillowModeSign => billowMotion switch
        {
            BillowMotion.Up => 1f,
            BillowMotion.Down => -1f,
            _ => 0f,
        };

        public float BillowSizeVoxels
        {
            get
            {
                if (billowScale <= 1.0001f)
                {
                    float legacyMin = Mathf.Max(2f, billowSizeMin);
                    float legacyMax = Mathf.Max(legacyMin, billowSizeMax);
                    return Mathf.Lerp(legacyMin, legacyMax, Mathf.Clamp01(billowScale));
                }
                return Mathf.Max(2f, billowScale);
            }
        }

        // Raw-gain calibration: normalized art knob × these → the physics coefficients the compute kernel expects.
        public const float DiscSpreadGain = 24f, DiscFlattenGain = 90f, DiscHoldGain = 16f,
                           DiscRollGain = 30f, DiscSwirlGain = 30f, DiscDistortGain = 20f, BillowGain = 45f;
        public const float VortexGripGain = 12f;
        // Spinner: normalized 0..1 art knobs → raw approach rates (1/s) the kernel drives velocity with.
        public const float SpinnerGripGain = 14f, SpinnerContainGain = 10f, SpinnerFlattenGain = 12f;

        public Vector3 Direction => transform.forward;

        // Only expose the knobs that apply to the CURRENT Type in the timeline picker — so a Gravity force never
        // shows Disc options, and the main Strength is its single magnitude (the disc uses Spread + Power instead).
        public bool IsAnimatableMemberRelevant(string member)
        {
            bool isDisc = type == ForceType.Disc;
            bool isCauli = type == ForceType.CauliBillows;
            bool isSpinner = type == ForceType.Spinner;
            bool isVortex = type == ForceType.Vortex;
            if (member.StartsWith("disc")) return isDisc;             // disc-only shape/force knobs
            if (member.StartsWith("billow")) return isCauli;          // Cauli Billows-only knobs
            if (member.StartsWith("spinner")) return isSpinner;       // Spinner-only knobs
            if (member.StartsWith("vortex")) return isVortex;         // Vortex-only knobs
            switch (member)
            {
                case "strength":        return !isDisc && !isCauli && !isSpinner;   // disc → Spread/Power; cauli → Cauli/Strength; spinner → Spin Speed
                case "targetResponse":  return type == ForceType.Lift || type == ForceType.Jet;
                case "turbulenceScale": return type == ForceType.Turbulence;
                case "falloffInner":
                case "falloffPower":
                case "hole":
                case "fieldHeight":     return !isDisc;               // generic force-field shaping (not the disc)
                // "radius" applies to every type → falls through
            }
            return true;
        }

        void EnforceFixedType()
        {
            if (this is IBrazeFixedPyroForce fixedForce)
                type = fixedForce.FixedForceType;
        }

        protected virtual void Reset() => EnforceFixedType();

        protected virtual void OnEnable()
        {
            EnforceFixedType();
            BrazeRuntimeRegistry.AddUnique(Active, this);
        }

        protected virtual void OnDisable() => BrazeRuntimeRegistry.Remove(Active, this);

        protected virtual void OnValidate()
        {
            EnforceFixedType();
            // Migrate the previous normalized Size slider (0..1 + Min/Max) to the new literal voxel-size knob.
            if (type == ForceType.CauliBillows && billowScale <= 1.0001f)
            {
                float legacyMin = Mathf.Max(2f, billowSizeMin);
                float legacyMax = Mathf.Max(legacyMin, billowSizeMax);
                billowScale = Mathf.Lerp(legacyMin, legacyMax, Mathf.Clamp01(billowScale));
            }
            billowScale = Mathf.Clamp(billowScale, 2f, 96f);
            billowSpeed = Mathf.Clamp(billowSpeed, 0f, 4f);
            billowSmokeDeposit = Mathf.Clamp(billowSmokeDeposit, 0f, 4f);
            billowFireDeposit = Mathf.Clamp(billowFireDeposit, 0f, 4f);
            billowSizeMin = Mathf.Clamp(billowSizeMin, 1f, 32f);
            billowSizeMax = Mathf.Clamp(Mathf.Max(billowSizeMax, billowSizeMin), 2f, 64f);
            vortexGrip = Mathf.Clamp01(vortexGrip);
            // Whole blade counts only — a fractional harmonic leaves a discontinuity seam at θ = ±π.
            vortexBlades = Mathf.Round(Mathf.Clamp(vortexBlades, 0f, 6f));
        }

        // Always-on faint preview so you can see + size the field while placing it; bright + direction arrows on select.
        void OnDrawGizmos()
        {
            if (type == ForceType.Disc) DrawDiscGizmo(0.3f);
            else DrawFieldGizmo(0.28f, false);
        }

        void OnDrawGizmosSelected()
        {
            if (type == ForceType.Disc) { DrawDiscGizmo(0.95f); return; }
            DrawFieldGizmo(0.95f, true);
        }

        // The falloff force field AREA: the shape boundary + central hole + full-strength core, plus a per-type
        // direction field (which way the force pushes) so the artist can see exactly where/how the force applies.
        void DrawFieldGizmo(float alpha, bool detailed)
        {
            Vector3 c = transform.position;
            float R = Mathf.Max(radius, 0.02f);
            Vector3 n = type == ForceType.CauliBillows
                ? Vector3.up
                : (transform.forward.sqrMagnitude > 1e-6f ? transform.forward.normalized : Vector3.up);
            Vector3 u = Vector3.Cross(n, Vector3.up);
            if (u.sqrMagnitude < 1e-4f) u = Vector3.Cross(n, Vector3.right);
            u.Normalize();
            Vector3 w = Vector3.Cross(n, u).normalized;

            // Field boundary + central hole, drawn for the chosen shape. Non-sphere shapes are always localized,
            // so they draw even when Infinite is on; a Sphere with Infinite has no boundary (it fills the container).
            bool drawBounds = type == ForceType.CauliBillows || type == ForceType.Spinner || !infinite || shape != FalloffShape.Sphere;
            if (drawBounds)
            {
                Color main = new Color(0.3f, 0.7f, 1f, alpha);
                Color core = new Color(0.3f, 0.7f, 1f, alpha * 0.45f);
                Color holeCol = new Color(1f, 0.85f, 0.4f, alpha * 0.7f);
                float Hh = Mathf.Max(fieldHeight, 0.01f);
                float h = Mathf.Clamp01(hole);
                switch (shape)
                {
                    case FalloffShape.Sphere:
                        Gizmos.color = main; Gizmos.DrawWireSphere(c, R);
                        if (h > 0.001f) { Gizmos.color = holeCol; Gizmos.DrawWireSphere(c, R * h); }
                        else if (falloffInner > 0.001f) { Gizmos.color = core; Gizmos.DrawWireSphere(c, R * falloffInner); }
                        break;
                    case FalloffShape.Cylinder:
                        DrawCylinder(c, n, u, w, R, Hh, main);
                        if (h > 0.001f) DrawCylinder(c, n, u, w, R * h, Hh, holeCol);
                        break;
                    case FalloffShape.Torus:
                        DrawTorus(c, n, u, w, R, h, main, holeCol);
                        break;
                    case FalloffShape.Disc:   // flat pancake: wide ring (R) at a thin half-height (Hh)
                        DrawCylinder(c, n, u, w, R, Hh, main);
                        if (h > 0.001f) DrawCylinder(c, n, u, w, R * h, Hh, holeCol);
                        break;
                }
            }

            if (!detailed) return;

            float arrow = R * 0.28f;
            bool repel = strength < 0f;
            Gizmos.color = new Color(1f, 0.85f, 0.4f, alpha);
            switch (type)
            {
                case ForceType.Wind:
                case ForceType.Gravity:   // uniform push along forward → parallel arrows across the field
                case ForceType.Jet:       // drive fluid to a target speed along forward → same directional arrows (aimable)
                {
                    Vector3 dir = (type == ForceType.Jet && strength < 0f) ? -n : n;
                    if (type == ForceType.Jet) Gizmos.color = new Color(0.4f, 1f, 0.6f, alpha);
                    float[] su = { 0f, 0.6f, -0.6f, 0f, 0f };
                    float[] sw = { 0f, 0f, 0f, 0.6f, -0.6f };
                    for (int i = 0; i < 5; i++)
                    {
                        Vector3 p = c + (u * su[i] + w * sw[i]) * R * 0.6f;
                        DrawArrow(p - dir * arrow * 0.5f, dir, arrow * 1.3f);
                    }
                    break;
                }
                case ForceType.Attract:   // arrows toward the centre (outward when Strength is negative = repel)
                {
                    const int k = 8;
                    for (int i = 0; i < k; i++)
                    {
                        float a = i / (float)k * Mathf.PI * 2f;
                        Vector3 dir = u * Mathf.Cos(a) + w * Mathf.Sin(a);
                        DrawArrow(c + dir * R * 0.85f, repel ? dir : -dir, arrow);
                    }
                    break;
                }
                case ForceType.Vortex:    // tangential swirl arrows around the axis + optional orbit ring / lift arrow
                {
                    float vsgn = strength < 0f ? -1f : 1f;
                    const int k = 10;
                    for (int i = 0; i < k; i++)
                    {
                        float a = i / (float)k * Mathf.PI * 2f;
                        Vector3 dir = u * Mathf.Cos(a) + w * Mathf.Sin(a);
                        Vector3 tang = Vector3.Cross(n, dir).normalized * vsgn;
                        DrawArrow(c + dir * R * 0.7f - tang * arrow * 0.5f, tang, arrow);
                    }
                    if (vortexMotion == VortexMotion.Orbit)   // the radius the smoke is held at
                    {
                        Gizmos.color = new Color(0.7f, 0.5f, 1f, alpha);
                        DrawEllipse(c, u, w, Mathf.Max(vortexOrbitRadius, 0.01f), Mathf.Max(vortexOrbitRadius, 0.01f));
                    }
                    if (Mathf.Abs(vortexLift) > 1e-4f)        // driven axial column flow
                    {
                        Gizmos.color = new Color(0.4f, 1f, 0.6f, alpha);
                        Vector3 lz = vortexLift < 0f ? -n : n;
                        DrawArrow(c - lz * arrow * 0.6f, lz, arrow * 1.6f);
                    }
                    break;
                }
                case ForceType.Spinner:   // a spinning wheel: rim arrows long (fast), hub arrows short (slow) → solid-body
                {
                    float sgn = spinnerSpeed < 0f ? -1f : 1f;
                    Gizmos.color = new Color(0.5f, 0.85f, 1f, alpha);
                    DrawEllipse(c, u, w, R, R);                       // rim
                    DrawEllipse(c, u, w, R * 0.5f, R * 0.5f);         // inner ring
                    Gizmos.DrawLine(c - n * R * 0.5f, c + n * R * 0.5f);   // axle
                    const int k = 12;
                    for (int i = 0; i < k; i++)
                    {
                        float ang = i / (float)k * Mathf.PI * 2f;
                        Vector3 dir = u * Mathf.Cos(ang) + w * Mathf.Sin(ang);
                        Vector3 tang = Vector3.Cross(n, dir).normalized * sgn;
                        DrawArrow(c + dir * R - tang * arrow * 0.6f, tang, arrow * 1.1f);   // fast rim
                    }
                    for (int i = 0; i < 6; i++)
                    {
                        float ang = i / 6f * Mathf.PI * 2f;
                        Vector3 dir = u * Mathf.Cos(ang) + w * Mathf.Sin(ang);
                        Vector3 tang = Vector3.Cross(n, dir).normalized * sgn;
                        DrawArrow(c + dir * R * 0.5f - tang * arrow * 0.25f, tang, arrow * 0.5f);   // slow hub
                    }
                    break;
                }
                case ForceType.Turbulence:   // scattered churn arrows
                {
                    for (int i = 0; i < 10; i++)
                    {
                        Vector3 p = c + (u * (Hash(i * 1.7f) * 2f - 1f) + w * (Hash(i * 3.1f + 5f) * 2f - 1f)
                                          + n * (Hash(i * 2.3f + 9f) * 2f - 1f)) * R * 0.6f;
                        Vector3 d = (u * (Hash(i + 0.2f) * 2f - 1f) + w * (Hash(i + 0.6f) * 2f - 1f)
                                      + n * (Hash(i + 0.9f) * 2f - 1f));
                        if (d.sqrMagnitude > 1e-4f) DrawArrow(p, d, arrow * 0.7f);
                    }
                    break;
                }
                case ForceType.Hold:   // calm region → inward damping ticks
                    DrawInwardTicks(c, R, u, w, alpha);
                    break;
                case ForceType.Lift:   // updraft → upward arrows across the field (down when Strength is negative)
                {
                    Vector3 lift = strength < 0f ? Vector3.down : Vector3.up;
                    Gizmos.color = new Color(0.4f, 1f, 0.6f, alpha);
                    float[] su = { 0f, 0.6f, -0.6f, 0f, 0f };
                    float[] sw = { 0f, 0f, 0f, 0.6f, -0.6f };
                    for (int i = 0; i < 5; i++)
                    {
                        Vector3 p = c + (u * su[i] + w * sw[i]) * R * 0.6f - lift * arrow * 0.6f;
                        DrawArrow(p, lift, arrow * 1.4f);
                    }
                    break;
                }
                case ForceType.CauliBillows:   // curling lobes climbing / sinking / swirling vertically
                {
                    Vector3 boilAxis = n;
                    float sign = BillowModeSign;
                    const int k = 8;
                    for (int i = 0; i < k; i++)
                    {
                        float a = i / (float)k * Mathf.PI * 2f;
                        Vector3 ring = (u * Mathf.Cos(a) + w * Mathf.Sin(a));
                        Vector3 p = c + ring * R * 0.55f;
                        Vector3 tang = Vector3.Cross(boilAxis, ring).normalized;
                        // motion along the axis (Up/Down) + a curl tangent → a boiling lobe
                        Vector3 d = boilAxis * (sign * 0.9f) + tang * 0.75f;
                        DrawArrow(p, d, arrow);
                    }
                    break;
                }
            }
        }

        static void DrawArrow(Vector3 from, Vector3 dir, float len)
        {
            if (dir.sqrMagnitude < 1e-6f) return;
            dir.Normalize();
            Vector3 to = from + dir * len;
            Gizmos.DrawLine(from, to);
            Vector3 any = Mathf.Abs(dir.y) < 0.95f ? Vector3.up : Vector3.right;
            Vector3 s = Vector3.Cross(dir, any).normalized;
            float hl = len * 0.3f;
            Gizmos.DrawLine(to, to - dir * hl + s * hl * 0.5f);
            Gizmos.DrawLine(to, to - dir * hl - s * hl * 0.5f);
        }

        void DrawInwardTicks(Vector3 c, float R, Vector3 u, Vector3 w, float alpha)
        {
            Gizmos.color = new Color(1f, 0.45f, 0.14f, alpha);
            const int k = 12;
            for (int i = 0; i < k; i++)
            {
                float a = i / (float)k * Mathf.PI * 2f;
                Vector3 dir = u * Mathf.Cos(a) + w * Mathf.Sin(a);
                Gizmos.DrawLine(c + dir * R, c + dir * R * 0.85f);
            }
        }

        static float Hash(float x) { float f = Mathf.Sin(x * 127.1f) * 43758.5453f; return f - Mathf.Floor(f); }

        // Cylinder field: two end caps (radius R at ±Hh along the axis) joined by 4 verticals.
        static void DrawCylinder(Vector3 c, Vector3 n, Vector3 u, Vector3 w, float R, float Hh, Color col)
        {
            Gizmos.color = col;
            DrawEllipse(c + n * Hh, u, w, R, R);
            DrawEllipse(c - n * Hh, u, w, R, R);
            for (int i = 0; i < 4; i++)
            {
                float a = i / 4f * Mathf.PI * 2f;
                Vector3 d = u * Mathf.Cos(a) + w * Mathf.Sin(a);
                Gizmos.DrawLine(c + d * R - n * Hh, c + d * R + n * Hh);
            }
        }

        // Torus field: outer rim + inner hole circle (the donut opening) + tube cross-sections around the ring.
        static void DrawTorus(Vector3 c, Vector3 n, Vector3 u, Vector3 w, float R, float hole, Color mainCol, Color holeCol)
        {
            float ringR = R * (1f + hole) * 0.5f;
            float tubeR = Mathf.Max(R * (1f - hole) * 0.5f, 0.001f);
            Gizmos.color = mainCol; DrawEllipse(c, u, w, R, R);
            Gizmos.color = holeCol; DrawEllipse(c, u, w, hole * R, hole * R);
            Gizmos.color = mainCol;
            const int k = 8;
            for (int i = 0; i < k; i++)
            {
                float a = i / (float)k * Mathf.PI * 2f;
                Vector3 d = u * Mathf.Cos(a) + w * Mathf.Sin(a);
                DrawEllipse(c + d * ringR, d, n, tubeR, tubeR);
            }
        }

        // Draw the annular disc/slab influence: concentric rings on the (domed) top surface + the flat bottom face,
        // joined at the rim, plus the open center and axis — so the artist sees the real guided volume.
        void DiscBasis(out Vector3 normal, out Vector3 u, out Vector3 w)
        {
            normal = transform.forward.sqrMagnitude > 1e-6f ? transform.forward.normalized : Vector3.up;
            u = Vector3.Cross(normal, Vector3.up);
            if (u.sqrMagnitude < 1e-4f) u = Vector3.Cross(normal, Vector3.right);
            u = u.normalized;
            w = Vector3.Cross(normal, u).normalized;
        }

        void DrawDiscGizmo(float alpha)
        {
            DiscBasis(out Vector3 n, out Vector3 u, out Vector3 w);
            Vector3 c = transform.position;
            float R = Mathf.Max(radius, 0.02f);
            float H = Mathf.Max(discThickness * R, 0.01f);            // Thickness is a fraction of Radius
            int layers = Mathf.Max(discLayers, 1);
            float spacing = Mathf.Max(discLayerSpacing * R, 0.001f);  // Spacing is a fraction of Radius
            float halfSpan = (layers - 1) * 0.5f * spacing;
            float layerHalf = layers > 1 ? Mathf.Min(H, spacing * 0.5f) : H;
            const int rings = 3, spokes = 8;
            for (int L = 0; L < layers; L++)
            {
                Vector3 lc = c + n * (-halfSpan + L * spacing);
                float hFrac = layers > 1 ? L / Mathf.Max(layers - 1, 1f) : 1f;
                float Rk = R * (1f - (layers > 1 ? Mathf.Clamp01(discTaper) * hFrac : 0f));   // tapered sheet radius
                // Chimney is open through the lower/mid stack and closes toward the crown (matches the kernel's
                // height-based core closing) — so a SINGLE disc still shows an open central opening, not a solid pad.
                float crownGate = layers > 1 ? Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((hFrac - 0.65f) / 0.35f)) : 0f;
                float core = Mathf.Clamp01(discCoreOpening) * Rk * (1f - crownGate);
                Gizmos.color = new Color(1f, 0.6f, 0.2f, alpha);
                if (core > 0.001f) DrawEllipse(lc, u, w, core, core);          // inner chimney edge
                for (int k = 1; k <= rings; k++)                              // concentric spherical-cap surface rings
                {
                    float rr = Mathf.Lerp(core, Rk, k / (float)rings);
                    float top = discDome * hFrac * Mathf.Sqrt(Mathf.Max(Rk * Rk - rr * rr, 0f));   // spherical cap
                    DrawEllipse(lc + n * top, u, w, rr, rr);
                }
                DrawEllipse(lc - n * layerHalf, u, w, Rk, Rk);               // sheet's underside
                if (core > 0.001f) DrawEllipse(lc - n * layerHalf, u, w, core, core);
                for (int i = 0; i < spokes; i++)                             // rim verticals (sheet thickness)
                {
                    float a = i / (float)spokes * Mathf.PI * 2f;
                    Vector3 outDir = (u * Mathf.Cos(a) + w * Mathf.Sin(a)).normalized;
                    Gizmos.DrawLine(lc + outDir * Rk - n * layerHalf, lc + outDir * Rk);
                }
            }
            // Seal hint: a brighter round cap dome at the top of the (tapered) crown when sealed.
            if (discSeal > 0.01f)
            {
                float Rtop = R * (1f - (layers > 1 ? Mathf.Clamp01(discTaper) : 0f));
                Gizmos.color = new Color(1f, 0.85f, 0.4f, alpha);
                DrawEllipse(c + n * halfSpan, u, w, Rtop, Rtop);                              // crown rim
                DrawEllipse(c + n * (halfSpan + discDome * Rtop * 0.5f), u, w, Rtop * 0.6f, Rtop * 0.6f);  // dome
            }
            Gizmos.color = new Color(0.3f, 0.7f, 1f, alpha);
            float RtopAxis = R * (1f - (layers > 1 ? Mathf.Clamp01(discTaper) : 0f));
            Gizmos.DrawRay(c, n * (halfSpan + RtopAxis * 0.5f + (discDome > 0f ? discDome * RtopAxis : 0f)));   // axis
        }

        // Ellipse centred at c in the plane spanned by u,w, with radii ru,rw.
        static void DrawEllipse(Vector3 c, Vector3 u, Vector3 w, float ru, float rw)
        {
            u = u.normalized; w = w.normalized;
            Vector3 prev = c + u * ru;
            for (int i = 1; i <= 48; i++)
            {
                float a = i / 48f * Mathf.PI * 2f;
                Vector3 cur = c + u * (Mathf.Cos(a) * ru) + w * (Mathf.Sin(a) * rw);
                Gizmos.DrawLine(prev, cur);
                prev = cur;
            }
        }
    }
}
