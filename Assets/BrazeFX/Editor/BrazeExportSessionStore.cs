using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Owns the single project-local Export Studio state. It lives under Library, never appears in the
    /// Project window, and survives Editor restarts and Play Mode domain reloads.</summary>
    [InitializeOnLoad]
    internal static class BrazeExportSessionStore
    {
        const int CurrentDiskFormat = 1;
        const string LegacySessionKey = "BrazeFX.ExportStudio.LastSession";
        const string ProjectSessionLocator = "@BrazeFXProjectExportSession";
        const string RelativeStatePath = "Library/BrazeFX/ExportStudioState.json";
        // HideAndDontSave also contains NotEditable, which silently disables every SerializedProperty drawn by
        // Export Studio. Keep this project-local object hidden and out of Unity's normal save lifecycle while
        // allowing its custom window to edit it; Save() persists it explicitly under Library.
        internal const HideFlags SessionHideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;

        [Serializable]
        sealed class DiskState
        {
            public int formatVersion = CurrentDiskFormat;
            public string sessionJson;
            public string cacheGuid;
            public string derivedRegionCacheGuid;
            public string flipbookAssetGuid;
        }

        static BrazeExportSession current;

        static BrazeExportSessionStore()
        {
            AssemblyReloadEvents.beforeAssemblyReload += SaveCurrent;
            EditorApplication.quitting += SaveCurrent;
        }

        public static BrazeExportSession Current
        {
            get
            {
                if (current != null) return current;

                current = ScriptableObject.CreateInstance<BrazeExportSession>();
                bool loaded = Load(current);
                if (!loaded) MigrateLegacyAsset(current);

                current.name = "Braze Export Studio State";
                current.hideFlags = SessionHideFlags;
                current.ResolveSources();
                current.Normalize();
                Save(current);
                return current;
            }
        }

        public static void Save(BrazeExportSession session)
        {
            if (session == null) return;
            EditorUtility.SetDirty(session);
            if (ReferenceEquals(session, current))
            {
                Write(session);
                return;
            }

            if (AssetDatabase.Contains(session)) AssetDatabase.SaveAssetIfDirty(session);
        }

        public static string Locator(BrazeExportSession session)
        {
            if (session == null) return string.Empty;
            return ReferenceEquals(session, current) ? ProjectSessionLocator : AssetDatabase.GetAssetPath(session);
        }

        public static BrazeExportSession Resolve(string locator)
        {
            if (string.Equals(locator, ProjectSessionLocator, StringComparison.Ordinal)) return Current;
            return string.IsNullOrEmpty(locator) ? null : AssetDatabase.LoadAssetAtPath<BrazeExportSession>(locator);
        }

        static string StatePath
        {
            get
            {
                string projectRoot = Path.GetDirectoryName(Application.dataPath);
                return Path.GetFullPath(Path.Combine(projectRoot ?? string.Empty, RelativeStatePath));
            }
        }

        static void SaveCurrent()
        {
            if (current != null) Save(current);
        }

        static bool Load(BrazeExportSession destination)
        {
            if (!File.Exists(StatePath)) return false;

            try
            {
                DiskState state = JsonUtility.FromJson<DiskState>(File.ReadAllText(StatePath));
                if (state == null || state.formatVersion != CurrentDiskFormat || string.IsNullOrEmpty(state.sessionJson))
                    throw new InvalidDataException("The Export Studio state has an unsupported or incomplete format.");

                // Asset references are restored below from their stable GUIDs. Older format-1 files could still
                // contain EditorJsonUtility object records here; strip them before deserialization so Unity never
                // recursively transfers a referenced cache while it is transferring the session itself.
                EditorJsonUtility.FromJsonOverwrite(WithoutPersistedAssetReferences(state.sessionJson), destination);
                RestoreAsset(state.cacheGuid, ref destination.cache);
                RestoreAsset(state.derivedRegionCacheGuid, ref destination.derivedRegionCache);
                RestoreAsset(state.flipbookAssetGuid, ref destination.flipbookAsset);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"BrazeFX could not load {RelativeStatePath}; default Export Studio settings " +
                                 $"will be used and the original file will be preserved. {exception.Message}");
                return false;
            }
        }

        static void Write(BrazeExportSession session)
        {
            var state = new DiskState
            {
                sessionJson = WithoutPersistedAssetReferences(EditorJsonUtility.ToJson(session)),
                cacheGuid = AssetGuid(session.cache),
                derivedRegionCacheGuid = AssetGuid(session.derivedRegionCache),
                flipbookAssetGuid = AssetGuid(session.flipbookAsset)
            };

            string directory = Path.GetDirectoryName(StatePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(StatePath, JsonUtility.ToJson(state, true));
        }

        internal static string WithoutPersistedAssetReferences(string json)
        {
            json = ReplaceObjectReference(json, nameof(BrazeExportSession.cache));
            json = ReplaceObjectReference(json, nameof(BrazeExportSession.derivedRegionCache));
            return ReplaceObjectReference(json, nameof(BrazeExportSession.flipbookAsset));
        }

        static string ReplaceObjectReference(string json, string propertyName)
        {
            if (string.IsNullOrEmpty(json)) return json;

            string property = "\"" + propertyName + "\"";
            int searchFrom = 0;
            while (searchFrom < json.Length)
            {
                int propertyStart = json.IndexOf(property, searchFrom, StringComparison.Ordinal);
                if (propertyStart < 0) break;

                int cursor = propertyStart + property.Length;
                while (cursor < json.Length && char.IsWhiteSpace(json[cursor])) cursor++;
                if (cursor >= json.Length || json[cursor] != ':')
                {
                    searchFrom = cursor;
                    continue;
                }

                cursor++;
                while (cursor < json.Length && char.IsWhiteSpace(json[cursor])) cursor++;
                if (cursor >= json.Length || json[cursor] != '{')
                {
                    searchFrom = cursor;
                    continue;
                }

                int valueStart = cursor;
                int depth = 0;
                bool inString = false;
                bool escaped = false;
                for (; cursor < json.Length; cursor++)
                {
                    char current = json[cursor];
                    if (inString)
                    {
                        if (escaped) escaped = false;
                        else if (current == '\\') escaped = true;
                        else if (current == '"') inString = false;
                        continue;
                    }

                    if (current == '"') inString = true;
                    else if (current == '{') depth++;
                    else if (current == '}' && --depth == 0) break;
                }

                if (cursor >= json.Length) break;
                const string emptyReference = "{\"instanceID\":0}";
                json = json.Substring(0, valueStart) + emptyReference + json.Substring(cursor + 1);
                searchFrom = valueStart + emptyReference.Length;
            }

            return json;
        }

        static string AssetGuid(UnityEngine.Object value)
        {
            return value != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(value, out string guid, out long _)
                ? guid
                : string.Empty;
        }

        static void RestoreAsset<T>(string guid, ref T destination) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(guid)) return;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            destination = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<T>(path);
        }

        static void MigrateLegacyAsset(BrazeExportSession destination)
        {
            string guid = EditorPrefs.GetString(LegacySessionKey, string.Empty);
            EditorPrefs.DeleteKey(LegacySessionKey);
            if (string.IsNullOrEmpty(guid)) return;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            BrazeExportSession legacy = AssetDatabase.LoadAssetAtPath<BrazeExportSession>(path);
            if (legacy != null) EditorUtility.CopySerialized(legacy, destination);
        }
    }
}
