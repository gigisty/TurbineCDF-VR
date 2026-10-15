using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Mature scene editor for <see cref="BrazeSpline"/> (Dreamteck-style): Move / Add / Delete modes, point
    /// selection, per-point Bezier tangent handles (mirror / align / broken), plane-locked dragging, presets.</summary>
    [CustomEditor(typeof(BrazeSpline))]
    public sealed class BrazeSplineEditor : Editor
    {
        enum EditMode { Move, Add, Delete }

        // Above this control-point count, per-point scene handles are suppressed (a wrapped/procedural spline has
        // hundreds of points → one Handle each is unusable and tanks the Scene view).
        const int MaxInteractivePoints = 256;

        readonly List<int> selection = new List<int>();
        // Single-point ops (tangents, type, smooth) act on the "primary" = the only selected point (else none).
        int selected { get => selection.Count == 1 ? selection[0] : -1; set { selection.Clear(); if (value >= 0) selection.Add(value); } }
        bool IsSel(int i) => selection.Contains(i);
        void ToggleSel(int i) { if (!selection.Remove(i)) selection.Add(i); }
        void SelectAllPoints(BrazeSpline s) { selection.Clear(); for (int i = 0; i < s.points.Count; i++) selection.Add(i); }
        void SelectInverse(BrazeSpline s) { var keep = new List<int>(); for (int i = 0; i < s.points.Count; i++) if (!selection.Contains(i)) keep.Add(i); selection.Clear(); selection.AddRange(keep); }

        EditMode mode = EditMode.Move;
        static readonly string[] ModeNames = { "Move", "Add", "Remove" };
        // Index 0 = Custom (edited); entries 1.. map to SplinePreset 0.. The popup shows the active preset.
        static readonly string[] PresetNames = { "Custom", "Line", "Arc", "S-Curve", "Wave", "Circle", "Spiral", "Helix" };

        static int PresetCount(BrazeSpline.SplinePreset p) => p switch
        {
            BrazeSpline.SplinePreset.Line => 4,
            BrazeSpline.SplinePreset.Arc => 6,
            BrazeSpline.SplinePreset.SCurve => 6,
            BrazeSpline.SplinePreset.Wave => 9,
            BrazeSpline.SplinePreset.Circle => 8,
            BrazeSpline.SplinePreset.Spiral => 18,
            BrazeSpline.SplinePreset.Helix => 18,
            _ => 6
        };

        void OnEnable() { var s = (BrazeSpline)target; selected = (s.points != null && s.points.Count > 0) ? 0 : -1; }

        // ── Inspector ────────────────────────────────────────────────────────────────────────────────────
        public override void OnInspectorGUI()
        {
            var s = (BrazeSpline)target;

            // Esc exits Add/Remove even while the Inspector (not the Scene view) has keyboard focus.
            if (mode != EditMode.Move && Event.current.type == EventType.KeyDown &&
                Event.current.keyCode == KeyCode.Escape)
            {
                mode = EditMode.Move;
                Event.current.Use();
                SceneView.RepaintAll();
            }

            serializedObject.Update();

            BrazeEditorGUI.PillsEnum("Interpolation", serializedObject.FindProperty("interpolation"));
            var oldProj = (BrazeSpline.ProjectionPlane)serializedObject.FindProperty("projection").enumValueIndex;
            BrazeEditorGUI.PillsEnum("Projection", serializedObject.FindProperty("projection"));
            BrazeEditorGUI.PropertyField(serializedObject.FindProperty("closed"), new GUIContent("Closed Loop"));
            BrazeEditorGUI.PropertyField(serializedObject.FindProperty("samplesPerSegment"), new GUIContent("Smoothness"));
            BrazeEditorGUI.PropertyField(serializedObject.FindProperty("pointsDistance"), new GUIContent("Points Distance"));
            serializedObject.ApplyModifiedProperties();

            // Changing the projection plane re-orients the whole spline into the new plane.
            if (s.projection != oldProj) ReorientToPlane(s, oldProj, s.projection);

            BrazeEditorGUI.Separator();
            // Shows the active preset; pick another to apply it immediately. "Custom" = points were edited.
            int shown = Mathf.Clamp(s.activePreset + 1, 0, PresetNames.Length - 1);
            EditorGUI.BeginChangeCheck();
            int presetSel = EditorGUILayout.Popup("Preset", shown, PresetNames);
            if (EditorGUI.EndChangeCheck() && presetSel != shown)
            {
                if (presetSel > 0)
                {
                    var preset = (BrazeSpline.SplinePreset)(presetSel - 1);
                    Undo.RecordObject(s, "Apply Spline Preset");
                    s.ApplyPreset(preset, PresetCount(preset));
                    selected = Mathf.Clamp(selected, 0, s.points.Count - 1);
                }
                else { Undo.RecordObject(s, "Custom Spline"); s.activePreset = -1; }
                EditorUtility.SetDirty(s); SceneView.RepaintAll();
            }

            BrazeEditorGUI.Separator();
            BrazeEditorGUI.SubLabel("Points");
            EditorGUILayout.BeginHorizontal();
            DrawSelectDropdown(s);
            // Smooth only affects Bezier tangents (Catmull-Rom/Linear have nothing to smooth).
            using (new EditorGUI.DisabledScope(selection.Count == 0 || s.interpolation != BrazeSpline.Interp.Bezier))
                if (GUILayout.Button(new GUIContent("Smooth", "Reset the selected Bezier points to smooth (Mirrored) " +
                    "tangents. Catmull-Rom is already smooth."), GUILayout.Width(70))) SmoothSelected(s);
            EditorGUILayout.EndHorizontal();
            string selLabel = selection.Count == 0 ? "None selected"
                            : selection.Count == 1 ? $"Selected: point {selection[0] + 1} / {s.points.Count}"
                            : $"Selected: {selection.Count} points";
            EditorGUILayout.LabelField(selLabel, BrazeEditorGUI.StyleFromSkin(() => EditorStyles.miniLabel));
            EditorGUILayout.BeginHorizontal();
            // Add Point activates the scene Add tool, which then waits for you to click on the projection plane
            // (it sniffs intent: extend an end or insert mid). Toggles off if already active.
            bool adding = mode == EditMode.Add;
            GUI.backgroundColor = adding ? new Color(0.4f, 1f, 0.6f) : Color.white;
            if (GUILayout.Button(adding ? "Adding… (click in scene)" : "Add Point"))
            { mode = adding ? EditMode.Move : EditMode.Add; FocusScene(); }

            // Remove Point activates the scene Remove tool: hover locks onto the nearest point, click removes it.
            // Only ENTERING is gated by the 2-point minimum — the active toggle must always stay clickable,
            // otherwise removing down to 2 points strands it on with no way off.
            bool deleting = mode == EditMode.Delete;
            GUI.backgroundColor = deleting ? new Color(1f, 0.45f, 0.4f) : Color.white;
            using (new EditorGUI.DisabledScope(!deleting && s.points.Count <= 2))
                if (GUILayout.Button(deleting ? "Removing… (click a point)" : "Remove Point"))
                { mode = deleting ? EditMode.Move : EditMode.Delete; FocusScene(); }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            // Per-point tangent type (Bezier) — Dreamteck-style, set on the selected point.
            using (new EditorGUI.DisabledScope(s.interpolation != BrazeSpline.Interp.Bezier || selected < 0 || selected >= s.points.Count))
            {
                var cur = (selected >= 0 && selected < s.points.Count) ? s.GetPointType(selected) : BrazeSpline.PointType.Mirrored;
                EditorGUI.BeginChangeCheck();
                var nt = (BrazeSpline.PointType)EditorGUILayout.EnumPopup("Point Type", cur);
                if (EditorGUI.EndChangeCheck() && selected >= 0)
                {
                    Undo.RecordObject(s, "Point Type");
                    s.SetPointType(selected, nt);
                    EditorUtility.SetDirty(s); SceneView.RepaintAll();
                }
            }

            serializedObject.Update();
            BrazeEditorGUI.PropertyField(serializedObject.FindProperty("points"), new GUIContent("Point List"), true);
            if (serializedObject.ApplyModifiedProperties()) { s.EnsureHandles(); s.version++; SceneView.RepaintAll(); }

            BrazeEditorGUI.Hint("Add Point waits for you to click on the projection plane: hover near an END to extend " +
                                "it, or beside the MIDDLE to insert into that segment (a ghost shows where it lands). " +
                                "Remove waits too: hover locks onto the nearest point (red ring), click to delete. " +
                                "Move = select & drag (Bezier shows tangent handles).");
        }

        // ── Scene ────────────────────────────────────────────────────────────────────────────────────────
        void OnSceneGUI()
        {
            var s = (BrazeSpline)target;
            if (s.points == null) return;
            var t = s.transform;
            var e = Event.current;

            // Esc exits Add/Remove back to Move.
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape && mode != EditMode.Move)
            {
                mode = EditMode.Move;
                if (SceneView.currentDrawingSceneView != null) SceneView.currentDrawingSceneView.Repaint();
                e.Use();
                return;
            }

            DrawToolbar();
            DrawCurve(s);

            // Delete mode cannot act at the 2-point minimum — leaving it armed would strand the toggle
            // (this also covers undo or Point List edits shrinking the spline while the tool is active).
            if (mode == EditMode.Delete && s.points.Count <= 2) mode = EditMode.Move;

            if (mode == EditMode.Add) { HandleAddMode(s, t, e); return; }
            if (mode == EditMode.Delete) { HandleDeleteMode(s, e); return; }

            // Tangent handles only for a single Bezier selection.
            if (selected >= 0 && s.interpolation == BrazeSpline.Interp.Bezier) DrawTangents(s, t, selected, s.PlaneNormal());

            // UNSELECTED points → click to select (Shift adds to a multi-selection). Skipped on very dense splines
            // (e.g. a surface wrap with hundreds of points): drawing a Handle per point is unusable AND tanks the
            // Scene view. Such splines are procedurally placed, not hand-edited point-by-point.
            if (s.points.Count <= MaxInteractivePoints)
                for (int i = 0; i < s.points.Count; i++)
                {
                    if (IsSel(i)) continue;
                    Vector3 w = s.PointWorld(i);
                    float sz = HandleUtility.GetHandleSize(w);
                    Handles.color = PointFill;
                    if (Handles.Button(w, Quaternion.identity, sz * 0.1f, sz * 0.13f, PointCap))
                    {
                        if (Event.current.shift) ToggleSel(i); else selected = i;
                        Repaint();
                    }
                }

            if (selection.Count == 0) return;

            // Single selection → an axis PositionHandle on the point (precise placement).
            if (selection.Count == 1)
            {
                int i = selection[0];
                Vector3 w = s.PointWorld(i);
                Handles.color = SelFill;
                SelectedPointCap(0, w, Quaternion.identity, HandleUtility.GetHandleSize(w) * 0.1f, EventType.Repaint);
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.PositionHandle(w, t.rotation);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(s, "Move Spline Point");
                    s.points[i] = s.WorldToUnscaledLocal(moved);
                    s.version++; EditorUtility.SetDirty(s);
                }
                return;
            }

            // Multi-selection → EACH selected point is a draggable grip; dragging any one moves the WHOLE selection.
            // Plus a centroid PositionHandle for axis-precise group moves. (Free 3D — repositioning isn't plane-locked.)
            for (int k = 0; k < selection.Count; k++)
            {
                int i = selection[k];
                Vector3 w = s.PointWorld(i);
                Handles.color = SelFill;
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.FreeMoveHandle(w, HandleUtility.GetHandleSize(w) * 0.1f, Vector3.zero, SelectedPointCap);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(s, "Move Spline Points");
                    ApplyGroupDelta(s, s.WorldToUnscaledLocal(moved) - s.points[i]);
                    return;
                }
            }

            Vector3 avg = Vector3.zero;
            for (int k = 0; k < selection.Count; k++) avg += s.points[selection[k]];
            avg /= selection.Count;
            EditorGUI.BeginChangeCheck();
            Vector3 cMoved = Handles.PositionHandle(s.ScaledLocalToWorld(avg), t.rotation);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(s, "Move Spline Points");
                ApplyGroupDelta(s, s.WorldToUnscaledLocal(cMoved) - avg);
            }
        }

        void ApplyGroupDelta(BrazeSpline s, Vector3 deltaLocal)
        {
            for (int k = 0; k < selection.Count; k++) s.points[selection[k]] += deltaLocal;
            s.version++; EditorUtility.SetDirty(s);
        }

        // Rotate every point + tangent handle from the old plane's basis into the new one.
        void ReorientToPlane(BrazeSpline s, BrazeSpline.ProjectionPlane from, BrazeSpline.ProjectionPlane to)
        {
            Undo.RecordObject(s, "Reorient Spline");
            s.EnsureHandles();
            for (int i = 0; i < s.points.Count; i++)
            {
                s.points[i] = BrazeSpline.Reproject(s.points[i], from, to);
                s.inHandles[i] = BrazeSpline.Reproject(s.inHandles[i], from, to);
                s.outHandles[i] = BrazeSpline.Reproject(s.outHandles[i], from, to);
            }
            s.version++; EditorUtility.SetDirty(s); SceneView.RepaintAll();
        }

        // Reset the selected points' Bezier handles to auto (smooth) tangents.
        void SmoothSelected(BrazeSpline s)
        {
            Undo.RecordObject(s, "Smooth Points");
            s.EnsureHandles();
            for (int k = 0; k < selection.Count; k++)
            {
                int i = selection[k];
                if (i < 0 || i >= s.points.Count) continue;
                s.inHandles[i] = s.outHandles[i] = Vector3.zero;       // auto (Catmull-Rom) tangent
                s.pointTypes[i] = (int)BrazeSpline.PointType.Mirrored; // and a smooth point type
            }
            s.version++; EditorUtility.SetDirty(s); SceneView.RepaintAll();
        }

        // Dreamteck-style Select control: All / None / Inverse / a specific Point. Shows the current selection.
        void DrawSelectDropdown(BrazeSpline s)
        {
            int n = s.points.Count;
            var opts = new string[4 + n];
            opts[0] = "- - -"; opts[1] = "All"; opts[2] = "None"; opts[3] = "Inverse";
            for (int i = 0; i < n; i++) opts[4 + i] = "Point " + (i + 1);

            int shown = selection.Count == 0 ? 2
                      : selection.Count == n && n > 1 ? 1
                      : selection.Count == 1 ? 4 + Mathf.Clamp(selection[0], 0, Mathf.Max(n - 1, 0))
                      : 0;

            EditorGUI.BeginChangeCheck();
            int sel = EditorGUILayout.Popup("Select", Mathf.Clamp(shown, 0, opts.Length - 1), opts);
            if (EditorGUI.EndChangeCheck() && sel != shown)
            {
                if (sel == 1) SelectAllPoints(s);
                else if (sel == 2) selection.Clear();
                else if (sel == 3) SelectInverse(s);
                else if (sel >= 4) selected = sel - 4;
                SceneView.RepaintAll(); Repaint();
            }
        }

        // Focus the Scene view so keyboard shortcuts (Esc to exit a tool) reach OnSceneGUI even when the tool was
        // activated from the Inspector button.
        static void FocusScene()
        {
            if (SceneView.lastActiveSceneView != null) SceneView.lastActiveSceneView.Focus();
            SceneView.RepaintAll();
        }

        void DrawToolbar()
        {
            Handles.BeginGUI();
            GUILayout.BeginArea(new Rect(10, 10, 220, 24));
            mode = (EditMode)GUILayout.Toolbar((int)mode, ModeNames);
            GUILayout.EndArea();
            Handles.EndGUI();
        }

        // ── Point gizmos: flat, camera-facing FILLED CIRCLES (replace the old square DotHandleCap). ──────────
        static readonly Color PointFill = Color.white;
        static readonly Color PointEdge = new Color(0f, 0f, 0f, 0.4f);          // subtle outline for definition
        static readonly Color SelFill   = new Color(0.55f, 0.78f, 1f, 1f);      // light-blue selected fill
        static readonly Color SelEdge   = new Color(0.15f, 0.5f, 1f, 1f);       // strong blue selected edge

        static Vector3 DiscNormal() =>
            SceneView.currentDrawingSceneView != null ? -SceneView.currentDrawingSceneView.camera.transform.forward : Vector3.forward;

        // Filled circle, fill = Handles.color, with a faint dark outline. Used for UNSELECTED / Add / Delete dots.
        static void PointCap(int id, Vector3 pos, Quaternion rot, float size, EventType ev)
        {
            if (ev == EventType.Repaint)
            {
                Vector3 n = DiscNormal(); Color c = Handles.color;
                Handles.DrawSolidDisc(pos, n, size);
                Handles.color = PointEdge; Handles.DrawWireDisc(pos, n, size, 1.5f);
                Handles.color = c;
            }
            else if (ev == EventType.Layout) HandleUtility.AddControl(id, HandleUtility.DistanceToCircle(pos, size));
        }

        // Filled circle, fill = Handles.color, with a BLUE edge ring. Used for SELECTED points.
        static void SelectedPointCap(int id, Vector3 pos, Quaternion rot, float size, EventType ev)
        {
            if (ev == EventType.Repaint)
            {
                Vector3 n = DiscNormal(); Color c = Handles.color;
                Handles.DrawSolidDisc(pos, n, size);
                Handles.color = SelEdge; Handles.DrawWireDisc(pos, n, size, 2.5f);
                Handles.color = c;
            }
            else if (ev == EventType.Layout) HandleUtility.AddControl(id, HandleUtility.DistanceToCircle(pos, size));
        }

        static void DrawCurve(BrazeSpline s)
        {
            if (s.points.Count < 2) return;
            Handles.color = new Color(1f, 0.7f, 0.2f, 1f);
            int count = s.GizmoSampleCount;
            Vector3 prev = s.EvaluateWorld(0f);
            for (int i = 1; i < count; i++)
            {
                Vector3 cur = s.EvaluateWorld(i / (float)(count - 1));
                Handles.DrawLine(prev, cur, 2f);
                prev = cur;
            }
        }

        // Bezier tangent handles for the selected point, with mirror/align/broken coupling + plane lock.
        void DrawTangents(BrazeSpline s, Transform t, int i, Vector3 normal)
        {
            s.EnsureHandles();
            Vector3 baseLocal = s.points[i];
            DrawOneTangent(s, t, i, normal, baseLocal, isOut: true);
            DrawOneTangent(s, t, i, normal, baseLocal, isOut: false);
        }

        void DrawOneTangent(BrazeSpline s, Transform t, int i, Vector3 normal, Vector3 baseLocal, bool isOut)
        {
            Vector3 off = isOut ? s.OutHandle(i) : s.InHandle(i);
            Vector3 world = s.ScaledLocalToWorld(baseLocal + off);
            Vector3 anchor = s.ScaledLocalToWorld(baseLocal);
            float size = HandleUtility.GetHandleSize(world);

            Handles.color = new Color(0.5f, 0.9f, 0.5f, 0.9f);
            Handles.DrawLine(anchor, world);
            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.FreeMoveHandle(world, size * 0.08f, Vector3.zero, Handles.SphereHandleCap);
            if (!EditorGUI.EndChangeCheck()) return;

            Undo.RecordObject(s, "Edit Tangent");
            Vector3 newOff = s.WorldToUnscaledLocal(moved) - baseLocal;
            if (normal != Vector3.zero) newOff -= Vector3.Dot(newOff, normal) * normal;   // keep in plane

            if (isOut) { s.outHandles[i] = newOff; ApplyCoupling(s, i, newOff, toOut: false); }
            else       { s.inHandles[i] = newOff;  ApplyCoupling(s, i, newOff, toOut: true); }
            s.version++; EditorUtility.SetDirty(s);
        }

        // Update the opposite handle per the handle mode (driven by the just-edited handle offset `src`).
        static void ApplyCoupling(BrazeSpline s, int i, Vector3 src, bool toOut)
        {
            var type = s.GetPointType(i);
            if (type == BrazeSpline.PointType.Broken) return;
            Vector3 cur = toOut ? s.outHandles[i] : s.inHandles[i];
            float len = (type == BrazeSpline.PointType.Free && cur.sqrMagnitude > 1e-8f) ? cur.magnitude : src.magnitude;
            Vector3 opp = src.sqrMagnitude > 1e-8f ? -src.normalized * len : Vector3.zero;
            if (toOut) s.outHandles[i] = opp; else s.inHandles[i] = opp;
        }

        // Add mode: hover on the projection plane, we sniff intent — beyond the start/end → extend that end; beside the
        // middle → insert into that segment. Live ghost + connection preview; click to commit.
        void HandleAddMode(BrazeSpline s, Transform t, Event e)
        {
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            if (e.type == EventType.MouseMove && SceneView.currentDrawingSceneView != null) SceneView.currentDrawingSceneView.Repaint();

            // Existing points keep the SAME white dots as Move mode (only the green ghost below is new).
            Handles.color = PointFill;
            for (int i = 0; i < s.points.Count; i++)
            {
                Vector3 wp = s.PointWorld(i);
                PointCap(0, wp, Quaternion.identity, HandleUtility.GetHandleSize(wp) * 0.09f, EventType.Repaint);
            }

            if (!ProjectCursorToPlane(s, t, e.mousePosition, out Vector3 cursorWorld)) return;
            int insertIndex = DetermineInsertIndex(s, cursorWorld);

            // Preview: ghost point + the connection(s) it will make.
            Handles.color = new Color(0.4f, 1f, 0.6f, 1f);
            float gs = HandleUtility.GetHandleSize(cursorWorld);
            Handles.SphereHandleCap(0, cursorWorld, Quaternion.identity, gs * 0.11f, EventType.Repaint);
            if (s.points.Count >= 1)
            {
                if (insertIndex <= 0) Handles.DrawDottedLine(cursorWorld, s.PointWorld(0), 4f);
                else if (insertIndex >= s.points.Count) Handles.DrawDottedLine(cursorWorld, s.PointWorld(s.points.Count - 1), 4f);
                else { Handles.DrawDottedLine(s.PointWorld(insertIndex - 1), cursorWorld, 4f); Handles.DrawDottedLine(cursorWorld, s.PointWorld(insertIndex), 4f); }
            }

            if (e.type == EventType.MouseDown && e.button == 0)
            {
                Undo.RecordObject(s, "Add Spline Point");
                DoInsert(s, insertIndex, s.WorldToUnscaledLocal(cursorWorld));
                e.Use();
            }
        }

        // Delete mode: locks onto the nearest EXISTING point on hover (highlight ring), click removes it.
        void HandleDeleteMode(BrazeSpline s, Event e)
        {
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
            if (e.type == EventType.MouseMove && SceneView.currentDrawingSceneView != null) SceneView.currentDrawingSceneView.Repaint();

            int nearest = NearestPointToMouse(s, e.mousePosition);
            Vector3 camFwd = SceneView.currentDrawingSceneView != null ? SceneView.currentDrawingSceneView.camera.transform.forward : Vector3.forward;

            for (int i = 0; i < s.points.Count; i++)
            {
                Vector3 w = s.PointWorld(i);
                float size = HandleUtility.GetHandleSize(w);
                // Keep the SAME white point dots as Move mode (so nothing looks removed) — only overlay a red lock
                // ring on the point the cursor is over.
                Handles.color = PointFill;
                PointCap(0, w, Quaternion.identity, size * 0.09f, EventType.Repaint);
                if (i == nearest)
                {
                    Handles.color = new Color(1f, 0.3f, 0.25f, 1f);
                    Handles.DrawWireDisc(w, camFwd, size * 0.2f, 3.5f);   // lock ring (thicker)
                }
            }

            if (e.type == EventType.MouseDown && e.button == 0 && nearest >= 0)
            {
                if (s.points.Count > 2) RemovePoint(s, nearest);
                // Reached the 2-point minimum: disarm immediately so the tool never sits armed but unable to act.
                if (s.points.Count <= 2) { mode = EditMode.Move; Repaint(); }
                e.Use();
            }
        }

        // Index of the point whose screen position is closest to the mouse (always locks to a real point).
        static int NearestPointToMouse(BrazeSpline s, Vector2 mouse)
        {
            int best = -1; float bestD = float.MaxValue;
            for (int i = 0; i < s.points.Count; i++)
            {
                float d = (HandleUtility.WorldToGUIPoint(s.PointWorld(i)) - mouse).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        // Raycast the mouse onto the projection plane (through the spline origin). Free → a camera-facing plane.
        static bool ProjectCursorToPlane(BrazeSpline s, Transform t, Vector2 mouse, out Vector3 world)
        {
            Vector3 nrmLocal = s.PlaneNormal();
            Vector3 normal = nrmLocal != Vector3.zero ? t.TransformDirection(nrmLocal).normalized
                           : (SceneView.lastActiveSceneView != null ? -SceneView.lastActiveSceneView.camera.transform.forward : Vector3.up);
            var plane = new Plane(normal, t.position);
            Ray ray = HandleUtility.GUIPointToWorldRay(mouse);
            if (plane.Raycast(ray, out float d)) { world = ray.GetPoint(d); return true; }
            world = default; return false;
        }

        // Intent: project the cursor onto the curve; near the start/end → extend (prepend/append), otherwise insert
        // into the nearest segment.
        static int DetermineInsertIndex(BrazeSpline s, Vector3 cursorWorld)
        {
            if (s.points.Count < 2) return s.points.Count;
            float tc = s.Project(cursorWorld, out _);
            float eps = 0.5f / Mathf.Max(s.SampleCount - 1, 1);
            if (!s.closed && tc <= eps) return 0;                       // extend start
            if (!s.closed && tc >= 1f - eps) return s.points.Count;     // extend end
            int seg = Mathf.Clamp(Mathf.FloorToInt(tc * s.SegmentCount), 0, s.SegmentCount - 1);
            return seg + 1;                                            // insert mid
        }

        // ── Point ops ────────────────────────────────────────────────────────────────────────────────────
        void DoInsert(BrazeSpline s, int index, Vector3 local)
        {
            index = Mathf.Clamp(index, 0, s.points.Count);
            s.points.Insert(index, local);
            s.inHandles.Insert(Mathf.Min(index, s.inHandles.Count), Vector3.zero);
            s.outHandles.Insert(Mathf.Min(index, s.outHandles.Count), Vector3.zero);
            s.pointTypes.Insert(Mathf.Min(index, s.pointTypes.Count), 0);
            s.EnsureHandles();
            s.activePreset = -1;   // structurally diverged → Custom
            selected = index;
            s.version++; EditorUtility.SetDirty(s); SceneView.RepaintAll();
        }

        void RemovePoint(BrazeSpline s, int index)
        {
            if (s.points.Count <= 2 || index < 0 || index >= s.points.Count) return;
            Undo.RecordObject(s, "Remove Spline Point");
            s.points.RemoveAt(index);
            if (index < s.inHandles.Count) s.inHandles.RemoveAt(index);
            if (index < s.outHandles.Count) s.outHandles.RemoveAt(index);
            if (index < s.pointTypes.Count) s.pointTypes.RemoveAt(index);
            s.EnsureHandles();
            s.activePreset = -1;   // structurally diverged → Custom
            selected = Mathf.Clamp(selected, 0, s.points.Count - 1);
            s.version++; EditorUtility.SetDirty(s); SceneView.RepaintAll();
        }
    }
}
