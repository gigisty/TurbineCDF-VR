using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Serialization;

namespace BrazeFX
{
    /// <summary>
    /// Overrides Timeline metadata for a public authoring field. Public value fields are discovered automatically;
    /// use this attribute when a field needs a curated label/range or when an explicitly serialized property should
    /// be exposed. A slash in Label creates a parameter-browser section.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public sealed class BrazeAnimatableAttribute : Attribute
    {
        public readonly string Label;
        public readonly float Min, Max;
        public readonly bool HasRange;

        public BrazeAnimatableAttribute() { }
        public BrazeAnimatableAttribute(string label) { Label = label; }
        public BrazeAnimatableAttribute(string label, float min, float max)
        {
            Label = label; Min = min; Max = max; HasRange = true;
        }
        public BrazeAnimatableAttribute(float min, float max) { Min = min; Max = max; HasRange = true; }
    }

    /// <summary>Explicitly removes a public value field from Timeline discovery.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public sealed class BrazeNotAnimatableAttribute : Attribute { }

    /// <summary>
    /// Makes a component's Timeline picker opt-in: only members carrying <see cref="BrazeAnimatableAttribute"/>
    /// are offered for new tracks. Existing bindings remain resolvable, so enabling this on an established
    /// component does not invalidate previously-authored Timeline data.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
    public sealed class BrazeTimelineCuratedAttribute : Attribute { }

    /// <summary>Marks a safe, parameterless public API action as available to Timeline event tracks.</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
    public sealed class BrazeTimelineEventAttribute : Attribute
    {
        public readonly string Label;
        public BrazeTimelineEventAttribute(string label = null) { Label = label; }
    }

    public enum BrazeAnimationValueKind { Float, Integer, Boolean, Enum }

    /// <summary>Optional instance filter for mode-dependent parameters.</summary>
    public interface IBrazeAnimatableFilter
    {
        bool IsAnimatableMemberRelevant(string member);
    }

    /// <summary>
    /// Optional instance-aware presentation source shared by custom Inspectors and Timeline. It supplies the exact
    /// hierarchy and leaf label for a member when the visible Inspector structure depends on component state.
    /// </summary>
    public interface IBrazeAnimatablePresentation
    {
        string GetAnimatableMemberPath(string member, string fallbackPath);
    }

    /// <summary>
    /// Optional apply hook for components which cache or bake their public authoring state. Timeline calls it once
    /// after all changed channels on a component have been written, including when preview values are restored.
    /// </summary>
    public interface IBrazeAnimatedTarget
    {
        void OnBrazeAnimated();
    }

    /// <summary>A single scalar Timeline channel. Vectors, integer vectors and colours expand into related channels.</summary>
    public sealed class BrazeAnimatableField
    {
        public string Id { get; private set; }
        public string Member { get; private set; }
        public string Label { get; private set; }
        public string ShortLabel { get; private set; }
        public float Min { get; private set; }
        public float Max { get; private set; }
        public BrazeAnimationValueKind Kind { get; private set; }
        public Type EnumType { get; private set; }
        public bool IsExplicit { get; private set; }
        public bool IsInt => Kind == BrazeAnimationValueKind.Integer || Kind == BrazeAnimationValueKind.Enum;
        public bool IsDiscrete => Kind != BrazeAnimationValueKind.Float;

        readonly FieldInfo[] path;
        readonly PropertyInfo property;
        readonly int channel;
        readonly HashSet<string> aliases;
        readonly Func<Component, float> getDel;
        readonly Action<Component, float> setDel;

        internal BrazeAnimatableField(string id, string member, string label, string shortLabel,
            float min, float max, BrazeAnimationValueKind kind, Type enumType, FieldInfo[] path,
            PropertyInfo property, int channel, IEnumerable<string> aliases, bool isExplicit,
            Func<Component, float> get = null, Action<Component, float> set = null)
        {
            Id = id;
            Member = member;
            Label = label;
            ShortLabel = shortLabel;
            Min = min;
            Max = max;
            Kind = kind;
            EnumType = enumType;
            IsExplicit = isExplicit;
            this.path = path;
            this.property = property;
            this.channel = channel;
            this.aliases = aliases != null ? new HashSet<string>(aliases) : new HashSet<string>();
            getDel = get;
            setDel = set;
        }

        public bool Matches(string idOrMember)
        {
            if (string.IsNullOrEmpty(idOrMember)) return false;
            return idOrMember == Id || idOrMember == Member || aliases.Contains(idOrMember);
        }

        public float Get(Component component)
        {
            if (getDel != null) return getDel(component);
            object value = ReadLeaf(component);
            return ReadChannel(value, channel);
        }

        public void Set(Component component, float value)
        {
            value = Coerce(value);
            if (setDel != null) { setDel(component, value); return; }

            if (property != null)
            {
                property.SetValue(component, ConvertScalar(value, property.PropertyType));
                return;
            }

            object[] owners = new object[path.Length];
            owners[0] = component;
            for (int i = 0; i < path.Length - 1; i++)
            {
                object child = path[i].GetValue(owners[i]);
                if (child == null)
                {
                    Type childType = path[i].FieldType;
                    if (childType.IsAbstract || childType.IsInterface) return;
                    child = Activator.CreateInstance(childType);
                    path[i].SetValue(owners[i], child);
                }
                owners[i + 1] = child;
            }

            int leafIndex = path.Length - 1;
            FieldInfo leaf = path[leafIndex];
            object changed = WriteChannel(leaf.GetValue(owners[leafIndex]), leaf.FieldType, channel, value);
            leaf.SetValue(owners[leafIndex], changed);

            // Propagate boxed structs back through their owners. Reassigning reference-type children is harmless and
            // keeps this path correct for arbitrary serializable class/struct combinations.
            for (int i = path.Length - 2; i >= 0; i--)
                path[i].SetValue(owners[i], owners[i + 1]);
        }

        public float Evaluate(AnimationCurve curve, float time)
        {
            if (curve == null || curve.length == 0) return 0f;
            if (!IsDiscrete) return curve.Evaluate(time);
            if (time <= curve[0].time) return Coerce(curve[0].value);
            for (int i = curve.length - 1; i >= 0; i--)
                if (time >= curve[i].time) return Coerce(curve[i].value);
            return Coerce(curve[0].value);
        }

        public float Coerce(float value)
        {
            if (Kind == BrazeAnimationValueKind.Boolean) return value >= 0.5f ? 1f : 0f;
            if (Kind == BrazeAnimationValueKind.Integer || Kind == BrazeAnimationValueKind.Enum)
                return Mathf.Clamp(Mathf.Round(value), Mathf.Min(Min, Max), Mathf.Max(Min, Max));
            return value;
        }

        object ReadLeaf(Component component)
        {
            if (property != null) return property.GetValue(component);
            object owner = component;
            for (int i = 0; i < path.Length; i++)
            {
                if (owner == null) return 0f;
                owner = path[i].GetValue(owner);
            }
            return owner;
        }

        static float ReadChannel(object value, int channel)
        {
            switch (value)
            {
                case float f: return f;
                case int i: return i;
                case bool b: return b ? 1f : 0f;
                case Enum e: return Convert.ToSingle(e);
                case Vector2 v: return v[channel];
                case Vector3 v: return v[channel];
                case Vector4 v: return v[channel];
                case Vector2Int v: return v[channel];
                case Vector3Int v: return v[channel];
                case Color c: return channel == 0 ? c.r : channel == 1 ? c.g : channel == 2 ? c.b : c.a;
                default: return 0f;
            }
        }

        static object WriteChannel(object current, Type type, int channel, float value)
        {
            if (type == typeof(float)) return value;
            if (type == typeof(int)) return Mathf.RoundToInt(value);
            if (type == typeof(bool)) return value >= 0.5f;
            if (type.IsEnum) return Enum.ToObject(type, Mathf.RoundToInt(value));
            if (type == typeof(Vector2)) { var v = (Vector2)current; v[channel] = value; return v; }
            if (type == typeof(Vector3)) { var v = (Vector3)current; v[channel] = value; return v; }
            if (type == typeof(Vector4)) { var v = (Vector4)current; v[channel] = value; return v; }
            if (type == typeof(Vector2Int)) { var v = (Vector2Int)current; v[channel] = Mathf.RoundToInt(value); return v; }
            if (type == typeof(Vector3Int)) { var v = (Vector3Int)current; v[channel] = Mathf.RoundToInt(value); return v; }
            if (type == typeof(Color))
            {
                var c = (Color)current;
                if (channel == 0) c.r = value; else if (channel == 1) c.g = value;
                else if (channel == 2) c.b = value; else c.a = value;
                return c;
            }
            return current;
        }

        static object ConvertScalar(float value, Type type)
        {
            if (type == typeof(float)) return value;
            if (type == typeof(int)) return Mathf.RoundToInt(value);
            if (type == typeof(bool)) return value >= 0.5f;
            if (type.IsEnum) return Enum.ToObject(type, Mathf.RoundToInt(value));
            return value;
        }
    }

    public sealed class BrazeTimelineEventDescriptor
    {
        public string Id { get; internal set; }
        public string Method { get; internal set; }
        public string Label { get; internal set; }
        internal MethodInfo Info;

        public void Invoke(Component component)
        {
            if (component == null || Info == null) return;
            try { Info.Invoke(component, null); }
            catch (Exception exception) { Debug.LogException(exception, component); }
        }

        public bool Matches(string idOrMethod) => idOrMethod == Id || idOrMethod == Method;
    }

    public static class BrazeTimelineEventRegistry
    {
        static readonly Dictionary<Type, List<BrazeTimelineEventDescriptor>> cache = new();

        public static IReadOnlyList<BrazeTimelineEventDescriptor> Events(Component component)
        {
            if (component == null) return Array.Empty<BrazeTimelineEventDescriptor>();
            Type type = component.GetType();
            if (cache.TryGetValue(type, out var existing)) return existing;
            var result = new List<BrazeTimelineEventDescriptor>();
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            foreach (MethodInfo method in methods)
            {
                var attr = method.GetCustomAttribute<BrazeTimelineEventAttribute>();
                if (attr == null || method.ReturnType != typeof(void) || method.GetParameters().Length != 0) continue;
                result.Add(new BrazeTimelineEventDescriptor
                {
                    Id = (type.FullName ?? type.Name) + ":event:" + method.Name,
                    Method = method.Name,
                    Label = string.IsNullOrEmpty(attr.Label) ? method.Name : attr.Label,
                    Info = method,
                });
            }
            cache[type] = result;
            return result;
        }

        public static BrazeTimelineEventDescriptor Find(Component component, string idOrMethod)
        {
            if (component == null || string.IsNullOrEmpty(idOrMethod)) return null;
            var events = Events(component);
            for (int i = 0; i < events.Count; i++) if (events[i].Matches(idOrMethod)) return events[i];
            return null;
        }
    }

    /// <summary>
    /// Cached typed discovery for Braze Timeline. Explicitly annotated fields keep their curated metadata; other
    /// public, serialized-safe value fields are exposed automatically. Serializable settings objects are flattened
    /// to two levels so components such as cache/flipbook players expose their authoring API without bespoke code.
    /// </summary>
    public static class BrazeAnimatableRegistry
    {
        static readonly Dictionary<Type, List<BrazeAnimatableField>> cache = new();
        static List<BrazeAnimatableField> transformFields;
        static readonly HashSet<string> DefaultDeniedNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "showGizmo", "renderInSceneView", "editorPreview", "preview", "previewLength"
        };

        public static IReadOnlyList<BrazeAnimatableField> Fields(Type type)
        {
            if (type == null) return Array.Empty<BrazeAnimatableField>();
            if (typeof(Transform).IsAssignableFrom(type)) return TransformFields();
            if (cache.TryGetValue(type, out var cached)) return cached;

            var result = new List<BrazeAnimatableField>();
            var hierarchy = new List<Type>();
            for (Type current = type; current != null && current != typeof(MonoBehaviour); current = current.BaseType)
                hierarchy.Add(current);
            hierarchy.Reverse();

            foreach (Type current in hierarchy)
            {
                string section = null;
                var fields = current.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                Array.Sort(fields, (a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
                foreach (FieldInfo field in fields)
                {
                    var header = field.GetCustomAttribute<HeaderAttribute>();
                    if (header != null) section = header.header;
                    AddField(result, type, new[] { field }, field.Name, section, 0);
                }

                var properties = current.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                foreach (PropertyInfo property in properties)
                {
                    var attr = property.GetCustomAttribute<BrazeAnimatableAttribute>();
                    if (attr == null || property.GetCustomAttribute<BrazeNotAnimatableAttribute>() != null ||
                        !property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0) continue;
                    AddProperty(result, type, property, attr);
                }
            }

            cache[type] = result;
            return result;
        }

        public static IReadOnlyList<BrazeAnimatableField> Fields(Component component) =>
            Fields(component == null ? null : component.GetType());

        /// <summary>
        /// Fields suitable for new tracks on this particular component instance. Curated components expose only
        /// explicitly annotated parameters, and instance filters remove controls that do not apply to the current
        /// mode. Resolution deliberately continues to use <see cref="Fields(Type)"/> so old tracks stay valid.
        /// </summary>
        public static IReadOnlyList<BrazeAnimatableField> VisibleFields(Component component)
        {
            if (component == null) return Array.Empty<BrazeAnimatableField>();
            var fields = Fields(component.GetType());
            bool explicitOnly = component.GetType().GetCustomAttribute<BrazeTimelineCuratedAttribute>() != null;
            var filter = component as IBrazeAnimatableFilter;
            if (!explicitOnly && filter == null) return fields;

            var result = new List<BrazeAnimatableField>(fields.Count);
            for (int i = 0; i < fields.Count; i++)
            {
                BrazeAnimatableField field = fields[i];
                if (explicitOnly && !field.IsExplicit) continue;
                if (filter != null && !filter.IsAnimatableMemberRelevant(field.Member)) continue;
                result.Add(field);
            }
            return result;
        }

        public static BrazeAnimatableField Find(Component component, string idOrMember)
        {
            if (component == null || string.IsNullOrEmpty(idOrMember)) return null;
            var fields = Fields(component.GetType());
            for (int i = 0; i < fields.Count; i++) if (fields[i].Matches(idOrMember)) return fields[i];
            return null;
        }

        public static BrazeAnimatableField Find(Component component, string id, string legacyMember)
        {
            var field = Find(component, id);
            return field ?? Find(component, legacyMember);
        }

        public static string DisplayPath(Component component, BrazeAnimatableField field)
        {
            if (field == null) return string.Empty;
            string path = field.Label;
            if (component is IBrazeAnimatablePresentation presentation)
            {
                string presented = presentation.GetAnimatableMemberPath(field.Member, path);
                if (!string.IsNullOrWhiteSpace(presented)) path = presented;
            }
            return path;
        }

        public static string DisplayPath(Component component, string member, string fallback = null)
        {
            if (component == null || string.IsNullOrEmpty(member)) return fallback ?? string.Empty;
            var fields = Fields(component);
            BrazeAnimatableField found = null;
            for (int i = 0; i < fields.Count; i++)
            {
                if (fields[i].Member == member) { found = fields[i]; break; }
                if (found == null && fields[i].Member.StartsWith(member + ".", StringComparison.Ordinal)) found = fields[i];
            }
            string path = found != null ? DisplayPath(component, found) :
                component is IBrazeAnimatablePresentation presentation
                    ? presentation.GetAnimatableMemberPath(member, fallback ?? Prettify(member))
                    : fallback ?? Prettify(member);
            // A representative vector channel resolves to .../X; the Inspector needs its parent property label.
            int slash = path.LastIndexOf('/');
            string leaf = slash >= 0 ? path.Substring(slash + 1) : path;
            if (found != null && found.Member != member && IsChannelLabel(leaf)) path = path.Substring(0, slash);
            return path;
        }

        public static string DisplayName(Component component, string member, string fallback = null)
        {
            string path = DisplayPath(component, member, fallback);
            int slash = path.LastIndexOf('/');
            string leaf = slash >= 0 ? path.Substring(slash + 1) : path;
            if (string.IsNullOrEmpty(leaf) && slash > 0)
            {
                string parent = path.Substring(0, slash);
                int parentSlash = parent.LastIndexOf('/');
                leaf = parentSlash >= 0 ? parent.Substring(parentSlash + 1) : parent;
            }
            return leaf;
        }

        public static bool IsAnimatable(Component component) => component != null && VisibleFields(component).Count > 0;
        public static void ClearCache() { cache.Clear(); transformFields = null; }

        static void AddField(List<BrazeAnimatableField> result, Type componentType, FieldInfo[] path,
            string memberPath, string section, int depth)
        {
            FieldInfo leaf = path[path.Length - 1];
            var explicitAttr = leaf.GetCustomAttribute<BrazeAnimatableAttribute>();
            if (leaf.IsStatic || leaf.IsLiteral || leaf.IsInitOnly || leaf.IsNotSerialized ||
                leaf.GetCustomAttribute<HideInInspector>() != null || leaf.GetCustomAttribute<BrazeNotAnimatableAttribute>() != null)
                return;
            if (explicitAttr == null && DefaultDeniedNames.Contains(leaf.Name)) return;

            Type valueType = leaf.FieldType;
            int channels = ChannelCount(valueType);
            if (channels == 0)
            {
                if (depth >= 2 || !CanRecurse(valueType)) return;
                string childSection = Join(section, Prettify(leaf.Name));
                var children = valueType.GetFields(BindingFlags.Public | BindingFlags.Instance);
                Array.Sort(children, (a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
                string nestedHeader = null;
                foreach (FieldInfo child in children)
                {
                    var header = child.GetCustomAttribute<HeaderAttribute>();
                    if (header != null) nestedHeader = header.header;
                    var childPath = new FieldInfo[path.Length + 1];
                    Array.Copy(path, childPath, path.Length);
                    childPath[path.Length] = child;
                    AddField(result, componentType, childPath, memberPath + "." + child.Name,
                        Join(childSection, nestedHeader), depth + 1);
                }
                return;
            }

            ResolveRange(leaf, explicitAttr, valueType, out float min, out float max);
            BrazeAnimationValueKind kind = KindFor(valueType);
            string baseLabel = explicitAttr != null && !string.IsNullOrEmpty(explicitAttr.Label)
                ? explicitAttr.Label
                : Join(section, Prettify(leaf.Name));
            string[] suffixes = ChannelSuffixes(valueType);
            var formerNames = leaf.GetCustomAttributes<FormerlySerializedAsAttribute>();

            for (int c = 0; c < channels; c++)
            {
                string suffix = suffixes[c];
                string member = memberPath + suffix;
                string label = baseLabel + (channels > 1 ? "/" + ChannelLabel(suffix) : string.Empty);
                string shortLabel = LastPath(label);
                string id = StableId(componentType, member);
                var aliases = new List<string>();
                foreach (var former in formerNames)
                {
                    string oldPath = ReplaceLeaf(memberPath, former.oldName) + suffix;
                    aliases.Add(oldPath);
                    aliases.Add(StableId(componentType, oldPath));
                }
                result.Add(new BrazeAnimatableField(id, member, label, shortLabel, min, max, kind,
                    valueType.IsEnum ? valueType : null, path, null, c, aliases, explicitAttr != null));
            }
        }

        static void AddProperty(List<BrazeAnimatableField> result, Type componentType, PropertyInfo property,
            BrazeAnimatableAttribute attr)
        {
            Type type = property.PropertyType;
            if (ChannelCount(type) != 1) return;
            ResolveRange(null, attr, type, out float min, out float max);
            string label = string.IsNullOrEmpty(attr.Label) ? Prettify(property.Name) : attr.Label;
            result.Add(new BrazeAnimatableField(StableId(componentType, property.Name), property.Name, label,
                LastPath(label), min, max, KindFor(type), type.IsEnum ? type : null, null, property, 0, null, true));
        }

        static IReadOnlyList<BrazeAnimatableField> TransformFields()
        {
            if (transformFields != null) return transformFields;
            transformFields = new List<BrazeAnimatableField>(9);
            AddTransform("m_LocalPosition", "Position", "Pos", -10f, 10f,
                t => t.localPosition, (t, v) => t.localPosition = v);
            AddTransform("localEulerAnglesRaw", "Rotation", "Rot", -180f, 180f,
                t => t.localEulerAngles, (t, v) => t.localEulerAngles = v);
            AddTransform("m_LocalScale", "Scale", "Scale", -3f, 3f,
                t => t.localScale, (t, v) => t.localScale = v);
            return transformFields;
        }

        static void AddTransform(string prefix, string group, string shortPrefix, float min, float max,
            Func<Transform, Vector3> get, Action<Transform, Vector3> set)
        {
            for (int i = 0; i < 3; i++)
            {
                int index = i;
                string axis = i == 0 ? "x" : i == 1 ? "y" : "z";
                string member = prefix + "." + axis;
                transformFields.Add(new BrazeAnimatableField(StableId(typeof(Transform), member), member,
                    group + "/" + axis.ToUpperInvariant(), shortPrefix + " " + axis.ToUpperInvariant(), min, max,
                    BrazeAnimationValueKind.Float, null, null, null, i, null, true,
                    c => get((Transform)c)[index],
                    (c, value) => { var t = (Transform)c; Vector3 v = get(t); v[index] = value; set(t, v); }));
            }
        }

        static int ChannelCount(Type type)
        {
            if (type == typeof(float) || type == typeof(int) || type == typeof(bool) || type.IsEnum) return 1;
            if (type == typeof(Vector2) || type == typeof(Vector2Int)) return 2;
            if (type == typeof(Vector3) || type == typeof(Vector3Int)) return 3;
            if (type == typeof(Vector4) || type == typeof(Color)) return 4;
            return 0;
        }

        static BrazeAnimationValueKind KindFor(Type type)
        {
            if (type == typeof(bool)) return BrazeAnimationValueKind.Boolean;
            if (type.IsEnum) return BrazeAnimationValueKind.Enum;
            if (type == typeof(int) || type == typeof(Vector2Int) || type == typeof(Vector3Int))
                return BrazeAnimationValueKind.Integer;
            return BrazeAnimationValueKind.Float;
        }

        static string[] ChannelSuffixes(Type type)
        {
            if (ChannelCount(type) == 1) return new[] { string.Empty };
            if (type == typeof(Color)) return new[] { ".r", ".g", ".b", ".a" };
            int count = ChannelCount(type);
            return count == 2 ? new[] { ".x", ".y" } : count == 3 ? new[] { ".x", ".y", ".z" } : new[] { ".x", ".y", ".z", ".w" };
        }

        static void ResolveRange(FieldInfo field, BrazeAnimatableAttribute attr, Type type, out float min, out float max)
        {
            if (attr != null && attr.HasRange) { min = attr.Min; max = attr.Max; return; }
            var range = field?.GetCustomAttribute<RangeAttribute>();
            if (range != null) { min = range.min; max = range.max; return; }
            if (type == typeof(bool)) { min = 0f; max = 1f; return; }
            if (type.IsEnum) { min = 0f; max = Mathf.Max(0, Enum.GetValues(type).Length - 1); return; }
            bool integer = type == typeof(int) || type == typeof(Vector2Int) || type == typeof(Vector3Int);
            var minimum = field?.GetCustomAttribute<MinAttribute>();
            if (minimum != null) { min = minimum.min; max = integer ? Mathf.Max(min + 10f, 100f) : Mathf.Max(min + 1f, 10f); return; }
            if (type == typeof(Color)) { min = 0f; max = field?.GetCustomAttribute<ColorUsageAttribute>()?.hdr == true ? 8f : 1f; return; }
            min = integer ? -100f : -10f;
            max = integer ? 100f : 10f;
        }

        static bool CanRecurse(Type type)
        {
            if (type == null || type == typeof(string) || typeof(UnityEngine.Object).IsAssignableFrom(type) ||
                type.IsArray || type.IsGenericType || type == typeof(AnimationCurve) || type == typeof(Gradient)) return false;
            return type.IsSerializable && (type.Namespace == "BrazeFX" || type.DeclaringType != null);
        }

        static string StableId(Type type, string member) => (type.FullName ?? type.Name) + ":" + member;
        static string Join(string a, string b) => string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : a + "/" + b;
        static string LastPath(string label) { int slash = label.LastIndexOf('/'); return slash >= 0 ? label.Substring(slash + 1) : label; }
        static bool IsChannelLabel(string label) => label.Length == 1 && "XYZWRGBA".IndexOf(char.ToUpperInvariant(label[0])) >= 0;
        static string ChannelLabel(string suffix) => suffix.Length > 1 ? suffix.Substring(1).ToUpperInvariant() : suffix;
        static string ReplaceLeaf(string path, string leaf) { int dot = path.LastIndexOf('.'); return dot < 0 ? leaf : path.Substring(0, dot + 1) + leaf; }

        static string Prettify(string name)
        {
            var sb = new System.Text.StringBuilder(name.Length + 4);
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                if (i == 0) sb.Append(char.ToUpperInvariant(ch));
                else { if (char.IsUpper(ch) && !char.IsUpper(name[i - 1])) sb.Append(' '); sb.Append(ch); }
            }
            return sb.ToString();
        }
    }
}
