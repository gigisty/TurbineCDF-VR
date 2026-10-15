using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>
    /// The BrazeFX timeline editor — a branded (dark / orange) dope-sheet + curve editor for
    /// <see cref="BrazeTimeline"/>. Custom-drawn ruler, lanes, diamond keys and a curve graph; NOT
    /// Unity's stock Animation window.
    ///
    /// GameObjects/components are explicitly dropped into the window as cards. Parameter discovery, recording and
    /// the Add browser are scoped to those cards, so large scenes never become one global parameter jungle.
    /// </summary>
    public sealed class BrazeTimelineWindow : EditorWindow
    {
        // ── theme (matches BrazeEditorGUI) ──
        static readonly Color Accent      = new Color(1.00f, 0.45f, 0.14f);
        static readonly Color BgToolbar   = new Color(0.075f, 0.078f, 0.088f);
        static readonly Color BgPanel     = new Color(0.128f, 0.133f, 0.150f);
        static readonly Color BgGraph     = new Color(0.092f, 0.096f, 0.108f);
        static readonly Color RowA        = new Color(0.132f, 0.137f, 0.154f);
        static readonly Color RowB        = new Color(0.112f, 0.117f, 0.132f);
        static readonly Color RowSel      = new Color(1.00f, 0.45f, 0.14f, 0.10f);
        static readonly Color Grid        = new Color(1f, 1f, 1f, 0.045f);
        static readonly Color GridStrong  = new Color(1f, 1f, 1f, 0.085f);
        static readonly Color Divider     = new Color(0f, 0f, 0f, 0.35f);
        static readonly Color TextDim     = new Color(0.50f, 0.51f, 0.54f);
        static readonly Color TextStrong  = new Color(0.86f, 0.88f, 0.92f);
        static readonly Color PlayModeVeil = new Color(0.34f, 0.35f, 0.38f, 0.34f);

        const float ToolbarH = 62f, DefaultLeftW = 270f, MinLeftW = 210f, MaxLeftW = 560f, MinGraphW = 180f;
        const float SplitterW = 7f, RulerH = 26f, TabH = 26f, RowH = 24f, LM = 10f, RM = 12f;
        const string LeftWidthPref = "BrazeFX.Timeline.LeftPanelWidth";

        enum Tab { Dopesheet, Curves }
        enum DragKind { None, Pan, Playhead, MoveSel, ScaleSel, Marquee, ResizeTracks }

        readonly struct TimelineKeyState
        {
            public readonly Keyframe key;
            public readonly AnimationUtility.TangentMode leftMode;
            public readonly AnimationUtility.TangentMode rightMode;
            public readonly bool broken;

            public TimelineKeyState(Keyframe key, AnimationUtility.TangentMode leftMode,
                AnimationUtility.TangentMode rightMode, bool broken)
            {
                this.key = key;
                this.leftMode = leftMode;
                this.rightMode = rightMode;
                this.broken = broken;
            }

            public TimelineKeyState AtTime(float time)
            {
                Keyframe moved = key;
                moved.time = time;
                return new TimelineKeyState(moved, leftMode, rightMode, broken);
            }

            public TimelineKeyState WithKey(Keyframe replacement)
                => new TimelineKeyState(replacement, leftMode, rightMode, broken);
        }

        readonly struct CopiedTimelineKey
        {
            public readonly int sourceTrack;
            public readonly string targetId;
            public readonly string bindingKey;
            public readonly TimelineKeyState state;
            public readonly float offset;

            public CopiedTimelineKey(int sourceTrack, BrazeTrack track, TimelineKeyState state, float offset)
            {
                this.sourceTrack = sourceTrack;
                targetId = track.TargetId;
                bindingKey = track.BindingKey;
                this.state = state;
                this.offset = offset;
            }
        }

        BrazeTimeline timeline;
        [SerializeField] string boundTimelineGlobalId;
        const string BoundTimelineSessionKey = "BrazeFX.Timeline.BoundGlobalObjectId";
        const string BoundTimelinePathSessionKey = "BrazeFX.Timeline.BoundHierarchyPath";
        const int RebindRetryFrames = 120;
        int rebindFramesRemaining;
        Tab tab = Tab.Dopesheet;
        int selectedTrack = -1;
        readonly HashSet<int> selectedTracks = new();  // multi-selected tracks (track indices) — shift/ctrl-click
        int selectedKey = -1;                          // primary (last-clicked) key — for the context menu
        readonly HashSet<(int track, int key)> selKeys = new();   // multi-selection
        readonly List<CopiedTimelineKey> copiedKeys = new();

        // Component-card layout. A dropped GameObject contributes one separate card per eligible Braze component.
        const float GroupHeaderH = 28f, CardGap = 7f;
        readonly List<int> rowOrder = new();    // slot → track index (grouped by target, container first)
        readonly List<float> rowTop = new();    // slot → content-space Y top (header space already folded in)
        readonly List<bool> rowHeader = new();  // slot → true if a group header sits directly above this row
        readonly List<BrazeTimelineTarget> layoutCards = new();
        readonly List<float> cardTop = new();
        readonly List<float> cardHeight = new();
        readonly Dictionary<string, List<int>> layoutTrackBuckets = new(StringComparer.Ordinal);
        readonly List<List<int>> layoutTrackBucketPool = new();
        readonly HashSet<Component> selectedCards = new();
        float rowsContentH;
        DragKind drag;
        int activeDragControl;
        Vector2 panLast;
        double lastTick;

        // marquee + multi-key drag/scale state
        Vector2 marqueeStart, marqueeEnd;
        Dictionary<int, TimelineKeyState[]> dragSnap;  // track → keys + tangent metadata at drag start
        Dictionary<int, List<int>> dragSel;            // track → selected key indices (into the snapshot)
        float dragMouseTime0, dragMouseNorm0;          // mouse time / value at drag start
        Vector2 dragPointerStart;
        bool dragChanged;
        bool dragUndoRecorded;
        (int track, int key)? pendingKeyToggle;
        float scalePivot;                              // fixed end while scaling
        float scaleSpan0;                              // selection span at scale start
        // Inline value scrubbing. The input itself becomes the horizontal drag surface; a click without movement
        // retains normal text/toggle/enum editing. One Undo snapshot covers the complete gesture.
        int valueScrubTrack = -1;
        float valueScrubStart;
        float valueScrubPixels;
        bool valueScrubRecorded;
        bool valueScrubDragged;

        // Direct typed preview. It avoids Unity AnimationClip's ambiguity when a GameObject has multiple components
        // of the same type and supports nested/vector/bool/enum bindings consistently.
        bool preview;
        readonly Dictionary<(Component target, string binding), float> previewBaseline = new();
        readonly HashSet<Component> animatedTargetBuffer = new();
        bool previewBaselineHeld;
        bool previewDirty = true;

        // edit-mode recording (auto-key bound component fields when their inspector values change)
        bool recordMode;
        readonly Dictionary<(Component target, string member), float> recordSnapshot = new();
        bool recordSnapshotDirty = true;

        // edit-mode simulation (run the solver in edit mode; transport drives forward playback)
        bool simulate;
        bool simPlaying;
        List<BrazeContainer> simContainers;
        // Spike guard for the simulate transport clock: matches BrazeContainer.EditorSimStep's guard so the
        // playhead and the solver clock absorb the same wall-clock time per tick.
        const float MaxEditorFrameDelta = 0.25f;

        // view transform
        float viewStart;          // time (seconds) at the left edge of the plot
        float pps = 80f;          // pixels per second (time zoom)
        float vStart = -0.05f;    // normalised value at the bottom of the curve view
        float vSpan = 1.10f;      // normalised value span shown
        float rowScroll;          // dope-sheet vertical scroll (px)
        bool viewInit;
        bool needFit;

        // layout cache (per OnGUI)
        float leftPanelW = DefaultLeftW;
        float plotX0, plotW;
        Rect graphRect;

        static Texture2D diamondTex;
        GUIStyle wordmark, dim, strongMini, rowName, miniBtn, tabStyle, tabOn, addBtn;

        [MenuItem("Window/BrazeFX/Timeline")]
        public static void Open()
        {
            var w = GetWindow<BrazeTimelineWindow>();
            w.titleContent = new GUIContent("Braze Timeline");
            w.minSize = new Vector2(560, 280);
            w.Show();
        }

        public static void OpenFor(BrazeTimeline t)
        {
            Open();
            var w = GetWindow<BrazeTimelineWindow>();
            w.SetTimeline(t);
        }

        void OnEnable()
        {
            EditorApplication.update += Tick;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            Undo.undoRedoPerformed += OnUndoRedo;
            Selection.selectionChanged += OnSelectionChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            lastTick = EditorApplication.timeSinceStartup;
            leftPanelW = EditorPrefs.GetFloat(LeftWidthPref, DefaultLeftW);
            if (!TryRebindTimeline()) RequestTimelineRebind();
        }

        void OnDisable()
        {
            EditorApplication.update -= Tick;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            Undo.undoRedoPerformed -= OnUndoRedo;
            Selection.selectionChanged -= OnSelectionChanged;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.delayCall -= DeferredTimelineRebind;
            rebindFramesRemaining = 0;
            ReleasePointerGesture();
            StopTransientModes("closing the Timeline window");
            if (valueScrubTrack >= 0) EndValueScrubGesture();
        }

        void OnHierarchyChanged()
        {
            // Dirtied scene objects can make Unity publish hierarchyChanged while an IMGUI gesture is still
            // captured. The curve/selection snapshot remains valid, and cancelling here makes a multi-key drag
            // move for one event and then stop following the cursor. Defer destructive cleanup until the gesture
            // ends; layout sanitization still runs every frame and a destroyed Timeline is handled immediately.
            if (timeline != null && (drag != DragKind.None || valueScrubTrack >= 0))
            {
                previewDirty = true;
                recordSnapshotDirty = true;
                Repaint();
                return;
            }
            HandleExternalStructureChange();
        }

        void OnUndoRedo() => HandleExternalStructureChange();

        void HandleExternalStructureChange()
        {
            // Hierarchy/Undo can invalidate indices while a key or value is being dragged. Release all captures
            // immediately; the next OnGUI rebuilds the normalized layout from serialized card IDs.
            ReleasePointerGesture();
            if (valueScrubTrack >= 0) EndValueScrubGesture();
            previewDirty = true;
            recordSnapshotDirty = true;
            if (timeline == null) RequestTimelineRebind();
            Repaint();
        }

        // Play-mode transitions must stop direct edit preview/simulation and force a repaint so the window does not
        // keep showing the previous mode's state.
        void OnPlayModeChanged(PlayModeStateChange c)
        {
            // Preserve the edit-scene identifier before Unity replaces scene objects with their Play Mode copies.
            // Never overwrite it with a transient Play Mode GlobalObjectId.
            if (c == PlayModeStateChange.ExitingEditMode && timeline != null) RememberBoundTimeline(timeline);
            ReleasePointerGesture();
            if (valueScrubTrack >= 0) EndValueScrubGesture();
            StopTransientModes("changing Play Mode");
            previewDirty = true;
            if (c == PlayModeStateChange.ExitingEditMode || c == PlayModeStateChange.ExitingPlayMode)
            {
                // Drop the outgoing scene reference at the lifecycle boundary, before Unity destroys it. With
                // Reload Domain disabled the edit object can still compare non-null in EnteredPlayMode; retaining
                // it made TryRebindTimeline report success, then left the window empty when Unity destroyed it.
                timeline = null;
            }
            if (c == PlayModeStateChange.ExitingEditMode || c == PlayModeStateChange.EnteredPlayMode)
            {
                // A text field or custom scrubber can own keyboard/hot control when Play is pressed elsewhere.
                // Release both so no edit-mode gesture survives underneath the read-only presentation.
                GUIUtility.keyboardControl = 0;
                GUIUtility.hotControl = 0;
            }

            // EnteredEditMode can fire before Unity has finished restoring Selection and scene PPtrs. Queue a
            // delayCall and keep retrying briefly from Tick; this is the lifecycle gap that left the window empty.
            if (c == PlayModeStateChange.EnteredEditMode)
            {
                timeline = null;
                RequestTimelineRebind();
            }
            else if (c == PlayModeStateChange.EnteredPlayMode)
            {
                // Objects can still be settling on the exact callback frame — keep retrying from Tick so the
                // window reliably rebinds to the play-mode copy and shows the live playhead.
                timeline = null; // reject anything a transition-time callback managed to attach
                if (!TryRebindTimeline()) RequestTimelineRebind();
            }
            Repaint();
        }

        void OnLostFocus()
        {
            // Unity normally routes MouseUp to hotControl even outside the window. Losing editor/OS focus is the
            // exceptional path where that release can never arrive; abandon the gesture so a marquee cannot stick.
            if (drag != DragKind.None || activeDragControl != 0)
            {
                ReleasePointerGesture();
                Repaint();
            }
        }

        // Timeline reachable from the current selection. Descendant-only ownership makes the nearest ancestor the
        // complete and unambiguous lookup; scene-wide searches and sibling-root guessing are intentionally rejected.
        BrazeTimeline SelectedTimeline()
        {
            var go = Selection.activeGameObject;
            if (go == null) return null;
            return go.GetComponentInParent<BrazeTimeline>(true);
        }

        void OnSelectionChanged()
        {
            // Unity can publish Selection changes after ExitingEditMode while the selected object still belongs to
            // the outgoing edit scene. EnteredPlayMode/EnteredEditMode own rebinding across that lifecycle gap.
            if (IsPlayModeTransition())
            {
                Repaint();
                return;
            }
            var t = SelectedTimeline();
            if (t != null && t != timeline) SetTimeline(t);
            Repaint();
        }

        void SetTimeline(BrazeTimeline t)
        {
            if (t == timeline)
            {
                if (t != null) RememberBoundTimeline(t);
                return;
            }
            StopTransientModes("switching Timelines");
            timeline = t;
            if (timeline != null)
            {
                rebindFramesRemaining = 0;
                EditorApplication.delayCall -= DeferredTimelineRebind;
                RememberBoundTimeline(timeline);
                if (!Application.isPlaying && timeline.AdoptTrackTargets()) MarkTimelineDirty();
            }
            selectedTrack = (t != null && t.tracks.Count > 0) ? 0 : -1;
            selectedTracks.Clear(); if (selectedTrack >= 0) selectedTracks.Add(selectedTrack);
            selectedCards.Clear();
            if (t != null && t.targets.Count > 0 && t.targets[0]?.target != null)
                selectedCards.Add(t.targets[0].target);
            selectedKey = -1;
            needFit = true;
            previewDirty = true;
            Repaint();
        }

        void StopTransientModes(string context)
        {
            Exception failure = null;
            try { ExitRecordMode(); } catch (Exception ex) { failure ??= ex; }
            try { ExitPreview(); } catch (Exception ex) { failure ??= ex; }
            try { ExitSimulate(); } catch (Exception ex) { failure ??= ex; }
            recordMode = false;
            preview = false;
            simulate = false;
            simPlaying = false;
            if (failure != null)
                Debug.LogWarning($"[BrazeFX] Timeline preview/simulation teardown failed while {context}: {failure.Message}");
        }

        void RememberBoundTimeline(BrazeTimeline value)
        {
            if (value == null || Application.isPlaying) return;
            string id = GlobalObjectId.GetGlobalObjectIdSlow(value).ToString();
            if (string.IsNullOrEmpty(id)) return;
            boundTimelineGlobalId = id;
            SessionState.SetString(BoundTimelineSessionKey, id);
            // GlobalObjectId cannot resolve scene objects while in Play Mode (play objects are copies), so also
            // remember a scene + hierarchy path that CAN find the play-mode copy of this timeline.
            SessionState.SetString(BoundTimelinePathSessionKey,
                value.gameObject.scene.path + "|" + HierarchyPath(value.transform));
        }

        static string HierarchyPath(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        bool RestoreBoundTimeline()
        {
            if (timeline != null) return true;
            // Play Mode: GlobalObjectId lookups return null for scene objects, which is what used to leave the
            // window empty during play. Resolve the play-mode copy by the remembered scene/hierarchy path instead
            // (falling back to the single timeline in the scene), so the window stays bound, read-only, with a
            // live playhead.
            if (Application.isPlaying) return RestoreBoundTimelineInPlayMode();
            string id = !string.IsNullOrEmpty(boundTimelineGlobalId)
                ? boundTimelineGlobalId : SessionState.GetString(BoundTimelineSessionKey, string.Empty);
            if (string.IsNullOrEmpty(id) || !GlobalObjectId.TryParse(id, out GlobalObjectId globalId)) return false;
            var restored = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId) as BrazeTimeline;
            if (restored == null) return false;
            SetTimeline(restored);
            return true;
        }

        bool RestoreBoundTimelineInPlayMode()
        {
            // Resources.FindObjectsOfTypeAll is the proven lookup for Timeline roots (the transform lock manager
            // synchronizes with it): the root carries editor hide flags, which narrower scene queries can skip.
            // Filter to loaded scene instances so prefab assets and stage objects are never bound.
            string remembered = SessionState.GetString(BoundTimelinePathSessionKey, string.Empty);
            BrazeTimeline match = null, first = null;
            foreach (BrazeTimeline candidate in Resources.FindObjectsOfTypeAll<BrazeTimeline>())
            {
                if (candidate == null) continue;
                var scene = candidate.gameObject.scene;
                if (!scene.IsValid() || !scene.isLoaded) continue;
                if (first == null) first = candidate;
                if (!string.IsNullOrEmpty(remembered) &&
                    scene.path + "|" + HierarchyPath(candidate.transform) == remembered)
                {
                    match = candidate;
                    break;
                }
            }
            // No (or stale) remembered path: bind SOME timeline rather than none — a read-only view of the wrong
            // timeline is recoverable by clicking the right one, while an empty window during play is a dead end.
            BrazeTimeline chosen = match != null ? match : first;
            if (chosen == null) return false;
            SetTimeline(chosen);
            return true;
        }

        bool TryRebindTimeline()
        {
            if (timeline != null) return true;
            // Selection can briefly retain the outgoing edit-scene object when Reload Domain is disabled. In Play
            // Mode always resolve from the loaded play scenes first so that stale Selection cannot short-circuit
            // the exhaustive lookup with an object Unity is about to destroy.
            if (Application.isPlaying) return RestoreBoundTimelineInPlayMode();
            BrazeTimeline selected = SelectedTimeline();
            if (selected != null)
            {
                SetTimeline(selected);
                return true;
            }
            return RestoreBoundTimeline();
        }

        void RequestTimelineRebind()
        {
            rebindFramesRemaining = RebindRetryFrames;
            EditorApplication.delayCall -= DeferredTimelineRebind;
            EditorApplication.delayCall += DeferredTimelineRebind;
        }

        void DeferredTimelineRebind()
        {
            if (this == null) return;
            if (IsPlayModeTransition()) return;
            TryRebindTimeline();
            Repaint();
        }

        static bool IsPlayModeTransition()
            => EditorApplication.isPlayingOrWillChangePlaymode != Application.isPlaying;

        void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            float dt = (float)(now - lastTick);
            lastTick = now;
            // Retry while transitioning play modes is unsafe (objects are being destroyed/copied), but once fully
            // IN play mode retries must run — that is how a window opened (or left empty) during play finds the
            // play-mode timeline copy and shows the live playhead instead of an empty view.
            if (timeline == null && rebindFramesRemaining > 0 && !IsPlayModeTransition())
            {
                rebindFramesRemaining--;
                TryRebindTimeline();
                Repaint();
            }
            if (timeline == null) return;

            if (Application.isPlaying)
            {
                if (timeline.playing) Repaint();   // play mode drives itself; just animate our playhead
                return;
            }

            if (recordMode && !simulate && RecordChangedValues())
            {
                Repaint();
                SceneView.RepaintAll();
            }

            // Only tick (and repaint) while actually playing — paused simulate must not spin the GPU/editor.
            if (simulate && simPlaying)
            {
                float speed = float.IsFinite(timeline.speed) ? Mathf.Max(0f, timeline.speed) : 1f;
                // Honest wall-clock delta, clamped only against pathological spikes (compile hitch, breakpoint).
                // A tighter ceiling here silently slow-motions the whole timeline whenever the editor loop drops
                // below the ceiling's rate — the solver's fixed-rate accumulator already prevents catch-up spirals.
                float sdt = Mathf.Clamp(dt, 0f, MaxEditorFrameDelta) * speed;
                float before = timeline.time;
                timeline.Advance(sdt);
                bool loopedToStart = DidLoopToStart(before, timeline.time, sdt);
                if (loopedToStart) ResetSimState(timeline.time, true);
                else SampleAt(timeline.time);
                if (simContainers != null)
                {
                    float stepDt = loopedToStart ? Mathf.Min(sdt, Mathf.Max(0f, timeline.time)) : sdt;
                    for (int i = 0; i < simContainers.Count; i++)
                        if (simContainers[i] != null) simContainers[i].EditorSimStep(stepDt);
                }
                // EditorSimStep dispatches the solver directly, outside Unity's normal player loop. Queue one so
                // auto-repainting camera views (notably Game view) render the newly-written GPU fields as well;
                // SceneView.RepaintAll below only covers Scene views.
                EditorApplication.QueuePlayerLoopUpdate();
                Repaint();
                SceneView.RepaintAll();
            }
        }

        // ════════════════════════════════ GUI ════════════════════════════════
        void OnGUI()
        {
            EnsureStyles();
            bool playModeReadOnly = Application.isPlaying;
            if (playModeReadOnly) ConsumePlayModeInteraction(Event.current);
            if (activeDragControl != 0 && drag != DragKind.None && GUIUtility.hotControl != activeDragControl)
                ReleasePointerGesture();
            if (valueScrubTrack >= 0 && GUIUtility.hotControl == 0) EndValueScrubGesture();
            EditorGUI.DrawRect(new Rect(0, 0, position.width, position.height), BgPanel);

            // Unbound (or the bound timeline was destroyed): re-acquire from the CURRENT selection before
            // giving up. Selection events alone are not enough — clicking an object that is already selected
            // fires no event, so a binding lost without a subsequent selection change (play-mode transition,
            // component re-add, a failed bind) would otherwise strand the empty state forever.
            if (timeline == null)
            {
                TryRebindTimeline();
            }

            try
            {
                if (!playModeReadOnly && timeline != null && timeline.AdoptTrackTargets()) MarkTimelineDirty();
                using (new EditorGUI.DisabledScope(playModeReadOnly))
                {
                    DrawToolbar(new Rect(0, 0, position.width, ToolbarH));

                    if (timeline == null) DrawEmptyState();
                    else
                    {
                        // The view transform lives in non-serialized fields that SURVIVE assembly hot-reloads but
                        // not editor restarts. Sanitize per frame so invalid zoom state can never blank the window.
                        SanitizeView();
                        DrawTimelineBody();
                        guiFailure = null;
                    }
                }
                if (playModeReadOnly) DrawPlayModeReadOnlyPresentation();
            }
            catch (ExitGUIException)
            {
                throw;   // normal IMGUI control flow (popups, object pickers) — never treat as a failure
            }
            catch (Exception ex)
            {
                // A throw mid-draw would otherwise blank the window every frame with nothing actionable on
                // screen. Surface it as a banner + one console log per distinct error so it can be reported.
                if (guiFailure != ex.Message)
                {
                    guiFailure = ex.Message;
                    Debug.LogException(ex);
                }
                var banner = new Rect(0, ToolbarH, position.width, 40f);
                EditorGUI.DrawRect(banner, new Color(0.45f, 0.10f, 0.08f));
                GUI.Label(new Rect(banner.x + 10, banner.y, banner.width - 20, banner.height),
                    "Timeline draw failed (see Console): " + guiFailure, strongMini);
            }
        }

        void CapturePointer(int controlId)
        {
            activeDragControl = controlId;
            GUIUtility.hotControl = controlId;
            GUIUtility.keyboardControl = 0;
        }

        bool OwnsPointer(int controlId)
            => activeDragControl == controlId && GUIUtility.hotControl == controlId;

        void ReleasePointerGesture()
        {
            if (activeDragControl != 0 && GUIUtility.hotControl == activeDragControl)
                GUIUtility.hotControl = 0;
            activeDragControl = 0;
            drag = DragKind.None;
            dragSnap = null;
            dragSel = null;
            dragChanged = false;
            dragUndoRecorded = false;
            pendingKeyToggle = null;
        }

        [NonSerialized] string guiFailure;

        static bool IsPlayModePointerEvent(EventType type)
            => type == EventType.MouseDown || type == EventType.MouseUp || type == EventType.MouseDrag ||
               type == EventType.ScrollWheel || type == EventType.ContextClick ||
               type == EventType.DragUpdated || type == EventType.DragPerform;

        static void ConsumePlayModeInteraction(Event e)
        {
            if (e == null || !IsPlayModePointerEvent(e.type)) return;
            if (e.type == EventType.DragUpdated || e.type == EventType.DragPerform)
                DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
            e.Use();
        }

        void DrawPlayModeReadOnlyPresentation()
        {
            // Grey every authoring panel, then redraw only the two live-status elements above the veil. The moving
            // playhead remains easy to follow, but every control and editing affordance reads as unavailable.
            EditorGUI.DrawRect(new Rect(0, ToolbarH, position.width, Mathf.Max(0f, position.height - ToolbarH)),
                PlayModeVeil);

            float y = 34f;
            var chip = new Rect(position.width - 234f, y - 1f, 158f, 21f);
            EditorGUI.DrawRect(chip, new Color(0.15f, 0.16f, 0.18f, 0.98f));
            Outline(chip, new Color(Accent.r, Accent.g, Accent.b, 0.58f));
            GUI.Label(new Rect(chip.x + 7f, chip.y + 1f, chip.width - 14f, chip.height - 2f),
                new GUIContent("PLAY MODE · READ ONLY",
                    "Author and save Timeline tracks in Edit Mode; Play Mode is fully locked."), strongMini);

            if (timeline == null) return;
            var ruler = new Rect(leftPanelW, ToolbarH, position.width - leftPanelW, RulerH);
            DrawPlayhead(ruler, graphRect);
            if (timeline.playing)
                GUI.Label(new Rect(position.width - 70f, y, 64f, 19f), $"{timeline.time:0.00}s", strongMini);
        }

        void SanitizeView()
        {
            bool poisoned = !float.IsFinite(pps) || pps < 1f || pps > 5000f || !float.IsFinite(viewStart);
            if (poisoned) { pps = 80f; viewStart = 0f; needFit = true; }
            if (!float.IsFinite(rowScroll)) rowScroll = 0f;
            if (!float.IsFinite(vStart)) vStart = -0.05f;
            if (!float.IsFinite(vSpan) || vSpan < 0.02f || vSpan > 1000f) vSpan = 1.10f;
            if (!float.IsFinite(leftPanelW)) leftPanelW = DefaultLeftW;
        }

        void DrawTimelineBody()
        {
            LayoutTracks();   // group tracks by object → slots + header bands (shared by both panels + hit-testing)

            float bodyH = position.height - ToolbarH - TabH;
            leftPanelW = ClampLeftWidth(leftPanelW);
            var left   = new Rect(0, ToolbarH, leftPanelW, bodyH);
            var splitter = new Rect(left.xMax - SplitterW * 0.5f, ToolbarH, SplitterW, bodyH);
            var ruler  = new Rect(leftPanelW, ToolbarH, position.width - leftPanelW, RulerH);
            graphRect  = new Rect(leftPanelW, ToolbarH + RulerH, position.width - leftPanelW, bodyH - RulerH);
            var tabbar = new Rect(0, position.height - TabH, position.width, TabH);

            bool splitterHandled = HandleTrackSplitter(splitter);
            if (splitterHandled)
            {
                leftPanelW = ClampLeftWidth(leftPanelW);
                left = new Rect(0, ToolbarH, leftPanelW, bodyH);
                splitter = new Rect(left.xMax - SplitterW * 0.5f, ToolbarH, SplitterW, bodyH);
                ruler = new Rect(leftPanelW, ToolbarH, position.width - leftPanelW, RulerH);
                graphRect = new Rect(leftPanelW, ToolbarH + RulerH, position.width - leftPanelW, bodyH - RulerH);
            }

            plotX0 = graphRect.x + LM;
            plotW = Mathf.Max(1f, graphRect.width - LM - RM);
            if (!viewInit || needFit) ApplyFit();
            ClampView();

            EditorGUI.DrawRect(graphRect, BgGraph);
            DrawGrid(ruler, graphRect);

            if (tab == Tab.Dopesheet) DrawDopesheet(graphRect);
            else DrawCurves(graphRect);

            DrawSelectionOverlay(graphRect);
            using (new EditorGUI.DisabledScope(Application.isPlaying)) DrawLeftPanel(left);
            DrawTrackSplitter(splitter);
            DrawRuler(ruler);
            DrawPlayhead(ruler, graphRect);
            DrawTabBar(tabbar);

            if (!Application.isPlaying)
            {
                HandleObjectDrop(new Rect(0, ToolbarH, position.width, position.height - ToolbarH - TabH));
                if (!splitterHandled) HandleInput(ruler, graphRect);

            }

            // Re-apply values after any edit this frame.
            if ((preview || simulate) && previewDirty && !Application.isPlaying) SampleAt(timeline.time);
        }


        // Compact two-row toolbar: transport stays on row one; the complete playback/work-area settings stay on
        // row two. This remains usable at the window's minimum dock width and exposes Reset-on-Loop explicitly.
        void DrawToolbar(Rect r)
        {
            EditorGUI.DrawRect(r, BgToolbar);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1, r.width, 1), new Color(Accent.r, Accent.g, Accent.b, 0.55f));

            float x = 10f, y = 5f;
            var icon = BrazeIconTex.Get("timeline");
            if (icon != null)
            {
                Color old = GUI.color; GUI.color = Accent;
                GUI.DrawTexture(new Rect(x, y, 20, 20), icon, ScaleMode.ScaleToFit, true);
                GUI.color = old; x += 25f;
            }
            GUI.Label(new Rect(x, y - 5, 62, 30), position.width >= 700f ? "BRAZE" : "BTL", wordmark);
            x += position.width >= 700f ? 66f : 42f;
            if (position.width >= 900f) { GUI.Label(new Rect(x, y - 5, 72, 30), "TIMELINE", dim); x += 74f; }
            if (timeline == null) return;

            Color oldBg = GUI.backgroundColor;
            using (new EditorGUI.DisabledScope(Application.isPlaying || simulate))
            {
                if (recordMode) GUI.backgroundColor = new Color(0.90f, 0.22f, 0.18f);
                bool next = GUI.Toggle(new Rect(x, y, 44, 20), recordMode,
                    new GUIContent(recordMode ? "● REC" : "REC", "Auto-key registered Timeline cards only"), miniBtn);
                GUI.backgroundColor = oldBg;
                if (next != recordMode) { if (next) EnterRecordMode(); else ExitRecordMode(); }
            }
            x += 48f;

            bool hasFirst = TryFindEdgeKeyframe(true, out _);
            using (new EditorGUI.DisabledScope(!hasFirst))
                if (GUI.Button(new Rect(x, y, 25, 20), new GUIContent("|◀", "First key"), miniBtn)) SnapToEdgeKeyframe(true);
            x += 27f;
            bool hasPrevious = !simulate && TryFindAdjacentKeyframe(-1, out _);
            using (new EditorGUI.DisabledScope(!hasPrevious))
                if (GUI.Button(new Rect(x, y, 27, 20), new GUIContent("◀◆", "Previous key"), miniBtn)) SnapToAdjacentKeyframe(-1);
            x += 29f;
            using (new EditorGUI.DisabledScope(Application.isPlaying || simulate || timeline.tracks.Count == 0))
                if (GUI.Button(new Rect(x, y, 27, 20), new GUIContent("◆+", "Add key. Shift: all tracks"), miniBtn))
                    AddKeyAtPlayhead(Event.current.shift || selectedTrack < 0 || selectedTrack >= timeline.tracks.Count);
            x += 29f;
            bool hasNext = !simulate && TryFindAdjacentKeyframe(1, out _);
            using (new EditorGUI.DisabledScope(!hasNext))
                if (GUI.Button(new Rect(x, y, 27, 20), new GUIContent("◆▶", "Next key"), miniBtn)) SnapToAdjacentKeyframe(1);
            x += 29f;
            bool hasLast = TryFindEdgeKeyframe(false, out _);
            using (new EditorGUI.DisabledScope(!hasLast))
                if (GUI.Button(new Rect(x, y, 25, 20), new GUIContent("▶|", "Last key"), miniBtn)) SnapToEdgeKeyframe(false);
            x += 29f;

            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                GUI.backgroundColor = simulate ? new Color(0.30f, 0.70f, 0.38f) : new Color(0.62f, 0.84f, 0.68f);
                bool next = GUI.Toggle(new Rect(x, y, 68, 20), simulate,
                    new GUIContent(simulate ? "● Sim" : "Simulate", "Run Braze simulation in Edit mode"), miniBtn);
                GUI.backgroundColor = oldBg;
                if (next != simulate) { if (next) EnterSimulate(); else ExitSimulate(); }
            }

            float rx = r.xMax - 8f;
            rx -= 38f;
            if (GUI.Button(new Rect(rx, y, 38, 20), new GUIContent("Fit", "Frame work area (A)"), miniBtn)) needFit = true;
            using (new EditorGUI.DisabledScope(Application.isPlaying || simulate))
            {
                rx -= 68f;
                if (preview) GUI.backgroundColor = new Color(0.90f, 0.26f, 0.24f);
                bool next = GUI.Toggle(new Rect(rx, y, 64, 20), preview,
                    new GUIContent(preview ? "● Preview" : "Preview", "Non-destructive typed parameter preview"), miniBtn);
                GUI.backgroundColor = oldBg;
                if (next != preview) { if (next) EnterPreview(); else ExitPreview(); }
            }

            x = 10f; y = 34f;
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                EditorGUI.BeginChangeCheck();
                GUI.Label(new Rect(x, y, 30, 19), new GUIContent("End", "Explicit playback/work-area end"), dim); x += 31f;
                float length = EditorGUI.FloatField(new Rect(x, y + 1, 46, 18), timeline.length); x += 51f;
                GUI.Label(new Rect(x, y, 27, 19), "Loop", dim); x += 28f;
                var loop = (BrazeTimeline.LoopMode)EditorGUI.EnumPopup(new Rect(x, y + 1, 72, 18), timeline.loopMode); x += 77f;
                GUI.Label(new Rect(x, y, 34, 19), "Speed", dim); x += 35f;
                float speed = EditorGUI.FloatField(new Rect(x, y + 1, 37, 18), timeline.speed); x += 42f;
                bool autoPlay = EditorGUI.Toggle(new Rect(x, y + 1, 15, 18), timeline.playOnEnable); x += 16f;
                GUI.Label(new Rect(x, y, 46, 19), new GUIContent("Auto", "Play on Enable"), dim); x += 42f;
                bool unscaled = EditorGUI.Toggle(new Rect(x, y + 1, 15, 18), timeline.ignoreTimeScale); x += 16f;
                GUI.Label(new Rect(x, y, 54, 19), new GUIContent("Unscaled", "Ignore TimeScale"), dim); x += 55f;
                bool reset = EditorGUI.Toggle(new Rect(x, y + 1, 15, 18), timeline.resetSimOnLoop); x += 16f;
                GUI.Label(new Rect(x, y, 66, 19), new GUIContent("Reset Loop", "Restart this Timeline's child simulations when looping"), dim);
                if (EditorGUI.EndChangeCheck())
                {
                    RecordTimeline("Edit Timeline Settings");
                    // Mathf.Max/Clamp pass NaN straight through; a NaN typed here would be SERIALIZED and poison
                    // every zoom/fit computation until the value is manually fixed. Reject non-finite input.
                    if (float.IsFinite(length)) timeline.length = Mathf.Max(BrazeTimeline.MinDuration, length);
                    timeline.loopMode = loop;
                    if (float.IsFinite(speed)) timeline.speed = Mathf.Clamp(speed, 0.01f, 8f);
                    timeline.playOnEnable = autoPlay;
                    timeline.ignoreTimeScale = unscaled;
                    timeline.resetSimOnLoop = reset;
                    needFit = true;
                }
            }

            if (simulate)
                GUI.Label(new Rect(r.xMax - 70, y, 64, 19), $"{timeline.time:0.00}s", strongMini);
        }

        void DrawEmptyState()
        {
            var r = new Rect(0, ToolbarH, position.width, position.height - ToolbarH);
            var s = BrazeEditorGUI.StyleFromSkin(() => EditorStyles.label);
            s.alignment = TextAnchor.MiddleCenter;
            s.fontSize = 12;
            s.wordWrap = true;
            s.normal.textColor = TextDim;
            // Play mode auto-binds any timeline it can find, so an empty view while playing has exactly one
            // meaning — say it, instead of suggesting a selection that cannot help.
            GUI.Label(r, Application.isPlaying
                ? "No Braze Timeline exists in the playing scene."
                : "Select a GameObject with a Braze Timeline,\nor create one via GameObject ▸ BrazeFX ▸ Cinematics ▸ Timeline.", s);
        }

        // ── left panel: track list ──
        // Build the grouped row layout: container tracks first, then each other object's tracks contiguous, with a
        // header band above the first row of every group. Rebuilt each frame; cheap for the handful of tracks here.
        void LayoutTracks()
        {
            rowOrder.Clear(); rowTop.Clear(); rowHeader.Clear();
            layoutCards.Clear(); cardTop.Clear(); cardHeight.Clear(); rowsContentH = 0f;
            if (timeline == null) return;
            SanitizeSelectionState();
            selectedCards.RemoveWhere(component => component == null || !timeline.ContainsTarget(component));

            for (int i = 0; i < layoutTrackBucketPool.Count; i++) layoutTrackBucketPool[i].Clear();
            layoutTrackBuckets.Clear();
            int usedBuckets = 0;
            for (int i = 0; i < timeline.tracks.Count; i++)
            {
                BrazeTrack track = timeline.tracks[i];
                if (track == null || string.IsNullOrEmpty(track.TargetId)) continue;
                if (!layoutTrackBuckets.TryGetValue(track.TargetId, out List<int> bucket))
                {
                    if (usedBuckets >= layoutTrackBucketPool.Count) layoutTrackBucketPool.Add(new List<int>());
                    bucket = layoutTrackBucketPool[usedBuckets++];
                    layoutTrackBuckets.Add(track.TargetId, bucket);
                }
                bucket.Add(i);
            }

            float y = CardGap;
            for (int c = 0; c < timeline.targets.Count; c++)
            {
                BrazeTimelineTarget card = timeline.targets[c];
                if (card == null) continue;
                float top = y;
                layoutCards.Add(card);
                cardTop.Add(top);
                y += GroupHeaderH;

                if (card.expanded && !string.IsNullOrEmpty(card.Id) &&
                    layoutTrackBuckets.TryGetValue(card.Id, out List<int> tracks))
                {
                    for (int i = 0; i < tracks.Count; i++)
                    {
                        rowOrder.Add(tracks[i]);
                        rowHeader.Add(false);
                        rowTop.Add(y);
                        y += RowH;
                    }
                }

                cardHeight.Add(y - top);
                y += CardGap;
            }
            rowsContentH = y;
        }

        void SanitizeSelectionState()
        {
            if (timeline == null) return;
            int count = timeline.tracks.Count;
            selectedTracks.RemoveWhere(i => i < 0 || i >= count || timeline.tracks[i] == null);
            selKeys.RemoveWhere(selection =>
            {
                if (selection.track < 0 || selection.track >= count) return true;
                AnimationCurve curve = timeline.tracks[selection.track]?.curve;
                return curve == null || selection.key < 0 || selection.key >= curve.length;
            });

            if (selectedTrack < 0 || selectedTrack >= count || timeline.tracks[selectedTrack] == null)
            {
                selectedTrack = -1;
                for (int i = 0; i < count; i++)
                    if (timeline.tracks[i] != null) { selectedTrack = i; break; }
                selectedKey = -1;
            }
            else
            {
                AnimationCurve curve = timeline.tracks[selectedTrack].curve;
                if (curve == null || selectedKey < 0 || selectedKey >= curve.length) selectedKey = -1;
            }

            if (valueScrubTrack >= count || (valueScrubTrack >= 0 && timeline.tracks[valueScrubTrack] == null))
                EndValueScrubGesture();
        }

        // Track index at a panel-local Y (already scroll-adjusted to content space). -1 if in a header band / gap.
        int TrackAtContentY(float contentY)
        {
            for (int s = 0; s < rowOrder.Count; s++)
                if (contentY >= rowTop[s] && contentY < rowTop[s] + RowH) return rowOrder[s];
            return -1;
        }

        // Content-space Y top of a given track index (its slot's row top, header space already folded in).
        float ContentTopForTrack(int trackIndex)
        {
            for (int s = 0; s < rowOrder.Count; s++) if (rowOrder[s] == trackIndex) return rowTop[s];
            return 0f;
        }

        bool CardHasTracks(BrazeTimelineTarget card)
        {
            return card != null && !string.IsNullOrEmpty(card.Id) &&
                   layoutTrackBuckets.TryGetValue(card.Id, out List<int> tracks) && tracks.Count > 0;
        }

        void ExpandCard(Component target)
        {
            if (timeline == null || target == null) return;
            for (int i = 0; i < timeline.targets.Count; i++)
                if (timeline.targets[i]?.target == target) timeline.targets[i].expanded = true;
        }

        // A card header identifies one exact component instance, not merely its GameObject.
        void DrawGroupHeader(Rect band, BrazeTimelineTarget card, bool selected, bool hasChildren)
        {
            Component target = card?.target;
            bool owned = target != null && timeline != null && timeline.OwnsTarget(target);
            Color accent = !owned && target != null ? new Color(1f, 0.30f, 0.22f) :
                selected ? Accent : new Color(0.32f, 0.34f, 0.40f);
            EditorGUI.DrawRect(band, selected ? new Color(Accent.r, Accent.g, Accent.b, 0.16f) : BgToolbar);
            EditorGUI.DrawRect(new Rect(band.x, band.y, 3f, band.height), accent);
            Outline(band, selected ? new Color(Accent.r, Accent.g, Accent.b, 0.55f) : new Color(1f, 1f, 1f, 0.08f));

            if (hasChildren) DrawDisclosureTriangle(new Rect(band.x + 5f, band.y, 18f, band.height), card.expanded, TextStrong);
            Texture icon = target != null ? EditorGUIUtility.ObjectContent(target, target.GetType()).image : null;
            if (icon != null) GUI.DrawTexture(new Rect(band.x + 24, band.y + 5, 18, 18), icon, ScaleMode.ScaleToFit, true);
            string name = card != null ? card.DisplayName : "Missing Component";
            if (!owned && target != null) name += "  ·  MOVE UNDER TIMELINE";
            float actionSpace = selected && owned ? 67f : 0f;
            GUI.Label(new Rect(band.x + 47, band.y, band.width - 56 - actionSpace, band.height), name, strongMini);
        }

        void DeleteSelectedTracks()
        {
            if (Application.isPlaying || selectedTracks.Count == 0) return;
            RecordTimeline(selectedTracks.Count > 1 ? "Delete Tracks" : "Delete Track");
            var idx = new List<int>(selectedTracks);
            idx.Sort((a, b) => b.CompareTo(a));            // descending so earlier removals don't shift later ones
            foreach (int i in idx) if (i >= 0 && i < timeline.tracks.Count) timeline.tracks.RemoveAt(i);
            selectedTracks.Clear(); selKeys.Clear(); selectedKey = -1;
            selectedTrack = timeline.tracks.Count > 0 ? 0 : -1;
            Repaint();
        }

        void DrawLeftPanel(Rect r)
        {
            EditorGUI.DrawRect(r, BgPanel);
            EditorGUI.DrawRect(new Rect(r.xMax - 1, r.y, 1, r.height), Divider);

            float top = r.y + RulerH;
            for (int c = 0; c < layoutCards.Count; c++)
            {
                BrazeTimelineTarget card = layoutCards[c];
                float y = top + cardTop[c] - rowScroll;
                var body = new Rect(r.x + 4, y, r.width - 8, cardHeight[c]);
                if (body.yMax < top || body.y > r.yMax) continue;
                EditorGUI.DrawRect(body, new Color(0.075f, 0.078f, 0.090f, 0.82f));
                var header = new Rect(body.x, body.y, body.width, GroupHeaderH);
                bool selectedCard = card?.target != null && selectedCards.Contains(card.target);
                bool hasChildren = CardHasTracks(card);
                DrawGroupHeader(header, card, selectedCard, hasChildren);

                // Parameter authoring belongs to the component card. Keeping the action here makes its scope
                // unambiguous even when several cards are selected for recording or bulk operations.
                Rect addRect = default;
                if (selectedCard && card?.target != null && timeline.OwnsTarget(card.target))
                {
                    addRect = new Rect(header.xMax - 62f, header.y + 4f, 56f, header.height - 8f);
                    if (GUI.Button(addRect, "+ Add", addBtn))
                        AddTrackMenu(new Rect(addRect.x, addRect.yMax, addRect.width, 1f), card.target);
                }

                if (!Application.isPlaying && Event.current.type == EventType.MouseDown && Event.current.button == 0 && header.Contains(Event.current.mousePosition))
                {
                    bool additive = Event.current.shift || Event.current.control || Event.current.command;
                    if (!additive) selectedCards.Clear();
                    if (card?.target != null)
                    {
                        if (additive && selectedCards.Contains(card.target)) selectedCards.Remove(card.target);
                        else selectedCards.Add(card.target);
                    }
                    if (hasChildren && (Event.current.clickCount == 2 || Event.current.mousePosition.x < header.x + 24f) && card != null)
                    {
                        RecordTimeline("Toggle Timeline Card");
                        card.expanded = !card.expanded;
                    }
                    Event.current.Use(); Repaint();
                }
                if (!Application.isPlaying && Event.current.type == EventType.ContextClick && header.Contains(Event.current.mousePosition))
                {
                    var menu = new GenericMenu();
                    BrazeTimelineTarget captured = card;
                    if (hasChildren)
                    {
                        menu.AddItem(new GUIContent(card != null && card.expanded ? "Collapse" : "Expand"), false,
                            () =>
                            {
                                if (Application.isPlaying) return;
                                RecordTimeline("Toggle Timeline Card");
                                captured.expanded = !captured.expanded;
                                Repaint();
                            });
                        menu.AddSeparator(string.Empty);
                    }
                    menu.AddItem(new GUIContent("Remove Card and Tracks"), false, () => RemoveCard(captured));
                    menu.ShowAsContext(); Event.current.Use();
                }
            }

            for (int s = 0; s < rowOrder.Count; s++)
            {
                int i = rowOrder[s];
                float y = top + rowTop[s] - rowScroll;
                if (y + RowH < top || y > r.yMax) continue;
                var tr = timeline.tracks[i];

                var row = new Rect(r.x + 4, y, r.width - 8, RowH);
                bool sel = selectedTracks.Contains(i);
                EditorGUI.DrawRect(row, sel ? RowSel : (s % 2 == 0 ? RowA : RowB));
                if (sel) EditorGUI.DrawRect(new Rect(row.x, row.y, 2, row.height), Accent);

                // Track rows are children of their component card, not peers. The inset rail and shifted controls
                // keep that ownership readable even when a card contains many authored properties.
                const float childIndent = 12f;
                EditorGUI.DrawRect(new Rect(row.x + 8f, row.y, 1f, row.height), new Color(1f, 1f, 1f, 0.10f));
                EditorGUI.DrawRect(new Rect(row.x + 8f, row.center.y, 8f, 1f), new Color(1f, 1f, 1f, 0.10f));
                EditorGUI.DrawRect(new Rect(row.x + 8 + childIndent, row.y + RowH * 0.5f - 5, 10, 10), tr.color);

                bool en = GUI.Toggle(new Rect(row.x + 24 + childIndent, row.y + 4, 16, 16), tr.enabled, GUIContent.none);
                if (en != tr.enabled) { RecordTimeline("Toggle Track"); tr.enabled = en; }

                bool solo = GUI.Toggle(new Rect(row.x + 40 + childIndent, row.y + 4, 18, 16), tr.solo,
                    new GUIContent("S", "Solo this track"), miniBtn);
                if (solo != tr.solo) { RecordTimeline("Solo Track"); tr.solo = solo; }
                bool locked = GUI.Toggle(new Rect(row.x + 59 + childIndent, row.y + 4, 18, 16), tr.locked,
                    new GUIContent("L", "Lock key editing"), miniBtn);
                if (locked != tr.locked) { RecordTimeline("Lock Track"); tr.locked = locked; }

                // name (click selects; shift / ctrl / cmd-click multi-selects)
                var nameR = new Rect(row.x + 80 + childIndent, row.y, row.width - 80 - childIndent - 64, RowH);
                var field = tr.Resolve();
                bool owned = timeline.OwnsTarget(tr.target);
                bool resolved = owned && (tr.IsEvent ? tr.ResolveEvent() != null : field != null);
                rowName.normal.textColor = !resolved ? new Color(1f, 0.42f, 0.32f) : tr.enabled ? TextStrong : TextDim;
                GUI.Label(nameR, tr.DisplayName(), rowName);
                if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && nameR.Contains(Event.current.mousePosition))
                {
                    bool additive = Event.current.shift || Event.current.control || Event.current.command;
                    if (additive) { if (!selectedTracks.Add(i)) selectedTracks.Remove(i); }   // toggle
                    else { selectedTracks.Clear(); selectedTracks.Add(i); }
                    selectedTrack = i; selectedKey = -1; Event.current.Use(); Repaint();
                }

                // live, EDITABLE value at the playhead — typing here sets/updates a key at the current time
                if (owned && field != null && tr.curve != null)
                {
                    var valR = new Rect(row.xMax - 62, row.y + 4, 56, 16);
                    using (new EditorGUI.DisabledScope(tr.locked)) DrawTypedValue(i, field, valR);
                }

                // right-click → remove. Acts on the whole multi-selection when this row is part of it.
                if (!Application.isPlaying && Event.current.type == EventType.ContextClick && row.Contains(Event.current.mousePosition))
                {
                    if (!selectedTracks.Contains(i)) { selectedTracks.Clear(); selectedTracks.Add(i); selectedTrack = i; }
                    var menu = new GenericMenu();
                    int count = selectedTracks.Count;
                    menu.AddItem(new GUIContent(count > 1 ? $"Remove {count} Tracks" : "Remove Track"), false,
                                 () => { DeleteSelectedTracks(); });
                    menu.ShowAsContext();
                    Event.current.Use();
                }
            }

            // header drawn LAST so scrolled rows tuck under it
            var head = new Rect(r.x, r.y, r.width, RulerH);
            EditorGUI.DrawRect(head, BgToolbar);
            EditorGUI.DrawRect(new Rect(head.x, head.yMax - 1, head.width, 1), Divider);
            GUI.Label(new Rect(head.x + 10, head.y, 120, head.height), "TRACKS", strongMini);

            if (layoutCards.Count == 0)
            {
                var empty = new Rect(r.x + 14, top + 24, r.width - 28, 80);
                var s = new GUIStyle(dim) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
                GUI.Label(empty, "Drop a GameObject or Braze component here\nto create component cards.", s);
            }
        }

        void DrawTrackSplitter(Rect r)
        {
            if (!Application.isPlaying) EditorGUIUtility.AddCursorRect(r, MouseCursor.ResizeHorizontal);
            Color c = drag == DragKind.ResizeTracks ? Accent : Divider;
            EditorGUI.DrawRect(new Rect(r.center.x - 1f, r.y, 2f, r.height), c);
        }

        bool HandleTrackSplitter(Rect r)
        {
            if (Application.isPlaying) return false;
            var e = Event.current;
            int controlId = GUIUtility.GetControlID("BrazeTimelineTrackSplitter".GetHashCode(), FocusType.Passive, r);
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
            {
                drag = DragKind.ResizeTracks;
                CapturePointer(controlId);
                e.Use();
                Repaint();
                return true;
            }

            if (drag != DragKind.ResizeTracks || !OwnsPointer(controlId)) return false;

            if (e.type == EventType.MouseDrag)
            {
                leftPanelW = ClampLeftWidth(e.mousePosition.x);
                EditorPrefs.SetFloat(LeftWidthPref, leftPanelW);
                e.Use();
                Repaint();
                return true;
            }

            if (e.type == EventType.MouseUp)
            {
                leftPanelW = ClampLeftWidth(leftPanelW);
                EditorPrefs.SetFloat(LeftWidthPref, leftPanelW);
                ReleasePointerGesture();
                e.Use();
                Repaint();
                return true;
            }

            if (e.type == EventType.Ignore)
            {
                ReleasePointerGesture();
                Repaint();
                return true;
            }

            return e.type == EventType.Used;
        }

        // ── ruler ──
        void DrawRuler(Rect r)
        {
            EditorGUI.DrawRect(r, BgToolbar);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1, r.width, 1), Divider);

            float visSpan = plotW / pps;
            float step = NiceStep(visSpan);
            float t = Mathf.Floor(viewStart / step) * step;
            for (int guard = 0; guard < 2000; guard++, t += step)
            {
                float x = TimeToX(t);
                if (x > r.xMax) break;
                if (x >= r.x - 1)
                {
                    EditorGUI.DrawRect(new Rect(x, r.yMax - 8, 1, 8), GridStrong);
                    GUI.Label(new Rect(x + 3, r.y + 2, 60, 16), FmtTime(t), dim);
                }
            }
            DrawDeadZone(r);

            // "End" remains the editable authoring/work-area boundary.
            float endX = TimeToX(timeline.duration);
            if (endX > r.x && endX < r.xMax - 2)
                GUI.Label(new Rect(endX + 3, r.y + 2, 60, 16), $"End {FmtTime(timeline.duration)}", dim);

            // Looping follows authored content. Show the distinct boundary when the work area extends beyond it.
            if (timeline.loopMode != BrazeTimeline.LoopMode.Once &&
                Mathf.Abs(timeline.loopEnd - timeline.duration) > 1e-4f)
            {
                float loopX = TimeToX(timeline.loopEnd);
                if (loopX > r.x && loopX < r.xMax - 2)
                    GUI.Label(new Rect(loopX + 3, r.y + 2, 72, 16), $"Loop {FmtTime(timeline.loopEnd)}", dim);
            }
        }

        void DrawGrid(Rect ruler, Rect g)
        {
            float step = NiceStep(plotW / pps);
            float t = Mathf.Floor(viewStart / step) * step;
            for (int guard = 0; guard < 2000; guard++, t += step)
            {
                float x = TimeToX(t);
                if (x > g.xMax) break;
                if (x >= g.x) EditorGUI.DrawRect(new Rect(x, g.y, 1, g.height), Mathf.Abs(t) < 1e-4f ? GridStrong : Grid);
            }
            DrawDeadZone(g);
        }

        // Shade the non-playing region. Once uses the explicit work area; Loop/Ping-Pong use the last authored key.
        void DrawDeadZone(Rect area)
        {
            if (timeline == null) return;
            float playbackEnd = timeline.loopMode == BrazeTimeline.LoopMode.Once
                ? timeline.duration
                : timeline.loopEnd;
            float endX = TimeToX(playbackEnd);
            if (endX >= area.xMax) return;
            float x = Mathf.Max(endX, area.x);
            EditorGUI.DrawRect(Rect.MinMaxRect(x, area.y, area.xMax, area.yMax), new Color(0f, 0f, 0f, 0.30f));
            EditorGUI.DrawRect(new Rect(x, area.y, 1f, area.height), new Color(Accent.r, Accent.g, Accent.b, 0.6f));
        }

        // ── dope sheet ──
        void DrawDopesheet(Rect g)
        {
            float top = g.y;
            for (int c = 0; c < layoutCards.Count; c++)
            {
                float y = top + cardTop[c] - rowScroll;
                var card = new Rect(g.x, y, g.width, cardHeight[c]);
                if (card.yMax < g.y || card.y > g.yMax) continue;
                EditorGUI.DrawRect(card, new Color(0f, 0f, 0f, 0.08f));
                var band = new Rect(g.x, y, g.width, GroupHeaderH);
                EditorGUI.DrawRect(band, selectedCards.Contains(layoutCards[c].target)
                    ? new Color(Accent.r, Accent.g, Accent.b, 0.08f) : BgToolbar);
                EditorGUI.DrawRect(new Rect(band.x, band.yMax - 1, band.width, 1), Divider);
            }
            for (int s = 0; s < rowOrder.Count; s++)
            {
                int i = rowOrder[s];
                float y = top + rowTop[s] - rowScroll;
                if (y + RowH < g.y || y > g.yMax) continue;
                var tr = timeline.tracks[i];

                var row = new Rect(g.x, y, g.width, RowH);
                EditorGUI.DrawRect(row, selectedTracks.Contains(i) ? RowSel : (s % 2 == 0 ? RowA : RowB));

                var c = tr.curve;
                if (c == null) continue;
                float cy = y + RowH * 0.5f;
                for (int k = 0; k < c.length; k++)
                {
                    float x = TimeToX(c[k].time);
                    if (x < g.x - 8 || x > g.xMax + 8) continue;
                    bool sel = selKeys.Contains((i, k));
                    DrawDiamond(new Vector2(x, cy), sel ? 7f : 5.5f, sel ? Color.white : tr.color);
                }
            }
        }

        // ── curve graph ──
        void DrawCurves(Rect g)
        {
            // value reference lines for the selected track (its min / max)
            if (selectedTrack >= 0 && selectedTrack < timeline.tracks.Count)
            {
                var f0 = timeline.tracks[selectedTrack].Resolve();
                float mn = f0 != null ? f0.Min : 0f, mx = f0 != null ? f0.Max : 1f;
                DrawValueLine(g, 0f, mn);
                DrawValueLine(g, 1f, mx);
            }

            if (Event.current.type == EventType.Repaint)
            {
                for (int i = 0; i < timeline.tracks.Count; i++)
                {
                    var tr = timeline.tracks[i];
                    if (tr.curve == null) continue;
                    var f = tr.Resolve();
                    float min = f != null ? f.Min : 0f, max = f != null ? f.Max : 1f;
                    DrawCurveLine(g, tr.curve, f, min, max, tr.color, i == selectedTrack);
                }
            }

            if (selectedTrack >= 0 && selectedTrack < timeline.tracks.Count)
            {
                var tr = timeline.tracks[selectedTrack];
                if (tr.curve != null)
                {
                    var f = tr.Resolve();
                    float min = f != null ? f.Min : 0f, max = f != null ? f.Max : 1f;
                    var c = tr.curve;
                    for (int k = 0; k < c.length; k++)
                    {
                        float x = TimeToX(c[k].time);
                        float yy = ValueToY(g, c[k].value, min, max);
                        if (x < g.x - 8 || x > g.xMax + 8) continue;
                        bool ksel = selKeys.Contains((selectedTrack, k));
                        DrawDiamond(new Vector2(x, yy), ksel ? 7f : 5.5f, ksel ? Color.white : tr.color);
                    }
                }
            }
        }

        void DrawValueLine(Rect g, float norm, float realValue)
        {
            float y = NormToY(g, norm);
            if (y < g.y || y > g.yMax) return;
            EditorGUI.DrawRect(new Rect(g.x, y, g.width, 1), Grid);
            GUI.Label(new Rect(g.x + 4, y - 16, 90, 16), $"{realValue:0.##}", dim);
        }

        void DrawCurveLine(Rect g, AnimationCurve curve, BrazeAnimatableField field, float min, float max, Color col, bool sel)
        {
            int n = Mathf.Clamp((int)plotW, 24, 320);
            var pts = new Vector3[n + 1];
            for (int i = 0; i <= n; i++)
            {
                float x = g.x + g.width * i / n;
                float t = XToTime(x);
                float value = field != null ? field.Evaluate(curve, t) : curve.Evaluate(t);
                pts[i] = new Vector3(x, ValueToY(g, value, min, max), 0);
            }
            Handles.color = sel ? col : new Color(col.r, col.g, col.b, 0.30f);
            Handles.DrawAAPolyLine(sel ? 3f : 2f, pts);
        }

        // ── playhead / scrubber (draggable in edit mode; live during Play) ──
        void DrawPlayhead(Rect ruler, Rect g)
        {
            float x = TimeToX(timeline.time);
            if (x < ruler.x - 8 || x > ruler.xMax + 8) return;
            Color c = simulate ? new Color(0.34f, 0.80f, 0.44f)            // green while simulating
                    : preview ? new Color(0.93f, 0.24f, 0.22f)            // red while previewing (REC)
                    : Accent;
            EditorGUI.DrawRect(new Rect(x, ruler.y + 10, 1, ruler.height - 10 + g.height), c);
            // grab handle in the ruler
            DrawDiamond(new Vector2(x, ruler.y + 8), 7f, c);
        }

        // ── tab bar ──
        void DrawTabBar(Rect r)
        {
            EditorGUI.DrawRect(r, BgToolbar);
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1), Divider);

            // tabs shrink to fit so they never overflow a narrow / docked window
            float w = Mathf.Clamp((r.width - 20f) * 0.5f, 48f, 110f);
            var t1 = new Rect(r.x + 8, r.y + 3, w, r.height - 6);
            var t2 = new Rect(t1.xMax + 4, r.y + 3, w, r.height - 6);
            if (GUI.Toggle(t1, tab == Tab.Dopesheet, "Dopesheet", tab == Tab.Dopesheet ? tabOn : tabStyle) && tab != Tab.Dopesheet)
            { tab = Tab.Dopesheet; Repaint(); }
            if (GUI.Toggle(t2, tab == Tab.Curves, "Curves", tab == Tab.Curves ? tabOn : tabStyle) && tab != Tab.Curves)
            { tab = Tab.Curves; Repaint(); }

            if (r.width > 430f)
            {
                float sx = r.xMax - 142f;
                using (new EditorGUI.DisabledScope(Application.isPlaying))
                {
                    EditorGUI.BeginChangeCheck();
                    bool snap = GUI.Toggle(new Rect(sx, r.y + 4, 54, 18), timeline.snapToFrames,
                        new GUIContent("Snap", "Snap key times to frames"), miniBtn);
                    int fps = EditorGUI.IntField(new Rect(sx + 58, r.y + 5, 38, 17), timeline.frameRate);
                    GUI.Label(new Rect(sx + 99, r.y, 40, r.height), "fps", dim);
                    if (EditorGUI.EndChangeCheck())
                    {
                        RecordTimeline("Edit Timeline Snapping");
                        timeline.snapToFrames = snap;
                        timeline.frameRate = Mathf.Clamp(fps, 1, 240);
                    }
                }
            }

            // shortcut hint — drawn only if the FULL text fits to the right of the tabs (never truncated)
            var hint = new GUIContent("Drag box: select   ·   Shift/Cmd: add   ·   Cmd/Ctrl-C/V: copy/paste   ·   Drag ends: scale   ·   Wheel: zoom   ·   Middle-drag: pan   ·   A: fit   ·   Del: remove");
            float hintX = t2.xMax + 16f;
            float hintRight = r.width > 430f ? r.xMax - 150f : r.xMax;
            float avail = hintRight - hintX - 8f;
            if (avail >= dim.CalcSize(hint).x)
                GUI.Label(new Rect(hintX, r.y, avail, r.height), hint, dim);
        }

        // ════════════════════════════════ input ════════════════════════════════
        void HandleInput(Rect ruler, Rect g)
        {
            var e = Event.current;
            Vector2 m = e.mousePosition;
            bool inGraph = g.Contains(m), inRuler = ruler.Contains(m);
            int controlId = GUIUtility.GetControlID("BrazeTimelineGraphGesture".GetHashCode(), FocusType.Passive, g);
            if (HandleEditorCommand(e)) return;

            // zoom (scroll wheel)
            if (e.type == EventType.ScrollWheel && (inGraph || inRuler))
            {
                float factor = Mathf.Clamp(1f - e.delta.y * 0.05f, 0.6f, 1.6f);
                if (e.shift && tab == Tab.Curves && inGraph)
                {
                    float nM = YToNorm(g, m.y);
                    vSpan = Mathf.Clamp(vSpan / factor, 0.02f, 1000f);
                    vStart = nM - (g.yMax - m.y) / g.height * vSpan;
                }
                else
                {
                    float tM = XToTime(m.x);
                    pps = Mathf.Clamp(pps * factor, 1f, 5000f);
                    viewStart = tM - (m.x - plotX0) / pps;
                }
                e.Use(); Repaint(); return;
            }

            // pan (middle mouse) — start
            if (e.type == EventType.MouseDown && e.button == 2 && (inGraph || inRuler))
            { drag = DragKind.Pan; panLast = m; CapturePointer(controlId); e.Use(); return; }

            if (e.type == EventType.MouseDrag && drag == DragKind.Pan && OwnsPointer(controlId))
            {
                Vector2 d = m - panLast; panLast = m;
                viewStart -= d.x / pps;
                if (tab == Tab.Curves) vStart += d.y / g.height * vSpan;
                else rowScroll -= d.y;
                e.Use(); Repaint(); return;
            }

            // scrub the playhead (grab it in the ruler) — edit mode, not while simulating (forward-only)
            if (e.type == EventType.MouseDown && e.button == 0 && inRuler && !Application.isPlaying && !simulate)
            {
                drag = DragKind.Playhead;
                CapturePointer(controlId);
                if (!recordMode) EnterPreview();          // grabbing the head starts preview (Unity-style)
                ScrubTo(XToTime(m.x));
                e.Use(); Repaint(); return;
            }

            if (e.type == EventType.MouseDrag && drag == DragKind.Playhead && OwnsPointer(controlId))
            {
                // auto-pan when dragging into the edges, so you can scrub past the end (like Unity)
                const float edge = 28f;
                if (m.x > g.xMax - edge) viewStart += (m.x - (g.xMax - edge)) / pps * 0.30f;
                else if (m.x < g.x + edge) viewStart -= ((g.x + edge) - m.x) / pps * 0.30f;
                ScrubTo(XToTime(Mathf.Clamp(m.x, plotX0, plotX0 + plotW)));
                e.Use(); Repaint(); return;
            }

            // left mouse down in the graph
            if (e.type == EventType.MouseDown && e.button == 0 && inGraph)
            {
                if (e.clickCount == 2) { AddKeyAt(g, m); e.Use(); Repaint(); return; }

                // scale via the selection-box edges (2+ keys with a real time span); move = box middle / a key
                if (selKeys.Count >= 2 && SelectionBox(g, out Rect sb) && sb.width > 2f * SelPad + 4f)
                {
                    const float grab = 7f;
                    bool inY = m.y >= sb.y - 2f && m.y <= sb.yMax + 2f;
                    if (inY && Mathf.Abs(m.x - sb.xMax) <= grab)
                    {
                        GetSelTimeBounds(out float a, out float _); BeginScale(a, m);
                        if (drag == DragKind.ScaleSel) CapturePointer(controlId);
                        e.Use(); return;
                    }   // right edge, pivot left
                    if (inY && Mathf.Abs(m.x - sb.x) <= grab)
                    {
                        GetSelTimeBounds(out float _, out float b); BeginScale(b, m);
                        if (drag == DragKind.ScaleSel) CapturePointer(controlId);
                        e.Use(); return;
                    }   // left edge, pivot right
                }

                // grab a key
                if (TryPickKey(g, m, out int trk, out int key))
                {
                    var id = (trk, key);
                    bool additive = e.shift || e.control || e.command;
                    PrepareKeyPointerSelection(trk, key, additive, !timeline.tracks[trk].locked);
                    if (selKeys.Contains(id) && !timeline.tracks[trk].locked)
                    {
                        BeginMove(m);
                        if (drag == DragKind.MoveSel) CapturePointer(controlId);
                    }
                    e.Use(); Repaint(); return;
                }

                // inside the selection box (empty area between keys) → move the whole selection
                if (selKeys.Count >= 2 && SelectionBox(g, out Rect selBox) && selBox.Contains(m))
                {
                    pendingKeyToggle = null;
                    BeginMove(m);
                    if (drag == DragKind.MoveSel) CapturePointer(controlId);
                    e.Use(); Repaint(); return;
                }

                // empty space → start a marquee (and pick the lane's track in the dope sheet)
                if (tab == Tab.Dopesheet)
                {
                    int row = TrackAtContentY(m.y - g.y + rowScroll);
                    if (row >= 0) selectedTrack = row;
                }
                drag = DragKind.Marquee; marqueeStart = marqueeEnd = m;
                pendingKeyToggle = null;
                CapturePointer(controlId);
                e.Use(); Repaint(); return;
            }

            if (e.type == EventType.MouseDrag && drag == DragKind.MoveSel && OwnsPointer(controlId)) { UpdateMove(g, m); e.Use(); Repaint(); return; }
            if (e.type == EventType.MouseDrag && drag == DragKind.ScaleSel && OwnsPointer(controlId)) { UpdateScale(m); e.Use(); Repaint(); return; }
            if (e.type == EventType.MouseDrag && drag == DragKind.Marquee && OwnsPointer(controlId)) { marqueeEnd = m; e.Use(); Repaint(); return; }

            if (e.type == EventType.MouseUp && OwnsPointer(controlId))
            {
                bool wasDrag = drag != DragKind.None;
                if (drag == DragKind.Marquee) FinishMarquee(g, e.shift || e.control || e.command);
                else if (drag == DragKind.MoveSel) FinishMoveClickSelection();
                ReleasePointerGesture();
                if (wasDrag) { e.Use(); Repaint(); }   // clear the lingering marquee / refresh selection box
            }

            if (e.type == EventType.Ignore && OwnsPointer(controlId))
            {
                ReleasePointerGesture();
                Repaint();
                return;
            }

            if (e.type == EventType.ContextClick && inGraph)
            {
                if (TryPickKey(g, m, out int trk, out int key))
                {
                    selectedTrack = trk; selectedKey = key;
                    if (!selKeys.Contains((trk, key))) { selKeys.Clear(); selKeys.Add((trk, key)); }
                    KeyContextMenu(); e.Use();
                }
            }

            if (e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Escape && drag != DragKind.None)
                {
                    ReleasePointerGesture();
                    e.Use(); Repaint(); return;
                }
                bool shortcut = e.command || e.control;
                if (shortcut && e.keyCode == KeyCode.C && selKeys.Count > 0)
                { CopySelectedKeys(); e.Use(); }
                else if (shortcut && e.keyCode == KeyCode.V && copiedKeys.Count > 0)
                { PasteKeysAtPlayhead(); e.Use(); }
                else if (shortcut && e.keyCode == KeyCode.X && selKeys.Count > 0)
                { CopySelectedKeys(); DeleteSelectedKeys(); e.Use(); Repaint(); }
                else if (e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace)
                {
                    if (DeleteSelectedKeys()) { e.Use(); Repaint(); }            // keys first (if any selected)
                    else if (selectedTracks.Count > 0) { DeleteSelectedTracks(); e.Use(); }   // else delete tracks
                }
                else if (e.keyCode == KeyCode.A)
                { needFit = true; e.Use(); Repaint(); }
            }
        }

        // macOS routes Cmd-C/V/X through ValidateCommand/ExecuteCommand rather than reliably emitting KeyDown.
        // Supporting both paths makes Timeline shortcuts behave like Unity's Animation window on every platform.
        bool HandleEditorCommand(Event e)
        {
            if (e.type != EventType.ValidateCommand && e.type != EventType.ExecuteCommand) return false;
            bool canExecute;
            switch (e.commandName)
            {
                case "Copy":
                case "Cut":
                    canExecute = selKeys.Count > 0 && !Application.isPlaying;
                    break;
                case "Paste":
                    canExecute = copiedKeys.Count > 0 && !Application.isPlaying;
                    break;
                case "Delete":
                case "SoftDelete":
                    canExecute = !Application.isPlaying && (selKeys.Count > 0 || selectedTracks.Count > 0);
                    break;
                default:
                    return false;
            }
            if (!canExecute) return false;

            if (e.type == EventType.ExecuteCommand)
            {
                switch (e.commandName)
                {
                    case "Copy": CopySelectedKeys(); break;
                    case "Cut": CopySelectedKeys(); DeleteSelectedKeys(); break;
                    case "Paste": PasteKeysAtPlayhead(); break;
                    default:
                        if (!DeleteSelectedKeys() && selectedTracks.Count > 0) DeleteSelectedTracks();
                        break;
                }
                Repaint();
            }
            e.Use();
            return true;
        }

        bool TryPickKey(Rect g, Vector2 m, out int trk, out int key)
        {
            trk = -1; key = -1;
            const float R = 7f;
            if (tab == Tab.Dopesheet)
            {
                int row = TrackAtContentY(m.y - g.y + rowScroll);
                if (row < 0) return false;
                var c = timeline.tracks[row].curve; if (c == null) return false;
                float cy = g.y + ContentTopForTrack(row) - rowScroll + RowH * 0.5f;
                for (int k = 0; k < c.length; k++)
                    if (Mathf.Abs(TimeToX(c[k].time) - m.x) < R && Mathf.Abs(cy - m.y) < RowH * 0.5f)
                    { trk = row; key = k; return true; }
                return false;
            }
            if (selectedTrack < 0 || selectedTrack >= timeline.tracks.Count) return false;
            var tr = timeline.tracks[selectedTrack];
            var cc = tr.curve;
            if (cc == null) return false;
            var f = tr.Resolve(); float min = f != null ? f.Min : 0f, max = f != null ? f.Max : 1f;
            for (int k = 0; k < cc.length; k++)
            {
                Vector2 p = new Vector2(TimeToX(cc[k].time), ValueToY(g, cc[k].value, min, max));
                if ((p - m).sqrMagnitude < R * R) { trk = selectedTrack; key = k; return true; }
            }
            return false;
        }

        // ── multi-key move / scale / marquee ─────────────────────────────────────────────────────
        // Snapshot the affected curves at drag start; every drag frame recomputes from the snapshot
        // (so crossing/reordering keys never corrupts the selection mid-drag).
        void PrepareKeyPointerSelection(int track, int key, bool additive, bool canDrag)
        {
            var id = (track, key);
            bool alreadySelected = selKeys.Contains(id);
            pendingKeyToggle = null;
            if (additive)
            {
                if (alreadySelected)
                {
                    // Defer deselection until MouseUp. If this becomes a drag, the key must stay selected so the
                    // entire lasso set moves together even while Shift/Cmd remains held from marquee selection.
                    if (canDrag) pendingKeyToggle = id;
                    else selKeys.Remove(id);
                }
                else selKeys.Add(id);
            }
            else if (!alreadySelected)
            {
                selKeys.Clear();
                selKeys.Add(id);
            }
            selectedTrack = track;
            selectedKey = key;
        }

        void FinishMoveClickSelection()
        {
            if (dragChanged || !pendingKeyToggle.HasValue) return;
            var id = pendingKeyToggle.Value;
            selKeys.Remove(id);
            if (selectedTrack == id.track && selectedKey == id.key)
            {
                selectedKey = -1;
                foreach (var selected in selKeys)
                {
                    selectedTrack = selected.track;
                    selectedKey = selected.key;
                    break;
                }
            }
        }

        void SnapshotSelection()
        {
            dragSnap = new Dictionary<int, TimelineKeyState[]>();
            dragSel = new Dictionary<int, List<int>>();
            foreach (var (t, k) in selKeys)
            {
                if (t < 0 || t >= timeline.tracks.Count) continue;
                if (timeline.tracks[t].locked) continue;
                var c = timeline.tracks[t].curve; if (c == null) continue;
                if (!dragSnap.ContainsKey(t))
                {
                    dragSnap[t] = CaptureCurveStates(c);
                    dragSel[t] = new List<int>();
                }
                if (k >= 0 && k < dragSnap[t].Length) dragSel[t].Add(k);
            }
        }

        void BeginMove(Vector2 m)
        {
            if (Application.isPlaying) return;
            SnapshotSelection();
            if (dragSnap.Count == 0)
            {
                dragSnap = null;
                dragSel = null;
                drag = DragKind.None;
                return;
            }
            dragMouseTime0 = XToTime(m.x);
            dragMouseNorm0 = YToNorm(graphRect, m.y);
            dragPointerStart = m;
            dragChanged = false;
            dragUndoRecorded = false;
            drag = DragKind.MoveSel;
        }

        void UpdateMove(Rect g, Vector2 m)
        {
            if (dragSnap == null) return;
            if (!dragChanged && (m - dragPointerStart).sqrMagnitude < 4f) return;
            EnsureDragUndo("Move Keys");
            dragChanged = true;
            pendingKeyToggle = null;
            float dt = XToTime(m.x) - dragMouseTime0;
            float dvNorm = YToNorm(g, m.y) - dragMouseNorm0;
            var newSel = new HashSet<(int, int)>();
            foreach (var kv in dragSnap)
            {
                int t = kv.Key; var snap = kv.Value; var sel = dragSel[t];
                var f = timeline.tracks[t].Resolve();
                float min = f != null ? f.Min : 0f, max = f != null ? f.Max : 1f;
                float dv = (max - min) * dvNorm;
                var work = (TimelineKeyState[])snap.Clone();
                foreach (int i in sel)
                {
                    Keyframe key = work[i].key;
                    key.time = SnapTime(Mathf.Max(0f, snap[i].key.time + dt));
                    if (tab == Tab.Curves)
                        key.value = Mathf.Clamp(snap[i].key.value + dv, Mathf.Min(min, max), Mathf.Max(min, max));
                    work[i] = work[i].WithKey(key);
                }
                CommitTrack(t, work, sel, newSel);
            }
            ReplaceSelection(newSel);
        }

        void BeginScale(float pivot, Vector2 m)
        {
            if (Application.isPlaying) return;
            SnapshotSelection();
            if (dragSnap.Count == 0)
            {
                dragSnap = null;
                dragSel = null;
                drag = DragKind.None;
                return;
            }
            GetSelTimeBounds(out float a, out float b);
            scalePivot = pivot;
            scaleSpan0 = (Mathf.Abs(pivot - a) < Mathf.Abs(pivot - b) ? b : a) - pivot;   // signed dist pivot→moving end
            dragPointerStart = m;
            dragChanged = false;
            dragUndoRecorded = false;
            pendingKeyToggle = null;
            drag = DragKind.ScaleSel;
        }

        void UpdateScale(Vector2 m)
        {
            if (dragSnap == null || Mathf.Abs(scaleSpan0) < 1e-4f) return;
            float factor = Mathf.Clamp((XToTime(m.x) - scalePivot) / scaleSpan0, 0.02f, 50f);
            if (!dragChanged && (m - dragPointerStart).sqrMagnitude < 4f) return;
            EnsureDragUndo("Scale Keys");
            dragChanged = true;
            var newSel = new HashSet<(int, int)>();
            foreach (var kv in dragSnap)
            {
                int t = kv.Key; var snap = kv.Value; var sel = dragSel[t];
                var work = (TimelineKeyState[])snap.Clone();
                foreach (int i in sel)
                {
                    Keyframe key = work[i].key;
                    key.time = SnapTime(Mathf.Max(0f, scalePivot + (snap[i].key.time - scalePivot) * factor));
                    work[i] = work[i].WithKey(key);
                }
                CommitTrack(t, work, sel, newSel);
            }
            ReplaceSelection(newSel);
        }

        void EnsureDragUndo(string label)
        {
            if (dragUndoRecorded) return;
            RecordTimeline(label);
            dragUndoRecorded = true;
        }

        // Write a modified keyframe array back to a track, then re-find the moved keys' new indices by time.
        void CommitTrack(int track, TimelineKeyState[] work, List<int> selIdx, HashSet<(int, int)> newSel)
        {
            if (Application.isPlaying) return;
            var selected = new HashSet<int>(selIdx);
            var entries = new List<(TimelineKeyState state, bool selected, int order)>(work.Length);
            for (int i = 0; i < work.Length; i++) entries.Add((work[i], selected.Contains(i), i));
            entries.Sort((a, b) =>
            {
                int timeOrder = a.state.key.time.CompareTo(b.state.key.time);
                if (timeOrder != 0) return timeOrder;
                // A dragged key replaces a stationary key when both land on the same property/frame.
                int selectionOrder = a.selected.CompareTo(b.selected);
                return selectionOrder != 0 ? selectionOrder : a.order.CompareTo(b.order);
            });

            var merged = new List<TimelineKeyState>(entries.Count);
            var selectedTimes = new List<float>();
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (merged.Count > 0 && SameKeyTime(merged[merged.Count - 1].key.time, entry.state.key.time))
                {
                    merged[merged.Count - 1] = entry.state;
                    if (selectedTimes.Count > 0 && SameKeyTime(selectedTimes[selectedTimes.Count - 1], entry.state.key.time))
                        selectedTimes.RemoveAt(selectedTimes.Count - 1);
                }
                else merged.Add(entry.state);
                if (entry.selected) selectedTimes.Add(entry.state.key.time);
            }

            var c = timeline.tracks[track].curve;
            CommitCurveStates(c, merged);
            int len = c.length;
            foreach (float time in selectedTimes)
            {
                int best = -1; float bd = 1e9f;
                for (int j = 0; j < len; j++) { float d = Mathf.Abs(c[j].time - time); if (d < bd) { bd = d; best = j; } }
                if (best >= 0) newSel.Add((track, best));
            }
        }

        void ReplaceSelection(HashSet<(int, int)> set)
        {
            selKeys.Clear();
            foreach (var s in set) selKeys.Add(s);
            if (selKeys.Count > 0 && !selKeys.Contains((selectedTrack, selectedKey)))
                foreach (var s in selKeys) { selectedTrack = s.track; selectedKey = s.key; break; }
        }

        void FinishMarquee(Rect g, bool additive)
        {
            var r = MarqueeRect();
            if (r.width < 3 && r.height < 3) { if (!additive) ClearSelection(); return; }   // a click, not a box
            if (!additive) selKeys.Clear();
            if (tab == Tab.Dopesheet)
            {
                for (int i = 0; i < timeline.tracks.Count; i++)
                {
                    var c = timeline.tracks[i].curve; if (c == null) continue;
                    float cy = g.y + ContentTopForTrack(i) - rowScroll + RowH * 0.5f;
                    if (cy < g.y || cy > g.yMax) continue;
                    for (int k = 0; k < c.length; k++)
                        if (r.Contains(new Vector2(TimeToX(c[k].time), cy))) selKeys.Add((i, k));
                }
            }
            else if (selectedTrack >= 0 && selectedTrack < timeline.tracks.Count)
            {
                var tr = timeline.tracks[selectedTrack]; var c = tr.curve;
                if (c != null)
                {
                    var f = tr.Resolve(); float min = f != null ? f.Min : 0f, max = f != null ? f.Max : 1f;
                    for (int k = 0; k < c.length; k++)
                        if (r.Contains(new Vector2(TimeToX(c[k].time), ValueToY(g, c[k].value, min, max)))) selKeys.Add((selectedTrack, k));
                }
            }
            if (selKeys.Count > 0) foreach (var s in selKeys) { selectedTrack = s.track; selectedKey = s.key; break; }
        }

        Rect MarqueeRect()
        {
            float x = Mathf.Min(marqueeStart.x, marqueeEnd.x), y = Mathf.Min(marqueeStart.y, marqueeEnd.y);
            return new Rect(x, y, Mathf.Abs(marqueeEnd.x - marqueeStart.x), Mathf.Abs(marqueeEnd.y - marqueeStart.y));
        }

        bool GetSelTimeBounds(out float tMin, out float tMax)
        {
            tMin = float.MaxValue; tMax = float.MinValue;
            foreach (var (t, k) in selKeys)
            {
                if (t < 0 || t >= timeline.tracks.Count) continue;
                var c = timeline.tracks[t].curve; if (c == null || k < 0 || k >= c.length) continue;
                float tt = c[k].time;
                if (tt < tMin) tMin = tt;
                if (tt > tMax) tMax = tt;
            }
            return tMin <= tMax;
        }

        void ClearSelection() { selKeys.Clear(); selectedKey = -1; }

        // marquee rectangle, or a bounding box confined to the selected keys' extent (with scale handles)
        void DrawSelectionOverlay(Rect g)
        {
            if (drag == DragKind.Marquee)
            {
                var r = MarqueeRect();
                EditorGUI.DrawRect(r, new Color(Accent.r, Accent.g, Accent.b, 0.12f));
                Outline(r, Accent);
                return;
            }

            if (selKeys.Count >= 2 && SelectionBox(g, out Rect box))
            {
                Outline(box, new Color(1f, 1f, 1f, 0.40f));
                if (!Application.isPlaying) EditorGUIUtility.AddCursorRect(box, MouseCursor.MoveArrow); // middle = move all
                if (box.width > 2f * SelPad + 4f)
                {
                    var lh = new Rect(box.x - 3, box.y, 6, box.height);
                    var rh = new Rect(box.xMax - 3, box.y, 6, box.height);
                    EditorGUI.DrawRect(new Rect(box.x - 1, box.y, 3, box.height), Color.white);      // left scale handle
                    EditorGUI.DrawRect(new Rect(box.xMax - 2, box.y, 3, box.height), Color.white);   // right scale handle
                    if (!Application.isPlaying)
                    {
                        EditorGUIUtility.AddCursorRect(lh, MouseCursor.ResizeHorizontal);
                        EditorGUIUtility.AddCursorRect(rh, MouseCursor.ResizeHorizontal);
                    }
                }
            }
        }

        const float SelPad = 8f;   // padding around selected keys for the bounding box

        // Bounding box (screen space) around the selected keys, padded — the move/scale affordance.
        bool SelectionBox(Rect g, out Rect box)
        {
            box = default;
            if (!GetSelScreen(g, out float x0, out float x1, out float y0, out float y1)) return false;
            box = Rect.MinMaxRect(x0 - SelPad, Mathf.Max(g.y, y0 - SelPad), x1 + SelPad, Mathf.Min(g.yMax, y1 + SelPad));
            return true;
        }

        static void Outline(Rect r, Color c)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 1), c);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1, r.width, 1), c);
            EditorGUI.DrawRect(new Rect(r.x, r.y, 1, r.height), c);
            EditorGUI.DrawRect(new Rect(r.xMax - 1, r.y, 1, r.height), c);
        }

        // Font triangle glyphs sit on the text baseline and look top-heavy. Drawing the disclosure marker as
        // geometry keeps it optically centered regardless of editor scale, skin, or font metrics.
        static void DrawDisclosureTriangle(Rect rect, bool expanded, Color color)
        {
            Vector2 c = rect.center;
            Vector3[] points = expanded
                ? new[] { new Vector3(c.x - 5f, c.y - 3.5f), new Vector3(c.x + 5f, c.y - 3.5f), new Vector3(c.x, c.y + 4.5f) }
                : new[] { new Vector3(c.x - 3.5f, c.y - 5f), new Vector3(c.x - 3.5f, c.y + 5f), new Vector3(c.x + 4.5f, c.y) };
            Handles.BeginGUI();
            Color previous = Handles.color;
            Handles.color = color;
            Handles.DrawAAConvexPolygon(points);
            Handles.color = previous;
            Handles.EndGUI();
        }

        // Screen-space bounds of the selected keys (as drawn: lane centres in Dopesheet, value points in Curves).
        bool GetSelScreen(Rect g, out float xMin, out float xMax, out float yMin, out float yMax)
        {
            xMin = yMin = 1e9f; xMax = yMax = -1e9f; int n = 0;
            foreach (var (t, k) in selKeys)
            {
                if (t < 0 || t >= timeline.tracks.Count) continue;
                var c = timeline.tracks[t].curve; if (c == null || k < 0 || k >= c.length) continue;
                float px = TimeToX(c[k].time), py;
                if (tab == Tab.Dopesheet) py = g.y + ContentTopForTrack(t) - rowScroll + RowH * 0.5f;
                else
                {
                    if (t != selectedTrack) continue;
                    var f = timeline.tracks[t].Resolve();
                    float mn = f != null ? f.Min : 0f, mx = f != null ? f.Max : 1f;
                    py = ValueToY(g, c[k].value, mn, mx);
                }
                xMin = Mathf.Min(xMin, px); xMax = Mathf.Max(xMax, px);
                yMin = Mathf.Min(yMin, py); yMax = Mathf.Max(yMax, py); n++;
            }
            return n > 0;
        }

        void AddKeyAt(Rect g, Vector2 m)
        {
            if (Application.isPlaying) return;
            int trk = selectedTrack;
            if (tab == Tab.Dopesheet)
            {
                int row = TrackAtContentY(m.y - g.y + rowScroll);
                if (row >= 0) trk = row;
            }
            if (trk < 0 || trk >= timeline.tracks.Count) return;
            var tr = timeline.tracks[trk];
            if (tr.locked || !timeline.OwnsTarget(tr.target) ||
                (tr.IsEvent ? tr.ResolveEvent() == null : tr.Resolve() == null)) return;
            if (tr.curve == null) tr.curve = new AnimationCurve();
            var f = tr.Resolve(); float min = f != null ? f.Min : 0f, max = f != null ? f.Max : 1f;
            float t = SnapTime(Mathf.Clamp(XToTime(m.x), 0f, timeline.duration));
            float v = tab == Tab.Curves ? Mathf.Clamp(YToValue(g, m.y, min, max), Mathf.Min(min, max), Mathf.Max(min, max))
                                        : tr.Evaluate(t);
            RecordTimeline("Add Key");
            TimelineKeyState[] existing = CaptureCurveStates(tr.curve);
            int idx = tr.curve.AddKey(t, v);
            bool added = idx >= 0;
            if (idx < 0) idx = FindKeyNear(tr.curve, t);
            if (added)
            {
                SetClampedAuto(tr.curve, idx);
                RestoreFreeTangentHandles(tr.curve, existing);
                idx = FindKeyAtTime(tr.curve, t);
            }
            if (idx >= 0) { selectedTrack = trk; selectedKey = idx; selKeys.Clear(); selKeys.Add((trk, idx)); }
        }

        bool DeleteSelectedKeys()
        {
            if (Application.isPlaying || selKeys.Count == 0) return false;
            RecordTimeline("Delete Keys");
            var byTrack = new Dictionary<int, List<int>>();
            foreach (var (t, k) in selKeys)
            {
                if (t >= 0 && t < timeline.tracks.Count && timeline.tracks[t].locked) continue;
                if (!byTrack.TryGetValue(t, out var list)) { list = new List<int>(); byTrack[t] = list; }
                list.Add(k);
            }
            foreach (var kv in byTrack)
            {
                if (kv.Key < 0 || kv.Key >= timeline.tracks.Count) continue;
                var c = timeline.tracks[kv.Key].curve; if (c == null) continue;
                var removed = new HashSet<int>(kv.Value);
                var remaining = new List<TimelineKeyState>(Mathf.Max(0, c.length - removed.Count));
                for (int i = 0; i < c.length; i++)
                    if (!removed.Contains(i)) remaining.Add(CaptureKeyState(c, i));
                CommitCurveStates(c, remaining);
            }
            selKeys.Clear(); selectedKey = -1;
            return true;
        }

        void CopySelectedKeys()
        {
            copiedKeys.Clear();
            var valid = new List<(int track, int key, float time)>();
            foreach (var selection in selKeys)
            {
                if (selection.track < 0 || selection.track >= timeline.tracks.Count) continue;
                AnimationCurve curve = timeline.tracks[selection.track].curve;
                if (curve == null || selection.key < 0 || selection.key >= curve.length) continue;
                valid.Add((selection.track, selection.key, curve[selection.key].time));
            }
            if (valid.Count == 0) return;
            valid.Sort((a, b) =>
            {
                int timeOrder = a.time.CompareTo(b.time);
                if (timeOrder != 0) return timeOrder;
                int trackOrder = a.track.CompareTo(b.track);
                return trackOrder != 0 ? trackOrder : a.key.CompareTo(b.key);
            });

            float first = valid[0].time;
            for (int i = 0; i < valid.Count; i++)
            {
                var selection = valid[i];
                BrazeTrack track = timeline.tracks[selection.track];
                TimelineKeyState state = CaptureKeyState(track.curve, selection.key);
                copiedKeys.Add(new CopiedTimelineKey(selection.track, track, state, selection.time - first));
            }
        }

        void PasteKeysAtPlayhead()
        {
            if (Application.isPlaying || copiedKeys.Count == 0) return;
            RecordTimeline("Paste Timeline Keys");
            var pasted = new HashSet<(int track, int key)>();
            float baseTime = SnapTime(Mathf.Max(0f, timeline.time));
            var byTrack = new Dictionary<int, List<TimelineKeyState>>();
            for (int i = 0; i < copiedKeys.Count; i++)
            {
                var source = copiedKeys[i];
                int trackIndex = ResolveCopiedTrack(source);
                if (trackIndex < 0 || timeline.tracks[trackIndex].locked) continue;
                if (!byTrack.TryGetValue(trackIndex, out List<TimelineKeyState> states))
                {
                    states = new List<TimelineKeyState>();
                    byTrack.Add(trackIndex, states);
                }
                states.Add(source.state.AtTime(SnapTime(baseTime + source.offset)));
            }

            int primaryTrack = -1, primaryKey = -1;
            float primaryTime = float.PositiveInfinity;
            foreach (var pair in byTrack)
            {
                int trackIndex = pair.Key;
                BrazeTrack track = timeline.tracks[trackIndex];
                AnimationCurve curve = track.curve ??= new AnimationCurve();
                List<TimelineKeyState> pending = pair.Value;
                pending.Sort((a, b) => a.key.time.CompareTo(b.key.time));

                // Snapping can make two very close source keys land on the same frame. Unity keeps one key per
                // property/frame; replace deterministically instead of repeatedly moving a single curve index.
                for (int i = pending.Count - 1; i > 0; i--)
                    if (SameKeyTime(pending[i - 1].key.time, pending[i].key.time)) pending.RemoveAt(i - 1);

                var merged = new List<TimelineKeyState>();
                for (int i = 0; i < curve.length; i++) merged.Add(CaptureKeyState(curve, i));
                merged.RemoveAll(existing => pending.Exists(candidate => SameKeyTime(existing.key.time, candidate.key.time)));
                merged.AddRange(pending);
                merged.Sort((a, b) => a.key.time.CompareTo(b.key.time));

                CommitCurveStates(curve, merged);

                for (int i = 0; i < pending.Count; i++)
                {
                    int index = FindKeyAtTime(curve, pending[i].key.time);
                    if (index < 0) continue;
                    pasted.Add((trackIndex, index));
                    if (pending[i].key.time < primaryTime ||
                        (Mathf.Approximately(pending[i].key.time, primaryTime) && trackIndex < primaryTrack))
                    {
                        primaryTime = pending[i].key.time;
                        primaryTrack = trackIndex;
                        primaryKey = index;
                    }
                }
            }
            ReplaceSelection(pasted);
            if (primaryTrack >= 0) { selectedTrack = primaryTrack; selectedKey = primaryKey; }
            Repaint();
        }

        static TimelineKeyState CaptureKeyState(AnimationCurve curve, int index)
        {
            return new TimelineKeyState(curve[index],
                AnimationUtility.GetKeyLeftTangentMode(curve, index),
                AnimationUtility.GetKeyRightTangentMode(curve, index),
                AnimationUtility.GetKeyBroken(curve, index));
        }

        static TimelineKeyState[] CaptureCurveStates(AnimationCurve curve)
        {
            var states = new TimelineKeyState[curve?.length ?? 0];
            for (int i = 0; i < states.Length; i++) states[i] = CaptureKeyState(curve, i);
            return states;
        }

        static void ApplyKeyState(AnimationCurve curve, int index, TimelineKeyState state)
        {
            AnimationUtility.SetKeyLeftTangentMode(curve, index, state.leftMode);
            AnimationUtility.SetKeyRightTangentMode(curve, index, state.rightMode);
            AnimationUtility.SetKeyBroken(curve, index, state.broken);
            ApplyFreeTangentHandles(curve, index, state);
        }

        static void CommitCurveStates(AnimationCurve curve, IReadOnlyList<TimelineKeyState> states)
        {
            var keys = new Keyframe[states.Count];
            for (int i = 0; i < states.Count; i++) keys[i] = states[i].key;
            curve.keys = keys;
            for (int i = 0; i < states.Count; i++) ApplyKeyState(curve, i, states[i]);
            RestoreFreeTangentHandles(curve, states);
        }

        static void RestoreFreeTangentHandles(AnimationCurve curve, IReadOnlyList<TimelineKeyState> states)
        {
            for (int i = 0; i < states.Count; i++)
            {
                TimelineKeyState state = states[i];
                bool restoreIn = state.leftMode == AnimationUtility.TangentMode.Free;
                bool restoreOut = state.rightMode == AnimationUtility.TangentMode.Free;
                if (!restoreIn && !restoreOut) continue;
                int index = FindKeyAtTime(curve, state.key.time);
                if (index < 0) continue;
                ApplyFreeTangentHandles(curve, index, state);
            }
        }

        static void ApplyFreeTangentHandles(AnimationCurve curve, int index, TimelineKeyState state)
        {
            bool restoreIn = state.leftMode == AnimationUtility.TangentMode.Free;
            bool restoreOut = state.rightMode == AnimationUtility.TangentMode.Free;
            if (!restoreIn && !restoreOut) return;
            // Tangent-mode APIs and topology changes can recalculate numeric slopes. Restore only Free handles;
            // Auto/Linear/Constant handles must remain governed by their modes and current neighbours.
            Keyframe key = curve[index];
            if (restoreIn)
            {
                key.inTangent = state.key.inTangent;
                key.inWeight = state.key.inWeight;
            }
            if (restoreOut)
            {
                key.outTangent = state.key.outTangent;
                key.outWeight = state.key.outWeight;
            }
            key.weightedMode = state.key.weightedMode;
            curve.MoveKey(index, key);
        }

        int ResolveCopiedTrack(CopiedTimelineKey source)
        {
            if (source.sourceTrack >= 0 && source.sourceTrack < timeline.tracks.Count &&
                ClipboardTrackMatches(timeline.tracks[source.sourceTrack], source)) return source.sourceTrack;
            for (int i = 0; i < timeline.tracks.Count; i++)
                if (ClipboardTrackMatches(timeline.tracks[i], source)) return i;
            return -1;
        }

        static bool ClipboardTrackMatches(BrazeTrack track, CopiedTimelineKey source)
        {
            return track != null && string.Equals(track.TargetId, source.targetId, StringComparison.Ordinal) &&
                   string.Equals(track.BindingKey, source.bindingKey, StringComparison.Ordinal);
        }

        static bool SameKeyTime(float a, float b) => Mathf.Abs(a - b) <= 1e-5f;

        static int FindKeyAtTime(AnimationCurve curve, float time)
        {
            for (int i = 0; i < curve.length; i++)
                if (SameKeyTime(curve[i].time, time)) return i;
            return -1;
        }

        void KeyContextMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Copy Keys"), false, CopySelectedKeys);
            if (copiedKeys.Count > 0) menu.AddItem(new GUIContent("Paste at Playhead"), false, PasteKeysAtPlayhead);
            else menu.AddDisabledItem(new GUIContent("Paste at Playhead"));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent(selKeys.Count > 1 ? "Delete Keys" : "Delete Key"), false, () => { DeleteSelectedKeys(); Repaint(); });
            menu.AddSeparator("");
            BrazeAnimatableField field = selectedTrack >= 0 && selectedTrack < timeline.tracks.Count
                ? timeline.tracks[selectedTrack].Resolve() : null;
            if (field != null && field.IsDiscrete)
                menu.AddDisabledItem(new GUIContent("Tangents/Stepped"));
            else
            {
                menu.AddItem(new GUIContent("Tangents/Auto (Clamped)"), false, () => SetTangent(0));
                menu.AddItem(new GUIContent("Tangents/Flat"), false, () => SetTangent(1));
                menu.AddItem(new GUIContent("Tangents/Linear"), false, () => SetTangent(2));
            }
            menu.ShowAsContext();
        }

        void SetTangent(int mode)
        {
            if (Application.isPlaying || selectedTrack < 0 || selectedKey < 0) return;
            var tr = timeline.tracks[selectedTrack];
            if (tr.curve == null || tr.locked) return;
            RecordTimeline("Set Tangent");
            if (mode == 0)
            {
                AnimationUtility.SetKeyBroken(tr.curve, selectedKey, false);
                AnimationUtility.SetKeyLeftTangentMode(tr.curve, selectedKey, AnimationUtility.TangentMode.ClampedAuto);
                AnimationUtility.SetKeyRightTangentMode(tr.curve, selectedKey, AnimationUtility.TangentMode.ClampedAuto);
            }
            else if (mode == 1)
            {
                var k = tr.curve[selectedKey]; k.inTangent = 0; k.outTangent = 0;
                selectedKey = tr.curve.MoveKey(selectedKey, k);
                AnimationUtility.SetKeyBroken(tr.curve, selectedKey, false);
                AnimationUtility.SetKeyLeftTangentMode(tr.curve, selectedKey, AnimationUtility.TangentMode.Free);
                AnimationUtility.SetKeyRightTangentMode(tr.curve, selectedKey, AnimationUtility.TangentMode.Free);
            }
            else
            {
                AnimationUtility.SetKeyBroken(tr.curve, selectedKey, false);
                AnimationUtility.SetKeyLeftTangentMode(tr.curve, selectedKey, AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(tr.curve, selectedKey, AnimationUtility.TangentMode.Linear);
            }
            Repaint();
        }

        static int FindKeyNear(AnimationCurve c, float t)
        {
            int best = -1; float bd = 1e9f;
            for (int i = 0; i < c.length; i++) { float d = Mathf.Abs(c[i].time - t); if (d < bd) { bd = d; best = i; } }
            return best;
        }

        // New keys use Unity's clamped-auto default. Existing keys keep their authored tangent modes through
        // move, scale, value edits, deletion and paste operations.
        static void SetClampedAuto(AnimationCurve curve, int index)
        {
            if (curve == null || index < 0 || index >= curve.length) return;
            AnimationUtility.SetKeyLeftTangentMode(curve, index, AnimationUtility.TangentMode.ClampedAuto);
            AnimationUtility.SetKeyRightTangentMode(curve, index, AnimationUtility.TangentMode.ClampedAuto);
        }

        // Set the track's value at the current playhead: update the key there, or add one (like Unity —
        // editing a property's value drops a key at the playhead).
        void SetValueAtPlayhead(int trackIndex, float value, bool recordUndo = true)
        {
            if (Application.isPlaying || trackIndex < 0 || trackIndex >= timeline.tracks.Count) return;
            if (timeline.tracks[trackIndex].locked) return;
            // Match Unity's Animation workflow: editing an animated value implicitly enters non-destructive preview
            // so the authored result is visible immediately in Scene/Game views.
            if (!Application.isPlaying && !preview && !simulate) EnterPreview();
            if (recordUndo) RecordTimeline("Set Value");
            else
            {
                MarkTimelineDirty();
                previewDirty = true;
                recordSnapshotDirty = true;
            }
            int idx = UpsertKeyAtPlayhead(trackIndex, value);
            selectedTrack = trackIndex; selectedKey = idx;
            selKeys.Clear(); if (idx >= 0) selKeys.Add((trackIndex, idx));
            if (recordMode) CaptureRecordSnapshot();
            RefreshEditedValuePreview();
        }

        void RefreshEditedValuePreview()
        {
            if (timeline == null) return;
            if (Application.isPlaying) timeline.Evaluate(timeline.time);
            else if (preview || simulate) SampleAt(timeline.time);
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
            Repaint();
        }

        void DrawTypedValue(int trackIndex, BrazeAnimatableField field, Rect rect)
        {
            BrazeTrack track = timeline.tracks[trackIndex];
            float shown = track.Evaluate(timeline.time);
            int scrubId = GUIUtility.GetControlID("BrazeTimelineValueScrubber".GetHashCode(), FocusType.Passive, rect);
            string controlName = "BrazeTimelineValue_" + trackIndex;

            DrawValueScrubber(trackIndex, field, scrubId, rect, shown, controlName);

            EditorGUI.BeginChangeCheck();
            float edited = shown;
            GUI.SetNextControlName(controlName);
            switch (field.Kind)
            {
                case BrazeAnimationValueKind.Boolean:
                    edited = EditorGUI.Toggle(rect, shown >= 0.5f) ? 1f : 0f;
                    break;
                case BrazeAnimationValueKind.Integer:
                    edited = EditorGUI.IntField(rect, Mathf.RoundToInt(shown));
                    break;
                case BrazeAnimationValueKind.Enum:
                    if (field.EnumType != null)
                    {
                        Enum current = (Enum)Enum.ToObject(field.EnumType, Mathf.RoundToInt(shown));
                        edited = Convert.ToSingle(EditorGUI.EnumPopup(rect, current));
                    }
                    break;
                default:
                    edited = EditorGUI.FloatField(rect, shown);
                    break;
            }
            if (EditorGUI.EndChangeCheck()) SetValueAtPlayhead(trackIndex, field.Coerce(edited));
            // Numeric fields register their own text cursor while drawing. Register last so the input consistently
            // advertises horizontal value scrubbing instead of reverting to the I-beam cursor on hover.
            if (!Application.isPlaying) EditorGUIUtility.AddCursorRect(rect, MouseCursor.SlideArrow);
        }

        void DrawValueScrubber(int trackIndex, BrazeAnimatableField field, int controlId, Rect rect, float shown,
            string controlName)
        {
            if (Application.isPlaying) return;
            Event e = Event.current;
            bool active = GUIUtility.hotControl == controlId;
            bool hover = rect.Contains(e.mousePosition);

            if (e.type == EventType.MouseDown && e.button == 0 && hover)
            {
                GUIUtility.hotControl = controlId;
                GUIUtility.keyboardControl = 0;
                valueScrubTrack = trackIndex;
                valueScrubStart = shown;
                valueScrubPixels = 0f;
                valueScrubRecorded = false;
                valueScrubDragged = false;
                EditorGUIUtility.SetWantsMouseJumping(1);
                e.Use();
                return;
            }

            if (!active || valueScrubTrack != trackIndex) return;

            if (e.type == EventType.MouseDrag)
            {
                valueScrubPixels += e.delta.x;
                if (!valueScrubDragged && Mathf.Abs(valueScrubPixels) < 2f)
                {
                    e.Use();
                    return;
                }
                valueScrubDragged = true;
                float value = ScrubValue(field, valueScrubStart, valueScrubPixels, e);
                float current = timeline.tracks[trackIndex].Evaluate(timeline.time);
                if (!Mathf.Approximately(value, current))
                {
                    if (!valueScrubRecorded)
                    {
                        RecordTimeline("Scrub Value");
                        valueScrubRecorded = true;
                    }
                    SetValueAtPlayhead(trackIndex, field.Coerce(value), false);
                }
                e.Use();
                Repaint();
                return;
            }

            if (e.type == EventType.MouseUp || e.type == EventType.MouseLeaveWindow)
            {
                bool clicked = !valueScrubDragged && e.type == EventType.MouseUp;
                EndValueScrubGesture();
                if (clicked) HandleValueFieldClick(trackIndex, field, rect, shown, controlName);
                e.Use();
                Repaint();
            }
        }

        void HandleValueFieldClick(int trackIndex, BrazeAnimatableField field, Rect rect, float shown,
            string controlName)
        {
            if (field.Kind == BrazeAnimationValueKind.Boolean)
            {
                SetValueAtPlayhead(trackIndex, shown >= 0.5f ? 0f : 1f);
                return;
            }

            if (field.Kind == BrazeAnimationValueKind.Enum && field.EnumType != null)
            {
                var menu = new GenericMenu();
                Array values = Enum.GetValues(field.EnumType);
                for (int i = 0; i < values.Length; i++)
                {
                    object enumValue = values.GetValue(i);
                    float value = Convert.ToSingle(enumValue);
                    string label = ObjectNames.NicifyVariableName(enumValue.ToString());
                    menu.AddItem(new GUIContent(label), Mathf.RoundToInt(shown) == Mathf.RoundToInt(value),
                        () => SetValueAtPlayhead(trackIndex, field.Coerce(value)));
                }
                menu.DropDown(rect);
                return;
            }

            EditorGUI.FocusTextInControl(controlName);
        }

        void EndValueScrubGesture()
        {
            GUIUtility.hotControl = 0;
            valueScrubTrack = -1;
            valueScrubPixels = 0f;
            valueScrubRecorded = false;
            valueScrubDragged = false;
            EditorGUIUtility.SetWantsMouseJumping(0);
        }

        static float ScrubValue(BrazeAnimatableField field, float start, float pixels, Event e)
        {
            float modifier = e.alt ? 0.01f : e.shift ? 0.1f : (e.control || e.command) ? 10f : 1f;
            switch (field.Kind)
            {
                case BrazeAnimationValueKind.Boolean:
                    return start >= 0.5f
                        ? (pixels < -6f ? 0f : 1f)
                        : (pixels > 6f ? 1f : 0f);

                case BrazeAnimationValueKind.Integer:
                case BrazeAnimationValueKind.Enum:
                    return start + Mathf.Round(pixels * modifier / 8f);

                default:
                    float range = Mathf.Abs(field.Max - field.Min);
                    float step = Mathf.Max(0.0001f, range > 1e-5f ? range / 400f : Mathf.Max(1f, Mathf.Abs(start)) / 200f);
                    step *= modifier;
                    float value = start + pixels * step;
                    return Mathf.Round(value / step) * step;
            }
        }

        int UpsertKeyAtPlayhead(int trackIndex, float value)
        {
            if (trackIndex < 0 || trackIndex >= timeline.tracks.Count) return -1;
            var tr = timeline.tracks[trackIndex];
            if (tr == null || tr.locked || !timeline.OwnsTarget(tr.target) ||
                (tr.IsEvent ? tr.ResolveEvent() == null : tr.Resolve() == null)) return -1;
            if (tr.curve == null) tr.curve = new AnimationCurve();
            float t = SnapTime(timeline.time);
            int idx = -1;
            for (int k = 0; k < tr.curve.length; k++)
                if (SameKeyTime(tr.curve[k].time, t)) { idx = k; break; }
            if (idx >= 0)
            {
                TimelineKeyState state = CaptureKeyState(tr.curve, idx);
                Keyframe key = state.key;
                key.value = value;
                idx = tr.curve.MoveKey(idx, key);
                if (idx >= 0) ApplyKeyState(tr.curve, idx, state.WithKey(key));
            }
            else
            {
                TimelineKeyState[] existing = CaptureCurveStates(tr.curve);
                idx = tr.curve.AddKey(t, value);
                bool added = idx >= 0;
                if (idx < 0) idx = FindKeyNear(tr.curve, t);
                if (added)
                {
                    SetClampedAuto(tr.curve, idx);
                    RestoreFreeTangentHandles(tr.curve, existing);
                    idx = FindKeyAtTime(tr.curve, t);
                }
            }
            return idx;
        }

        // ── track add / remove ──
        void AddTrackMenu(Rect anchor, Component target)
        {
            if (Application.isPlaying || target == null || timeline == null) return;
            if (!timeline.OwnsTarget(target))
            {
                ShowNotification(new GUIContent("Move the target GameObject under Braze Timeline first."));
                return;
            }
            PopupWindow.Show(anchor, new ParameterBrowserPopup(this, target));
        }

        float NewTrackSpan() => timeline.duration;

        void AddTrack(Component comp, BrazeAnimatableField fld)
        {
            if (Application.isPlaying || timeline == null || !timeline.OwnsTarget(comp))
            {
                ShowNotification(new GUIContent("Only child objects of this Timeline can be animated."));
                return;
            }
            int existing = FindTrackIndex(comp, fld.Id, fld.Member);
            if (existing >= 0)
            {
                selectedTrack = existing;
                selectedTracks.Clear(); selectedTracks.Add(existing);
                Repaint();
                return;
            }
            RecordTimeline("Add Track");
            timeline.AddTarget(comp);
            ExpandCard(comp);
            float v = fld.Get(comp);
            timeline.tracks.Add(new BrazeTrack
            {
                target = comp,
                member = fld.Member,
                parameterId = fld.Id,
                curve = new AnimationCurve(new Keyframe(0f, v), new Keyframe(NewTrackSpan(), v)),
                enabled = true,
                color = TrackColor(timeline.tracks.Count),
            });
            selectedTrack = timeline.tracks.Count - 1;
            selectedTracks.Clear(); selectedTracks.Add(selectedTrack);
            selectedKey = -1; selKeys.Clear();
            Repaint();
        }

        void AddEventTrack(Component component, BrazeTimelineEventDescriptor descriptor)
        {
            if (Application.isPlaying || timeline == null || !timeline.OwnsTarget(component))
            {
                ShowNotification(new GUIContent("Only child objects of this Timeline can be animated."));
                return;
            }
            int existing = FindEventTrackIndex(component, descriptor.Id, descriptor.Method);
            if (existing >= 0) { selectedTrack = existing; selectedTracks.Clear(); selectedTracks.Add(existing); Repaint(); return; }
            RecordTimeline("Add Event Track");
            timeline.AddTarget(component);
            ExpandCard(component);
            timeline.tracks.Add(new BrazeTrack
            {
                target = component,
                eventId = descriptor.Id,
                eventMethod = descriptor.Method,
                curve = new AnimationCurve(new Keyframe(Mathf.Clamp(timeline.time, 0f, timeline.duration), 1f)),
                enabled = true,
                color = TrackColor(timeline.tracks.Count),
            });
            selectedTrack = timeline.tracks.Count - 1;
            selectedTracks.Clear(); selectedTracks.Add(selectedTrack);
            selectedKey = 0; selKeys.Clear(); selKeys.Add((selectedTrack, 0));
            Repaint();
        }

        void RemoveCard(BrazeTimelineTarget card)
        {
            if (Application.isPlaying || timeline == null || card == null) return;
            Component target = card.target;
            RecordTimeline("Remove Timeline Card");
            for (int i = timeline.tracks.Count - 1; i >= 0; i--)
                if (card.Matches(timeline.tracks[i])) timeline.tracks.RemoveAt(i);
            timeline.targets.Remove(card);
            if (target != null) selectedCards.Remove(target);
            selectedTracks.Clear(); selKeys.Clear(); selectedKey = -1;
            selectedTrack = timeline.tracks.Count > 0 ? 0 : -1;
            Repaint();
        }

        void HandleObjectDrop(Rect area)
        {
            Event e = Event.current;
            if (!area.Contains(e.mousePosition) || (e.type != EventType.DragUpdated && e.type != EventType.DragPerform)) return;

            var candidates = new List<Component>();
            var seen = new HashSet<Component>();
            bool rejectedHierarchy = false;
            foreach (UnityEngine.Object reference in DragAndDrop.objectReferences)
            {
                if (reference is GameObject go)
                {
                    int before = candidates.Count;
                    foreach (Component component in go.GetComponents<Component>())
                    {
                        if (component is Transform || !IsDroppableComponent(component) || !seen.Add(component)) continue;
                        if (timeline.OwnsTarget(component)) candidates.Add(component);
                        else rejectedHierarchy = true;
                    }
                    // Transform is useful alongside Braze parameters, but a plain non-Braze GameObject is not accepted.
                    if (candidates.Count > before && go.transform != null && seen.Add(go.transform)) candidates.Insert(before, go.transform);
                }
                else if (reference is Component component && IsDroppableComponent(component) && seen.Add(component))
                {
                    if (timeline.OwnsTarget(component)) candidates.Add(component);
                    else rejectedHierarchy = true;
                }
            }

            if (rejectedHierarchy)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                if (e.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    ShowNotification(new GUIContent("Rejected: move animated objects under this Braze Timeline first."));
                    e.Use();
                }
                else
                {
                    EditorGUI.DrawRect(area, new Color(0.55f, 0.08f, 0.05f, 0.16f));
                    Repaint();
                    e.Use();
                }
                return;
            }
            if (candidates.Count == 0) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (e.type == EventType.DragUpdated)
            {
                EditorGUI.DrawRect(area, new Color(Accent.r, Accent.g, Accent.b, 0.10f));
                Outline(new Rect(area.x + 4, area.y + 4, area.width - 8, area.height - 8), Accent);
                Repaint(); e.Use(); return;
            }

            DragAndDrop.AcceptDrag();
            RecordTimeline("Add Timeline Cards");
            selectedCards.Clear();
            for (int i = 0; i < candidates.Count; i++)
            {
                Component component = candidates[i];
                timeline.AddTarget(component);
            }
            // One component card is selected by default, keeping its local + Add action ready even when the dropped
            // object carries many Braze components. Shift/Cmd-click still broadens the recording selection.
            Component preferred = candidates.Find(component => !(component is Transform)) ?? candidates[0];
            selectedCards.Add(preferred);
            e.Use(); Repaint();
        }

        static bool IsDroppableComponent(Component component)
        {
            if (component == null || component is BrazeTimeline) return false;
            if (component is Transform) return true;
            string ns = component.GetType().Namespace;
            return !string.IsNullOrEmpty(ns) && (ns == "BrazeFX" || ns.StartsWith("BrazeFX.", StringComparison.Ordinal)) &&
                   (BrazeAnimatableRegistry.IsAnimatable(component) || BrazeTimelineEventRegistry.Events(component).Count > 0);
        }

        List<Component> CollectTargets()
        {
            var result = new List<Component>();
            if (timeline == null) return result;
            for (int i = 0; i < timeline.targets.Count; i++)
            {
                Component component = timeline.targets[i]?.target;
                if (component != null && timeline.OwnsTarget(component) && selectedCards.Contains(component) &&
                    (BrazeAnimatableRegistry.IsAnimatable(component) || BrazeTimelineEventRegistry.Events(component).Count > 0))
                    result.Add(component);
            }
            return result;
        }

        float SnapTime(float value)
        {
            if (timeline == null || !timeline.snapToFrames) return value;
            int fps = Mathf.Clamp(timeline.frameRate, 1, 240);
            return Mathf.Round(value * fps) / fps;
        }

        // ── view helpers ──
        float ClampLeftWidth(float width)
        {
            float max = Mathf.Min(MaxLeftW, Mathf.Max(MinLeftW, position.width - MinGraphW));
            return Mathf.Clamp(width, MinLeftW, max);
        }

        void ApplyFit()
        {
            float duration = timeline != null && float.IsFinite(timeline.duration)
                ? timeline.duration : BrazeTimeline.DefaultDuration;
            pps = plotW / Mathf.Max(0.05f, duration);
            viewStart = 0f;
            vStart = -0.05f; vSpan = 1.10f;
            rowScroll = 0f;
            viewInit = true; needFit = false;
        }

        void ClampView()
        {
            // Don't let the view scroll into negative time (the sim has no t < 0). Keep ~16px of headroom
            // left of t=0 so the first keyframe isn't glued to the edge. Right side stays unbounded.
            viewStart = Mathf.Max(viewStart, -16f / pps);

            float maxScroll = Mathf.Max(0f, rowsContentH - graphRect.height);
            rowScroll = Mathf.Clamp(rowScroll, 0f, maxScroll);
        }

        void RecordTimeline(string label)
        {
            // Complete-object snapshots are immediate. This is essential for gestures: Move/Scale begins on one
            // IMGUI event and mutates nested AnimationCurves on later MouseDrag events, a span during which
            // Undo.RecordObject can discard its end-of-event diff as a no-op.
            Undo.RegisterCompleteObjectUndo(timeline, label);
            MarkTimelineDirty();
            previewDirty = true;
            recordSnapshotDirty = true;
        }

        void MarkTimelineDirty()
        {
            if (timeline == null) return;
            EditorUtility.SetDirty(timeline);
            if (Application.isPlaying) return;
            var scene = timeline.gameObject.scene;
            if (scene.IsValid() && scene.isLoaded) EditorSceneManager.MarkSceneDirty(scene);
        }

        // ── edit-mode typed preview ──────────────────────────────────────────────────────────────
        // Directly drives registered parameters and restores an explicit scalar-channel baseline on exit.
        void ScrubTo(float t)
        {
            // Free scrubbing: only floored at 0, no upper clamp — the playhead can go past the last key /
            // the duration (curves hold their last value beyond it), matching Unity's timeline.
            timeline.time = Mathf.Max(0f, t);
            if (preview && !Application.isPlaying) SampleAt(timeline.time);
        }

        void SnapToAdjacentKeyframe(int direction)
        {
            if (timeline == null || simulate) return;
            if (!TryFindAdjacentKeyframe(direction, out float t)) return;

            if (Application.isPlaying) timeline.Scrub(t);
            else if (recordMode) ScrubTo(t);
            else
            {
                EnterPreview();
                ScrubTo(t);
            }

            Repaint();
        }

        void SnapToEdgeKeyframe(bool first)
        {
            if (timeline == null) return;
            if (!TryFindEdgeKeyframe(first, out float t)) return;

            if (Application.isPlaying) timeline.Scrub(t);
            else if (simulate)
            {
                if (first) ResetSimState(t, simPlaying);
                else
                {
                    timeline.time = Mathf.Max(0f, t);
                    SampleAt(timeline.time);
                }
            }
            else if (recordMode) ScrubTo(t);
            else
            {
                EnterPreview();
                ScrubTo(t);
            }

            Repaint();
        }

        bool TryFindAdjacentKeyframe(int direction, out float keyTime)
        {
            keyTime = timeline != null ? timeline.time : 0f;
            if (timeline == null || direction == 0) return false;

            float now = timeline.time;
            float eps = Mathf.Max(1e-4f, Mathf.Max(Mathf.Abs(now), Mathf.Max(0.05f, timeline.duration)) * 1e-5f);
            bool found = false;
            float best = direction < 0 ? float.NegativeInfinity : float.PositiveInfinity;

            for (int i = 0; i < timeline.tracks.Count; i++)
            {
                var tr = timeline.tracks[i];
                var curve = tr != null ? tr.curve : null;
                if (curve == null) continue;

                for (int k = 0; k < curve.length; k++)
                {
                    float t = curve[k].time;
                    if (direction < 0)
                    {
                        if (t >= now - eps || (found && t <= best)) continue;
                        best = t;
                        found = true;
                    }
                    else
                    {
                        if (t <= now + eps || (found && t >= best)) continue;
                        best = t;
                        found = true;
                    }
                }
            }

            if (!found) return false;
            keyTime = best;
            return true;
        }

        bool TryFindEdgeKeyframe(bool first, out float keyTime)
        {
            keyTime = timeline != null ? timeline.time : 0f;
            if (timeline == null) return false;

            bool found = false;
            float best = first ? float.PositiveInfinity : float.NegativeInfinity;
            for (int i = 0; i < timeline.tracks.Count; i++)
            {
                var tr = timeline.tracks[i];
                var curve = tr != null ? tr.curve : null;
                if (curve == null) continue;

                for (int k = 0; k < curve.length; k++)
                {
                    float t = curve[k].time;
                    if (first)
                    {
                        if (found && t >= best) continue;
                        best = t;
                    }
                    else
                    {
                        if (found && t <= best) continue;
                        best = t;
                    }
                    found = true;
                }
            }

            if (!found) return false;
            keyTime = best;
            return true;
        }

        bool DidLoopToStart(float before, float after, float dt)
        {
            if (timeline == null || timeline.loopMode != BrazeTimeline.LoopMode.Loop || dt <= 0f) return false;
            float d = Mathf.Max(0.0001f, timeline.loopEnd);
            float eps = Mathf.Max(1e-4f, d * 1e-5f);
            return before + dt >= d - eps && after <= before + eps;
        }

        void AddKeyAtPlayhead(bool allTracks)
        {
            if (Application.isPlaying || timeline == null || simulate || timeline.tracks.Count == 0) return;

            var keyed = new HashSet<(int track, int key)>();
            RecordTimeline(allTracks ? "Add Keyframes" : "Add Keyframe");

            if (allTracks)
            {
                for (int i = 0; i < timeline.tracks.Count; i++)
                {
                    if (TryReadTrackValue(i, out float value))
                    {
                        int key = UpsertKeyAtPlayhead(i, value);
                        if (key >= 0) keyed.Add((i, key));
                    }
                }
            }
            else if (TryReadTrackValue(selectedTrack, out float value))
            {
                int key = UpsertKeyAtPlayhead(selectedTrack, value);
                if (key >= 0) keyed.Add((selectedTrack, key));
            }

            if (keyed.Count > 0)
            {
                ReplaceSelection(keyed);
                selectedKey = -1;
                foreach (var s in selKeys) { selectedTrack = s.track; selectedKey = s.key; break; }
                if (recordMode) CaptureRecordSnapshot();
                Repaint();
            }
        }

        bool TryReadTrackValue(int trackIndex, out float value)
        {
            value = 0f;
            if (trackIndex < 0 || trackIndex >= timeline.tracks.Count) return false;
            var tr = timeline.tracks[trackIndex];
            if (tr == null || !timeline.OwnsTarget(tr.target)) return false;
            if (tr.IsEvent)
            {
                if (tr.ResolveEvent() == null) return false;
                value = 1f;
                return true;
            }
            var field = tr.Resolve();
            if (field != null && tr.target != null)
            {
                value = field.Get(tr.target);
                return true;
            }
            return false;
        }

        void EnterRecordMode()
        {
            if (timeline == null || Application.isPlaying || simulate) return;
            ExitPreview();
            recordMode = true;
            CaptureRecordSnapshot();
            Repaint();
        }

        void ExitRecordMode()
        {
            recordMode = false;
            recordSnapshot.Clear();
            recordSnapshotDirty = true;
        }

        void CaptureRecordSnapshot()
        {
            recordSnapshot.Clear();
            if (timeline == null) return;

            var targets = CollectTargets();
            for (int i = 0; i < targets.Count; i++)
            {
                var comp = targets[i];
                var fields = BrazeAnimatableRegistry.VisibleFields(comp);
                for (int f = 0; f < fields.Count; f++)
                {
                    var field = fields[f];
                    var key = (comp, field.Id);
                    if (!recordSnapshot.ContainsKey(key)) recordSnapshot[key] = field.Get(comp);
                }
            }
            recordSnapshotDirty = false;
        }

        bool RecordChangedValues()
        {
            if (timeline == null || Application.isPlaying || simulate) return false;
            if (recordSnapshotDirty) CaptureRecordSnapshot();

            bool changed = false;
            var keyed = new HashSet<(int track, int key)>();
            var targets = CollectTargets();
            for (int i = 0; i < targets.Count; i++)
            {
                var comp = targets[i];
                var fields = BrazeAnimatableRegistry.VisibleFields(comp);
                for (int f = 0; f < fields.Count; f++)
                {
                    var field = fields[f];
                    var snapshotKey = (comp, field.Id);
                    float current = field.Get(comp);
                    if (!recordSnapshot.TryGetValue(snapshotKey, out float previous))
                    {
                        recordSnapshot[snapshotKey] = current;
                        continue;
                    }
                    if (Mathf.Abs(current - previous) <= (field.IsInt ? 0.01f : 1e-4f)) continue;

                    if (!changed)
                    {
                        RecordTimeline("Record Keyframe");
                        changed = true;
                    }

                    int track = FindTrackIndex(comp, field.Id, field.Member);
                    if (track < 0) track = CreateRecordedTrack(comp, field, previous);
                    int key = UpsertKeyAtPlayhead(track, current);
                    if (key >= 0) keyed.Add((track, key));
                    recordSnapshot[snapshotKey] = current;
                }
            }

            if (changed)
            {
                ReplaceSelection(keyed);
                selectedKey = -1;
                foreach (var s in selKeys) { selectedTrack = s.track; selectedKey = s.key; break; }
            }
            return changed;
        }

        int FindTrackIndex(Component target, string parameterId, string member = null)
        {
            for (int i = 0; i < timeline.tracks.Count; i++)
            {
                var tr = timeline.tracks[i];
                if (tr == null || tr.target != target) continue;
                if (!string.IsNullOrEmpty(parameterId) && tr.parameterId == parameterId) return i;
                if (!string.IsNullOrEmpty(member) && tr.member == member) return i;
            }
            return -1;
        }

        int FindEventTrackIndex(Component target, string eventId, string method = null)
        {
            for (int i = 0; i < timeline.tracks.Count; i++)
            {
                BrazeTrack track = timeline.tracks[i];
                if (track == null || track.target != target || !track.IsEvent) continue;
                if (!string.IsNullOrEmpty(eventId) && track.eventId == eventId) return i;
                if (!string.IsNullOrEmpty(method) && track.eventMethod == method) return i;
            }
            return -1;
        }

        int CreateRecordedTrack(Component comp, BrazeAnimatableField fld, float baselineValue)
        {
            ExpandCard(comp);
            int index = timeline.tracks.Count;
            timeline.tracks.Add(new BrazeTrack
            {
                target = comp,
                member = fld.Member,
                parameterId = fld.Id,
                curve = new AnimationCurve(new Keyframe(0f, baselineValue), new Keyframe(NewTrackSpan(), baselineValue)),
                enabled = true,
                color = TrackColor(index),
            });
            selectedTrack = index;
            return index;
        }

        void EnterPreview()
        {
            if (timeline == null || Application.isPlaying) return;
            ExitRecordMode();
            ExitSimulate();              // preview and simulate are mutually exclusive
            preview = true;
            CapturePreviewBaseline();
            previewDirty = true;
            SampleAt(timeline.time);
        }

        void ExitPreview()
        {
            preview = false;
            StopAnimModeIfIdle();
        }

        // Preview and Simulate share one direct typed baseline. Restore only after both modes are idle.
        void StopAnimModeIfIdle()
        {
            if (!preview && !simulate) RestorePreviewBaseline();
        }

        // Cached/baked targets do not read every authoring field live. Kick only components whose values were
        // actually sampled or restored; disabled, solo-muted and unresolved tracks must not trigger refresh work.
        void NotifyAnimatedTargets()
        {
            Exception failure = null;
            try
            {
                foreach (Component component in animatedTargetBuffer)
                {
                    if (component == null) continue;
                    try { (component as BrazeFX.IBrazeAnimatedTarget)?.OnBrazeAnimated(); }
                    catch (Exception ex) { failure ??= ex; }
                }
            }
            finally { animatedTargetBuffer.Clear(); }
            if (failure != null) throw failure;
        }

        void CapturePreviewBaseline()
        {
            if (previewBaselineHeld || timeline == null) return;
            previewBaseline.Clear();
            bool hasSolo = false;
            foreach (BrazeTrack candidate in timeline.tracks)
                if (IsResolvedSolo(candidate)) { hasSolo = true; break; }
            foreach (BrazeTrack track in timeline.tracks)
            {
                if (track == null || !track.enabled || (hasSolo && !track.solo) || track.curve == null) continue;
                BrazeAnimatableField field = track.Resolve();
                if (field == null || !timeline.OwnsTarget(track.target)) continue;
                var key = (track.target, track.BindingKey);
                if (!previewBaseline.ContainsKey(key)) previewBaseline[key] = field.Get(track.target);
            }
            previewBaselineHeld = true;
        }

        void RestorePreviewBaseline()
        {
            if (!previewBaselineHeld) return;
            Exception failure = null;
            animatedTargetBuffer.Clear();
            try
            {
                foreach (var pair in previewBaseline)
                {
                    Component component = pair.Key.target;
                    if (component == null) continue;
                    try
                    {
                        BrazeAnimatableField field = BrazeAnimatableRegistry.Find(component, pair.Key.binding);
                        if (field == null) continue;
                        float previous = field.Get(component);
                        field.Set(component, pair.Value);
                        if (component is BrazeFX.IBrazeAnimatedTarget &&
                            !Mathf.Approximately(previous, field.Get(component))) animatedTargetBuffer.Add(component);
                    }
                    catch (Exception ex) { failure ??= ex; }
                }
                try { NotifyAnimatedTargets(); } catch (Exception ex) { failure ??= ex; }
            }
            finally
            {
                previewBaseline.Clear();
                previewBaselineHeld = false;
                SceneView.RepaintAll();
            }
            if (failure != null)
                Debug.LogWarning($"[BrazeFX] Timeline could not restore one previewed value: {failure.Message}");
        }

        // ── edit-mode simulation (forward playback of the actual solver, no Play mode) ──
        void EnterSimulate()
        {
            if (timeline == null || Application.isPlaying) return;
            ExitRecordMode();
            ExitPreview();
            simulate = true;
            simPlaying = true;
            CapturePreviewBaseline();
            previewDirty = true;
            timeline.time = 0f;
            simContainers = new List<BrazeContainer>();
            foreach (BrazeContainer container in timeline.GetComponentsInChildren<BrazeContainer>(true))
                if (timeline.OwnsTarget(container)) simContainers.Add(container);
            ResetOwnedEmitterBursts();
            timeline.FireEventsAt(0f);
            SampleAt(0f);
            Repaint();
        }

        void ExitSimulate()
        {
            if (!simulate && simContainers == null) return;
            simulate = false;
            simPlaying = false;
            Exception failure = null;
            List<BrazeContainer> containers = simContainers;
            simContainers = null;
            if (containers != null)
            {
                foreach (var c in containers)
                {
                    if (c == null) continue;
                    try { c.EditorSimStop(); } catch (Exception ex) { failure ??= ex; }
                }
            }
            StopAnimModeIfIdle();
            SceneView.RepaintAll();
            Repaint();
            if (failure != null)
                Debug.LogWarning($"[BrazeFX] Timeline could not stop one simulated container cleanly: {failure.Message}");
        }

        void ResetSimState(float t, bool keepPlaying)
        {
            if (!simulate) return;
            if (simContainers != null)
                foreach (var c in simContainers) if (c != null) c.EditorSimStop();   // dispose → fresh on next step
            ResetOwnedEmitterBursts();
            timeline.time = Mathf.Max(0f, t);
            if (Mathf.Approximately(timeline.time, 0f)) timeline.FireEventsAt(0f);
            simPlaying = keepPlaying;
            SampleAt(timeline.time);
            SceneView.RepaintAll();
            Repaint();
        }

        void SampleAt(float t)
        {
            if ((!preview && !simulate) || timeline == null || Application.isPlaying) return;
            try
            {
                CapturePreviewBaseline();
                animatedTargetBuffer.Clear();
                bool hasSolo = false;
                foreach (BrazeTrack candidate in timeline.tracks)
                    if (IsResolvedSolo(candidate)) { hasSolo = true; break; }
                foreach (BrazeTrack track in timeline.tracks)
                {
                    if (track == null || !track.enabled || (hasSolo && !track.solo) ||
                        !timeline.OwnsTarget(track.target) || track.curve == null) continue;
                    BrazeAnimatableField field = track.Resolve();
                    if (field == null) continue;
                    var key = (track.target, track.BindingKey);
                    float previous = field.Get(track.target);
                    if (!previewBaseline.ContainsKey(key)) previewBaseline[key] = previous;
                    field.Set(track.target, field.Evaluate(track.curve, t));
                    if (track.target is BrazeFX.IBrazeAnimatedTarget &&
                        !Mathf.Approximately(previous, field.Get(track.target))) animatedTargetBuffer.Add(track.target);
                }
                NotifyAnimatedTargets();
                previewDirty = false;
                SceneView.RepaintAll();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BrazeFX] Timeline value sampling failed: {ex.Message}");
                StopTransientModes("recovering from a sampling failure");
            }
        }

        bool IsResolvedSolo(BrazeTrack track)
        {
            if (track == null || !track.enabled || !track.solo || !timeline.OwnsTarget(track.target) || track.curve == null) return false;
            return track.IsEvent ? track.ResolveEvent() != null : track.Resolve() != null;
        }

        void ResetOwnedEmitterBursts()
        {
            if (timeline == null) return;
            foreach (BrazePyroEmitter emitter in timeline.GetComponentsInChildren<BrazePyroEmitter>(true))
                if (timeline.OwnsTarget(emitter)) emitter.ResetBurst();
        }

        float TimeToX(float t) => plotX0 + (t - viewStart) * pps;
        float XToTime(float x) => viewStart + (x - plotX0) / pps;
        float NormToY(Rect g, float n) => g.yMax - (n - vStart) / vSpan * g.height;
        float YToNorm(Rect g, float y) => vStart + (g.yMax - y) / g.height * vSpan;
        float ValueToY(Rect g, float v, float min, float max)
            => NormToY(g, Mathf.Approximately(max, min) ? 0.5f : (v - min) / (max - min));
        float YToValue(Rect g, float y, float min, float max) => Mathf.Lerp(min, max, YToNorm(g, y));

        static float NiceStep(float span)
        {
            float raw = span / 8f;
            float[] steps = { 0.01f, 0.025f, 0.05f, 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f, 30f, 60f, 120f, 300f };
            foreach (var s in steps) if (raw <= s) return s;
            return 600f;
        }

        static string FmtTime(float t) => t >= 60f ? $"{(int)(t / 60)}:{t % 60:00.0}" : $"{t:0.##}s";

        static Color TrackColor(int i)
        {
            Color[] pal =
            {
                new Color(1.00f, 0.45f, 0.14f), new Color(0.36f, 0.72f, 1.00f), new Color(0.55f, 0.85f, 0.40f),
                new Color(0.95f, 0.78f, 0.25f), new Color(0.78f, 0.50f, 1.00f), new Color(1.00f, 0.40f, 0.55f),
                new Color(0.30f, 0.85f, 0.78f), new Color(0.90f, 0.55f, 0.30f),
            };
            return pal[i % pal.Length];
        }

        static void DrawDiamond(Vector2 c, float r, Color col)
        {
            if (diamondTex == null) diamondTex = MakeDiamond();
            var prev = GUI.color; GUI.color = col;
            GUI.DrawTexture(new Rect(c.x - r, c.y - r, r * 2, r * 2), diamondTex, ScaleMode.StretchToFill, true);
            GUI.color = prev;
        }

        static Texture2D MakeDiamond()
        {
            const int S = 24;
            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float u = (x + 0.5f) / S * 2f - 1f, v = (y + 0.5f) / S * 2f - 1f;
                    float a = Mathf.Clamp01((0.92f - (Mathf.Abs(u) + Mathf.Abs(v))) / 0.10f);
                    px[y * S + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            var t = new Texture2D(S, S, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
            t.SetPixels32(px); t.Apply();
            return t;
        }

        sealed class ParameterBrowserPopup : PopupWindowContent
        {
            const float TreeRowH = 21f, IndentW = 16f;
            readonly BrazeTimelineWindow owner;
            readonly Component component;
            readonly Dictionary<string, bool> branchOpen = new(StringComparer.OrdinalIgnoreCase);
            Vector2 scroll;
            string search = string.Empty;
            bool rootOpen = true;
            GUIStyle rootStyle, branchStyle, leafStyle, plusStyle, emptyStyle;

            sealed class PropertyNode
            {
                public string name;
                public readonly List<PropertyNode> children = new();
                public readonly List<BrazeAnimatableField> fields = new();
                public readonly List<BrazeAnimatableField> allFields = new();

                public PropertyNode Child(string childName)
                {
                    for (int i = 0; i < children.Count; i++)
                        if (string.Equals(children[i].name, childName, StringComparison.OrdinalIgnoreCase)) return children[i];
                    var child = new PropertyNode { name = childName };
                    children.Add(child);
                    return child;
                }
            }

            public ParameterBrowserPopup(BrazeTimelineWindow owner, Component component)
            {
                this.owner = owner;
                this.component = component;
            }

            public override Vector2 GetWindowSize() => new Vector2(450f, 500f);

            public override void OnGUI(Rect rect)
            {
                EnsurePopupStyles();
                if (component == null)
                {
                    GUILayout.Label("This component is no longer available.", EditorStyles.helpBox);
                    return;
                }

                GUILayout.Space(4f);
                GUI.SetNextControlName("BrazeTimelineSearch");
                search = EditorGUILayout.TextField(search, EditorStyles.toolbarSearchField);
                GUILayout.Space(3f);

                List<BrazeAnimatableField> fields = VisibleFields();
                List<BrazeTimelineEventDescriptor> events = VisibleEvents();
                List<PropertyNode> roots = BuildTree(fields);
                bool searching = !string.IsNullOrWhiteSpace(search);

                scroll = EditorGUILayout.BeginScrollView(scroll);
                bool showRoot = searching || rootOpen;
                DrawBranchRow(TreeTargetName(), 0, showRoot, () => rootOpen = !rootOpen,
                    EditorGUIUtility.ObjectContent(component, component.GetType()).image, fields,
                    "Add every visible parameter on this component");

                if (showRoot)
                {
                    for (int i = 0; i < roots.Count; i++)
                        DrawPropertyNode(roots[i], 1, roots[i].children.Count > 0 && i == 0, searching, roots[i].name);
                    if (events.Count > 0) DrawEvents(events, roots.Count == 0, searching);
                    if (roots.Count == 0 && events.Count == 0)
                        GUILayout.Label($"No properties match “{search}”.", emptyStyle, GUILayout.Height(42f));
                }
                EditorGUILayout.EndScrollView();

                if (Event.current.type == EventType.Repaint && string.IsNullOrEmpty(GUI.GetNameOfFocusedControl()))
                    EditorGUI.FocusTextInControl("BrazeTimelineSearch");
            }

            List<BrazeAnimatableField> VisibleFields()
            {
                var result = new List<BrazeAnimatableField>();
                var fields = BrazeAnimatableRegistry.VisibleFields(component);
                for (int i = 0; i < fields.Count; i++)
                {
                    BrazeAnimatableField field = fields[i];
                    if (Matches(field, search)) result.Add(field);
                }
                return result;
            }

            List<BrazeTimelineEventDescriptor> VisibleEvents()
            {
                var result = new List<BrazeTimelineEventDescriptor>();
                var events = BrazeTimelineEventRegistry.Events(component);
                for (int i = 0; i < events.Count; i++) if (Matches(events[i], search)) result.Add(events[i]);
                return result;
            }

            List<PropertyNode> BuildTree(List<BrazeAnimatableField> fields)
            {
                var result = new List<PropertyNode>();
                var roots = new Dictionary<string, PropertyNode>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < fields.Count; i++)
                {
                    BrazeAnimatableField field = fields[i];
                    string displayPath = BrazeAnimatableRegistry.DisplayPath(component, field);
                    string[] parts = displayPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    bool channel = parts.Length > 1 && IsChannel(parts[parts.Length - 1], field.Member);
                    int pathCount = parts.Length - (channel ? 1 : 0);
                    bool needsGeneral = pathCount <= 1 && !channel;
                    int totalDepth = pathCount + (needsGeneral ? 1 : 0);
                    PropertyNode node = null;
                    for (int depth = 0; depth < totalDepth; depth++)
                    {
                        string part = needsGeneral && depth == 0 ? "GENERAL" : parts[depth - (needsGeneral ? 1 : 0)];
                        if (depth == 0)
                        {
                            if (!roots.TryGetValue(part, out node))
                            {
                                node = new PropertyNode { name = part };
                                roots.Add(part, node);
                                result.Add(node);
                            }
                        }
                        else node = node.Child(part);
                        node.allFields.Add(field);
                    }
                    node?.fields.Add(field);
                }
                return result;
            }

            void DrawPropertyNode(PropertyNode node, int indent, bool defaultOpen, bool searching, string path)
            {
                bool channels = node.fields.Count > 1 ||
                                (node.fields.Count == 1 && IsChannelLabel(node.fields[0].ShortLabel, node.fields[0].Member));
                bool branch = node.children.Count > 0 || channels;
                if (!branch)
                {
                    if (node.fields.Count == 1) DrawFieldRow(node.name, indent, node.fields[0]);
                    return;
                }

                string key = "property:" + path;
                if (!branchOpen.ContainsKey(key)) branchOpen[key] = defaultOpen;
                bool open = searching || branchOpen[key];
                DrawBranchRow(node.name, indent, open, () => branchOpen[key] = !branchOpen[key], null,
                    node.allFields, "Add every property in " + node.name);
                if (!open) return;

                if (channels)
                    for (int i = 0; i < node.fields.Count; i++)
                        DrawFieldRow(ChannelName(node.fields[i]), indent + 1, node.fields[i]);
                else if (node.fields.Count == 1)
                    DrawFieldRow("Value", indent + 1, node.fields[0]);

                for (int i = 0; i < node.children.Count; i++)
                    DrawPropertyNode(node.children[i], indent + 1, false, searching, path + "/" + node.children[i].name);
            }

            void DrawEvents(List<BrazeTimelineEventDescriptor> events, bool first, bool searching)
            {
                const string key = "section:EVENTS";
                if (!branchOpen.ContainsKey(key)) branchOpen[key] = first;
                bool open = searching || branchOpen[key];
                DrawBranchRow("EVENTS", 1, open, () => branchOpen[key] = !branchOpen[key], null, null, null);
                if (!open) return;
                for (int i = 0; i < events.Count; i++) DrawEventRow(events[i], 2);
            }

            void DrawBranchRow(string label, int indent, bool open, Action toggle, Texture icon,
                List<BrazeAnimatableField> fields, string addTooltip)
            {
                Rect row = TreeRow();
                DrawRowHover(row);
                float x = row.x + 4f + indent * IndentW;
                Rect addRect = new Rect(row.xMax - 27f, row.y, 25f, row.height);
                Rect toggleRect = new Rect(x, row.y, Mathf.Max(1f, addRect.x - x), row.height);
                if (GUI.Button(toggleRect, GUIContent.none, GUIStyle.none) && string.IsNullOrWhiteSpace(search)) toggle();

                DrawDisclosureTriangle(new Rect(x, row.y, 18f, row.height), open, TextStrong);
                x += 19f;
                if (icon != null)
                {
                    GUI.DrawTexture(new Rect(x, row.y + 2f, 17f, 17f), icon, ScaleMode.ScaleToFit, true);
                    x += 20f;
                }
                GUI.Label(new Rect(x, row.y, Mathf.Max(1f, addRect.x - x - 3f), row.height), label,
                    indent == 0 ? rootStyle : branchStyle);
                if (fields != null && fields.Count > 0) DrawAddFields(addRect, fields, addTooltip);
            }

            void DrawFieldRow(string label, int indent, BrazeAnimatableField field)
            {
                Rect row = TreeRow();
                DrawRowHover(row);
                float x = row.x + 4f + indent * IndentW;
                EditorGUI.DrawRect(new Rect(x + 5f, row.center.y - 1.5f, 3f, 3f), Accent);
                x += 14f;
                Rect addRect = new Rect(row.xMax - 27f, row.y, 25f, row.height);
                GUI.Label(new Rect(x, row.y, Mathf.Max(1f, addRect.x - x - 3f), row.height),
                    new GUIContent(label, BrazeAnimatableRegistry.DisplayPath(component, field) + "\n" + field.Member), leafStyle);
                bool exists = owner.FindTrackIndex(component, field.Id, field.Member) >= 0;
                using (new EditorGUI.DisabledScope(exists))
                    if (GUI.Button(addRect, new GUIContent(exists ? "✓" : "+",
                            "Add " + BrazeAnimatableRegistry.DisplayPath(component, field)), plusStyle))
                        owner.AddTrack(component, field);
            }

            void DrawEventRow(BrazeTimelineEventDescriptor descriptor, int indent)
            {
                Rect row = TreeRow();
                DrawRowHover(row);
                float x = row.x + 4f + indent * IndentW;
                Rect addRect = new Rect(row.xMax - 27f, row.y, 25f, row.height);
                GUI.Label(new Rect(x, row.y, 15f, row.height), "⚡", leafStyle);
                x += 16f;
                GUI.Label(new Rect(x, row.y, Mathf.Max(1f, addRect.x - x - 3f), row.height), descriptor.Label, leafStyle);
                bool exists = owner.FindEventTrackIndex(component, descriptor.Id, descriptor.Method) >= 0;
                using (new EditorGUI.DisabledScope(exists))
                    if (GUI.Button(addRect, new GUIContent(exists ? "✓" : "+", "Add event " + descriptor.Label), plusStyle))
                        owner.AddEventTrack(component, descriptor);
            }

            void DrawAddFields(Rect rect, List<BrazeAnimatableField> fields, string tooltip)
            {
                bool all = AllAdded(component, fields);
                using (new EditorGUI.DisabledScope(all))
                    if (GUI.Button(rect, new GUIContent(all ? "✓" : "+", tooltip), plusStyle))
                        for (int i = 0; i < fields.Count; i++) owner.AddTrack(component, fields[i]);
            }

            static Rect TreeRow() => GUILayoutUtility.GetRect(0f, TreeRowH, GUILayout.ExpandWidth(true));

            static void DrawRowHover(Rect row)
            {
                if (row.Contains(Event.current.mousePosition))
                    EditorGUI.DrawRect(row, new Color(1f, 1f, 1f, 0.055f));
            }

            bool AllAdded(Component target, List<BrazeAnimatableField> fields)
            {
                for (int i = 0; i < fields.Count; i++)
                    if (owner.FindTrackIndex(target, fields[i].Id, fields[i].Member) < 0) return false;
                return true;
            }

            string TreeTargetName()
            {
                string path = HierarchyPath(component.transform);
                string type = ComponentTypeName(component);
                return SameWords(component.gameObject.name, type) ? path : path + "  ·  " + type;
            }

            void EnsurePopupStyles()
            {
                if (rootStyle == null)
                {
                    rootStyle = new GUIStyle(EditorStyles.label) { fontStyle = FontStyle.Bold, clipping = TextClipping.Clip };
                    branchStyle = new GUIStyle(EditorStyles.label) { clipping = TextClipping.Clip };
                    leafStyle = new GUIStyle(EditorStyles.label) { clipping = TextClipping.Clip };
                    plusStyle = new GUIStyle(EditorStyles.label) { fontSize = 17, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                    emptyStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter };
                }
                ForceTextColor(rootStyle, TextStrong);
                ForceTextColor(branchStyle, TextStrong);
                ForceTextColor(leafStyle, TextStrong);
                ForceTextColor(plusStyle, TextStrong);
                ForceTextColor(emptyStyle, TextDim);
                plusStyle.hover.textColor = Accent;
            }

            static string ComponentTypeName(Component target)
            {
                if (target is Transform) return "Transform";
                string name = target.GetType().Name;
                if (name.StartsWith("Braze", StringComparison.Ordinal)) name = name.Substring(5);
                return ObjectNames.NicifyVariableName(name);
            }

            static string HierarchyPath(Transform target)
            {
                var names = new List<string>();
                for (Transform current = target; current != null; current = current.parent) names.Add(current.name);
                names.Reverse();
                return string.Join("  /  ", names);
            }

            static bool SameWords(string a, string b) => Normalize(a) == Normalize(b);

            static string Normalize(string value)
            {
                var result = new System.Text.StringBuilder(value?.Length ?? 0);
                if (value != null)
                    for (int i = 0; i < value.Length; i++)
                        if (char.IsLetterOrDigit(value[i])) result.Append(char.ToLowerInvariant(value[i]));
                return result.ToString();
            }

            static bool IsChannel(string label, string member) =>
                IsAxisToken(label) && member.EndsWith("." + label.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);

            static bool IsChannelLabel(string label, string member) => IsChannel(label, member);

            static bool IsAxisToken(string value) => value.Length == 1 && "XYZWRGBA".IndexOf(char.ToUpperInvariant(value[0])) >= 0;

            string ChannelName(BrazeAnimatableField field)
            {
                string path = BrazeAnimatableRegistry.DisplayPath(component, field);
                int slash = path.LastIndexOf('/');
                string label = slash >= 0 ? path.Substring(slash + 1) : field.ShortLabel;
                return IsAxisToken(label) ? label.ToUpperInvariant() : label;
            }

            bool Matches(BrazeAnimatableField field, string query)
            {
                if (string.IsNullOrWhiteSpace(query)) return true;
                return BrazeAnimatableRegistry.DisplayPath(component, field).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       field.Member.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            static bool Matches(BrazeTimelineEventDescriptor descriptor, string query)
            {
                if (string.IsNullOrWhiteSpace(query)) return true;
                return descriptor.Label.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                       descriptor.Method.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        // Force a style's text colour in EVERY interaction state. The custom-drawn window paints its own dark
        // panels, so it can't inherit EditorStyles' THEME-dependent text colours (they go dark on our dark bg under
        // the Light skin / certain Unity versions — the cause of the unreadable buttons). Setting the colours here
        // only mutates THIS window's private GUIStyle copies, never the shared EditorStyles, so no other inspector
        // is affected. Both the off (normal/hover/active/focused) and on (toggle-pressed) states are covered.
        static void ForceTextColor(GUIStyle s, Color c)
        {
            s.normal.textColor = s.hover.textColor = s.active.textColor = s.focused.textColor = c;
            s.onNormal.textColor = s.onHover.textColor = s.onActive.textColor = s.onFocused.textColor = c;
        }

        void EnsureStyles()
        {
            // Allocate the GUIStyles ONCE (they survive as instance fields until a domain reload nulls them).
            if (wordmark == null)
            {
                wordmark    = BrazeEditorGUI.StyleFromSkin(() => EditorStyles.boldLabel);
                wordmark.fontSize = 14;
                wordmark.alignment = TextAnchor.MiddleLeft;
                dim         = BrazeEditorGUI.StyleFromSkin(() => EditorStyles.label);
                dim.fontSize = 10;
                dim.alignment = TextAnchor.MiddleLeft;
                strongMini  = BrazeEditorGUI.StyleFromSkin(() => EditorStyles.boldLabel);
                strongMini.fontSize = 11;
                strongMini.alignment = TextAnchor.MiddleLeft;
                rowName     = BrazeEditorGUI.StyleFromSkin(() => EditorStyles.label);
                rowName.fontSize = 11;
                rowName.alignment = TextAnchor.MiddleLeft;
                miniBtn     = BrazeEditorGUI.StyleFromSkin(() => EditorStyles.miniButton);
                miniBtn.fontSize = 11;
                miniBtn.alignment = TextAnchor.MiddleCenter;
                tabStyle    = BrazeEditorGUI.StyleFromSkin(() => EditorStyles.miniButton);
                tabStyle.fontSize = 11;
                tabOn       = new GUIStyle(tabStyle)               { fontStyle = FontStyle.Bold };
                addBtn      = BrazeEditorGUI.StyleFromSkin(() => EditorStyles.miniButton);
                addBtn.fontSize = 10;
                addBtn.fontStyle = FontStyle.Bold;
            }

            // Re-assert the text colours EVERY OnGUI, not just on first build. A cached GUIStyle silently loses its
            // forced textColor across a domain reload / editor-skin change / Play-mode toggle (worst with "Enter Play
            // Mode → Reload Domain" disabled) — and a build-once guard would then skip the re-colour, leaving the whole
            // toolbar dim/unreadable. That is the recurring timeline-colour regression. Re-forcing is pure colour
            // assignment (no allocation), so running it per frame is free.
            ForceTextColor(wordmark,   Accent);
            ForceTextColor(dim,        TextDim);
            ForceTextColor(strongMini, TextStrong);
            ForceTextColor(rowName,    TextStrong);   // per-row enabled/disabled tint still overrides at draw time
            ForceTextColor(miniBtn,    TextStrong);
            ForceTextColor(tabStyle,   TextDim);
            ForceTextColor(tabOn,      Accent);
            ForceTextColor(addBtn,     TextStrong);
        }
    }
}
