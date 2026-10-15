using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Persistent edit-mode player that drives the runtime flipbook renderer.</summary>
    [InitializeOnLoad]
    internal static class BrazeFlipbookPreviewController
    {
        const double TickInterval = 1.0 / 60.0;
        const string PositionKey = "BrazeFX.FlipbookPlayer.ScenePlayer";

        static BrazeFlipbookPlayer player;
        static bool playing;
        static float time;
        static double lastTick;
        static Vector2 panelPosition = new(-1f, -1f);
        static bool panelLoaded;
        static GUIStyle titleStyle, stateStyle, timeStyle;

        static BrazeFlipbookPreviewController()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += StopEditorPreview;
            Selection.selectionChanged += OnSelectionChanged;
        }

        public static bool IsActive(BrazeFlipbookPlayer value) =>
            !Application.isPlaying && player == value && value != null && value.Ready;
        public static bool IsPlaying(BrazeFlipbookPlayer value) => IsActive(value) && playing;
        public static float Time(BrazeFlipbookPlayer value) =>
            Application.isPlaying ? (value != null ? value.PlaybackTime : 0f) : player == value ? time : 0f;

        public static void Play(BrazeFlipbookPlayer value)
        {
            if (value == null) return;
            if (Application.isPlaying) { value.Play(); return; }
            Activate(value);
            if (!value.Repeats && time >= value.Duration) time = 0f;
            value.EditorPreviewAt(time);
            playing = true;
            lastTick = EditorApplication.timeSinceStartup;
            Repaint();
        }

        public static void Pause(BrazeFlipbookPlayer value)
        {
            if (value == null) return;
            if (Application.isPlaying) { value.Pause(); return; }
            if (player == value) playing = false;
            Repaint();
        }

        public static void Restart(BrazeFlipbookPlayer value)
        {
            if (value == null) return;
            if (Application.isPlaying) { value.Restart(); return; }
            Activate(value);
            time = 0f;
            value.EditorPreviewAt(time);
            playing = true;
            lastTick = EditorApplication.timeSinceStartup;
            Repaint();
        }

        public static void Stop(BrazeFlipbookPlayer value)
        {
            if (value == null) return;
            if (Application.isPlaying) { value.Stop(); return; }
            if (player == value) StopEditorPreview();
        }

        public static void SetTime(BrazeFlipbookPlayer value, float valueTime)
        {
            if (value == null || Application.isPlaying) return;
            Activate(value);
            time = Mathf.Max(0f, valueTime);
            playing = false;
            value.EditorPreviewAt(time);
            Repaint();
        }

        static void Activate(BrazeFlipbookPlayer value)
        {
            if (player == value) return;
            if (player != null) player.EditorPreviewStop();
            player = value;
            time = 0f;
            playing = false;
        }

        static void Tick()
        {
            if (!playing || player == null || Application.isPlaying || EditorApplication.isCompiling) return;
            double now = EditorApplication.timeSinceStartup;
            double elapsed = now - lastTick;
            if (elapsed < TickInterval) return;
            lastTick = now;
            time += Mathf.Clamp((float)elapsed, 0f, 1f / 20f) * Mathf.Max(0f, player.timeScale);
            if (!player.Repeats && time >= player.Duration)
            {
                time = player.Duration;
                playing = false;
                if (!player.KeepsVisibleAtEnd)
                {
                    StopEditorPreview();
                    return;
                }
            }
            player.EditorPreviewAt(time);
            Repaint();
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) StopEditorPreview();
        }

        static void OnSelectionChanged()
        {
            if (player == null || Application.isPlaying) return;
            if (Selection.activeGameObject == null ||
                Selection.activeGameObject.GetComponent<BrazeFlipbookPlayer>() != player)
                StopEditorPreview();
        }

        static void StopEditorPreview()
        {
            playing = false;
            if (player != null && !Application.isPlaying) player.EditorPreviewStop();
            player = null;
            time = 0f;
            Repaint();
        }

        static void Repaint() => SceneView.RepaintAll();

        public static void DrawInspector(BrazeFlipbookPlayer value, string title)
        {
            if (value == null) return;
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label(title, EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("▶ Play", EditorStyles.miniButtonLeft)) Play(value);
            if (GUILayout.Button("Ⅱ Pause", EditorStyles.miniButtonMid)) Pause(value);
            if (GUILayout.Button("↻ Restart", EditorStyles.miniButtonMid)) Restart(value);
            if (GUILayout.Button("■ Stop", EditorStyles.miniButtonRight)) Stop(value);
            EditorGUILayout.EndHorizontal();

            float duration = Mathf.Max(value.Duration, 0.01f);
            float current = Time(value);
            float displayed = value.Repeats ? Mathf.Repeat(current, duration) : Mathf.Clamp(current, 0f, duration);
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                EditorGUI.BeginChangeCheck();
                float scrubbed = BrazeEditorGUI.Slider("Preview Time", displayed, 0f, duration);
                if (EditorGUI.EndChangeCheck()) SetTime(value, scrubbed);
            }

            string state = Application.isPlaying ? "Runtime" : IsPlaying(value) ? "Playing"
                : IsActive(value) ? "Paused" : "Stopped";
            EditorGUILayout.LabelField(
                $"{state}  ·  {current:0.00}s / {value.Duration:0.00}s  ·  real renderer",
                EditorStyles.miniLabel);
            EditorGUILayout.EndVertical();
        }

        public static void DrawSceneControls(BrazeFlipbookPlayer value)
        {
            if (value == null || Application.isPlaying) return;
            EnsureStyles();
            var scene = SceneView.currentDrawingSceneView;
            float viewWidth = scene != null ? scene.position.width : 900f;
            float viewHeight = scene != null ? scene.position.height : 600f;
            const float width = 300f, height = 66f, margin = 12f;
            if (!panelLoaded)
            {
                panelPosition = new Vector2(EditorPrefs.GetFloat(PositionKey + ".x", -1f),
                    EditorPrefs.GetFloat(PositionKey + ".y", -1f));
                panelLoaded = true;
            }
            if (panelPosition.x < 0f || panelPosition.y < 0f)
                panelPosition = new Vector2(Mathf.Max(margin, (viewWidth - width) * 0.5f),
                    Mathf.Max(34f, viewHeight - height - 28f));
            panelPosition.x = Mathf.Clamp(panelPosition.x, 0f, Mathf.Max(0f, viewWidth - width));
            panelPosition.y = Mathf.Clamp(panelPosition.y, 24f, Mathf.Max(24f, viewHeight - height - 18f));
            var panel = new Rect(panelPosition.x, panelPosition.y, width, height);

            Handles.BeginGUI();
            var dragRect = new Rect(panel.x, panel.y, panel.width, 21f);
            EditorGUIUtility.AddCursorRect(dragRect, MouseCursor.Pan);
            int dragId = GUIUtility.GetControlID("BrazeFlipbookPlayerDrag".GetHashCode(), FocusType.Passive, dragRect);
            Event e = Event.current;
            switch (e.GetTypeForControl(dragId))
            {
                case EventType.MouseDown:
                    if (e.button == 0 && dragRect.Contains(e.mousePosition)) { GUIUtility.hotControl = dragId; e.Use(); }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == dragId) { panelPosition += e.delta; panel.position = panelPosition; e.Use(); }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == dragId)
                    {
                        GUIUtility.hotControl = 0;
                        EditorPrefs.SetFloat(PositionKey + ".x", panelPosition.x);
                        EditorPrefs.SetFloat(PositionKey + ".y", panelPosition.y);
                        e.Use();
                    }
                    break;
            }

            var radius = new Vector4(6f, 6f, 6f, 6f);
            RoundRect(new Rect(panel.x + 2f, panel.y + 3f, width, height), new Color(0f, 0f, 0f, 0.28f), radius);
            RoundRect(panel, new Color(0.085f, 0.09f, 0.10f, 0.97f), radius);
            RoundRect(new Rect(panel.x, panel.y, 3f, height), new Color(1f, 0.45f, 0.14f, 0.9f),
                new Vector4(6f, 0f, 0f, 6f));
            GUI.Label(new Rect(panel.x + 10f, panel.y + 3f, 190f, 18f), "≡  FLIPBOOK PLAYER", titleStyle);
            string state = IsPlaying(value) ? "● PLAYING" : IsActive(value) ? "Ⅱ PAUSED" : "■ STOPPED";
            GUI.Label(new Rect(panel.xMax - 92f, panel.y + 3f, 82f, 18f), state, stateStyle);

            GUILayout.BeginArea(new Rect(panel.x + 9f, panel.y + 25f, panel.width - 18f, 20f));
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("▶ Play", EditorStyles.miniButtonLeft)) Play(value);
            if (GUILayout.Button("Ⅱ Pause", EditorStyles.miniButtonMid)) Pause(value);
            if (GUILayout.Button("↻ Restart", EditorStyles.miniButtonMid)) Restart(value);
            if (GUILayout.Button("■ Stop", EditorStyles.miniButtonRight)) Stop(value);
            EditorGUILayout.EndHorizontal();
            GUILayout.EndArea();
            GUI.Label(new Rect(panel.x + 10f, panel.y + 46f, panel.width - 20f, 16f),
                $"Real Renderer  ·  {Time(value):0.00}s / {value.Duration:0.00}s", timeStyle);
            Handles.EndGUI();
        }

        static void EnsureStyles()
        {
            if (titleStyle != null) return;
            titleStyle = new GUIStyle(EditorStyles.miniBoldLabel) { fontSize = 10, alignment = TextAnchor.MiddleLeft };
            titleStyle.normal.textColor = new Color(1f, 0.56f, 0.25f);
            stateStyle = new GUIStyle(EditorStyles.miniBoldLabel) { fontSize = 9, alignment = TextAnchor.MiddleRight };
            timeStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleRight };
            timeStyle.normal.textColor = new Color(0.66f, 0.68f, 0.72f);
        }

        static void RoundRect(Rect rect, Color color, Vector4 radii) =>
            GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, color, Vector4.zero, radii);
    }
}
