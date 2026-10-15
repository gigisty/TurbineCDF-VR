using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>
    /// IMGUI building blocks for the BrazeFX inspectors — DCC/Cinema4D-style dark theme adapted from
    /// the publisher-owned three-js-volume prototype: icon header, collapsible section cards, dim labels, purple accent.
    /// Immediate-mode = robust (no binding/compile-fallback surprises). Icons are rasterized from
    /// Lucide geometry at load (cached), so we ship zero icon assets.
    /// </summary>
    internal static class BrazeEditorGUI
    {
        public static readonly Color Accent = new Color(1.0f, 0.45f, 0.14f);       // BrazeFX fiery orange ≈ #FF7324
        // Hierarchical tones: groups alternate dark/light per index; a group's sub-cards sit one step
        // lighter than the group body. currentCardBg is updated by GroupBegin for its children.
        static readonly Color GroupDarkBody = new Color(0.100f, 0.104f, 0.116f);
        static readonly Color GroupLightBody = new Color(0.139f, 0.145f, 0.162f);
        static Color currentCardBg = new Color(0.135f, 0.139f, 0.155f);             // sub-card tone for current group
        static int groupNo;                                                         // group index → dark/light
        static readonly Color Dim = new Color(0.478f, 0.494f, 0.522f);             // #7a7e85
        static readonly Color Strong = new Color(0.88f, 0.90f, 0.94f);            // section / group title (bright)
        static readonly Color AccentSoft = new Color(1.0f, 0.45f, 0.14f, 0.16f);
        static readonly Color AccentBand = new Color(1.0f, 0.45f, 0.14f, 0.10f);   // section header tint
        static readonly Color AccentLine = new Color(1.0f, 0.45f, 0.14f, 0.55f);   // header underline
        static readonly Color HeaderBlack = new Color(0.05f, 0.05f, 0.06f);        // header bar bg
        static readonly Color HintBg = new Color(1f, 1f, 1f, 0.025f);
        static readonly Color CountBg = new Color(1f, 1f, 1f, 0.06f);              // sub-panel count badge

        static GUIStyle cardStyle, titleStyle, subtitleStyle, foldoutStyle, dimLabel, wrappingDimLabel,
                        hintStyle, pillOn, pillOff;
        static GUIStyle groupFold, countStyle, groupBodyStyle;
        const int CardRadius = 6;
        const float CardGap = 8f;
        const float RowPad = 1.5f;
        const float PairedFieldGap = 10f;
        const float PairedLabelHeight = 14f;
        const float LabeledControlGap = 6f;
        const float InspectorLayoutAllowance = 40f;
        const float MinimumPropertyValueWidth = 96f;
        static bool ready;

        static int activeNumericScrub;
        static double numericScrubStart;
        static float numericScrubPixels;
        static bool numericScrubDragged;

        static void Ensure()
        {
            // Colours are re-asserted EVERY call (ReassertColors), even when the styles are already built: a cached
            // GUIStyle silently loses its forced textColor across a domain reload / editor-skin change / Play-mode
            // toggle (worst with "Enter Play Mode → Reload Domain" disabled). The old build-once guard skipped the
            // re-colour in that case, leaving every inspector's header + labels dark — the recurring colour regression.
            bool hasSkin = HasCurrentSkin();
            if (ready && cardStyle != null) { ReassertColors(); return; }
            if (!hasSkin && cardStyle != null) return;

            // Card backgrounds are painted manually via RoundedRect (display-only, no control IDs),
            // so these styles carry only padding/margin — never a background texture or GUI.Box.
            cardStyle = new GUIStyle
            {
                padding = new RectOffset(10, 9, 8, 11),
                margin = new RectOffset(0, 0, 0, 0),
            };

            titleStyle = StyleFromSkin(() => EditorStyles.boldLabel);
            titleStyle.fontSize = 12;
            titleStyle.alignment = TextAnchor.MiddleLeft;
            titleStyle.normal.textColor = Accent;   // orange text on the black header bar

            // Module qualifier next to the brand wordmark (dim, so it doesn't compete with the logo).
            subtitleStyle = StyleFromSkin(() => EditorStyles.boldLabel);
            subtitleStyle.fontSize = 11;
            subtitleStyle.alignment = TextAnchor.MiddleLeft;
            subtitleStyle.normal.textColor = Dim;

            foldoutStyle = StyleFromSkin(() => EditorStyles.foldout);
            foldoutStyle.fontStyle = FontStyle.Bold;
            foldoutStyle.fontSize = 12;   // section (sub-module) title
            SetFoldoutColor(foldoutStyle, Strong);

            dimLabel = StyleFromSkin(() => EditorStyles.label);
            dimLabel.fontSize = 11;
            dimLabel.normal.textColor = Dim;

            // Fixed Unity prefix widths truncate descriptive labels before a control even when the Inspector has
            // ample horizontal room. GUILayout rows use this wrapping variant together with a measured prefix.
            wrappingDimLabel = new GUIStyle(dimLabel) { wordWrap = true };

            hintStyle = StyleFromSkin(() => EditorStyles.label);
            hintStyle.fontSize = 10;
            hintStyle.fontStyle = FontStyle.Italic;
            hintStyle.wordWrap = true;
            hintStyle.padding = new RectOffset(6, 6, 4, 4);
            hintStyle.normal.textColor = Dim;

            pillOff = StyleFromSkin(() => EditorStyles.miniButtonMid);
            pillOff.fontSize = 10;
            pillOff.fixedHeight = 19;
            pillOn = new GUIStyle(pillOff) { fontStyle = FontStyle.Bold };
            // Solid accent fill (the same colour as the title text) so the active pill is full-bright,
            // not a dimmed tint of a grey button.
            var aTex = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            aTex.SetPixel(0, 0, new Color(0.72f, 0.37f, 0.14f)); aTex.Apply();   // muted accent (less bright than title)
            pillOn.normal.background = aTex;  pillOn.onNormal.background = aTex;
            pillOn.active.background = aTex;  pillOn.onActive.background = aTex;
            pillOn.hover.background = aTex;   pillOn.onHover.background = aTex;
            pillOn.focused.background = aTex; pillOn.onFocused.background = aTex;
            pillOn.normal.textColor = Color.white;
            pillOn.onNormal.textColor = Color.white;
            // The solid 1×1 fill must NOT 9-slice (the inherited miniButton border scales the tiny
            // texture up, making the active pill render larger). Zero border/overflow → exact-size fill.
            pillOn.border = new RectOffset(0, 0, 0, 0);
            pillOn.overflow = new RectOffset(0, 0, 0, 0);

            groupFold = StyleFromSkin(() => EditorStyles.foldout);
            groupFold.fontStyle = FontStyle.Bold;
            groupFold.fontSize = 16;  // main module title
            SetFoldoutColor(groupFold, Strong);
            countStyle = StyleFromSkin(() => EditorStyles.miniLabel);
            countStyle.alignment = TextAnchor.MiddleCenter;
            countStyle.normal.textColor = Dim;
            groupBodyStyle = new GUIStyle
            {
                padding = new RectOffset(6, 6, 5, 5),
                margin = new RectOffset(0, 0, 0, 6),
            };
            leftPill = null;
            rightPill = null;
            dropLabelStyle = null;
            dropRowStyle = null;
            ready = hasSkin;
            ReassertColors();
        }

        internal static bool HasCurrentSkin()
        {
            if (Event.current == null || GUI.skin == null) return false;
            try
            {
                _ = GUI.skin.label;
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static GUIStyle StyleFromSkin(System.Func<GUIStyle> resolve)
        {
            if (!HasCurrentSkin()) return new GUIStyle();

            try
            {
                var style = resolve();
                return style != null ? new GUIStyle(style) : new GUIStyle();
            }
            catch
            {
                return new GUIStyle();
            }
        }

        // Re-apply the forced text colours onto the (already-allocated) cached styles. Pure colour assignment — no
        // allocation — so it is safe to run on every inspector repaint, which is what keeps a reload/skin/play-mode
        // event from ever leaving the header + labels dark. Backgrounds/textures are NOT touched here (built once).
        static void ReassertColors()
        {
            titleStyle.normal.textColor = Accent;
            subtitleStyle.normal.textColor = Dim;
            SetFoldoutColor(foldoutStyle, Strong);
            SetFoldoutColor(groupFold, Strong);
            dimLabel.normal.textColor = Dim;
            wrappingDimLabel.normal.textColor = Dim;
            hintStyle.normal.textColor = Dim;
            countStyle.normal.textColor = Dim;
            pillOn.normal.textColor = Color.white;
            pillOn.onNormal.textColor = Color.white;
        }

        static void SetFoldoutColor(GUIStyle s, Color c)
        {
            s.normal.textColor = c; s.onNormal.textColor = c;
            s.focused.textColor = c; s.onFocused.textColor = c;
            s.active.textColor = c; s.onActive.textColor = c;
            s.hover.textColor = Accent; s.onHover.textColor = Accent;
        }

        // Filled, antialiased rounded rect. Display-only — uses GUI.DrawTexture, which never
        // allocates a control ID, so backgrounds can't disturb field focus / control-ID ordering
        // (unlike GUI.Box). Repaint-guarded.
        static void RoundedRect(Rect r, Color c, float radius)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c,
                            Vector4.zero, new Vector4(radius, radius, radius, radius));
        }

        // Per-corner radius (topLeft, topRight, bottomRight, bottomLeft) — for top-rounded header bars.
        static void RoundedRect(Rect r, Color c, Vector4 radii)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c, Vector4.zero, radii);
        }

        /// <summary>Reset the per-pass card bookkeeping (group dark/light alternation + nesting depth) WITHOUT drawing
        /// a header bar. Call once at the very top of an OnInspectorGUI that uses Group/Section but no Header — else the
        /// static <c>groupNo</c> counter drifts across IMGUI's Layout/Repaint passes and grouped cards flip shade
        /// (blink) on every repaint. Header/BrandHeader already do this; this is for inspectors that draw neither.</summary>
        public static void BeginCards()
        {
            Ensure();
            groupNo = 0;
            sectionDepth = 0;
            currentCardBg = new Color(0.135f, 0.139f, 0.155f);
        }

        /// <summary>Component title bar: tinted icon chip + accent title.</summary>
        public static void Header(string title, string icon)
        {
            Ensure();
            groupNo = 0;
            sectionDepth = 0;   // start each inspector pass clean (guards against drift on an interrupted pass)
            currentCardBg = new Color(0.135f, 0.139f, 0.155f);   // default for ungrouped sections (fluid editors)
            var r = EditorGUILayout.GetControlRect(false, 30);
            EditorGUI.DrawRect(r, HeaderBlack);                                          // black header bar
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1, r.width, 1), AccentLine);       // orange underline

            var tex = BrazeIconTex.Get(icon);
            if (tex != null)
            {
                var ir = new Rect(r.x + 8, r.y + 3, 24, 24);
                var prev = GUI.color;
                GUI.color = Accent;
                GUI.DrawTexture(ir, tex, ScaleMode.ScaleToFit, true);
                GUI.color = prev;
            }
            GUI.Label(new Rect(r.x + 38, r.y, r.width - 40, r.height), title, titleStyle);
            GUILayout.Space(3);
        }

        /// <summary>Brand title bar: package-owned procedural icon plus a module subtitle.</summary>
        public static void BrandHeader(string subtitle)
        {
            Ensure();
            groupNo = 0;
            sectionDepth = 0;   // start each inspector pass clean (guards against drift on an interrupted pass)
            currentCardBg = new Color(0.135f, 0.139f, 0.155f);
            var r = EditorGUILayout.GetControlRect(false, 30);
            EditorGUI.DrawRect(r, HeaderBlack);                                          // black header bar
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1, r.width, 1), AccentLine);       // orange underline

            var tex = BrazeIconTex.Get("particles");
            if (tex != null)
            {
                var prev = GUI.color;
                GUI.color = Accent;
                GUI.DrawTexture(new Rect(r.x + 8, r.y + 3, 24, 24), tex, ScaleMode.ScaleToFit, true);
                GUI.color = prev;
            }
            if (!string.IsNullOrEmpty(subtitle))
                GUI.Label(new Rect(r.x + 38, r.y, r.width - 42, r.height), subtitle, subtitleStyle);
            GUILayout.Space(3);
        }

        static int sectionDepth;   // nesting level → each nested sub-card sits a step lighter than its parent

        /// <summary>Begin a collapsible section card. Returns true if expanded (draw rows then EndSection).</summary>
        public static bool Section(string prefsKey, string title) => Section(prefsKey, title, true);

        public static bool Section(string prefsKey, string title, bool defaultOpen)
        {
            Ensure();
            GUILayout.Space(CardGap);                                            // clear gap between cards
            sectionDepth++;
            // A nested sub-card (Section inside a Section) reads as its own card by sitting a step LIGHTER.
            Color bodyTone = Shade(currentCardBg, (sectionDepth - 1) * 0.03f);
            var card = EditorGUILayout.BeginVertical(cardStyle);
            RoundedRect(card, bodyTone, CardRadius);                             // body tone (lighter when nested)

            bool open = EditorPrefs.GetBool(prefsKey, defaultOpen);
            var hr = EditorGUILayout.GetControlRect(false, 26);                  // tall row for the large title

            // Gentle warm tint over the title row only — gives the header presence WITHOUT splitting it
            // from the body (no separate darker bar, no edge tick). Header + content share one card.
            var hbar = new Rect(card.x, hr.y - 5, card.width, hr.height + 6);
            var radii = open ? new Vector4(CardRadius, CardRadius, 0f, 0f) : Vector4.one * CardRadius;
            RoundedRect(hbar, AccentBand, radii);

            bool now = EditorGUI.Foldout(new Rect(hr.x + 6, hr.y, hr.width - 6, hr.height), open, title, true, foldoutStyle);
            if (now != open) EditorPrefs.SetBool(prefsKey, now);
            if (now) GUILayout.Space(6);
            return now;
        }

        public static void EndSection()
        {
            EditorGUILayout.EndVertical();
            if (sectionDepth > 0) sectionDepth--;
        }

        /// <summary>
        /// Begin an OUTER group card (e.g. System / Particle / Spawn) that visually contains nested
        /// sub-cards (Sections). Title bar with accent tick + a sub-panel count badge. Returns true if
        /// expanded — draw Sections then call <see cref="GroupEnd"/> with the same value.
        /// </summary>
        static Color Shade(Color c, float add) => new Color(c.r + add, c.g + add, c.b + add, 1f);

        public static bool GroupBegin(string prefsKey, string title, int count)
        {
            Ensure();
            // Groups alternate dark/light; their sub-cards sit a step lighter than the group body.
            bool darkGroup = (groupNo++ % 2) == 0;
            Color bodyCol = darkGroup ? GroupDarkBody : GroupLightBody;
            Color headerCol = Shade(bodyCol, -0.028f);
            currentCardBg = Shade(bodyCol, 0.037f);

            GUILayout.Space(CardGap);
            bool open = EditorPrefs.GetBool(prefsKey, true);
            var hr = EditorGUILayout.GetControlRect(false, 28);
            var header = new Rect(hr.x - 5, hr.y, hr.width + 10, hr.height);
            // Header: top-rounded when open (it joins the body below); fully rounded when collapsed.
            RoundedRect(header, headerCol, open ? new Vector4(CardRadius, CardRadius, 0f, 0f) : Vector4.one * CardRadius);

            bool now = EditorGUI.Foldout(new Rect(hr.x + 8, hr.y + 3, hr.width - 44, hr.height - 4),
                                         open, title, true, groupFold);
            if (count > 0)
            {
                var cr = new Rect(header.xMax - 30, hr.y + 7, 22, hr.height - 14);
                RoundedRect(cr, CountBg, 3f);
                GUI.Label(cr, count.ToString(), countStyle);
            }
            if (now != open) EditorPrefs.SetBool(prefsKey, now);
            if (now)
            {
                var bodyRect = EditorGUILayout.BeginVertical(groupBodyStyle);
                // Body: same width as the header, flush against its flat bottom (fills any layout gap),
                // bottom-rounded → header + body read as ONE connected panel.
                var bg = new Rect(header.x, header.yMax, header.width, Mathf.Max(0f, bodyRect.yMax - header.yMax));
                RoundedRect(bg, bodyCol, new Vector4(0f, 0f, CardRadius, CardRadius));
            }
            return now;
        }

        public static void GroupEnd(bool open)
        {
            if (open) EditorGUILayout.EndVertical();
        }

        /// <summary>Segmented pill control bound to an enum property (e.g. Detail Mode, Force Type).</summary>
        public static void PillsEnum(string label, SerializedProperty p)
        {
            Ensure();
            GUILayout.Space(RowPad);
            EditorGUILayout.BeginHorizontal();
            var names = p.enumDisplayNames;
            GUILayout.Label(new GUIContent(label, p.tooltip), wrappingDimLabel,
                GUILayout.Width(LayoutPrefixWidth(label, names.Length * 40f)));
            int sel = p.enumValueIndex;
            for (int i = 0; i < names.Length; i++)
            {
                bool on = i == sel;
                var prev = GUI.backgroundColor;
                if (on) GUI.backgroundColor = Color.white;
                var style = names.Length == 1 ? pillOff
                          : i == 0 ? AsLeft() : i == names.Length - 1 ? AsRight() : pillOff;
                if (GUILayout.Toggle(on, names[i], on ? pillOn : style, GUILayout.MinWidth(40)) && !on)
                    p.enumValueIndex = i;
                GUI.backgroundColor = prev;
            }
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(RowPad);
        }

        /// <summary>Segmented pill control exposing only explicit integer/enum values (useful for hidden legacy values).</summary>
        public static void PillsInt(string label, SerializedProperty p, string[] names, int[] values)
        {
            Ensure();
            if (names == null || values == null || names.Length == 0 || names.Length != values.Length) return;
            GUILayout.Space(RowPad);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(label, p.tooltip), wrappingDimLabel,
                GUILayout.Width(LayoutPrefixWidth(label, names.Length * 40f)));
            int selected = p.intValue;
            for (int i = 0; i < names.Length; i++)
            {
                bool on = selected == values[i];
                var previous = GUI.backgroundColor;
                if (on) GUI.backgroundColor = Color.white;
                var style = names.Length == 1 ? pillOff
                          : i == 0 ? AsLeft() : i == names.Length - 1 ? AsRight() : pillOff;
                if (GUILayout.Toggle(on, names[i], on ? pillOn : style, GUILayout.MinWidth(40)) && !on)
                    p.intValue = values[i];
                GUI.backgroundColor = previous;
            }
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(RowPad);
        }

        /// <summary>Equal-width pill grid exposing explicit integer/enum values.</summary>
        public static void PillsIntGrid(string label, SerializedProperty p, string[] names, int[] values, int columns)
        {
            Ensure();
            if (names == null || values == null || names.Length == 0 || names.Length != values.Length) return;
            columns = Mathf.Clamp(columns, 1, names.Length);
            int rows = Mathf.CeilToInt(names.Length / (float)columns);
            GUILayout.Space(RowPad);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(new GUIContent(label, p.tooltip), wrappingDimLabel,
                GUILayout.Width(LayoutPrefixWidth(label, columns * 72f)),
                GUILayout.MinHeight(rows * 21f));
            EditorGUILayout.BeginVertical();
            int selected = p.intValue;
            for (int row = 0; row < rows; row++)
            {
                EditorGUILayout.BeginHorizontal();
                int first = row * columns;
                int count = Mathf.Min(columns, names.Length - first);
                for (int column = 0; column < count; column++)
                {
                    int index = first + column;
                    bool on = selected == values[index];
                    Color previous = GUI.backgroundColor;
                    if (on) GUI.backgroundColor = Color.white;
                    GUIStyle style = count == 1 ? pillOff
                        : column == 0 ? AsLeft() : column == count - 1 ? AsRight() : pillOff;
                    if (GUILayout.Toggle(on, names[index], on ? pillOn : style,
                            GUILayout.MinWidth(72), GUILayout.ExpandWidth(true)) && !on)
                    {
                        p.intValue = values[index];
                        selected = values[index];
                    }
                    GUI.backgroundColor = previous;
                }
                EditorGUILayout.EndHorizontal();
                if (row + 1 < rows) GUILayout.Space(2f);
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(RowPad);
        }

        static GUIStyle leftPill, rightPill;
        static GUIStyle AsLeft()
        {
            if (leftPill == null)
            {
                leftPill = StyleFromSkin(() => EditorStyles.miniButtonLeft);
                leftPill.fontSize = 10;
                leftPill.fixedHeight = 19;
            }
            return leftPill;
        }

        static GUIStyle AsRight()
        {
            if (rightPill == null)
            {
                rightPill = StyleFromSkin(() => EditorStyles.miniButtonRight);
                rightPill.fontSize = 10;
                rightPill.fixedHeight = 19;
            }
            return rightPill;
        }

        /// <summary>Generic segmented pill control over fixed string options. Returns the chosen index.</summary>
        public static int Segmented(string label, string[] names, int selected)
        {
            Ensure();
            GUILayout.Space(RowPad);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label(label, wrappingDimLabel,
                GUILayout.Width(LayoutPrefixWidth(label, names.Length * 36f)));
            int result = selected;
            for (int i = 0; i < names.Length; i++)
            {
                bool on = i == selected;
                var prev = GUI.backgroundColor;
                if (on) GUI.backgroundColor = Color.white;
                var style = names.Length == 1 ? pillOff
                          : i == 0 ? AsLeft() : i == names.Length - 1 ? AsRight() : pillOff;
                if (GUILayout.Toggle(on, names[i], on ? pillOn : style, GUILayout.MinWidth(36)) && !on)
                    result = i;
                GUI.backgroundColor = prev;
            }
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(RowPad);
            return result;
        }

        /// <summary>Standard padded property row used by branded inspectors.</summary>
        public static void PropertyField(SerializedProperty p, GUIContent label = null, bool includeChildren = false)
        {
            Ensure();
            GUIContent content = PropertyContent(p, label);
            float height = PropertyHeight(p, content, includeChildren);
            Rect row = EditorGUILayout.GetControlRect(content != GUIContent.none, height);
            DrawProperty(row, p, content, includeChildren);
        }

        /// <summary>Rect-based counterpart for compact/custom rows. Keeping these fields on the shared path makes
        /// their cursor and numeric scrubbing behaviour match normal inspector rows.</summary>
        public static void PropertyField(Rect row, SerializedProperty p, GUIContent label = null,
            bool includeChildren = false)
        {
            Ensure();
            DrawProperty(row, p, PropertyContent(p, label), includeChildren);
        }

        /// <summary>Read-only label/value row with the same measured prefix behaviour as editable fields.</summary>
        public static void LabelField(string label, string value)
        {
            Ensure();
            Rect row = EditorGUILayout.GetControlRect(true, EditorGUIUtility.singleLineHeight);
            float previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = PropertyLabelWidth(row.width, previousLabelWidth,
                PreferredLabelWidth(new GUIContent(label), EditorStyles.label));
            try
            {
                EditorGUI.LabelField(row, new GUIContent(label, label), new GUIContent(value, value));
            }
            finally
            {
                EditorGUIUtility.labelWidth = previousLabelWidth;
            }
        }

        /// <summary>Draw all visible top-level properties through the shared inspector field path.</summary>
        public static void VisibleProperties(SerializedObject serializedObject, params string[] excludedPaths)
        {
            if (serializedObject == null) return;
            var property = serializedObject.GetIterator();
            bool enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = false;
                bool excluded = false;
                if (excludedPaths != null)
                {
                    for (int i = 0; i < excludedPaths.Length; i++)
                    {
                        if (property.propertyPath != excludedPaths[i]) continue;
                        excluded = true;
                        break;
                    }
                }
                if (excluded) continue;

                using (new EditorGUI.DisabledScope(property.propertyPath == "m_Script"))
                {
                    GUIContent content = new(property.displayName, property.tooltip);
                    float height = PropertyHeight(property, content, true, suppressLayoutDecorators: false);
                    Rect row = EditorGUILayout.GetControlRect(true, height);
                    DrawProperty(row, property, content, true, suppressLayoutDecorators: false);
                }
            }
        }

        public static void Slider(SerializedProperty p, float min, float max, GUIContent label = null)
        {
            Ensure();
            GUIContent content = label ?? new GUIContent(p.displayName, p.tooltip);
            Rect row = EditorGUILayout.GetControlRect(content != GUIContent.none,
                EditorGUIUtility.singleLineHeight);
            EditorGUI.Slider(row, p, min, max, content);
            EditorGUIUtility.AddCursorRect(row, MouseCursor.SlideArrow);
        }

        public static float Slider(float value, float min, float max, GUIContent label = null)
        {
            Ensure();
            GUIContent content = label ?? GUIContent.none;
            Rect row = EditorGUILayout.GetControlRect(content != GUIContent.none,
                EditorGUIUtility.singleLineHeight);
            float result = EditorGUI.Slider(row, content, value, min, max);
            EditorGUIUtility.AddCursorRect(row, MouseCursor.SlideArrow);
            return result;
        }

        public static float Slider(string label, float value, float min, float max) =>
            Slider(value, min, max, new GUIContent(label));

        public static float Slider(GUIContent label, float value, float min, float max) =>
            Slider(value, min, max, label);

        public static float Slider(Rect row, GUIContent label, float value, float min, float max)
        {
            Ensure();
            float result = EditorGUI.Slider(row, label ?? GUIContent.none, value, min, max);
            EditorGUIUtility.AddCursorRect(row, MouseCursor.SlideArrow);
            return result;
        }

        public static void IntSlider(SerializedProperty p, int min, int max, GUIContent label = null)
        {
            Ensure();
            GUIContent content = label ?? new GUIContent(p.displayName, p.tooltip);
            Rect row = EditorGUILayout.GetControlRect(content != GUIContent.none,
                EditorGUIUtility.singleLineHeight);
            EditorGUI.IntSlider(row, p, min, max, content);
            EditorGUIUtility.AddCursorRect(row, MouseCursor.SlideArrow);
        }

        public static int IntSlider(GUIContent label, int value, int min, int max)
        {
            Ensure();
            Rect row = EditorGUILayout.GetControlRect(label != GUIContent.none,
                EditorGUIUtility.singleLineHeight);
            int result = EditorGUI.IntSlider(row, label, value, min, max);
            EditorGUIUtility.AddCursorRect(row, MouseCursor.SlideArrow);
            return result;
        }

        /// <summary>One-line randomized-range row: the caption sits in the standard prefix column and two compact
        /// "Min"/"Max" fields share the value column. Both fields go through the shared rect-based path so their
        /// numeric scrubbing and cursor behaviour match normal inspector rows.</summary>
        public static void MinMaxRow(GUIContent label, SerializedProperty min, SerializedProperty max)
        {
            Ensure();
            GUILayout.Space(RowPad);
            Rect row = EditorGUILayout.GetControlRect(true, EditorGUIUtility.singleLineHeight);
            Rect indented = EditorGUI.IndentedRect(row);
            float labelWidth = PropertyLabelWidth(indented.width, EditorGUIUtility.labelWidth,
                PreferredLabelWidth(label, EditorStyles.label));
            GUI.Label(new Rect(indented.x, indented.y, labelWidth, indented.height), label, EditorStyles.label);
            float fieldsX = indented.x + labelWidth;
            float fieldWidth = Mathf.Max(0f, (indented.xMax - fieldsX - PairedFieldGap) * 0.5f);
            PropertyField(new Rect(fieldsX, indented.y, fieldWidth, indented.height), min,
                new GUIContent("Min", min.tooltip));
            PropertyField(new Rect(fieldsX + fieldWidth + PairedFieldGap, indented.y, fieldWidth, indented.height),
                max, new GUIContent("Max", max.tooltip));
        }

        /// <summary>Two equal-width fields with one shared geometry pass. Explicit rectangles keep mixed controls
        /// (plain numbers, sliders, booleans) aligned and give each numeric input a reliable scrub zone.</summary>
        public static void PairedFields(SerializedProperty left, GUIContent leftLabel,
            SerializedProperty right, GUIContent rightLabel)
        {
            Ensure();
            float leftHeight = PropertyHeight(left, GUIContent.none, false);
            float rightHeight = PropertyHeight(right, GUIContent.none, false);
            float fieldHeight = Mathf.Max(EditorGUIUtility.singleLineHeight, leftHeight, rightHeight);
            float totalHeight = PairedLabelHeight + 2f + fieldHeight;
            GUILayout.Space(RowPad);
            Rect row = EditorGUILayout.GetControlRect(false, totalHeight);
            float width = (row.width - PairedFieldGap) * 0.5f;

            Rect leftLabelRect = new(row.x, row.y, width, PairedLabelHeight);
            Rect rightLabelRect = new(row.x + width + PairedFieldGap, row.y, width, PairedLabelHeight);
            Rect leftFieldRect = new(row.x, row.y + PairedLabelHeight + 2f, width, leftHeight);
            Rect rightFieldRect = new(rightLabelRect.x, leftFieldRect.y, width, rightHeight);

            GUI.Label(leftLabelRect, leftLabel, dimLabel);
            GUI.Label(rightLabelRect, rightLabel, dimLabel);
            DrawProperty(leftFieldRect, left, GUIContent.none, false,
                new Rect(leftLabelRect.x, leftLabelRect.y, leftLabelRect.width,
                    leftFieldRect.yMax - leftLabelRect.y));
            DrawProperty(rightFieldRect, right, GUIContent.none, false,
                new Rect(rightLabelRect.x, rightLabelRect.y, rightLabelRect.width,
                    rightFieldRect.yMax - rightLabelRect.y));
            GUILayout.Space(RowPad);
        }

        internal static float PropertyHeight(SerializedProperty property, GUIContent label = null,
            bool includeChildren = false, bool suppressLayoutDecorators = true)
        {
            if (suppressLayoutDecorators && HasRedundantLayoutDecorator(property, includeChildren))
                return EditorGUIUtility.singleLineHeight;
            GUIContent content = label ?? new GUIContent(property.displayName, property.tooltip);
            return EditorGUI.GetPropertyHeight(property, content, includeChildren);
        }

        static bool HasRedundantLayoutDecorator(SerializedProperty property, bool includeChildren)
        {
            if (includeChildren && property.hasVisibleChildren) return false;
            if (property.isArray || property.propertyType == SerializedPropertyType.Generic ||
                property.propertyType == SerializedPropertyType.ManagedReference)
                return false;

            FieldInfo field = SerializedField(property);
            if (field == null) return false;
            if (field.IsDefined(typeof(MultilineAttribute), true) ||
                field.IsDefined(typeof(TextAreaAttribute), true))
                return false;
            return field.IsDefined(typeof(HeaderAttribute), true) ||
                   field.IsDefined(typeof(SpaceAttribute), true);
        }

        static FieldInfo SerializedField(SerializedProperty property)
        {
            Object target = property.serializedObject.targetObject;
            if (target == null || property.propertyPath.IndexOf('.') >= 0) return null;

            System.Type type = target.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField(property.propertyPath,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field;
                type = type.BaseType;
            }
            return null;
        }

        // [Range] properties render as a native slider; its track already owns drag semantics. A scrub zone on
        // top would steal hotControl two pixels into every slider drag and replace the slider's screen-mapped
        // movement with the scrub's magnitude-based step — near-zero for small values, i.e. a "rigid" slider.
        static bool DrawsAsSlider(SerializedProperty property)
        {
            FieldInfo field = SerializedField(property);
            return field != null && field.IsDefined(typeof(RangeAttribute), true);
        }

        static void DrawProperty(Rect row, SerializedProperty property, GUIContent label, bool includeChildren,
            Rect? scrubZone = null, bool suppressLayoutDecorators = true)
        {
            float previousLabelWidth = EditorGUIUtility.labelWidth;
            if (label != GUIContent.none && !string.IsNullOrEmpty(label?.text))
                EditorGUIUtility.labelWidth = PropertyLabelWidth(row.width, previousLabelWidth,
                    PreferredLabelWidth(label, EditorStyles.label));

            try
            {
                bool numeric = (property.propertyType == SerializedPropertyType.Float ||
                                property.propertyType == SerializedPropertyType.Integer) &&
                               !DrawsAsSlider(property);
                Rect hotZone = scrubZone ?? NumericFieldRect(row, label);
                if (numeric) HandleNumericScrub(hotZone, property);

                float rawHeight = EditorGUI.GetPropertyHeight(property, label, includeChildren);
                if (suppressLayoutDecorators && HasRedundantLayoutDecorator(property, includeChildren) &&
                    rawHeight > row.height + 0.5f)
                {
                    // Header/Space decorators belong to Unity's fallback inspector. Branded inspectors already
                    // provide section cards, so clip the decorator and shift only the actual field into the row.
                    GUI.BeginGroup(row);
                    EditorGUI.PropertyField(new Rect(0f, row.height - rawHeight, row.width, rawHeight),
                        property, label, includeChildren);
                    GUI.EndGroup();
                }
                else
                {
                    EditorGUI.PropertyField(row, property, label, includeChildren);
                }
                if (numeric) EditorGUIUtility.AddCursorRect(hotZone, MouseCursor.SlideArrow);
            }
            finally
            {
                EditorGUIUtility.labelWidth = previousLabelWidth;
            }
        }

        static Rect NumericFieldRect(Rect row, GUIContent label)
        {
            if (label == GUIContent.none || string.IsNullOrEmpty(label?.text)) return row;
            Rect indented = EditorGUI.IndentedRect(row);
            float labelWidth = Mathf.Min(EditorGUIUtility.labelWidth, indented.width * 0.55f);
            return new Rect(indented.x + labelWidth, indented.y,
                Mathf.Max(0f, indented.width - labelWidth), indented.height);
        }

        static void HandleNumericScrub(Rect hotZone, SerializedProperty property)
        {
            int seed = property.propertyPath.GetHashCode();
            if (property.serializedObject.targetObject != null)
                seed = seed * 31 + property.serializedObject.targetObject.BrazeInstanceId();
            int id = GUIUtility.GetControlID(seed, FocusType.Passive, hotZone);
            Event e = Event.current;
            bool pending = activeNumericScrub == id;
            bool hover = hotZone.Contains(e.mousePosition);

            if (e.type == EventType.MouseDown && e.button == 0 && hover)
            {
                activeNumericScrub = id;
                numericScrubStart = property.propertyType == SerializedPropertyType.Integer
                    ? property.intValue : property.floatValue;
                numericScrubPixels = 0f;
                numericScrubDragged = false;
                // Do not consume mouse-down. The native numeric field must receive it so a normal click,
                // double-click, caret placement, and keyboard entry continue to work.
                return;
            }

            if (!pending) return;
            // A fresh press anywhere else while still armed means the release happened where this row never
            // saw it (outside the window, over another control) — disarm instead of hijacking the new drag.
            if (e.type == EventType.MouseDown)
            {
                activeNumericScrub = 0;
                numericScrubPixels = 0f;
                numericScrubDragged = false;
                return;
            }
            if (e.type == EventType.MouseDrag)
            {
                numericScrubPixels += e.delta.x;
                if (!numericScrubDragged && Mathf.Abs(numericScrubPixels) >= 2f)
                {
                    numericScrubDragged = true;
                    GUIUtility.hotControl = id;      // take over only once this is clearly a drag
                    GUIUtility.keyboardControl = 0;
                    EditorGUIUtility.SetWantsMouseJumping(1);
                }
                if (numericScrubDragged)
                {
                    float modifier = e.alt ? 0.01f : e.shift ? 0.1f :
                        (e.control || e.command) ? 10f : 1f;
                    if (property.propertyType == SerializedPropertyType.Integer)
                    {
                        property.intValue = (int)ScrubbedValue(numericScrubStart,
                            numericScrubPixels, true, modifier);
                    }
                    else
                    {
                        property.floatValue = (float)ScrubbedValue(numericScrubStart,
                            numericScrubPixels, false, modifier);
                    }
                    GUI.changed = true;
                }
                if (numericScrubDragged) e.Use();
                return;
            }

            if (e.type == EventType.MouseUp || e.type == EventType.MouseLeaveWindow)
            {
                bool wasDragging = numericScrubDragged;
                activeNumericScrub = 0;
                if (GUIUtility.hotControl == id) GUIUtility.hotControl = 0;
                numericScrubPixels = 0f;
                numericScrubDragged = false;
                if (wasDragging)
                {
                    EditorGUIUtility.SetWantsMouseJumping(0);
                    e.Use();
                }
                // An ordinary mouse-up remains untouched for native text editing.
                return;
            }
        }

        internal static double ScrubbedValue(double start, float pixels, bool integer, float modifier)
        {
            if (integer) return System.Math.Round(start + pixels * modifier / 6f);
            double magnitude = System.Math.Abs(start);
            double decade = magnitude > 1e-9
                ? System.Math.Pow(10.0, System.Math.Floor(System.Math.Log10(magnitude)))
                : 1.0;
            double step = System.Math.Max(0.0001, decade * 0.01) * modifier;
            double value = start + pixels * step;
            return System.Math.Round(value / step) * step;
        }

        public static float DelayedFloatField(float value, params GUILayoutOption[] options)
        {
            Ensure();
            Rect row = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight, options);
            int id = GUIUtility.GetControlID("BrazeRawFloatScrub".GetHashCode(), FocusType.Passive, row);
            HandleRawNumericScrub(row, id, value, false, out double scrubbed);
            if (activeNumericScrub == id && numericScrubDragged) value = (float)scrubbed;
            value = EditorGUI.DelayedFloatField(row, value);
            EditorGUIUtility.AddCursorRect(row, MouseCursor.SlideArrow);
            return value;
        }

        public static int DelayedIntField(int value, params GUILayoutOption[] options)
        {
            Ensure();
            Rect row = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight, options);
            int id = GUIUtility.GetControlID("BrazeRawIntScrub".GetHashCode(), FocusType.Passive, row);
            HandleRawNumericScrub(row, id, value, true, out double scrubbed);
            if (activeNumericScrub == id && numericScrubDragged) value = (int)scrubbed;
            value = EditorGUI.DelayedIntField(row, value);
            EditorGUIUtility.AddCursorRect(row, MouseCursor.SlideArrow);
            return value;
        }

        static void HandleRawNumericScrub(Rect hotZone, int id, double shown, bool integer, out double value)
        {
            value = shown;
            Event e = Event.current;
            bool pending = activeNumericScrub == id;
            bool hover = hotZone.Contains(e.mousePosition);
            if (e.type == EventType.MouseDown && e.button == 0 && hover)
            {
                activeNumericScrub = id;
                numericScrubStart = shown;
                numericScrubPixels = 0f;
                numericScrubDragged = false;
                return;
            }

            if (!pending) return;
            // A fresh press elsewhere while still armed means the release was never seen — disarm.
            if (e.type == EventType.MouseDown)
            {
                activeNumericScrub = 0;
                numericScrubPixels = 0f;
                numericScrubDragged = false;
                return;
            }
            if (e.type == EventType.MouseDrag)
            {
                numericScrubPixels += e.delta.x;
                if (!numericScrubDragged && Mathf.Abs(numericScrubPixels) >= 2f)
                {
                    numericScrubDragged = true;
                    GUIUtility.hotControl = id;
                    GUIUtility.keyboardControl = 0;
                    EditorGUIUtility.SetWantsMouseJumping(1);
                }
                if (numericScrubDragged)
                {
                    float modifier = e.alt ? 0.01f : e.shift ? 0.1f :
                        (e.control || e.command) ? 10f : 1f;
                    value = ScrubbedValue(numericScrubStart, numericScrubPixels, integer, modifier);
                    GUI.changed = true;
                }
                if (numericScrubDragged) e.Use();
                return;
            }

            if (e.type == EventType.MouseUp || e.type == EventType.MouseLeaveWindow)
            {
                bool wasDragging = numericScrubDragged;
                activeNumericScrub = 0;
                if (GUIUtility.hotControl == id) GUIUtility.hotControl = 0;
                numericScrubPixels = 0f;
                numericScrubDragged = false;
                if (wasDragging)
                {
                    EditorGUIUtility.SetWantsMouseJumping(0);
                    e.Use();
                }
                return;
            }
        }

        static readonly string[] SignedAxisNames = { "X", "Y", "Z" };
        static readonly int[] PositiveAxisValues = { 4, 2, 0 };
        static readonly int[] NegativeAxisValues = { 5, 3, 1 };

        /// <summary>Compact two-row selector for FractureEffectorAimAxis: positive axes above negative axes.</summary>
        public static void SignedAxis(SerializedProperty p)
        {
            int current = p.enumValueIndex;
            int positive = current == 4 ? 0 : current == 2 ? 1 : current == 0 ? 2 : -1;
            int chosenPositive = Segmented("Positive", SignedAxisNames, positive);
            if (chosenPositive >= 0 && chosenPositive != positive)
                p.enumValueIndex = PositiveAxisValues[chosenPositive];

            current = p.enumValueIndex;
            int negative = current == 5 ? 0 : current == 3 ? 1 : current == 1 ? 2 : -1;
            int chosenNegative = Segmented("Negative", SignedAxisNames, negative);
            if (chosenNegative >= 0 && chosenNegative != negative)
                p.enumValueIndex = NegativeAxisValues[chosenNegative];
        }

        /// <summary>Bool drawn as an On/Off toggle BUTTON (green when on) instead of a checkbox.</summary>
        public static void ToggleButton(string label, SerializedProperty p) => ToggleButton(label, p, null);

        /// <summary>Toggle pill with an optional status text drawn next to the pill while it is ON
        /// (e.g. "Using Baked Lighting") so the active mode reads at a glance.</summary>
        public static void ToggleButton(string label, SerializedProperty p, string statusWhenOn)
        {
            Ensure();
            GUILayout.Space(RowPad);
            EditorGUILayout.BeginHorizontal();
            bool on = p.boolValue;
            GUILayout.Label(new GUIContent(label, p.tooltip), wrappingDimLabel,
                GUILayout.Width(LayoutPrefixWidth(label, 64f)));
            var prev = GUI.backgroundColor;
            if (on) GUI.backgroundColor = Color.white;
            bool now = GUILayout.Toggle(on, on ? "On" : "Off", on ? pillOn : pillOff,
                                        GUILayout.Width(64), GUILayout.Height(19));
            GUI.backgroundColor = prev;
            if (now != on) p.boolValue = now;
            if (now && !string.IsNullOrEmpty(statusWhenOn))
            {
                GUILayout.Space(8);
                GUILayout.Label(statusWhenOn, wrappingDimLabel, GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true));
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(RowPad);
        }

        static GUIContent PropertyContent(SerializedProperty property, GUIContent label)
        {
            if (label == null) return new GUIContent(property.displayName, property.tooltip);
            if (label == GUIContent.none || !string.IsNullOrEmpty(label.tooltip) ||
                string.IsNullOrEmpty(property.tooltip)) return label;
            return new GUIContent(label.text, label.image, property.tooltip);
        }

        static float PreferredLabelWidth(GUIContent content, GUIStyle style)
        {
            if (content == null || string.IsNullOrEmpty(content.text) || style == null) return 0f;
            return style.CalcSize(content).x + 4f;
        }

        static float LayoutPrefixWidth(string label, float reservedControlWidth)
        {
            float estimatedRowWidth = Mathf.Max(0f,
                EditorGUIUtility.currentViewWidth - InspectorLayoutAllowance);
            return ResponsivePrefixWidth(estimatedRowWidth, EditorGUIUtility.labelWidth - 2f,
                PreferredLabelWidth(new GUIContent(label), dimLabel), reservedControlWidth);
        }

        /// <summary>Expands a prefix to its measured caption while always reserving the requested control width.</summary>
        internal static float ResponsivePrefixWidth(float rowWidth, float defaultWidth,
            float preferredLabelWidth, float reservedControlWidth)
        {
            float available = Mathf.Max(0f, rowWidth - Mathf.Max(0f, reservedControlWidth) - LabeledControlGap);
            return Mathf.Min(Mathf.Max(0f, Mathf.Max(defaultWidth, preferredLabelWidth)), available);
        }

        /// <summary>Property prefix policy: readable captions may grow, but values retain a useful edit region.</summary>
        internal static float PropertyLabelWidth(float rowWidth, float defaultWidth, float preferredLabelWidth)
        {
            float valueLimited = Mathf.Max(0f, rowWidth - MinimumPropertyValueWidth);
            float proportionalLimit = Mathf.Max(0f, rowWidth * 0.55f);
            float maximum = Mathf.Min(valueLimited, proportionalLimit);
            return Mathf.Min(Mathf.Max(0f, Mathf.Max(defaultWidth, preferredLabelWidth)), maximum);
        }

        /// <summary>Two mutually exclusive mode pills on one horizontal row (tab-like). Turning one on turns the
        /// other off; clicking the active pill turns it off, so both-off remains a legal state.</summary>
        public static void ExclusiveToggleButtons(string labelA, SerializedProperty a,
                                                  string labelB, SerializedProperty b)
        {
            Ensure();
            GUILayout.Space(RowPad);
            EditorGUILayout.BeginHorizontal();
            bool onA = a.boolValue;
            bool onB = b.boolValue;
            var prev = GUI.backgroundColor;
            if (onA) GUI.backgroundColor = Color.white;
            bool nowA = GUILayout.Toggle(onA, labelA, onA ? pillOn : pillOff,
                                         GUILayout.Height(19), GUILayout.ExpandWidth(true));
            GUI.backgroundColor = prev;
            GUILayout.Space(4);
            if (onB) GUI.backgroundColor = Color.white;
            bool nowB = GUILayout.Toggle(onB, labelB, onB ? pillOn : pillOff,
                                         GUILayout.Height(19), GUILayout.ExpandWidth(true));
            GUI.backgroundColor = prev;
            EditorGUILayout.EndHorizontal();
            GUILayout.Space(RowPad);
            if (nowA != onA)
            {
                a.boolValue = nowA;
                if (nowA) b.boolValue = false;
            }
            else if (nowB != onB)
            {
                b.boolValue = nowB;
                if (nowB) a.boolValue = false;
            }
        }


        static GUIStyle dropLabelStyle, dropRowStyle, dropHandleStyle;
        static readonly List<Rect> reorderRows = new();
        static int reorderControl;
        static int reorderTarget;
        static string reorderPath;
        static int reorderSource = -1;
        static int reorderInsertion = -1;
        static Vector2 reorderStart;
        static bool reorderDragging;
        static Object reorderPingObject;

        static void EnsureDrop()
        {
            if (dropLabelStyle == null)
            {
                dropLabelStyle = StyleFromSkin(() => EditorStyles.label);
                dropLabelStyle.alignment = TextAnchor.MiddleCenter;
                dropLabelStyle.fontSize = 11;
                dropLabelStyle.fontStyle = FontStyle.Bold;
                dropRowStyle = StyleFromSkin(() => EditorStyles.label);
                dropRowStyle.fontSize = 11;
                dropRowStyle.alignment = TextAnchor.MiddleLeft;
                dropHandleStyle = StyleFromSkin(() => EditorStyles.miniLabel);
                dropHandleStyle.fontSize = 13;
                dropHandleStyle.alignment = TextAnchor.MiddleCenter;
            }
            // Re-assert every call — same reason as ReassertColors: a cached style loses its textColor across a
            // reload / skin change and the build-once guard would otherwise leave these dropdown labels dark.
            dropLabelStyle.normal.textColor = new Color(0.62f, 0.64f, 0.68f);
            dropRowStyle.normal.textColor = new Color(0.86f, 0.88f, 0.92f);
            dropHandleStyle.normal.textColor = new Color(0.54f, 0.56f, 0.60f);
        }

        static void RoundRect(Rect r, Color c, Vector4 radii)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, c, Vector4.zero, radii);
        }

        static void DropListAddUnique(SerializedProperty arr, Object o)
        {
            for (int i = 0; i < arr.arraySize; i++) if (arr.GetArrayElementAtIndex(i).objectReferenceValue == o) return;
            arr.arraySize++;
            arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = o;
        }

        /// <summary>Display the owning hierarchy object's authored name for component references. Unity's
        /// ObjectContent text decorates components with their type, which becomes noisy and duplicated in compact
        /// drop-list rows. Non-component assets retain their normal object name.</summary>
        internal static string HierarchyName(Object o)
        {
            if (o == null) return "<missing>";
            return o is Component component && component.gameObject != null
                ? component.gameObject.name
                : o.name;
        }

        /// <summary>Serialized arrays with different contents cannot be safely edited through Unity's combined
        /// multi-object SerializedProperty view: insertion/removal may overwrite unrelated lists on other targets.</summary>
        internal static bool CanEditObjectList(SerializedProperty arr)
        {
            if (!arr.serializedObject.isEditingMultipleObjects) return true;
            EditorGUILayout.HelpBox("Select a single object to edit this drag-and-drop list.", MessageType.Info);
            return false;
        }

        internal static void BeginReorderableRows()
        {
            EnsureDrop();
            reorderRows.Clear();
        }

        internal static void ReorderableRow(SerializedProperty arr, int index, Rect row,
            Rect handleRect, Rect nameRect, Object pingObject)
        {
            reorderRows.Add(row);
            GUI.Label(handleRect, "≡", dropHandleStyle);
            EditorGUIUtility.AddCursorRect(handleRect, MouseCursor.Pan);
            EditorGUIUtility.AddCursorRect(nameRect, MouseCursor.Pan);

            Event e = Event.current;
            bool hit = handleRect.Contains(e.mousePosition) || nameRect.Contains(e.mousePosition);
            int targetId = arr.serializedObject.targetObject != null
                ? arr.serializedObject.targetObject.BrazeInstanceId() : 0;
            int seed = ((targetId * 397) ^ arr.propertyPath.GetHashCode()) * 397 + index;
            int id = GUIUtility.GetControlID(seed, FocusType.Passive, row);
            if (e.type != EventType.MouseDown || e.button != 0 || !hit || GUIUtility.hotControl != 0) return;

            reorderControl = id;
            reorderTarget = targetId;
            reorderPath = arr.propertyPath;
            reorderSource = index;
            reorderInsertion = index;
            reorderStart = e.mousePosition;
            reorderDragging = false;
            reorderPingObject = nameRect.Contains(e.mousePosition) ? pingObject : null;
            GUIUtility.hotControl = id;
            e.Use();
        }

        internal static bool EndReorderableRows(SerializedProperty arr)
        {
            if (reorderControl == 0 || reorderRows.Count == 0) return false;
            int targetId = arr.serializedObject.targetObject != null
                ? arr.serializedObject.targetObject.BrazeInstanceId() : 0;
            if (reorderTarget != targetId || reorderPath != arr.propertyPath) return false;

            Event e = Event.current;
            if (e.type == EventType.MouseDrag && GUIUtility.hotControl == reorderControl)
            {
                if (!reorderDragging && (e.mousePosition - reorderStart).sqrMagnitude >= 9f)
                    reorderDragging = true;
                if (reorderDragging)
                {
                    reorderInsertion = ReorderInsertionAt(e.mousePosition);
                    GUI.changed = true;
                    e.Use();
                }
            }

            if (e.type == EventType.Repaint && reorderDragging && reorderInsertion >= 0)
            {
                float y = reorderInsertion < reorderRows.Count
                    ? reorderRows[reorderInsertion].y - 1f
                    : reorderRows[reorderRows.Count - 1].yMax + 1f;
                Rect first = reorderRows[0];
                EditorGUI.DrawRect(new Rect(first.x + 3f, y, first.width - 6f, 2f), Accent);
            }

            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape &&
                GUIUtility.hotControl == reorderControl)
            {
                ResetReorderState();
                e.Use();
                return false;
            }

            if ((e.type == EventType.MouseUp && e.button == 0) || e.type == EventType.MouseLeaveWindow)
            {
                bool moved = false;
                if (e.type == EventType.MouseUp)
                {
                    if (reorderDragging && reorderInsertion >= 0)
                        moved = MoveObjectListElement(arr, reorderSource, reorderInsertion);
                    else if (!reorderDragging && reorderPingObject != null)
                        EditorGUIUtility.PingObject(reorderPingObject);
                }
                bool ownedMouse = GUIUtility.hotControl == reorderControl;
                ResetReorderState();
                if (ownedMouse) e.Use();
                return moved;
            }
            return false;
        }

        static int ReorderInsertionAt(Vector2 mouse)
        {
            Rect first = reorderRows[0];
            Rect last = reorderRows[reorderRows.Count - 1];
            if (mouse.x < first.x - 36f || mouse.x > first.xMax + 36f ||
                mouse.y < first.y - 24f || mouse.y > last.yMax + 40f)
                return -1;
            for (int i = 0; i < reorderRows.Count; i++)
                if (mouse.y < reorderRows[i].center.y) return i;
            return reorderRows.Count;
        }

        internal static bool MoveObjectListElement(SerializedProperty arr, int sourceIndex, int insertionIndex)
        {
            if (arr == null || sourceIndex < 0 || sourceIndex >= arr.arraySize ||
                insertionIndex < 0 || insertionIndex > arr.arraySize)
                return false;
            int destination = insertionIndex > sourceIndex ? insertionIndex - 1 : insertionIndex;
            destination = Mathf.Clamp(destination, 0, arr.arraySize - 1);
            if (destination == sourceIndex) return false;
            arr.MoveArrayElement(sourceIndex, destination);
            GUI.changed = true;
            return true;
        }

        static void ResetReorderState()
        {
            if (GUIUtility.hotControl == reorderControl) GUIUtility.hotControl = 0;
            reorderControl = 0;
            reorderTarget = 0;
            reorderPath = null;
            reorderSource = -1;
            reorderInsertion = -1;
            reorderDragging = false;
            reorderPingObject = null;
        }

        /// <summary>A branded drag-and-drop object list — rounded rows (orange accent bar, ping, ✕ remove) plus a
        /// "drop here" zone — matching the emitter's connected-emitter list. <paramref name="resolve"/> maps a dragged
        /// object to the stored element type (or returns null to reject it).</summary>
        public static void ObjectDropList(SerializedProperty arr, string emptyLabel, string addLabel,
                                          System.Func<Object, Object> resolve,
                                          bool reorderable = false)
        {
            Ensure(); EnsureDrop();
            if (!CanEditObjectList(arr)) return;
            if (reorderable) BeginReorderableRows();
            for (int i = 0; i < arr.arraySize; i++)
            {
                var o = arr.GetArrayElementAtIndex(i).objectReferenceValue;
                var row = EditorGUILayout.GetControlRect(false, 22f);
                RoundRect(row, new Color(1f, 1f, 1f, 0.05f), new Vector4(4, 4, 4, 4));
                RoundRect(new Rect(row.x, row.y, 2f, row.height), new Color(Accent.r, Accent.g, Accent.b, 0.7f), Vector4.zero);
                var handleR = new Rect(row.x + 5f, row.y, 14f, row.height);
                var nameR = new Rect(row.x + (reorderable ? 23f : 10f), row.y,
                    row.width - (reorderable ? 51f : 36f), row.height);
                GUI.Label(nameR, HierarchyName(o), dropRowStyle);
                if (reorderable)
                    ReorderableRow(arr, i, row, handleR, nameR, o);
                else if (o != null && Event.current.type == EventType.MouseDown && nameR.Contains(Event.current.mousePosition))
                { EditorGUIUtility.PingObject(o); Event.current.Use(); }

                if (GUI.Button(new Rect(row.xMax - 24f, row.y + 2f, 20f, 18f), "✕"))
                { arr.GetArrayElementAtIndex(i).objectReferenceValue = null; arr.DeleteArrayElementAtIndex(i); break; }
            }
            if (reorderable) EndReorderableRows(arr);

            GUILayout.Space(2);
            var e = Event.current;
            var drop = GUILayoutUtility.GetRect(0f, 42f, GUILayout.ExpandWidth(true));
            bool hovering = drop.Contains(e.mousePosition) && DragAndDrop.objectReferences.Length > 0;
            RoundRect(drop, hovering ? new Color(Accent.r, Accent.g, Accent.b, 0.16f) : new Color(1f, 1f, 1f, 0.035f), new Vector4(6, 6, 6, 6));
            RoundRect(new Rect(drop.x, drop.y, drop.width, 1.5f), new Color(Accent.r, Accent.g, Accent.b, hovering ? 0.85f : 0.3f), new Vector4(6, 6, 0, 0));
            GUI.Label(drop, arr.arraySize == 0 ? emptyLabel : addLabel, dropLabelStyle);

            if ((e.type == EventType.DragUpdated || e.type == EventType.DragPerform) && drop.Contains(e.mousePosition))
            {
                bool any = false;
                foreach (var o in DragAndDrop.objectReferences) if (resolve(o) != null) { any = true; break; }
                DragAndDrop.visualMode = any ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
                if (e.type == EventType.DragPerform && any)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var o in DragAndDrop.objectReferences) { var r = resolve(o); if (r != null) DropListAddUnique(arr, r); }
                }
                e.Use();
            }
        }

        static readonly Color SepLine = new Color(1f, 1f, 1f, 0.07f);
        static readonly Color HairLine = new Color(1f, 1f, 1f, 0.045f);

        /// <summary>Horizontal divider between control groups, with breathing room.</summary>
        public static void Separator()
        {
            Ensure();
            GUILayout.Space(8);
            var r = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(new Rect(r.x - 4, r.y, r.width + 8, 1), SepLine);
            GUILayout.Space(8);
        }

        /// <summary>Faint per-row divider: fainter and tighter than <see cref="Separator"/>. Goes BETWEEN adjacent
        /// parameters so dense rows read as distinct; Separator remains the divider between control GROUPS.</summary>
        public static void Hairline()
        {
            Ensure();
            GUILayout.Space(4);
            var r = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(new Rect(r.x - 4, r.y, r.width + 8, 1), HairLine);
            GUILayout.Space(3);
        }

        /// <summary>Small dim caps sub-title to label a group inside a section.</summary>
        public static void SubLabel(string text)
        {
            Ensure();
            GUILayout.Space(3);
            GUILayout.Label(text, dimLabel);
        }

        /// <summary>A labeled group divider: a horizontal rule + a dim caps sub-title. Groups related controls
        /// inside a section without spending a whole collapsible card on each cluster of sliders.</summary>
        public static void SubHeader(string text)
        {
            Separator();
            SubLabel(text);
        }

        /// <summary>Subtle inline helper note (FImpossible-style guidance text).</summary>
        public static void Hint(string text)
        {
            Ensure();
            GUILayout.Space(6);
            var r = GUILayoutUtility.GetRect(new GUIContent(text), hintStyle);
            EditorGUI.DrawRect(r, HintBg);
            EditorGUI.DrawRect(new Rect(r.x, r.y, 2, r.height), AccentSoft);
            GUI.Label(r, text, hintStyle);
            GUILayout.Space(2);
        }
    }

    /// <summary>Lucide-geometry icons rasterized to cached textures (white + alpha, tinted on draw).</summary>
    internal static class BrazeIconTex
    {
        static readonly Dictionary<string, Texture2D> cache = new();
        const int Size = 48;

        public static Texture2D Get(string name)
        {
            if (cache.TryGetValue(name, out var t) && t != null) return t;
            t = Rasterize(Strokes(name), Size, Color.white);
            cache[name] = t;
            return t;
        }

        /// <summary>Fresh, readable, colored icon texture (for exporting to a PNG asset).</summary>
        public static Texture2D CreateColored(string name, Color col, int size) => Rasterize(Strokes(name), size, col);

        static List<Vector2[]> Strokes(string name)
        {
            var s = new List<Vector2[]>();
            switch (name)
            {
                case "emitter": // a source nozzle launching particles into the simulation
                    s.Add(Poly(P(5f, 20f), P(8f, 15.5f), P(16f, 15.5f), P(19f, 20f), P(5f, 20f)));
                    s.Add(Poly(P(12f, 15.5f), P(12f, 8f)));
                    s.Add(Poly(P(9.2f, 10.8f), P(12f, 8f), P(14.8f, 10.8f)));
                    s.Add(Circle(6f, 7f, 1.25f, 10));
                    s.Add(Circle(12f, 3.8f, 1.45f, 10));
                    s.Add(Circle(18f, 7f, 1.25f, 10));
                    break;
                case "renderer": // eye
                    s.Add(Poly(P(3, 12), P(8, 6.5f), P(12, 5.5f), P(16, 6.5f), P(21, 12), P(16, 17.5f), P(12, 18.5f), P(8, 17.5f), P(3, 12)));
                    s.Add(Circle(12, 12, 3.0f, 14));
                    break;
                case "container": // cube
                    s.Add(Poly(P(12, 2.5f), P(20.5f, 7.25f), P(20.5f, 16.75f), P(12, 21.5f), P(3.5f, 16.75f), P(3.5f, 7.25f), P(12, 2.5f)));
                    s.Add(Poly(P(3.5f, 7.25f), P(12, 12), P(20.5f, 7.25f)));
                    s.Add(Poly(P(12, 12), P(12, 21.5f)));
                    break;
                case "cfd": // domain cube crossed by a pair of airflow streamlines
                    s.Add(Poly(P(4f, 4f), P(20f, 4f), P(20f, 20f), P(4f, 20f), P(4f, 4f)));
                    s.Add(Poly(P(2.5f, 9f), P(7f, 9f), P(9.5f, 7f), P(13f, 7f), P(15f, 9f), P(21.5f, 9f)));
                    s.Add(Poly(P(2.5f, 15f), P(8f, 15f), P(10f, 17f), P(14f, 17f), P(16f, 15f), P(21.5f, 15f)));
                    break;
                case "wind_tunnel": // test section with an inlet fan and a forward airflow arrow
                    s.Add(Poly(P(3.5f, 5f), P(20.5f, 5f), P(20.5f, 19f), P(3.5f, 19f), P(3.5f, 5f)));
                    s.Add(Circle(7f, 12f, 3.2f, 16));
                    s.Add(Poly(P(7f, 8.8f), P(8.2f, 11f), P(7f, 12f), P(5.8f, 11f), P(7f, 8.8f)));
                    s.Add(Poly(P(7f, 12f), P(9.5f, 13f), P(8.5f, 15f), P(6.3f, 14.4f), P(7f, 12f)));
                    s.Add(Poly(P(11f, 12f), P(18.5f, 12f)));
                    s.Add(Poly(P(16f, 9.5f), P(18.5f, 12f), P(16f, 14.5f)));
                    break;
                case "test_body": // aerodynamic body with pressure contours
                    s.Add(Poly(P(4f, 14.5f), P(7f, 9f), P(13f, 6.5f), P(19.5f, 10f), P(20f, 14.5f), P(4f, 14.5f)));
                    s.Add(Poly(P(5.5f, 12.5f), P(9f, 10.5f), P(13f, 10.8f), P(17.5f, 9.5f)));
                    s.Add(Poly(P(5.5f, 16.8f), P(10f, 15.5f), P(14.5f, 16f), P(19f, 15.2f)));
                    s.Add(Poly(P(2f, 7f), P(8f, 7f)));
                    s.Add(Poly(P(6f, 5f), P(8f, 7f), P(6f, 9f)));
                    break;
                case "inlet": // airflow entering through a finite patch
                    s.Add(Poly(P(4f, 4f), P(4f, 20f)));
                    s.Add(Poly(P(7f, 7f), P(16.5f, 7f), P(20f, 12f), P(16.5f, 17f), P(7f, 17f)));
                    s.Add(Poly(P(7f, 12f), P(19f, 12f)));
                    break;
                case "outlet": // airflow leaving through a finite pressure patch
                    s.Add(Poly(P(20f, 4f), P(20f, 20f)));
                    s.Add(Poly(P(4f, 7f), P(13.5f, 7f), P(17f, 12f), P(13.5f, 17f), P(4f, 17f)));
                    s.Add(Poly(P(5f, 12f), P(17f, 12f)));
                    break;
                case "boundary_patch": // domain-face patch with bidirectional boundary-condition arrows
                    s.Add(Poly(P(12f, 3f), P(12f, 21f)));
                    s.Add(Poly(P(3f, 8f), P(8f, 8f), P(10f, 6f)));
                    s.Add(Poly(P(8f, 8f), P(10f, 10f)));
                    s.Add(Poly(P(21f, 16f), P(16f, 16f), P(14f, 14f)));
                    s.Add(Poly(P(16f, 16f), P(14f, 18f)));
                    break;
                case "heater": // heat source with rising thermal waves
                    s.Add(Poly(P(4f, 19f), P(20f, 19f)));
                    s.Add(Poly(P(7f, 16f), P(6f, 13f), P(8f, 10f), P(7f, 7f), P(8f, 4f)));
                    s.Add(Poly(P(12f, 16f), P(11f, 13f), P(13f, 10f), P(12f, 7f), P(13f, 4f)));
                    s.Add(Poly(P(17f, 16f), P(16f, 13f), P(18f, 10f), P(17f, 7f), P(18f, 4f)));
                    break;
                case "heatmap": // three isotherm contours inside a sampling frame
                    s.Add(Poly(P(3.5f, 3.5f), P(20.5f, 3.5f), P(20.5f, 20.5f), P(3.5f, 20.5f), P(3.5f, 3.5f)));
                    s.Add(Poly(P(5.5f, 17f), P(8f, 13f), P(11f, 15f), P(14f, 9f), P(18.5f, 11f)));
                    s.Add(Poly(P(5.5f, 12.5f), P(8.5f, 9.5f), P(11.5f, 11f), P(14.5f, 6f), P(18.5f, 7.5f)));
                    break;
                case "pressure": // surface mesh crossed by pressure contours and a resultant-load arrow
                    s.Add(Poly(P(4f, 18f), P(7f, 7f), P(14f, 4f), P(20f, 9f), P(17f, 19f), P(4f, 18f)));
                    s.Add(Poly(P(6f, 14.5f), P(10f, 12f), P(14f, 13f), P(18f, 10.5f)));
                    s.Add(Poly(P(7f, 17f), P(11f, 15f), P(15f, 16f), P(18f, 14f)));
                    s.Add(Poly(P(3f, 5f), P(9f, 5f)));
                    s.Add(Poly(P(7f, 3f), P(9f, 5f), P(7f, 7f)));
                    break;
                case "sdf": // triangulated boundary wrapped by signed-distance contours
                    s.Add(Poly(P(5f, 18.5f), P(9f, 5f), P(19f, 9f), P(15f, 19f), P(5f, 18.5f)));
                    s.Add(Poly(P(9f, 5f), P(15f, 19f)));
                    s.Add(Poly(P(5f, 18.5f), P(19f, 9f)));
                    s.Add(Poly(P(3f, 21f), P(12f, 2.5f), P(21f, 7f)));
                    break;
                case "cloud": // a puffy cloud silhouette — three overlapping lobes on a flat base
                    s.Add(Circle(8.5f, 13.5f, 4.0f, 16));
                    s.Add(Circle(13.5f, 11.0f, 5.2f, 18));
                    s.Add(Circle(17.5f, 14.0f, 3.6f, 14));
                    s.Add(Poly(P(5.5f, 17.2f), P(19.5f, 17.2f)));   // flat base
                    break;
                case "dynamics": // wind
                    s.Add(Poly(P(3, 8), P(14, 8), P(16.5f, 6), P(15, 4)));
                    s.Add(Poly(P(3, 12), P(17, 12), P(19.5f, 14.2f), P(17.5f, 16.5f)));
                    s.Add(Poly(P(3, 16), P(11, 16), P(13, 17.6f), P(11.6f, 19.4f)));
                    break;
                case "force": // vortex
                    s.Add(Spiral(12, 12, 9f, 1.5f, 2.4f, 22));
                    break;
                case "obstacle": // octagon (blocker)
                    s.Add(Circle(12, 12, 9f, 8));
                    break;
                case "radial": // burst — centre dot + outward ticks
                    s.Add(Circle(12, 12, 2.6f, 12));
                    s.Add(Poly(P(12, 6.6f), P(12, 3f)));
                    s.Add(Poly(P(12, 17.4f), P(12, 21f)));
                    s.Add(Poly(P(6.6f, 12), P(3f, 12)));
                    s.Add(Poly(P(17.4f, 12), P(21f, 12)));
                    s.Add(Poly(P(8.4f, 8.4f), P(5.6f, 5.6f)));
                    s.Add(Poly(P(15.6f, 8.4f), P(18.4f, 5.6f)));
                    s.Add(Poly(P(8.4f, 15.6f), P(5.6f, 18.4f)));
                    s.Add(Poly(P(15.6f, 15.6f), P(18.4f, 18.4f)));
                    break;
                case "pushapart": // two fragments separating from a shared centre
                    s.Add(Poly(P(10, 5), P(5, 5), P(5, 10), P(10, 10), P(10, 5)));
                    s.Add(Poly(P(14, 14), P(19, 14), P(19, 19), P(14, 19), P(14, 14)));
                    s.Add(Poly(P(9, 9), P(4, 4), P(4, 7)));
                    s.Add(Poly(P(4, 4), P(7, 4)));
                    s.Add(Poly(P(15, 15), P(20, 20), P(20, 17)));
                    s.Add(Poly(P(20, 20), P(17, 20)));
                    break;
                case "turbulence": // two wavy flow lines
                    s.Add(Poly(P(3, 9f), P(6, 6.5f), P(9, 9f), P(12, 11.5f), P(15, 9f), P(18, 6.5f), P(21, 9f)));
                    s.Add(Poly(P(3, 15f), P(6, 12.5f), P(9, 15f), P(12, 17.5f), P(15, 15f), P(18, 12.5f), P(21, 15f)));
                    break;
                case "gravity": // down arrow
                    s.Add(Poly(P(12, 3f), P(12, 18.5f)));
                    s.Add(Poly(P(7.5f, 14f), P(12, 18.8f), P(16.5f, 14f)));
                    break;
                case "drag": // concentric falloff rings
                    s.Add(Circle(12, 12, 8.5f, 20));
                    s.Add(Circle(12, 12, 5f, 16));
                    s.Add(Circle(12, 12, 2f, 10));
                    break;
                case "particles": // sparkle + scattered particle dots
                    s.Add(Poly(P(12, 2.5f), P(13.7f, 10.3f), P(21.5f, 12), P(13.7f, 13.7f),
                               P(12, 21.5f), P(10.3f, 13.7f), P(2.5f, 12), P(10.3f, 10.3f), P(12, 2.5f)));
                    s.Add(Circle(18.6f, 5.4f, 1.5f, 10));
                    s.Add(Circle(5.4f, 18.6f, 1.2f, 10));
                    break;
                case "flipbook": // stacked animation frames + play triangle
                    s.Add(Poly(P(5f, 5f), P(19f, 5f), P(19f, 19f), P(5f, 19f), P(5f, 5f)));
                    s.Add(Poly(P(8f, 5f), P(8f, 2.8f), P(21.2f, 2.8f), P(21.2f, 16f), P(19f, 16f)));
                    s.Add(Poly(P(9.5f, 8.5f), P(9.5f, 15.5f), P(15.8f, 12f), P(9.5f, 8.5f)));
                    break;
                case "flame": // flame tongue
                    s.Add(Poly(P(12f, 2.5f), P(16.5f, 8f), P(16f, 13f), P(13.5f, 15.5f), P(17f, 16f), P(17.5f, 20.5f),
                               P(12f, 22f), P(6.5f, 20.5f), P(7f, 15.5f), P(10f, 13.5f), P(10.5f, 16f), P(12.2f, 15f),
                               P(11f, 10f), P(12f, 2.5f)));
                    break;
                case "surface": // triangulated mesh patch
                    s.Add(Poly(P(3, 15.5f), P(8.5f, 5.5f), P(21, 8.5f), P(15.5f, 18.5f), P(3, 15.5f)));
                    s.Add(Poly(P(8.5f, 5.5f), P(15.5f, 18.5f)));   // diagonal split
                    s.Add(Poly(P(3, 15.5f), P(21, 8.5f)));         // diagonal split
                    break;
                case "growth": // branching vine / tree
                    s.Add(Poly(P(12, 21.5f), P(12, 12.5f)));                 // trunk
                    s.Add(Poly(P(12, 14.5f), P(7.5f, 9f), P(5.5f, 4.5f)));   // left branch
                    s.Add(Poly(P(12, 13f), P(16.5f, 8f), P(18.5f, 3.5f)));   // right branch
                    s.Add(Poly(P(9.7f, 11.6f), P(7f, 12.6f)));               // small left shoot
                    s.Add(Poly(P(14.3f, 10.4f), P(17f, 11f)));               // small right shoot
                    break;
                case "collider": // a particle bouncing off a surface
                    s.Add(Poly(P(3f, 17.5f), P(21f, 17.5f)));                // collider surface / plane
                    s.Add(Poly(P(7f, 5.5f), P(12f, 17.5f), P(17f, 5.5f)));   // incoming + reflected trajectory
                    s.Add(Circle(17f, 5.5f, 1.9f, 12));                      // the particle, after the bounce
                    break;
                case "spread": // a coverage stain creeping outward from a seed
                    s.Add(Poly(P(12f, 4.5f), P(15.5f, 5.4f), P(18.6f, 8.4f), P(19f, 12.4f), P(17.1f, 16.4f),
                               P(13f, 18.8f), P(8.6f, 18.3f), P(5f, 15.6f), P(4.4f, 11f), P(6.4f, 7f),
                               P(9.4f, 5f), P(12f, 4.5f)));                   // lumpy covered patch
                    s.Add(Circle(12f, 12f, 1.5f, 12));                       // seed point at the centre
                    s.Add(Circle(20.4f, 6.2f, 1.0f, 8));                     // advancing-front speck
                    s.Add(Circle(4.2f, 19f, 0.9f, 8));                       // advancing-front speck
                    break;
                case "mapper": // selected vertices on a surface patch (a vertex-map selection)
                    s.Add(Poly(P(3.5f, 15.5f), P(9f, 5.5f), P(20.5f, 8.5f), P(15f, 18.5f), P(3.5f, 15.5f))); // surface patch
                    s.Add(Circle(8.2f, 13.2f, 1.15f, 9));                    // selected vert
                    s.Add(Circle(11.3f, 12.0f, 1.15f, 9));                   // selected vert
                    s.Add(Circle(9.0f, 10.0f, 1.05f, 9));                    // selected vert
                    break;
                case "voronoi": // a cell fractured into Voronoi shards (junctions + cell edges)
                    s.Add(Poly(P(4, 7), P(11, 3.5f), P(19, 6), P(20.5f, 13), P(15, 20.5f), P(6.5f, 19.5f), P(3.5f, 12), P(4, 7))); // outer cell
                    s.Add(Poly(P(10, 11), P(15, 13.5f)));      // shared interior edge between two junctions
                    s.Add(Poly(P(10, 11), P(11, 3.5f)));        // junction A spokes to the rim
                    s.Add(Poly(P(10, 11), P(3.7f, 9.5f)));
                    s.Add(Poly(P(10, 11), P(7.5f, 19f)));
                    s.Add(Poly(P(15, 13.5f), P(20.3f, 10f)));   // junction B spokes to the rim
                    s.Add(Poly(P(15, 13.5f), P(14.6f, 20.2f)));
                    break;
                case "cloner": // an array of instanced cubes receding across the GPU grid
                    s.Add(Poly(P(4f, 7f), P(8f, 4.8f), P(12f, 7f), P(12f, 11.5f), P(8f, 13.8f), P(4f, 11.5f), P(4f, 7f)));
                    s.Add(Poly(P(4f, 7f), P(8f, 9.2f), P(12f, 7f)));
                    s.Add(Poly(P(8f, 9.2f), P(8f, 13.8f)));
                    s.Add(Poly(P(12f, 11f), P(15.2f, 9.2f), P(18.4f, 11f), P(18.4f, 14.6f), P(15.2f, 16.4f), P(12f, 14.6f)));
                    s.Add(Poly(P(14.8f, 16.2f), P(17.2f, 14.9f), P(19.7f, 16.3f), P(19.7f, 19.1f), P(17.2f, 20.5f), P(14.8f, 19.1f), P(14.8f, 16.2f)));
                    break;
                case "instance": // one source cube linked to its single live copy
                    s.Add(Poly(P(3.5f, 6.5f), P(7f, 4.6f), P(10.5f, 6.5f), P(10.5f, 10.4f), P(7f, 12.3f), P(3.5f, 10.4f), P(3.5f, 6.5f)));
                    s.Add(Poly(P(3.5f, 6.5f), P(7f, 8.4f), P(10.5f, 6.5f)));
                    s.Add(Poly(P(7f, 8.4f), P(7f, 12.3f)));
                    s.Add(Poly(P(13f, 13.5f), P(16.5f, 11.6f), P(20f, 13.5f), P(20f, 17.4f), P(16.5f, 19.3f), P(13f, 17.4f), P(13f, 13.5f)));
                    s.Add(Poly(P(13f, 13.5f), P(16.5f, 15.4f), P(20f, 13.5f)));
                    s.Add(Poly(P(16.5f, 15.4f), P(16.5f, 19.3f)));
                    s.Add(Poly(P(10.6f, 10.8f), P(12.9f, 12.6f)));           // link from source to copy
                    s.Add(Poly(P(11.7f, 12.7f), P(12.9f, 12.6f), P(12.7f, 11.4f))); // arrowhead
                    break;
                case "tracer": // connected nodes forming an open wire graph
                    s.Add(Circle(4.2f, 6f, 1.35f, 10));
                    s.Add(Circle(18.8f, 4.5f, 1.35f, 10));
                    s.Add(Circle(12.5f, 12f, 1.35f, 10));
                    s.Add(Circle(5.5f, 19.2f, 1.35f, 10));
                    s.Add(Circle(20f, 18f, 1.35f, 10));
                    s.Add(Poly(P(4.2f, 6f), P(12.5f, 12f), P(18.8f, 4.5f)));
                    s.Add(Poly(P(12.5f, 12f), P(5.5f, 19.2f)));
                    s.Add(Poly(P(12.5f, 12f), P(20f, 18f)));
                    s.Add(Poly(P(5.5f, 19.2f), P(20f, 18f)));
                    break;
                case "effector": // one source directly transforming several fragments
                    s.Add(Circle(7f, 12f, 2.2f, 12));
                    s.Add(Poly(P(9.2f, 11f), P(16.5f, 6f), P(20.5f, 7.5f)));
                    s.Add(Poly(P(17.1f, 4.8f), P(16.5f, 6f), P(17.8f, 6.4f)));
                    s.Add(Poly(P(9.2f, 12f), P(18.8f, 12f)));
                    s.Add(Poly(P(17.2f, 10.8f), P(18.8f, 12f), P(17.2f, 13.2f)));
                    s.Add(Poly(P(9.2f, 13f), P(16.5f, 18f), P(20.5f, 16.5f)));
                    s.Add(Poly(P(17.8f, 17.6f), P(16.5f, 18f), P(17.1f, 19.2f)));
                    break;
                case "plane": // a transform arrow rising from a finite influence plane
                    s.Add(Poly(P(3.2f, 15.5f), P(12f, 10.5f), P(20.8f, 15.5f), P(12f, 20.5f), P(3.2f, 15.5f)));
                    s.Add(Poly(P(7.6f, 13f), P(16.4f, 18f)));
                    s.Add(Poly(P(16.4f, 13f), P(7.6f, 18f)));
                    s.Add(Poly(P(12f, 15.5f), P(12f, 3.5f)));
                    s.Add(Poly(P(8.7f, 6.8f), P(12f, 3.5f), P(15.3f, 6.8f)));
                    break;
                case "step": // ascending fragment values
                    s.Add(Poly(P(3.5f, 19.5f), P(8f, 19.5f), P(8f, 15f), P(12.5f, 15f),
                               P(12.5f, 10.5f), P(17f, 10.5f), P(17f, 6f), P(21f, 6f)));
                    s.Add(Circle(5.5f, 16.5f, 1.0f, 9));
                    s.Add(Circle(10.2f, 12f, 1.0f, 9));
                    s.Add(Circle(14.7f, 7.5f, 1.0f, 9));
                    s.Add(Circle(19.2f, 3.5f, 1.0f, 9));
                    break;
                case "delay": // clock face with trailing samples
                    s.Add(Circle(13f, 12f, 7.6f, 24));
                    s.Add(Poly(P(13f, 7.2f), P(13f, 12f), P(17.2f, 14.2f)));
                    s.Add(Circle(3.2f, 7f, 0.85f, 9));
                    s.Add(Circle(2.2f, 12f, 0.85f, 9));
                    s.Add(Circle(3.2f, 17f, 0.85f, 9));
                    break;
                case "target": // crosshair with an incoming aim direction
                    s.Add(Circle(12f, 12f, 6.8f, 20));
                    s.Add(Circle(12f, 12f, 2.2f, 14));
                    s.Add(Poly(P(12f, 2.5f), P(12f, 7.2f)));
                    s.Add(Poly(P(12f, 16.8f), P(12f, 21.5f)));
                    s.Add(Poly(P(2.5f, 12f), P(7.2f, 12f)));
                    s.Add(Poly(P(16.8f, 12f), P(21.5f, 12f)));
                    break;
                case "shader": // projected grayscale tiles
                    s.Add(Poly(P(3.5f, 4f), P(20.5f, 4f), P(20.5f, 20f), P(3.5f, 20f), P(3.5f, 4f)));
                    s.Add(Poly(P(3.5f, 9.3f), P(20.5f, 9.3f)));
                    s.Add(Poly(P(3.5f, 14.7f), P(20.5f, 14.7f)));
                    s.Add(Poly(P(9.2f, 4f), P(9.2f, 20f)));
                    s.Add(Poly(P(14.8f, 4f), P(14.8f, 20f)));
                    s.Add(Circle(6.3f, 6.6f, 1.25f, 10));
                    s.Add(Circle(12f, 12f, 1.25f, 10));
                    s.Add(Circle(17.7f, 17.3f, 1.25f, 10));
                    break;
                case "disintegrate": // a shape shattering into separating chunks
                    s.Add(Poly(P(4f, 4.5f), P(10f, 5.5f), P(7.5f, 11f), P(4f, 4.5f)));            // top-left chunk
                    s.Add(Poly(P(13.5f, 4f), P(20f, 7f), P(15.5f, 12f), P(13.5f, 4f)));           // top-right chunk
                    s.Add(Poly(P(5.5f, 14.5f), P(12f, 13f), P(13.5f, 20f), P(7f, 21f), P(5.5f, 14.5f))); // bottom chunk
                    break;
                case "camera": // camera body + viewfinder bump + lens
                    s.Add(Poly(P(2.5f, 7.5f), P(8f, 7.5f), P(9.5f, 4.8f), P(14.5f, 4.8f), P(16f, 7.5f),
                               P(21.5f, 7.5f), P(21.5f, 19.5f), P(2.5f, 19.5f), P(2.5f, 7.5f)));   // body + bump
                    s.Add(Circle(12f, 13.2f, 3.6f, 16));                                            // lens
                    break;
                case "timeline": // keyframe track + playhead
                    s.Add(Poly(P(3f, 13.5f), P(21f, 13.5f)));                                                  // track baseline
                    s.Add(Poly(P(7f, 11.3f), P(9.2f, 13.5f), P(7f, 15.7f), P(4.8f, 13.5f), P(7f, 11.3f)));     // keyframe diamond
                    s.Add(Poly(P(16f, 11.3f), P(18.2f, 13.5f), P(16f, 15.7f), P(13.8f, 13.5f), P(16f, 11.3f))); // keyframe diamond
                    s.Add(Poly(P(11.5f, 4f), P(11.5f, 19f)));                                                  // playhead line
                    s.Add(Poly(P(9.4f, 5.6f), P(11.5f, 3.4f), P(13.6f, 5.6f)));                                // playhead caret
                    break;
                case "motion": // sine wave with transform key dots
                    s.Add(Poly(P(2.8f, 12f), P(5.8f, 7.3f), P(8.8f, 12f), P(11.8f, 16.7f),
                               P(14.8f, 12f), P(17.8f, 7.3f), P(21.2f, 12f)));
                    s.Add(Circle(5.8f, 7.3f, 1.15f, 10));
                    s.Add(Circle(11.8f, 16.7f, 1.15f, 10));
                    s.Add(Circle(17.8f, 7.3f, 1.15f, 10));
                    break;
                case "vdb_import": // an incoming volume frame becoming a playable sparse cloud
                    s.Add(Poly(P(12f, 2.5f), P(12f, 9.3f)));                                  // import shaft
                    s.Add(Poly(P(8.8f, 6.4f), P(12f, 9.6f), P(15.2f, 6.4f)));                  // arrow head
                    s.Add(Circle(8.2f, 15.1f, 3.3f, 14));                                      // sparse volume lobes
                    s.Add(Circle(12.8f, 13.8f, 4.2f, 18));
                    s.Add(Circle(17.1f, 15.4f, 2.8f, 14));
                    s.Add(Poly(P(5.2f, 18.2f), P(19.5f, 18.2f)));                              // volume base
                    break;
                case "export_studio": // delivery tray with an outgoing export arrow
                    s.Add(Poly(P(4f, 11f), P(4f, 20f), P(20f, 20f), P(20f, 11f)));             // output tray
                    s.Add(Poly(P(12f, 16f), P(12f, 3.2f)));                                    // export shaft
                    s.Add(Poly(P(7.7f, 7.5f), P(12f, 3.2f), P(16.3f, 7.5f)));                  // arrow head
                    s.Add(Poly(P(4f, 15.7f), P(8.2f, 15.7f)));                                 // tray lip
                    s.Add(Poly(P(15.8f, 15.7f), P(20f, 15.7f)));
                    break;
                case "bullet_spawner": // a fast projectile with twin motion trails
                    s.Add(Poly(P(8f, 9f), P(16.2f, 9f), P(21f, 12f), P(16.2f, 15f), P(8f, 15f), P(8f, 9f)));
                    s.Add(Poly(P(3f, 8f), P(8.2f, 8f)));                                       // upper trail
                    s.Add(Poly(P(2.2f, 12f), P(7.2f, 12f)));                                   // centre trail
                    s.Add(Poly(P(3f, 16f), P(8.2f, 16f)));                                     // lower trail
                    break;
                case "cache_baker": // database cylinder + a down arrow (bake / write into the cache)
                    s.Add(Ellipse(12, 7, 6.2f, 2.2f, 18));                 // top disc
                    s.Add(Poly(P(5.8f, 7), P(5.8f, 17)));                  // left wall
                    s.Add(Poly(P(18.2f, 7), P(18.2f, 17)));               // right wall
                    s.Add(Arc(12, 17, 6.2f, 2.2f, 0, Mathf.PI, 14));       // front bottom curve
                    s.Add(Poly(P(12, 10), P(12, 15.3f)));                  // arrow shaft
                    s.Add(Poly(P(9.8f, 12.7f), P(12, 15.3f), P(14.2f, 12.7f)));   // arrow head (points down)
                    break;
                case "cache_player": // database cylinder + a play triangle (play back from the cache)
                    s.Add(Ellipse(12, 7, 6.2f, 2.2f, 18));
                    s.Add(Poly(P(5.8f, 7), P(5.8f, 17)));
                    s.Add(Poly(P(18.2f, 7), P(18.2f, 17)));
                    s.Add(Arc(12, 17, 6.2f, 2.2f, 0, Mathf.PI, 14));
                    s.Add(Poly(P(10.2f, 10.1f), P(10.2f, 15.1f), P(15.2f, 12.6f), P(10.2f, 10.1f)));   // play triangle
                    break;
                case "spline": // a curved path through control-point handles
                    s.Add(Poly(P(3.5f, 18.5f), P(6f, 13.5f), P(8.5f, 8.5f), P(12f, 8f), P(15.5f, 12f),
                               P(18f, 16f), P(20.5f, 14f)));   // the curve
                    s.Add(Circle(3.5f, 18.5f, 1.5f, 12));      // control points
                    s.Add(Circle(8.5f, 8.5f, 1.5f, 12));
                    s.Add(Circle(15.5f, 12f, 1.5f, 12));
                    s.Add(Circle(20.5f, 14f, 1.5f, 12));
                    break;
                case "followspline": // a follower riding a rail — curve + carriage dot + travel arrowhead
                    s.Add(Poly(P(2.8f, 18f), P(6f, 12f), P(10f, 8f), P(14.5f, 7.8f), P(18f, 10.2f), P(20.5f, 14.5f)));   // the rail
                    s.Add(Circle(10f, 8f, 2.2f, 14));                                    // the follower on the rail
                    s.Add(Poly(P(20.9f, 11.2f), P(20.5f, 14.5f), P(17.4f, 13.6f)));      // arrowhead: direction of travel
                    break;
                case "groundfollow": // a down arrow pinning to a hatched ground line
                    s.Add(Poly(P(3f, 17.5f), P(21f, 17.5f)));                                   // ground line
                    s.Add(Poly(P(5.5f, 17.5f), P(3.5f, 20f)));                                  // ground hatch
                    s.Add(Poly(P(10f, 17.5f), P(8f, 20f)));
                    s.Add(Poly(P(14.5f, 17.5f), P(12.5f, 20f)));
                    s.Add(Poly(P(19f, 17.5f), P(17f, 20f)));
                    s.Add(Poly(P(12f, 3.5f), P(12f, 13f)));                                      // arrow shaft (down)
                    s.Add(Poly(P(8.4f, 9.4f), P(12f, 13.4f), P(15.6f, 9.4f)));                   // arrow head
                    break;
                case "ruler": // a graduated measuring stick
                    s.Add(Poly(P(9f, 3f), P(15f, 3f), P(15f, 21f), P(9f, 21f), P(9f, 3f)));   // ruler body
                    s.Add(Poly(P(9f, 6f), P(13f, 6f)));           // long graduation
                    s.Add(Poly(P(9f, 8.5f), P(11.4f, 8.5f)));     // short
                    s.Add(Poly(P(9f, 11f), P(13f, 11f)));         // long
                    s.Add(Poly(P(9f, 13.5f), P(11.4f, 13.5f)));   // short
                    s.Add(Poly(P(9f, 16f), P(13f, 16f)));         // long
                    s.Add(Poly(P(9f, 18.5f), P(11.4f, 18.5f)));   // short
                    break;
                case "noise": // scattered grain dots (fractal noise field)
                    s.Add(Circle(6.2f, 7f, 1.25f, 9));
                    s.Add(Circle(11.4f, 5.4f, 1.05f, 9));
                    s.Add(Circle(17f, 7.4f, 1.35f, 9));
                    s.Add(Circle(8.2f, 12f, 1.1f, 9));
                    s.Add(Circle(13.2f, 12.4f, 1.45f, 9));
                    s.Add(Circle(18.3f, 13f, 1.0f, 9));
                    s.Add(Circle(6.6f, 17.2f, 1.2f, 9));
                    s.Add(Circle(12f, 18.2f, 1.0f, 9));
                    s.Add(Circle(17.2f, 17.6f, 1.25f, 9));
                    break;
                case "density": // a region with a plus (add/remove density)
                    s.Add(Circle(12f, 12f, 8.6f, 24));
                    s.Add(Poly(P(12f, 7.6f), P(12f, 16.4f)));
                    s.Add(Poly(P(7.6f, 12f), P(16.4f, 12f)));
                    break;
                case "deform": // a warped grid (mesh-style deformer)
                    s.Add(Poly(P(4f, 7f), P(8.5f, 5.8f), P(14f, 7.6f), P(20f, 6.6f)));      // top row (wavy)
                    s.Add(Poly(P(4.2f, 12f), P(9f, 10.8f), P(15f, 13f), P(19.8f, 12f)));    // mid row
                    s.Add(Poly(P(4f, 17f), P(8.5f, 16f), P(14f, 18f), P(20f, 17.2f)));      // bottom row
                    s.Add(Poly(P(5f, 6.2f), P(4.4f, 17.6f)));                              // left col
                    s.Add(Poly(P(19.4f, 6f), P(20f, 17.6f)));                              // right col
                    break;
                case "smooth": // a droplet (smoothing / softening)
                    s.Add(Poly(P(12f, 3.5f), P(16.6f, 10.6f), P(17.4f, 15f), P(15.4f, 18.6f), P(12f, 20f),
                               P(8.6f, 18.6f), P(6.6f, 15f), P(7.4f, 10.6f), P(12f, 3.5f)));
                    s.Add(Arc(10f, 15f, 2.4f, 2.4f, Mathf.PI * 0.85f, Mathf.PI * 1.6f, 8));   // highlight
                    break;
                case "remap": // levels / transfer curve in axes
                    s.Add(Poly(P(4.5f, 19.5f), P(19.5f, 19.5f)));   // x axis
                    s.Add(Poly(P(4.5f, 19.5f), P(4.5f, 4.5f)));     // y axis
                    s.Add(Poly(P(4.5f, 17.3f), P(8.5f, 16f), P(12f, 9.8f), P(15.5f, 5.6f), P(19.5f, 4.6f)));   // S-curve
                    break;
                default: // emitter / flame
                    s.Add(Poly(P(12, 2.5f), P(16.5f, 8), P(16, 13), P(13.5f, 15.5f), P(17, 16), P(17.5f, 20.5f), P(12, 22),
                               P(6.5f, 20.5f), P(7, 15.5f), P(10, 13.5f), P(10.5f, 16), P(12.2f, 15), P(11, 10), P(12, 2.5f)));
                    break;
            }
            return s;
        }

        static Vector2 P(float x, float y) => new Vector2(x, y);
        static Vector2[] Poly(params Vector2[] pts) => pts;

        static Vector2[] Circle(float cx, float cy, float r, int seg)
        {
            var a = new Vector2[seg + 1];
            for (int i = 0; i <= seg; i++)
            {
                float t = i / (float)seg * Mathf.PI * 2f;
                a[i] = new Vector2(cx + Mathf.Cos(t) * r, cy + Mathf.Sin(t) * r);
            }
            return a;
        }

        static Vector2[] Ellipse(float cx, float cy, float rx, float ry, int seg)
        {
            var a = new Vector2[seg + 1];
            for (int i = 0; i <= seg; i++)
            {
                float t = i / (float)seg * Mathf.PI * 2f;
                a[i] = new Vector2(cx + Mathf.Cos(t) * rx, cy + Mathf.Sin(t) * ry);
            }
            return a;
        }

        static Vector2[] Arc(float cx, float cy, float rx, float ry, float a0, float a1, int seg)
        {
            var a = new Vector2[seg + 1];
            for (int i = 0; i <= seg; i++)
            {
                float t = Mathf.Lerp(a0, a1, i / (float)seg);
                a[i] = new Vector2(cx + Mathf.Cos(t) * rx, cy + Mathf.Sin(t) * ry);
            }
            return a;
        }

        static Vector2[] Spiral(float cx, float cy, float r0, float r1, float turns, int seg)
        {
            var a = new Vector2[seg + 1];
            for (int i = 0; i <= seg; i++)
            {
                float u = i / (float)seg;
                float ang = u * turns * Mathf.PI * 2f;
                float rad = Mathf.Lerp(r0, r1, u);
                a[i] = new Vector2(cx + Mathf.Cos(ang) * rad, cy + Mathf.Sin(ang) * rad);
            }
            return a;
        }

        static Texture2D Rasterize(List<Vector2[]> strokes, int size, Color col)
        {
            var px = new Color32[size * size];
            const float half = 0.85f;            // half stroke width in viewBox units (24-unit box)
            float aa = 24f / size * 1.2f;        // ~1px feather
            byte cr = (byte)(col.r * 255f), cg = (byte)(col.g * 255f), cb = (byte)(col.b * 255f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var pvb = new Vector2((x + 0.5f) / size * 24f, (1f - (y + 0.5f) / size) * 24f);
                    float best = 999f;
                    for (int si = 0; si < strokes.Count; si++)
                    {
                        var s = strokes[si];
                        for (int i = 0; i < s.Length - 1; i++)
                            best = Mathf.Min(best, DistSeg(pvb, s[i], s[i + 1]));
                    }
                    float cov = Mathf.Clamp01((half - best) / aa + 0.5f);
                    px[y * size + x] = new Color32(cr, cg, cb, (byte)(cov * 255f));
                }
            }

            var t = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
            };
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        static float DistSeg(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a, ap = p - a;
            float t = Mathf.Clamp01(Vector2.Dot(ap, ab) / Mathf.Max(Vector2.Dot(ab, ab), 1e-5f));
            return Vector2.Distance(p, a + ab * t);
        }
    }
}
