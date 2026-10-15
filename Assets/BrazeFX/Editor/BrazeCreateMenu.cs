using UnityEditor;
using UnityEngine;
using BrazeFX;
using BrazeFX.CFD;

namespace BrazeFX.EditorTools
{
    /// <summary>
    /// GameObject ▸ BrazeFX create menu — make a ready-to-use BrazeFX object directly, instead of
    /// "create empty GameObject, then Add Component". Mirrors the Add Component taxonomy exactly.
    /// Each entry spawns a named GameObject carrying the one component, parented to the clicked
    /// object (if any), registered for Undo, and selected.
    /// </summary>
    internal static class BrazeCreateMenu
    {
        const string Root = "GameObject/BrazeFX/";
        // Unity derives an auto-generated parent menu's priority from its first registered child. Every leaf must
        // therefore use the same creation priority or the BrazeFX parent can move after a domain reload.
        const int CreatePriority = 10;

        static T Make<T>(MenuCommand cmd, string name) where T : Component
        {
            var go = new GameObject(name);
            var component = go.AddComponent<T>();
            GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
            return component;
        }

        static void Make(MenuCommand cmd, string name, System.Type componentType)
        {
            var go = new GameObject(name);
            if (componentType != null && typeof(Component).IsAssignableFrom(componentType))
                go.AddComponent(componentType);
            GameObjectUtility.SetParentAndAlign(go, cmd.context as GameObject);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
        }

        // ── Fire ────────────────────────────────────────────────────────────────────────────────────
        [MenuItem(Root + "Fire/Container", false, CreatePriority)]
        static void Container(MenuCommand c) => Make<BrazeContainer>(c, "Braze Container");
        [MenuItem(Root + "Fire/Pyro Emitter", false, CreatePriority)]
        static void Emitter(MenuCommand c) => Make<BrazePyroEmitter>(c, "Braze Pyro Emitter");
        [MenuItem(Root + "Fire/Obstacle", false, CreatePriority)]
        static void Obstacle(MenuCommand c) => Make<BrazeObstacle>(c, "Braze Obstacle");
        [MenuItem(Root + "Fire/Forces/Wind", false, CreatePriority)]
        static void WindForce(MenuCommand c) => Make<BrazePyroWindForce>(c, "Braze Wind Force");
        [MenuItem(Root + "Fire/Forces/Vortex", false, CreatePriority)]
        static void VortexForce(MenuCommand c) => Make<BrazePyroVortexForce>(c, "Braze Vortex Force");
        [MenuItem(Root + "Fire/Forces/Spline Vortex", false, CreatePriority)]
        static void SplineVortex(MenuCommand c)
        {
            var go = new GameObject("Braze Spline Vortex");
            GameObjectUtility.SetParentAndAlign(go, c.context as GameObject);

            // A ready-to-shape vertical funnel. Artists can still assign any external BrazeSpline afterward.
            var spline = go.AddComponent<BrazeSpline>();
            spline.projection = BrazeSpline.ProjectionPlane.Free;
            spline.interpolation = BrazeSpline.Interp.CatmullRom;
            spline.points = new System.Collections.Generic.List<Vector3>
            {
                new Vector3(0f, -2f, 0f),
                new Vector3(0.12f, -0.7f, 0.06f),
                new Vector3(-0.1f, 0.7f, 0.1f),
                new Vector3(0f, 2f, 0f),
            };
            spline.inHandles.Clear();
            spline.outHandles.Clear();
            spline.pointTypes.Clear();
            spline.EnsureHandles();
            spline.activePreset = -1;
            spline.version++;

            var vortex = go.AddComponent<BrazeSplineVortex>();
            vortex.spline = spline;
            Undo.RegisterCreatedObjectUndo(go, "Create Braze Spline Vortex");
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
        }
        [MenuItem(Root + "Fire/Forces/Turbulence", false, CreatePriority)]
        static void TurbulenceForce(MenuCommand c) => Make<BrazePyroTurbulenceForce>(c, "Braze Turbulence Force");
        [MenuItem(Root + "Fire/Forces/Gravity", false, CreatePriority)]
        static void GravityForce(MenuCommand c) => Make<BrazePyroGravityForce>(c, "Braze Gravity Force");
        [MenuItem(Root + "Fire/Forces/Attract - Repel", false, CreatePriority)]
        static void AttractForce(MenuCommand c) => Make<BrazePyroAttractForce>(c, "Braze Attract Force");
        [MenuItem(Root + "Fire/Forces/Mushroom Disc", false, CreatePriority)]
        static void DiscForce(MenuCommand c) => Make<BrazePyroDiscForce>(c, "Braze Mushroom Disc Force");
        [MenuItem(Root + "Fire/Forces/Hold", false, CreatePriority)]
        static void HoldForce(MenuCommand c) => Make<BrazePyroHoldForce>(c, "Braze Hold Force");
        [MenuItem(Root + "Fire/Forces/Cauli Billows", false, CreatePriority)]
        static void CauliBillowsForce(MenuCommand c) => Make<BrazePyroCauliBillowsForce>(c, "Braze Cauli Billows Force");
        [MenuItem(Root + "Fire/Forces/Lift", false, CreatePriority)]
        static void LiftForce(MenuCommand c) => Make<BrazePyroLiftForce>(c, "Braze Lift Force");
        [MenuItem(Root + "Fire/Forces/Jet", false, CreatePriority)]
        static void JetForce(MenuCommand c) => Make<BrazePyroJetForce>(c, "Braze Jet Force");
        [MenuItem(Root + "Fire/Forces/Spinner", false, CreatePriority)]
        static void SpinnerForce(MenuCommand c) => Make<BrazePyroSpinnerForce>(c, "Braze Spinner Force");
        [MenuItem(Root + "Fire/Forces/Advanced/Generic Pyro Force", false, CreatePriority)]
        static void PyroForce(MenuCommand c) => Make<BrazePyroForce>(c, "Braze Pyro Force");

        [MenuItem(Root + "Fire/Ribbon Renderer", false, CreatePriority)]
        static void RibbonRenderer(MenuCommand c) =>
            Make(c, "Braze Ribbon Renderer", System.Type.GetType("BrazeFX.BrazeRibbonRenderer, BrazeFX.Runtime"));

        [MenuItem(Root + "Fire/Solo Flame", false, CreatePriority)]
        static void SoloFlame(MenuCommand c) =>
            Make(c, "Braze Solo Flame", System.Type.GetType("BrazeFX.BrazeSoloFlame, BrazeFX.Runtime"));

        [MenuItem(Root + "Fire/Primitive Cinders", false, CreatePriority)]
        static void PrimitiveCinders(MenuCommand c) =>
            Make(c, "Primitive Cinders", System.Type.GetType("BrazeFX.BrazePrimitiveCinders, BrazeFX.Runtime"));

        // ── CFD ─────────────────────────────────────────────────────────────────────────────────────
        [MenuItem(Root + "CFD/CFD Domain", false, CreatePriority)]
        static void CfdDomain(MenuCommand c)
        {
            var go = new GameObject("Braze CFD Domain");
            GameObjectUtility.SetParentAndAlign(go, c.context as GameObject);
            go.AddComponent<BrazeContainer>();
            go.AddComponent<BrazeCfdDomain>();
            Undo.RegisterCreatedObjectUndo(go, "Create Braze CFD Domain");
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
        }

        [MenuItem(Root + "CFD/Wind Tunnel", false, CreatePriority)]
        static void CfdWindTunnel(MenuCommand c)
        {
            var tunnel = Make<BrazeCfdWindTunnel>(c, "Braze CFD Wind Tunnel");
            tunnel.domain = FindCfdDomain(c.context as GameObject);
            if (tunnel.domain != null && tunnel.domain.Container != null)
            {
                BrazeContainer container = tunnel.domain.Container;
                tunnel.transform.rotation = container.transform.rotation;
                Vector3 size = container.WorldSize();
                tunnel.transform.position = container.transform.position - container.transform.forward * size.z * 0.48f;
                tunnel.airTemperatureC = tunnel.domain.ambientTemperatureC;
            }
            tunnel.SyncManagedComponents(true);
        }

        [MenuItem(Root + "CFD/Test Body", false, CreatePriority)]
        static void CfdTestBody(MenuCommand c)
        {
            GameObject context = c.context as GameObject;
            MeshFilter mesh = context != null ? context.GetComponent<MeshFilter>() : null;
            BrazeCfdTestBody body;
            if (context != null && mesh != null && context.GetComponent<BrazeCfdTestBody>() == null)
            {
                body = Undo.AddComponent<BrazeCfdTestBody>(context);
                Selection.activeGameObject = context;
                EditorGUIUtility.PingObject(context);
            }
            else body = Make<BrazeCfdTestBody>(c, "Braze CFD Test Body");
            body.domain = FindCfdDomain(context);
            body.meshSource = mesh;
            if (body.domain != null) body.initialTemperatureC = body.domain.ambientTemperatureC;
            body.SyncManagedComponents(true);
        }

        [MenuItem(Root + "CFD/Advanced/Flow Source", false, CreatePriority)]
        static void CfdFlowSource(MenuCommand c)
        {
            var source = Make<BrazeCfdFlowSource>(c, "Braze CFD Flow Source");
            source.domain = FindCfdDomain(c.context as GameObject);
        }

        [MenuItem(Root + "CFD/Advanced/Thermal Source", false, CreatePriority)]
        static void CfdThermalSource(MenuCommand c)
        {
            var source = Make<BrazeCfdThermalSource>(c, "Braze CFD Thermal Source");
            source.domain = FindCfdDomain(c.context as GameObject);
        }

        [MenuItem(Root + "CFD/Advanced/Visualizer", false, CreatePriority)]
        static void CfdVisualizer(MenuCommand c)
        {
            var visualizer = Make<BrazeCfdVisualizer>(c, "Braze CFD Visualizer");
            visualizer.domain = FindCfdDomain(c.context as GameObject);
            visualizer.SyncManagedComponents(true);
        }

        [MenuItem(Root + "CFD/Advanced/SDF Boundary", false, CreatePriority)]
        static void CfdSdfBoundary(MenuCommand c)
        {
            var boundary = Make<BrazeCfdSdfBoundary>(c, "Braze CFD SDF Boundary");
            var context = c.context as GameObject;
            boundary.domain = FindCfdDomain(context);
            boundary.meshFilter = context != null ? context.GetComponent<MeshFilter>() : null;
        }

        [MenuItem(Root + "CFD/Advanced/Boundary Conditions/Velocity Inlet", false, CreatePriority)]
        static void CfdVelocityInlet(MenuCommand c)
        {
            var patch = Make<BrazeCfdBoundaryPatch>(c, "Braze CFD Velocity Inlet");
            patch.domain = FindCfdDomain(c.context as GameObject);
            patch.mode = BrazeCfdBoundaryPatchMode.VelocityInlet;
        }

        [MenuItem(Root + "CFD/Advanced/Boundary Conditions/Pressure Outlet", false, CreatePriority)]
        static void CfdPressureOutlet(MenuCommand c)
        {
            var patch = Make<BrazeCfdBoundaryPatch>(c, "Braze CFD Pressure Outlet");
            patch.domain = FindCfdDomain(c.context as GameObject);
            patch.mode = BrazeCfdBoundaryPatchMode.PressureOutlet;
        }

        [MenuItem(Root + "CFD/Advanced/Heat Map", false, CreatePriority)]
        static void CfdHeatMap(MenuCommand c)
        {
            var heatMap = Make<BrazeCfdHeatMap>(c, "Braze CFD Heat Map");
            heatMap.domain = FindCfdDomain(c.context as GameObject);
        }

        [MenuItem(Root + "CFD/Advanced/Surface Pressure", false, CreatePriority)]
        static void CfdSurfacePressure(MenuCommand c)
        {
            var context = c.context as GameObject;
            var pressure = Make<BrazeCfdSurfacePressure>(c, "Braze CFD Surface Pressure");
            pressure.domain = FindCfdDomain(context);
            pressure.meshFilter = context != null ? context.GetComponent<MeshFilter>() : null;
        }

        static BrazeCfdDomain FindCfdDomain(GameObject context)
        {
            if (context != null)
            {
                var domain = context.GetComponentInParent<BrazeCfdDomain>();
                if (domain != null) return domain;
                domain = context.GetComponentInChildren<BrazeCfdDomain>(true);
                if (domain != null) return domain;
            }

            // The common simple scene keeps Domain, Wind Tunnel, and Test Body as siblings. Auto-bind only when
            // there is exactly one unambiguous domain in the same scene; multiple studies still require a choice.
#if UNITY_6000_5_OR_NEWER
            var domains = Object.FindObjectsByType<BrazeCfdDomain>(FindObjectsInactive.Include);
#else
            var domains = Object.FindObjectsByType<BrazeCfdDomain>(FindObjectsInactive.Include,
                FindObjectsSortMode.None);
#endif
            BrazeCfdDomain only = null;
            for (int i = 0; i < domains.Length; i++)
            {
                if (context != null && domains[i].gameObject.scene != context.scene) continue;
                if (only != null) return null;
                only = domains[i];
            }
            return only;
        }

        // ── Volume ──────────────────────────────────────────────────────────────────────────────────
        [MenuItem(Root + "Volume/Volume Generator", false, CreatePriority)]
        static void VolumeGenerator(MenuCommand c) => Make<BrazeVolume>(c, "Braze Volume");

        [MenuItem(Root + "Volume/Volume Deformers/Noise", false, CreatePriority)]
        static void VFieldNoise(MenuCommand c) => Make<BrazeNoiseField>(c, "Volume Field — Noise");
        [MenuItem(Root + "Volume/Volume Deformers/Density (Add or Remove)", false, CreatePriority)]
        static void VFieldDensity(MenuCommand c) => Make<BrazeDensityField>(c, "Volume Field — Density");
        [MenuItem(Root + "Volume/Volume Deformers/Deform", false, CreatePriority)]
        static void VFieldDeform(MenuCommand c) => Make<BrazeDeformField>(c, "Volume Field — Deform");
        [MenuItem(Root + "Volume/Volume Deformers/Smooth", false, CreatePriority)]
        static void VFieldSmooth(MenuCommand c) => Make<BrazeSmoothField>(c, "Volume Field — Smooth");
        [MenuItem(Root + "Volume/Volume Deformers/Remap (Opacity)", false, CreatePriority)]
        static void VFieldRemap(MenuCommand c) => Make<BrazeRemapField>(c, "Volume Field — Remap");
        [MenuItem(Root + "Volume/Volume Deformers/Spline (Add or Remove)", false, CreatePriority)]
        static void VFieldSpline(MenuCommand c) => Make<BrazeSplineField>(c, "Volume Field — Spline");

        // ── Baking ──────────────────────────────────────────────────────────────────────────────────
        [MenuItem(Root + "Baking/Cache Baker", false, CreatePriority)]
        static void CacheBaker(MenuCommand c) => Make<BrazeCacheBaker>(c, "Braze Cache Baker");
        [MenuItem(Root + "Baking/Cache Player", false, CreatePriority)]
        static void CachePlayer(MenuCommand c) => Make<BrazeCachePlayer>(c, "Braze Cache Player");
        [MenuItem(Root + "Baking/Flipbook Player", false, CreatePriority)]
        static void FlipbookPlayer(MenuCommand c) => Make<BrazeFlipbookPlayer>(c, "Braze Flipbook Player");
        [MenuItem(Root + "Baking/VDB Import", false, CreatePriority)]
        static void Import(MenuCommand c)
        {
            Make<BrazeVDBImport>(c, "Braze VDB Import");
        }
        [MenuItem(Root + "Baking/Export Studio", false, CreatePriority)]
        static void Export(MenuCommand c)
        {
            Make<BrazeExport>(c, "Braze Export Studio");
            var workflow = Selection.activeGameObject.GetComponent<BrazeExport>();
            GameObject context = c.context as GameObject;
            if (context != null)
            {
                workflow.container = context.GetComponentInParent<BrazeContainer>();
                if (workflow.container == null) workflow.container = context.GetComponentInChildren<BrazeContainer>(true);
            }
        }

        // ── Particles ───────────────────────────────────────────────────────────────────────────────
        [MenuItem(Root + "Particles/Particle System", false, CreatePriority)]
        static void ParticleSys(MenuCommand c) => Make<BrazeParticleSystem>(c, "Braze Particle System");
        [MenuItem(Root + "Particles/Particle Collider", false, CreatePriority)]
        static void ParticleCollider(MenuCommand c) => Make<BrazeParticleCollider>(c, "Braze Particle Collider");
        [MenuItem(Root + "Particles/Trail Renderer", false, CreatePriority)]
        static void TrailRenderer(MenuCommand c) => Make<BrazeTrailRenderer>(c, "Braze Trail Renderer");

        [MenuItem(Root + "Particles/Forces/Drag", false, CreatePriority)]
        static void Drag(MenuCommand c) => Make<BrazeDrag>(c, "Braze Drag");
        [MenuItem(Root + "Particles/Forces/Wind", false, CreatePriority)]
        static void Wind(MenuCommand c) => Make<BrazeWind>(c, "Braze Wind");
        [MenuItem(Root + "Particles/Forces/Gravity", false, CreatePriority)]
        static void Gravity(MenuCommand c) => Make<BrazeGravity>(c, "Braze Gravity");
        [MenuItem(Root + "Particles/Forces/Radial", false, CreatePriority)]
        static void Radial(MenuCommand c) => Make<BrazeRadial>(c, "Braze Radial");
        [MenuItem(Root + "Particles/Forces/Vortex", false, CreatePriority)]
        static void Vortex(MenuCommand c) => Make<BrazeVortex>(c, "Braze Vortex");
        [MenuItem(Root + "Particles/Forces/Turbulence", false, CreatePriority)]
        static void Turbulence(MenuCommand c) => Make<BrazeTurbulence>(c, "Braze Turbulence");
        [MenuItem(Root + "Particles/Forces/Surface Flow", false, CreatePriority)]
        static void SurfaceFlow(MenuCommand c) => Make<BrazeSurfaceFlow>(c, "Braze Surface Flow");

        // ── Motion Design ────────────────────────────────────────────────────────────────────────────
        [MenuItem(Root + "Motion Design/Cloner", false, CreatePriority)]
        static void Cloner(MenuCommand c) => Make<BrazeCloner>(c, "Braze Cloner");
        [MenuItem(Root + "Motion Design/Instance", false, CreatePriority)]
        static void Instance(MenuCommand c) => Make<BrazeInstance>(c, "Braze Instance");
        [MenuItem(Root + "Motion Design/Tracer", false, CreatePriority)]
        static void Tracer(MenuCommand c) => Make<BrazeTracer>(c, "Braze Tracer");
        [MenuItem(Root + "Motion Design/Effectors/Plane", false, CreatePriority)]
        static void PlaneEffector(MenuCommand c) => Make<BrazePlaneEffector>(c, "Braze Plane Effector");
        [MenuItem(Root + "Motion Design/Effectors/Random", false, CreatePriority)]
        static void RandomEffector(MenuCommand c) => Make<BrazeRandomEffector>(c, "Braze Random Effector");
        [MenuItem(Root + "Motion Design/Effectors/Step", false, CreatePriority)]
        static void StepEffector(MenuCommand c) => Make<BrazeStepEffector>(c, "Braze Step Effector");
        [MenuItem(Root + "Motion Design/Effectors/Delay", false, CreatePriority)]
        static void DelayEffector(MenuCommand c) => Make<BrazeDelayEffector>(c, "Braze Delay Effector");
        [MenuItem(Root + "Motion Design/Effectors/Target", false, CreatePriority)]
        static void TargetEffector(MenuCommand c) => Make<BrazeTargetEffector>(c, "Braze Target Effector");
        [MenuItem(Root + "Motion Design/Effectors/Spline", false, CreatePriority)]
        static void SplineEffector(MenuCommand c) => Make<BrazeSplineEffector>(c, "Braze Spline Effector");
        [MenuItem(Root + "Motion Design/Effectors/Shader", false, CreatePriority)]
        static void ShaderEffector(MenuCommand c) => Make<BrazeShaderEffector>(c, "Braze Shader Effector");
        [MenuItem(Root + "Motion Design/Effectors/Push Apart", false, CreatePriority)]
        static void PushApartEffector(MenuCommand c) => Make<BrazePushApartEffector>(c, "Braze Push Apart Effector");

        // ── Destruction ──────────────────────────────────────────────────────────────────────────────
        [MenuItem(Root + "Destruction/Voronoi Fracture", false, CreatePriority)]
        static void Voronoi(MenuCommand c) => Make<BrazeVoronoiFracture>(c, "Braze Voronoi Fracture");
        [MenuItem(Root + "Destruction/Fracture Impact Source", false, CreatePriority)]
        static void FractureImpactSource(MenuCommand c) => Make<BrazeFractureImpactSource>(c, "Braze Fracture Impact Source");
        [MenuItem(Root + "Destruction/Surface Disintegrator", false, CreatePriority)]
        static void Disintegrator(MenuCommand c) => Make<BrazeSurfaceDisintegrator>(c, "Braze Surface Disintegrator");
        [MenuItem(Root + "Destruction/Spread", false, CreatePriority)]
        static void Spread(MenuCommand c) => Make<BrazeSpread>(c, "Braze Spread");
        [MenuItem(Root + "Destruction/Surface Mask", false, CreatePriority)]
        static void SurfaceMask(MenuCommand c) => Make<BrazeSurfaceMask>(c, "Braze Surface Mask");
        // ── Spline ──────────────────────────────────────────────────────────────────────────────────
        [MenuItem(Root + "Spline/Spline", false, CreatePriority)]
        static void Spline(MenuCommand c) => Make<BrazeSpline>(c, "Braze Spline");
        [MenuItem(Root + "Spline/Spline Surface", false, CreatePriority)]
        static void SplineSurface(MenuCommand c) => Make<BrazeSplineSurface>(c, "Braze Spline Surface");
        [MenuItem(Root + "Spline/Spline Flow", false, CreatePriority)]
        static void SplineFlow(MenuCommand c) => Make<BrazeSplineFlow>(c, "Braze Spline Flow");
        [MenuItem(Root + "Spline/Follow Spline", false, CreatePriority)]
        static void FollowSpline(MenuCommand c) => Make<BrazeFollowSpline>(c, "Braze Follow Spline");

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────────
        [MenuItem(Root + "Helpers/Ground Follow", false, CreatePriority)]
        static void GroundFollow(MenuCommand c) => Make<BrazeGroundFollow>(c, "Braze Ground Follow");
        [MenuItem(Root + "Helpers/Ruler", false, CreatePriority)]
        static void Ruler(MenuCommand c) => Make<BrazeRuler>(c, "Braze Ruler");
        [MenuItem(Root + "Helpers/Fracture Bullet Spawner", false, CreatePriority)]
        static void FractureBulletSpawner(MenuCommand c) => Make<BrazeFractureBulletSpawner>(c, "Braze Fracture Bullet Spawner");

        // ── Cinematics ──────────────────────────────────────────────────────────────────────────────
        [MenuItem(Root + "Cinematics/Camera", false, CreatePriority)]
        static void Camera(MenuCommand c) => Make<BrazeCamera>(c, "Braze Camera");
        [MenuItem(Root + "Cinematics/Timeline", false, CreatePriority)]
        static void Timeline(MenuCommand c) => Make<BrazeTimeline>(c, "Braze Timeline");
        [MenuItem(Root + "Cinematics/Motion", false, CreatePriority)]
        static void Motion(MenuCommand c) => Make<BrazeMotion>(c, "Braze Motion");
    }
}
