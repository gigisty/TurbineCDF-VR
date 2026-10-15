using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BrazeFX
{
    /// <summary>
    /// Voronoi Fracture — a GPU re-creation of Cinema 4D's Voronoi Fracture object. Drives destruction with the
    /// "thinking particles" philosophy: seed points scatter inside the target's volume, a 3D Voronoi diagram is
    /// built from them, and the source mesh is clipped into convex cell fragments — each fragment then becomes a
    /// rigid GPU piece driven by the shared Braze force list and rendered by the Chunk vertex shader (the same
    /// rigid-piece sim + render tail the Surface Disintegrator already uses).
    ///
    /// The authoring layer separates geometry patterns/presets and transient previews from the committed runtime bake;
    /// effectors and event-driven structural support continue to operate on that committed cell adjacency.
    /// </summary>
    [AddComponentMenu("BrazeFX/Destruction/Braze Voronoi Fracture", 40)]
    [DisallowMultipleComponent]
    [ExecuteAlways]   // render the textured fracture in edit mode too (not just a flat gizmo)
    public sealed class BrazeVoronoiFracture : MonoBehaviour
    {
        public enum Distribution { Uniform, Even, Clustered }

        /// <summary>Artist-facing starting points. A style copies explicit, editable authoring values; it is not a
        /// runtime material/physics choice. Editing any copied value changes the inspector label to Custom.</summary>
        public enum FractureStyle { Generic, Concrete, Stone, Wood, Glass, Ceramic, Custom }

        /// <summary>The geometric family used to place seeds. Unlike a value-only preset, these patterns are capable
        /// of producing genuinely different material structures (3D rubble, fibres, and 2.5D radial sheets).</summary>
        public enum FracturePattern { Volume, MultiScale, Fibres, RadialSheet }

        public enum AuthoringPreviewMode { Shaded, Fragments, XRay, Slice }

        [Header("Target")]
        [HideInInspector, Tooltip("Legacy external-renderer override retained for serialized scene compatibility. " +
                                  "New authoring uses the MeshFilter or SkinnedMeshRenderer on this object.")]
        public Renderer target;
        [Tooltip("Hide the source mesh renderer so only the fracture is shown.")]
        public bool hideOriginal = false;

        [Tooltip("Baked fracture data (.asset). The fracture ONLY runs from this — it is never re-fractured live. " +
                 "Press Bake to (re)generate it; created automatically on first Bake if empty. Change any fracture " +
                 "input and the inspector turns red until you Bake again.")]
        public BrazeFractureBake bake;
#if UNITY_EDITOR
        [SerializeField, HideInInspector] string bakeAssetPath;
        internal string BakeAssetPathForEditor => bakeAssetPath;
#endif

        // ---------------- FRACTURE AUTHORING (geometry only; never physics) ----------------
        [Header("Fracture Authoring")]
        [Tooltip("Geometry-only fracture starting point. Destruction response remains controlled separately below.")]
        public FractureStyle fractureStyle = FractureStyle.Generic;
        [Tooltip("Seed-pattern family. Volume = ordinary 3D Voronoi; Multi Scale = macro chunks plus local micro-shatter; " +
                 "Fibres = directionally elongated splinters; Radial Sheet = rings/rays extruded through thickness.")]
        public FracturePattern fracturePattern = FracturePattern.Volume;

        [Tooltip("Local-space material direction. For wood this is the grain/splinter axis; for sheet patterns it is " +
                 "the sheet normal. A Scene handle is provided by the fracture inspector.")]
        public Vector3 patternDirection = Vector3.up;
        [Tooltip("Wood/fibre cell length relative to its cross-section.")]
        [Range(1f, 12f)] public float fibreSlenderness = 6f;
        [Tooltip("Fraction of Multi Scale cells reserved for fine fragments near the impact point.")]
        [Range(0f, 0.8f)] public float secondaryFraction = 0.35f;
        [Tooltip("Art-directed impact point in target-local space. Scene handles are easier than typing this value.")]
        public Vector3 impactPoint = Vector3.zero;
        [Tooltip("Micro-shatter radius as a fraction of the target bounds diagonal, so presets survive mesh scale changes.")]
        [Range(0.02f, 1f)] public float impactRadius = 0.25f;

        [Tooltip("Radial-sheet concentric bands.")]
        [Range(1, 16)] public int radialRings = 5;
        [Tooltip("Radial-sheet angular divisions.")]
        [Range(3, 48)] public int radialRays = 14;
        [Tooltip("Breaks the perfect polar grid while preserving a readable impact pattern.")]
        [Range(0f, 1f)] public float radialJitter = 0.22f;
        [Tooltip("How much of the target's planar bounds the radial fracture occupies.")]
        [Range(0.1f, 1f)] public float radialExtent = 0.92f;

        [SerializeField, HideInInspector] int fractureAuthoringVersion;

        // ---------------- SOURCES (seed points → the Voronoi diagram) ----------------
        [Header("Sources — Distribution")]
        [Tooltip("How many Voronoi cells (fragments) to generate. Art-directable; the count is preserved even with " +
                 "a shader source on (it only biases WHERE the points land).")]
        [Min(1)] public int pointCount = 64;
        [Tooltip("Layout seed — change for a different fracture pattern at the same count.")]
        public int seed = 12345;
        [Tooltip("Uniform = pure random. Even = jittered grid (natural, evenly spread). Clustered = packed toward " +
                 "an impact centre for hero hits + micro-shatter.")]
        public Distribution distribution = Distribution.Even;
        [Tooltip("Even mode: jitter inside each grid cell. 0 = perfect grid, 1 = fully random within the cell.")]
        [Range(0f, 1f)] public float variation = 0.7f;

        [Header("Sources — Clustered")]
        [Tooltip("Impact centre in the target's LOCAL space (cells pack around here).")]
        public Vector3 clusterCenter = Vector3.zero;
        [Tooltip("Radius of the cluster in local units.")]
        [Min(0f)] public float clusterRadius = 0.5f;
        [Tooltip("0 = ignore the cluster (uniform). 1 = pull every point into the cluster.")]
        [Range(0f, 1f)] public float clusterStrength = 0.7f;
        [Tooltip("Higher packs points tighter to the centre (finer micro-shatter at the hit point).")]
        [Range(1f, 8f)] public float clusterFalloff = 2f;

        [Header("Sources — Shader density")]
        [Tooltip("Bias seeds toward high-density regions of a procedural noise field (localize the break). " +
                 "Texture-driven density comes in a later slice.")]
        public bool shaderSource = false;
        [Tooltip("Noise frequency: cells per local unit.")]
        [Min(0.01f)] public float noiseScale = 2f;
        public int noiseSeed = 7;
        [Tooltip("Sharpness of the density pockets. Higher = tighter, more contrasty clumping.")]
        [Range(0.05f, 8f)] public float densityContrast = 2f;
        [Tooltip("Best-candidate tries per seed: more = stronger adherence to the density field (slower).")]
        [Range(1, 8)] public int densityTries = 4;

        [Header("Cells")]
        [HideInInspector, Tooltip("Legacy field retained for scene compatibility. Exact cells now choose their own " +
                                  "security radius, so a fixed neighbour limit is no longer artist-authored.")]
        [Range(4, 26)] public int neighborK = 16;

        // ---------------- OBJECT (fragment look — applied from slice 1b) ----------------
        [Header("Object (applied once cells exist)")]
        [Tooltip("Squash/stretch the Voronoi cells directionally (wood grain, crystals). 1,1,1 = isotropic.")]
        public Vector3 scaleCells = Vector3.one;
        [HideInInspector, Tooltip("Legacy serialized value. Migrated once to the render-only Preview Explode control.")]
        [Min(0f)] public float offsetFragments = 0f;
        [Tooltip("Tint each fragment a distinct colour (debug / stylized).")]
        public bool colorizeFragments = true;

        [Header("Effectors (direct fragment transforms)")]
        [Tooltip("Ordered transform-effector stack. Effectors directly offset/rotate/scale fragments inside their " +
                 "fields; they do not add force or velocity.")]
        public System.Collections.Generic.List<BrazeFractureEffector> effectors = new();

        // ---------------- DETAILING (render-time — NO extra geometry) ----------------
        [Header("Outer Surface")]
        [Tooltip("Optional override for the fragments' original outer skin. Empty = the source renderer's material.")]
        public Material materialSource;
        [Tooltip("Minimum albedo visibility for detached fragments. Dense, tumbling debris can otherwise mutually " +
                 "shadow until it collapses to black. This is a lower bound only: it does not brighten pixels that " +
                 "are already lit, and costs no extra texture or shadow sample.")]
        [Range(0f, 0.5f)] public float movingFragmentLightFloor = 0.08f;

        [Header("Interior Faces")]
        [Tooltip("Optional material used only on newly exposed cut faces. Braze copies its base colour/map, metallic, " +
                 "and smoothness into the single chunk shader, so this does not add a draw call.")]
        public Material interiorMaterial;
        [Tooltip("Interior colour when no Interior Material is assigned.")]
        public Color interiorColor = new(0.28f, 0.25f, 0.22f, 1f);
        [Range(0f, 1f)] public float interiorMetallic = 0f;
        [Range(0f, 1f)] public float interiorSmoothness = 0.22f;
        [Tooltip("Triplanar-project the Interior Material's base texture across cut faces. Colour/PBR values still " +
                 "apply when this is off.")]
        public bool triplanarUV = true;
        [Tooltip("Interior texture tiling in target-local space.")]
        [Min(0.01f)] public float triplanarScale = 1f;

        [Header("Interior Surface Relief")]
        [Tooltip("Procedural normal and tone breakup on cut faces. Render-only: no displaced vertices and no rebake.")]
        public bool enableDetailing = false;
        [Tooltip("Normal perturbation strength.")]
        [Min(0f)] public float detailAmount = 0.4f;
        [Tooltip("Relief frequency in target-local space.")]
        [Min(0.01f)] public float detailScale = 6f;
        [Range(1, 6)] public int detailOctaves = 3;
        [Tooltip("How much the relief also varies interior albedo and roughness.")]
        [Range(0f, 1f)] public float detailContrast = 0.28f;

        [Header("Fake Fracture Edges")]
        [Tooltip("Draw a noisy screen-space wear band along real chunk creases. Internal cap-triangulation diagonals " +
                 "are filtered on the CPU once; no geometry or extra draw calls are added.")]
        public bool enableEdgeDetail = false;
        public Color edgeDetailColor = new(0.07f, 0.055f, 0.045f, 1f);
        [Tooltip("Edge band width in screen pixels.")]
        [Range(0.25f, 8f)] public float edgeDetailWidth = 1.6f;
        [Range(0f, 1f)] public float edgeDetailStrength = 0.7f;
        [Tooltip("Breaks up the edge band so it reads chipped instead of mathematically smooth.")]
        [Range(0f, 1f)] public float edgeDetailNoise = 0.35f;
        [Tooltip("Frequency of edge breakup in target-local space.")]
        [Min(0.01f)] public float edgeDetailScale = 12f;

        // ---------------- DESTRUCTION (selection + structural support) ----------------
        public enum FieldShape { Sphere, Box, Plane }
        public enum BreakMode
        {
            Manual = 0,
            Field = 1,
            [Obsolete("Connectors migrated to Field + Structural Support.")] Connectors = 2
        }

        [Tooltip("Manual = release through impacts or release APIs. Field = a persistent field progressively releases cells.")]
        public BreakMode breakMode = BreakMode.Manual;

        [Header("Break Field")]
        [Tooltip("Field shape. Sphere/Box = a region; Plane = a cut front (e.g. a rising/falling break line).")]
        public FieldShape breakFieldShape = FieldShape.Sphere;
        [Tooltip("Transform that positions/orients/scales the field. Empty = this object. ANIMATE it (or the timeline) " +
                 "to sweep the break front through the fracture.")]
        public Transform breakField;
        [Tooltip("Field size in world units (× the field transform's scale).")]
        [Min(0f)] public float breakFieldSize = 0.6f;
        [Tooltip("Irregularity of the break front: 0 = clean geometric boundary, 1 = ragged/organic crumble edge.")]
        [Range(0f, 1f)] public float breakFalloff = 0.4f;
        [Tooltip("0 = nothing, 1 = the whole field is released. Animate it (or use the timeline) to drive destruction.")]
        [BrazeAnimatable("Destruction/Break Progress", 0f, 1f)]
        [Range(0f, 1f)] public float breakProgress = 0f;
        [Tooltip("Auto-advance Break Progress at this rate per second on Play (0 = manual / timeline-driven).")]
        [Min(0f)] public float breakSpeed = 0f;
        [Tooltip("Show the break field region in the Scene view.")]
        public bool showField = true;

        [Header("Structural Support")]
        [Tooltip("Maintain a baked shared-face support network. Cells disconnected from the anchored boundary release " +
                 "automatically after fields, impacts, or release calls remove their support.")]
        public bool structuralSupport = false;
        [Tooltip("Derive the anchored boundary from the net direction of listed Braze Gravity forces. Falls back to Custom Anchor Direction when no gravity is listed.")]
        public bool supportFollowsGravity = true;
        [Tooltip("Fracture-local direction toward the supporting boundary when Follow Braze Gravity is off or no gravity is listed.")]
        public Vector3 supportAnchorDirection = Vector3.down;
        [Range(0f, 2f)] [Tooltip("Thickness of the anchored boundary layer, measured in average cell radii.")]
        public float supportAnchorThickness = 0.35f;
        [Range(0f, 1f)] [Tooltip("Allow same-level neighbours to carry sideways support for arches and bridges.")]
        public float lateralSupport = 0.75f;
        [Range(1, 8)] [Tooltip("Maximum support-network layers resolved per frame. Lower values spread large collapses over time and reduce spikes.")]
        public int collapseSpeed = 2;
        [Tooltip("RayFire-style demolition: a section disconnected from the anchor detaches as ONE rigid piece — one " +
                 "centre of mass, one rotation — and only shatters into cells when a contact or impact exceeds the " +
                 "material's Break Impulse. Off = legacy granular collapse (every cell falls separately).")]
        public bool rigidClusters = true;
        [Min(0f)] [Tooltip("Custom material: impact speed (m/s) a rigid cluster survives before it shatters. Landing " +
                           "speed is measured at the contact point, with a modest stress boost for long/heavy spans. " +
                           "Presets provide their own value.")]
        public float clusterBreakImpulse = 5f;
        [Range(1, 512)] [Tooltip("Disconnected islands with fewer cells than this skip clustering and release as loose debris.")]
        public int clusterMinCells = 6;

        public enum CollapseMaterial { Cement, Wood, Metal, Plastic, Glass, Custom }

        [Header("Cohesive Collapse")]
        [Tooltip("Temporarily keep unsupported neighboring cells in coherent macro-chunks so slabs topple and tear before becoming granular debris.")]
        public bool cohesiveCollapse = true;
        [Tooltip("Material response preset. Custom exposes the underlying cohesion controls.")]
        public CollapseMaterial collapseMaterial = CollapseMaterial.Cement;
        [Range(1, 64)] [Tooltip("Custom: approximate number of neighboring cells per coherent macro-chunk.")]
        public int cohesionChunkSize = 14;
        [Range(0f, 1f)] [Tooltip("Custom: positional/orientation rigidity of a falling macro-chunk.")]
        public float cohesion = 0.82f;
        [Range(0f, 1f)] [Tooltip("Custom: velocity and angular-velocity matching within a macro-chunk.")]
        public float cohesionDamping = 0.75f;
        [Range(0.01f, 1f)] [Tooltip("Custom: relative bond stretch that detaches an individual cell.")]
        public float cohesionBreakStrain = 0.12f;
        [Min(0.05f)] [Tooltip("Custom: maximum seconds a released macro-chunk remains bonded before granular breakup.")]
        public float cohesionDuration = 1.8f;
        [Range(0f, 3f)] [Tooltip("Custom: coherent angular kick applied when support is lost.")]
        public float cohesionTopple = 1f;
        [Range(0f, 1f)] [Tooltip("Custom: fraction of structurally released cells that bypass cohesion as immediate rubble.")]
        public float cohesionDebrisFraction = 0.18f;
        [Range(1, 4)] [Tooltip("Custom: GPU constraint iterations per active frame. Higher is more rigid but costs more while a collapse is active.")]
        public int cohesionIterations = 2;

        // ---------------- FORCES & RELEASE (play mode — fragments fly as rigid GPU pieces) ----------------
        [Header("Forces & Release")]
        [Tooltip("Drop the Braze force components that drive free fragments (Gravity / Wind / Drag / Radial / Vortex / " +
                 "Turbulence). ONLY the forces listed here act on this fracture — add a Braze Gravity to make fragments " +
                 "fall. Empty = no forces (fragments just sit where released).")]
        public System.Collections.Generic.List<BrazeParticleForce> forces = new();

        [Header("Batched Impacts")]
        [Tooltip("Accept queued bullet, shell, and explosion events as inputs to this fracture's Destruction system.")]
        public bool acceptImpacts = true;
        [Range(1, 4096)] [Tooltip("Maximum bullet/shell impacts accepted by this fracture in one frame. Commands upload and dispatch as one GPU batch.")]
        public int maxImpactsPerFrame = 1024;
        [Min(0f)] [Tooltip("Safety clamp for velocity after repeated impacts. Zero disables the clamp.")]
        public float maxImpactSpeed = 80f;

        [Header("Release Defaults")]
        [Tooltip("Minimum signed release speed. Negative values reverse the primary release direction.")]
        public float releaseImpulseMin = 1.5f;
        [Tooltip("Maximum signed release speed. Each newly released fragment samples between Min and Max.")]
        public float releaseImpulse = 1.5f;
        [SerializeField, HideInInspector] bool releaseImpulseRangeInitialized;
        [Tooltip("Minimum random tumble speed (rad/s) given to a fragment when it breaks off.")]
        [Min(0f)] public float releaseSpinMin = 0f;
        [Tooltip("Maximum random tumble speed (rad/s) given to a fragment when it breaks off.")]
        [Min(0f)] public float releaseSpin = 6f;
        [Tooltip("The release spin is an INITIAL THRUST: it runs free for a random number of frames in this range " +
                 "(per fragment), then is damped out so chunks settle instead of spinning forever. x = min, y = max.")]
        public Vector2Int thrustFrames = new(5, 10);
        [Tooltip("How fast the spin bleeds off once the thrust window ends (higher = stops sooner).")]
        [Min(0f)] public float spinDamping = 8f;
        [Tooltip("Linear drag applied after the thrust window (0 = none; air resistance for the debris).")]
        [Min(0f)] public float linearDrag = 0f;
        [Tooltip("Release every fragment the moment Play starts (otherwise call Release All / Release Sphere).")]
        public bool releaseOnPlay = false;

        [Header("Secondary Particles (GPU)")]
        [Tooltip("Braze Particle Systems that receive GPU spawn requests from fragment surface points (dust / sparks / " +
                 "smoke trailing the debris). Each emitter owns its own particle look, lifetime, size, colour, and forces.")]
        public System.Collections.Generic.List<BrazeParticleSystem> debrisEmitters = new();
        [SerializeField, HideInInspector]
        List<float> debrisEmitterSpawnPercentages = new();
        [SerializeField, HideInInspector]
        List<BrazeParticleSystem> debrisEmitterSpawnOwners = new();
        [Tooltip("Master switch for continuous secondary particles. When on, Stick To Fragment enables attached riders " +
                 "and Spawn At Start enables free grains. Off = Burst / Fragment only.")]
        public bool debrisFromShrinkingClumps = false;
        [Tooltip("Burst: particles emitted from a fragment the moment it breaks off (one-time). Independent of Spawn " +
                 "While Shrinking. 0 = no burst.")]
        [Min(0)] public int debrisPointsPerChunk = 6;
        [Tooltip("Spawn While Shrinking: average particles per second shed by each moving free fragment.")]
        [Min(0f)] public float debrisRatePerChunk = 12f;
        [Tooltip("How much of the moving fragment surface velocity each secondary particle inherits.")]
        [Range(0f, 3f)] public float debrisInheritVelocity = 1f;
        [Tooltip("Extra velocity along the sampled surface normal added to each debris request.")]
        [Min(0f)] public float debrisScatter = 0.3f;
        [Tooltip("Hard cap on debris spawn requests appended by this fracture in one frame.")]
        [Min(0)] public int maxDebrisPoints = 8000;
        [Tooltip("Stick spawned particles to the fragment: they ride its position + rotation rigidly (ignoring forces) " +
                 "instead of detaching at birth, then fade out after the emitter's Start Lifetime. For continuous " +
                 "riders, Spawn While Shrinking must also be on.")]
        public bool stickToFragment = false;
        [Tooltip("Free shed stream: spawn unattached particles from fragments as they move. Runs alongside Stick To " +
                 "Fragment when Spawn While Shrinking is on, and stops once a fragment settles.")]
        public bool spawnAtStart = false;

        [Header("Age (free fragments shrink + dissolve away)")]
        [Tooltip("Lifetime of a free fragment in seconds (0 = never dies).")]
        [Min(0f)] public float fragmentLifetime = 0f;
        [Range(0f, 1f)] public float shrink = 0.4f;
        [Range(0f, 1f)] public float dissolve = 0.8f;
        [Min(0.1f)] public float dissolveScale = 30f;
        [ColorUsage(true, true)] public Color dissolveEdge = new(1f, 0.4f, 0.1f, 2f);

        [Header("Collision (shape-aware)")]
        [Tooltip("Treat each fragment's vertices as collision sample points (rigidly attached) so chunks rest on " +
                 "faces/edges and tumble against Braze Particle Colliders — instead of sinking as a centroid sphere. " +
                 "Off = the cheap single-sphere collision.")]
        public bool shapeAwareCollision = true;
        [Tooltip("Max vertices sampled per fragment for contacts (strided). Higher = more accurate, costlier.")]
        [Range(4, 64)] public int collisionSamples = 24;
        [Tooltip("How firmly penetration is resolved each frame (1 = pop fully out; lower = softer settle).")]
        [Range(0.1f, 1f)] public float collisionStrength = 0.8f;
        [Tooltip("How strongly off-centre contacts spin the chunk (tumbling). 0 = no induced rotation.")]
        [Range(0f, 1f)] public float angularResponse = 0.35f;
        [Tooltip("Gravity topples a contacting chunk about its support so big chunks (poles/slabs) lie down at a " +
                 "natural rate instead of balancing forever on a landing corner. Inertia-scaled, so a large piece " +
                 "rotates more deliberately while its falling tip still accelerates naturally. 0 = off.")]
        [Range(0f, 4f)] public float toppleStrength = 1f;
        [Tooltip("How strongly penetrated contact points rotate a fragment out of the collider. Higher topples leaning slabs faster.")]
        [Range(0f, 2f)] public float contactTipResponse = 1.1f;
        [Tooltip("Tiny allowed penetration at rest. Prevents gravity/contact correction from re-tipping settled chunks every frame.")]
        [Min(0f)] public float contactRestSlop = 0.015f;
        [Tooltip("Extra angular damping applied only while a fragment is touching a collider.")]
        [Min(0f)] public float contactAngularDamping = 18f;
        [Tooltip("Contacting fragments below this linear speed are snapped to rest.")]
        [Min(0f)] public float sleepLinearSpeed = 0.12f;
        [Tooltip("Contacting fragments below this angular speed are snapped to rest.")]
        [Min(0f)] public float sleepAngularSpeed = 0.08f;
        [Tooltip("Once a chunk stays below both sleep speeds while touching, it must hold still this long (seconds) " +
                 "before it truly sleeps — gravity then stops being integrated for it, so a settled pile stops " +
                 "jittering entirely. It wakes if it loses contact. 0 = never sleep (old behaviour).")]
        [Min(0f)] public float sleepDelay = 0.25f;
        [Tooltip("Fragment-vs-fragment separation using bounded sphere proxies in a no-drop GPU spatial grid. " +
                 "Pairs that overlap in the assembled pose remain muted until they separate, sleeping chunks wake " +
                 "on contact, and detached sections can land on intact structure. Four small dispatches per frame; " +
                 "buffers allocate lazily when first used.")]
        public bool selfCollision = false;
        [Range(0f, 1f)] [Tooltip("How firmly overlapping fragments separate each frame (1 = pop fully apart; lower = softer piles).")]
        public float selfCollisionStrength = 0.6f;

        [Header("Soft body (cheap shader wobble — no sim cost)")]
        public bool softBody = false;
        [Range(0f, 1f)] public float softness = 0.3f;

        // ---------------- PARTICLE DEBUG (play mode) ----------------
        [Header("Particle Debug")]
        [Tooltip("Draw the per-vertex particles in Play — one point at every fragment vertex, resolved from the same " +
                 "rigid-fragment buffers that drive the mesh. Shows how the geometry is attached to the simulation.")]
        public bool showParticles = false;
        [Tooltip("Point size in world units.")]
        [Min(0f)] public float particleSize = 0.02f;
        [Tooltip("Colour each particle by its fragment (off = single tint).")]
        public bool particleColorByChunk = true;
        [ColorUsage(true, true)] public Color particleColor = new(0.4f, 0.8f, 1f, 1f);

        // ---------------- Preview ----------------
        [Header("Preview (edit mode)")]
        [Tooltip("Automatically rebuild a transient draft shortly after an authoring value changes.")]
        public bool autoPreview = true;
        [Tooltip("How the draft is inspected. Fragments adds stable per-piece colours; X-Ray shows the cell wire; " +
                 "Slice clips the solid with an editable plane.")]
        public AuthoringPreviewMode previewMode = AuthoringPreviewMode.Fragments;
        public bool showSeeds = false;
        [Tooltip("Draw the target bounds box the seeds scatter within.")]
        public bool showBounds = true;
        [Min(0.001f)] public float seedSize = 0.02f;
        [Tooltip("Draw the solid fragment cells.")]
        public bool showCells = true;
        [Tooltip("Draw a batched per-fragment wireframe. Large bakes are decimated by Wire Budget but every fragment is represented.")]
        public bool showWire = false;
        [Tooltip("Push fragments out from the centre, measured in average fragment radii. Preview-only: it never " +
                 "changes baked geometry or the target silhouette.")]
        [Min(0f)] public float previewExplode = 0f;
        [Tooltip("Slice-plane normal in target-local space.")]
        public Vector3 previewSliceNormal = Vector3.right;
        [Tooltip("Slice-plane offset from the bounds centre, normalized to the target bounds diagonal.")]
        [Range(-0.75f, 0.75f)] public float previewSliceOffset = 0f;
        [Tooltip("Approximate maximum line segments in the Scene wireframe preview. Every fragment is represented; " +
                 "large bakes draw a decimated wire so the editor stays responsive.")]
        [Min(3000)] public int previewWireSegments = 300000;

        public static readonly System.Collections.Generic.List<BrazeVoronoiFracture> Active = new();

        int _allocCount = -1;

        const int MaxNb = 32;   // must match MAX_NB in BrazeVoronoi.compute
        const int BakeBlobMagic = 0x56584642; // "BFXV"
        const int GeometryBakeVersion = 6;    // v6: disconnected cell islands split; tight-contact structural graph
        const int PreviewWireAbsoluteMaxSegments = 1500000;

        ComputeShader _cs;
        int _kGenSeeds = -1, _kMarkField = -1, _kMarkUnsupported = -1, _kCollide = -1, _kSettle = -1;
        int _kClearEffectors = -1, _kApplySingleEffector = -1, _kPushApartEffector = -1,
            _kApplyDelayEffector = -1, _kApplyDelaySpringEffector = -1;
        int _kSeedCohesion = -1, _kSolveCohesion = -1, _kApplyCohesion = -1;
        int _kClearClusterRelease = -1, _kMarkClusterRelease = -1, _kExpandClusterRelease = -1;
        int _kIslandLabelInit = -1, _kIslandLabelProp = -1, _kIslandLabelCompress = -1;
        int _kIslandAccumClear = -1, _kIslandAccumCount = -1, _kIslandAccumInertia = -1;
        int _kIslandFinalizeRoots = -1, _kIslandCreatePrepare = -1, _kIslandCreateActivate = -1;
        int _kIslandFrameBegin = -1, _kIslandApplyContacts = -1, _kIslandProject = -1;
        int _kRouteImpactsIslands = -1;
        int _kSelfGridClear = -1, _kSelfGridInsert = -1, _kSelfActivateRestPairs = -1, _kSelfCollide = -1;
        BrazeFractureClipper _clipper;                 // CPU clip-and-cap fracturer (closed cells) — runs ONLY in Bake()
        BrazeFractureClipper.Frag[] _frags;            // topology chunks: disconnected islands split after clipping/load
        BrazeFractureBake _loadedBake;                 // which bake the current render state was built from
        string _loadedSignature;                       // its signature, so a re-bake / param change forces a reload
        ComputeBuffer _seedBuffer;
        ComputeBuffer _neighborBuf, _neighborCountBuf; // tight-contact structural adjacency graph
        ComputeBuffer _supportLevelBuf;                // distance from the automatically anchored boundary
        ComputeBuffer _cohesionGroupBuf, _cohesionGroupCenterBuf;
        Vector4[] _seedsCpu;
        bool[] _seedInside;                            // per-seed: inside the solid after rejection (clipper skips outside)
        Vector4[] _cellCentroidCpu;                    // per-fragment local centroid (xyz) + radius (w)
        Vector4[] _centroidWorldCpu;
#if UNITY_EDITOR
        bool _seedsReady;
#endif
        bool _dirty = true;
        Bounds _localBounds = new(Vector3.zero, Vector3.one);
        Renderer _trackedRenderer;
        Mesh _trackedMesh;
        Bounds _trackedBounds;
        Matrix4x4 _boundL2W, _boundN2W;
        bool _sourceStateValid, _transformBindingsValid;

        public int SeedCount => pointCount;
        /// <summary>Actual independently simulated pieces after disconnected geometry inside a seed cell is split.</summary>
        public int TopologyChunkCount => _frags != null ? _frags.Length : bake != null ? bake.fragmentCount : 0;

        // ---------------- runtime (play mode): rigid-fragment sim + Chunk render tail ----------------
        ComputeShader _simCS;
        int _kSim = -1, _kRelease = -1, _kTagImpactFluid = -1, _kApplyImpacts = -1;
        int _kDebrisReleased = -1, _kDebrisFree = -1, _kCopyClumpHits = -1;
        GraphicsBuffer _debrisReqDummy, _debrisReqCountDummy;
        GraphicsBuffer _impactBuf;
        const int SpawnRequestStride = 48;   // must match SpawnRequest in BrazeParticles.compute (3 × float4)
        Material _chunkMat;
        bool _interiorHasTexture;
        Material _pointsMat;
        Mesh _chunkMesh;
        // The cells render through a real MeshRenderer (not Graphics.DrawMesh) so URP includes them in EVERY pass —
        // depth, depth-normals, shadows, motion vectors. DrawMesh geometry is skipped by the depth prepass, which
        // left the cells out of the camera depth texture (SSAO / fog / soft-particles / depth-fade ignored them),
        // so they looked like a flat layer pasted over the scene.
        GameObject _chunkGo;
        MeshRenderer _chunkRenderer;
        GraphicsBuffer _posStateBuf, _rotBuf, _lifeBuf, _velBuf, _angVelBuf;     // per fragment
        GraphicsBuffer _deformedPosBuf, _deformedNrmBuf, _freeOffsetBuf, _freeNrmBuf; // per vertex
        GraphicsBuffer _clumpRangeBuf, _shatteredIdsBuf, _clumpHitBuf, _clumpMassBuf;
        GraphicsBuffer _clumpHitBackupBuf;
        GraphicsBuffer _impactFluidStateBuf;   // xyz emission rates + remaining seconds, written/read entirely on GPU
        GraphicsBuffer _impactFluidMotionBuf;  // packed half2: buoyancy + fragment-velocity inheritance
        GraphicsBuffer _impactFluidGeometryBuf; // local surface direction/bias + authored duration
        GraphicsBuffer _impactFluidVelocityBuf; // local ejecta velocity + breakup speed
        GraphicsBuffer _supportHitBuf;        // private selection flags; does not overwrite field/impact debris events
        GraphicsBuffer _cohesionStateBuf;
        GraphicsBuffer _cohesionGroupReleaseBuf;

        // Rigid-cluster state (see BrazeVoronoi.compute "RIGID CLUSTERS"). Root-indexed slots in cell-sized buffers.
        GraphicsBuffer _islandOfBuf, _islandPosStateBuf, _islandRotBuf, _islandVelBuf, _islandAngVelBuf;
        GraphicsBuffer _islandDataBuf, _islandRestOffsetBuf, _islandAccumBuf;
        bool _islandsArmed;
        const int IslandAccumInts = 24;   // must match ISLAND_ACC / SIM_ISLAND_ACC in the compute files

        // Self-collision spatial hash (fragment-vs-fragment separation). A linked bucket list has no occupancy cap;
        // immutable snapshots make the response a real Jacobi pass instead of an in-dispatch read/write race.
        GraphicsBuffer _selfGridHeadBuf, _selfGridNextBuf, _selfSnapshotBuf, _selfRadiusBuf;
        GraphicsBuffer _selfRestRangeBuf, _selfRestEntryBuf, _selfRestPairBuf, _selfPairActiveBuf;
        GraphicsBuffer _selfWakeBuf, _selfStatsBuf;
        int _selfBucketTotal, _selfRestPairCount;
        float _selfMaxRadiusLocal;
        long _selfGpuBytes;
#if UNITY_EDITOR || UNITY_ENABLE_CHECKS
        AsyncGPUReadbackRequest _selfStatsRequest;
        bool _selfStatsRequestActive;
        int _selfStatsNextFrame;
#endif
        int _selfContactPairs, _selfCollidingBodies, _selfSuppressedContacts, _selfWakeRequests;

        /// <summary>Debug: unique fragment-contact pairs resolved by the last periodic Self Collision readback.</summary>
        public int SelfCollisionContacts => _selfContactPairs;
        /// <summary>Debug: free bodies that responded to Self Collision last readback.</summary>
        public int SelfCollisionBodies => _selfCollidingBodies;
        /// <summary>Debug: assembled rest-pair responses muted until those proxies separate.</summary>
        public int SelfCollisionSuppressedContacts => _selfSuppressedContacts;
        /// <summary>Debug: sleeping body/cluster wake requests queued by fragment contacts.</summary>
        public int SelfCollisionWakeRequests => _selfWakeRequests;
        /// <summary>Lazily allocated GPU working set for Self Collision.</summary>
        public long SelfCollisionGpuBytes => _selfGpuBytes;
        GraphicsBuffer _cohesionPositionDeltaBuf, _cohesionVelocityDeltaBuf;
        GraphicsBuffer _cohesionAngularDeltaBuf, _cohesionRotationTargetBuf;
        GraphicsBuffer _clumpCentroidBuf;     // per fragment: world anchored centroid (for the break field)
        GraphicsBuffer _effectorPositionBuf, _effectorPositionScratchBuf;
        GraphicsBuffer _effectorRotationBuf, _effectorScaleBuf; // direct-transform result per fragment
        GraphicsBuffer _effectorBuf, _effectorFalloffBuf, _effectorFunctionBuf, _effectorAuxBuf;
        GraphicsBuffer _forceBuf, _colliderBuf;
        GraphicsBuffer _forceFalloffBuf;      // forceCount × 256 floats (flat) — each force's centre→edge falloff curve
        readonly System.Collections.Generic.List<BrazeParticleForce> _forceScratch = new();
        readonly System.Collections.Generic.List<BrazeParticleCollider> _colliderScratch = new();
        readonly System.Collections.Generic.List<BrazeParticleSystem> _debrisEmitterScratch = new();
        readonly System.Collections.Generic.List<float> _debrisEmitterSpawnScratch = new();
        readonly System.Collections.Generic.List<int> _debrisEmitterBudgetScratch = new();
        readonly System.Collections.Generic.List<uint> _debrisEmitterSaltScratch = new();
        readonly System.Collections.Generic.List<BrazeFractureEffector> _effectorScratch = new();
        readonly System.Collections.Generic.List<Vector4> _effectorAuxScratch = new();
        readonly BrazeDelayGpuStateCollection _delayStates = new();
        GpuParticleForce[] _forceData = new GpuParticleForce[8];
        GpuParticleCollider[] _colliderData = new GpuParticleCollider[8];
        GpuFractureEffector[] _effectorData = new GpuFractureEffector[4];
        GpuFractureImpact[] _impactData;
        Vector4[] _effectorAuxData = new Vector4[16];
        float[] _falloffData = new float[256];
        float[] _effectorFalloffData = new float[256];
        float[] _effectorFunctionData = new float[256];
        int _falloffHash;                     // last-uploaded falloff-atlas signature; skips the rebake when unchanged
        int _effectorFalloffHash;
        int _effectorFunctionHash;
        int _effectorGpuHash;
        bool _effectorGpuHashValid;
        int _frameForceCount, _frameColliderCount, _frameEffectorCount;
        int _queuedImpactCount, _impactSeedSequence, _droppedImpactCount;
        bool _queuedImpactFluid;

        // CPU shadow of which cells have been released, refreshed by a rolling async readback of _posStateBuf
        // (~40 KB, no GPU stall, one request in flight). The projectile narrow phase uses it to ignore the baked
        // triangles of cells that already flew away, so shots pass through carved openings and reach the interior
        // instead of contacting the ghost of the old shell. The few-frame latency is invisible: fresh debris still
        // occupies the gap while the readback drains.
        AsyncGPUReadbackRequest _cellStateRequest;
        bool _cellStateRequestActive;
        uint[] _cellFreeMask;

        // Live cell positions + island membership kept from the same rolling readbacks, so the bullet narrow phase
        // can hit DETACHED rigid clusters at their current pose (sphere tests — baked triangles no longer apply
        // once a section has moved). Membership is a second tiny request (~10 KB); positions ride the existing one.
        Vector4[] _cellLivePosCpu;
        uint[] _islandOfCpu;
        AsyncGPUReadbackRequest _islandOfRequest;
        bool _islandOfRequestActive;
        Vector3 _liveMemberBoundsMin, _liveMemberBoundsMax;
        bool _liveMemberBoundsValid;
        int _supportPassesRemaining, _supportGraphDepth, _supportGraphConfigHash, _cohesionGroupConfigHash;
        int _cohesionGroupCount;
        int _lastFieldReleaseHash;
        bool _fieldReleaseHashValid;
        float _cohesionTimeRemaining;
        float _impactFluidRemaining;
        float _impactFluidCellFraction;
        BrazeContainer _impactFluidContainer;
        int _runtimeCells, _runtimeVerts;

        /// <summary>Total impact commands refused because the per-frame queue was full.</summary>
        public int DroppedImpactCount => _droppedImpactCount;

#if UNITY_EDITOR
        Mesh _previewWireMesh;
        Material _previewWireMat;
        BrazeFractureClipper.Frag[] _previewWireSource;
        float _previewWireExplode = -1f;
        int _previewWireSegmentBudget = -1;
        string _renderGeometrySignature;
        bool _renderIsDraft;
        readonly System.Collections.Generic.List<Vector3> _previewWireVerts = new();
        readonly System.Collections.Generic.List<Color32> _previewWireColors = new();
        readonly System.Collections.Generic.List<Vector2> _previewWireIds = new();
        readonly System.Collections.Generic.List<Vector3> _previewWirePush = new();
        readonly System.Collections.Generic.List<int> _previewWireIndices = new();
#endif

        // ---- bake diagnostics (non-serialized; shown in the inspector + logged on Bake) ----
        [System.NonSerialized] public int bakeFragments;
        [System.NonSerialized] public int bakeTris;
        [System.NonSerialized] public int bakeVerts;
        [System.NonSerialized] public int bakeEmptyCells;
        [System.NonSerialized] public long bakeMs;
        [System.NonSerialized] public string bakeReport;
        float _avgRadius = 0.1f;
        bool _renderReady, _releaseOnReady;
#if UNITY_EDITOR
        int _editorEffectorHash;
        bool _editorEffectorHashValid;
        double _nextEditorEffectorTick;
        const double EditorEffectorInterval = 1.0 / 60.0;
#endif

        void Start()
        {
            if (Application.isPlaying && releaseOnPlay) _releaseOnReady = true;
        }

        // Runs in edit AND play (ExecuteAlways). In edit it just builds + draws the textured fracture; in play it
        // also steps the sim. So the cells look identical (real material, triplanar, detail) in both modes.
        void Update()
        {
            if (!Application.isPlaying) Tick();
        }

        void LateUpdate()
        {
            if (Application.isPlaying) Tick();
        }

        void Tick()
        {
            DetectSourceChanges();
            if (_dirty) { _dirty = false; RefreshFromBake(); }

            if (!_renderReady || _chunkMesh == null) { SetSourceVisible(true); return; }
            SyncSourceTransformBindings();
            if (structuralSupport) RefreshSupportGraphIfNeeded();
            if (structuralSupport && cohesiveCollapse) RefreshCohesionGroupsIfNeeded();

            if (Application.isPlaying)
            {
                float dt = Mathf.Min(Time.deltaTime, 1f / 30f);
                _frameForceCount = GatherGpuForces();
                _frameColliderCount = GatherGpuColliders();
                RunIslandFrameBegin(dt);   // clear island accumulators + integrate BEFORE anything feeds them
                if (breakMode == BreakMode.Field) RunFieldRelease(dt);
                else _fieldReleaseHashValid = false;
                PollCellStateReadback();
                RunQueuedImpacts();
                RunStructuralSupport();
                RunSim(dt);
                RunCohesion(dt);
                RunSettle(dt);
                // Rigid clusters need the shape-aware contact pass for their ground response even when the user
                // authored it off for loose debris — _IslandOnlyContacts restricts it to members in that case.
                if (shapeAwareCollision || (_islandsArmed && rigidClusters)) RunCollide(dt);
                RunSelfCollision();        // fragment-vs-fragment separation (islands feed their accumulators)
                RunIslandFinish(dt);       // apply aggregated contacts, break/sleep, project members
                RunDebris(dt);
            }

            RunEffectors();

            ApplyRenderTimeDetail();   // live: detail/triplanar knobs update without re-fracturing
            SetSourceVisible(true);   // Hide Original is the single control for source mesh visibility.
            EnsureChunkRenderer();    // depth-correct cell rendering via a real MeshRenderer (in all URP passes)

            if (showParticles && _pointsMat != null && _runtimeVerts > 0)
            {
                _pointsMat.SetFloat("_PointSize", particleSize);
                _pointsMat.SetFloat("_ColorByChunk", particleColorByChunk ? 1f : 0f);
                _pointsMat.SetColor("_PointColor", particleColor);
                var bounds = new Bounds(transform.position, Vector3.one * 1e5f);
                Graphics.DrawProcedural(_pointsMat, bounds, MeshTopology.Triangles, _runtimeVerts * 6, 1,
                                        null, null, UnityEngine.Rendering.ShadowCastingMode.Off, false, gameObject.layer);
            }

        }

        // Host the cells on a hidden MeshRenderer (identity transform — the shader places verts in world space).
        // Unlike Graphics.DrawMesh, a MeshRenderer is rendered in URP's depth/depth-normals/shadow prepasses, so the
        // cells write to the camera depth texture and integrate with SSAO / fog / soft particles / depth fade.
        void EnsureChunkRenderer()
        {
            if (_chunkMesh == null || _chunkMat == null) return;
            if (_chunkGo == null)
            {
                _chunkGo = new GameObject("BrazeVoronoiChunks") { hideFlags = HideFlags.HideAndDontSave };
                _chunkGo.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            }
            if (!_chunkGo.TryGetComponent(out MeshFilter mf)) mf = _chunkGo.AddComponent<MeshFilter>();
            if (_chunkRenderer == null && !_chunkGo.TryGetComponent(out _chunkRenderer))
                _chunkRenderer = _chunkGo.AddComponent<MeshRenderer>();
            if (mf.sharedMesh != _chunkMesh) mf.sharedMesh = _chunkMesh;
            if (_chunkRenderer.sharedMaterial != _chunkMat) _chunkRenderer.sharedMaterial = _chunkMat;
            Renderer source = SourceRenderer;
            if (source != null)
            {
                // The host lives at the world origin because the shader emits world-space vertices. Explicitly carry
                // the source's renderer settings across so Unity does not sample light probes at that unrelated host
                // transform or silently change the object's rendering/light layers during the material handoff.
                _chunkRenderer.shadowCastingMode = source.shadowCastingMode;
                _chunkRenderer.receiveShadows = source.receiveShadows;
                _chunkRenderer.lightProbeUsage = source.lightProbeUsage;
                _chunkRenderer.reflectionProbeUsage = source.reflectionProbeUsage;
                _chunkRenderer.probeAnchor = source.probeAnchor != null ? source.probeAnchor : Space;
                _chunkRenderer.renderingLayerMask = source.renderingLayerMask;
            }
            else
            {
                _chunkRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                _chunkRenderer.receiveShadows = true;
            }
            if (_chunkGo.layer != gameObject.layer) _chunkGo.layer = gameObject.layer;
            bool wantCells = showCells;
#if UNITY_EDITOR
            if (!Application.isPlaying && previewMode == AuthoringPreviewMode.XRay) wantCells = false;
#endif
            if (_chunkRenderer.enabled != wantCells) _chunkRenderer.enabled = wantCells;
        }

        // Source mesh visibility is controlled only by Hide Original, so its renderer/material remains editable
        // while the fracture preview is shown.
        void SetSourceVisible(bool visible)
        {
            var r = SourceRenderer;
            if (r == null) return;
            // The closed fragments reproduce the whole mesh, so the source renderer is hidden once they exist
            // (else it z-fights the assembled fragments).
            bool want = visible && !hideOriginal && !_renderReady;
            if (r.enabled != want) r.enabled = want;   // avoid dirtying the scene every frame in edit mode
        }

        void DetectSourceChanges()
        {
            var r = SourceRenderer;
            var mesh = SourceMesh;
            Bounds b = ResolveLocalBounds();
            if (!_sourceStateValid || r != _trackedRenderer || mesh != _trackedMesh || !SameBounds(b, _trackedBounds))
            {
                _dirty = true;
                _trackedRenderer = r;
                _trackedMesh = mesh;
                _trackedBounds = b;
                _sourceStateValid = true;
            }
        }

        static bool SameBounds(Bounds a, Bounds b)
            => (a.center - b.center).sqrMagnitude < 1e-10f && (a.size - b.size).sqrMagnitude < 1e-10f;

        static Matrix4x4 NormalMatrix(Matrix4x4 l2w) => l2w.inverse.transpose;

        static float MaxAbsComponent(Vector3 v)
            => Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));

        float WorldRadiusScale()
        {
            Transform t = Space;
            return t != null ? Mathf.Max(MaxAbsComponent(t.lossyScale), 1e-4f) : 1f;
        }

        void SyncSourceTransformBindings(bool force = false)
        {
            Transform t = Space;
            Matrix4x4 l2w = t != null ? t.localToWorldMatrix : Matrix4x4.identity;
            Matrix4x4 n2w = NormalMatrix(l2w);

            if (_chunkMat != null)
            {
                _chunkMat.SetMatrix("_L2W", l2w);
                _chunkMat.SetMatrix("_N2W", n2w);
            }
            if (_pointsMat != null)
                _pointsMat.SetMatrix("_L2W", l2w);

            if (!force && _transformBindingsValid && _boundL2W == l2w && _boundN2W == n2w) return;
            _boundL2W = l2w;
            _boundN2W = n2w;
            _transformBindingsValid = true;
            UploadWorldCentroids(l2w);
        }

        void UploadWorldCentroids(Matrix4x4 l2w)
        {
            if (_clumpCentroidBuf == null || _cellCentroidCpu == null) return;
            int cells = _cellCentroidCpu.Length;
            if (_centroidWorldCpu == null || _centroidWorldCpu.Length != cells)
                _centroidWorldCpu = new Vector4[cells];

            float radiusScale = WorldRadiusScale();
            for (int c = 0; c < cells; c++)
            {
                Vector4 local = _cellCentroidCpu[c];
                Vector3 w = l2w.MultiplyPoint3x4(new Vector3(local.x, local.y, local.z));
                _centroidWorldCpu[c] = new Vector4(w.x, w.y, w.z, local.w * radiusScale);
            }
            _clumpCentroidBuf.SetData(_centroidWorldCpu, 0, 0, cells);
        }

        bool EnsureSimCompute()
        {
            if (_simCS != null) return true;
            var asset = Resources.Load<ComputeShader>("BrazeDisintegrateSim");
            if (asset == null) { Debug.LogError("[BrazeVoronoiFracture] Resources/BrazeDisintegrateSim.compute not found."); return false; }
            _simCS = Instantiate(asset);   // instance so per-component kernel bindings don't collide
            _kSim = _simCS.FindKernel("SimClumps");
            _kRelease = _simCS.FindKernel("ReleaseClumps");
            _kTagImpactFluid = _simCS.FindKernel("TagImpactFluid");
            _kApplyImpacts = _simCS.FindKernel("ApplyFractureImpacts");
            _kRouteImpactsIslands = _simCS.FindKernel("RouteImpactsToIslands");
            _kDebrisReleased = _simCS.FindKernel("DebrisFromReleased");
            _kDebrisFree = _simCS.FindKernel("DebrisFromFreeClumps");
            _kCopyClumpHits = _simCS.FindKernel("CopyClumpHits");
            return _kSim >= 0 && _kRelease >= 0;
        }

        // Assemble the rigid-fragment buffers + Chunk mesh from the CPU clip-and-cap fragments (source-local space),
        // then upload + bind. Source transform changes are applied live through _L2W/_N2W so anchored cells stay synced.
        void BuildRenderState()
        {
            if (!EnsureSimCompute() || _frags == null) return;
            ReleaseRuntime();

            int cells = _frags.Length;
            float[] fragmentMasses = BuildFragmentMassData(_frags);

            var dpos = new System.Collections.Generic.List<Vector3>();
            var dnrm = new System.Collections.Generic.List<Vector3>();
            var sids = new System.Collections.Generic.List<Vector2Int>();
            var meshPos = new System.Collections.Generic.List<Vector3>();
            var meshUv0 = new System.Collections.Generic.List<Vector4>();   // xy = albedo UV, z = 1 for surface verts (real UV)
            var meshUv1 = new System.Collections.Generic.List<Vector2>();
            var meshTris = new System.Collections.Generic.List<int>();
            var ranges = new Vector2Int[cells];
            var nbFlat = new int[Mathf.Max(1, cells * MaxNb)];
            var nbCount = new uint[Mathf.Max(1, cells)];
            if (_cellCentroidCpu == null || _cellCentroidCpu.Length != cells) _cellCentroidCpu = new Vector4[cells];
            float radSum = 0f; int radN = 0;
            float edgeWeld = Mathf.Max(_localBounds.size.magnitude * 1e-5f, 1e-6f);

            int v = 0;
            for (int c = 0; c < cells; c++)
            {
                var f = _frags[c];
                int start = v;
                int tc = f.TriCount;
                int[] edgeMasks = BuildFragmentEdgeMasks(f, edgeWeld);
                for (int t = 0; t < tc; t++)
                {
                    int o = t * 3;
                    for (int e = 0; e < 3; e++)
                        AddVert(dpos, dnrm, sids, meshPos, meshUv0, meshUv1, meshTris, c, ref v,
                                f.pos[o + e], f.nrm[o + e], f.uv[o + e], f.skin[o + e], edgeMasks[t]);
                }
                ranges[c] = new Vector2Int(start, v - start);
                _cellCentroidCpu[c] = new Vector4(f.centroid.x, f.centroid.y, f.centroid.z, f.radius);
                if (f.radius > 0f) { radSum += f.radius; radN++; }

                int m = Mathf.Min(f.neighbors.Count, MaxNb);
                for (int k = 0; k < m; k++) nbFlat[c * MaxNb + k] = f.neighbors[k];
                nbCount[c] = (uint)m;
            }

            _runtimeCells = cells;
            _runtimeVerts = v;
            _avgRadius = radN > 0 ? radSum / radN : 0.1f;
            if (v == 0) return;

            // Tight-contact adjacency and its rooted support levels come from the normalized topology graph.
            _neighborBuf?.Release(); _neighborBuf = new ComputeBuffer(nbFlat.Length, 4); _neighborBuf.SetData(nbFlat);
            _neighborCountBuf?.Release(); _neighborCountBuf = new ComputeBuffer(nbCount.Length, 4); _neighborCountBuf.SetData(nbCount);
            int[] supportLevels = BuildSupportLevels(cells);
            _supportLevelBuf?.Release(); _supportLevelBuf = new ComputeBuffer(Mathf.Max(1, cells), 4);
            _supportLevelBuf.SetData(supportLevels);
            RefreshCohesionGroups(cells, true);

            _chunkMesh = new Mesh { name = "BrazeVoronoiChunks", hideFlags = HideFlags.HideAndDontSave };
            _chunkMesh.indexFormat = v > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
            _chunkMesh.SetVertices(meshPos);
            _chunkMesh.SetUVs(0, meshUv0);
            _chunkMesh.SetUVs(1, meshUv1);
            _chunkMesh.SetTriangles(meshTris, 0);
            _chunkMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e5f);   // never frustum-cull (shader places verts)

            // per-fragment state buffers (start anchored = assembled solid)
            _posStateBuf = NewBuf(cells, 16); _rotBuf = NewBuf(cells, 16); _lifeBuf = NewBuf(cells, 16);
            _velBuf = NewBuf(cells, 16); _angVelBuf = NewBuf(cells, 16);
            var posState = new Vector4[cells]; var rot = new Vector4[cells]; var life = new Vector4[cells];
            for (int c = 0; c < cells; c++) { rot[c] = new Vector4(0, 0, 0, 1); life[c] = new Vector4(1, 0, 0, 0); }
            _posStateBuf.SetData(posState); _rotBuf.SetData(rot); _lifeBuf.SetData(life);
            _velBuf.SetData(new Vector4[cells]); _angVelBuf.SetData(new Vector4[cells]);
            _clumpMassBuf = NewBuf(cells, 4); _clumpMassBuf.SetData(fragmentMasses);

            // per-vertex buffers
            _deformedPosBuf = NewBuf(v, 12); _deformedPosBuf.SetData(dpos);
            _deformedNrmBuf = NewBuf(v, 12); _deformedNrmBuf.SetData(dnrm);
            _freeOffsetBuf = NewBuf(v, 12); _freeNrmBuf = NewBuf(v, 12);
            _shatteredIdsBuf = NewBuf(v, 8); _shatteredIdsBuf.SetData(sids.ToArray());
            _clumpRangeBuf = NewBuf(cells, 8); _clumpRangeBuf.SetData(ranges);
            _clumpHitBuf = NewBuf(cells, 4); _clumpHitBuf.SetData(new uint[cells]);
            _impactFluidStateBuf = NewBuf(cells, 16); _impactFluidStateBuf.SetData(new Vector4[cells]);
            _impactFluidMotionBuf = NewBuf(cells, 4); _impactFluidMotionBuf.SetData(new uint[cells]);
            _impactFluidGeometryBuf = NewBuf(cells, 16); _impactFluidGeometryBuf.SetData(new Vector4[cells]);
            _impactFluidVelocityBuf = NewBuf(cells, 16); _impactFluidVelocityBuf.SetData(new Vector4[cells]);
            _supportHitBuf = NewBuf(cells, 4); _supportHitBuf.SetData(new uint[cells]);
            _cohesionStateBuf = NewBuf(cells, 4); _cohesionStateBuf.SetData(new uint[cells]);

            // rigid-cluster island state (root-indexed slots; _islandOfBuf starts all-none, islands inactive)
            _islandOfBuf = NewBuf(cells, 4);
            var islandNone = new uint[cells];
            for (int c = 0; c < cells; c++) islandNone[c] = 0xffffffffu;
            _islandOfBuf.SetData(islandNone);
            _islandPosStateBuf = NewBuf(cells, 16); _islandPosStateBuf.SetData(new Vector4[cells]);
            _islandRotBuf = NewBuf(cells, 16);
            _islandVelBuf = NewBuf(cells, 16);
            _islandAngVelBuf = NewBuf(cells, 16);
            _islandDataBuf = NewBuf(cells, 16);
            _islandRestOffsetBuf = NewBuf(cells, 16);
            _islandAccumBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cells * IslandAccumInts, 4);
            _islandAccumBuf.SetData(new int[cells * IslandAccumInts]);
            _islandsArmed = false;

            // world anchored centroids for the break field; refreshed whenever the source transform changes
            _clumpCentroidBuf = NewBuf(cells, 16);
            _effectorPositionBuf = NewBuf(cells, 16);
            _effectorPositionScratchBuf = NewBuf(cells, 16);
            _effectorRotationBuf = NewBuf(cells, 16);
            _effectorScaleBuf = NewBuf(cells, 16);

            BuildChunkMaterial();
            _renderReady = true;   // source-renderer visibility is handled per-frame in Update/SetSourceVisible
            SyncSourceTransformBindings(true);
            if (_releaseOnReady) { _releaseOnReady = false; ReleaseAll(); }
        }

        // Closed triangle-soup volume, normalized so an average authored cell has mass 1. Using tetrahedra from
        // the fragment centroid and absolute signed volume makes this insensitive to cap winding while retaining
        // the real distinction between a thin splinter and a solid block. The clamp only protects malformed or
        // near-zero geometry from destabilizing mass ratios; world-scale density cancels in the normalization.
        internal static float[] BuildFragmentMassData(BrazeFractureClipper.Frag[] fragments)
        {
            int count = fragments != null ? fragments.Length : 0;
            var raw = new float[count];
            double sum = 0.0;
            int valid = 0;
            for (int c = 0; c < count; c++)
            {
                BrazeFractureClipper.Frag fragment = fragments[c];
                if (fragment == null || fragment.pos == null) continue;
                Vector3 centre = fragment.centroid;
                double volume = 0.0;
                int triangleCount = fragment.pos.Count / 3;
                for (int t = 0; t < triangleCount; t++)
                {
                    int o = t * 3;
                    Vector3 a = fragment.pos[o] - centre;
                    Vector3 b = fragment.pos[o + 1] - centre;
                    Vector3 d = fragment.pos[o + 2] - centre;
                    volume += System.Math.Abs(Vector3.Dot(a, Vector3.Cross(b, d))) / 6.0;
                }
                if (volume <= 1e-12 || double.IsNaN(volume) || double.IsInfinity(volume)) continue;
                raw[c] = (float)volume;
                sum += volume;
                valid++;
            }

            float average = valid > 0 ? (float)(sum / valid) : 1f;
            average = Mathf.Max(average, 1e-12f);
            for (int c = 0; c < count; c++)
            {
                float relative = raw[c] > 0f ? raw[c] / average : 1f;
                raw[c] = Mathf.Clamp(relative, 0.05f, 20f);
            }
            return raw;
        }

        readonly struct FragmentContactCandidate
        {
            public readonly float gapRatio;
            public readonly float centerRatio;
            public readonly int a, b;
            public FragmentContactCandidate(float gapRatio, float centerRatio, int a, int b)
            {
                this.gapRatio = gapRatio;
                this.centerRatio = centerRatio;
                this.a = a;
                this.b = b;
            }
        }

        // A single Voronoi cell can intersect many disconnected parts of a combined architectural mesh. Treating those
        // islands as one rigid body gives them one huge sphere and one transform, bridging courtyards, windows, and the
        // gaps between pillars. Split triangle-soup connectivity first, without changing a vertex or triangle.
        internal static BrazeFractureClipper.Frag[] SplitDisconnectedFragments(
            BrazeFractureClipper.Frag[] source, float weld, out int splitSourceCells)
        {
            splitSourceCells = 0;
            if (source == null || source.Length == 0) return Array.Empty<BrazeFractureClipper.Frag>();

            float inverseWeld = 1f / Mathf.Max(weld, 1e-7f);
            var result = new List<BrazeFractureClipper.Frag>(source.Length);
            for (int cell = 0; cell < source.Length; cell++)
            {
                BrazeFractureClipper.Frag fragment = source[cell];
                int triangleCount = fragment != null ? fragment.TriCount : 0;
                if (triangleCount <= 0) continue;

                var parent = new int[triangleCount];
                for (int t = 0; t < triangleCount; t++) parent[t] = t;
                var vertexOwner = new Dictionary<EdgePoint, int>(triangleCount * 2);
                for (int t = 0; t < triangleCount; t++)
                {
                    int o = t * 3;
                    for (int v = 0; v < 3; v++)
                    {
                        var key = new EdgePoint(fragment.pos[o + v], inverseWeld);
                        if (vertexOwner.TryGetValue(key, out int owner)) UnionTriangleSets(parent, t, owner);
                        else vertexOwner.Add(key, t);
                    }
                }

                var rootCounts = new Dictionary<int, int>();
                var rootsInOrder = new List<int>();
                for (int t = 0; t < triangleCount; t++)
                {
                    int root = FindTriangleRoot(parent, t);
                    parent[t] = root;
                    if (rootCounts.TryGetValue(root, out int count)) rootCounts[root] = count + 1;
                    else { rootCounts.Add(root, 1); rootsInOrder.Add(root); }
                }

                if (rootsInOrder.Count == 1)
                {
                    fragment.neighbors.Clear();
                    RecalculateFragmentMetrics(fragment);
                    result.Add(fragment);
                    continue;
                }

                splitSourceCells++;
                var rootToFragment = new Dictionary<int, BrazeFractureClipper.Frag>(rootsInOrder.Count);
                for (int i = 0; i < rootsInOrder.Count; i++)
                {
                    int root = rootsInOrder[i];
                    var part = new BrazeFractureClipper.Frag();
                    int vertexCapacity = rootCounts[root] * 3;
                    part.pos.Capacity = vertexCapacity;
                    part.nrm.Capacity = vertexCapacity;
                    part.uv.Capacity = vertexCapacity;
                    part.skin.Capacity = vertexCapacity;
                    rootToFragment.Add(root, part);
                    result.Add(part);
                }

                for (int t = 0; t < triangleCount; t++)
                {
                    BrazeFractureClipper.Frag part = rootToFragment[parent[t]];
                    int o = t * 3;
                    for (int v = 0; v < 3; v++)
                    {
                        int index = o + v;
                        part.pos.Add(fragment.pos[index]);
                        part.nrm.Add(index < fragment.nrm.Count ? fragment.nrm[index] : Vector3.up);
                        part.uv.Add(index < fragment.uv.Count ? fragment.uv[index] : Vector2.zero);
                        part.skin.Add(index < fragment.skin.Count ? fragment.skin[index] : 0f);
                    }
                }
                for (int i = 0; i < rootsInOrder.Count; i++)
                    RecalculateFragmentMetrics(rootToFragment[rootsInOrder[i]]);
            }
            return result.ToArray();
        }

        static int FindTriangleRoot(int[] parent, int node)
        {
            int root = node;
            while (parent[root] != root) root = parent[root];
            while (parent[node] != node)
            {
                int next = parent[node];
                parent[node] = root;
                node = next;
            }
            return root;
        }

        static void UnionTriangleSets(int[] parent, int a, int b)
        {
            int rootA = FindTriangleRoot(parent, a);
            int rootB = FindTriangleRoot(parent, b);
            if (rootA == rootB) return;
            if (rootA < rootB) parent[rootB] = rootA;
            else parent[rootA] = rootB;
        }

        static void RecalculateFragmentMetrics(BrazeFractureClipper.Frag fragment)
        {
            if (fragment == null || fragment.pos.Count == 0)
            {
                if (fragment != null) { fragment.centroid = Vector3.zero; fragment.radius = 0f; }
                return;
            }
            Vector3 centroid = Vector3.zero;
            for (int i = 0; i < fragment.pos.Count; i++) centroid += fragment.pos[i];
            centroid /= fragment.pos.Count;
            float radiusSq = 0f;
            for (int i = 0; i < fragment.pos.Count; i++)
                radiusSq = Mathf.Max(radiusSq, (fragment.pos[i] - centroid).sqrMagnitude);
            fragment.centroid = centroid;
            fragment.radius = Mathf.Sqrt(radiusSq);
        }

        static float BoundsGapSquared(Bounds a, Bounds b)
        {
            float dx = Mathf.Max(0f, Mathf.Max(a.min.x - b.max.x, b.min.x - a.max.x));
            float dy = Mathf.Max(0f, Mathf.Max(a.min.y - b.max.y, b.min.y - a.max.y));
            float dz = Mathf.Max(0f, Mathf.Max(a.min.z - b.max.z, b.min.z - a.max.z));
            return dx * dx + dy * dy + dz * dz;
        }

        // Rebuild support links from tight component bounds. Unlike the old centroid-sphere merge, this cannot bridge a
        // visible empty span merely because two long fragments have large radii. The small tolerance heals cap trimming
        // and floating-point seams; the neighbor cap keeps all GPU loops bounded.
        internal static int RebuildContactNeighbors(BrazeFractureClipper.Frag[] fragments, float boundsDiagonal)
        {
            int count = fragments != null ? fragments.Length : 0;
            if (count == 0) return 0;
            var bounds = new Bounds[count];
            var order = new int[count];
            for (int i = 0; i < count; i++)
            {
                BrazeFractureClipper.Frag fragment = fragments[i];
                fragment.neighbors.Clear();
                Bounds tight = new(fragment.pos[0], Vector3.zero);
                for (int v = 1; v < fragment.pos.Count; v++) tight.Encapsulate(fragment.pos[v]);
                bounds[i] = tight;
                order[i] = i;
            }
            Array.Sort(order, (a, b) => bounds[a].min.x.CompareTo(bounds[b].min.x));

            float baseTolerance = Mathf.Max(Mathf.Abs(boundsDiagonal) * 2e-5f, 1e-6f);
            var candidates = new List<FragmentContactCandidate>(count * 8);
            for (int oi = 0; oi < count; oi++)
            {
                int a = order[oi];
                float maxPairTolerance = Mathf.Max(baseTolerance, fragments[a].radius * 0.08f);
                for (int oj = oi + 1; oj < count; oj++)
                {
                    int b = order[oj];
                    if (bounds[b].min.x - bounds[a].max.x > maxPairTolerance) break;
                    float tolerance = Mathf.Max(baseTolerance,
                        Mathf.Min(fragments[a].radius, fragments[b].radius) * 0.08f);
                    float gapSq = BoundsGapSquared(bounds[a], bounds[b]);
                    if (gapSq > tolerance * tolerance) continue;
                    float reach = Mathf.Max(fragments[a].radius + fragments[b].radius, baseTolerance);
                    float centerRatio = (fragments[a].centroid - fragments[b].centroid).sqrMagnitude / (reach * reach);
                    candidates.Add(new FragmentContactCandidate(gapSq / (tolerance * tolerance), centerRatio, a, b));
                }
            }
            candidates.Sort((x, y) =>
            {
                int comparison = x.gapRatio.CompareTo(y.gapRatio);
                if (comparison == 0) comparison = x.centerRatio.CompareTo(y.centerRatio);
                if (comparison == 0) comparison = x.a.CompareTo(y.a);
                return comparison != 0 ? comparison : x.b.CompareTo(y.b);
            });

            int pairs = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                FragmentContactCandidate contact = candidates[i];
                if (fragments[contact.a].neighbors.Count >= MaxNb ||
                    fragments[contact.b].neighbors.Count >= MaxNb) continue;
                fragments[contact.a].neighbors.Add(contact.b);
                fragments[contact.b].neighbors.Add(contact.a);
                pairs++;
            }
            return pairs;
        }

        void PrepareTopologyAwareFragments(int authoredCells)
        {
            if (_frags == null) return;
            int sourceCount = _frags.Length;
            float diagonal = Mathf.Max(_localBounds.size.magnitude, 1e-4f);
            _frags = SplitDisconnectedFragments(_frags, Mathf.Max(diagonal * 1e-5f, 1e-6f),
                                                out int splitSourceCells);
            int contactPairs = RebuildContactNeighbors(_frags, diagonal);
            int graphComponents = CountFragmentGraphComponents(_frags);
            if (splitSourceCells > 0 || sourceCount != _frags.Length)
                Debug.Log($"[BrazeVoronoiFracture] '{name}': {authoredCells:n0} authored cells resolved to " +
                          $"{_frags.Length:n0} topology chunks; {splitSourceCells:n0} cells crossed disconnected mesh " +
                          $"islands. Built {contactPairs:n0} tight-contact support pairs in {graphComponents:n0} component(s).");
        }

        static int CountFragmentGraphComponents(BrazeFractureClipper.Frag[] fragments)
        {
            int count = fragments != null ? fragments.Length : 0;
            if (count == 0) return 0;
            var seen = new bool[count];
            var queue = new Queue<int>();
            int components = 0;
            for (int start = 0; start < count; start++)
            {
                if (seen[start]) continue;
                components++;
                seen[start] = true;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int cell = queue.Dequeue();
                    List<int> neighbors = fragments[cell].neighbors;
                    for (int i = 0; i < neighbors.Count; i++)
                    {
                        int neighbor = neighbors[i];
                        if ((uint)neighbor >= (uint)count || seen[neighbor]) continue;
                        seen[neighbor] = true;
                        queue.Enqueue(neighbor);
                    }
                }
            }
            return components;
        }

        int[] BuildSupportLevels(int cells)
        {
            var levels = new int[Mathf.Max(1, cells)];
            if (cells <= 0 || _frags == null)
            {
                _supportGraphDepth = 1;
                return levels;
            }

            for (int i = 0; i < cells; i++) levels[i] = -1;
            Vector3 direction = ResolveSupportAnchorDirection();
            float anchorProjection = float.NegativeInfinity;
            int lowestCell = 0;
            for (int c = 0; c < cells; c++)
            {
                var fragment = _frags[c];
                float projection = FragmentExtremeProjection(fragment, direction);
                if (projection <= anchorProjection) continue;
                anchorProjection = projection;
                lowestCell = c;
            }

            float thickness = Mathf.Max(1e-5f, _avgRadius * supportAnchorThickness);
            var frontier = new Queue<int>(Mathf.Min(cells, 256));
            for (int c = 0; c < cells; c++)
            {
                var fragment = _frags[c];
                float exposedProjection = FragmentExtremeProjection(fragment, direction);
                if (anchorProjection - exposedProjection > thickness) continue;
                levels[c] = 0;
                frontier.Enqueue(c);
            }
            if (frontier.Count == 0)
            {
                levels[lowestCell] = 0;
                frontier.Enqueue(lowestCell);
            }

            int maxLevel = 0;
            while (frontier.Count > 0)
            {
                int cell = frontier.Dequeue();
                var neighbors = _frags[cell].neighbors;
                int neighborCount = Mathf.Min(neighbors.Count, MaxNb);
                for (int i = 0; i < neighborCount; i++)
                {
                    int neighbor = neighbors[i];
                    if ((uint)neighbor >= (uint)cells || levels[neighbor] >= 0) continue;
                    levels[neighbor] = levels[cell] + 1;
                    maxLevel = Mathf.Max(maxLevel, levels[neighbor]);
                    frontier.Enqueue(neighbor);
                }
            }

            // A disconnected baked island has no route to the anchor. Its deterministic lateral chain will peel
            // away after the next destruction event instead of remaining suspended forever.
            int highestLevel = maxLevel + 1;
            for (int c = 0; c < cells; c++)
                if (levels[c] < 0) levels[c] = highestLevel;

            // Exact upper bound for the directed runtime graph, including deterministic same-level bridge edges.
            // This prevents a long lateral chain from exhausting its event budget and leaving its tail suspended.
            var dependencyDepth = new int[cells];
            int graphDepth = 1;
            for (int level = 1; level <= highestLevel; level++)
            {
                for (int c = 0; c < cells; c++)
                {
                    if (levels[c] != level) continue;
                    int deepestParent = 0;
                    var neighbors = _frags[c].neighbors;
                    int neighborCount = Mathf.Min(neighbors.Count, MaxNb);
                    for (int i = 0; i < neighborCount; i++)
                    {
                        int neighbor = neighbors[i];
                        if ((uint)neighbor >= (uint)cells) continue;
                        bool lowerParent = levels[neighbor] < level;
                        bool lateralParent = levels[neighbor] == level && neighbor < c &&
                                             SupportEdgeWeight(c, neighbor) <= lateralSupport;
                        if (lowerParent || lateralParent)
                            deepestParent = Mathf.Max(deepestParent, dependencyDepth[neighbor]);
                    }
                    dependencyDepth[c] = deepestParent + 1;
                    graphDepth = Mathf.Max(graphDepth, dependencyDepth[c] + 1);
                }
            }

            _supportGraphDepth = graphDepth;
            _supportGraphConfigHash = SupportGraphConfigHash();
            _supportPassesRemaining = 0;
            return levels;
        }

        static float FragmentExtremeProjection(BrazeFractureClipper.Frag fragment, Vector3 direction)
        {
            if (fragment == null || fragment.pos.Count == 0) return float.NegativeInfinity;
            float projection = float.NegativeInfinity;
            for (int i = 0; i < fragment.pos.Count; i++)
                projection = Mathf.Max(projection, Vector3.Dot(fragment.pos[i], direction));
            return projection;
        }

        void RefreshSupportGraphIfNeeded()
        {
            if (_frags == null || _runtimeCells <= 0 || SupportGraphConfigHash() == _supportGraphConfigHash) return;
            int[] supportLevels = BuildSupportLevels(_runtimeCells);
            if (_supportLevelBuf == null || _supportLevelBuf.count != _runtimeCells)
            {
                _supportLevelBuf?.Release();
                _supportLevelBuf = new ComputeBuffer(_runtimeCells, 4);
            }
            _supportLevelBuf.SetData(supportLevels);
        }

        int SupportGraphConfigHash()
        {
            unchecked
            {
                int hash = ResolveSupportAnchorDirection().GetHashCode();
                hash = hash * 31 + supportFollowsGravity.GetHashCode();
                hash = hash * 31 + supportAnchorThickness.GetHashCode();
                hash = hash * 31 + lateralSupport.GetHashCode();
                hash = hash * 31 + _runtimeCells;
                return hash;
            }
        }

        internal Vector3 ResolveSupportAnchorDirection()
        {
            if (supportFollowsGravity && forces != null)
            {
                float gravityStrength = 0f;
                for (int i = 0; i < forces.Count; i++)
                {
                    // C# type patterns use CLR null semantics: they still match a destroyed Unity wrapper, then
                    // accessing Behaviour.isActiveAndEnabled throws MissingReferenceException. Test Unity's
                    // overloaded null first so stale serialized force slots are simply ignored until reassigned.
                    BrazeParticleForce force = forces[i];
                    if (force == null) continue;
                    if (force is BrazeGravity gravity && gravity.isActiveAndEnabled)
                        gravityStrength += gravity.strength;
                }

                if (Mathf.Abs(gravityStrength) > 1e-6f)
                {
                    Vector3 worldDirection = gravityStrength < 0f ? Vector3.down : Vector3.up;
                    Transform space = Space;
                    Vector3 localDirection = space != null
                        ? space.localToWorldMatrix.transpose.MultiplyVector(worldDirection)
                        : worldDirection;
                    if (localDirection.sqrMagnitude > 1e-8f) return localDirection.normalized;
                }
            }

            return supportAnchorDirection.sqrMagnitude > 1e-8f
                ? supportAnchorDirection.normalized
                : Vector3.down;
        }

        static float SupportEdgeWeight(int cell, int neighbor)
        {
            uint value = unchecked((uint)cell * 0x9e3779b9u ^ (uint)neighbor * 0x85ebca6bu);
            value ^= value >> 16; value *= 0x7feb352du;
            value ^= value >> 15; value *= 0x846ca68bu;
            value ^= value >> 16;
            return (value & 0xffffu) / 65535f;
        }

        struct CohesionSettings
        {
            public int chunkSize, iterations;
            public float strength, damping, breakStrain, duration, topple, debrisFraction;
            public float breakImpulse;   // rigid-cluster material impact-speed threshold (m/s)
        }

        CohesionSettings ResolveCohesionSettings()
        {
            switch (collapseMaterial)
            {
                case CollapseMaterial.Wood:
                    return new CohesionSettings { chunkSize = 20, iterations = 2, strength = 0.88f, damping = 0.82f, breakStrain = 0.32f, duration = 3f, topple = 0.65f, debrisFraction = 0.06f, breakImpulse = 8f };
                case CollapseMaterial.Metal:
                    return new CohesionSettings { chunkSize = 30, iterations = 3, strength = 0.94f, damping = 0.92f, breakStrain = 0.55f, duration = 4f, topple = 0.45f, debrisFraction = 0.02f, breakImpulse = 14f };
                case CollapseMaterial.Plastic:
                    return new CohesionSettings { chunkSize = 16, iterations = 2, strength = 0.72f, damping = 0.86f, breakStrain = 0.65f, duration = 2.5f, topple = 0.35f, debrisFraction = 0.08f, breakImpulse = 10f };
                case CollapseMaterial.Glass:
                    return new CohesionSettings { chunkSize = 5, iterations = 1, strength = 0.48f, damping = 0.35f, breakStrain = 0.04f, duration = 0.25f, topple = 1.4f, debrisFraction = 0.65f, breakImpulse = 2f };
                case CollapseMaterial.Custom:
                    return new CohesionSettings
                    {
                        chunkSize = Mathf.Clamp(cohesionChunkSize, 1, 64),
                        iterations = Mathf.Clamp(cohesionIterations, 1, 4),
                        strength = Mathf.Clamp01(cohesion),
                        damping = Mathf.Clamp01(cohesionDamping),
                        breakStrain = Mathf.Clamp(cohesionBreakStrain, 0.01f, 1f),
                        duration = Mathf.Max(0.05f, cohesionDuration),
                        topple = Mathf.Clamp(cohesionTopple, 0f, 3f),
                        debrisFraction = Mathf.Clamp01(cohesionDebrisFraction),
                        breakImpulse = Mathf.Max(0f, clusterBreakImpulse)
                    };
                default: // Cement
                    return new CohesionSettings { chunkSize = 14, iterations = 2, strength = 0.82f, damping = 0.75f, breakStrain = 0.12f, duration = 1.8f, topple = 1f, debrisFraction = 0.18f, breakImpulse = 5f };
            }
        }

        int CohesionGroupConfigHash()
        {
            CohesionSettings settings = ResolveCohesionSettings();
            unchecked
            {
                int hash = _runtimeCells;
                hash = hash * 31 + settings.chunkSize;
                hash = hash * 31 + seed;
                return hash;
            }
        }

        void RefreshCohesionGroupsIfNeeded()
        {
            if (!cohesiveCollapse || _frags == null || _runtimeCells <= 0 ||
                CohesionGroupConfigHash() == _cohesionGroupConfigHash) return;
            RefreshCohesionGroups(_runtimeCells, false);
        }

        void RefreshCohesionGroups(int cells, bool force)
        {
            if (!cohesiveCollapse || cells <= 0 || _frags == null)
            {
                _cohesionGroupBuf?.Release(); _cohesionGroupBuf = null;
                _cohesionGroupCenterBuf?.Release(); _cohesionGroupCenterBuf = null;
                _cohesionGroupReleaseBuf?.Release(); _cohesionGroupReleaseBuf = null;
                _cohesionGroupCount = 0;
                _cohesionGroupConfigHash = 0;
                return;
            }
            int configHash = CohesionGroupConfigHash();
            if (!force && configHash == _cohesionGroupConfigHash) return;

            int targetSize = ResolveCohesionSettings().chunkSize;
            var groupIds = new uint[cells];
            var assigned = new bool[cells];
            var groupMembers = new List<int>(targetSize);
            var frontier = new Queue<int>(targetSize * 2);
            var centers = new List<Vector4>();
            uint groupId = 0u;

            for (int seedCell = 0; seedCell < cells; seedCell++)
            {
                if (assigned[seedCell]) continue;
                groupMembers.Clear();
                frontier.Clear();
                assigned[seedCell] = true;
                frontier.Enqueue(seedCell);
                while (frontier.Count > 0 && groupMembers.Count < targetSize)
                {
                    int cell = frontier.Dequeue();
                    groupMembers.Add(cell);
                    var neighbors = _frags[cell].neighbors;
                    int neighborCount = Mathf.Min(neighbors.Count, MaxNb);
                    for (int i = 0; i < neighborCount && groupMembers.Count + frontier.Count < targetSize; i++)
                    {
                        int neighbor = neighbors[i];
                        if ((uint)neighbor >= (uint)cells || assigned[neighbor]) continue;
                        assigned[neighbor] = true;
                        frontier.Enqueue(neighbor);
                    }
                }

                Vector3 center = Vector3.zero;
                for (int i = 0; i < groupMembers.Count; i++) center += _frags[groupMembers[i]].centroid;
                center /= Mathf.Max(1, groupMembers.Count);
                float radius = 0f;
                for (int i = 0; i < groupMembers.Count; i++)
                {
                    int cell = groupMembers[i];
                    groupIds[cell] = groupId;
                    radius = Mathf.Max(radius, Vector3.Distance(center, _frags[cell].centroid) + _frags[cell].radius);
                }
                centers.Add(new Vector4(center.x, center.y, center.z, radius));
                groupId++;
            }

            var centerPerCell = new Vector4[cells];
            for (int c = 0; c < cells; c++) centerPerCell[c] = centers[(int)groupIds[c]];
            _cohesionGroupCount = Mathf.Max(1, (int)groupId);
            _cohesionGroupBuf?.Release(); _cohesionGroupBuf = new ComputeBuffer(cells, 4); _cohesionGroupBuf.SetData(groupIds);
            _cohesionGroupCenterBuf?.Release(); _cohesionGroupCenterBuf = new ComputeBuffer(cells, 16); _cohesionGroupCenterBuf.SetData(centerPerCell);
            if (_cohesionGroupReleaseBuf == null || _cohesionGroupReleaseBuf.count != _cohesionGroupCount)
            {
                _cohesionGroupReleaseBuf?.Release();
                _cohesionGroupReleaseBuf = NewBuf(_cohesionGroupCount, 4);
            }
            EnsureCohesionScratch(cells);
            if (_cohesionStateBuf != null && _cohesionStateBuf.count == cells)
                _cohesionStateBuf.SetData(new uint[cells]);
            _cohesionGroupConfigHash = configHash;
        }

        void EnsureCohesionScratch(int cells)
        {
            EnsureScratch(ref _cohesionPositionDeltaBuf, cells);
            EnsureScratch(ref _cohesionVelocityDeltaBuf, cells);
            EnsureScratch(ref _cohesionAngularDeltaBuf, cells);
            EnsureScratch(ref _cohesionRotationTargetBuf, cells);
        }

        static void EnsureScratch(ref GraphicsBuffer buffer, int cells)
        {
            if (buffer != null && buffer.count == cells) return;
            buffer?.Release();
            buffer = NewBuf(cells, 16);
        }

        readonly struct EdgePoint : IEquatable<EdgePoint>, IComparable<EdgePoint>
        {
            readonly long x, y, z;
            public EdgePoint(Vector3 p, float inverseWeld)
            {
                x = (long)System.Math.Round(p.x * inverseWeld);
                y = (long)System.Math.Round(p.y * inverseWeld);
                z = (long)System.Math.Round(p.z * inverseWeld);
            }
            public int CompareTo(EdgePoint other)
            {
                int c = x.CompareTo(other.x);
                if (c != 0) return c;
                c = y.CompareTo(other.y);
                return c != 0 ? c : z.CompareTo(other.z);
            }
            public bool Equals(EdgePoint other) => x == other.x && y == other.y && z == other.z;
            public override bool Equals(object obj) => obj is EdgePoint other && Equals(other);
            public override int GetHashCode()
            {
                unchecked { return ((x.GetHashCode() * 397) ^ y.GetHashCode()) * 397 ^ z.GetHashCode(); }
            }
        }

        readonly struct FragmentEdgeKey : IEquatable<FragmentEdgeKey>
        {
            readonly EdgePoint a, b;
            public FragmentEdgeKey(Vector3 p0, Vector3 p1, float inverseWeld)
            {
                EdgePoint q0 = new(p0, inverseWeld), q1 = new(p1, inverseWeld);
                if (q0.CompareTo(q1) <= 0) { a = q0; b = q1; }
                else { a = q1; b = q0; }
            }
            public bool Equals(FragmentEdgeKey other) => a.Equals(other.a) && b.Equals(other.b);
            public override bool Equals(object obj) => obj is FragmentEdgeKey other && Equals(other);
            public override int GetHashCode() { unchecked { return a.GetHashCode() * 397 ^ b.GetHashCode(); } }
        }

        struct FragmentEdgeRecord
        {
            public int triangle;
            public int opposite;
            public Vector3 normal;
            public bool cutFace;
            public bool paired;
        }

        // One-time CPU classification for the shader-only edge band. Each triangle stores three bits in UV0.w:
        // bit 0 is the edge opposite vertex 0, etc. Coplanar cap triangulation is deliberately rejected, while
        // cap/cap creases and the cap/outer-skin boundary survive. No vertex or index count is changed.
        static int[] BuildFragmentEdgeMasks(BrazeFractureClipper.Frag fragment, float weld)
        {
            int triangleCount = fragment != null ? fragment.TriCount : 0;
            var masks = new int[triangleCount];
            if (triangleCount == 0) return masks;

            float inverseWeld = 1f / Mathf.Max(weld, 1e-7f);
            var normals = new Vector3[triangleCount];
            var cutFaces = new bool[triangleCount];
            var edges = new Dictionary<FragmentEdgeKey, FragmentEdgeRecord>(triangleCount * 2);
            for (int t = 0; t < triangleCount; t++)
            {
                int o = t * 3;
                Vector3 n = Vector3.Cross(fragment.pos[o + 1] - fragment.pos[o],
                                          fragment.pos[o + 2] - fragment.pos[o]);
                normals[t] = n.sqrMagnitude > 1e-16f ? n.normalized : fragment.nrm[o].normalized;
                cutFaces[t] = fragment.skin[o] < 0.5f && fragment.skin[o + 1] < 0.5f &&
                              fragment.skin[o + 2] < 0.5f;
            }

            void AddEdge(int triangle, int opposite, int ia, int ib)
            {
                var key = new FragmentEdgeKey(fragment.pos[ia], fragment.pos[ib], inverseWeld);
                if (!edges.TryGetValue(key, out FragmentEdgeRecord first))
                {
                    edges.Add(key, new FragmentEdgeRecord
                    {
                        triangle = triangle,
                        opposite = opposite,
                        normal = normals[triangle],
                        cutFace = cutFaces[triangle],
                    });
                    return;
                }

                bool fractureBoundary = first.cutFace || cutFaces[triangle];
                bool differentSurface = first.cutFace != cutFaces[triangle] ||
                                        Mathf.Abs(Vector3.Dot(first.normal, normals[triangle])) < 0.9995f;
                if (fractureBoundary && differentSurface)
                {
                    masks[first.triangle] |= 1 << first.opposite;
                    masks[triangle] |= 1 << opposite;
                }
                first.paired = true;
                edges[key] = first;
            }

            for (int t = 0; t < triangleCount; t++)
            {
                int o = t * 3;
                AddEdge(t, 0, o + 1, o + 2);
                AddEdge(t, 1, o + 2, o);
                AddEdge(t, 2, o, o + 1);
            }

            // Open/T-junction cap boundaries still need an edge band; unmatched original-skin edges do not.
            foreach (FragmentEdgeRecord edge in edges.Values)
                if (!edge.paired && edge.cutFace)
                    masks[edge.triangle] |= 1 << edge.opposite;
            return masks;
        }

        static void AddVert(System.Collections.Generic.List<Vector3> dpos, System.Collections.Generic.List<Vector3> dnrm,
            System.Collections.Generic.List<Vector2Int> sids, System.Collections.Generic.List<Vector3> mp,
            System.Collections.Generic.List<Vector4> uv0, System.Collections.Generic.List<Vector2> uv1,
            System.Collections.Generic.List<int> tris, int cell, ref int v, Vector3 w, Vector3 n, Vector2 uv,
            float skin, int edgeMask)
        {
            dpos.Add(w); dnrm.Add(n);
            sids.Add(new Vector2Int(cell, v));
            mp.Add(w); uv0.Add(new Vector4(uv.x, uv.y, skin, edgeMask)); uv1.Add(new Vector2(cell, v));
            tris.Add(v);
            v++;
        }

        static GraphicsBuffer NewBuf(int count, int stride)
            => new(GraphicsBuffer.Target.Structured, Mathf.Max(1, count), stride);

        void BuildChunkMaterial()
        {
            var sh = BrazeRuntimeShaderLibrary.Find("BrazeFX/Chunk");
            if (sh == null) { Debug.LogError("[BrazeVoronoiFracture] BrazeFX/Chunk shader not found."); return; }
            if (_chunkMat == null) _chunkMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            ConfigureOpaqueChunkMaterial();

            CopySurfaceProps();

            _chunkMat.SetBuffer("_ClumpPosState", _posStateBuf);
            _chunkMat.SetBuffer("_ClumpRot", _rotBuf);
            _chunkMat.SetBuffer("_ClumpLife", _lifeBuf);
            _chunkMat.SetBuffer("_DeformedPos", _deformedPosBuf);
            _chunkMat.SetBuffer("_DeformedNrm", _deformedNrmBuf);
            _chunkMat.SetBuffer("_FreeOffset", _freeOffsetBuf);
            _chunkMat.SetBuffer("_FreeNrm", _freeNrmBuf);
            _chunkMat.SetBuffer("_ClumpCentroid", _clumpCentroidBuf);
            _chunkMat.SetBuffer("_EffectorPosition", _effectorPositionBuf);
            _chunkMat.SetBuffer("_EffectorRotation", _effectorRotationBuf);
            _chunkMat.SetBuffer("_EffectorScale", _effectorScaleBuf);
            Matrix4x4 l2w = Space != null ? Space.localToWorldMatrix : Matrix4x4.identity;
            _chunkMat.SetMatrix("_L2W", l2w);
            _chunkMat.SetMatrix("_N2W", NormalMatrix(l2w));
            _chunkMat.SetFloat("_DrawMode", 0f);
            _chunkMat.SetInt("_EffectorCount", 0);
            _chunkMat.SetFloat("_SoftAmount", softBody ? softness : 0f);
            _chunkMat.SetFloat("_DissolveScale", dissolveScale);
            _chunkMat.SetColor("_DissolveEdge", dissolveEdge);
            ApplyRenderTimeDetail();

            // particle-debug points: same buffers, resolved per vertex
            var psh = BrazeRuntimeShaderLibrary.Find("BrazeFX/VoronoiPoints");
            if (psh != null)
            {
                if (_pointsMat == null) _pointsMat = new Material(psh) { hideFlags = HideFlags.HideAndDontSave };
                _pointsMat.SetBuffer("_ClumpPosState", _posStateBuf);
                _pointsMat.SetBuffer("_ClumpRot", _rotBuf);
                _pointsMat.SetBuffer("_ClumpLife", _lifeBuf);
                _pointsMat.SetBuffer("_DeformedPos", _deformedPosBuf);
                _pointsMat.SetBuffer("_FreeOffset", _freeOffsetBuf);
                _pointsMat.SetBuffer("_PointIds", _shatteredIdsBuf);
                _pointsMat.SetBuffer("_ClumpCentroid", _clumpCentroidBuf);
                _pointsMat.SetBuffer("_EffectorPosition", _effectorPositionBuf);
                _pointsMat.SetBuffer("_EffectorRotation", _effectorRotationBuf);
                _pointsMat.SetBuffer("_EffectorScale", _effectorScaleBuf);
                _pointsMat.SetInt("_EffectorCount", 0);
                _pointsMat.SetMatrix("_L2W", l2w);
            }
        }

        // Copy the look (albedo/colour/metallic/smoothness/texture) from materialSource (or the source mesh's
        // material) into the chunk material — called live so editing that material shows on the fragments at once.
        void CopySurfaceProps()
        {
            if (_chunkMat == null) return;
            ConfigureOpaqueChunkMaterial();

            _chunkMat.SetColor("_Color", Color.white);
            _chunkMat.SetTexture("_MainTex", Texture2D.whiteTexture);
            _chunkMat.SetTextureScale("_MainTex", Vector2.one);
            _chunkMat.SetTextureOffset("_MainTex", Vector2.zero);
            _chunkMat.SetFloat("_Metallic", 0f);
            _chunkMat.SetFloat("_Smoothness", 0.5f);
            _chunkMat.SetFloat("_BumpScale", 1f);
            _chunkMat.SetFloat("_OcclusionStrength", 1f);
            _chunkMat.SetColor("_EmissionColor", Color.black);
            _chunkMat.SetFloat("_HasMetallicMap", 0f);
            _chunkMat.SetFloat("_HasBump", 0f);
            _chunkMat.SetFloat("_HasOcclusionMap", 0f);
            _chunkMat.SetFloat("_HasEmissionMap", 0f);

            var src = materialSource != null ? materialSource
                    : (SourceRenderer != null ? SourceRenderer.sharedMaterial : null);
            if (src == null) return;
            if (src.HasProperty("_BaseColor")) _chunkMat.SetColor("_Color", SolidChunkColor(src.GetColor("_BaseColor")));
            else if (src.HasProperty("_Color")) _chunkMat.SetColor("_Color", SolidChunkColor(src.GetColor("_Color")));
            bool copiedTexture = TryCopyMainTexture(src, "_BaseMap") || TryCopyMainTexture(src, "_MainTex");
            if (!copiedTexture && src.mainTexture != null)
            {
                _chunkMat.SetTexture("_MainTex", src.mainTexture);
                _chunkMat.SetTextureScale("_MainTex", src.mainTextureScale);
                _chunkMat.SetTextureOffset("_MainTex", src.mainTextureOffset);
            }
            if (src.HasProperty("_Metallic")) _chunkMat.SetFloat("_Metallic", src.GetFloat("_Metallic"));
            if (src.HasProperty("_Smoothness")) _chunkMat.SetFloat("_Smoothness", src.GetFloat("_Smoothness"));
            else if (src.HasProperty("_Glossiness")) _chunkMat.SetFloat("_Smoothness", src.GetFloat("_Glossiness"));

            if (TryCopyTexture(src, "_MetallicGlossMap", "_MetallicGlossMap"))
                _chunkMat.SetFloat("_HasMetallicMap", 1f);
            if (TryCopyTexture(src, "_BumpMap", "_BumpMap"))
            {
                if (src.HasProperty("_BumpScale")) _chunkMat.SetFloat("_BumpScale", src.GetFloat("_BumpScale"));
                _chunkMat.SetFloat("_HasBump", 1f);
            }
            if (TryCopyTexture(src, "_OcclusionMap", "_OcclusionMap"))
            {
                if (src.HasProperty("_OcclusionStrength"))
                    _chunkMat.SetFloat("_OcclusionStrength", src.GetFloat("_OcclusionStrength"));
                _chunkMat.SetFloat("_HasOcclusionMap", 1f);
            }
            if (src.HasProperty("_EmissionColor"))
                _chunkMat.SetColor("_EmissionColor", src.GetColor("_EmissionColor"));
            if (TryCopyTexture(src, "_EmissionMap", "_EmissionMap"))
                _chunkMat.SetFloat("_HasEmissionMap", 1f);
        }

        void ConfigureOpaqueChunkMaterial()
        {
            if (_chunkMat == null) return;
            _chunkMat.renderQueue = (int)RenderQueue.Geometry;
            _chunkMat.SetOverrideTag("RenderType", "Opaque");
            _chunkMat.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            _chunkMat.DisableKeyword("_ALPHATEST_ON");
            _chunkMat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            _chunkMat.DisableKeyword("_ALPHAMODULATE_ON");
            _chunkMat.SetShaderPassEnabled("DepthOnly", true);
            _chunkMat.SetShaderPassEnabled("DepthNormals", true);
            _chunkMat.SetShaderPassEnabled("ShadowCaster", true);
            if (_chunkMat.HasProperty("_Cull")) _chunkMat.SetFloat("_Cull", (float)CullMode.Back);
        }

        static Color SolidChunkColor(Color color)
        {
            color.a = 1f;
            return color;
        }

        bool TryCopyMainTexture(Material src, string property)
            => TryCopyTexture(src, property, "_MainTex");

        bool TryCopyTexture(Material src, string sourceProperty, string destinationProperty)
        {
            if (!src.HasProperty(sourceProperty)) return false;
            Texture tex = src.GetTexture(sourceProperty);
            if (tex == null) return false;
            _chunkMat.SetTexture(destinationProperty, tex);
            _chunkMat.SetTextureScale(destinationProperty, src.GetTextureScale(sourceProperty));
            _chunkMat.SetTextureOffset(destinationProperty, src.GetTextureOffset(sourceProperty));
            return true;
        }

        void CopyInteriorSurfaceProps()
        {
            if (_chunkMat == null) return;
            _interiorHasTexture = false;
            _chunkMat.SetTexture("_InteriorTex", Texture2D.whiteTexture);
            _chunkMat.SetTextureScale("_InteriorTex", Vector2.one);
            _chunkMat.SetTextureOffset("_InteriorTex", Vector2.zero);
            _chunkMat.SetColor("_InteriorColor", SolidChunkColor(interiorColor));
            _chunkMat.SetFloat("_InteriorMetallic", interiorMetallic);
            _chunkMat.SetFloat("_InteriorSmoothness", interiorSmoothness);

            Material src = interiorMaterial;
            if (src == null) return;
            if (src.HasProperty("_BaseColor"))
                _chunkMat.SetColor("_InteriorColor", SolidChunkColor(src.GetColor("_BaseColor")));
            else if (src.HasProperty("_Color"))
                _chunkMat.SetColor("_InteriorColor", SolidChunkColor(src.GetColor("_Color")));

            _interiorHasTexture = TryCopyTexture(src, "_BaseMap", "_InteriorTex") ||
                                  TryCopyTexture(src, "_MainTex", "_InteriorTex");
            if (!_interiorHasTexture && src.mainTexture != null)
            {
                _chunkMat.SetTexture("_InteriorTex", src.mainTexture);
                _chunkMat.SetTextureScale("_InteriorTex", src.mainTextureScale);
                _chunkMat.SetTextureOffset("_InteriorTex", src.mainTextureOffset);
                _interiorHasTexture = true;
            }
            if (src.HasProperty("_Metallic"))
                _chunkMat.SetFloat("_InteriorMetallic", src.GetFloat("_Metallic"));
            if (src.HasProperty("_Smoothness"))
                _chunkMat.SetFloat("_InteriorSmoothness", src.GetFloat("_Smoothness"));
            else if (src.HasProperty("_Glossiness"))
                _chunkMat.SetFloat("_InteriorSmoothness", src.GetFloat("_Glossiness"));
        }

        // Render-time detailing/texturing uniforms — refreshed live so the knobs tweak without re-fracturing.
        void ApplyRenderTimeDetail()
        {
            if (_chunkMat == null) return;
            CopySurfaceProps();   // live material edits flow to the fragments
            CopyInteriorSurfaceProps();
            _chunkMat.SetFloat("_VxInterior", 1f);
            _chunkMat.SetFloat("_VxTriplanar", triplanarUV && _interiorHasTexture ? 1f : 0f);
            _chunkMat.SetFloat("_VxTriScale", triplanarScale);
            _chunkMat.SetFloat("_VxDetail", enableDetailing ? 1f : 0f);
            if (enableDetailing) _chunkMat.EnableKeyword("_VX_INTERIOR_RELIEF");
            else _chunkMat.DisableKeyword("_VX_INTERIOR_RELIEF");
            _chunkMat.SetFloat("_VxDetailAmp", detailAmount);
            _chunkMat.SetFloat("_VxDetailScale", detailScale);
            _chunkMat.SetFloat("_VxDetailOct", detailOctaves);
            _chunkMat.SetFloat("_VxDetailContrast", detailContrast);
            _chunkMat.SetFloat("_VxEdge", enableEdgeDetail ? 1f : 0f);
            if (enableEdgeDetail) _chunkMat.EnableKeyword("_VX_EDGE_DETAIL");
            else _chunkMat.DisableKeyword("_VX_EDGE_DETAIL");
            _chunkMat.SetColor("_VxEdgeColor", SolidChunkColor(edgeDetailColor));
            _chunkMat.SetFloat("_VxEdgeWidth", edgeDetailWidth);
            _chunkMat.SetFloat("_VxEdgeStrength", edgeDetailStrength);
            _chunkMat.SetFloat("_VxEdgeNoise", edgeDetailNoise);
            _chunkMat.SetFloat("_VxEdgeScale", edgeDetailScale);
            _chunkMat.SetFloat("_SoftAmount", softBody ? softness : 0f);
            _chunkMat.SetFloat("_Ambient", movingFragmentLightFloor);
            ApplyAuthoringPreviewMaterial();
        }

        void ApplyAuthoringPreviewMaterial()
        {
            if (_chunkMat == null) return;
            float explode = 0f, colorize = 0f, slice = 0f, stale = 0f;
            Vector3 centreWS = Space != null ? Space.TransformPoint(_localBounds.center) : _localBounds.center;
            Vector4 slicePlane = new(1f, 0f, 0f, 0f);
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                explode = previewExplode * _avgRadius * WorldRadiusScale();
                colorize = previewMode == AuthoringPreviewMode.Fragments || previewMode == AuthoringPreviewMode.Slice ? 1f : 0f;
                slice = previewMode == AuthoringPreviewMode.Slice ? 1f : 0f;
                stale = IsAuthoringResultCurrent ? 0f : 1f;

                Vector3 localNormal = previewSliceNormal.sqrMagnitude > 1e-8f ? previewSliceNormal.normalized : Vector3.right;
                Matrix4x4 l2w = Space != null ? Space.localToWorldMatrix : Matrix4x4.identity;
                Vector3 normalWS = l2w.inverse.transpose.MultiplyVector(localNormal).normalized;
                Vector3 pointWS = l2w.MultiplyPoint3x4(_localBounds.center + localNormal *
                                  (previewSliceOffset * Mathf.Max(_localBounds.size.magnitude, 1e-4f)));
                slicePlane = new Vector4(normalWS.x, normalWS.y, normalWS.z, -Vector3.Dot(normalWS, pointWS));
            }
#endif
            _chunkMat.SetFloat("_PreviewExplode", explode);
            _chunkMat.SetVector("_PreviewCenterWS", centreWS);
            _chunkMat.SetFloat("_PreviewColorize", colorize);
            _chunkMat.SetFloat("_PreviewSlice", slice);
            _chunkMat.SetVector("_PreviewSlicePlane", slicePlane);
            _chunkMat.SetFloat("_PreviewStale", stale);
        }

        void RunSim(float dt)
        {
            if (_simCS == null || _runtimeCells <= 0 || _posStateBuf == null) return;
            _simCS.SetInt("_ClumpCount", _runtimeCells);
            _simCS.SetFloat("_Dt", Mathf.Max(0f, dt));
            _simCS.SetFloat("_TurbTime", Time.time);   // drives turbulence curl field so it evolves (no static sinks)
            _simCS.SetFloat("_ChunkRadius", Mathf.Max(0.001f, _avgRadius * WorldRadiusScale() * 0.5f));
            _simCS.SetFloat("_AverageClumpRadius", Mathf.Max(0.001f, _avgRadius * WorldRadiusScale()));
            _simCS.SetFloat("_ClumpLifetime", fragmentLifetime);
            _simCS.SetFloat("_Shrink", shrink);
            _simCS.SetFloat("_Dissolve", dissolve);
            _simCS.SetInt("_ImpactFluidActive", _impactFluidRemaining > 0f ? 1 : 0);
            _simCS.SetInt("_ForceCount", _frameForceCount);
            // sleep timer is only driven by the shape-aware collide pass; disable sleep otherwise.
            _simCS.SetFloat("_SleepDelay", shapeAwareCollision ? Mathf.Max(0f, sleepDelay) : 0f);
            // shape-aware collision runs as its own pass; tell SimClumps to skip its single-sphere collision.
            _simCS.SetInt("_ColliderCount", shapeAwareCollision ? 0 : _frameColliderCount);
            _simCS.SetBuffer(_kSim, "_ClumpPosState", _posStateBuf);
            _simCS.SetBuffer(_kSim, "_ClumpRot", _rotBuf);
            _simCS.SetBuffer(_kSim, "_ClumpLife", _lifeBuf);
            _simCS.SetBuffer(_kSim, "_ClumpVel", _velBuf);
            _simCS.SetBuffer(_kSim, "_ClumpAngVel", _angVelBuf);
            _simCS.SetBuffer(_kSim, "_ClumpMassRead", _clumpMassBuf);
            _simCS.SetBuffer(_kSim, "_ImpactClumpCentroid", _clumpCentroidBuf);
            _simCS.SetBuffer(_kSim, "_ImpactFluidState", _impactFluidStateBuf);
            _simCS.SetBuffer(_kSim, "_Forces", _forceBuf);
            _simCS.SetBuffer(_kSim, "_ForceFalloff", _forceFalloffBuf);
            _simCS.SetBuffer(_kSim, "_Colliders", _colliderBuf);
            _simCS.Dispatch(_kSim, Mathf.CeilToInt(_runtimeCells / 64f), 1, 1);
            _impactFluidRemaining = Mathf.Max(0f, _impactFluidRemaining - Mathf.Max(0f, dt));
            if (_impactFluidRemaining <= 0f)
            {
                _impactFluidCellFraction = 0f;
                _impactFluidContainer = null;
            }
        }

        // Evaluate the ordered direct-transform stack once per fragment. This is deliberately separate from the
        // velocity simulation: moving an effector changes the resulting transform without injecting momentum.
        void RunEffectors()
        {
            if (_runtimeCells <= 0 || _posStateBuf == null || _clumpCentroidBuf == null ||
                _effectorPositionBuf == null || _effectorPositionScratchBuf == null ||
                _effectorRotationBuf == null || _effectorScaleBuf == null ||
                !EnsureCompute() || _kClearEffectors < 0 || _kApplySingleEffector < 0 || _kPushApartEffector < 0 ||
                _kApplyDelayEffector < 0 || _kApplyDelaySpringEffector < 0)
                return;

            int count = GatherGpuEffectors();
            _frameEffectorCount = count;
            _cs.SetInt("_EffectorCount", count);
            _cs.SetInt("_EffectorClumpCount", _runtimeCells);
            _cs.SetFloat("_EffectorTime", Application.isPlaying ? Time.time : Time.realtimeSinceStartup);
            _cs.SetMatrix("_EffectorObjectW2L", Space != null ? Space.worldToLocalMatrix : Matrix4x4.identity);
            Quaternion objectRotation = Space != null ? Space.rotation : Quaternion.identity;
            _cs.SetVector("_EffectorObjectRotation", new Vector4(objectRotation.x, objectRotation.y,
                                                                  objectRotation.z, objectRotation.w));
            _cs.SetVector("_EffectorBoundsMin", _localBounds.min);
            _cs.SetVector("_EffectorBoundsMax", _localBounds.max);
            int groups = Mathf.CeilToInt(_runtimeCells / 64f);
            _cs.SetBuffer(_kClearEffectors, "_EffectorPosition", _effectorPositionBuf);
            _cs.SetBuffer(_kClearEffectors, "_EffectorRotation", _effectorRotationBuf);
            _cs.SetBuffer(_kClearEffectors, "_EffectorScale", _effectorScaleBuf);
            _cs.Dispatch(_kClearEffectors, groups, 1, 1);

            _delayStates.BeginEvaluation();
            for (int i = 0; i < count; i++)
            {
                _cs.SetInt("_EffectorIndex", i);
                if (_effectorScratch[i] is BrazePushApartEffector pushApart)
                {
                    int iterations = Mathf.Clamp(pushApart.iterations, 1, 32);
                    for (int pass = 0; pass < iterations; pass++)
                    {
                        BindPushApartKernel();
                        _cs.Dispatch(_kPushApartEffector, groups, 1, 1);
                        (_effectorPositionBuf, _effectorPositionScratchBuf) =
                            (_effectorPositionScratchBuf, _effectorPositionBuf);
                    }
                }
                else if (_effectorScratch[i] is BrazeDelayEffector delay)
                {
                    ApplyDelayEffector(delay, i, count, groups);
                }
                else
                {
                    BindSingleEffectorKernel();
                    _cs.Dispatch(_kApplySingleEffector, groups, 1, 1);
                }
            }
            _delayStates.EndEvaluation();
            BindEffectorResultBuffers();
            SetEffectorMaterialCount(count);
        }

        void BindSingleEffectorKernel()
        {
            _cs.SetBuffer(_kApplySingleEffector, "_Effectors", _effectorBuf);
            _cs.SetBuffer(_kApplySingleEffector, "_EffectorFalloff", _effectorFalloffBuf);
            _cs.SetBuffer(_kApplySingleEffector, "_EffectorFunctionLut", _effectorFunctionBuf);
            _cs.SetBuffer(_kApplySingleEffector, "_EffectorAux", _effectorAuxBuf);
            _cs.SetBuffer(_kApplySingleEffector, "_ClumpCentroidE", _clumpCentroidBuf);
            _cs.SetBuffer(_kApplySingleEffector, "_ClumpPosStateE", _posStateBuf);
            _cs.SetBuffer(_kApplySingleEffector, "_ClumpRotE", _rotBuf);
            _cs.SetBuffer(_kApplySingleEffector, "_EffectorPosition", _effectorPositionBuf);
            _cs.SetBuffer(_kApplySingleEffector, "_EffectorRotation", _effectorRotationBuf);
            _cs.SetBuffer(_kApplySingleEffector, "_EffectorScale", _effectorScaleBuf);
        }

        void BindPushApartKernel()
        {
            _cs.SetBuffer(_kPushApartEffector, "_Effectors", _effectorBuf);
            _cs.SetBuffer(_kPushApartEffector, "_EffectorFalloff", _effectorFalloffBuf);
            _cs.SetBuffer(_kPushApartEffector, "_ClumpCentroidE", _clumpCentroidBuf);
            _cs.SetBuffer(_kPushApartEffector, "_ClumpPosStateE", _posStateBuf);
            _cs.SetBuffer(_kPushApartEffector, "_EffectorPosition", _effectorPositionBuf);
            _cs.SetBuffer(_kPushApartEffector, "_EffectorPositionScratch", _effectorPositionScratchBuf);
            _cs.SetBuffer(_kPushApartEffector, "_EffectorNeighbors", _neighborBuf);
            _cs.SetBuffer(_kPushApartEffector, "_EffectorNeighborCounts", _neighborCountBuf);
        }

        void ApplyDelayEffector(BrazeDelayEffector delay, int stackIndex, int effectorCount, int groups)
        {
            BrazeDelayGpuState state = _delayStates.Use(delay, _runtimeCells, stackIndex);
            BrazeDelayGpuSample sample = state.BeginSample();
            bool spring = delay.mode == BrazeDelayEffector.DelayMode.Spring;
            int kernel = spring ? _kApplyDelaySpringEffector : _kApplyDelayEffector;

            _cs.SetInt("_EffectorIndex", stackIndex);
            _cs.SetInt("_EffectorCount", effectorCount);
            _cs.SetInt("_DelayHasHistory", sample.hasHistory ? 1 : 0);
            _cs.SetFloat("_DelayDeltaTime", sample.deltaTime);
            _cs.SetBuffer(kernel, "_Effectors", _effectorBuf);
            _cs.SetBuffer(kernel, "_EffectorFalloff", _effectorFalloffBuf);
            _cs.SetBuffer(kernel, "_ClumpCentroidE", _clumpCentroidBuf);
            _cs.SetBuffer(kernel, "_ClumpPosStateE", _posStateBuf);
            _cs.SetBuffer(kernel, "_ClumpRotE", _rotBuf);
            _cs.SetBuffer(kernel, "_EffectorPosition", _effectorPositionBuf);
            _cs.SetBuffer(kernel, "_EffectorRotation", _effectorRotationBuf);
            _cs.SetBuffer(kernel, "_EffectorScale", _effectorScaleBuf);
            _cs.SetBuffer(kernel, "_DelayPositionState", state.position);
            _cs.SetBuffer(kernel, "_DelayRotationState", state.rotation);
            _cs.SetBuffer(kernel, "_DelayScaleState", state.scale);
            if (spring)
            {
                _cs.SetBuffer(kernel, "_DelayVelocity", state.velocity);
            }
            _cs.Dispatch(kernel, groups, 1, 1);
            state.CommitSample();
        }

        void BindEffectorResultBuffers()
        {
            if (_chunkMat != null) _chunkMat.SetBuffer("_EffectorPosition", _effectorPositionBuf);
            if (_pointsMat != null) _pointsMat.SetBuffer("_EffectorPosition", _effectorPositionBuf);
        }

        void SetEffectorMaterialCount(int count)
        {
            if (_chunkMat != null) _chunkMat.SetInt("_EffectorCount", count);
            if (_pointsMat != null) _pointsMat.SetInt("_EffectorCount", count);
        }

        int GatherGpuEffectors()
        {
            _effectorScratch.Clear();
            if (effectors != null)
            {
                for (int i = 0; i < effectors.Count; i++)
                {
                    var e = effectors[i];
                    if (e != null && e.isActiveAndEnabled && !_effectorScratch.Contains(e)) _effectorScratch.Add(e);
                }
            }

            int count = _effectorScratch.Count;
            int gpuHash = EffectorStackHash(_effectorScratch);
            if (_effectorGpuHashValid && gpuHash == _effectorGpuHash &&
                _effectorBuf != null && _effectorFalloffBuf != null &&
                _effectorFunctionBuf != null && _effectorAuxBuf != null)
                return count;

            _effectorGpuHash = gpuHash;
            _effectorGpuHashValid = true;
            int uploadCount = Mathf.Max(1, count);
            _effectorAuxScratch.Clear();
            if (_effectorData.Length < uploadCount)
                _effectorData = new GpuFractureEffector[Mathf.NextPowerOfTwo(uploadCount)];
            if (count == 0) _effectorData[0] = default;
            else for (int i = 0; i < count; i++)
            {
                _effectorData[i] = _effectorScratch[i].ToGpu(i);
                _effectorScratch[i].AppendGpuData(ref _effectorData[i], _effectorAuxScratch);
            }

            int auxCount = Mathf.Max(1, _effectorAuxScratch.Count);
            if (_effectorAuxData.Length < auxCount)
                _effectorAuxData = new Vector4[Mathf.NextPowerOfTwo(auxCount)];
            if (_effectorAuxScratch.Count == 0) _effectorAuxData[0] = Vector4.zero;
            else _effectorAuxScratch.CopyTo(_effectorAuxData, 0);
            if (_effectorAuxBuf == null || _effectorAuxBuf.count < auxCount)
            {
                _effectorAuxBuf?.Release();
                _effectorAuxBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    Mathf.NextPowerOfTwo(auxCount), sizeof(float) * 4);
            }
            _effectorAuxBuf.SetData(_effectorAuxData, 0, 0, auxCount);

            if (_effectorBuf == null || _effectorBuf.count < uploadCount)
            {
                _effectorBuf?.Release();
                _effectorBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    Mathf.NextPowerOfTwo(uploadCount), GpuFractureEffector.Stride);
            }
            _effectorBuf.SetData(_effectorData, 0, 0, uploadCount);

            int floats = uploadCount * 256;
            bool realloc = _effectorFalloffBuf == null || _effectorFalloffBuf.count != floats;
            if (realloc)
            {
                _effectorFalloffBuf?.Release();
                _effectorFalloffBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, floats, sizeof(float));
                if (_effectorFalloffData.Length < floats) _effectorFalloffData = new float[floats];
            }
            int hash = BrazeFractureEffector.FalloffAtlasHash(_effectorScratch, count);
            if (realloc || hash != _effectorFalloffHash)
            {
                for (int row = 0; row < uploadCount; row++)
                {
                    AnimationCurve curve = row < count ? _effectorScratch[row].falloff : null;
                    int offset = row * 256;
                    for (int x = 0; x < 256; x++)
                        _effectorFalloffData[offset + x] = curve != null ? curve.Evaluate(x / 255f) : 0f;
                }
                _effectorFalloffBuf.SetData(_effectorFalloffData, 0, 0, floats);
                _effectorFalloffHash = hash;
            }

            bool functionRealloc = _effectorFunctionBuf == null || _effectorFunctionBuf.count != floats;
            if (functionRealloc)
            {
                _effectorFunctionBuf?.Release();
                _effectorFunctionBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, floats, sizeof(float));
                if (_effectorFunctionData.Length < floats) _effectorFunctionData = new float[floats];
            }
            int functionHash = BrazeFractureEffector.FunctionAtlasHash(_effectorScratch, count);
            if (functionRealloc || functionHash != _effectorFunctionHash)
            {
                for (int row = 0; row < uploadCount; row++)
                {
                    AnimationCurve curve = row < count ? _effectorScratch[row].FunctionCurve : null;
                    int offset = row * 256;
                    for (int x = 0; x < 256; x++)
                    {
                        float t = x / 255f;
                        _effectorFunctionData[offset + x] = curve != null ? curve.Evaluate(t) : t;
                    }
                }
                _effectorFunctionBuf.SetData(_effectorFunctionData, 0, 0, floats);
                _effectorFunctionHash = functionHash;
            }
            return count;
        }

        static int EffectorStackHash(System.Collections.Generic.List<BrazeFractureEffector> stack)
        {
            unchecked
            {
                int h = stack.Count;
                for (int i = 0; i < stack.Count; i++)
                {
                    BrazeFractureEffector effector = stack[i];
                    h = h * 31 + effector.BrazeInstanceId();
                    h = h * 31 + effector.ContentHash();
                }
                return h;
            }
        }

        // ── Fluid emission bridge ───────────────────────────────────────────────────────────────────────────
        // A Mesh BrazePyroEmitter consumes these live buffers directly in BrazeSolver.FragmentSplat. Fragment state
        // never leaves the GPU: one thread reads one centroid/state/velocity/life record and splats only free pieces.
        bool _fluidReleaseArmed;

        /// <summary>True when this fracture has live runtime fragments that can drive GPU fluid emission.
        /// Before any release path runs, emitters stay on the intact static-surface path.</summary>
        public bool HasFluidFracture => Application.isPlaying && isActiveAndEnabled
                                        && _runtimeCells > 0 && _posStateBuf != null && _fluidReleaseArmed;

        /// <summary>Expose live fracture buffers to the fluid solver without a GPU/CPU synchronization point.
        /// The buffers remain owned by this component and are valid only while this method keeps succeeding.</summary>
        internal bool TryGetFluidGpuBuffers(out GraphicsBuffer posState, out GraphicsBuffer velocity,
                                            out GraphicsBuffer life, out int count)
        {
            posState = _posStateBuf;
            velocity = _velBuf;
            life = _lifeBuf;
            count = _runtimeCells;
            return HasFluidFracture
                   && posState != null && posState.IsValid()
                   && velocity != null && velocity.IsValid()
                   && life != null && life.IsValid();
        }

        /// <summary>Expose only the fragments carrying a live impact-emission timer. FragmentSplat reads the
        /// per-fragment state/motion buffers directly; the CPU never requests impacted ids or positions.</summary>
        internal bool TryGetImpactFluidGpuSource(BrazeContainer requester, out BrazeSolver.FragmentSourceGpu source)
        {
            int depositingCapacity = Mathf.Clamp(
                Mathf.CeilToInt(_runtimeCells * _impactFluidCellFraction), 0, _runtimeCells);
            source = new BrazeSolver.FragmentSourceGpu
            {
                posState = _posStateBuf,
                rotation = _rotBuf,
                velocity = _velBuf,
                angularVelocity = _angVelBuf,
                life = _lifeBuf,
                centroidRadius = _clumpCentroidBuf,
                impactFluidState = _impactFluidStateBuf,
                impactFluidMotion = _impactFluidMotionBuf,
                impactFluidGeometry = _impactFluidGeometryBuf,
                impactFluidVelocity = _impactFluidVelocityBuf,
                impactFiltered = true,
                count = _runtimeCells,
                emitCount = _runtimeCells,
                hashEntryCount = depositingCapacity,
            };
            if (_impactFluidContainer != null && _impactFluidContainer != requester) return false;
            return Application.isPlaying && isActiveAndEnabled && _impactFluidRemaining > 0f
                   && source.count > 0 && depositingCapacity > 0
                   && source.posState != null && source.posState.IsValid()
                   && source.rotation != null && source.rotation.IsValid()
                   && source.velocity != null && source.velocity.IsValid()
                   && source.angularVelocity != null && source.angularVelocity.IsValid()
                   && source.life != null && source.life.IsValid()
                   && source.centroidRadius != null && source.centroidRadius.IsValid()
                   && source.impactFluidState != null && source.impactFluidState.IsValid()
                   && source.impactFluidMotion != null && source.impactFluidMotion.IsValid()
                   && source.impactFluidGeometry != null && source.impactFluidGeometry.IsValid()
                   && source.impactFluidVelocity != null && source.impactFluidVelocity.IsValid();
        }

        // Shape-aware collision: resolve each free fragment's vertex sample points against the scene colliders.
        void RunCollide(float dt)
        {
            if (_cs == null || _kCollide < 0 || _frameColliderCount <= 0 || _colliderBuf == null || _runtimeCells <= 0) return;
            _cs.SetInt("_CFCount", _runtimeCells);
            _cs.SetInt("_ColliderCount", _frameColliderCount);
            _cs.SetInt("_CFMaxSamples", collisionSamples);
            _cs.SetFloat("_CFDt", Mathf.Max(0f, dt));
            _cs.SetFloat("_CFStrength", collisionStrength);
            _cs.SetFloat("_AngularFactor", angularResponse);
            _cs.SetVector("_CFGravity", NetGravity());
            _cs.SetFloat("_ToppleStrength", toppleStrength);
            _cs.SetFloat("_ContactTipResponse", contactTipResponse);
            _cs.SetFloat("_ContactRestSlop", contactRestSlop);
            _cs.SetFloat("_ContactAngularDamping", contactAngularDamping);
            _cs.SetFloat("_SleepLinearSpeed", sleepLinearSpeed);
            _cs.SetFloat("_SleepAngularSpeed", sleepAngularSpeed);
            _cs.SetBuffer(_kCollide, "_CFPosState", _posStateBuf);
            _cs.SetBuffer(_kCollide, "_CFRot", _rotBuf);
            _cs.SetBuffer(_kCollide, "_CFVel", _velBuf);
            _cs.SetBuffer(_kCollide, "_CFAngVel", _angVelBuf);
            _cs.SetBuffer(_kCollide, "_CFRange", _clumpRangeBuf);
            _cs.SetBuffer(_kCollide, "_CFFreeOffset", _freeOffsetBuf);
            _cs.SetBuffer(_kCollide, "_Colliders", _colliderBuf);
            // Rigid-cluster members aggregate their contacts into the island instead of self-responding.
            _cs.SetInt("_IslandOnlyContacts", shapeAwareCollision ? 0 : 1);
            _cs.SetBuffer(_kCollide, "_IslandOfRead", _islandOfBuf);
            _cs.SetBuffer(_kCollide, "_IslandStateRead", _islandPosStateBuf);
            _cs.SetBuffer(_kCollide, "_IslandDataRead", _islandDataBuf);
            _cs.SetBuffer(_kCollide, "_ClumpMass", _clumpMassBuf);
            _cs.SetBuffer(_kCollide, "_IslandAccum", _islandAccumBuf);
            _cs.Dispatch(_kCollide, Mathf.CeilToInt(_runtimeCells / 64f), 1, 1);
        }

        // Build bounded sphere proxies plus the list of pairs whose proxies overlap in the assembled pose. Those
        // pairs stay muted only until their spheres separate once; after that they collide normally on re-entry.
        // This is the fracture equivalent of disabling collision between attached rigid bodies before a joint breaks.
        internal static void BuildSelfCollisionProxyData(BrazeFractureClipper.Frag[] fragments,
            out float[] radii, out Vector2Int[] ranges, out Vector2Int[] entries, out Vector2Int[] restPairs)
        {
            int count = fragments != null ? fragments.Length : 0;
            radii = new float[count];
            ranges = new Vector2Int[count];
            if (count == 0)
            {
                entries = Array.Empty<Vector2Int>();
                restPairs = Array.Empty<Vector2Int>();
                return;
            }

            float radiusSum = 0f;
            int radiusCount = 0;
            for (int i = 0; i < count; i++)
            {
                float radius = fragments[i] != null ? Mathf.Max(0f, fragments[i].radius) : 0f;
                if (radius > 0f) { radiusSum += radius; radiusCount++; }
            }
            float averageRadius = radiusCount > 0 ? radiusSum / radiusCount : 0.1f;
            float radiusClamp = Mathf.Max(1e-6f, averageRadius * 1.5f);
            float maxRadius = 0f;
            for (int i = 0; i < count; i++)
            {
                radii[i] = Mathf.Min(fragments[i] != null ? Mathf.Max(0f, fragments[i].radius) : 0f, radiusClamp);
                maxRadius = Mathf.Max(maxRadius, radii[i]);
            }

            var order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;
            Array.Sort(order, (a, b) => fragments[a].centroid.x.CompareTo(fragments[b].centroid.x));
            float restSlop = averageRadius * 0.04f;
            var pairs = new List<Vector2Int>(count * 8);
            var perBody = new List<Vector2Int>[count];
            for (int oi = 0; oi < count; oi++)
            {
                int a = order[oi];
                if (radii[a] <= 0f) continue;
                for (int oj = oi + 1; oj < count; oj++)
                {
                    int b = order[oj];
                    if (fragments[b].centroid.x - fragments[a].centroid.x >=
                        radii[a] + maxRadius - restSlop) break;
                    if (radii[b] <= 0f) continue;
                    float contactDistance = Mathf.Max(0f, radii[a] + radii[b] - restSlop);
                    if ((fragments[a].centroid - fragments[b].centroid).sqrMagnitude >=
                        contactDistance * contactDistance) continue;
                    int pairIndex = pairs.Count;
                    pairs.Add(new Vector2Int(a, b));
                    (perBody[a] ??= new List<Vector2Int>()).Add(new Vector2Int(b, pairIndex));
                    (perBody[b] ??= new List<Vector2Int>()).Add(new Vector2Int(a, pairIndex));
                }
            }

            var flat = new List<Vector2Int>(pairs.Count * 2);
            for (int c = 0; c < count; c++)
            {
                List<Vector2Int> bodyEntries = perBody[c];
                int start = flat.Count;
                if (bodyEntries != null)
                {
                    bodyEntries.Sort((x, y) => x.x.CompareTo(y.x));
                    flat.AddRange(bodyEntries);
                }
                ranges[c] = new Vector2Int(start, flat.Count - start);
            }
            entries = flat.ToArray();
            restPairs = pairs.ToArray();
        }

        bool EnsureSelfCollisionBuffers()
        {
            if (_selfGridHeadBuf != null) return true;
            if (_frags == null || _frags.Length != _runtimeCells || _runtimeCells <= 0) return false;

            BuildSelfCollisionProxyData(_frags, out float[] radii, out Vector2Int[] ranges,
                                        out Vector2Int[] entries, out Vector2Int[] restPairs);
            _selfRestPairCount = restPairs.Length;
            _selfMaxRadiusLocal = 0f;
            for (int i = 0; i < radii.Length; i++) _selfMaxRadiusLocal = Mathf.Max(_selfMaxRadiusLocal, radii[i]);
            _selfBucketTotal = Mathf.Clamp(Mathf.NextPowerOfTwo(_runtimeCells * 2), 1024, 65536);

            _selfGridHeadBuf = NewBuf(_selfBucketTotal, 4);
            _selfGridNextBuf = NewBuf(_runtimeCells, 4);
            _selfSnapshotBuf = NewBuf(_runtimeCells, 32);       // 2 × float4
            _selfRadiusBuf = NewBuf(_runtimeCells, 4); _selfRadiusBuf.SetData(radii);
            _selfRestRangeBuf = NewBuf(_runtimeCells, 8); _selfRestRangeBuf.SetData(ranges);
            _selfRestEntryBuf = NewBuf(Mathf.Max(1, entries.Length), 8);
            _selfRestEntryBuf.SetData(entries.Length > 0 ? entries : new[] { Vector2Int.zero });
            _selfRestPairBuf = NewBuf(Mathf.Max(1, restPairs.Length), 8);
            _selfRestPairBuf.SetData(restPairs.Length > 0 ? restPairs : new[] { Vector2Int.zero });
            _selfPairActiveBuf = NewBuf(Mathf.Max(1, restPairs.Length), 4);
            _selfPairActiveBuf.SetData(new uint[Mathf.Max(1, restPairs.Length)]);
            _selfWakeBuf = NewBuf(_runtimeCells, 4); _selfWakeBuf.SetData(new uint[_runtimeCells]);
            _selfStatsBuf = NewBuf(4, 4); _selfStatsBuf.SetData(new uint[4]);

            _selfGpuBytes = (long)_selfBucketTotal * 4L + (long)_runtimeCells * (4L + 32L + 4L + 8L + 4L)
                          + (long)Mathf.Max(1, entries.Length) * 8L
                          + (long)Mathf.Max(1, restPairs.Length) * (8L + 4L) + 16L;
            return true;
        }

        float WorldMinScale()
        {
            Vector3 scale = Space != null ? Space.lossyScale : Vector3.one;
            return Mathf.Max(1e-4f, Mathf.Min(Mathf.Abs(scale.x), Mathf.Min(Mathf.Abs(scale.y), Mathf.Abs(scale.z))));
        }

        // Fragment-vs-fragment separation: clear → snapshot/insert → arm separated rest pairs → resolve. Buffers
        // allocate lazily, so an unused toggle has no GPU working set. Runs before island finish so member contacts
        // feed this frame's rigid-body accumulators.
        void RunSelfCollision()
        {
            if (!selfCollision || _cs == null || _runtimeCells <= 0 || _posStateBuf == null ||
                _clumpCentroidBuf == null || _islandOfBuf == null ||
                _kSelfGridClear < 0 || _kSelfGridInsert < 0 || _kSelfActivateRestPairs < 0 ||
                _kSelfCollide < 0 || !EnsureSelfCollisionBuffers()) return;

            // Periodic 16-byte editor/development readback: gameplay builds do not pay diagnostic atomics or readbacks.
#if UNITY_EDITOR || UNITY_ENABLE_CHECKS
            if (_selfStatsRequestActive && _selfStatsRequest.done)
            {
                _selfStatsRequestActive = false;
                if (!_selfStatsRequest.hasError)
                {
                    var stats = _selfStatsRequest.GetData<uint>();
                    if (stats.Length >= 4)
                    {
                        _selfContactPairs = (int)stats[0];
                        _selfCollidingBodies = (int)stats[1];
                        _selfSuppressedContacts = (int)stats[2];
                        _selfWakeRequests = (int)stats[3];
                    }
                }
            }
#endif

            float radiusScale = WorldMinScale();
            float maxRadius = Mathf.Max(1e-4f, _selfMaxRadiusLocal * radiusScale);
            float restSlop = Mathf.Max(1e-6f, _avgRadius * radiusScale * 0.04f);
            _cs.SetInt("_FieldClumpCount", _runtimeCells);
            _cs.SetInt("_SelfBucketMask", _selfBucketTotal - 1);
            _cs.SetInt("_SelfBucketTotal", _selfBucketTotal);
            _cs.SetInt("_SelfRestPairCount", _selfRestPairCount);
            _cs.SetFloat("_SelfRadiusScale", radiusScale);
            _cs.SetFloat("_SelfCellSize", maxRadius * 2f + 1e-3f);   // every proxy radius <= maxRadius: ±1 is exact
            _cs.SetFloat("_SelfStrength", selfCollisionStrength);
            _cs.SetFloat("_SelfRestSlop", restSlop);
            _cs.SetFloat("_SelfSleepDelay", shapeAwareCollision ? Mathf.Max(0f, sleepDelay) : 0f);
            _cs.SetFloat("_SelfIslandAngularFactor", Mathf.Max(0f, angularResponse));
#if UNITY_EDITOR || UNITY_ENABLE_CHECKS
            _cs.SetInt("_SelfCollectStats", 1);
#else
            _cs.SetInt("_SelfCollectStats", 0);
#endif

            _cs.SetBuffer(_kSelfGridClear, "_SelfGridHead", _selfGridHeadBuf);
            _cs.SetBuffer(_kSelfGridClear, "_SelfContactStats", _selfStatsBuf);
            _cs.Dispatch(_kSelfGridClear, Mathf.CeilToInt(_selfBucketTotal / 64f), 1, 1);

            _cs.SetBuffer(_kSelfGridInsert, "_SelfGridHead", _selfGridHeadBuf);
            _cs.SetBuffer(_kSelfGridInsert, "_SelfGridNext", _selfGridNextBuf);
            _cs.SetBuffer(_kSelfGridInsert, "_SelfSnapshot", _selfSnapshotBuf);
            _cs.SetBuffer(_kSelfGridInsert, "_SelfCollisionRadius", _selfRadiusBuf);
            _cs.SetBuffer(_kSelfGridInsert, "_SelfLifeRead", _lifeBuf);
            _cs.SetBuffer(_kSelfGridInsert, "_SelfWakeFlags", _selfWakeBuf);
            _cs.SetBuffer(_kSelfGridInsert, "_IslandCellPosRead", _posStateBuf);
            _cs.SetBuffer(_kSelfGridInsert, "_ClumpCentroid", _clumpCentroidBuf);
            _cs.SetBuffer(_kSelfGridInsert, "_IslandOfRead", _islandOfBuf);
            _cs.SetBuffer(_kSelfGridInsert, "_IslandStateRead", _islandPosStateBuf);
            _cs.SetBuffer(_kSelfGridInsert, "_IslandVel", _islandVelBuf);
            _cs.SetBuffer(_kSelfGridInsert, "_IslandClumpVel", _velBuf);
            int groups = Mathf.CeilToInt(_runtimeCells / 64f);
            _cs.Dispatch(_kSelfGridInsert, groups, 1, 1);

            if (_selfRestPairCount > 0)
            {
                _cs.SetBuffer(_kSelfActivateRestPairs, "_SelfRestPairs", _selfRestPairBuf);
                _cs.SetBuffer(_kSelfActivateRestPairs, "_SelfSnapshotRead", _selfSnapshotBuf);
                _cs.SetBuffer(_kSelfActivateRestPairs, "_SelfPairActive", _selfPairActiveBuf);
                _cs.Dispatch(_kSelfActivateRestPairs, Mathf.CeilToInt(_selfRestPairCount / 64f), 1, 1);
            }

            _cs.SetBuffer(_kSelfCollide, "_SelfGridHeadRead", _selfGridHeadBuf);
            _cs.SetBuffer(_kSelfCollide, "_SelfGridNextRead", _selfGridNextBuf);
            _cs.SetBuffer(_kSelfCollide, "_SelfSnapshotRead", _selfSnapshotBuf);
            _cs.SetBuffer(_kSelfCollide, "_SelfRestRanges", _selfRestRangeBuf);
            _cs.SetBuffer(_kSelfCollide, "_SelfRestEntries", _selfRestEntryBuf);
            _cs.SetBuffer(_kSelfCollide, "_SelfPairActiveRead", _selfPairActiveBuf);
            _cs.SetBuffer(_kSelfCollide, "_SelfWakeFlags", _selfWakeBuf);
            _cs.SetBuffer(_kSelfCollide, "_ClumpPosStateF", _posStateBuf);
            _cs.SetBuffer(_kSelfCollide, "_IslandClumpVel", _velBuf);
            _cs.SetBuffer(_kSelfCollide, "_IslandOfRead", _islandOfBuf);
            _cs.SetBuffer(_kSelfCollide, "_IslandStateRead", _islandPosStateBuf);
            _cs.SetBuffer(_kSelfCollide, "_IslandVelRead", _islandVelBuf);
            _cs.SetBuffer(_kSelfCollide, "_IslandDataRead", _islandDataBuf);
            _cs.SetBuffer(_kSelfCollide, "_ClumpMass", _clumpMassBuf);
            _cs.SetBuffer(_kSelfCollide, "_IslandAccum", _islandAccumBuf);
            _cs.SetBuffer(_kSelfCollide, "_SelfContactStats", _selfStatsBuf);
            _cs.Dispatch(_kSelfCollide, groups, 1, 1);

#if UNITY_EDITOR || UNITY_ENABLE_CHECKS
            if (!_selfStatsRequestActive && Time.frameCount >= _selfStatsNextFrame)
            {
                _selfStatsRequest = AsyncGPUReadback.Request(_selfStatsBuf);
                _selfStatsRequestActive = true;
                _selfStatsNextFrame = Time.frameCount + 6;
            }
#endif
        }

        // Net world-space gravitational acceleration. Gravity and Wind intentionally share the directional-force
        // kernel, but misc.z tags true gravity so wind cannot become mass-independent acceleration / structural down
        // for a rigid monolith. Falloff is ignored here because this vector only drives island free-fall and support.
        Vector3 NetGravity()
        {
            Vector3 g = Vector3.zero;
            for (int i = 0; i < _frameForceCount && i < _forceData.Length; i++)
                if (Mathf.RoundToInt(_forceData[i].type) == 0 && _forceData[i].misc.z > 0.5f)
                    g += (Vector3)_forceData[i].direction * _forceData[i].strength;
            return g;
        }

        // ---- Secondary particles: append GPU spawn requests to a Braze Particle System from fragment surface points.
        // Mirrors the Surface Disintegrator: a burst when a fragment breaks off (DebrisFromReleased reads _ClumpHit==2),
        // and an optional steady shed from shrinking free fragments (DebrisFromFreeClumps).
        static bool DebrisEmitterReady(BrazeParticleSystem emitter) =>
            emitter != null && emitter.isActiveAndEnabled && emitter.Ready;

        void SetDebrisAttachmentSources(GraphicsBuffer posState, GraphicsBuffer rot, int bodyCount)
        {
            for (int i = 0; i < _debrisEmitterScratch.Count; i++)
            {
                var emitter = _debrisEmitterScratch[i];
                if (_debrisEmitterSpawnScratch[i] > 0f && DebrisEmitterReady(emitter))
                    emitter.SetAttachmentSource(posState, rot, bodyCount);
            }
        }

        void ClearDebrisAttachmentSources()
        {
            if (debrisEmitters != null)
            {
                for (int i = 0; i < debrisEmitters.Count; i++)
                {
                    var emitter = debrisEmitters[i];
                    if (emitter != null) emitter.SetAttachmentSource(null, null, 0);
                }
            }

            for (int i = 0; i < _debrisEmitterScratch.Count; i++)
            {
                var emitter = _debrisEmitterScratch[i];
                if (emitter != null) emitter.SetAttachmentSource(null, null, 0);
            }
            _debrisEmitterScratch.Clear();
            _debrisEmitterSpawnScratch.Clear();
            _debrisEmitterBudgetScratch.Clear();
            _debrisEmitterSaltScratch.Clear();
        }

        void EnsureDebrisDummies()
        {
            _debrisReqDummy ??= new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, SpawnRequestStride);
            if (_debrisReqCountDummy == null)
            {
                _debrisReqCountDummy = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
                _debrisReqCountDummy.SetData(new uint[] { 0 });
            }
        }

        void BuildDebrisEmitterBudgets()
        {
            BuildWeightedDebrisBudgets(_debrisEmitterSpawnScratch, maxDebrisPoints, _debrisEmitterBudgetScratch);
        }

        internal static void BuildWeightedDebrisBudgets(IReadOnlyList<float> weights, int maximum, List<int> output)
        {
            output.Clear();
            int count = weights?.Count ?? 0;
            double total = 0.0;
            int lastPositive = -1;
            for (int i = 0; i < count; i++)
            {
                float raw = weights[i];
                double weight = float.IsNaN(raw) || float.IsInfinity(raw) ? 0.0 : Mathf.Clamp01(raw);
                total += weight;
                if (weight > 0.0) lastPositive = i;
            }

            int cap = Mathf.Max(0, maximum);
            int allocated = 0;
            double cumulative = 0.0;
            for (int i = 0; i < count; i++)
            {
                float raw = weights[i];
                cumulative += float.IsNaN(raw) || float.IsInfinity(raw) ? 0.0 : Mathf.Clamp01(raw);
                int end = total <= 0.0 ? 0
                    : i == lastPositive ? cap
                    : (int)Math.Floor(cap * cumulative / total);
                output.Add(Mathf.Max(0, end - allocated));
                allocated = end;
            }
        }

        void BindDebrisRequests(int kernel, BrazeParticleSystem emitter, int requestCount, float requestRate,
            int requestBudget, float spawnFraction, uint spawnSalt)
        {
            requestCount = Mathf.Max(0, requestCount);
            spawnFraction = Mathf.Clamp01(spawnFraction);
            requestRate = Mathf.Max(0f, requestRate) * spawnFraction;
            requestBudget = Mathf.Max(0, requestBudget);
            if (DebrisEmitterReady(emitter) && spawnFraction > 0f && requestBudget > 0
                && (requestCount > 0 || requestRate > 0f))
            {
                int parity = Time.frameCount & 1;
                int requestCap = Mathf.Max(0, Mathf.Min(requestBudget, emitter.IncomingRequestCapacity));
                _simCS.SetBuffer(kernel, "_DebrisRequests", emitter.IncomingReqBuf(parity));
                _simCS.SetBuffer(kernel, "_DebrisReqCount", emitter.IncomingReqCount(parity));
                _simCS.SetInt("_DebrisReqCapacity", emitter.IncomingRequestCapacity);
                _simCS.SetInt("_DebrisMaxRequests", requestCap);
                _simCS.SetInt("_DebrisCount", requestCount);
                _simCS.SetFloat("_DebrisRate", requestRate);
                _simCS.SetFloat("_DebrisSpawnProbability", spawnFraction);
                _simCS.SetInt("_DebrisSpawnSalt", unchecked((int)spawnSalt));
                _simCS.SetFloat("_DebrisVelocityInherit", Mathf.Max(0f, debrisInheritVelocity));
                _simCS.SetFloat("_DebrisScatter", Mathf.Max(0f, debrisScatter));
                return;
            }

            EnsureDebrisDummies();
            _simCS.SetBuffer(kernel, "_DebrisRequests", _debrisReqDummy);
            _simCS.SetBuffer(kernel, "_DebrisReqCount", _debrisReqCountDummy);
            _simCS.SetInt("_DebrisReqCapacity", 0);
            _simCS.SetInt("_DebrisMaxRequests", 0);
            _simCS.SetInt("_DebrisCount", 0);
            _simCS.SetFloat("_DebrisRate", 0f);
            _simCS.SetFloat("_DebrisSpawnProbability", 0f);
            _simCS.SetInt("_DebrisSpawnSalt", 0);
            _simCS.SetFloat("_DebrisVelocityInherit", 0f);
            _simCS.SetFloat("_DebrisScatter", 0f);
        }

        int GatherDebrisEmitters()
        {
            _debrisEmitterScratch.Clear();
            _debrisEmitterSpawnScratch.Clear();
            _debrisEmitterSaltScratch.Clear();
            if (debrisEmitters == null) return 0;
            for (int i = 0; i < debrisEmitters.Count; i++)
            {
                var e = debrisEmitters[i];
                if (!DebrisEmitterReady(e) || _debrisEmitterScratch.Contains(e)) continue;
                _debrisEmitterScratch.Add(e);
                _debrisEmitterSpawnScratch.Add(DebrisEmitterSpawnFraction(i));
                _debrisEmitterSaltScratch.Add(unchecked(((uint)i + 1u) * 0x9e3779b9u));
            }
            BuildDebrisEmitterBudgets();
            return _debrisEmitterScratch.Count;
        }

        internal void EnsureDebrisEmitterSpawnPercentages()
        {
            debrisEmitters ??= new List<BrazeParticleSystem>();
            debrisEmitterSpawnPercentages ??= new List<float>();
            debrisEmitterSpawnOwners ??= new List<BrazeParticleSystem>();

            if (debrisEmitterSpawnOwners.Count == 0)
            {
                // One-time migration for assets authored before owner tracking: preserve existing percentages by index.
                while (debrisEmitterSpawnPercentages.Count < debrisEmitters.Count)
                    debrisEmitterSpawnPercentages.Add(100f);
                if (debrisEmitterSpawnPercentages.Count > debrisEmitters.Count)
                    debrisEmitterSpawnPercentages.RemoveRange(
                        debrisEmitters.Count, debrisEmitterSpawnPercentages.Count - debrisEmitters.Count);
                debrisEmitterSpawnOwners.AddRange(debrisEmitters);
            }
            else
            {
                bool aligned = debrisEmitterSpawnOwners.Count == debrisEmitters.Count
                            && debrisEmitterSpawnPercentages.Count == debrisEmitters.Count;
                for (int i = 0; aligned && i < debrisEmitters.Count; i++)
                    aligned = debrisEmitterSpawnOwners[i] == debrisEmitters[i];

                if (!aligned)
                {
                    // The public emitter list may be edited/reordered from scripts. Match by stored owner so an
                    // authored percentage follows its emitter instead of silently moving to a different VFX stream.
                    var oldOwners = debrisEmitterSpawnOwners.ToArray();
                    var oldPercentages = debrisEmitterSpawnPercentages.ToArray();
                    var used = new bool[oldOwners.Length];
                    debrisEmitterSpawnOwners.Clear();
                    debrisEmitterSpawnPercentages.Clear();
                    for (int i = 0; i < debrisEmitters.Count; i++)
                    {
                        var emitter = debrisEmitters[i];
                        int match = -1;
                        for (int j = 0; j < oldOwners.Length; j++)
                        {
                            if (!used[j] && oldOwners[j] == emitter) { match = j; break; }
                        }
                        if (match >= 0) used[match] = true;
                        debrisEmitterSpawnOwners.Add(emitter);
                        debrisEmitterSpawnPercentages.Add(
                            match >= 0 && match < oldPercentages.Length ? oldPercentages[match] : 100f);
                    }
                }
            }

            for (int i = 0; i < debrisEmitterSpawnPercentages.Count; i++)
            {
                float percentage = debrisEmitterSpawnPercentages[i];
                debrisEmitterSpawnPercentages[i] = float.IsNaN(percentage) || float.IsInfinity(percentage)
                    ? 100f : Mathf.Clamp(percentage, 0f, 100f);
            }
        }

        internal float DebrisEmitterSpawnPercentage(int index)
        {
            if (index < 0 || debrisEmitterSpawnPercentages == null || index >= debrisEmitterSpawnPercentages.Count)
                return 100f;
            float percentage = debrisEmitterSpawnPercentages[index];
            return float.IsNaN(percentage) || float.IsInfinity(percentage)
                ? 100f : Mathf.Clamp(percentage, 0f, 100f);
        }

        float DebrisEmitterSpawnFraction(int index) => DebrisEmitterSpawnPercentage(index) * 0.01f;

        void EnsureClumpHitBackup()
        {
            if (_clumpHitBackupBuf != null && _clumpHitBackupBuf.count >= _runtimeCells) return;
            _clumpHitBackupBuf?.Release();
            _clumpHitBackupBuf = NewBuf(_runtimeCells, 4);
        }

        void CopyClumpHits(bool restore)
        {
            if (_simCS == null || _kCopyClumpHits < 0 || _runtimeCells <= 0 || _clumpHitBuf == null) return;
            EnsureClumpHitBackup();
            _simCS.SetInt("_ClumpCount", _runtimeCells);
            _simCS.SetInt("_CopyHitMode", restore ? 1 : 0);
            _simCS.SetBuffer(_kCopyClumpHits, "_ClumpHit", _clumpHitBuf);
            _simCS.SetBuffer(_kCopyClumpHits, "_ClumpHitBackup", _clumpHitBackupBuf);
            _simCS.Dispatch(_kCopyClumpHits, Mathf.CeilToInt(_runtimeCells / 64f), 1, 1);
        }

        // Run once per frame after releases/sim: burst from just-released fragments (+ steady shed if enabled).
        void RunDebris(float dt)
        {
            int emitterCount = GatherDebrisEmitters();
            if (emitterCount == 0 || _simCS == null || _runtimeCells <= 0) return;

            bool burstOn = debrisPointsPerChunk > 0 && _kDebrisReleased >= 0;
            bool shedOn = debrisFromShrinkingClumps && debrisRatePerChunk > 0f && dt > 0f && _kDebrisFree >= 0;
            bool attachOn = stickToFragment && shedOn;
            if (attachOn) SetDebrisAttachmentSources(_posStateBuf, _rotBuf, _runtimeCells);

            if (_kDebrisReleased >= 0)
            {
                // Burst: DebrisFromReleased consumes _ClumpHit==2 (set by ReleaseClumps) and clears it. When burst is
                // off, still dispatch once into a dummy request buffer so stale release hits cannot fire later. Burst
                // particles are always free-moving; Stick To Fragment only controls the continuous attached stream.
                _simCS.SetInt("_DebrisAttach", 0);
                _simCS.SetInt("_ClumpCount", _runtimeCells);
                _simCS.SetBuffer(_kDebrisReleased, "_ClumpHit", _clumpHitBuf);
                _simCS.SetBuffer(_kDebrisReleased, "_ClumpRange", _clumpRangeBuf);
                _simCS.SetBuffer(_kDebrisReleased, "_ClumpPosStateRead", _posStateBuf);
                _simCS.SetBuffer(_kDebrisReleased, "_ClumpVelRead", _velBuf);
                _simCS.SetBuffer(_kDebrisReleased, "_ClumpAngVelRead", _angVelBuf);
                _simCS.SetBuffer(_kDebrisReleased, "_FreeOffsetRead", _freeOffsetBuf);
                _simCS.SetBuffer(_kDebrisReleased, "_FreeNrmRead", _freeNrmBuf);

                if (burstOn)
                {
                    if (emitterCount > 1) CopyClumpHits(false);
                    for (int i = 0; i < emitterCount; i++)
                    {
                        if (i > 0) CopyClumpHits(true);
                        BindDebrisRequests(_kDebrisReleased, _debrisEmitterScratch[i], debrisPointsPerChunk, 0f,
                            _debrisEmitterBudgetScratch[i], _debrisEmitterSpawnScratch[i], _debrisEmitterSaltScratch[i]);
                        _simCS.Dispatch(_kDebrisReleased, Mathf.CeilToInt(_runtimeCells / 64f), 1, 1);
                    }
                }
                else
                {
                    BindDebrisRequests(_kDebrisReleased, null, 0, 0f, 0, 0f, 0u);
                    _simCS.Dispatch(_kDebrisReleased, Mathf.CeilToInt(_runtimeCells / 64f), 1, 1);
                }
            }

            // Continuous shed while fragments move. Spawn While Shrinking is the master switch; Stick To Fragment
            // enables the attached stream, and Spawn At Start enables the free stream.
            if (shedOn && stickToFragment) DispatchShed(dt, true);
            if (shedOn && spawnAtStart) DispatchShed(dt, false);
        }

        // One continuous-shed dispatch (DebrisFromFreeClumps). attach=true → particles ride the fragment; false →
        // free grains that fall away. The kernel's _DebrisSettleSpeed gate stops shedding once the fragment rests.
        void DispatchShed(float dt, bool attach)
        {
            int emitterCount = _debrisEmitterScratch.Count;
            if (emitterCount == 0) return;

            _simCS.SetInt("_ClumpCount", _runtimeCells);
            _simCS.SetFloat("_Dt", dt);
            _simCS.SetFloat("_ClumpLifetime", fragmentLifetime);
            _simCS.SetFloat("_Shrink", shrink);
            _simCS.SetFloat("_DebrisSettleSpeed", Mathf.Max(sleepLinearSpeed, 0.1f));
            _simCS.SetInt("_FrameSeed", unchecked((int)((uint)Time.frameCount * 2654435761u)));
            _simCS.SetInt("_DebrisAttach", attach ? 1 : 0);
            if (attach) SetDebrisAttachmentSources(_posStateBuf, _rotBuf, _runtimeCells);
            _simCS.SetBuffer(_kDebrisFree, "_ClumpRange", _clumpRangeBuf);
            _simCS.SetBuffer(_kDebrisFree, "_ClumpPosStateRead", _posStateBuf);
            _simCS.SetBuffer(_kDebrisFree, "_ClumpRotRead", _rotBuf);
            _simCS.SetBuffer(_kDebrisFree, "_ClumpLifeRead", _lifeBuf);
            _simCS.SetBuffer(_kDebrisFree, "_ClumpVelRead", _velBuf);
            _simCS.SetBuffer(_kDebrisFree, "_ClumpAngVelRead", _angVelBuf);
            _simCS.SetBuffer(_kDebrisFree, "_FreeOffsetRead", _freeOffsetBuf);
            _simCS.SetBuffer(_kDebrisFree, "_FreeNrmRead", _freeNrmBuf);
            for (int i = 0; i < emitterCount; i++)
            {
                BindDebrisRequests(_kDebrisFree, _debrisEmitterScratch[i], 0, debrisRatePerChunk,
                    _debrisEmitterBudgetScratch[i], _debrisEmitterSpawnScratch[i], _debrisEmitterSaltScratch[i]);
                _simCS.Dispatch(_kDebrisFree, Mathf.CeilToInt(_runtimeCells / 64f), 1, 1);
            }
        }

        // Spin window: damp the initial release spin after each fragment's thrust-frame window ends.
        void RunSettle(float dt)
        {
            if (_cs == null || _kSettle < 0 || _runtimeCells <= 0) return;
            if (spinDamping <= 0f && linearDrag <= 0f) return;
            _cs.SetInt("_SFCount", _runtimeCells);
            _cs.SetFloat("_SFDt", dt);
            _cs.SetFloat("_ThrustFramesMin", thrustFrames.x);
            _cs.SetFloat("_ThrustFramesMax", Mathf.Max(thrustFrames.x, thrustFrames.y));
            _cs.SetFloat("_SpinDamp", spinDamping);
            _cs.SetFloat("_LinearDrag", linearDrag);
            _cs.SetBuffer(_kSettle, "_SFAngVel", _angVelBuf);
            _cs.SetBuffer(_kSettle, "_SFVel", _velBuf);
            _cs.SetBuffer(_kSettle, "_SFPosState", _posStateBuf);
            _cs.SetBuffer(_kSettle, "_SFLife", _lifeBuf);
            _cs.Dispatch(_kSettle, Mathf.CeilToInt(_runtimeCells / 64f), 1, 1);
        }

        /// <summary>
        /// Queue a one-shot bullet, shell, or explosion command. All commands received before this fracture's
        /// LateUpdate are uploaded once and resolved by one GPU dispatch. Returns false when the fracture is not
        /// accepting impacts, not ready, not playing, or its per-frame queue is full.
        /// </summary>
        public bool QueueImpact(in BrazeFractureImpact impact)
            => QueueImpact(impact, default);

        internal bool QueueImpact(in BrazeFractureImpact impact, in BrazeFractureFluidImpact fluid)
        {
            if (!acceptImpacts || !Application.isPlaying || !isActiveAndEnabled || !_renderReady) return false;

            int capacity = Mathf.Clamp(maxImpactsPerFrame, 1, 4096);
            if (_queuedImpactCount >= capacity)
            {
                _droppedImpactCount++;
                return false;
            }

            if (_impactData == null || _impactData.Length != capacity)
            {
                var resized = new GpuFractureImpact[capacity];
                if (_impactData != null && _queuedImpactCount > 0)
                    System.Array.Copy(_impactData, resized, Mathf.Min(_queuedImpactCount, capacity));
                _impactData = resized;
            }

            int resolvedSeed = impact.seed;
            if (resolvedSeed == 0)
            {
                _impactSeedSequence = unchecked(_impactSeedSequence + 1);
                resolvedSeed = unchecked(_impactSeedSequence * 73856093 ^ Time.frameCount * 19349663 ^ this.BrazeInstanceId());
                if (resolvedSeed == 0) resolvedSeed = 1;
            }
            _impactData[_queuedImpactCount++] = impact.ToGpu(resolvedSeed, fluid);
            if (fluid.enabled && fluid.duration > 0f && fluid.cellFraction > 0f
                && fluid.channels.sqrMagnitude > 1e-10f)
            {
                _queuedImpactFluid = true;
                _impactFluidCellFraction = Mathf.Max(_impactFluidCellFraction, Mathf.Clamp01(fluid.cellFraction));
                // If concurrently queued sources name different containers, allow every container to clip against
                // the live fragment positions rather than silently routing one source's fragments to the other.
                if (_impactFluidRemaining > 0f && _impactFluidContainer != fluid.container)
                    _impactFluidContainer = null;
                else
                    _impactFluidContainer = fluid.container;
                _impactFluidRemaining = Mathf.Max(_impactFluidRemaining, fluid.duration);
            }
            return true;
        }

        /// <summary>Convenience overload for a directional spherical impact.</summary>
        public bool QueueImpact(Vector3 position, Vector3 direction, float radius, float impulse)
        {
            var impact = new BrazeFractureImpact(position, direction, radius, impulse);
            return QueueImpact(impact);
        }

        // Allocation-free CPU contact test for custom transform-swept projectiles. Two stages: a swept-AABB reject
        // against the renderer bounds, then the earliest hit along the motion against the baked per-cell bounding
        // spheres (the same world centroid+radius array the GPU kernels use). The exact fragment-volume response
        // stays on the GPU.
        //
        // The narrow phase is load-bearing, not an optimisation: the impact used to fire at the AABB ENTRY point,
        // and the GPU damage capsule only reaches radius + penetrationDepth past the contact. Any trajectory whose
        // bounds-to-surface gap exceeded that (tower/cylinder corners, tapered tops, shots into carved holes) queued
        // an impact into empty air — "accepted", projectile consumed, zero cells selected. Box-shaped walls hid this;
        // on non-box meshes roughly half of all shots dudded.
        internal bool TryGetImpactContact(Vector3 start, Vector3 end, float sweepRadius, out Vector3 contactPoint)
        {
            contactPoint = end;
            if (!acceptImpacts || !Application.isPlaying || !isActiveAndEnabled || !_renderReady) return false;

            Bounds fractureBounds;
            Renderer source = SourceRenderer;
            if (source != null)
            {
                fractureBounds = source.bounds;
            }
            else
            {
                Transform space = Space;
                Matrix4x4 l2w = space != null ? space.localToWorldMatrix : Matrix4x4.identity;
                Vector3 localExtents = _localBounds.extents;
                Vector3 worldExtents = new(
                    Mathf.Abs(l2w.m00) * localExtents.x + Mathf.Abs(l2w.m01) * localExtents.y + Mathf.Abs(l2w.m02) * localExtents.z,
                    Mathf.Abs(l2w.m10) * localExtents.x + Mathf.Abs(l2w.m11) * localExtents.y + Mathf.Abs(l2w.m12) * localExtents.z,
                    Mathf.Abs(l2w.m20) * localExtents.x + Mathf.Abs(l2w.m21) * localExtents.y + Mathf.Abs(l2w.m22) * localExtents.z);
                fractureBounds = new Bounds(l2w.MultiplyPoint3x4(_localBounds.center), worldExtents * 2f);
            }

            // A detached rigid cluster can travel far outside the fracture's original bounds — keep it shootable.
            if (_liveMemberBoundsValid)
            {
                fractureBounds.Encapsulate(_liveMemberBoundsMin);
                fractureBounds.Encapsulate(_liveMemberBoundsMax);
            }
            fractureBounds.Expand(Mathf.Max(0.001f, sweepRadius) * 2f);
            Vector3 delta = end - start;
            Vector3 minimum = fractureBounds.min;
            Vector3 maximum = fractureBounds.max;
            float enter = 0f;
            float exit = 1f;
            if (!ClipImpactSweepAxis(start.x, delta.x, minimum.x, maximum.x, ref enter, ref exit) ||
                !ClipImpactSweepAxis(start.y, delta.y, minimum.y, maximum.y, ref enter, ref exit) ||
                !ClipImpactSweepAxis(start.z, delta.z, minimum.z, maximum.z, ref enter, ref exit))
                return false;

            // Narrow phase: earliest swept contact against the ACTUAL baked fragment triangles. The anchored cell
            // bounding spheres only pre-filter candidates — they must never BE the contact: large wall slabs and
            // cap-sealed interior fragments own spheres that reach metres past the visible stone, so sphere contact
            // reproduces the fire-into-empty-air dud at a different distance. Triangle data (_frags) is already
            // CPU-resident from the bake, in the fracture's local space.
            Vector4[] cells = _centroidWorldCpu;
            int cellCount = cells != null ? Mathf.Min(_runtimeCells, cells.Length) : 0;
            var frags = _frags;
            if (cellCount <= 0 || frags == null || frags.Length < cellCount)
            {
                contactPoint = start + delta * enter;   // no cell data yet — legacy bounds contact
                return true;
            }

            Transform fractureSpace = Space;
            Matrix4x4 w2l = fractureSpace != null ? fractureSpace.worldToLocalMatrix : Matrix4x4.identity;
            Vector3 localStart = w2l.MultiplyPoint3x4(start);
            Vector3 localDelta = w2l.MultiplyPoint3x4(end) - localStart;
            float localDeltaLenSq = localDelta.sqrMagnitude;
            float sweep = Mathf.Max(0.001f, sweepRadius);
            float localSweepSq = sweep / WorldRadiusScale(); localSweepSq *= localSweepSq;
            float deltaLenSq = delta.sqrMagnitude;
            float bestT = float.PositiveInfinity;

            for (int c = 0; c < cellCount; c++)
            {
                Vector4 cell = cells[c];
                if (cell.w <= 1e-6f) continue;    // degenerate cell (no geometry)
                if (CellReleased(c))
                {
                    // Released cells: loose debris stays transparent to bullets, but members of an active rigid
                    // cluster remain shootable at their LIVE readback pose. The baked triangles no longer apply to
                    // a moved body, so contact is the swept hit against the member's bounding sphere — within one
                    // cell of the true surface, plenty for a projectile against a monolith. The queued impact then
                    // reaches the island through RouteImpactsToIslands (shove + Break Impulse).
                    if (_islandOfCpu == null || c >= _islandOfCpu.Length || _islandOfCpu[c] == 0xffffffffu) continue;
                    if (_cellLivePosCpu == null || c >= _cellLivePosCpu.Length) continue;
                    Vector4 live = _cellLivePosCpu[c];
                    if (live.w < 0.5f) continue;
                    float memberReach = cell.w + Mathf.Max(0.001f, sweepRadius);
                    float toX = live.x - start.x, toY = live.y - start.y, toZ = live.z - start.z;
                    float memberDistSq = toX * toX + toY * toY + toZ * toZ;
                    float memberReachSq = memberReach * memberReach;
                    if (memberDistSq <= memberReachSq) { if (bestT > 0f) bestT = 0f; continue; }
                    if (deltaLenSq <= 1e-12f) continue;
                    float memberProj = toX * delta.x + toY * delta.y + toZ * delta.z;
                    if (memberProj <= 0f) continue;
                    float memberDisc = memberProj * memberProj - deltaLenSq * (memberDistSq - memberReachSq);
                    if (memberDisc < 0f) continue;
                    float memberT = (memberProj - Mathf.Sqrt(memberDisc)) / deltaLenSq;
                    if (memberT >= 0f && memberT <= 1f && memberT < bestT) bestT = memberT;
                    continue;
                }

                // world-space swept-sphere candidate filter; sphere entry t is a lower bound on any triangle t
                float reach = cell.w + sweep;
                float toCellX = cell.x - start.x, toCellY = cell.y - start.y, toCellZ = cell.z - start.z;
                float distSq = toCellX * toCellX + toCellY * toCellY + toCellZ * toCellZ;
                float reachSq = reach * reach;
                if (distSq > reachSq)
                {
                    if (deltaLenSq <= 1e-12f) continue;
                    float proj = toCellX * delta.x + toCellY * delta.y + toCellZ * delta.z;
                    if (proj <= 0f) continue;                    // sphere behind the motion
                    float discriminant = proj * proj - deltaLenSq * (distSq - reachSq);
                    if (discriminant < 0f) continue;
                    float sphereT = (proj - Mathf.Sqrt(discriminant)) / deltaLenSq;
                    if (sphereT > 1f || sphereT >= bestT) continue;
                }

                var f = frags[c];
                if (f == null) continue;
                var pos = f.pos;
                int vertCount = pos.Count;

                // exact: centre-line vs triangles (fast path), then swept-radius proximity to the vertex cloud so a
                // grazing shot that misses every face interior still contacts (soup verts are dense along edges)
                for (int vertIndex = 0; vertIndex + 2 < vertCount; vertIndex += 3)
                {
                    if (localDeltaLenSq > 1e-12f &&
                        RaySegmentTriangle(localStart, localDelta, pos[vertIndex], pos[vertIndex + 1], pos[vertIndex + 2], out float triT)
                        && triT < bestT)
                        bestT = triT;
                }
                if (localSweepSq > 1e-12f)
                {
                    for (int vertIndex = 0; vertIndex < vertCount; vertIndex++)
                    {
                        Vector3 toVert = pos[vertIndex] - localStart;
                        float along = localDeltaLenSq > 1e-12f
                            ? Mathf.Clamp01(Vector3.Dot(toVert, localDelta) / localDeltaLenSq)
                            : 0f;
                        if ((toVert - localDelta * along).sqrMagnitude <= localSweepSq && along < bestT)
                            bestT = along;
                    }
                }
            }

            if (float.IsPositiveInfinity(bestT)) return false;   // crossed the bounds but touched no geometry — keep flying
            contactPoint = start + delta * bestT;
            return true;
        }

        // Collect the completed cell-state readback (if any) into the free-cell bitmask, then keep exactly one
        // request in flight. Called once per play-mode Tick; does nothing when the destruction buffers are gone.
        void PollCellStateReadback()
        {
            if (_islandOfRequestActive && _islandOfRequest.done)
            {
                _islandOfRequestActive = false;
                if (!_islandOfRequest.hasError)
                {
                    var membership = _islandOfRequest.GetData<uint>();
                    int n = Mathf.Min(_runtimeCells, membership.Length);
                    if (_islandOfCpu == null || _islandOfCpu.Length < _runtimeCells) _islandOfCpu = new uint[_runtimeCells];
                    for (int c = 0; c < n; c++) _islandOfCpu[c] = membership[c];
                }
            }
            if (!_islandOfRequestActive && rigidClusters && _islandsArmed && _islandOfBuf != null && _runtimeCells > 0)
            {
                _islandOfRequest = AsyncGPUReadback.Request(_islandOfBuf);
                _islandOfRequestActive = true;
            }

            if (_cellStateRequestActive)
            {
                if (!_cellStateRequest.done) return;
                _cellStateRequestActive = false;
                if (!_cellStateRequest.hasError)
                {
                    var states = _cellStateRequest.GetData<Vector4>();
                    int n = Mathf.Min(_runtimeCells, states.Length);
                    int words = (n + 31) >> 5;
                    if (_cellFreeMask == null || _cellFreeMask.Length < words) _cellFreeMask = new uint[words];
                    for (int w = 0; w < _cellFreeMask.Length; w++) _cellFreeMask[w] = 0u;
                    if (_cellLivePosCpu == null || _cellLivePosCpu.Length < _runtimeCells)
                        _cellLivePosCpu = new Vector4[_runtimeCells];

                    Vector3 memberMin = new(float.MaxValue, float.MaxValue, float.MaxValue);
                    Vector3 memberMax = new(float.MinValue, float.MinValue, float.MinValue);
                    bool anyMember = false;
                    for (int c = 0; c < n; c++)
                    {
                        Vector4 state = states[c];
                        _cellLivePosCpu[c] = state;
                        if (state.w < 0.5f) continue;
                        _cellFreeMask[c >> 5] |= 1u << (c & 31);
                        // Track a world AABB over live cluster members so the bullet broad phase still finds a
                        // section that has fallen outside the fracture's original bounds.
                        if (_islandOfCpu == null || c >= _islandOfCpu.Length || _islandOfCpu[c] == 0xffffffffu) continue;
                        float radius = _centroidWorldCpu != null && c < _centroidWorldCpu.Length ? _centroidWorldCpu[c].w : 0f;
                        memberMin = Vector3.Min(memberMin, new Vector3(state.x - radius, state.y - radius, state.z - radius));
                        memberMax = Vector3.Max(memberMax, new Vector3(state.x + radius, state.y + radius, state.z + radius));
                        anyMember = true;
                    }
                    _liveMemberBoundsValid = anyMember;
                    if (anyMember) { _liveMemberBoundsMin = memberMin; _liveMemberBoundsMax = memberMax; }
                }
            }
            if (_posStateBuf != null && _runtimeCells > 0)
            {
                _cellStateRequest = AsyncGPUReadback.Request(_posStateBuf);
                _cellStateRequestActive = true;
            }
        }

        bool CellReleased(int cellIndex)
            => _cellFreeMask != null && (cellIndex >> 5) < _cellFreeMask.Length
               && (_cellFreeMask[cellIndex >> 5] & (1u << (cellIndex & 31))) != 0u;

        // Möller–Trumbore restricted to the segment (t in [0,1]), no backface culling so shots exiting or starting
        // inside the shell still contact.
        static bool RaySegmentTriangle(Vector3 origin, Vector3 delta, Vector3 a, Vector3 b, Vector3 c, out float t)
        {
            t = 0f;
            Vector3 edge1 = b - a, edge2 = c - a;
            Vector3 p = Vector3.Cross(delta, edge2);
            float det = Vector3.Dot(edge1, p);
            if (det > -1e-12f && det < 1e-12f) return false;
            float invDet = 1f / det;
            Vector3 s = origin - a;
            float u = Vector3.Dot(s, p) * invDet;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(s, edge1);
            float v = Vector3.Dot(delta, q) * invDet;
            if (v < 0f || u + v > 1f) return false;
            t = Vector3.Dot(edge2, q) * invDet;
            return t >= 0f && t <= 1f;
        }

        static bool ClipImpactSweepAxis(float origin, float delta, float minimum, float maximum,
                                        ref float enter, ref float exit)
        {
            if (Mathf.Abs(delta) <= 1e-7f)
                return origin >= minimum && origin <= maximum;

            float inverse = 1f / delta;
            float near = (minimum - origin) * inverse;
            float far = (maximum - origin) * inverse;
            if (near > far) (near, far) = (far, near);
            enter = Mathf.Max(enter, near);
            exit = Mathf.Min(exit, far);
            return enter <= exit;
        }

        void RunQueuedImpacts()
        {
            int count = _queuedImpactCount;
            if (count <= 0) return;
            bool tagFluid = _queuedImpactFluid;
            EnsureReleaseImpulseRange();
            _queuedImpactCount = 0;
            _queuedImpactFluid = false;

            if (_simCS == null || _kApplyImpacts < 0 || _runtimeCells <= 0 ||
                _impactData == null || _clumpCentroidBuf == null || _posStateBuf == null)
                return;

            int capacity = Mathf.Clamp(maxImpactsPerFrame, 1, 4096);
            if (_impactBuf == null || _impactBuf.count != capacity || _impactBuf.stride != GpuFractureImpact.Stride)
            {
                _impactBuf?.Release();
                _impactBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, GpuFractureImpact.Stride);
            }
            _impactBuf.SetData(_impactData, 0, 0, count);

            Matrix4x4 l2w = Space != null ? Space.localToWorldMatrix : Matrix4x4.identity;
            _simCS.SetInt("_FractureImpactCount", count);
            _simCS.SetInt("_ClumpCount", _runtimeCells);
            _simCS.SetFloat("_FractureImpactMaxSpeed", Mathf.Max(0f, maxImpactSpeed));
            _simCS.SetFloat("_FractureReleaseImpulseMin", Mathf.Min(releaseImpulseMin, releaseImpulse));
            _simCS.SetFloat("_FractureReleaseImpulseMax", Mathf.Max(releaseImpulseMin, releaseImpulse));
            _simCS.SetFloat("_FractureReleaseSpinMin", Mathf.Min(releaseSpinMin, releaseSpin));
            _simCS.SetFloat("_FractureReleaseSpinMax", Mathf.Max(releaseSpinMin, releaseSpin));
            _simCS.SetInt("_FractureReleaseSeed", unchecked(seed * 73856093 ^ Time.frameCount * 19349663));
            _simCS.SetMatrix("_L2W", l2w);
            _simCS.SetMatrix("_N2W", NormalMatrix(l2w));
            if (tagFluid && _kTagImpactFluid >= 0 && _impactFluidStateBuf != null && _impactFluidMotionBuf != null
                && _impactFluidGeometryBuf != null && _impactFluidVelocityBuf != null)
            {
                _simCS.SetBuffer(_kTagImpactFluid, "_FractureImpacts", _impactBuf);
                _simCS.SetBuffer(_kTagImpactFluid, "_ImpactClumpCentroid", _clumpCentroidBuf);
                _simCS.SetBuffer(_kTagImpactFluid, "_ClumpPosStateRead", _posStateBuf);
                _simCS.SetBuffer(_kTagImpactFluid, "_ClumpRotRead", _rotBuf);
                _simCS.SetBuffer(_kTagImpactFluid, "_ImpactFluidState", _impactFluidStateBuf);
                _simCS.SetBuffer(_kTagImpactFluid, "_ImpactFluidMotion", _impactFluidMotionBuf);
                _simCS.SetBuffer(_kTagImpactFluid, "_ImpactFluidGeometry", _impactFluidGeometryBuf);
                _simCS.SetBuffer(_kTagImpactFluid, "_ImpactFluidVelocity", _impactFluidVelocityBuf);
                _simCS.Dispatch(_kTagImpactFluid, Mathf.CeilToInt(_runtimeCells / 64f), 1, 1);
            }
            _simCS.SetBuffer(_kApplyImpacts, "_FractureImpacts", _impactBuf);
            _simCS.SetBuffer(_kApplyImpacts, "_ImpactClumpCentroid", _clumpCentroidBuf);
            _simCS.SetBuffer(_kApplyImpacts, "_ClumpPosState", _posStateBuf);
            _simCS.SetBuffer(_kApplyImpacts, "_ClumpRot", _rotBuf);
            _simCS.SetBuffer(_kApplyImpacts, "_ClumpLife", _lifeBuf);
            _simCS.SetBuffer(_kApplyImpacts, "_ClumpVel", _velBuf);
            _simCS.SetBuffer(_kApplyImpacts, "_ClumpAngVel", _angVelBuf);
            _simCS.SetBuffer(_kApplyImpacts, "_ClumpRange", _clumpRangeBuf);
            _simCS.SetBuffer(_kApplyImpacts, "_ShatteredIds", _shatteredIdsBuf);
            _simCS.SetBuffer(_kApplyImpacts, "_DeformedPos", _deformedPosBuf);
            _simCS.SetBuffer(_kApplyImpacts, "_DeformedNrm", _deformedNrmBuf);
            _simCS.SetBuffer(_kApplyImpacts, "_FreeOffset", _freeOffsetBuf);
            _simCS.SetBuffer(_kApplyImpacts, "_FreeNrm", _freeNrmBuf);
            _simCS.SetBuffer(_kApplyImpacts, "_ClumpHit", _clumpHitBuf);
            // Impacts on rigid-cluster members route into the island body (a direct velocity write would just be
            // overwritten by the next kinematic projection) and can break the island like a hard landing. The
            // routing runs as its OWN single-UAV kernel: ApplyFractureImpacts already writes the 8-UAV maximum,
            // so it only READS the island buffers to skip members.
            bool islandBuffersReady = _islandOfBuf != null && _islandPosStateBuf != null && _islandAccumBuf != null;
            bool existingIslands = rigidClusters && _islandsArmed && islandBuffersReady && _kRouteImpactsIslands >= 0;
            _simCS.SetInt("_IslandRoutingOn", existingIslands ? 1 : 0);
            if (islandBuffersReady)   // always bind when present — some backends validate statically-referenced buffers
            {
                _simCS.SetBuffer(_kApplyImpacts, "_IslandOfSim", _islandOfBuf);
                _simCS.SetBuffer(_kApplyImpacts, "_IslandPosStateSim", _islandPosStateBuf);
            }

            // Release direct-hit anchored cells first. That state change is what lets structural support discover
            // the newly unsupported section around the wound. Existing island members are skipped above because
            // their per-cell velocity is kinematic and would be overwritten by IslandProject.
            _simCS.Dispatch(_kApplyImpacts, Mathf.CeilToInt(_runtimeCells / 64f), 1, 1);
            _fluidReleaseArmed = true;
            ScheduleStructuralSupport();

            // IMPORTANT ORDERING: cluster construction and live rigid-body contacts share _islandAccumBuf. The
            // construction reduction begins with IslandAccumClear. Routing a projectile before that reduction
            // therefore accepted the hit and then silently erased it, making detached pillars impact-proof.
            // Resolve the event now, then write the projectile impulse into the clean live accumulator consumed by
            // RunIslandFinish later this frame. This adds no buffer, readback, or dispatch versus the old order.
            bool structuralEventUsesIslandAccumulator = rigidClusters && structuralSupport
                                                        && IslandKernelsReady && _islandOfBuf != null;
            if (structuralEventUsesIslandAccumulator) RunStructuralSupport();

            bool routeIslands = rigidClusters && _islandsArmed && islandBuffersReady && _kRouteImpactsIslands >= 0;
            if (routeIslands)
            {
                _simCS.SetInt("_IslandRoutingOn", 1);
                _simCS.SetBuffer(_kRouteImpactsIslands, "_FractureImpacts", _impactBuf);
                _simCS.SetBuffer(_kRouteImpactsIslands, "_ImpactClumpCentroid", _clumpCentroidBuf);
                _simCS.SetBuffer(_kRouteImpactsIslands, "_ClumpPosStateRead", _posStateBuf);
                _simCS.SetBuffer(_kRouteImpactsIslands, "_IslandOfSim", _islandOfBuf);
                _simCS.SetBuffer(_kRouteImpactsIslands, "_IslandPosStateSim", _islandPosStateBuf);
                _simCS.SetBuffer(_kRouteImpactsIslands, "_IslandAccumSim", _islandAccumBuf);
                _simCS.Dispatch(_kRouteImpactsIslands, Mathf.CeilToInt(_runtimeCells / 64f), 1, 1);
            }
        }

        void RunRelease(int mode, Vector3 center, float radius, float impulseMultiplier = 1f,
                        GraphicsBuffer selectionBuffer = null, float spinMultiplier = 1f)
        {
            if (_simCS == null || _runtimeCells <= 0 || _posStateBuf == null) return;
            EnsureReleaseImpulseRange();
            if (mode != 0) _fluidReleaseArmed = true;
            // forces/colliders may not have been gathered yet (release before first Update)
            if (_forceBuf == null) { _frameForceCount = GatherGpuForces(); _frameColliderCount = GatherGpuColliders(); }
            _simCS.SetInt("_ClumpCount", _runtimeCells);
            _simCS.SetInt("_ReleaseMode", mode);
            _simCS.SetVector("_ReleaseCenterRadius", new Vector4(center.x, center.y, center.z, Mathf.Max(0f, radius)));
            _simCS.SetVector("_BodyVel", Vector3.zero);
            _simCS.SetFloat("_ReleaseImpulseMin",
                Mathf.Min(releaseImpulseMin, releaseImpulse) * Mathf.Max(0f, impulseMultiplier));
            _simCS.SetFloat("_ReleaseImpulse",
                Mathf.Max(releaseImpulseMin, releaseImpulse) * Mathf.Max(0f, impulseMultiplier));
            _simCS.SetFloat("_ReleaseSpinMin",
                Mathf.Min(releaseSpinMin, releaseSpin) * Mathf.Max(0f, spinMultiplier));
            _simCS.SetFloat("_ReleaseSpin",
                Mathf.Max(releaseSpinMin, releaseSpin) * Mathf.Max(0f, spinMultiplier));
            _simCS.SetFloat("_InheritBodyVelocity", 0f);
            Matrix4x4 l2w = Space != null ? Space.localToWorldMatrix : Matrix4x4.identity;
            _simCS.SetMatrix("_L2W", l2w);
            _simCS.SetMatrix("_N2W", NormalMatrix(l2w));
            _simCS.SetBuffer(_kRelease, "_ClumpPosState", _posStateBuf);
            _simCS.SetBuffer(_kRelease, "_ClumpRot", _rotBuf);
            _simCS.SetBuffer(_kRelease, "_ClumpLife", _lifeBuf);
            _simCS.SetBuffer(_kRelease, "_ClumpVel", _velBuf);
            _simCS.SetBuffer(_kRelease, "_ClumpAngVel", _angVelBuf);
            _simCS.SetBuffer(_kRelease, "_ClumpRange", _clumpRangeBuf);
            _simCS.SetBuffer(_kRelease, "_ShatteredIds", _shatteredIdsBuf);
            _simCS.SetBuffer(_kRelease, "_DeformedPos", _deformedPosBuf);
            _simCS.SetBuffer(_kRelease, "_DeformedNrm", _deformedNrmBuf);
            _simCS.SetBuffer(_kRelease, "_FreeOffset", _freeOffsetBuf);
            _simCS.SetBuffer(_kRelease, "_FreeNrm", _freeNrmBuf);
            _simCS.SetBuffer(_kRelease, "_ClumpHit", selectionBuffer != null ? selectionBuffer : _clumpHitBuf);
            _simCS.Dispatch(_kRelease, Mathf.CeilToInt(_runtimeCells / 64f), 1, 1);
        }

        // Field-driven progressive release: flag fragments inside the break field (≥ threshold), then release them.
        void RunFieldRelease(float dt)
        {
            if (_cs == null || _kMarkField < 0 || _clumpCentroidBuf == null || _runtimeCells <= 0) return;
            if (breakSpeed > 0f) breakProgress = Mathf.Clamp01(breakProgress + breakSpeed * dt);

            int fieldHash = FieldReleaseHash();
            if (_fieldReleaseHashValid && fieldHash == _lastFieldReleaseHash) return;
            _lastFieldReleaseHash = fieldHash;
            _fieldReleaseHashValid = true;

            Vector3 center = SetFieldParams(_kMarkField);
            _cs.SetFloat("_BreakThreshold", 1f - breakProgress);
            _cs.Dispatch(_kMarkField, Mathf.CeilToInt(_runtimeCells / 64f), 1, 1);

            if (breakProgress > 1e-4f)
            {
                _fluidReleaseArmed = true;
                ScheduleStructuralSupport();
            }
            RunRelease(0, center, 0f);   // mode 0 = release the flagged fragments (burst away from the field centre)
        }

        int FieldReleaseHash()
        {
            unchecked
            {
                Transform fieldTransform = breakField != null ? breakField : transform;
                int hash = fieldTransform.localToWorldMatrix.GetHashCode();
                hash = hash * 31 + (int)breakFieldShape;
                hash = hash * 31 + breakFieldSize.GetHashCode();
                hash = hash * 31 + breakFalloff.GetHashCode();
                hash = hash * 31 + breakProgress.GetHashCode();
                return hash;
            }
        }

        // Sets the field params on the Voronoi compute. Returns the field centre.
        Vector3 SetFieldParams(int kernel)
        {
            Transform ft = breakField != null ? breakField : transform;
            Vector3 center = ft.position, scale = ft.lossyScale;
            float size = Mathf.Max(1e-4f, breakFieldSize);
            float radius = size * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z));
            Matrix4x4 fieldMat = Matrix4x4.TRS(center, ft.rotation, size * scale);
            _cs.SetInt("_FieldShape", (int)breakFieldShape);
            _cs.SetVector("_FieldCenter", center);
            _cs.SetFloat("_FieldRadius", Mathf.Max(1e-4f, radius));
            _cs.SetFloat("_FieldFalloff", breakFalloff);
            _cs.SetVector("_FieldUp", ft.up);
            _cs.SetMatrix("_FieldInv", fieldMat.inverse);
            _cs.SetInt("_FieldClumpCount", _runtimeCells);
            _cs.SetBuffer(kernel, "_ClumpCentroid", _clumpCentroidBuf);
            _cs.SetBuffer(kernel, "_ClumpPosStateF", _posStateBuf);
            _cs.SetBuffer(kernel, "_ClumpHitF", _clumpHitBuf);
            return center;
        }

        void ScheduleStructuralSupport()
        {
            if (!structuralSupport) return;
            _supportPassesRemaining = Mathf.Max(_supportPassesRemaining, _supportGraphDepth);
        }

        // Event-driven structural collapse. A private hit buffer preserves field/impact debris markers.
        // Rigid Clusters: the FULL unsupported set is resolved in the event frame, its connected components are
        // labeled on the GPU, and each island detaches as one rigid body (see BrazeVoronoi.compute RIGID CLUSTERS).
        // Legacy (rigidClusters off): each pass advances one rooted graph layer and releases it granular.
        void RunStructuralSupport()
        {
            if (!structuralSupport)
            {
                _supportPassesRemaining = 0;
                return;
            }
            if (_supportPassesRemaining <= 0 || _cs == null || _kMarkUnsupported < 0 ||
                _neighborBuf == null || _neighborCountBuf == null || _supportLevelBuf == null ||
                _supportHitBuf == null || _cohesionStateBuf == null || _posStateBuf == null || _runtimeCells <= 0)
                return;

            _cs.SetInt("_FieldClumpCount", _runtimeCells);
            _cs.SetFloat("_SupportLateral", Mathf.Clamp01(lateralSupport));
            CohesionSettings cohesionSettings = ResolveCohesionSettings();
            bool useCohesion = cohesiveCollapse && _cohesionStateBuf != null &&
                               _cohesionGroupBuf != null && _cohesionGroupCenterBuf != null &&
                               _cohesionGroupReleaseBuf != null && _cohesionGroupCount > 0 &&
                               _kClearClusterRelease >= 0 && _kMarkClusterRelease >= 0 && _kExpandClusterRelease >= 0;
            _cs.SetBuffer(_kMarkUnsupported, "_NeighborsRO", _neighborBuf);
            _cs.SetBuffer(_kMarkUnsupported, "_NeighborCountRO", _neighborCountBuf);
            _cs.SetBuffer(_kMarkUnsupported, "_SupportLevelRO", _supportLevelBuf);
            _cs.SetBuffer(_kMarkUnsupported, "_ClumpPosStateF", _posStateBuf);
            _cs.SetBuffer(_kMarkUnsupported, "_ClumpHitF", _supportHitBuf);

            Vector3 center = Space.TransformPoint(_localBounds.center);
            if (rigidClusters && IslandKernelsReady && _islandOfBuf != null)
            {
                RunClusterCollapse(center);
                return;
            }

            _cs.SetInt("_CascadeAccumulate", 0);
            int passes = Mathf.Min(Mathf.Clamp(collapseSpeed, 1, 8), _supportPassesRemaining);
            for (int pass = 0; pass < passes; pass++)
            {
                _cs.Dispatch(_kMarkUnsupported, Mathf.CeilToInt(_runtimeCells / 64f), 1, 1);
                if (useCohesion) ExpandCohesiveRelease(cohesionSettings);
                RunRelease(0, center, 0f, 0f, _supportHitBuf); // disconnect, then let the Braze force stack take over
                if (useCohesion) SeedCohesiveMotion(cohesionSettings);
                _supportPassesRemaining--;
            }
        }

        bool IslandKernelsReady =>
            _kIslandLabelInit >= 0 && _kIslandLabelProp >= 0 && _kIslandLabelCompress >= 0 &&
            _kIslandAccumClear >= 0 && _kIslandAccumCount >= 0 && _kIslandAccumInertia >= 0 &&
            _kIslandFinalizeRoots >= 0 && _kIslandCreatePrepare >= 0 && _kIslandCreateActivate >= 0 &&
            _kIslandFrameBegin >= 0 && _kIslandApplyContacts >= 0 && _kIslandProject >= 0;

        // The condemned-cell test reads the support marks + cell state through SRV aliases (UAV budget).
        void BindIslandMarked(int kernel)
        {
            _cs.SetBuffer(kernel, "_IslandHitRead", _supportHitBuf);
            _cs.SetBuffer(kernel, "_IslandCellPosRead", _posStateBuf);
        }

        // One-frame rigid-cluster event: complete cascade → connected-component labels → per-island reduction →
        // island creation. Cells in islands below Min Cluster Cells keep their hit flag and fall through to the
        // legacy granular release below (loose — the pre-baked cohesion groups only apply in legacy mode, where
        // ExpandClusterRelease seeds them). ~(graphDepth + 32) trivial 1-per-cell dispatches, event-only.
        void RunClusterCollapse(Vector3 center)
        {
            _supportPassesRemaining = 0;   // whole event resolves now
            int groups = Mathf.CeilToInt(_runtimeCells / 64f);

            _cs.SetInt("_CascadeAccumulate", 1);
            int cascadePasses = Mathf.Clamp(_supportGraphDepth, 1, 512);
            for (int pass = 0; pass < cascadePasses; pass++)
                _cs.Dispatch(_kMarkUnsupported, groups, 1, 1);
            _cs.SetInt("_CascadeAccumulate", 0);

            BindIslandMarked(_kIslandLabelInit);
            _cs.SetBuffer(_kIslandLabelInit, "_IslandOf", _islandOfBuf);
            _cs.Dispatch(_kIslandLabelInit, groups, 1, 1);
            BindIslandMarked(_kIslandLabelProp);
            _cs.SetBuffer(_kIslandLabelProp, "_IslandOf", _islandOfBuf);
            _cs.SetBuffer(_kIslandLabelProp, "_NeighborsRO", _neighborBuf);
            _cs.SetBuffer(_kIslandLabelProp, "_NeighborCountRO", _neighborCountBuf);
            BindIslandMarked(_kIslandLabelCompress);
            _cs.SetBuffer(_kIslandLabelCompress, "_IslandOf", _islandOfBuf);
            for (int pass = 0; pass < 32; pass++)   // min-label + path compression: log-time convergence
            {
                _cs.Dispatch(_kIslandLabelProp, groups, 1, 1);
                _cs.Dispatch(_kIslandLabelCompress, groups, 1, 1);
            }

            _cs.SetBuffer(_kIslandAccumClear, "_IslandAccum", _islandAccumBuf);
            _cs.Dispatch(_kIslandAccumClear, groups, 1, 1);

            Vector3 islandBase = Space.TransformPoint(_localBounds.center);
            _cs.SetVector("_IslandBase", islandBase);
            BindIslandMarked(_kIslandAccumCount);
            _cs.SetBuffer(_kIslandAccumCount, "_IslandOfRead", _islandOfBuf);
            _cs.SetBuffer(_kIslandAccumCount, "_IslandAccum", _islandAccumBuf);
            _cs.SetBuffer(_kIslandAccumCount, "_ClumpCentroid", _clumpCentroidBuf);
            _cs.SetBuffer(_kIslandAccumCount, "_ClumpMass", _clumpMassBuf);
            _cs.SetBuffer(_kIslandAccumCount, "_NeighborsRO", _neighborBuf);       // break-hinge detection
            _cs.SetBuffer(_kIslandAccumCount, "_NeighborCountRO", _neighborCountBuf);
            _cs.Dispatch(_kIslandAccumCount, groups, 1, 1);
            BindIslandMarked(_kIslandAccumInertia);
            _cs.SetBuffer(_kIslandAccumInertia, "_IslandOfRead", _islandOfBuf);
            _cs.SetBuffer(_kIslandAccumInertia, "_IslandAccum", _islandAccumBuf);
            _cs.SetBuffer(_kIslandAccumInertia, "_ClumpCentroid", _clumpCentroidBuf);
            _cs.SetBuffer(_kIslandAccumInertia, "_ClumpMass", _clumpMassBuf);
            _cs.Dispatch(_kIslandAccumInertia, groups, 1, 1);

            _cs.SetInt("_ClusterMinCells", Mathf.Max(1, clusterMinCells));
            // Newborn islands inherit the gravity torque about their break hinge (~toppleStrength-scaled seconds
            // of lean) plus the matching hinge-swing linear velocity, and a size-attenuated micro-asymmetry tumble
            // from the authored Spin Speed range — so nothing drops laser-straight down the axis.
            _cs.SetVector("_IslandGravity", NetGravity());
            _cs.SetFloat("_IslandToppleSeed", Mathf.Max(0f, toppleStrength) * 0.35f);
            _cs.SetFloat("_IslandSpinMin", Mathf.Min(releaseSpinMin, releaseSpin));
            _cs.SetFloat("_IslandSpinMax", Mathf.Max(releaseSpinMin, releaseSpin));
            _cs.SetFloat("_IslandAvgRadius", Mathf.Max(1e-4f, _avgRadius * WorldRadiusScale()));
            _cs.SetInt("_IslandSpinSeed", unchecked(seed * 73856093 ^ Time.frameCount * 19349663));
            BindIslandMarked(_kIslandFinalizeRoots);
            _cs.SetBuffer(_kIslandFinalizeRoots, "_IslandOfRead", _islandOfBuf);
            _cs.SetBuffer(_kIslandFinalizeRoots, "_IslandAccum", _islandAccumBuf);
            _cs.SetBuffer(_kIslandFinalizeRoots, "_IslandPosState", _islandPosStateBuf);
            _cs.SetBuffer(_kIslandFinalizeRoots, "_IslandRot", _islandRotBuf);
            _cs.SetBuffer(_kIslandFinalizeRoots, "_IslandVel", _islandVelBuf);
            _cs.SetBuffer(_kIslandFinalizeRoots, "_IslandAngVel", _islandAngVelBuf);
            _cs.SetBuffer(_kIslandFinalizeRoots, "_IslandData", _islandDataBuf);
            _cs.Dispatch(_kIslandFinalizeRoots, groups, 1, 1);

            // Member creation is split (8-UAV limit): PREPARE captures vertices + resolves flags while anchored,
            // ACTIVATE then frees accepted members into the island.
            Matrix4x4 l2w = Space != null ? Space.localToWorldMatrix : Matrix4x4.identity;
            _cs.SetMatrix("_IslandL2W", l2w);
            _cs.SetMatrix("_IslandN2W", NormalMatrix(l2w));
            _cs.SetBuffer(_kIslandCreatePrepare, "_ClumpHitF", _supportHitBuf);
            _cs.SetBuffer(_kIslandCreatePrepare, "_IslandCellPosRead", _posStateBuf);
            _cs.SetBuffer(_kIslandCreatePrepare, "_IslandOf", _islandOfBuf);
            _cs.SetBuffer(_kIslandCreatePrepare, "_IslandStateRead", _islandPosStateBuf);
            _cs.SetBuffer(_kIslandCreatePrepare, "_ClumpCentroid", _clumpCentroidBuf);
            _cs.SetBuffer(_kIslandCreatePrepare, "_IslandClumpRange", _clumpRangeBuf);
            _cs.SetBuffer(_kIslandCreatePrepare, "_IslandShatteredIds", _shatteredIdsBuf);
            _cs.SetBuffer(_kIslandCreatePrepare, "_IslandDeformedPos", _deformedPosBuf);
            _cs.SetBuffer(_kIslandCreatePrepare, "_IslandDeformedNrm", _deformedNrmBuf);
            _cs.SetBuffer(_kIslandCreatePrepare, "_IslandFreeOffset", _freeOffsetBuf);
            _cs.SetBuffer(_kIslandCreatePrepare, "_IslandFreeNrm", _freeNrmBuf);
            _cs.Dispatch(_kIslandCreatePrepare, groups, 1, 1);

            _cs.SetBuffer(_kIslandCreateActivate, "_IslandOfRead", _islandOfBuf);
            _cs.SetBuffer(_kIslandCreateActivate, "_IslandStateRead", _islandPosStateBuf);
            _cs.SetBuffer(_kIslandCreateActivate, "_ClumpCentroid", _clumpCentroidBuf);
            _cs.SetBuffer(_kIslandCreateActivate, "_ClumpPosStateF", _posStateBuf);
            _cs.SetBuffer(_kIslandCreateActivate, "_IslandRestOffset", _islandRestOffsetBuf);
            _cs.SetBuffer(_kIslandCreateActivate, "_IslandClumpRot", _rotBuf);
            _cs.SetBuffer(_kIslandCreateActivate, "_IslandClumpVel", _velBuf);
            _cs.SetBuffer(_kIslandCreateActivate, "_IslandClumpAngVel", _angVelBuf);
            _cs.SetBuffer(_kIslandCreateActivate, "_IslandClumpLife", _lifeBuf);
            _cs.SetBuffer(_kIslandCreateActivate, "_IslandCohesionState", _cohesionStateBuf);
            _cs.Dispatch(_kIslandCreateActivate, groups, 1, 1);

            // Leftover flagged cells (islands too small to cluster) release as loose debris.
            RunRelease(0, center, 0f, 0f, _supportHitBuf);

            _islandsArmed = true;
            _fluidReleaseArmed = true;
        }

        // Per-frame island step 1 (before impacts/sim): clear this frame's contact accumulators and integrate the
        // island rigid bodies. Nearly free when no island is active — every thread early-outs.
        void RunIslandFrameBegin(float dt)
        {
            if (!_islandsArmed || !rigidClusters || _cs == null || !IslandKernelsReady ||
                _islandOfBuf == null || _runtimeCells <= 0) return;
            _cs.SetInt("_FieldClumpCount", _runtimeCells);
            _cs.SetFloat("_IslandDt", Mathf.Max(dt, 1e-4f));
            _cs.SetVector("_IslandGravity", NetGravity());
            _cs.SetFloat("_IslandLinearDrag", Mathf.Max(0f, linearDrag));
            _cs.SetFloat("_IslandSpinDamp", Mathf.Max(0f, spinDamping));
            _cs.SetFloat("_IslandSleepDelay", Mathf.Max(0f, sleepDelay));
            _cs.SetBuffer(_kIslandFrameBegin, "_IslandOfRead", _islandOfBuf);
            _cs.SetBuffer(_kIslandFrameBegin, "_IslandPosState", _islandPosStateBuf);
            _cs.SetBuffer(_kIslandFrameBegin, "_IslandRot", _islandRotBuf);
            _cs.SetBuffer(_kIslandFrameBegin, "_IslandVel", _islandVelBuf);
            _cs.SetBuffer(_kIslandFrameBegin, "_IslandAngVel", _islandAngVelBuf);
            _cs.SetBuffer(_kIslandFrameBegin, "_IslandDataRead", _islandDataBuf);
            _cs.SetBuffer(_kIslandFrameBegin, "_IslandAccum", _islandAccumBuf);
            _cs.Dispatch(_kIslandFrameBegin, Mathf.CeilToInt(_runtimeCells / 64f), 1, 1);
        }

        // Per-frame island step 2 (after the collide pass): apply aggregated contacts/impacts, break/sleep, then
        // kinematically project every member from its island transform — the projection is what makes the section
        // move as ONE piece instead of sand.
        void RunIslandFinish(float dt)
        {
            if (!_islandsArmed || !rigidClusters || _cs == null || !IslandKernelsReady ||
                _islandOfBuf == null || _runtimeCells <= 0) return;
            int groups = Mathf.CeilToInt(_runtimeCells / 64f);
            _cs.SetInt("_FieldClumpCount", _runtimeCells);
            _cs.SetFloat("_IslandDt", Mathf.Max(dt, 1e-4f));
            _cs.SetFloat("_ClusterBreakImpulse", ResolveCohesionSettings().breakImpulse);
            _cs.SetFloat("_IslandContactStrength", collisionStrength);
            _cs.SetFloat("_IslandAngularFactor", angularResponse);
            _cs.SetFloat("_IslandSleepLinear", Mathf.Max(0f, sleepLinearSpeed));
            _cs.SetFloat("_IslandSleepAngular", Mathf.Max(0f, sleepAngularSpeed));
            _cs.SetVector("_IslandGravity", NetGravity());
            _cs.SetFloat("_IslandTopple", Mathf.Max(0f, toppleStrength));
            _cs.SetFloat("_IslandAvgRadius", Mathf.Max(1e-4f, _avgRadius * WorldRadiusScale()));
            // The GPU applies only 30% on a hard strike and ramps to the full authored damping near rest. This keeps
            // the weighty first tumble while letting a supported monolith converge instead of rocking forever.
            _cs.SetFloat("_IslandAngularDamp", Mathf.Max(0f, contactAngularDamping));
            _cs.SetBuffer(_kIslandApplyContacts, "_IslandOfRead", _islandOfBuf);
            _cs.SetBuffer(_kIslandApplyContacts, "_IslandPosState", _islandPosStateBuf);
            _cs.SetBuffer(_kIslandApplyContacts, "_IslandVel", _islandVelBuf);
            _cs.SetBuffer(_kIslandApplyContacts, "_IslandAngVel", _islandAngVelBuf);
            _cs.SetBuffer(_kIslandApplyContacts, "_IslandDataRead", _islandDataBuf);
            _cs.SetBuffer(_kIslandApplyContacts, "_IslandAccumRead", _islandAccumBuf);
            _cs.Dispatch(_kIslandApplyContacts, groups, 1, 1);

            _cs.SetBuffer(_kIslandProject, "_IslandOf", _islandOfBuf);
            _cs.SetBuffer(_kIslandProject, "_IslandStateRead", _islandPosStateBuf);
            _cs.SetBuffer(_kIslandProject, "_IslandRotRead", _islandRotBuf);
            _cs.SetBuffer(_kIslandProject, "_IslandVelRead", _islandVelBuf);
            _cs.SetBuffer(_kIslandProject, "_IslandAngVelRead", _islandAngVelBuf);
            _cs.SetBuffer(_kIslandProject, "_IslandRestOffsetRead", _islandRestOffsetBuf);
            _cs.SetBuffer(_kIslandProject, "_ClumpPosStateF", _posStateBuf);
            _cs.SetBuffer(_kIslandProject, "_ClumpHitF", _clumpHitBuf);
            _cs.SetBuffer(_kIslandProject, "_IslandClumpRot", _rotBuf);
            _cs.SetBuffer(_kIslandProject, "_IslandClumpVel", _velBuf);
            _cs.SetBuffer(_kIslandProject, "_IslandClumpAngVel", _angVelBuf);
            _cs.Dispatch(_kIslandProject, groups, 1, 1);
        }

        void ExpandCohesiveRelease(CohesionSettings settings)
        {
            int cellGroups = Mathf.CeilToInt(_runtimeCells / 64f);
            int clusterGroups = Mathf.CeilToInt(_cohesionGroupCount / 64f);
            _cs.SetInt("_FieldClumpCount", _runtimeCells);
            _cs.SetInt("_CohesionGroupCount", _cohesionGroupCount);
            _cs.SetInt("_CohesionEnabled", 1);
            _cs.SetFloat("_CohesionDebrisFraction", settings.debrisFraction);

            _cs.SetBuffer(_kClearClusterRelease, "_CohesionGroupRelease", _cohesionGroupReleaseBuf);
            _cs.Dispatch(_kClearClusterRelease, clusterGroups, 1, 1);

            _cs.SetBuffer(_kMarkClusterRelease, "_SupportHitRead", _supportHitBuf);
            _cs.SetBuffer(_kMarkClusterRelease, "_CohesionGroup", _cohesionGroupBuf);
            _cs.SetBuffer(_kMarkClusterRelease, "_CohesionGroupRelease", _cohesionGroupReleaseBuf);
            _cs.Dispatch(_kMarkClusterRelease, cellGroups, 1, 1);

            _cs.SetBuffer(_kExpandClusterRelease, "_SupportPosRead", _posStateBuf);
            _cs.SetBuffer(_kExpandClusterRelease, "_SupportLevelRO", _supportLevelBuf);
            _cs.SetBuffer(_kExpandClusterRelease, "_CohesionGroup", _cohesionGroupBuf);
            _cs.SetBuffer(_kExpandClusterRelease, "_CohesionGroupReleaseRead", _cohesionGroupReleaseBuf);
            _cs.SetBuffer(_kExpandClusterRelease, "_ClumpHitF", _supportHitBuf);
            _cs.SetBuffer(_kExpandClusterRelease, "_CohesionState", _cohesionStateBuf);
            _cs.Dispatch(_kExpandClusterRelease, cellGroups, 1, 1);
        }

        void SeedCohesiveMotion(CohesionSettings settings)
        {
            if (_kSeedCohesion < 0 || _cohesionStateBuf == null || _cohesionGroupBuf == null ||
                _cohesionGroupCenterBuf == null || _velBuf == null || _angVelBuf == null) return;
            Matrix4x4 l2w = Space != null ? Space.localToWorldMatrix : Matrix4x4.identity;
            _cs.SetInt("_FieldClumpCount", _runtimeCells);
            _cs.SetMatrix("_CohesionL2W", l2w);
            _cs.SetVector("_CohesionGravity", NetGravity());
            _cs.SetFloat("_CohesionTopple", settings.topple);
            _cs.SetFloat("_CohesionReleaseSpinMin", Mathf.Min(releaseSpinMin, releaseSpin));
            _cs.SetFloat("_CohesionReleaseSpinMax", Mathf.Max(releaseSpinMin, releaseSpin));
            _cs.SetInt("_CohesionReleaseSeed", seed);
            _cs.SetBuffer(_kSeedCohesion, "_CohesionState", _cohesionStateBuf);
            _cs.SetBuffer(_kSeedCohesion, "_CohesionGroup", _cohesionGroupBuf);
            _cs.SetBuffer(_kSeedCohesion, "_CohesionGroupCenter", _cohesionGroupCenterBuf);
            _cs.SetBuffer(_kSeedCohesion, "_ClumpPosStateF", _posStateBuf);
            _cs.SetBuffer(_kSeedCohesion, "_CohesionVel", _velBuf);
            _cs.SetBuffer(_kSeedCohesion, "_CohesionAngVel", _angVelBuf);
            _cs.Dispatch(_kSeedCohesion, Mathf.CeilToInt(_runtimeCells / 64f), 1, 1);
            _cohesionTimeRemaining = Mathf.Max(_cohesionTimeRemaining, settings.duration + 0.1f);
        }

        void RunCohesion(float dt)
        {
            if (!cohesiveCollapse || _cohesionTimeRemaining <= 0f)
            {
                _cohesionTimeRemaining = 0f;
                return;
            }
            if (_cs == null || _kSolveCohesion < 0 || _kApplyCohesion < 0 || _runtimeCells <= 0 ||
                _cohesionStateBuf == null || _cohesionGroupBuf == null || _cohesionGroupCenterBuf == null ||
                _cohesionPositionDeltaBuf == null || _cohesionVelocityDeltaBuf == null ||
                _cohesionAngularDeltaBuf == null || _cohesionRotationTargetBuf == null)
                return;

            CohesionSettings settings = ResolveCohesionSettings();
            _cs.SetInt("_FieldClumpCount", _runtimeCells);
            _cs.SetFloat("_CohesionStrength", settings.strength);
            _cs.SetFloat("_CohesionDamping", settings.damping);
            _cs.SetFloat("_CohesionBreakStrain", settings.breakStrain);
            _cs.SetFloat("_CohesionDuration", settings.duration);
            _cs.SetFloat("_CohesionDt", Mathf.Max(dt, 1e-4f));
            _cs.SetFloat("_CohesionMaxCorrection", Mathf.Max(0.001f, _avgRadius * WorldRadiusScale() * 0.3f));

            BindCohesionKernel(_kSolveCohesion);
            BindCohesionKernel(_kApplyCohesion);
            int groups = Mathf.CeilToInt(_runtimeCells / 64f);
            for (int iteration = 0; iteration < settings.iterations; iteration++)
            {
                _cs.Dispatch(_kSolveCohesion, groups, 1, 1);
                _cs.Dispatch(_kApplyCohesion, groups, 1, 1);
            }
            _cohesionTimeRemaining = Mathf.Max(0f, _cohesionTimeRemaining - dt);
        }

        void BindCohesionKernel(int kernel)
        {
            if (kernel == _kSolveCohesion)
            {
                _cs.SetBuffer(kernel, "_NeighborsRO", _neighborBuf);
                _cs.SetBuffer(kernel, "_NeighborCountRO", _neighborCountBuf);
                _cs.SetBuffer(kernel, "_ClumpCentroid", _clumpCentroidBuf);
                _cs.SetBuffer(kernel, "_CohesionGroup", _cohesionGroupBuf);
                _cs.SetBuffer(kernel, "_CohesionStateRead", _cohesionStateBuf);
                _cs.SetBuffer(kernel, "_CohesionPosRead", _posStateBuf);
                _cs.SetBuffer(kernel, "_CohesionVelRead", _velBuf);
                _cs.SetBuffer(kernel, "_CohesionAngVelRead", _angVelBuf);
                _cs.SetBuffer(kernel, "_CohesionRotRead", _rotBuf);
                _cs.SetBuffer(kernel, "_CohesionLife", _lifeBuf);
                _cs.SetBuffer(kernel, "_CohesionPositionDelta", _cohesionPositionDeltaBuf);
                _cs.SetBuffer(kernel, "_CohesionVelocityDelta", _cohesionVelocityDeltaBuf);
                _cs.SetBuffer(kernel, "_CohesionAngularDelta", _cohesionAngularDeltaBuf);
                _cs.SetBuffer(kernel, "_CohesionRotationTarget", _cohesionRotationTargetBuf);
                return;
            }

            _cs.SetBuffer(kernel, "_ClumpPosStateF", _posStateBuf);
            _cs.SetBuffer(kernel, "_CohesionState", _cohesionStateBuf);
            _cs.SetBuffer(kernel, "_CohesionVel", _velBuf);
            _cs.SetBuffer(kernel, "_CohesionAngVel", _angVelBuf);
            _cs.SetBuffer(kernel, "_CohesionRot", _rotBuf);
            _cs.SetBuffer(kernel, "_CohesionPositionDeltaRead", _cohesionPositionDeltaBuf);
            _cs.SetBuffer(kernel, "_CohesionVelocityDeltaRead", _cohesionVelocityDeltaBuf);
            _cs.SetBuffer(kernel, "_CohesionAngularDeltaRead", _cohesionAngularDeltaBuf);
            _cs.SetBuffer(kernel, "_CohesionRotationTargetRead", _cohesionRotationTargetBuf);
        }

        /// <summary>Release every fragment so they fly off as rigid GPU debris.</summary>
        [ContextMenu("Test ▶ Release All")]
        [BrazeTimelineEvent("DESTRUCTION/Release All")]
        public void ReleaseAll()
        {
            if (!Application.isPlaying) { Debug.LogWarning("[BrazeVoronoiFracture] Release only works in Play mode."); return; }
            if (!_renderReady) { _releaseOnReady = true; return; }
            _supportPassesRemaining = 0;
            DissolveAllIslands();   // "everything flies" — active monoliths break into their cells
            RunRelease(1, Space.TransformPoint(_localBounds.center), 0f);
        }

        // Unbind every rigid-cluster member (they keep their last projected ballistic state) and deactivate all
        // island slots. Event-rare CPU uploads — never on the per-frame path.
        void DissolveAllIslands()
        {
            if (!_islandsArmed || _islandOfBuf == null || _runtimeCells <= 0) return;
            var none = new uint[_runtimeCells];
            for (int c = 0; c < _runtimeCells; c++) none[c] = 0xffffffffu;
            _islandOfBuf.SetData(none);
            if (_islandPosStateBuf != null) _islandPosStateBuf.SetData(new Vector4[_runtimeCells]);
        }

        /// <summary>Release fragments whose centre is within <paramref name="radius"/> of a world point.</summary>
        public void ReleaseSphere(Vector3 centerWorld, float radius)
        {
            if (!_renderReady) return;
            RunRelease(2, centerWorld, radius);
            ScheduleStructuralSupport();
        }

        int GatherGpuForces()
        {
            _forceScratch.Clear();
            // ONLY the forces explicitly dropped into the list act on the fragments — forces are never global, so a
            // stray Gravity/Wind elsewhere in the scene won't silently drive this fracture (matches Braze emitters).
            if (forces != null)
                for (int i = 0; i < forces.Count; i++)
                {
                    var f = forces[i];
                    if (f != null && f.isActiveAndEnabled && !_forceScratch.Contains(f)) _forceScratch.Add(f);
                }
            int count = _forceScratch.Count, uploadCount = Mathf.Max(1, count);
            if (_forceData.Length < uploadCount) _forceData = new GpuParticleForce[Mathf.NextPowerOfTwo(uploadCount)];
            if (count == 0) _forceData[0] = default;
            else for (int i = 0; i < count; i++) _forceData[i] = _forceScratch[i].ToGpu(i);

            if (_forceBuf == null || _forceBuf.count < uploadCount)
            {
                _forceBuf?.Release();
                _forceBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.NextPowerOfTwo(uploadCount), GpuParticleForce.Stride);
            }
            _forceBuf.SetData(_forceData, 0, 0, uploadCount);

            // Skip the 256×count curve rebake + full upload when the curves and buffer are unchanged (see FalloffAtlasHash).
            bool realloc = EnsureFalloffBuf(uploadCount);
            int hash = BrazeParticleForce.FalloffAtlasHash(_forceScratch, count);
            if (realloc || hash != _falloffHash)
            {
                for (int r = 0; r < uploadCount; r++)
                {
                    var c = r < count ? _forceScratch[r].falloff : null;
                    int baseI = r * 256;
                    for (int x = 0; x < 256; x++) _falloffData[baseI + x] = c != null ? c.Evaluate(x / 255f) : 0f;
                }
                _forceFalloffBuf.SetData(_falloffData, 0, 0, uploadCount * 256);
                _falloffHash = hash;
            }
            return count;
        }

        int GatherGpuColliders()
        {
            _colliderScratch.Clear();
            var all = BrazeParticleCollider.Active;
            BrazeRuntimeRegistry.PruneNulls(all);
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (c != null && c.isActiveAndEnabled && c.shape != BrazeParticleCollider.Shape.Mesh) _colliderScratch.Add(c);
            }
            int count = _colliderScratch.Count, uploadCount = Mathf.Max(1, count);
            if (_colliderData.Length < uploadCount) _colliderData = new GpuParticleCollider[Mathf.NextPowerOfTwo(uploadCount)];
            if (count == 0) _colliderData[0] = default;
            else for (int i = 0; i < count; i++) _colliderData[i] = _colliderScratch[i].ToGpu();

            if (_colliderBuf == null || _colliderBuf.count < uploadCount)
            {
                _colliderBuf?.Release();
                _colliderBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.NextPowerOfTwo(uploadCount), GpuParticleCollider.Stride);
            }
            _colliderBuf.SetData(_colliderData, 0, 0, uploadCount);
            return count;
        }

        // Returns true if the buffer was (re)allocated, so the caller re-uploads even when the content hash matches.
        bool EnsureFalloffBuf(int rows)
        {
            rows = Mathf.Max(1, rows);
            int floats = rows * 256;
            if (_forceFalloffBuf != null && _forceFalloffBuf.count == floats) return false;
            _forceFalloffBuf?.Release();
            _forceFalloffBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, floats, sizeof(float));
            if (_falloffData.Length < floats) _falloffData = new float[floats];
            return true;
        }

        // Edit mode ([ExecuteAlways]) needs DestroyImmediate; play needs Destroy.
        static void DestroyObj(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
        }

        // The hidden renderer host is deliberately longer-lived than its transient mesh/buffers. Reusing it across
        // drafts avoids invalidating Unity's Scene Hierarchy row cache on every authoring rebuild. Its final edit-mode
        // destruction is deferred until inspectors finish updating; this also avoids a Unity 6 hierarchy race while
        // the backup scene is swapped during Enter Play Mode with Domain Reload disabled.
        void ClearChunkRendererHost()
        {
            if (_chunkRenderer != null) _chunkRenderer.enabled = false;
            if (_chunkGo != null && _chunkGo.TryGetComponent(out MeshFilter mf)) mf.sharedMesh = null;
        }

        void DestroyChunkRendererHost()
        {
            if (_chunkGo == null) { _chunkRenderer = null; return; }
            GameObject host = _chunkGo;
            _chunkGo = null;
            _chunkRenderer = null;
#if UNITY_EDITOR
            if (!Application.isPlaying && !UnityEditor.EditorApplication.isCompiling &&
                !UnityEditor.EditorApplication.isUpdating)
            {
                UnityEditor.EditorApplication.delayCall += () =>
                {
                    DestroyObj(host);
                    UnityEditor.EditorApplication.RepaintHierarchyWindow();
                };
                return;
            }
#endif
            DestroyObj(host);
        }

        void ReleaseRuntime()
        {
            _renderReady = false;
            _transformBindingsValid = false;
            _fluidReleaseArmed = false;
            _impactFluidRemaining = 0f;
            _impactFluidCellFraction = 0f;
            _impactFluidContainer = null;
            _frameEffectorCount = 0;
            _queuedImpactCount = 0;
            _queuedImpactFluid = false;
            _supportPassesRemaining = 0;
            _fieldReleaseHashValid = false;
            _cohesionTimeRemaining = 0f;
            _cellStateRequestActive = false;   // pending readback (if any) will surface as hasError and be ignored
            _cellFreeMask = null;
            _islandOfRequestActive = false;
            _islandOfCpu = null;
            _cellLivePosCpu = null;
            _liveMemberBoundsValid = false;
#if UNITY_EDITOR
            ReleasePreviewWire();
#endif
            // Stop the emitter referencing chunk buffers we're about to free (prevents a stale-buffer bind next frame).
            ClearDebrisAttachmentSources();
            _posStateBuf?.Release(); _posStateBuf = null;
            _rotBuf?.Release(); _rotBuf = null;
            _lifeBuf?.Release(); _lifeBuf = null;
            _velBuf?.Release(); _velBuf = null;
            _angVelBuf?.Release(); _angVelBuf = null;
            _deformedPosBuf?.Release(); _deformedPosBuf = null;
            _deformedNrmBuf?.Release(); _deformedNrmBuf = null;
            _freeOffsetBuf?.Release(); _freeOffsetBuf = null;
            _freeNrmBuf?.Release(); _freeNrmBuf = null;
            _clumpRangeBuf?.Release(); _clumpRangeBuf = null;
            _shatteredIdsBuf?.Release(); _shatteredIdsBuf = null;
            _clumpHitBuf?.Release(); _clumpHitBuf = null;
            _clumpMassBuf?.Release(); _clumpMassBuf = null;
            _clumpHitBackupBuf?.Release(); _clumpHitBackupBuf = null;
            _impactFluidStateBuf?.Release(); _impactFluidStateBuf = null;
            _impactFluidMotionBuf?.Release(); _impactFluidMotionBuf = null;
            _impactFluidGeometryBuf?.Release(); _impactFluidGeometryBuf = null;
            _impactFluidVelocityBuf?.Release(); _impactFluidVelocityBuf = null;
            _supportHitBuf?.Release(); _supportHitBuf = null;
            _cohesionStateBuf?.Release(); _cohesionStateBuf = null;
            _cohesionPositionDeltaBuf?.Release(); _cohesionPositionDeltaBuf = null;
            _cohesionVelocityDeltaBuf?.Release(); _cohesionVelocityDeltaBuf = null;
            _cohesionAngularDeltaBuf?.Release(); _cohesionAngularDeltaBuf = null;
            _cohesionRotationTargetBuf?.Release(); _cohesionRotationTargetBuf = null;
            _clumpCentroidBuf?.Release(); _clumpCentroidBuf = null;
            _delayStates.Release();
            _islandOfBuf?.Release(); _islandOfBuf = null;
            _islandPosStateBuf?.Release(); _islandPosStateBuf = null;
            _islandRotBuf?.Release(); _islandRotBuf = null;
            _islandVelBuf?.Release(); _islandVelBuf = null;
            _islandAngVelBuf?.Release(); _islandAngVelBuf = null;
            _islandDataBuf?.Release(); _islandDataBuf = null;
            _islandRestOffsetBuf?.Release(); _islandRestOffsetBuf = null;
            _islandAccumBuf?.Release(); _islandAccumBuf = null;
            _islandsArmed = false;
            _selfGridHeadBuf?.Release(); _selfGridHeadBuf = null;
            _selfGridNextBuf?.Release(); _selfGridNextBuf = null;
            _selfSnapshotBuf?.Release(); _selfSnapshotBuf = null;
            _selfRadiusBuf?.Release(); _selfRadiusBuf = null;
            _selfRestRangeBuf?.Release(); _selfRestRangeBuf = null;
            _selfRestEntryBuf?.Release(); _selfRestEntryBuf = null;
            _selfRestPairBuf?.Release(); _selfRestPairBuf = null;
            _selfPairActiveBuf?.Release(); _selfPairActiveBuf = null;
            _selfWakeBuf?.Release(); _selfWakeBuf = null;
            _selfStatsBuf?.Release(); _selfStatsBuf = null;
            _selfBucketTotal = _selfRestPairCount = 0;
            _selfMaxRadiusLocal = 0f;
            _selfGpuBytes = 0L;
#if UNITY_EDITOR || UNITY_ENABLE_CHECKS
            _selfStatsRequestActive = false;
            _selfStatsNextFrame = 0;
#endif
            _selfContactPairs = _selfCollidingBodies = _selfSuppressedContacts = _selfWakeRequests = 0;
            _effectorPositionBuf?.Release(); _effectorPositionBuf = null;
            _effectorPositionScratchBuf?.Release(); _effectorPositionScratchBuf = null;
            _effectorRotationBuf?.Release(); _effectorRotationBuf = null;
            _effectorScaleBuf?.Release(); _effectorScaleBuf = null;
            ClearChunkRendererHost();
            if (_chunkMesh != null) { DestroyObj(_chunkMesh); _chunkMesh = null; }
        }

        void DestroyRuntime()
        {
            ReleaseRuntime();
            DestroyChunkRendererHost();
            _forceBuf?.Release(); _forceBuf = null;
            _colliderBuf?.Release(); _colliderBuf = null;
            _debrisReqDummy?.Release(); _debrisReqDummy = null;
            _debrisReqCountDummy?.Release(); _debrisReqCountDummy = null;
            _impactBuf?.Release(); _impactBuf = null;
            _forceFalloffBuf?.Release(); _forceFalloffBuf = null;
            _effectorBuf?.Release(); _effectorBuf = null;
            _effectorFalloffBuf?.Release(); _effectorFalloffBuf = null;
            _effectorFunctionBuf?.Release(); _effectorFunctionBuf = null;
            _effectorAuxBuf?.Release(); _effectorAuxBuf = null;
            _effectorGpuHashValid = false;
            if (_chunkMat != null) { DestroyObj(_chunkMat); _chunkMat = null; }
            if (_pointsMat != null) { DestroyObj(_pointsMat); _pointsMat = null; }
            if (_simCS != null) { DestroyObj(_simCS); _simCS = null; }
        }

        void OnEnable()
        {
            MigrateLegacyDestruction();
            MigrateFractureAuthoring();
            EnsureDebrisEmitterSpawnPercentages();
            BrazeRuntimeRegistry.AddUnique(Active, this);
            _dirty = true;
            _queuedImpactCount = 0;
            _queuedImpactFluid = false;
            _droppedImpactCount = 0;
            _impactSeedSequence = 0;
            _supportPassesRemaining = 0;
            _cohesionTimeRemaining = 0f;
            _impactFluidRemaining = 0f;
            _impactFluidCellFraction = 0f;
            _impactFluidContainer = null;
            _fieldReleaseHashValid = false;
#if UNITY_EDITOR
            _editorEffectorHashValid = false;
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
            ReleaseBuffers();
            DestroyRuntime();
            var r = SourceRenderer; if (r != null) r.enabled = !hideOriginal;   // restore on removal
        }
        void OnValidate()
        {
            MigrateLegacyDestruction();
            MigrateFractureAuthoring();
            EnsureReleaseImpulseRange();
            EnsureDebrisEmitterSpawnPercentages();
            pointCount = Mathf.Max(1, pointCount);
            if (patternDirection.sqrMagnitude <= 1e-8f) patternDirection = Vector3.up;
            patternDirection.Normalize();
            fibreSlenderness = Mathf.Clamp(fibreSlenderness, 1f, 12f);
            secondaryFraction = Mathf.Clamp(secondaryFraction, 0f, 0.8f);
            impactRadius = Mathf.Clamp(impactRadius, 0.02f, 1f);
            radialRings = Mathf.Clamp(radialRings, 1, 16);
            radialRays = Mathf.Clamp(radialRays, 3, 48);
            radialJitter = Mathf.Clamp01(radialJitter);
            radialExtent = Mathf.Clamp(radialExtent, 0.1f, 1f);
            if (fracturePattern == FracturePattern.RadialSheet)
                pointCount = radialRings * radialRays + 1;
            triplanarScale = Mathf.Max(0.01f, triplanarScale);
            movingFragmentLightFloor = Mathf.Clamp(movingFragmentLightFloor, 0f, 0.5f);
            interiorMetallic = Mathf.Clamp01(interiorMetallic);
            interiorSmoothness = Mathf.Clamp01(interiorSmoothness);
            detailAmount = Mathf.Max(0f, detailAmount);
            detailScale = Mathf.Max(0.01f, detailScale);
            detailOctaves = Mathf.Clamp(detailOctaves, 1, 6);
            detailContrast = Mathf.Clamp01(detailContrast);
            edgeDetailWidth = Mathf.Clamp(edgeDetailWidth, 0.25f, 8f);
            edgeDetailStrength = Mathf.Clamp01(edgeDetailStrength);
            edgeDetailNoise = Mathf.Clamp01(edgeDetailNoise);
            edgeDetailScale = Mathf.Max(0.01f, edgeDetailScale);
            if (previewSliceNormal.sqrMagnitude <= 1e-8f) previewSliceNormal = Vector3.right;
            previewSliceNormal.Normalize();
            previewExplode = Mathf.Clamp(previewExplode, 0f, 8f);
            maxImpactsPerFrame = Mathf.Clamp(maxImpactsPerFrame, 1, 4096);
            maxImpactSpeed = Mathf.Max(0f, maxImpactSpeed);
            releaseSpinMin = Mathf.Max(0f, releaseSpinMin);
            releaseSpin = Mathf.Max(0f, releaseSpin);
            thrustFrames.x = Mathf.Max(0, thrustFrames.x);
            thrustFrames.y = Mathf.Max(thrustFrames.x, thrustFrames.y);
            spinDamping = Mathf.Max(0f, spinDamping);
            linearDrag = Mathf.Max(0f, linearDrag);
            if (supportAnchorDirection.sqrMagnitude <= 1e-8f) supportAnchorDirection = Vector3.down;
            supportAnchorThickness = Mathf.Clamp(supportAnchorThickness, 0f, 2f);
            lateralSupport = Mathf.Clamp01(lateralSupport);
            collapseSpeed = Mathf.Clamp(collapseSpeed, 1, 8);
            clusterBreakImpulse = Mathf.Max(0f, clusterBreakImpulse);
            clusterMinCells = Mathf.Clamp(clusterMinCells, 1, 512);
            selfCollisionStrength = Mathf.Clamp01(selfCollisionStrength);
            cohesionChunkSize = Mathf.Clamp(cohesionChunkSize, 1, 64);
            cohesion = Mathf.Clamp01(cohesion);
            cohesionDamping = Mathf.Clamp01(cohesionDamping);
            cohesionBreakStrain = Mathf.Clamp(cohesionBreakStrain, 0.01f, 1f);
            cohesionDuration = Mathf.Max(0.05f, cohesionDuration);
            cohesionTopple = Mathf.Clamp(cohesionTopple, 0f, 3f);
            cohesionDebrisFraction = Mathf.Clamp01(cohesionDebrisFraction);
            cohesionIterations = Mathf.Clamp(cohesionIterations, 1, 4);
            previewWireSegments = Mathf.Clamp(previewWireSegments, 3000, PreviewWireAbsoluteMaxSegments);
            _dirty = true;
#if UNITY_EDITOR
            _editorEffectorHashValid = false;
#endif
        }

        void MigrateLegacyDestruction()
        {
            // Serialized enum value 2 was the old field-driven Connectors mode. Preserve its authored field while
            // moving connectivity to the new inherent Structural Support subsection.
            if ((int)breakMode != 2) return;
            breakMode = BreakMode.Field;
            structuralSupport = true;
        }

        void MigrateFractureAuthoring()
        {
            if (fractureAuthoringVersion >= 1) return;

            if (patternDirection.sqrMagnitude <= 1e-8f) patternDirection = Vector3.up;
            if (previewSliceNormal.sqrMagnitude <= 1e-8f) previewSliceNormal = Vector3.right;

            // Legacy Offset Fragments was described as a seam control but uniformly shrank every surface vertex.
            // Convert it to a safe, render-only explode amount and permanently remove it from geometry generation.
            if (offsetFragments > 0f && previewExplode <= 0f)
            {
                Bounds b = ResolveLocalBounds();
                float estimatedRadius = Mathf.Max(b.size.magnitude / Mathf.Max(2f, 2f * Mathf.Pow(pointCount, 1f / 3f)), 1e-4f);
                previewExplode = Mathf.Clamp(offsetFragments / estimatedRadius, 0f, 3f);
            }
            offsetFragments = 0f;
            showWire = true;
            fractureAuthoringVersion = 1;
        }

        void EnsureReleaseImpulseRange()
        {
            if (releaseImpulseRangeInitialized) return;
            releaseImpulseMin = releaseImpulse;
            releaseImpulseRangeInitialized = true;
        }

#if UNITY_EDITOR
        // ExecuteAlways.Update is event-driven and may not run when a separate effector inspector changes. This
        // lightweight callback hashes the authored stack and refreshes buffers only when something actually changed.
        void EditorEffectorTick()
        {
            if (this == null || Application.isPlaying || !isActiveAndEnabled) return;
            double now = UnityEditor.EditorApplication.timeSinceStartup;
            if (now < _nextEditorEffectorTick) return;
            _nextEditorEffectorTick = now + EditorEffectorInterval;
            int hash = EditorEffectorContentHash();
            if (_editorEffectorHashValid && hash == _editorEffectorHash && !HasAnimatingEffector()) return;
            _editorEffectorHash = hash;
            _editorEffectorHashValid = true;
            Tick();
            UnityEditor.SceneView.RepaintAll();
        }

        bool HasAnimatingEffector()
        {
            if (effectors == null) return false;
            for (int i = 0; i < effectors.Count; i++)
                if (effectors[i] != null && effectors[i].isActiveAndEnabled && effectors[i].Animating) return true;
            return false;
        }

        int EditorEffectorContentHash()
        {
            unchecked
            {
                int h = Space != null ? Space.localToWorldMatrix.GetHashCode() : 0;
                h = h * 31 + (effectors != null ? effectors.Count : 0);
                if (effectors == null) return h;
                for (int i = 0; i < effectors.Count; i++)
                {
                    var effector = effectors[i];
                    h = h * 31 + (effector != null ? effector.BrazeInstanceId() : 0);
                    h = h * 31 + (effector != null ? effector.ContentHash() : 0);
                }
                return h;
            }
        }
#endif

        Renderer SourceRenderer => target != null ? target : (TryGetComponent(out Renderer r) ? r : null);

        Mesh SourceMesh
        {
            get
            {
                var r = SourceRenderer;
                if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
                if (r != null && r.TryGetComponent(out MeshFilter mf)) return mf.sharedMesh;
                return TryGetComponent(out MeshFilter selfMf) ? selfMf.sharedMesh : null;
            }
        }

        void ReleaseBuffers()
        {
            _seedBuffer?.Release(); _seedBuffer = null;
            _neighborBuf?.Release(); _neighborBuf = null;
            _neighborCountBuf?.Release(); _neighborCountBuf = null;
            _supportLevelBuf?.Release(); _supportLevelBuf = null;
            _cohesionGroupBuf?.Release(); _cohesionGroupBuf = null;
            _cohesionGroupCenterBuf?.Release(); _cohesionGroupCenterBuf = null;
            _cohesionGroupReleaseBuf?.Release(); _cohesionGroupReleaseBuf = null;
            _cohesionGroupCount = 0;
            _cohesionGroupConfigHash = 0;
#if UNITY_EDITOR
            _seedsReady = false;
#endif
        }

        bool EnsureCompute()
        {
            if (_cs != null) return true;
            _cs = Resources.Load<ComputeShader>("BrazeVoronoi");
            if (_cs == null) { Debug.LogError("[BrazeVoronoiFracture] Resources/BrazeVoronoi.compute not found."); return false; }
            _kGenSeeds = _cs.FindKernel("GenSeeds");
            _kMarkField = _cs.FindKernel("MarkFieldHits");
            _kMarkUnsupported = _cs.FindKernel("MarkUnsupported");
            _kCollide = _cs.FindKernel("CollideFragments");
            _kSettle = _cs.FindKernel("SettleFragments");
            _kClearEffectors = _cs.FindKernel("ClearEffectorTransforms");
            _kApplySingleEffector = _cs.FindKernel("ApplySingleEffector");
            _kPushApartEffector = _cs.FindKernel("PushApartEffector");
            _kApplyDelayEffector = _cs.FindKernel("ApplyDelayEffector");
            _kApplyDelaySpringEffector = _cs.FindKernel("ApplyDelaySpringEffector");
            _kSeedCohesion = _cs.FindKernel("SeedCohesiveMotion");
            _kSolveCohesion = _cs.FindKernel("SolveCohesion");
            _kApplyCohesion = _cs.FindKernel("ApplyCohesion");
            _kClearClusterRelease = _cs.FindKernel("ClearClusterRelease");
            _kMarkClusterRelease = _cs.FindKernel("MarkClusterRelease");
            _kExpandClusterRelease = _cs.FindKernel("ExpandClusterRelease");
            _kIslandLabelInit = _cs.FindKernel("IslandLabelInit");
            _kIslandLabelProp = _cs.FindKernel("IslandLabelProp");
            _kIslandLabelCompress = _cs.FindKernel("IslandLabelCompress");
            _kIslandAccumClear = _cs.FindKernel("IslandAccumClear");
            _kIslandAccumCount = _cs.FindKernel("IslandAccumCount");
            _kIslandAccumInertia = _cs.FindKernel("IslandAccumInertia");
            _kIslandFinalizeRoots = _cs.FindKernel("IslandFinalizeRoots");
            _kIslandCreatePrepare = _cs.FindKernel("IslandCreateMembersPrepare");
            _kIslandCreateActivate = _cs.FindKernel("IslandCreateMembersActivate");
            _kIslandFrameBegin = _cs.FindKernel("IslandFrameBegin");
            _kIslandApplyContacts = _cs.FindKernel("IslandApplyContacts");
            _kIslandProject = _cs.FindKernel("IslandProject");
            _kSelfGridClear = _cs.FindKernel("SelfGridClear");
            _kSelfGridInsert = _cs.FindKernel("SelfGridInsert");
            _kSelfActivateRestPairs = _cs.FindKernel("SelfActivateRestPairs");
            _kSelfCollide = _cs.FindKernel("SelfCollide");
            return true;
        }

#if UNITY_EDITOR
        // A ComputeShader asset can be hot-reimported without recreating ExecuteAlways components. Kernel integers
        // belong to the imported kernel table, so retaining them across a reimport can dispatch a different kernel.
        internal void InvalidateComputeKernels()
        {
            _cs = null;
            _kGenSeeds = _kMarkField = _kMarkUnsupported = _kCollide = _kSettle = -1;
            _kClearEffectors = _kApplySingleEffector = _kPushApartEffector = -1;
            _kApplyDelayEffector = _kApplyDelaySpringEffector = -1;
            _kSeedCohesion = _kSolveCohesion = _kApplyCohesion = -1;
            _kClearClusterRelease = _kMarkClusterRelease = _kExpandClusterRelease = -1;
            _kIslandLabelInit = _kIslandLabelProp = _kIslandLabelCompress = -1;
            _kIslandAccumClear = _kIslandAccumCount = _kIslandAccumInertia = -1;
            _kIslandFinalizeRoots = _kIslandCreatePrepare = _kIslandCreateActivate = -1;
            _kIslandFrameBegin = _kIslandApplyContacts = _kIslandProject = -1;
            _kSelfGridClear = _kSelfGridInsert = _kSelfActivateRestPairs = _kSelfCollide = -1;
            _editorEffectorHashValid = false;
        }
#endif

        Bounds ResolveLocalBounds()
        {
            Mesh m = SourceMesh;
            return m != null ? m.bounds : new Bounds(Vector3.zero, Vector3.one);
        }

        Transform Space => target != null ? target.transform : transform;

        /// <summary>Editor authoring accessors. They expose the same target space used by the baker without leaking
        /// the runtime renderer/mesh resolution implementation into the custom inspector.</summary>
        public Bounds AuthoringBounds => ResolveLocalBounds();
        public Transform AuthoringSpace => Space;
        public float AuthoringReferenceSize => Mathf.Max(ResolveLocalBounds().size.magnitude, 1e-4f);

        Vector3 EffectivePatternDirection
            => patternDirection.sqrMagnitude > 1e-8f ? patternDirection.normalized : Vector3.up;

        Vector3 EffectiveCellScale
            => fracturePattern == FracturePattern.Fibres
                ? new Vector3(1f, Mathf.Max(1f, fibreSlenderness), 1f)
                : new Vector3(Mathf.Max(Mathf.Abs(scaleCells.x), 1e-4f),
                              Mathf.Max(Mathf.Abs(scaleCells.y), 1e-4f),
                              Mathf.Max(Mathf.Abs(scaleCells.z), 1e-4f));

        Quaternion EffectiveCellOrientation
            => fracturePattern == FracturePattern.Fibres
                ? Quaternion.FromToRotation(Vector3.up, EffectivePatternDirection)
                : Quaternion.identity;

#if UNITY_EDITOR
        public bool HasAuthoringResult => _renderReady && _frags != null && _frags.Length > 0;
        public bool IsAuthoringResultCurrent
            => HasAuthoringResult && !string.IsNullOrEmpty(_renderGeometrySignature) &&
               _renderGeometrySignature == ComputeSignature();
        public bool IsAuthoringDraft => HasAuthoringResult && _renderIsDraft;

        /// <summary>Called by Scene handles after direct field edits (which bypass SerializedObject/OnValidate).</summary>
        public void MarkAuthoringChanged()
        {
            if (patternDirection.sqrMagnitude <= 1e-8f) patternDirection = Vector3.up;
            if (previewSliceNormal.sqrMagnitude <= 1e-8f) previewSliceNormal = Vector3.right;
            _dirty = true;
            _editorEffectorHashValid = false;
            RepaintScene();
        }
#endif

        Vector3Int EvenGridDims(Vector3 size, int count)
        {
            // Cells proportional to bounds extents, product >= count.
            Vector3 s = new(Mathf.Max(size.x, 1e-4f), Mathf.Max(size.y, 1e-4f), Mathf.Max(size.z, 1e-4f));
            float vol = s.x * s.y * s.z;
            float k = Mathf.Pow(count / vol, 1f / 3f);
            int dx = Mathf.Max(1, Mathf.CeilToInt(s.x * k));
            int dy = Mathf.Max(1, Mathf.CeilToInt(s.y * k));
            int dz = Mathf.Max(1, Mathf.CeilToInt(s.z * k));
            while ((long)dx * dy * dz < count) dz++;   // guarantee enough cells
            return new Vector3Int(dx, dy, dz);
        }

        // ---------------- BAKE (explicit one-time fracture) ----------------
        // The CPU clip-and-cap fracturer is slow, so it runs ONCE here — never on OnValidate or per frame. Bake()
        // scatters the seeds, clips every cell into a closed fragment, writes the result (+ a signature of the inputs)
        // to the `bake` .asset, and builds the render state. At runtime LoadFromBake() reconstitutes that asset with
        // no clipper. This is what kept high fragment counts unusable: re-fracturing live froze the editor.

        /// <summary>Run the fracture once and write/refresh the bake asset. Editor-driven (Bake button).</summary>
        public void Bake()
        {
#if UNITY_EDITOR
            // Resolve a typed path before the expensive clipper runs. Invalid/occupied paths fail immediately rather
            // than wasting a potentially multi-minute high-fragment bake and discovering the problem at write time.
            if (!string.IsNullOrWhiteSpace(bakeAssetPath))
            {
                BrazeFractureBake configured = CreateBakeAssetAtPathForEditor(bakeAssetPath);
                if (configured == null) return;
                AssignBakeAssetForEditor(configured);
            }
#endif
            BuildFracture(writeBakeAsset: true);
        }

#if UNITY_EDITOR
        /// <summary>Build the exact fracture into transient editor state without changing the assigned bake asset.</summary>
        public void BuildAuthoringPreview() => BuildFracture(writeBakeAsset: false);
#endif

        void BuildFracture(bool writeBakeAsset)
        {
            if (!EnsureCompute() || _kGenSeeds < 0) return;   // kernel failed to compile — avoid spamming dispatch errors

            if (fracturePattern == FracturePattern.RadialSheet)
                pointCount = Mathf.Clamp(radialRings, 1, 16) * Mathf.Clamp(radialRays, 3, 48) + 1;

            Mesh mesh = SourceMesh;
            if (mesh == null) { Debug.LogWarning("[BrazeVoronoiFracture] No source mesh to bake."); return; }
            InvalidateMeshFingerprintCache();   // the signature saved by this bake must be fresh, never a cached sample
            if (!mesh.isReadable)
            {
                Debug.LogWarning($"[BrazeVoronoiFracture] Mesh '{mesh.name}' is not readable — enable Read/Write in its " +
                                 "model import settings to fracture it.");
                return;
            }
            var mv = mesh.vertices;
            var mt = mesh.triangles;
            if (mv.Length == 0 || mt.Length < 3) { Debug.LogWarning("[BrazeVoronoiFracture] Source mesh is empty."); return; }
            int sourceVertCount = mv.Length, sourceTriCount = mt.Length / 3;
            var mn = mesh.normals;
            if (mn == null || mn.Length != mv.Length) { mesh.RecalculateNormals(); mn = mesh.normals; }
            var muv = mesh.uv;
            var sealedMesh = BrazeFractureClipper.SealOpenBoundaries(mv, mn, muv, mt);
            if (sealedMesh.HasCaps)
            {
                mv = sealedMesh.vertices;
                mn = sealedMesh.normals;
                muv = sealedMesh.uv;
                mt = sealedMesh.triangles;
            }


            var sw = System.Diagnostics.Stopwatch.StartNew();
            string operation = writeBakeAsset ? "Bake" : "Draft";
            Debug.Log($"[BrazeVoronoi] {operation} '{name}' START — {pointCount} fragments requested, mesh '{mesh.name}' " +
                      $"({sourceVertCount} verts, {sourceTriCount} tris).");
            if (sealedMesh.boundaryEdges > 0)
            {
                string sealedMsg = sealedMesh.HasCaps
                    ? $"sealed {sealedMesh.sealedLoops} boundary loop(s), {sealedMesh.capTriangles:n0} virtual cap tris"
                    : "found open boundaries but could not build closure caps";
                Debug.Log($"[BrazeVoronoi] Source mesh has {sealedMesh.boundaryEdges:n0} welded open boundary edge(s); " +
                          $"{sealedMsg}. Non-manifold edges: {sealedMesh.nonManifoldEdges:n0}; open chains skipped: {sealedMesh.openChains:n0}.");
            }

            GenerateSeedsSync();                 // scatter seeds on the GPU, read them straight back (one-time, blocking)
            long msSeeds = sw.ElapsedMilliseconds;
            _clipper ??= new BrazeFractureClipper();
            _clipper.BuildInsideAccel(mv, mt);   // (y,z) grid so the inside-test is local, not O(all triangles)
            RejectSeedsOutside(mv, mt);          // pull stray seeds into the solid so every fragment is meaningful
            long msReject = sw.ElapsedMilliseconds - msSeeds;

            long msClipStart = sw.ElapsedMilliseconds;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.DisplayProgressBar("Braze Voronoi Fracture", $"{operation}: {pointCount} fragments…", 0.5f);
            try
            {
                _frags = _clipper.Build(mv, mn, muv, mt, _seedsCpu, 96, EffectiveCellScale, 0f,
                                        _seedInside, EffectiveCellOrientation);
            }
            finally { UnityEditor.EditorUtility.ClearProgressBar(); }
#else
            _frags = _clipper.Build(mv, mn, muv, mt, _seedsCpu, 96, EffectiveCellScale, 0f,
                                    _seedInside, EffectiveCellOrientation);
#endif
            PrepareTopologyAwareFragments(pointCount);
            long msClip = sw.ElapsedMilliseconds - msClipStart;

            // fracture stats — count geometry and degenerate cells so a "slab" / empty result is visible in the log
            int tris = 0, empties = 0, minV = int.MaxValue, maxV = 0;
            for (int i = 0; i < _frags.Length; i++)
            {
                int vc = _frags[i] != null ? _frags[i].pos.Count : 0;
                tris += vc / 3;
                if (vc < 3) empties++;
                if (vc < minV) minV = vc;
                if (vc > maxV) maxV = vc;
            }
            if (_frags.Length == 0) minV = 0;

            long msRenderStart = sw.ElapsedMilliseconds;
            _dirty = false;
            BuildRenderState();
            long msRender = sw.ElapsedMilliseconds - msRenderStart;

#if UNITY_EDITOR
            long msWriteStart = sw.ElapsedMilliseconds;
            bool bakeWritten = !writeBakeAsset || WriteBakeAsset(); // commit accepted draft geometry to the persistent asset
            long msWrite = sw.ElapsedMilliseconds - msWriteStart;
#else
            bool bakeWritten = !writeBakeAsset;
            long msWrite = 0;
#endif
            if (writeBakeAsset && bakeWritten)
            {
                _loadedBake = bake;
                _loadedSignature = bake != null ? bake.signature : null;
            }
#if UNITY_EDITOR
            _renderGeometrySignature = ComputeSignature();
            _renderIsDraft = !writeBakeAsset || !bakeWritten;
            if (_renderIsDraft) { _loadedBake = null; _loadedSignature = _renderGeometrySignature; }
#endif
            sw.Stop();

            bakeFragments = _frags.Length;
            bakeTris = tris;
            bakeVerts = _runtimeVerts;
            bakeEmptyCells = empties;
            bakeMs = sw.ElapsedMilliseconds;
            string topologyReport = pointCount == bakeFragments
                ? $"{bakeFragments:n0} chunks · {tris:n0} tris · {_runtimeVerts:n0} verts · {bakeMs} ms"
                : $"{pointCount:n0} cells → {bakeFragments:n0} topology chunks · {tris:n0} tris · {_runtimeVerts:n0} verts · {bakeMs} ms";
            bakeReport = topologyReport +
                         (sealedMesh.HasCaps ? $" · sealed {sealedMesh.boundaryEdges:n0} edges" : "") +
                         (_clipper.CapsCulledOutside > 0 || _clipper.CapTrisCulledOutside > 0
                             ? $" · culled {_clipper.CapsCulledOutside:n0}/{_clipper.CapTrisCulledOutside:n0} outside caps"
                             : "") +
                         (empties > 0 ? $"  ⚠ {empties} empty" : "");

            Debug.Log($"[BrazeVoronoi] {operation} '{name}' DONE — {_frags.Length} topology chunks from {pointCount} cells, " +
                      $"{tris:n0} tris, {_runtimeVerts:n0} verts " +
                      $"(per-cell verts min {minV} / max {maxV}), {empties} empty.  " +
                      $"Culled outside caps: {_clipper.CapsCulledOutside:n0} faces / {_clipper.CapTrisCulledOutside:n0} tris.  " +
                      $"Timing: seeds {msSeeds}ms · reject {msReject}ms · clip {msClip}ms · render {msRender}ms · write {msWrite}ms · TOTAL {bakeMs}ms.");
            if (empties > 0)
                Debug.LogWarning($"[BrazeVoronoi] {empties}/{_frags.Length} cells produced NO geometry — those seeds made degenerate/empty cells.");
            if (_runtimeVerts == 0)
                Debug.LogError("[BrazeVoronoi] Bake produced 0 vertices — nothing will render. Check the source mesh is closed & readable.");

            RepaintScene();
        }

#if UNITY_EDITOR
        /// <summary>Clear the assigned bake cache and its binary payload, keeping the asset slot assigned.</summary>
        public void ClearBakeCache()
        {
            if (bake != null)
            {
                string expectedBlobPath = BlobPathForBakeAsset(bake);
                string assignedBlobPath = bake.blob != null ? UnityEditor.AssetDatabase.GetAssetPath(bake.blob) : null;
                if (!string.IsNullOrEmpty(assignedBlobPath) && assignedBlobPath == expectedBlobPath)
                    UnityEditor.AssetDatabase.DeleteAsset(assignedBlobPath);
                else if (!string.IsNullOrEmpty(expectedBlobPath) && UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(expectedBlobPath) != null)
                    UnityEditor.AssetDatabase.DeleteAsset(expectedBlobPath);

                bake.signature = null;
                bake.signatureNoMeshSample = null;
                bake.inputs = default;
                bake.inputsRecorded = false;
                bake.fragmentCount = 0;
                bake.blob = null;
                bake.blobVersion = 0;
                bake.seedCount = 0;
                bake.vertexCount = 0;
                bake.triangleCount = 0;
                bake.neighborLinkCount = 0;
                bake.payloadBytes = 0;
                UnityEditor.EditorUtility.SetDirty(bake);
                UnityEditor.AssetDatabase.SaveAssetIfDirty(bake);
            }

            _frags = null;
            _seedsCpu = null;
            _seedsReady = false;
            _loadedBake = null;
            _loadedSignature = null;
#if UNITY_EDITOR
            _renderGeometrySignature = null;
            _renderIsDraft = false;
#endif
            bakeFragments = bakeTris = bakeVerts = bakeEmptyCells = 0;
            bakeMs = 0;
            bakeReport = null;
            ReleaseRuntime();
            _dirty = false;
            SetSourceVisible(true);
            RepaintScene();
        }
#endif

        // Decide what to show when something changed (enable / source swap / param edit). NEVER re-fractures — it only
        // (re)loads the bake or, when there is no current bake, drops back to the original mesh. Edit mode is strict
        // (a stale signature counts as "no bake" → red); play/builds just use whatever bake is assigned.
        void RefreshFromBake()
        {
            bool hasBake = bake != null && bake.HasData;
            if (Application.isPlaying)
            {
                if (!hasBake)
                {
                    if (_renderReady) ReleaseRuntime();
                    _loadedBake = null; _loadedSignature = null;
                    return;
                }
                if (_renderReady && _loadedBake == bake && _loadedSignature == bake.signature) return;
                if (LoadFromBake()) { _loadedBake = bake; _loadedSignature = bake.signature; }
                else { if (_renderReady) ReleaseRuntime(); _loadedBake = null; _loadedSignature = null; }
                return;
            }

#if UNITY_EDITOR
            // A draft is independent of the committed asset. Keep it while it is current, and also keep stale results
            // visible while the artist edits—the old behavior erased all visual context on the first changed slider.
            if (IsAuthoringResultCurrent) return;

            if (IsBakeValid())
            {
                if (_renderReady && _loadedBake == bake && _loadedSignature == bake.signature) return;
                if (LoadFromBake())
                {
                    _loadedBake = bake;
                    _loadedSignature = bake.signature;
                    _renderGeometrySignature = bake.signature;
                    _renderIsDraft = false;
                }
                return;
            }

            if (_renderReady) return; // deliberately retain the stale draft/bake until a new draft replaces it
            if (hasBake && LoadFromBake())
            {
                _loadedBake = bake;
                _loadedSignature = bake.signature;
                _renderGeometrySignature = bake.signature;
                _renderIsDraft = false;
            }
#endif
        }

        // Generate the exact seed set consumed by both transient Draft and committed Bake. Legacy/volume layouts keep
        // their proven GPU generator; material patterns use deterministic CPU placement before the shared inside-solid
        // repair. Draft and Bake therefore produce byte-identical layouts for the same signature.
        void GenerateSeedsSync()
        {
            _localBounds = ResolveLocalBounds();
            _trackedRenderer = SourceRenderer;
            _trackedMesh = SourceMesh;
            _trackedBounds = _localBounds;
            _sourceStateValid = true;

            int count = Mathf.Max(1, pointCount);
            if (_seedsCpu == null || _seedsCpu.Length != count)
                _seedsCpu = new Vector4[count];

            if (fracturePattern == FracturePattern.Fibres)
            {
                GenerateFibreSeeds(count);
                _allocCount = count;
#if UNITY_EDITOR
                _seedsReady = true;
#endif
                return;
            }
            if (fracturePattern == FracturePattern.MultiScale)
            {
                GenerateMultiScaleSeeds(count);
                _allocCount = count;
#if UNITY_EDITOR
                _seedsReady = true;
#endif
                return;
            }
            if (fracturePattern == FracturePattern.RadialSheet)
            {
                GenerateRadialSheetSeeds(count);
                _allocCount = count;
#if UNITY_EDITOR
                _seedsReady = true;
#endif
                return;
            }

            // Reallocate only the offline seed buffer. Releasing every fracture buffer here used to destroy the stale
            // visual result precisely when a changed fragment count most needed comparison context.
            if (_seedBuffer == null || _allocCount != count || _seedBuffer.count != count)
            {
                _seedBuffer?.Release();
                _seedBuffer = new ComputeBuffer(count, 16, ComputeBufferType.Structured);
                _allocCount = count;
            }

            Vector3 bMin = _localBounds.min, bMax = _localBounds.max;
            Vector3Int dims = EvenGridDims(_localBounds.size, count);

            _cs.SetBuffer(_kGenSeeds, "_Seeds", _seedBuffer);
            _cs.SetInt("_SeedCount", count);
            _cs.SetInt("_Seed", seed);
            _cs.SetVector("_BoundsMin", bMin);
            _cs.SetVector("_BoundsMax", bMax);
            _cs.SetInt("_DistMode", (int)distribution);
            _cs.SetFloat("_Variation", variation);
            _cs.SetInts("_GridDims", dims.x, dims.y, dims.z);
            _cs.SetVector("_ClusterCenter", clusterCenter);
            _cs.SetFloat("_ClusterRadius", clusterRadius);
            _cs.SetFloat("_ClusterStrength", clusterStrength);
            _cs.SetFloat("_ClusterFalloff", clusterFalloff);
            _cs.SetInt("_ShaderSource", shaderSource ? 1 : 0);
            _cs.SetFloat("_NoiseScale", noiseScale);
            _cs.SetInt("_NoiseSeed", noiseSeed);
            _cs.SetFloat("_DensityContrast", densityContrast);
            _cs.SetInt("_DensityTries", densityTries);

            _cs.Dispatch(_kGenSeeds, (count + 63) / 64, 1, 1);
            _seedBuffer.GetData(_seedsCpu);      // blocking readback (bake is a one-time action)
#if UNITY_EDITOR
            _seedsReady = true;
#endif
        }

        void GenerateFibreSeeds(int count)
        {
            Vector3 grain = EffectivePatternDirection;
            Vector3 u = Vector3.Cross(Mathf.Abs(Vector3.Dot(grain, Vector3.up)) < 0.9f
                ? Vector3.up : Vector3.right, grain).normalized;
            Vector3 v = Vector3.Cross(grain, u).normalized;
            Vector3 centre = _localBounds.center;

            Vector3 lo = new(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 hi = new(float.MinValue, float.MinValue, float.MinValue);
            Vector3 bMin = _localBounds.min, bMax = _localBounds.max;
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = new((c & 1) == 0 ? bMin.x : bMax.x,
                                     (c & 2) == 0 ? bMin.y : bMax.y,
                                     (c & 4) == 0 ? bMin.z : bMax.z);
                Vector3 d = corner - centre;
                Vector3 frame = new(Vector3.Dot(d, u), Vector3.Dot(d, grain), Vector3.Dot(d, v));
                lo = Vector3.Min(lo, frame);
                hi = Vector3.Max(hi, frame);
            }

            Vector3 span = hi - lo;
            // Reduce the grain span only while choosing grid divisions. Mapping the grid back through the full span
            // then leaves fewer planes along the grain and many more across it: actual long splinters, not merely a
            // differently weighted distance field over an otherwise isotropic seed lattice.
            Vector3 logicalSpan = new(span.x, span.y / Mathf.Max(1f, fibreSlenderness), span.z);
            Vector3Int dims = EvenGridDims(logicalSpan, count);
            int capacity = Mathf.Max(1, dims.x * dims.y * dims.z);
            float jitter = Mathf.Clamp01(variation);
            for (int i = 0; i < count; i++)
            {
                int flat = Mathf.Min(capacity - 1, Mathf.FloorToInt((i + 0.5f) * capacity / count));
                int gx = flat % dims.x;
                int gy = (flat / dims.x) % dims.y;
                int gz = flat / (dims.x * dims.y);
                uint st = (uint)((i + 1) * 747796405) ^ (uint)(seed * 2891336453L) ^ 0x9e3779b9u;
                Vector3 cell = new(
                    (gx + 0.5f + (Rand01(ref st) - 0.5f) * jitter) / dims.x,
                    (gy + 0.5f + (Rand01(ref st) - 0.5f) * jitter) / dims.y,
                    (gz + 0.5f + (Rand01(ref st) - 0.5f) * jitter) / dims.z);
                Vector3 frame = lo + Vector3.Scale(cell, span);
                Vector3 p = centre + u * frame.x + grain * frame.y + v * frame.z;
                p = Vector3.Max(bMin, Vector3.Min(bMax, p));
                _seedsCpu[i] = new Vector4(p.x, p.y, p.z, 1f);
            }
        }

        void GenerateMultiScaleSeeds(int count)
        {
            int fineCount = Mathf.Clamp(Mathf.RoundToInt(count * secondaryFraction), 0, Mathf.Max(0, count - 1));
            int macroCount = count - fineCount;
            FillEvenSeeds(0, macroCount, _localBounds, variation);

            Vector3 centre = impactPoint;
            float radius = impactRadius * AuthoringReferenceSize;
            for (int i = 0; i < fineCount; i++)
            {
                int id = macroCount + i;
                uint st = (uint)((id + 1) * 747796405) ^ (uint)(seed * 2891336453L) ^ 0x68bc21ebu;
                Vector3 dir = RandomUnitVector(ref st);
                float r = radius * Mathf.Pow(Rand01(ref st), 1f / 3f);
                Vector3 p = centre + dir * r;
                p = Vector3.Max(_localBounds.min, Vector3.Min(_localBounds.max, p));
                _seedsCpu[id] = new Vector4(p.x, p.y, p.z, 1f);
            }
        }

        void FillEvenSeeds(int start, int count, Bounds bounds, float jitter)
        {
            if (count <= 0) return;
            Vector3Int dims = EvenGridDims(bounds.size, count);
            Vector3 lo = bounds.min, size = bounds.size;
            for (int i = 0; i < count; i++)
            {
                uint st = (uint)((i + start + 1) * 747796405) ^ (uint)(seed * 2891336453L);
                int gx = i % dims.x;
                int gy = (i / dims.x) % dims.y;
                int gz = (i / (dims.x * dims.y)) % dims.z;
                Vector3 cell = new(
                    (gx + 0.5f + (Rand01(ref st) - 0.5f) * jitter) / dims.x,
                    (gy + 0.5f + (Rand01(ref st) - 0.5f) * jitter) / dims.y,
                    (gz + 0.5f + (Rand01(ref st) - 0.5f) * jitter) / dims.z);
                Vector3 p = lo + Vector3.Scale(cell, size);
                _seedsCpu[start + i] = new Vector4(p.x, p.y, p.z, 1f);
            }
        }

        void GenerateRadialSheetSeeds(int count)
        {
            Vector3 n = EffectivePatternDirection;
            Vector3 u = Vector3.Cross(Mathf.Abs(Vector3.Dot(n, Vector3.up)) < 0.9f ? Vector3.up : Vector3.right, n).normalized;
            Vector3 v = Vector3.Cross(n, u).normalized;

            // Preserve the artist's in-plane impact position but move the generator plane to the middle of the solid.
            Vector3 origin = impactPoint + n * Vector3.Dot(_localBounds.center - impactPoint, n);
            float uExtent = 0f, vExtent = 0f;
            Vector3 min = _localBounds.min, max = _localBounds.max;
            for (int c = 0; c < 8; c++)
            {
                Vector3 corner = new((c & 1) == 0 ? min.x : max.x,
                                     (c & 2) == 0 ? min.y : max.y,
                                     (c & 4) == 0 ? min.z : max.z);
                Vector3 d = corner - origin;
                uExtent = Mathf.Max(uExtent, Mathf.Abs(Vector3.Dot(d, u)));
                vExtent = Mathf.Max(vExtent, Mathf.Abs(Vector3.Dot(d, v)));
            }
            float maxRadius = Mathf.Max(1e-4f, Mathf.Min(uExtent, vExtent) * radialExtent);

            _seedsCpu[0] = new Vector4(origin.x, origin.y, origin.z, 1f);
            int rings = Mathf.Max(1, radialRings), rays = Mathf.Max(3, radialRays);
            int capacity = rings * rays;
            for (int i = 1; i < count; i++)
            {
                int polar = (i - 1) % capacity;
                int layer = (i - 1) / capacity;
                int ring = polar / rays + 1;
                int ray = polar % rays;
                uint st = (uint)(i * 747796405) ^ (uint)(seed * 2891336453L) ^ 0xa511e9b3u;

                float ringStep = maxRadius / rings;
                float radius = ringStep * ring;
                radius += (Rand01(ref st) * 2f - 1f) * radialJitter * ringStep * 0.38f;
                float angle = ray * (Mathf.PI * 2f / rays) + (ring & 1) * (Mathf.PI / rays) * 0.35f;
                angle += (Rand01(ref st) * 2f - 1f) * radialJitter * (Mathf.PI / rays) * 0.65f;
                if (layer > 0) angle += layer * 2.39996323f; // deterministic golden-angle overflow, avoids duplicates

                Vector3 p = origin + (u * Mathf.Cos(angle) + v * Mathf.Sin(angle)) * Mathf.Max(0f, radius);
                _seedsCpu[i] = new Vector4(p.x, p.y, p.z, 1f);
            }
        }

        static Vector3 RandomUnitVector(ref uint st)
        {
            float z = Rand01(ref st) * 2f - 1f;
            float a = Rand01(ref st) * Mathf.PI * 2f;
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
            return new Vector3(r * Mathf.Cos(a), r * Mathf.Sin(a), z);
        }

        // Reconstitute the baked fragments (NO clipper) and build the render state. Returns false if there is no usable
        // bake. Cheap — this is what runs on enable / source change / play, instead of re-fracturing.
        bool LoadFromBake()
        {
            if (bake == null || !bake.HasData) return false;
            if (!EnsureCompute()) return false;

            _localBounds = ResolveLocalBounds();
            _trackedRenderer = SourceRenderer;
            _trackedMesh = SourceMesh;
            _trackedBounds = _localBounds;
            _sourceStateValid = true;

            if (!LoadBakeBlob()) return false;

#if UNITY_EDITOR
            _seedsReady = true;
#endif
            BuildRenderState();
#if UNITY_EDITOR
            _renderGeometrySignature = bake.signature;
            _renderIsDraft = false;
#endif
            return true;
        }

        bool LoadBakeBlob()
        {
            try
            {
                byte[] bytes = bake.blob.bytes;
                using var ms = new System.IO.MemoryStream(bytes, false);
                using var r = new System.IO.BinaryReader(ms);

                int magic = r.ReadInt32();
                int version = r.ReadInt32();
                if (magic != BakeBlobMagic)
                    throw new InvalidDataException($"bad magic 0x{magic:x8}");
                if (version != BrazeFractureBake.CurrentBlobVersion)
                    throw new InvalidDataException($"unsupported version {version}");

                int n = r.ReadInt32();
                int seedN = r.ReadInt32();
                int vertexN = r.ReadInt32();
                int triN = r.ReadInt32();
                int nbN = r.ReadInt32();
                if (n <= 0 || seedN <= 0 || vertexN <= 0)
                    throw new InvalidDataException("empty blob header");

                _seedsCpu = new Vector4[seedN];
                for (int i = 0; i < seedN; i++) _seedsCpu[i] = ReadVector4(r);
                _allocCount = -1;                    // force a fresh seed buffer if a later Bake runs

                _frags = new BrazeFractureClipper.Frag[n];
                int readVerts = 0, readNbrs = 0;
                for (int c = 0; c < n; c++)
                {
                    var f = new BrazeFractureClipper.Frag();
                    int vc = r.ReadInt32();
                    int nc = r.ReadInt32();
                    Vector4 cr = ReadVector4(r);
                    f.centroid = new Vector3(cr.x, cr.y, cr.z);
                    f.radius = cr.w;

                    for (int i = 0; i < vc; i++)
                    {
                        f.pos.Add(ReadVector3(r));
                        f.nrm.Add(ReadVector3(r));
                        f.uv.Add(ReadVector2(r));
                        f.skin.Add(r.ReadSingle());
                    }
                    for (int i = 0; i < nc; i++) f.neighbors.Add(r.ReadInt32());

                    readVerts += vc;
                    readNbrs += nc;
                    _frags[c] = f;
                }

                if (readVerts != vertexN || readVerts / 3 != triN || readNbrs != nbN)
                    throw new InvalidDataException("blob count mismatch");
                // Version 6+ bakes already persist split chunks and the tight-contact graph. Older blobs are repaired
                // once in memory so existing projects behave correctly before the artist gets a chance to re-bake.
                bool hasCurrentTopology = bake.inputsRecorded && bake.inputs.geometryVersion == GeometryBakeVersion;
                if (!hasCurrentTopology) PrepareTopologyAwareFragments(seedN);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[BrazeVoronoiFracture] Failed to load bake blob '{bake.name}': {e.Message}", bake);
                return false;
            }
        }

        static Vector2 ReadVector2(System.IO.BinaryReader r) => new(r.ReadSingle(), r.ReadSingle());
        static Vector3 ReadVector3(System.IO.BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        static Vector4 ReadVector4(System.IO.BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        sealed class InvalidDataException : Exception
        {
            public InvalidDataException(string message) : base(message) { }
        }

        // ---- bake-state signature: every input that changes the GEOMETRY (not the look/physics, which stay live) ----

        /// <summary>True when the bake asset exists and was produced by the current fracture inputs. Strict signature
        /// compare is an authoring-time concern only: in play mode this just requires a loadable bake (mirrors
        /// RefreshFromBake) because Mesh.isReadable legitimately flips between edit and play mode for meshes imported
        /// without Read/Write, which made the strict compare report false "Bake required" states.</summary>
        public bool IsBakeValid()
        {
            if (bake == null || !bake.HasData) return false;
            if (Application.isPlaying) return true;

            var m = SourceMesh;
            if (m != null && m.isReadable) return ComputeSignature() == bake.signature;

            // Mesh can't be vertex-sampled right now (Read/Write off, or no mesh yet): compare the sample-free
            // signature both sides can agree on. Legacy assets predate that field — keep their old behavior.
            return !string.IsNullOrEmpty(bake.signatureNoMeshSample)
                ? ComputeSignature(sampleMeshVertices: false) == bake.signatureNoMeshSample
                : ComputeSignature() == bake.signature;
        }

        public string ComputeSignature() => ComputeSignature(sampleMeshVertices: true);

        /// <summary>Deterministic hash of every fracture-affecting input. Render/detail/destruction/force/age knobs are
        /// deliberately excluded — they apply live without re-fracturing, so they must NOT invalidate the bake. Inputs
        /// that are inactive (cluster shape outside Clustered distribution, noise fields with Shader Source off) are
        /// excluded too: they don't change the result, so editing them must not flip the bake red.</summary>
        public string ComputeSignature(bool sampleMeshVertices)
        {
            ulong h = 14695981039346656037UL;   // FNV-1a 64
            void Mix(int x) { unchecked { h = (h ^ (uint)x) * 1099511628211UL; } }
            void Mix64(ulong x) { unchecked { h = (h ^ x) * 1099511628211UL; } }
            void MixF(float f) => Mix(BitConverter.SingleToInt32Bits(f));

            Mix(GeometryBakeVersion);
            Mix64(MeshFingerprint(SourceMesh, sampleMeshVertices));   // cached: only re-reads vertices when the mesh changes

            // Fracture-style is intentionally omitted: it is a label for copied values, not hidden behavior.
            Mix(pointCount); Mix(seed); Mix((int)fracturePattern);
            if (fracturePattern == FracturePattern.Volume)
            {
                Mix((int)distribution); MixF(variation);
                if (distribution == Distribution.Clustered)
                {
                    MixF(clusterCenter.x); MixF(clusterCenter.y); MixF(clusterCenter.z);
                    MixF(clusterRadius); MixF(clusterStrength); MixF(clusterFalloff);
                }
                if (shaderSource)
                {
                    Mix(1); MixF(noiseScale); Mix(noiseSeed); MixF(densityContrast); Mix(densityTries);
                }
            }
            else if (fracturePattern == FracturePattern.Fibres)
            {
                MixF(variation);
                MixF(EffectivePatternDirection.x); MixF(EffectivePatternDirection.y); MixF(EffectivePatternDirection.z);
                MixF(fibreSlenderness);
            }
            else if (fracturePattern == FracturePattern.MultiScale)
            {
                MixF(variation); MixF(secondaryFraction);
                MixF(impactPoint.x); MixF(impactPoint.y); MixF(impactPoint.z); MixF(impactRadius);
            }
            else if (fracturePattern == FracturePattern.RadialSheet)
            {
                MixF(EffectivePatternDirection.x); MixF(EffectivePatternDirection.y); MixF(EffectivePatternDirection.z);
                MixF(impactPoint.x); MixF(impactPoint.y); MixF(impactPoint.z);
                Mix(radialRings); Mix(radialRays); MixF(radialJitter); MixF(radialExtent);
            }
            Vector3 cellScale = EffectiveCellScale;
            MixF(cellScale.x); MixF(cellScale.y); MixF(cellScale.z);
            return h.ToString("x16");
        }

        // Vertex-sample cache so IsBakeValid() (per inspector repaint) doesn't re-read m.vertices. The header
        // (counts + bounds) is cheap and recomputed every call and is PART of the key, so an in-place reimport that
        // changes the mesh invalidates the cached samples instead of serving stale ones into a saved signature.
        int _meshFpId; ulong _meshFpHeader; ulong _meshFpSampled;

        void InvalidateMeshFingerprintCache() { _meshFpId = 0; _meshFpHeader = 0; _meshFpSampled = 0; }

        // Counts + bounds, plus a strided vertex sample (≤256 verts) when requested and readable.
        ulong MeshFingerprint(Mesh m, bool sampleVertices)
        {
            if (m == null) return 0;

            ulong h = 14695981039346656037UL;
            void Mix(int x) { unchecked { h = (h ^ (uint)x) * 1099511628211UL; } }
            void MixF(float f) => Mix(BitConverter.SingleToInt32Bits(f));

            Mix(m.vertexCount); Mix(m.subMeshCount);
            var b = m.bounds;
            MixF(b.center.x); MixF(b.center.y); MixF(b.center.z);
            MixF(b.size.x); MixF(b.size.y); MixF(b.size.z);
            if (h == 0) h = 1;
            if (!sampleVertices || !m.isReadable) return h;

            int id = m.BrazeInstanceId();
            if (id == _meshFpId && h == _meshFpHeader && _meshFpSampled != 0) return _meshFpSampled;
            ulong header = h;

            var vts = m.vertices;
            int stride = Mathf.Max(1, vts.Length / 256);
            for (int i = 0; i < vts.Length; i += stride) { MixF(vts[i].x); MixF(vts[i].y); MixF(vts[i].z); }

            _meshFpId = id; _meshFpHeader = header; _meshFpSampled = h == 0 ? 1UL : h;
            return _meshFpSampled;
        }

        // Make every seed land INSIDE the solid so no fragment is a floating exterior chunk (the "spikes"). The cat
        // fills only a fraction of its bounding box, so most box-scattered seeds start outside. We (1) classify with
        // the accelerated inside-test, (2) re-roll each outside seed a few times in the box (preserves the uniform/
        // even distribution where it can land inside), then (3) fall back to jittering off a known-inside seed so it
        // ALWAYS ends up inside. `_seedInside` records the final state; the clipper skips any that are still outside
        // (only happens if the mesh is non-watertight enough that nothing tests inside).
        void RejectSeedsOutside(Vector3[] mv, int[] mt)
        {
            int n = _seedsCpu.Length;
            if (_seedInside == null || _seedInside.Length != n) _seedInside = new bool[n];

            var insideIdx = new System.Collections.Generic.List<int>(n);
            for (int i = 0; i < n; i++)
            {
                bool ins = _clipper.InsideAccel(_seedsCpu[i]);
                _seedInside[i] = ins;
                if (ins) insideIdx.Add(i);
            }
            if (insideIdx.Count == 0) return;   // nothing tests inside (broken mesh) — leave seeds, clipper skips them

            Vector3 lo = _localBounds.min, sz = _localBounds.size;
            float jitter = 0.35f * Mathf.Pow(Mathf.Max(sz.x * sz.y * sz.z, 1e-9f) / Mathf.Max(insideIdx.Count, 1), 1f / 3f);

            for (int i = 0; i < n; i++)
            {
                if (_seedInside[i]) continue;
                uint st = (uint)((i * 73856093) ^ (seed * 19349663)) ^ 0x9e3779b9u;
                Vector3 placed = default; bool ok = false;

                if (fracturePattern == FracturePattern.RadialSheet)
                {
                    // First contract toward the bounds centre while remaining in the authored sheet plane. This keeps
                    // polar rings/rays intact on rounded panes instead of replacing their exterior points randomly.
                    Vector3 original = _seedsCpu[i];
                    for (int t = 1; t <= 12 && !ok; t++)
                    {
                        Vector3 cand = Vector3.LerpUnclamped(original, _localBounds.center, t / 12f);
                        if (_clipper.InsideAccel(cand)) { placed = cand; ok = true; }
                    }

                    // A surface-authored impact can leave the midpoint numerically just outside a very thin pane.
                    // Search through its thickness before falling back to the generic volume repair.
                    Vector3 normal = EffectivePatternDirection;
                    float halfSpan = AuthoringReferenceSize * 0.5f;
                    for (int t = 0; t <= 16 && !ok; t++)
                    {
                        float d = Mathf.Lerp(-halfSpan, halfSpan, t / 16f);
                        Vector3 cand = original + normal * d;
                        if (_clipper.InsideAccel(cand)) { placed = cand; ok = true; }
                    }
                }

                for (int t = 0; t < 16 && !ok; t++)   // re-roll in the box (keeps the chosen distribution when it works)
                {
                    Vector3 cand = lo + new Vector3(Rand01(ref st) * sz.x, Rand01(ref st) * sz.y, Rand01(ref st) * sz.z);
                    if (_clipper.InsideAccel(cand)) { placed = cand; ok = true; }
                }
                if (!ok)                              // guaranteed-inside fallback: jitter off a random inside seed
                {
                    Vector3 host = _seedsCpu[insideIdx[(int)(Rand01(ref st) * insideIdx.Count) % insideIdx.Count]];
                    placed = host;
                    for (int t = 0; t < 8; t++)
                    {
                        Vector3 cand = host + new Vector3((Rand01(ref st) * 2 - 1) * jitter, (Rand01(ref st) * 2 - 1) * jitter, (Rand01(ref st) * 2 - 1) * jitter);
                        if (_clipper.InsideAccel(cand)) { placed = cand; break; }
                    }
                    ok = true;   // `host` itself is inside, so placement is guaranteed
                }
                _seedsCpu[i] = new Vector4(placed.x, placed.y, placed.z, _seedsCpu[i].w);
                _seedInside[i] = ok;
            }
        }

        static float Rand01(ref uint s) { s = s * 747796405u + 2891336453u; uint w = ((s >> ((int)(s >> 28) + 4)) ^ s) * 277803737u; return ((w >> 22) ^ w) / 4294967296f; }

        void RepaintScene()
        {
#if UNITY_EDITOR
            UnityEditor.SceneView.RepaintAll();
#endif
        }

#if UNITY_EDITOR
        // Write the freshly-clipped _frags (+ seeds + input signature) to a binary .bytes payload referenced by the
        // bake .asset. The .asset stays metadata-only so Unity's inspector/serializer never walks millions of values.
        bool WriteBakeAsset()
        {
            if (_frags == null) return false;
            if (!string.IsNullOrWhiteSpace(bakeAssetPath))
            {
                BrazeFractureBake configured = CreateBakeAssetAtPathForEditor(bakeAssetPath);
                if (configured == null) return false;
                AssignBakeAssetForEditor(configured);
            }
            else if (bake == null)
            {
                BrazeFractureBake created = CreateBakeAssetFile();
                if (created == null) return false;   // no valid asset path could be resolved
                AssignBakeAssetForEditor(created);
            }

            int n = _frags.Length;
            int totalV = 0, totalNb = 0;
            for (int c = 0; c < n; c++) { totalV += _frags[c].pos.Count; totalNb += _frags[c].neighbors.Count; }

            string blobPath = BlobPathForBakeAsset(bake);
            if (string.IsNullOrEmpty(blobPath))
            {
                Debug.LogError("[BrazeVoronoiFracture] Could not resolve a .bytes path for the fracture bake payload.", this);
                return false;
            }

            long payloadBytes = WriteBakeBlobFile(blobPath, n, totalV, totalNb);
            UnityEditor.AssetDatabase.ImportAsset(blobPath, UnityEditor.ImportAssetOptions.ForceUpdate);
            TextAsset blob = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(blobPath);
            if (blob == null)
            {
                Debug.LogError($"[BrazeVoronoiFracture] Wrote bake payload but Unity did not import it: {blobPath}", this);
                return false;
            }

            bake.signature = ComputeSignature();
            bake.signatureNoMeshSample = ComputeSignature(sampleMeshVertices: false);
            bake.inputs = CaptureBakedInputs();
            bake.inputsRecorded = true;
            bake.fragmentCount = n;
            bake.blob = blob;
            bake.blobVersion = BrazeFractureBake.CurrentBlobVersion;
            bake.seedCount = _seedsCpu != null ? _seedsCpu.Length : 0;
            bake.vertexCount = totalV;
            bake.triangleCount = totalV / 3;
            bake.neighborLinkCount = totalNb;
            bake.payloadBytes = payloadBytes;

            UnityEditor.EditorUtility.SetDirty(bake);
            UnityEditor.AssetDatabase.SaveAssetIfDirty(bake);
            return true;
        }

        // Creating the ScriptableObject is only half of a bake commit. The component reference is serialized in a
        // scene or prefab, not in the bake asset itself. A direct assignment survives in memory but is not registered
        // as a scene change/prefab override, so it can disappear on a later play-mode or domain reload and the next
        // Bake creates an orphaned "...FractureBake 1.bytes" payload. Persist the owner-side link explicitly.
        internal void AssignBakeAssetForEditor(BrazeFractureBake asset)
        {
            if (asset == null || bake == asset) return;

            UnityEditor.Undo.RecordObject(this, "Assign Fracture Bake");
            bake = asset;
            bakeAssetPath = UnityEditor.AssetDatabase.GetAssetPath(asset);
            _dirty = true;
            UnityEditor.EditorUtility.SetDirty(this);

            if (UnityEditor.PrefabUtility.IsPartOfPrefabInstance(this))
                UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(this);

            UnityEngine.SceneManagement.Scene scene = gameObject.scene;
            if (scene.IsValid() && scene.isLoaded)
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            RepaintScene();
        }

        /// <summary>Resolve a typed/pasted project path, create any missing folders, and create or reuse the bake
        /// asset at that exact location. Absolute paths and file:// URLs are accepted only when they resolve under
        /// this project's Assets directory.</summary>
        internal BrazeFractureBake CreateBakeAssetAtPathForEditor(string rawPath)
        {
            string path = NormalizeBakeAssetPath(rawPath, gameObject.name + "_FractureBake");
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError("[BrazeVoronoiFracture] Bake Path must resolve to a .asset file inside this project's Assets folder.", this);
                return null;
            }

            string folder = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!EnsureAssetFolderExists(folder))
            {
                Debug.LogError($"[BrazeVoronoiFracture] Could not create the bake folder '{folder}'.", this);
                return null;
            }

            UnityEngine.Object existing = UnityEditor.AssetDatabase.LoadMainAssetAtPath(path);
            if (existing != null)
            {
                if (existing is BrazeFractureBake existingBake) return existingBake;
                Debug.LogError($"[BrazeVoronoiFracture] Bake Path is already occupied by '{existing.name}', which is not a Braze Fracture Bake: {path}", this);
                return null;
            }

            var asset = ScriptableObject.CreateInstance<BrazeFractureBake>();
            UnityEditor.AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        static string NormalizeBakeAssetPath(string rawPath, string defaultName)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return null;
            string path = rawPath.Trim().Trim('"').Replace('\\', '/');

            if (System.Uri.TryCreate(path, System.UriKind.Absolute, out System.Uri uri) && uri.IsFile)
                path = uri.LocalPath.Replace('\\', '/');

            try
            {
                string projectRoot = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(Application.dataPath, "..")).Replace('\\', '/').TrimEnd('/');
                string assetsRoot = projectRoot + "/Assets";

                if (System.IO.Path.IsPathRooted(path))
                {
                    string absolute = System.IO.Path.GetFullPath(path).Replace('\\', '/');
                    if (!absolute.Equals(assetsRoot, System.StringComparison.OrdinalIgnoreCase) &&
                        !absolute.StartsWith(assetsRoot + "/", System.StringComparison.OrdinalIgnoreCase)) return null;
                    path = "Assets" + absolute.Substring(assetsRoot.Length);
                }
                else
                {
                    while (path.StartsWith("./")) path = path.Substring(2);
                    if (!path.Equals("Assets", System.StringComparison.OrdinalIgnoreCase) &&
                        !path.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase)) return null;
                    path = "Assets" + path.Substring(6);
                }

                // A pasted folder path is useful too: append the conventional bake filename. A filename without an
                // extension is treated as an asset filename, so "Assets/Bakes/Hero" becomes "Hero.asset".
                if (path.EndsWith("/") || UnityEditor.AssetDatabase.IsValidFolder(path))
                    path = path.TrimEnd('/') + "/" + defaultName + ".asset";
                else if (string.IsNullOrEmpty(System.IO.Path.GetExtension(path)))
                    path += ".asset";

                if (!string.Equals(System.IO.Path.GetExtension(path), ".asset",
                    System.StringComparison.OrdinalIgnoreCase)) return null;

                string absoluteAsset = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(projectRoot, path)).Replace('\\', '/');
                if (!absoluteAsset.StartsWith(assetsRoot + "/", System.StringComparison.OrdinalIgnoreCase)) return null;
                return "Assets/" + absoluteAsset.Substring(assetsRoot.Length + 1);
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        static bool EnsureAssetFolderExists(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !folder.StartsWith("Assets")) return false;
            if (UnityEditor.AssetDatabase.IsValidFolder(folder)) return true;

            string parent = System.IO.Path.GetDirectoryName(folder)?.Replace('\\', '/');
            string name = System.IO.Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name) || !EnsureAssetFolderExists(parent))
                return false;
            UnityEditor.AssetDatabase.CreateFolder(parent, name);
            return UnityEditor.AssetDatabase.IsValidFolder(folder);
        }

        BrazeFractureBake.BakedInputs CaptureBakedInputs()
        {
            Mesh m = SourceMesh;
            return new BrazeFractureBake.BakedInputs
            {
                geometryVersion = GeometryBakeVersion,
                meshFingerprint = MeshFingerprint(m, sampleVertices: true).ToString("x16"),
                meshFingerprintNoSample = MeshFingerprint(m, sampleVertices: false).ToString("x16"),
                pointCount = pointCount,
                seed = seed,
                fracturePattern = (int)fracturePattern,
                patternDirection = EffectivePatternDirection,
                fibreSlenderness = fibreSlenderness,
                secondaryFraction = secondaryFraction,
                impactPoint = impactPoint,
                impactRadius = impactRadius,
                radialRings = radialRings,
                radialRays = radialRays,
                radialJitter = radialJitter,
                radialExtent = radialExtent,
                distribution = (int)distribution,
                variation = variation,
                clusterCenter = clusterCenter,
                clusterRadius = clusterRadius,
                clusterStrength = clusterStrength,
                clusterFalloff = clusterFalloff,
                shaderSource = shaderSource,
                noiseScale = noiseScale,
                noiseSeed = noiseSeed,
                densityContrast = densityContrast,
                densityTries = densityTries,
                neighborK = 96,
                scaleCells = EffectiveCellScale,
                offsetFragments = 0f,
            };
        }

        static bool BitsEq(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
        static bool BitsEq(Vector3 a, Vector3 b) => BitsEq(a.x, b.x) && BitsEq(a.y, b.y) && BitsEq(a.z, b.z);

        /// <summary>Editor-only diagnostics: names the fracture inputs that changed since the last bake, so the
        /// inspector can say WHY the light is red instead of a generic "an input changed". Returns null when the bake
        /// is valid or there is no bake data to compare against.</summary>
        public string DescribeBakeMismatch()
        {
            if (bake == null || !bake.HasData || IsBakeValid()) return null;
            if (!bake.inputsRecorded)
                return "The bake asset is still linked and usable, but it predates input tracking in this BrazeFX " +
                       "version. Re-Bake once to persist the current tracking and topology data.";

            var i = bake.inputs;
            var sb = new System.Text.StringBuilder();
            void Add(string s) { sb.Append(sb.Length == 0 ? "Changed since bake:  " : "  ·  ").Append(s); }

            if (i.geometryVersion != GeometryBakeVersion) Add("fracture algorithm version");
            if (i.pointCount != pointCount) Add($"Fragments {i.pointCount} → {pointCount}");
            if (i.seed != seed) Add($"Seed {i.seed} → {seed}");
            if (i.fracturePattern != (int)fracturePattern) Add($"Pattern {(FracturePattern)i.fracturePattern} → {fracturePattern}");
            if (fracturePattern == FracturePattern.Volume && i.distribution != (int)distribution)
                Add($"Distribution {(Distribution)i.distribution} → {distribution}");
            if ((fracturePattern == FracturePattern.Volume || fracturePattern == FracturePattern.MultiScale ||
                 fracturePattern == FracturePattern.Fibres) && !BitsEq(i.variation, variation))
                Add($"Variation {i.variation:0.###} → {variation:0.###}");
            if (fracturePattern == FracturePattern.Volume && distribution == Distribution.Clustered &&
                i.distribution == (int)Distribution.Clustered &&
                (!BitsEq(i.clusterCenter, clusterCenter) || !BitsEq(i.clusterRadius, clusterRadius) ||
                 !BitsEq(i.clusterStrength, clusterStrength) || !BitsEq(i.clusterFalloff, clusterFalloff)))
                Add("Cluster placement");
            if (fracturePattern == FracturePattern.Volume && i.shaderSource != shaderSource)
                Add(shaderSource ? "Shader Source enabled" : "Shader Source disabled");
            else if (fracturePattern == FracturePattern.Volume && shaderSource &&
                     (!BitsEq(i.noiseScale, noiseScale) || i.noiseSeed != noiseSeed ||
                     !BitsEq(i.densityContrast, densityContrast) || i.densityTries != densityTries))
                Add("Shader density noise");
            if (fracturePattern == FracturePattern.Fibres &&
                (!BitsEq(i.patternDirection, EffectivePatternDirection) || !BitsEq(i.fibreSlenderness, fibreSlenderness)))
                Add("Fibre direction/slenderness");
            if (fracturePattern == FracturePattern.MultiScale &&
                (!BitsEq(i.secondaryFraction, secondaryFraction) || !BitsEq(i.impactPoint, impactPoint) ||
                 !BitsEq(i.impactRadius, impactRadius))) Add("Multi-scale impact pattern");
            if (fracturePattern == FracturePattern.RadialSheet &&
                (!BitsEq(i.patternDirection, EffectivePatternDirection) || !BitsEq(i.impactPoint, impactPoint) ||
                 i.radialRings != radialRings || i.radialRays != radialRays ||
                 !BitsEq(i.radialJitter, radialJitter) || !BitsEq(i.radialExtent, radialExtent)))
                Add("Radial sheet pattern");
            if (!BitsEq(i.scaleCells, EffectiveCellScale)) Add("Cell anisotropy");

            Mesh m = SourceMesh;
            if (m == null) Add("source mesh is missing");
            else if (m.isReadable)
            { if (MeshFingerprint(m, sampleVertices: true).ToString("x16") != i.meshFingerprint) Add("source mesh geometry"); }
            else
            { if (MeshFingerprint(m, sampleVertices: false).ToString("x16") != i.meshFingerprintNoSample) Add("source mesh geometry"); }

            if (sb.Length == 0)
                return "Bake signature drifted with no detectable input change — press Re-Bake once. If this recurs, " +
                       "it is a BrazeFX bug worth reporting.";
            return sb.ToString();
        }

        long WriteBakeBlobFile(string path, int fragmentN, int totalV, int totalNb)
        {
            using var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None);
            using var w = new System.IO.BinaryWriter(fs);

            w.Write(BakeBlobMagic);
            w.Write(BrazeFractureBake.CurrentBlobVersion);
            w.Write(fragmentN);
            w.Write(_seedsCpu != null ? _seedsCpu.Length : 0);
            w.Write(totalV);
            w.Write(totalV / 3);
            w.Write(totalNb);

            if (_seedsCpu != null)
                for (int i = 0; i < _seedsCpu.Length; i++) WriteVector4(w, _seedsCpu[i]);

            for (int c = 0; c < fragmentN; c++)
            {
                var f = _frags[c];
                int vc = f != null ? f.pos.Count : 0;
                int nc = f != null ? f.neighbors.Count : 0;
                w.Write(vc);
                w.Write(nc);
                WriteVector4(w, f != null ? new Vector4(f.centroid.x, f.centroid.y, f.centroid.z, f.radius) : Vector4.zero);

                for (int i = 0; i < vc; i++)
                {
                    WriteVector3(w, f.pos[i]);
                    WriteVector3(w, f.nrm[i]);
                    WriteVector2(w, f.uv[i]);
                    w.Write(f.skin[i]);
                }
                for (int i = 0; i < nc; i++) w.Write(f.neighbors[i]);
            }

            w.Flush();
            return fs.Length;
        }

        static string BlobPathForBakeAsset(BrazeFractureBake asset)
        {
            string assetPath = UnityEditor.AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(assetPath) || !assetPath.Replace('\\', '/').StartsWith("Assets/")) return null;
            return System.IO.Path.ChangeExtension(assetPath, ".bytes")?.Replace('\\', '/');
        }

        static void WriteVector2(System.IO.BinaryWriter w, Vector2 v) { w.Write(v.x); w.Write(v.y); }
        static void WriteVector3(System.IO.BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static void WriteVector4(System.IO.BinaryWriter w, Vector4 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); w.Write(v.w); }

        BrazeFractureBake CreateBakeAssetFile()
        {
            var asset = ScriptableObject.CreateInstance<BrazeFractureBake>();

            // prefer a folder next to the source mesh; fall back to the scene's folder, then Assets/. The folder MUST
            // live under Assets/ — built-in primitives (Cube/Sphere) report a "Library/..." path that can't host assets.
            string dir = "Assets";
            Mesh m = SourceMesh;
            string meshPath = m != null ? UnityEditor.AssetDatabase.GetAssetPath(m) : null;
            string meshDir = !string.IsNullOrEmpty(meshPath) ? System.IO.Path.GetDirectoryName(meshPath) : null;
            string sceneDir = !string.IsNullOrEmpty(gameObject.scene.path)
                ? System.IO.Path.GetDirectoryName(gameObject.scene.path) : null;
            if (IsUnderAssets(meshDir)) dir = meshDir;
            else if (IsUnderAssets(sceneDir)) dir = sceneDir;

            string baseName = $"{gameObject.name}_FractureBake";
            string path = UnityEditor.AssetDatabase.GenerateUniqueAssetPath($"{dir}/{baseName}.asset");
            UnityEditor.AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        // Unity can only create assets under the project's Assets/ tree (forward-slashed).
        static bool IsUnderAssets(string dir)
            => !string.IsNullOrEmpty(dir) && dir.Replace('\\', '/').StartsWith("Assets");
#endif

#if UNITY_EDITOR
        void OnDrawGizmos()   // debug overlays only — the textured fracture itself is drawn in Update (edit + play)
        {
            Gizmos.matrix = Space.localToWorldMatrix;

            if (showBounds)
            {
                Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.5f);
                Gizmos.DrawWireCube(_localBounds.center, _localBounds.size);
            }

            if ((showWire || previewMode == AuthoringPreviewMode.XRay) && _renderReady && _frags != null)
                DrawExactWirePreview();

            if (showSeeds && _seedsReady && _seedsCpu != null)
            {
                for (int i = 0; i < _seedsCpu.Length; i++)
                {
                    Vector4 s = _seedsCpu[i];
                    Gizmos.color = colorizeFragments
                        ? Color.HSVToRGB((i * 0.61803398f) % 1f, 0.6f, 1f)
                        : Color.Lerp(new Color(0.2f, 0.3f, 0.5f), Color.white, s.w);
                    Gizmos.DrawSphere(new Vector3(s.x, s.y, s.z), seedSize);
                }
            }

            if (fracturePattern == FracturePattern.Volume && distribution == Distribution.Clustered)
            {
                Gizmos.color = new Color(1f, 0.4f, 0.2f, 0.5f);
                Gizmos.DrawWireSphere(clusterCenter, clusterRadius);
            }
            else if (fracturePattern == FracturePattern.MultiScale)
            {
                Gizmos.color = new Color(1f, 0.42f, 0.16f, 0.65f);
                Gizmos.DrawWireSphere(impactPoint, impactRadius * AuthoringReferenceSize);
            }

            if (fracturePattern == FracturePattern.Fibres || fracturePattern == FracturePattern.RadialSheet)
            {
                Vector3 d = EffectivePatternDirection * Mathf.Max(_localBounds.size.magnitude * 0.25f, 0.05f);
                Gizmos.color = fracturePattern == FracturePattern.Fibres
                    ? new Color(0.82f, 0.55f, 0.2f, 0.9f) : new Color(0.3f, 0.75f, 1f, 0.9f);
                Vector3 o = fracturePattern == FracturePattern.RadialSheet ? impactPoint : _localBounds.center;
                Gizmos.DrawLine(o - d, o + d);
            }

            if (breakMode != BreakMode.Manual && showField)
            {
                Transform ft = breakField != null ? breakField : transform;
                Vector3 scale = ft.lossyScale;
                float size = Mathf.Max(1e-4f, breakFieldSize);
                Gizmos.color = new Color(1f, 0.25f, 0.15f, 0.9f);
                if (breakFieldShape == FieldShape.Box)
                {
                    Gizmos.matrix = Matrix4x4.TRS(ft.position, ft.rotation, size * scale);
                    Gizmos.DrawWireCube(Vector3.zero, Vector3.one * 2f);   // half-size 1 in field space
                }
                else if (breakFieldShape == FieldShape.Plane)
                {
                    Gizmos.matrix = Matrix4x4.TRS(ft.position, ft.rotation, Vector3.one);
                    float r = size * Mathf.Max(scale.x, scale.z);
                    Gizmos.DrawLine(new Vector3(-r, 0, 0), new Vector3(r, 0, 0));
                    Gizmos.DrawLine(new Vector3(0, 0, -r), new Vector3(0, 0, r));
                    Gizmos.DrawLine(Vector3.zero, Vector3.up * r * 0.3f);   // break-front normal
                }
                else
                {
                    Gizmos.matrix = Matrix4x4.identity;
                    Gizmos.DrawWireSphere(ft.position, size * Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)));
                }
            }
        }

        void DrawExactWirePreview()
        {
            EnsurePreviewWireMesh();
            if (_previewWireMesh == null) return;

            bool gpuFollow = BindPreviewWireMaterial();
            if (!gpuFollow)
            {
                if (!EnsurePreviewWireMaterial()) return;
                _previewWireMat.SetMatrix("_L2W", Space != null ? Space.localToWorldMatrix : Matrix4x4.identity);
            }

            int pass = gpuFollow ? 1 : 0;
            if (!_previewWireMat.SetPass(pass) && pass != 0)
            {
                _previewWireMat.SetMatrix("_L2W", Space != null ? Space.localToWorldMatrix : Matrix4x4.identity);
                if (!_previewWireMat.SetPass(0)) return;
            }

            Graphics.DrawMeshNow(_previewWireMesh, Matrix4x4.identity);
        }

        bool EnsurePreviewWireMaterial()
        {
            if (_previewWireMat != null) return true;
            Shader sh = BrazeRuntimeShaderLibrary.Find("BrazeFX/VoronoiWire");
            if (sh == null) return false;
            _previewWireMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            return true;
        }

        bool BindPreviewWireMaterial()
        {
            if (!EnsurePreviewWireMaterial()) return false;
            if (_posStateBuf == null || _rotBuf == null || _lifeBuf == null ||
                _deformedPosBuf == null || _freeOffsetBuf == null || _clumpCentroidBuf == null ||
                _effectorPositionBuf == null || _effectorRotationBuf == null || _effectorScaleBuf == null)
                return false;

            _previewWireMat.SetBuffer("_ClumpPosState", _posStateBuf);
            _previewWireMat.SetBuffer("_ClumpRot", _rotBuf);
            _previewWireMat.SetBuffer("_ClumpLife", _lifeBuf);
            _previewWireMat.SetBuffer("_DeformedPos", _deformedPosBuf);
            _previewWireMat.SetBuffer("_FreeOffset", _freeOffsetBuf);
            _previewWireMat.SetBuffer("_ClumpCentroid", _clumpCentroidBuf);
            _previewWireMat.SetBuffer("_EffectorPosition", _effectorPositionBuf);
            _previewWireMat.SetBuffer("_EffectorRotation", _effectorRotationBuf);
            _previewWireMat.SetBuffer("_EffectorScale", _effectorScaleBuf);
            _previewWireMat.SetInt("_EffectorCount", _frameEffectorCount);
            _previewWireMat.SetMatrix("_L2W", Space != null ? Space.localToWorldMatrix : Matrix4x4.identity);
            return true;
        }

        void EnsurePreviewWireMesh()
        {
            if (_frags == null || _frags.Length == 0) return;
            int segmentBudget = Mathf.Clamp(previewWireSegments, 3000, PreviewWireAbsoluteMaxSegments);
            if (_previewWireMesh != null && ReferenceEquals(_previewWireSource, _frags) &&
                Mathf.Approximately(_previewWireExplode, previewExplode) &&
                _previewWireSegmentBudget == segmentBudget)
                return;

            _previewWireSource = _frags;
            _previewWireExplode = previewExplode;
            _previewWireSegmentBudget = segmentBudget;
            if (_previewWireMesh == null)
                _previewWireMesh = new Mesh { name = "BrazeVoronoiWirePreview", hideFlags = HideFlags.HideAndDontSave };
            else
                _previewWireMesh.Clear();
            _previewWireMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            int sourceTris = 0, nonEmpty = 0;
            for (int c = 0; c < _frags.Length; c++)
            {
                var f = _frags[c];
                if (f == null || f.TriCount == 0) continue;
                int cutTris = CountCutTriangles(f);
                sourceTris += cutTris;
                if (cutTris > 0) nonEmpty++;
            }
            if (sourceTris == 0 || nonEmpty == 0) return;

            int triBudget = Mathf.Min(Mathf.Max(nonEmpty, segmentBudget / 3), PreviewWireAbsoluteMaxSegments / 3);
            int stride = Mathf.Max(1, Mathf.CeilToInt(sourceTris / (float)triBudget));
            int totalTris = CountPreviewWireTris(stride);
            while (totalTris > triBudget && stride < 4096)
                totalTris = CountPreviewWireTris(++stride);
            int totalVerts = totalTris * 3;

            _previewWireVerts.Clear();
            _previewWireColors.Clear();
            _previewWireIds.Clear();
            _previewWirePush.Clear();
            _previewWireIndices.Clear();
            TrimPreviewWireCapacity(totalVerts, totalTris * 6);
            _previewWireVerts.Capacity = Mathf.Max(_previewWireVerts.Capacity, totalVerts);
            _previewWireColors.Capacity = Mathf.Max(_previewWireColors.Capacity, totalVerts);
            _previewWireIds.Capacity = Mathf.Max(_previewWireIds.Capacity, totalVerts);
            _previewWirePush.Capacity = Mathf.Max(_previewWirePush.Capacity, totalVerts);
            _previewWireIndices.Capacity = Mathf.Max(_previewWireIndices.Capacity, totalTris * 6);

            Vector3 bc = _localBounds.center;
            int runtimeBase = 0;
            for (int c = 0; c < _frags.Length; c++)
            {
                var f = _frags[c];
                if (f == null || f.TriCount == 0)
                {
                    if (f != null) runtimeBase += f.pos.Count;
                    continue;
                }
                Vector3 push = previewExplode > 0f
                    ? (f.centroid - bc).normalized * (previewExplode * _avgRadius) : Vector3.zero;
                Color col = Color.HSVToRGB((c * 0.61803398f) % 1f, 0.65f, 1f);
                col.a = 0.9f;
                Color32 c32 = col;

                int cutOrdinal = 0;
                for (int t = 0; t < f.TriCount; t++)
                {
                    int src = t * 3;
                    if (!IsCutTriangle(f, src)) continue;
                    if ((cutOrdinal++ % stride) != 0) continue;
                    int o = _previewWireVerts.Count;
                    for (int e = 0; e < 3; e++)
                    {
                        int srcV = src + e;
                        _previewWireVerts.Add(f.pos[srcV] + push);
                        _previewWireColors.Add(c32);
                        _previewWireIds.Add(new Vector2(c, runtimeBase + srcV));
                        _previewWirePush.Add(push);
                    }
                    _previewWireIndices.Add(o);
                    _previewWireIndices.Add(o + 1);
                    _previewWireIndices.Add(o + 1);
                    _previewWireIndices.Add(o + 2);
                    _previewWireIndices.Add(o + 2);
                    _previewWireIndices.Add(o);
                }
                runtimeBase += f.pos.Count;
            }

            _previewWireMesh.SetVertices(_previewWireVerts);
            _previewWireMesh.SetColors(_previewWireColors);
            _previewWireMesh.SetUVs(1, _previewWireIds);
            _previewWireMesh.SetUVs(2, _previewWirePush);
            _previewWireMesh.SetIndices(_previewWireIndices, MeshTopology.Lines, 0, false);
            Vector3 inflate = Vector3.one * Mathf.Max(0.01f, (previewExplode + 1f) * _avgRadius);
            _previewWireMesh.bounds = new Bounds(_localBounds.center, _localBounds.size + inflate * 2f);
        }

        int CountPreviewWireTris(int stride)
        {
            int total = 0;
            for (int c = 0; c < _frags.Length; c++)
            {
                var f = _frags[c];
                if (f == null || f.TriCount == 0) continue;
                int cutTris = CountCutTriangles(f);
                if (cutTris > 0) total += Mathf.Max(1, (cutTris + stride - 1) / stride);
            }
            return total;
        }

        static bool IsCutTriangle(BrazeFractureClipper.Frag fragment, int firstVertex)
            => fragment.skin != null && firstVertex + 2 < fragment.skin.Count &&
               fragment.skin[firstVertex] < 0.5f && fragment.skin[firstVertex + 1] < 0.5f &&
               fragment.skin[firstVertex + 2] < 0.5f;

        static int CountCutTriangles(BrazeFractureClipper.Frag fragment)
        {
            int count = 0;
            for (int t = 0; t < fragment.TriCount; t++)
                if (IsCutTriangle(fragment, t * 3)) count++;
            return count;
        }

        void TrimPreviewWireCapacity(int vertexTarget, int indexTarget)
        {
            int vertexKeep = Mathf.Max(vertexTarget * 2, 1024);
            int indexKeep = Mathf.Max(indexTarget * 2, 2048);
            if (_previewWireVerts.Capacity > vertexKeep) _previewWireVerts.Capacity = vertexTarget;
            if (_previewWireColors.Capacity > vertexKeep) _previewWireColors.Capacity = vertexTarget;
            if (_previewWireIds.Capacity > vertexKeep) _previewWireIds.Capacity = vertexTarget;
            if (_previewWirePush.Capacity > vertexKeep) _previewWirePush.Capacity = vertexTarget;
            if (_previewWireIndices.Capacity > indexKeep) _previewWireIndices.Capacity = indexTarget;
        }

        void ReleasePreviewWire()
        {
            _previewWireSource = null;
            _previewWireExplode = -1f;
            _previewWireSegmentBudget = -1;
            if (_previewWireMesh != null) { DestroyObj(_previewWireMesh); _previewWireMesh = null; }
            if (_previewWireMat != null) { DestroyObj(_previewWireMat); _previewWireMat = null; }
            _previewWireVerts.Clear();
            _previewWireColors.Clear();
            _previewWireIds.Clear();
            _previewWirePush.Clear();
            _previewWireIndices.Clear();
        }
#endif
    }
}
