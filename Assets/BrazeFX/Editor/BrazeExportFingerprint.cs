using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Deterministic fingerprints for cache provenance. Serialized properties are walked explicitly so
    /// object references are represented by stable GlobalObjectIds instead of transient instance IDs.</summary>
    internal static class BrazeExportFingerprint
    {
        public static string SourceFingerprint(BrazeExportSession session)
        {
            if (session == null || session.SourceContainer == null) return string.Empty;
            var text = new StringBuilder(16384);
            BrazeContainer container = session.SourceContainer;
            text.Append("BrazeFXSource:v5\n")
                .Append(session.SourceContainerGlobalId).Append('\n')
                .Append(session.duration.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(session.BakeFps).Append('|').Append((int)session.storage).Append('|')
                .Append((int)session.cachePurpose).Append('|')
                .Append(BrazeCacheBaker.DefaultBakeCull.ToString("R", CultureInfo.InvariantCulture)).Append('\n');

            string scenePath = container.gameObject.scene.path;
            text.Append(scenePath).Append('\n');
            if (!string.IsNullOrEmpty(scenePath)) text.Append(AssetDatabase.GetAssetDependencyHash(scenePath)).Append('\n');
            Component[] components = container.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++) AppendComponent(text, components[i], i);
            return Hash(text.ToString());
        }

        public static string CacheFingerprint(BrazeFluidCacheAsset cache)
        {
            if (cache == null) return string.Empty;
            var text = new StringBuilder(512);
            text.Append("BrazeFXCache:v1|").Append(cache.version).Append('|')
                .Append(cache.baseRes.x).Append(',').Append(cache.baseRes.y).Append(',').Append(cache.baseRes.z).Append('|')
                .Append(cache.warpRes.x).Append(',').Append(cache.warpRes.y).Append(',').Append(cache.warpRes.z).Append('|')
                .Append(cache.brickSize).Append('|').Append(cache.frameCount).Append('|')
                .Append(cache.fps.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(cache.worldSizeAtBake.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(cache.worldSizeAtBake.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(cache.worldSizeAtBake.z.ToString("R", CultureInfo.InvariantCulture)).Append('|')
                .Append(cache.buildFingerprint).Append('|').Append(cache.binaryBytes).Append('|').Append(cache.binaryFile);
            string path = BrazeExportCachePreview.ResolveBinaryPath(cache);
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var info = new FileInfo(path);
                text.Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
            }
            return Hash(text.ToString());
        }

        static void AppendComponent(StringBuilder text, Component component, int order)
        {
            text.Append("component:").Append(order).Append('|');
            if (component == null) { text.Append("missing\n"); return; }
            text.Append(component.GetType().AssemblyQualifiedName).Append('|');
            try { text.Append(GlobalObjectId.GetGlobalObjectIdSlow(component)); } catch { text.Append(component.name); }
            text.Append('\n');
            var serialized = new SerializedObject(component);
            SerializedProperty property = serialized.GetIterator();
            bool enterChildren = true;
            while (property.Next(enterChildren))
            {
                enterChildren = true;
                if (property.propertyPath == "m_Script") continue;
                if (property.propertyType == SerializedPropertyType.Generic) continue;
                text.Append(property.propertyPath).Append('=').Append(Value(property)).Append('\n');
            }
        }

        static string Value(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.longValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return p.boolValue ? "1" : "0";
                case SerializedPropertyType.Float: return p.doubleValue.ToString("R", CultureInfo.InvariantCulture);
                case SerializedPropertyType.String: return p.stringValue ?? string.Empty;
                case SerializedPropertyType.Color: return p.colorValue.ToString("R");
                case SerializedPropertyType.ObjectReference:
                    if (p.objectReferenceValue == null) return "null";
                    try { return GlobalObjectId.GetGlobalObjectIdSlow(p.objectReferenceValue).ToString(); }
                    catch { return p.objectReferenceValue.name; }
                case SerializedPropertyType.LayerMask: return p.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Enum: return p.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Vector2: return p.vector2Value.ToString("R");
                case SerializedPropertyType.Vector3: return p.vector3Value.ToString("R");
                case SerializedPropertyType.Vector4: return p.vector4Value.ToString("R");
                case SerializedPropertyType.Rect: return p.rectValue.ToString();
                case SerializedPropertyType.ArraySize: return p.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Character: return p.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.AnimationCurve:
                    var curve = p.animationCurveValue;
                    var curveText = new StringBuilder().Append(curve.preWrapMode).Append('|')
                        .Append(curve.postWrapMode).Append('|');
                    foreach (Keyframe key in curve.keys)
                        curveText.Append(key.time.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                            .Append(key.value.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                            .Append(key.inTangent.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                            .Append(key.outTangent.ToString("R", CultureInfo.InvariantCulture)).Append(';');
                    return curveText.ToString();
                case SerializedPropertyType.Gradient:
                    Gradient gradient = p.gradientValue;
                    var gradientText = new StringBuilder().Append(gradient.mode).Append('|');
                    foreach (GradientColorKey key in gradient.colorKeys)
                        gradientText.Append(key.time.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                            .Append(key.color.ToString("R")).Append(';');
                    gradientText.Append('|');
                    foreach (GradientAlphaKey key in gradient.alphaKeys)
                        gradientText.Append(key.time.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                            .Append(key.alpha.ToString("R", CultureInfo.InvariantCulture)).Append(';');
                    return gradientText.ToString();
                case SerializedPropertyType.Bounds: return p.boundsValue.ToString();
                case SerializedPropertyType.Quaternion: return p.quaternionValue.ToString("R");
                case SerializedPropertyType.ExposedReference:
                    UnityEngine.Object value = p.exposedReferenceValue;
                    return value == null ? "null" : GlobalObjectId.GetGlobalObjectIdSlow(value).ToString();
                case SerializedPropertyType.Vector2Int: return p.vector2IntValue.ToString();
                case SerializedPropertyType.Vector3Int: return p.vector3IntValue.ToString();
                case SerializedPropertyType.RectInt: return p.rectIntValue.ToString();
                case SerializedPropertyType.BoundsInt: return p.boundsIntValue.ToString();
                case SerializedPropertyType.ManagedReference: return p.managedReferenceFullTypename ?? string.Empty;
                case SerializedPropertyType.Hash128: return p.hash128Value.ToString();
                default: return p.type ?? string.Empty;
            }
        }

        static string Hash(string value)
        {
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
            var result = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++) result.Append(hash[i].ToString("x2"));
            return result.ToString();
        }
    }
}
