using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Premium IMGUI inspector + interactive scene handles for <see cref="BrazeCamera"/> (DCC theme).</summary>
    [CustomEditor(typeof(BrazeCamera))]
    [CanEditMultipleObjects]
    public sealed class BrazeCameraEditor : Editor
    {
        const string K = "BrazeFX.Camera.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("BRAZE CAMERA", "camera");

            SerializedProperty P(string n) => serializedObject.FindProperty(n);
            void Field(string n, string label) =>
                BrazeEditorGUI.PropertyField(P(n), new GUIContent(label));

            if (BrazeEditorGUI.Section(K + "rig", "RIG"))
            {
                Field("cam", "Camera");
                if (P("cam").objectReferenceValue == null)
                    BrazeEditorGUI.Hint("No Camera assigned — the rig will drive a Camera on this object or a child.");
                Field("follow", "Follow Target");
                Field("pivotOffset", "Pivot Offset");
                BrazeEditorGUI.Hint("Follow Target is the orbit CENTRE the camera circles (a Spline overrides it). To " +
                                    "AIM at a different point — e.g. while riding a path — use the Look At section, not " +
                                    "this. Empty = pivot on this object.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "orbit", "ORBIT"))
            {
                Field("azimuth", "Azimuth (horizontal)");
                Field("elevation", "Elevation (vertical)");
                Field("radius", "Radius");
                if (GUILayout.Button("Frame Follow Target"))
                    foreach (var t in targets) FrameTarget((BrazeCamera)t);
                BrazeEditorGUI.Hint("Azimuth orbits 360° horizontally; Elevation orbits the full vertical (over the " +
                                    "top, no roll flip); Radius dollies in/out. Drag the orange handle in the scene too.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "spin", "SPIN"))
            {
                Field("autoSpinAzimuth", "Azimuth Speed °/s");
                Field("autoSpinElevation", "Elevation Speed °/s");
                BrazeEditorGUI.Hint("Continuous hands-off orbit, added on top of the angle above. Animate the angle " +
                                    "for keyed moves, or set a speed here for a turntable.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "path", "PATH (SPLINE DOLLY)"))
            {
                Field("path", "Spline");
                if (P("path").objectReferenceValue != null)
                {
                    Field("pathPosition", "Position");
                    Field("pathSpeed", "Speed (frac/s)");
                    Field("pathWrap", "Wrap");
                    BrazeEditorGUI.ToggleButton("Constant Speed", P("constantSpeed"));
                    BrazeEditorGUI.ToggleButton("Align to Tangent", P("alignToTangent"));
                    BrazeEditorGUI.Hint("The orbit centre rides the spline — Radius→0 puts the camera on the curve, " +
                                        "or keep Radius to orbit a point gliding along it. Position keys the dolly; " +
                                        "Speed runs it hands-off. Align to Tangent turns the orbit with the path heading.");
                }
                else
                {
                    BrazeEditorGUI.Hint("Assign a Braze Spline to make the rig ride a path. Empty = fixed-pivot orbit " +
                                        "(a Spline overrides the Follow Target as the orbit centre).");
                }
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "lookat", "LOOK AT"))
            {
                Field("lookTarget", "Look Target");
                if (P("lookTarget").objectReferenceValue != null)
                    Field("lookTargetOffset", "Look Offset");
                BrazeEditorGUI.Hint("AIM override: the orbit / path still POSITIONS the camera, but it now points at " +
                                    "the Look Target instead of the orbit centre — so a spline-riding camera can hold " +
                                    "a fixed subject. This is the AIM; Follow Target (Rig) is the orbit CENTRE. " +
                                    "Empty = look at the orbit centre.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "lens", "LENS"))
            {
                Field("fieldOfView", "Field of View");
                BrazeEditorGUI.ToggleButton("Dolly Zoom", P("dollyZoom"));
                BrazeEditorGUI.Hint("Dolly Zoom holds the subject's size while Radius animates → the 'vertigo' effect.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "motion", "MOTION (NOISE)"))
            {
                Field("shakeIntensity", "Shake Intensity");
                BrazeEditorGUI.SubLabel("SWAY  (slow drift)");
                DrawNoise(P("sway"));
                BrazeEditorGUI.SubLabel("SHAKE  (fast jitter)");
                DrawNoise(P("shake"));
                BrazeEditorGUI.Hint("Two Perlin layers. Sway = slow handheld float, Shake = fast impact jitter. " +
                                    "Shake Intensity scales both — ramp it on the timeline for the detonation.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "feel", "FEEL"))
            {
                Field("damping", "Damping (sec)");
                BrazeEditorGUI.Hint("Smoothing toward the (animated) targets. 0 = snap; higher = heavier ease.");
            }
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();
        }

        static void DrawNoise(SerializedProperty layer)
        {
            BrazeEditorGUI.PropertyField(layer.FindPropertyRelative("positionAmp"), new GUIContent("Position"));
            BrazeEditorGUI.PropertyField(layer.FindPropertyRelative("rotationAmp"), new GUIContent("Rotation °"));
            BrazeEditorGUI.PropertyField(layer.FindPropertyRelative("frequency"), new GUIContent("Frequency Hz"));
            BrazeEditorGUI.PropertyField(layer.FindPropertyRelative("seed"), new GUIContent("Seed"));
        }

        // Fit the follow target's renderer bounds in view: set the pivot offset to the bounds centre and the
        // radius to frame it for the current FOV. Falls back to a small radius if there's no renderer.
        static void FrameTarget(BrazeCamera bc)
        {
            Transform anchor = bc.follow != null ? bc.follow : bc.transform;
            var rends = anchor.GetComponentsInChildren<Renderer>();
            if (rends == null || rends.Length == 0) return;
            Bounds b = rends[0].bounds;
            for (int i = 1; i < rends.Length; i++) b.Encapsulate(rends[i].bounds);

            Undo.RecordObject(bc, "Frame Braze Camera");
            bc.pivotOffset = b.center - anchor.position;
            float fit = b.extents.magnitude;
            bc.radius = fit / Mathf.Max(0.05f, Mathf.Tan(bc.fieldOfView * 0.5f * Mathf.Deg2Rad)) + fit;
            EditorUtility.SetDirty(bc);
        }

        void OnSceneGUI()
        {
            var bc = (BrazeCamera)target;
            Vector3 pivot = bc.Pivot;
            float r = Mathf.Max(0.05f, bc.radius);
            float azRad = bc.azimuth * Mathf.Deg2Rad;

            // Orbit ring (azimuth) + elevation arc (great circle at the current azimuth).
            Handles.color = new Color(1f, 0.45f, 0.14f, 0.9f);
            Handles.DrawWireDisc(pivot, Vector3.up, r);
            Vector3 radial = new Vector3(Mathf.Sin(azRad), 0f, Mathf.Cos(azRad));
            Handles.color = new Color(0.3f, 0.85f, 0.85f, 0.9f);
            Handles.DrawWireDisc(pivot, Vector3.Cross(radial, Vector3.up), r);

            // Camera marker — drag anywhere to set azimuth / elevation / radius from the new offset.
            float tanYaw = bc.TangentYaw();
            BrazeCamera.OrbitFrame(bc.azimuth + tanYaw, bc.elevation, r, pivot, out Vector3 camPos, out _);
            float hs = HandleUtility.GetHandleSize(camPos) * 0.09f;
            Handles.color = new Color(1f, 0.45f, 0.14f, 1f);
            Handles.DrawLine(pivot, camPos);

            EditorGUI.BeginChangeCheck();
            Vector3 newPos = Handles.FreeMoveHandle(camPos, hs, Vector3.zero, Handles.SphereHandleCap);
            if (EditorGUI.EndChangeCheck())
            {
                Vector3 d = newPos - pivot;
                float newR = d.magnitude;
                if (newR > 1e-3f)
                {
                    Undo.RecordObject(bc, "Orbit Braze Camera");
                    Vector3 dn = d / newR;
                    bc.elevation = Mathf.Asin(Mathf.Clamp(dn.y, -1f, 1f)) * Mathf.Rad2Deg;
                    bc.azimuth = Mathf.Repeat(Mathf.Atan2(dn.x, dn.z) * Mathf.Rad2Deg - tanYaw, 360f);
                    bc.radius = newR;
                    EditorUtility.SetDirty(bc);
                }
            }

            // Pivot handle (only when the pivot IS this object — a Follow Target or a Spline drives it otherwise).
            if (bc.follow == null && bc.path == null)
            {
                EditorGUI.BeginChangeCheck();
                Vector3 newPivot = Handles.PositionHandle(pivot, Quaternion.identity);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(bc.transform, "Move Braze Camera Pivot");
                    bc.transform.position = newPivot - bc.pivotOffset;
                }
            }
        }
    }
}
