using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>
    /// Gives each BrazeFX component a native inspector/hierarchy icon (replacing the default script
    /// "#" icon), like Unity's built-in components. Bakes the procedural BrazeFX glyphs to PNG assets and
    /// assigns them to each script via MonoImporter. Self-healing: re-assigns any missing icon on load.
    /// </summary>
    [InitializeOnLoad]
    internal static class BrazeIconAssigner
    {
        const string Dir = "Assets/BrazeFX/Editor/Icons";

        static readonly (string type, string icon)[] Map =
        {
            ("BrazeCfdDomain", "cfd"),
            ("BrazeCfdWindTunnel", "wind_tunnel"),
            ("BrazeCfdVisualizer", "cfd"),
            ("BrazeCfdTestBody", "test_body"),
            ("BrazeCfdFlowSource", "inlet"),
            ("BrazeCfdThermalSource", "heater"),
            ("BrazeCfdSdfBoundary", "sdf"),
            ("BrazeCfdBoundaryPatch", "boundary_patch"),
            ("BrazeCfdHeatMap", "heatmap"),
            ("BrazeCfdAnalysisSlice", "heatmap"),
            ("BrazeCfdSurfacePressure", "pressure"),
            ("BrazeContainer", "container"),
            ("BrazePyroForce", "force"),
            ("BrazePyroWindForce", "force"),
            ("BrazePyroVortexForce", "force"),
            ("BrazePyroTurbulenceForce", "force"),
            ("BrazePyroGravityForce", "force"),
            ("BrazePyroAttractForce", "force"),
            ("BrazePyroDiscForce", "force"),
            ("BrazePyroHoldForce", "force"),
            ("BrazePyroCauliBillowsForce", "force"),
            ("BrazePyroLiftForce", "force"),
            ("BrazePyroJetForce", "force"),
            ("BrazePyroSpinnerForce", "force"),
            ("BrazeSplineVortex", "spline"),
            ("BrazePyroEmitter",   "emitter"),
            ("BrazeObstacle",  "obstacle"),
            ("BrazeFlipbookPlayer", "flipbook"),
            ("BrazeParticleSystem", "particles"),
            ("BrazeParticleCollider", "collider"),
            ("BrazeSpread", "spread"),
            ("BrazeCloner", "cloner"),
            ("BrazeInstance", "instance"),
            ("BrazeTracer", "tracer"),
            ("BrazeSurfaceDisintegrator", "disintegrate"),
            ("BrazeVoronoiFracture", "voronoi"),
            ("BrazeFractureImpactSource", "radial"),
            ("BrazePlaneEffector", "plane"),
            ("BrazeRandomEffector", "effector"),
            ("BrazeStepEffector", "step"),
            ("BrazeDelayEffector", "delay"),
            ("BrazeTargetEffector", "target"),
            ("BrazeSplineEffector", "spline"),
            ("BrazeShaderEffector", "shader"),
            ("BrazePushApartEffector", "pushapart"),
            ("BrazeWind", "dynamics"),
            ("BrazeGravity", "gravity"),
            ("BrazeRadial", "radial"),
            ("BrazeVortex", "force"),
            ("BrazeDrag", "drag"),
            ("BrazeTurbulence", "turbulence"),
            ("BrazeSurfaceFlow", "turbulence"),
            ("BrazeSplineFlow", "spline"),
            ("BrazeTimeline", "timeline"),
            ("BrazeCamera", "camera"),
            ("BrazeMotion", "motion"),
            ("BrazeTrailRenderer", "renderer"),
            ("BrazeRibbonRenderer", "flame"),
            ("BrazeSoloFlame", "flame"),
            ("BrazePrimitiveCinders", "flame"),
            ("BrazeSurfaceMask", "mapper"),
            ("BrazeVDBImport", "vdb_import"),
            ("BrazeExport", "export_studio"),
            ("BrazeFractureBulletSpawner", "bullet_spawner"),
            ("BrazeCacheBaker", "cache_baker"),     // procedural glyph baked on first load (no PNG yet)
            ("BrazeCachePlayer", "cache_player"),
            ("BrazeGroundFollow", "groundfollow"),
            ("BrazeRuler", "ruler"),
            ("BrazeVolume", "cloud"),
            ("BrazeNoiseField", "noise"),
            ("BrazeDensityField", "density"),
            ("BrazeDeformField", "deform"),
            ("BrazeSmoothField", "smooth"),
            ("BrazeRemapField", "remap"),
            ("BrazeSplineField", "spline"),
            ("BrazeSplineSurface", "spline"),
            ("BrazeSpline", "spline"),
            ("BrazeFollowSpline", "followspline"),
        };

        static BrazeIconAssigner() => EditorApplication.delayCall += () => Run(false);

        [MenuItem("Tools/BrazeFX/Reassign Component Icons")]
        static void Reassign() => Run(true);

        static void Run(bool force)
        {
            try
            {
                foreach (var (type, icon) in Map)
                {
                    string path = ScriptPath(type);
                    if (path == null) continue;
                    if (AssetImporter.GetAtPath(path) is not MonoImporter importer) continue;

                    var tex = EnsureIconAsset(icon, force);
                    if (tex == null) continue;

                    var current = importer.GetIcon();
                    if (!force && current != null && AssetDatabase.GetAssetPath(current) == AssetDatabase.GetAssetPath(tex))
                        continue;

                    importer.SetIcon(tex);
                    importer.SaveAndReimport();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[BrazeFX] Component icon assignment skipped: {e.Message}");
            }
        }

        static Texture2D EnsureIconAsset(string icon, bool force)
        {
            if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
            string p = $"{Dir}/braze_{icon}.png";
            if (force || !File.Exists(p))
            {
                var tmp = BrazeIconTex.CreateColored(icon, BrazeEditorGUI.Accent, 128);
                File.WriteAllBytes(p, tmp.EncodeToPNG());
                UnityEngine.Object.DestroyImmediate(tmp);
                AssetDatabase.ImportAsset(p);
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(p);
        }

        static string ScriptPath(string typeName)
        {
            foreach (var guid in AssetDatabase.FindAssets($"{typeName} t:MonoScript"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var ms = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                var cls = ms != null ? ms.GetClass() : null;
                if (cls != null && cls.Name == typeName &&
                    (cls.Namespace == "BrazeFX" || cls.Namespace == "BrazeFX.CFD")) return path;
            }
            return null;
        }
    }
}
