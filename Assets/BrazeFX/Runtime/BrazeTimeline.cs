using System;
using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Lightweight typed animation for BrazeFX authoring parameters. A Timeline owns an explicit set of component
    /// cards; tracks bind stable parameter IDs on those cards to scalar curves. Vector and colour parameters are
    /// represented by related scalar channels, while booleans/enums/integers use stepped evaluation.
    /// </summary>
    [AddComponentMenu("BrazeFX/Cinematics/Braze Timeline", 51)]
    public sealed class BrazeTimeline : MonoBehaviour
    {
        public enum LoopMode { Once, Loop, PingPong }
        public const float MinDuration = 0.1f;
        public const float DefaultDuration = 5f;

        [Tooltip("Explicit authoring/work-area end in seconds. Loop and Ping Pong playback use the final authored keyframe when one exists.")]
        [Min(MinDuration)] public float length = DefaultDuration;
        public float duration => float.IsFinite(length) ? Mathf.Max(MinDuration, length) : DefaultDuration;

        /// <summary>The furthest authored key, useful for diagnostics and fitting without changing playback length.</summary>
        public float contentEnd
        {
            get
            {
                float end = 0f;
                if (tracks == null) return end;
                for (int i = 0; i < tracks.Count; i++)
                {
                    BrazeTrack track = tracks[i];
                    AnimationCurve curve = IsResolvedTrack(track) ? track.curve : null;
                    if (curve == null || curve.length == 0) continue;
                    float keyTime = curve[curve.length - 1].time;
                    if (float.IsFinite(keyTime)) end = Mathf.Max(end, keyTime);
                }
                return end;
            }
        }

        /// <summary>
        /// Loop/Ping-Pong boundary. Authored animation ends on its furthest key; an empty or zero-time-only Timeline
        /// falls back to the explicit work area so it still has a usable playback span.
        /// </summary>
        public float loopEnd
        {
            get
            {
                float authoredEnd = contentEnd;
                return authoredEnd > 1e-5f ? Mathf.Max(MinDuration, authoredEnd) : duration;
            }
        }

        [Tooltip("What happens when the playhead reaches the end.")]
        public LoopMode loopMode = LoopMode.Loop;
        [Tooltip("Playback rate. 1 = real time.")]
        [Range(0.01f, 8f)] public float speed = 1f;
        [Tooltip("Start playing automatically when this object becomes active.")]
        public bool playOnEnable = true;
        [Tooltip("Use unscaled time so Time.timeScale does not affect the timeline.")]
        public bool ignoreTimeScale;
        [Tooltip("When Loop wraps, clear active child Braze Containers so simulation does not accumulate across loops.")]
        public bool resetSimOnLoop = true;
        [Tooltip("Authoring frame rate used when snapping Timeline keys.")]
        [Range(1, 240)] public int frameRate = 30;
        public bool snapToFrames = true;

        [Tooltip("Components registered as cards in the Timeline editor.")]
        public List<BrazeTimelineTarget> targets = new();
        public List<BrazeTrack> tracks = new();
        [SerializeField, HideInInspector] int serializationVersion;
        const int CurrentSerializationVersion = 2;

        [NonSerialized] public float time;
        [NonSerialized] public bool playing;
        int pingDir = 1;

        readonly Dictionary<(Component, string), float> baseline = new();
        bool baselineHeld;
        readonly HashSet<Component> notifyBuf = new();
        readonly List<(float order, BrazeTrack track)> eventBuf = new();
#if UNITY_EDITOR
        [NonSerialized] Transform editorLockedTransform;
        const HideFlags EditorTransformFlags = HideFlags.NotEditable | HideFlags.HideInInspector;
#endif

        void OnEnable()
        {
            LockEditorTransform();
            MigrateSerializedData();
            AdoptTrackTargets();
            time = 0f;
            pingDir = 1;
            playing = playOnEnable;
            if (playing) { CaptureBaseline(); Evaluate(0f); FireEventsAt(0f); }
        }

        void OnDisable()
        {
            playing = false;
            RestoreBaseline();
        }

        void OnValidate()
        {
            LockEditorTransform();
            MigrateSerializedData();
            // Mathf.Max/Clamp pass NaN through — sanitize first so a bad serialized value can't poison playback
            // and every editor zoom/fit computation derived from duration.
            if (!float.IsFinite(length)) length = DefaultDuration;
            if (!float.IsFinite(speed)) speed = 1f;
            length = Mathf.Max(MinDuration, length);
            speed = Mathf.Clamp(speed, 0.01f, 8f);
            frameRate = Mathf.Clamp(frameRate, 1, 240);
            AdoptTrackTargets();
        }

        void OnDestroy()
        {
#if UNITY_EDITOR
            // A Transform cannot be removed from a GameObject. Release the editor-only lock when the Timeline
            // component itself is removed so the former authoring root returns to being an ordinary GameObject.
            if (!Application.isPlaying && editorLockedTransform != null)
            {
                Transform root = editorLockedTransform;
                root.hideFlags &= ~EditorTransformFlags;
                // Unity can restore serialized hide flags after DestroyImmediate finishes. Repeat once on the next
                // editor update, but only if another Timeline was not added to the same root in the meantime.
                UnityEditor.EditorApplication.delayCall += () =>
                {
                    if (root != null && root.GetComponent<BrazeTimeline>() == null)
                        root.hideFlags &= ~EditorTransformFlags;
                };
            }
#endif
        }

        void LockEditorTransform()
        {
#if UNITY_EDITOR
            if (transform != null)
            {
                editorLockedTransform = transform;
                if (editorLockedTransform.localPosition != Vector3.zero) editorLockedTransform.localPosition = Vector3.zero;
                if (editorLockedTransform.localRotation != Quaternion.identity) editorLockedTransform.localRotation = Quaternion.identity;
                if (editorLockedTransform.localScale != Vector3.one) editorLockedTransform.localScale = Vector3.one;
                if ((editorLockedTransform.hideFlags & EditorTransformFlags) != EditorTransformFlags)
                    editorLockedTransform.hideFlags |= EditorTransformFlags;
            }
#endif
        }

        void MigrateSerializedData()
        {
            if (serializationVersion < 1)
            {
                // V0 derived its end from the last key. Preserve that playback boundary once, then decouple it.
                length = Mathf.Max(DefaultDuration, length, contentEnd);
            }
            // V2 gives every component card and its tracks a stable serialized relationship. AdoptTrackTargets
            // performs the reference-aware migration because it also has to cover legacy missing references.
            serializationVersion = CurrentSerializationVersion;
        }

        void Update()
        {
            if (!playing) return;
            float dt = (ignoreTimeScale ? Time.unscaledDeltaTime : Time.deltaTime) * speed;
            float before = time;
            int beforeDirection = pingDir;
            Advance(dt);
            if (resetSimOnLoop && DidLoopToStart(before, beforeDirection, dt)) RestartOwnedContainers();
            Evaluate(time);
        }

        /// <summary>Advance by a non-negative duration, correctly handling any number of crossed boundaries.</summary>
        public void Advance(float deltaTime)
        {
            float dt = float.IsFinite(deltaTime) ? Mathf.Max(0f, deltaTime) : 0f;
            float d = loopMode == LoopMode.Once ? duration : loopEnd;
            if (!float.IsFinite(time)) time = 0f;
            if (dt > 0f) QueueAndFireEvents(time, pingDir, dt, d);
            switch (loopMode)
            {
                case LoopMode.Once:
                    time += dt;
                    if (time >= d) { time = d; playing = false; }
                    break;

                case LoopMode.Loop:
                    time = Mathf.Repeat(time + dt, d);
                    break;

                case LoopMode.PingPong:
                    float cycle = d * 2f;
                    float phase = pingDir >= 0 ? time : cycle - time;
                    phase = Mathf.Repeat(phase + dt, cycle);
                    if (phase < d)
                    {
                        time = phase;
                        pingDir = 1;
                    }
                    else
                    {
                        time = cycle - phase;
                        pingDir = -1;
                    }
                    if (Mathf.Approximately(time, 0f)) pingDir = 1;
                    else if (Mathf.Approximately(time, d)) pingDir = -1;
                    break;
            }
            time = Mathf.Clamp(time, 0f, d);
        }

        /// <summary>Apply all enabled, resolved parameter tracks and batch component refresh hooks.</summary>
        public void Evaluate(float sampleTime)
        {
            if (!float.IsFinite(sampleTime)) sampleTime = 0f;
            notifyBuf.Clear();
            bool hasSolo = false;
            for (int i = 0; i < tracks.Count; i++)
                if (IsResolvedSolo(tracks[i])) { hasSolo = true; break; }
            for (int i = 0; i < tracks.Count; i++)
            {
                BrazeTrack track = tracks[i];
                if (track == null || !track.enabled || (hasSolo && !track.solo) || track.curve == null) continue;
                BrazeAnimatableField field = track.Resolve();
                if (field == null || !OwnsTarget(track.target)) continue;

                float previous = field.Get(track.target);
                var binding = (track.target, track.BindingKey);
                if (baselineHeld && !baseline.ContainsKey(binding)) baseline[binding] = previous;
                float value = field.Evaluate(track.curve, sampleTime);
                field.Set(track.target, value);
                if (track.target is IBrazeAnimatedTarget &&
                    !Mathf.Approximately(previous, field.Get(track.target))) notifyBuf.Add(track.target);
            }
            NotifyTargets(notifyBuf);
        }

        /// <summary>Invoke event-track keys exactly at a time, used when playback/simulation begins at zero.</summary>
        public void FireEventsAt(float sampleTime)
        {
            if (!float.IsFinite(sampleTime)) return;
            const float epsilon = 1e-4f;
            bool hasSolo = false;
            for (int i = 0; i < tracks.Count; i++)
                if (IsResolvedSolo(tracks[i])) { hasSolo = true; break; }
            for (int i = 0; i < tracks.Count; i++)
            {
                BrazeTrack track = tracks[i];
                if (track == null || !track.enabled || (hasSolo && !track.solo) || !track.IsEvent ||
                    !IsResolvedTrack(track)) continue;
                for (int k = 0; k < track.curve.length; k++)
                    if (Mathf.Abs(track.curve[k].time - sampleTime) <= epsilon) track.InvokeEvent();
            }
        }

        public void Play()
        {
            CaptureBaseline();
            playing = true;
        }

        public void Pause() => playing = false;

        /// <summary>Stop playback and restore the values authored before playback began.</summary>
        public void Stop()
        {
            playing = false;
            time = 0f;
            pingDir = 1;
            RestoreBaseline();
        }

        public void Scrub(float sampleTime)
        {
            CaptureBaseline();
            time = Mathf.Clamp(float.IsFinite(sampleTime) ? sampleTime : 0f, 0f, duration);
            Evaluate(time);
        }

        public void CaptureBaseline()
        {
            if (!baselineHeld) baseline.Clear();
            bool hasSolo = false;
            for (int i = 0; i < tracks.Count; i++)
                if (IsResolvedSolo(tracks[i])) { hasSolo = true; break; }
            for (int i = 0; i < tracks.Count; i++)
            {
                BrazeTrack track = tracks[i];
                if (track == null || !track.enabled || (hasSolo && !track.solo) || track.curve == null) continue;
                BrazeAnimatableField field = track.Resolve();
                if (field == null || !OwnsTarget(track.target)) continue;
                var key = (track.target, track.BindingKey);
                if (!baseline.ContainsKey(key)) baseline[key] = field.Get(track.target);
            }
            baselineHeld = true;
        }

        public void RestoreBaseline()
        {
            if (!baselineHeld) return;
            notifyBuf.Clear();
            foreach (var pair in baseline)
            {
                Component component = pair.Key.Item1;
                if (component == null) continue;
                BrazeAnimatableField field = BrazeAnimatableRegistry.Find(component, pair.Key.Item2);
                if (field == null) continue;
                float previous = field.Get(component);
                field.Set(component, pair.Value);
                if (component is IBrazeAnimatedTarget &&
                    !Mathf.Approximately(previous, field.Get(component))) notifyBuf.Add(component);
            }
            NotifyTargets(notifyBuf);
            baseline.Clear();
            baselineHeld = false;
        }

        public bool ContainsTarget(Component component)
        {
            if (component == null || targets == null) return false;
            for (int i = 0; i < targets.Count; i++)
                if (targets[i]?.target != null && targets[i].target == component) return true;
            return false;
        }

        /// <summary>
        /// A Timeline exclusively authors components on descendant GameObjects. Keeping all animated objects under
        /// the Timeline creates an unambiguous, scene-serializable ownership boundary and avoids global searches.
        /// The Timeline GameObject itself is deliberately not a target.
        /// </summary>
        public bool OwnsTarget(Component component)
        {
            if (component == null || component is BrazeTimeline || component.transform == transform) return false;
            return component.GetComponentInParent<BrazeTimeline>(true) == this;
        }

        public bool AddTarget(Component component)
        {
            if (!OwnsTarget(component) || ContainsTarget(component)) return false;
            targets ??= new List<BrazeTimelineTarget>();
            var card = new BrazeTimelineTarget { target = component, expanded = true };
            card.EnsureId();
            targets.Add(card);
            return true;
        }

        /// <summary>
        /// Reconcile component cards and tracks while preserving missing bindings. Unity's destroyed-object
        /// references all compare equal to null, so reference equality alone cannot associate orphan tracks with
        /// their original cards. Stable IDs keep those relationships distinct after delete/undo/reload cycles.
        /// Returns true when serialized data was repaired or migrated.
        /// </summary>
        public bool AdoptTrackTargets()
        {
            bool changed = false;
            if (targets == null) { targets = new List<BrazeTimelineTarget>(); changed = true; }
            if (tracks == null) { tracks = new List<BrazeTrack>(); changed = true; }

            // Null managed entries have no recoverable data. Live duplicate cards can occur after damaged Undo
            // state; keep the first so one component is never rendered more than once.
            var liveCards = new Dictionary<int, BrazeTimelineTarget>();
            for (int i = 0; i < targets.Count;)
            {
                BrazeTimelineTarget card = targets[i];
                if (card == null)
                {
                    targets.RemoveAt(i);
                    changed = true;
                    continue;
                }
                if (card.target != null)
                {
                    int instanceId = card.target.BrazeInstanceId();
                    if (liveCards.ContainsKey(instanceId))
                    {
                        targets.RemoveAt(i);
                        changed = true;
                        continue;
                    }
                    liveCards.Add(instanceId, card);
                }
                i++;
            }

            for (int i = 0; i < tracks.Count;)
            {
                if (tracks[i] == null)
                {
                    tracks.RemoveAt(i);
                    changed = true;
                }
                else i++;
            }

            // Ensure card IDs are non-empty and unique within this Timeline. Multiple already-missing legacy cards
            // cannot be distinguished; the first becomes their shared migration bucket without multiplying rows.
            var cardsById = new Dictionary<string, BrazeTimelineTarget>(StringComparer.Ordinal);
            string legacyMissingId = null;
            for (int i = 0; i < targets.Count; i++)
            {
                BrazeTimelineTarget card = targets[i];
                string before = card.Id;
                string id = card.EnsureId();
                if (before != id) changed = true;
                if (cardsById.ContainsKey(id))
                {
                    card.RegenerateId();
                    id = card.Id;
                    changed = true;
                }
                cardsById.Add(id, card);
                if (card.target == null && legacyMissingId == null) legacyMissingId = id;
            }

            // Rebuild the live lookup after duplicate removal/ID creation.
            liveCards.Clear();
            for (int i = 0; i < targets.Count; i++)
                if (targets[i].target != null) liveCards[targets[i].target.BrazeInstanceId()] = targets[i];

            for (int i = 0; i < tracks.Count; i++)
            {
                BrazeTrack track = tracks[i];
                if (track.target != null)
                {
                    int instanceId = track.target.BrazeInstanceId();
                    if (!liveCards.TryGetValue(instanceId, out BrazeTimelineTarget card))
                    {
                        // Existing external bindings are retained as repairable cards, but AddTarget deliberately
                        // rejects creating new ones. Playback and authoring ignore them until they are reparented.
                        card = new BrazeTimelineTarget { target = track.target, expanded = true };
                        card.EnsureId();
                        targets.Add(card);
                        liveCards.Add(instanceId, card);
                        cardsById.Add(card.Id, card);
                        changed = true;
                    }
                    if (track.TargetId != card.Id)
                    {
                        track.SetTargetId(card.Id);
                        changed = true;
                    }
                    continue;
                }

                string id = track.TargetId;
                if (string.IsNullOrEmpty(id))
                {
                    if (legacyMissingId == null)
                    {
                        var missing = new BrazeTimelineTarget();
                        legacyMissingId = missing.EnsureId();
                        targets.Add(missing);
                        cardsById.Add(legacyMissingId, missing);
                        changed = true;
                    }
                    track.SetTargetId(legacyMissingId);
                    id = legacyMissingId;
                    changed = true;
                }

                if (cardsById.TryGetValue(id, out BrazeTimelineTarget owner) && owner.target != null)
                {
                    // Undo can restore the card before Unity restores a damaged track PPtr. The stable relationship
                    // is authoritative, so reconnect the track to that exact live component.
                    track.target = owner.target;
                    changed = true;
                    continue;
                }

                // A track can outlive a card after manual serialized-data edits. Recreate a removable placeholder
                // instead of hiding the keyframes or allowing drawing code to dereference a missing card.
                if (!cardsById.ContainsKey(id))
                {
                    var missing = new BrazeTimelineTarget();
                    missing.SetId(id);
                    targets.Add(missing);
                    cardsById.Add(id, missing);
                    changed = true;
                }
            }

            // Missing cards with no tracks carry no authoring data. Removing them coalesces legacy GameObject drops
            // (Transform + component cards) after the object was deleted, while preserving every orphaned curve.
            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < tracks.Count; i++)
                if (!string.IsNullOrEmpty(tracks[i].TargetId)) usedIds.Add(tracks[i].TargetId);
            for (int i = targets.Count - 1; i >= 0; i--)
            {
                BrazeTimelineTarget card = targets[i];
                if (card.target == null && !usedIds.Contains(card.Id))
                {
                    targets.RemoveAt(i);
                    changed = true;
                }
            }
            return changed;
        }

        bool DidLoopToStart(float before, int directionBefore, float dt)
        {
            if (loopMode != LoopMode.Loop || dt <= 0f || directionBefore < 0) return false;
            return before + dt >= loopEnd;
        }

        static void NotifyTargets(HashSet<Component> components)
        {
            foreach (Component component in components)
                if (component != null) (component as IBrazeAnimatedTarget)?.OnBrazeAnimated();
        }

        void QueueAndFireEvents(float startTime, int direction, float dt, float d)
        {
            eventBuf.Clear();
            bool hasSolo = false;
            for (int i = 0; i < tracks.Count; i++)
                if (IsResolvedSolo(tracks[i])) { hasSolo = true; break; }

            for (int i = 0; i < tracks.Count; i++)
            {
                BrazeTrack track = tracks[i];
                if (track == null || !track.enabled || (hasSolo && !track.solo) || !track.IsEvent ||
                    !IsResolvedTrack(track)) continue;

                for (int k = 0; k < track.curve.length; k++)
                {
                    float authoredTime = track.curve[k].time;
                    if (!float.IsFinite(authoredTime)) continue;
                    float keyTime = Mathf.Clamp(authoredTime, 0f, d);
                    if (loopMode == LoopMode.Once)
                    {
                        float end = Mathf.Min(d, startTime + dt);
                        if (keyTime > startTime + 1e-5f && keyTime <= end + 1e-5f)
                            eventBuf.Add((keyTime - startTime, track));
                    }
                    else if (loopMode == LoopMode.Loop)
                    {
                        AddPeriodicOccurrence(track, keyTime, d, startTime, startTime + dt);
                    }
                    else
                    {
                        float cycle = d * 2f;
                        float phaseStart = direction >= 0 ? startTime : cycle - startTime;
                        AddPeriodicOccurrence(track, keyTime, cycle, phaseStart, phaseStart + dt);
                        if (keyTime > 1e-5f && keyTime < d - 1e-5f)
                            AddPeriodicOccurrence(track, cycle - keyTime, cycle, phaseStart, phaseStart + dt);
                    }
                }
            }

            eventBuf.Sort((a, b) => a.order.CompareTo(b.order));
            for (int i = 0; i < eventBuf.Count; i++) eventBuf[i].track.InvokeEvent();
        }

        bool IsResolvedSolo(BrazeTrack track)
        {
            return track != null && track.enabled && track.solo && IsResolvedTrack(track);
        }

        bool IsResolvedTrack(BrazeTrack track)
        {
            if (track == null || track.curve == null || !OwnsTarget(track.target)) return false;
            return track.IsEvent ? track.ResolveEvent() != null : track.Resolve() != null;
        }

        void AddPeriodicOccurrence(BrazeTrack track, float phase, float period, float start, float end)
        {
            if (period <= 0f) return;
            int first = Mathf.FloorToInt((start - phase) / period) + 1;
            float occurrence = phase + first * period;
            for (int guard = 0; guard < 4096 && occurrence <= end + 1e-5f; guard++, occurrence += period)
                eventBuf.Add((occurrence - start, track));
        }

        void RestartOwnedContainers()
        {
            BrazeContainer[] containers = GetComponentsInChildren<BrazeContainer>(true);
            for (int i = 0; i < containers.Length; i++)
                if (OwnsTarget(containers[i]) && containers[i].isActiveAndEnabled)
                    containers[i].RestartSimulation();
        }
    }

    static class BrazeTimelineNaming
    {
        public static string ComponentPath(Component target)
        {
            if (target == null) return "Missing Component";
            var names = new List<string>();
            for (Transform current = target.transform; current != null; current = current.parent) names.Add(current.name);
            names.Reverse();
            string path = string.Join("  /  ", names);
            string type = target is Transform ? "Transform" : Prettify(StripBraze(target.GetType().Name));
            return Normalize(target.gameObject.name) == Normalize(type) ? path : path + "  ·  " + type;
        }

        static string StripBraze(string value) => value.StartsWith("Braze", StringComparison.Ordinal) ? value.Substring(5) : value;

        static string Normalize(string value)
        {
            var result = new System.Text.StringBuilder(value?.Length ?? 0);
            if (value != null)
                for (int i = 0; i < value.Length; i++)
                    if (char.IsLetterOrDigit(value[i])) result.Append(char.ToLowerInvariant(value[i]));
            return result.ToString();
        }

        static string Prettify(string value)
        {
            var result = new System.Text.StringBuilder(value.Length + 4);
            for (int i = 0; i < value.Length; i++)
            {
                bool acronymBoundary = i > 0 && i + 1 < value.Length && char.IsUpper(value[i]) &&
                                       char.IsUpper(value[i - 1]) && char.IsLower(value[i + 1]);
                if (i > 0 && char.IsUpper(value[i]) && (!char.IsUpper(value[i - 1]) || acronymBoundary)) result.Append(' ');
                result.Append(value[i]);
            }
            return result.ToString();
        }
    }

    [Serializable]
    public sealed class BrazeTimelineTarget
    {
        public Component target;
        public bool expanded = true;
        [SerializeField, HideInInspector] string id;

        public string Id => id;

        internal string EnsureId()
        {
            if (string.IsNullOrEmpty(id)) id = Guid.NewGuid().ToString("N");
            return id;
        }

        internal void RegenerateId() => id = Guid.NewGuid().ToString("N");

        internal void SetId(string value) => id = string.IsNullOrEmpty(value) ? Guid.NewGuid().ToString("N") : value;

        /// <summary>True when this card owns the track, including after its Unity object was destroyed.</summary>
        public bool Matches(BrazeTrack track)
        {
            if (track == null) return false;
            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(track.TargetId))
                return string.Equals(id, track.TargetId, StringComparison.Ordinal);
            // Never let two literal/fake-null Unity references collapse into the same binding.
            return target != null && track.target != null && target == track.target;
        }

        public string DisplayName
        {
            get
            {
                return BrazeTimelineNaming.ComponentPath(target);
            }
        }
    }

    /// <summary>One typed scalar channel bound to a stable Braze parameter ID.</summary>
    [Serializable]
    public sealed class BrazeTrack
    {
        public Component target;
        [SerializeField, HideInInspector] string targetId;
        [Tooltip("Legacy serialized binding path. Retained for old timelines and Unity-native binding diagnostics.")]
        public string member;
        [Tooltip("Stable Timeline parameter identifier. New tracks resolve this before the legacy member path.")]
        public string parameterId;
        [Tooltip("Stable no-argument API event identifier. When set, curve key times invoke the event instead of sampling a value.")]
        public string eventId;
        public string eventMethod;
        public AnimationCurve curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
        public bool enabled = true;
        public bool solo;
        public bool locked;
        public Color color = new(1f, 0.45f, 0.14f);

        [NonSerialized] BrazeAnimatableField cached;
        [NonSerialized] Component cachedFor;
        [NonSerialized] string cachedId;
        [NonSerialized] string cachedMember;

        public string TargetId => targetId;
        public bool IsEvent => !string.IsNullOrEmpty(eventId) || !string.IsNullOrEmpty(eventMethod);
        public string BindingKey => IsEvent ? (!string.IsNullOrEmpty(eventId) ? eventId : eventMethod) :
            !string.IsNullOrEmpty(parameterId) ? parameterId : member;
        public bool IsMissing => target == null || (IsEvent ? ResolveEvent() == null : Resolve() == null);

        internal void SetTargetId(string value) => targetId = value;

        public BrazeAnimatableField Resolve()
        {
            if (target == null || (string.IsNullOrEmpty(parameterId) && string.IsNullOrEmpty(member)))
            {
                cached = null;
                return null;
            }
            if (cached != null && cachedFor == target && cachedId == parameterId && cachedMember == member) return cached;
            cached = BrazeAnimatableRegistry.Find(target, parameterId, member);
            cachedFor = target;
            cachedId = parameterId;
            cachedMember = member;
            if (cached != null)
            {
                // Lazy migration keeps old scene/prefab data readable without a destructive bulk migration.
                if (string.IsNullOrEmpty(parameterId)) parameterId = cached.Id;
                if (string.IsNullOrEmpty(member)) member = cached.Member;
            }
            return cached;
        }

        public float Evaluate(float sampleTime)
        {
            BrazeAnimatableField field = Resolve();
            return field != null ? field.Evaluate(curve, sampleTime) : curve?.Evaluate(sampleTime) ?? 0f;
        }

        public BrazeTimelineEventDescriptor ResolveEvent()
        {
            if (!IsEvent || target == null) return null;
            BrazeTimelineEventDescriptor descriptor = BrazeTimelineEventRegistry.Find(target, eventId);
            descriptor ??= BrazeTimelineEventRegistry.Find(target, eventMethod);
            if (descriptor != null)
            {
                if (string.IsNullOrEmpty(eventId)) eventId = descriptor.Id;
                if (string.IsNullOrEmpty(eventMethod)) eventMethod = descriptor.Method;
            }
            return descriptor;
        }

        public void InvokeEvent() => ResolveEvent()?.Invoke(target);

        public string DisplayName()
        {
            if (IsEvent)
            {
                BrazeTimelineEventDescriptor descriptor = ResolveEvent();
                return descriptor != null ? "⚡ " + descriptor.Label : "⚡ Missing Event  ⚠";
            }
            BrazeAnimatableField field = Resolve();
            if (field != null)
            {
                string path = BrazeAnimatableRegistry.DisplayPath(target, field);
                string[] parts = path.Split('/');
                string leaf = parts.Length > 0 ? parts[parts.Length - 1] : field.ShortLabel;
                bool channel = leaf.Length == 1 && "XYZWRGBA".IndexOf(char.ToUpperInvariant(leaf[0])) >= 0;
                return channel && parts.Length > 1 ? parts[parts.Length - 2] + " " + leaf.ToUpperInvariant() : leaf;
            }
            string unresolved = string.IsNullOrEmpty(member) ? parameterId : member;
            return string.IsNullOrEmpty(unresolved) ? "Missing Binding" : unresolved + "  ⚠";
        }

        public string GroupName()
        {
            return BrazeTimelineNaming.ComponentPath(target);
        }
    }
}
