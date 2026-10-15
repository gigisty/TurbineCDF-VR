using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using BrazeFX.CFD;

namespace BrazeFX.EditorTools
{
    /// <summary>
    /// Draws the BrazeFX component icon next to any GameObject in the Hierarchy that owns a BrazeFX
    /// component (container / emitter / force / timeline) — auto-detected, no per-object setup, updates live.
    /// </summary>
    [InitializeOnLoad]
    internal static class BrazeHierarchyIcons
    {
        static readonly Dictionary<int, string> IconCache = new();

        static BrazeHierarchyIcons()
        {
#if UNITY_6000_5_OR_NEWER
            EditorApplication.hierarchyWindowItemByEntityIdOnGUI += DrawByEntityId;
#else
            EditorApplication.hierarchyWindowItemOnGUI += DrawByInstanceId;
#endif
            EditorApplication.hierarchyChanged += ClearCache;
        }

        static void ClearCache() => IconCache.Clear();

#if UNITY_6000_5_OR_NEWER
        static void DrawByEntityId(EntityId entityId, Rect rect)
        {
            if (EditorUtility.EntityIdToObject(entityId) is not GameObject go) return;
            Draw(go, entityId.GetHashCode(), rect, Selection.Contains(entityId));
        }
#else
        static void DrawByInstanceId(int instanceId, Rect rect)
        {
#if UNITY_6000_3_OR_NEWER
            EntityId entityId = instanceId;
            if (EditorUtility.EntityIdToObject(entityId) is not GameObject go) return;
            Draw(go, instanceId, rect, Selection.Contains(entityId));
#else
            if (EditorUtility.InstanceIDToObject(instanceId) is not GameObject go) return;
            Draw(go, instanceId, rect, Selection.Contains(instanceId));
#endif
        }
#endif

        static void Draw(GameObject go, int identity, Rect rect, bool selected)
        {
            if (!IconCache.TryGetValue(identity, out string icon))
            {
                icon = IconFor(go);
                IconCache.Add(identity, icon);
            }
            if (icon == null) return;
            var tex = BrazeIconTex.Get(icon);
            if (tex == null) return;

            // Overlay our glyph on the default icon slot (left of the label).
            var ir = new Rect(rect.x, rect.y + (rect.height - 16f) * 0.5f, 16f, 16f);
            EditorGUI.DrawRect(ir, RowBg(selected));   // mask the default GameObject icon
            var prev = GUI.color;
            GUI.color = BrazeEditorGUI.Accent;
            GUI.DrawTexture(ir, tex, ScaleMode.ScaleToFit, true);
            GUI.color = prev;
        }

        static string IconFor(GameObject go)
        {
            // The import workflow can own hidden playback/render helpers; keep its authored root identifiable.
            if (go.TryGetComponent<BrazeVDBImport>(out _)) return "vdb_import";
            if (go.TryGetComponent<BrazeExport>(out _)) return "export_studio";
            if (go.TryGetComponent<BrazeFractureBulletSpawner>(out _)) return "bullet_spawner";
            if (go.TryGetComponent<BrazeCfdDomain>(out _)) return "cfd";
            if (go.TryGetComponent<BrazeCfdWindTunnel>(out _)) return "wind_tunnel";
            if (go.TryGetComponent<BrazeCfdTestBody>(out _)) return "test_body";
            if (go.TryGetComponent<BrazeCfdFlowSource>(out _)) return "inlet";
            if (go.TryGetComponent<BrazeCfdThermalSource>(out _)) return "heater";
            if (go.TryGetComponent<BrazeCfdSdfBoundary>(out _)) return "sdf";
            if (go.TryGetComponent<BrazeCfdBoundaryPatch>(out var patch))
                return patch.mode == BrazeCfdBoundaryPatchMode.VelocityInlet ? "inlet" : "outlet";
            if (go.TryGetComponent<BrazeCfdHeatMap>(out _)) return "heatmap";
            if (go.TryGetComponent<BrazeCfdAnalysisSlice>(out _)) return "heatmap";
            if (go.TryGetComponent<BrazeCfdSurfacePressure>(out _)) return "pressure";
            if (go.TryGetComponent<BrazeContainer>(out _)) return "container";
            if (go.TryGetComponent<BrazePyroEmitter>(out _)) return "emitter";
            if (go.TryGetComponent<BrazeCamera>(out _)) return "camera";
            if (go.TryGetComponent<BrazeMotion>(out _)) return "motion";
            if (go.TryGetComponent<BrazeTimeline>(out _)) return "timeline";
            if (go.TryGetComponent<BrazeSplineVortex>(out _)) return "spline";
            if (go.TryGetComponent<BrazePyroForce>(out _)) return "force";
            if (go.TryGetComponent<BrazeObstacle>(out _)) return "obstacle";
            if (go.TryGetComponent<BrazeCacheBaker>(out _)) return "cache_baker";
            if (go.TryGetComponent<BrazeCachePlayer>(out _)) return "cache_player";
            if (go.TryGetComponent<BrazeFlipbookPlayer>(out _)) return "flipbook";
            if (go.TryGetComponent<BrazeParticleSystem>(out _)) return "particles";
            if (go.TryGetComponent<BrazeParticleCollider>(out _)) return "collider";
            if (go.TryGetComponent<BrazeTrailRenderer>(out _)) return "renderer";
            if (go.TryGetComponent<BrazeRibbonRenderer>(out _)) return "flame";
            if (go.TryGetComponent<BrazePrimitiveCinders>(out _)) return "flame";
            if (go.TryGetComponent<BrazeSoloFlame>(out _)) return "flame";
            if (go.TryGetComponent<BrazeSpread>(out _)) return "spread";
            // Braze Instance hosts a hidden BrazeCloner engine on the same GameObject — check it first.
            if (go.TryGetComponent<BrazeInstance>(out _)) return "instance";
            if (go.TryGetComponent<BrazeCloner>(out _)) return "cloner";
            if (go.TryGetComponent<BrazeTracer>(out _)) return "tracer";
            if (go.TryGetComponent<BrazeSurfaceDisintegrator>(out _)) return "disintegrate";
            if (go.TryGetComponent<BrazeVoronoiFracture>(out _)) return "voronoi";
            if (go.TryGetComponent<BrazeFractureImpactSource>(out _)) return "radial";
            if (go.TryGetComponent<BrazePlaneEffector>(out _)) return "plane";
            if (go.TryGetComponent<BrazeRandomEffector>(out _)) return "effector";
            if (go.TryGetComponent<BrazeStepEffector>(out _)) return "step";
            if (go.TryGetComponent<BrazeDelayEffector>(out _)) return "delay";
            if (go.TryGetComponent<BrazeTargetEffector>(out _)) return "target";
            if (go.TryGetComponent<BrazeSplineEffector>(out _)) return "spline";
            if (go.TryGetComponent<BrazeShaderEffector>(out _)) return "shader";
            if (go.TryGetComponent<BrazePushApartEffector>(out _)) return "pushapart";
            if (go.TryGetComponent<BrazeSurfaceMask>(out _)) return "mapper";
            if (go.TryGetComponent<BrazeWind>(out _)) return "dynamics";
            if (go.TryGetComponent<BrazeGravity>(out _)) return "gravity";
            if (go.TryGetComponent<BrazeRadial>(out _)) return "radial";
            if (go.TryGetComponent<BrazeVortex>(out _)) return "force";
            if (go.TryGetComponent<BrazeDrag>(out _)) return "drag";
            if (go.TryGetComponent<BrazeTurbulence>(out _)) return "turbulence";
            if (go.TryGetComponent<BrazeSurfaceFlow>(out _)) return "turbulence";
            if (go.TryGetComponent<BrazeSplineFlow>(out _)) return "spline";
            if (go.TryGetComponent<BrazeFollowSpline>(out _)) return "followspline";
            // Volume generator + its deformer fields (check fields before BrazeSpline — a Spline field carries a spline).
            if (go.TryGetComponent<BrazeNoiseField>(out _)) return "noise";
            if (go.TryGetComponent<BrazeDensityField>(out _)) return "density";
            if (go.TryGetComponent<BrazeDeformField>(out _)) return "deform";
            if (go.TryGetComponent<BrazeSmoothField>(out _)) return "smooth";
            if (go.TryGetComponent<BrazeRemapField>(out _)) return "remap";
            if (go.TryGetComponent<BrazeSplineField>(out _)) return "spline";
            if (go.TryGetComponent<BrazeVolume>(out _)) return "cloud";
            if (go.TryGetComponent<BrazeSplineSurface>(out _)) return "spline";
            if (go.TryGetComponent<BrazeSpline>(out _)) return "spline";
            if (go.TryGetComponent<BrazeGroundFollow>(out _)) return "groundfollow";
            if (go.TryGetComponent<BrazeRuler>(out _)) return "ruler";
            return null;
        }

        // Approximate hierarchy row background so the default icon doesn't bleed through our glyph.
        static Color RowBg(bool selected)
        {
            if (selected) return new Color(0.17f, 0.27f, 0.42f);                 // selection blue
            return EditorGUIUtility.isProSkin ? new Color(0.22f, 0.22f, 0.22f)   // dark hierarchy bg
                                              : new Color(0.78f, 0.78f, 0.78f);
        }
    }
}
