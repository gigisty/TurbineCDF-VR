using System;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Explicit Player-build dependency for shaders instantiated by BrazeFX at runtime.
    /// Unity cannot discover a build dependency from Shader.Find calls alone, so the
    /// Resources asset for this type owns references to every runtime-created shader.
    /// </summary>
    public sealed class BrazeRuntimeShaderLibrary : ScriptableObject
    {
        const string ResourcePath = "BrazeRuntimeShaderLibrary";

        [SerializeField] Shader[] shaders = Array.Empty<Shader>();

        static BrazeRuntimeShaderLibrary instance;
        static bool loadAttempted;

        public int ShaderCount => shaders != null ? shaders.Length : 0;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            instance = null;
            loadAttempted = false;
        }

        public static Shader Find(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName))
                return null;

            BrazeRuntimeShaderLibrary library = Load();
            if (library != null && library.shaders != null)
            {
                for (int i = 0; i < library.shaders.Length; i++)
                {
                    Shader shader = library.shaders[i];
                    if (shader != null && string.Equals(shader.name, shaderName, StringComparison.Ordinal))
                        return shader;
                }
            }

            // Editor source projects and deliberately minimal development fixtures may not
            // carry the library asset. Retain Shader.Find as a compatibility fallback.
            return Shader.Find(shaderName);
        }

        public bool Contains(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName) || shaders == null)
                return false;

            for (int i = 0; i < shaders.Length; i++)
            {
                Shader shader = shaders[i];
                if (shader != null && string.Equals(shader.name, shaderName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        public Shader GetShader(int index)
        {
            return shaders != null && index >= 0 && index < shaders.Length ? shaders[index] : null;
        }

        static BrazeRuntimeShaderLibrary Load()
        {
            if (!loadAttempted)
            {
                loadAttempted = true;
                instance = Resources.Load<BrazeRuntimeShaderLibrary>(ResourcePath);
                if (instance == null)
                    Debug.LogError("[BrazeFX] Runtime shader library is missing. " +
                                   "Reimport the complete BrazeFX package before making a Player build.");
            }
            return instance;
        }
    }
}
