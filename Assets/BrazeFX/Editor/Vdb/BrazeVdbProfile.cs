using System;
using System.Collections.Generic;
using System.IO;

namespace BrazeFX.EditorTools.Vdb
{
    /// <summary>The deliberately narrow, cross-DCC OpenVDB contract owned by BrazeFX.</summary>
    internal static class BrazeVdbProfile
    {
        public const ulong Magic = 0x0000000056444220UL;
        public const uint WriterFileVersion = 224;
        public const uint WriterLibraryMajor = 12;
        public const uint WriterLibraryMinor = 0;
        public const uint NativeHalfVectorFileVersion = 225;

        public const uint CompressionNone = 0;
        public const uint CompressionZip = 1;
        public const uint CompressionActiveMask = 2;
        public const uint CompressionBlosc = 4;

        public const string FloatTreeType = "Tree_float_5_4_3";
        public const string VectorTreeType = "Tree_vec3s_5_4_3";
        public const string HalfVectorTreeType = "Tree_vec3s_5_4_3_HalfFloat";
        public const int ProductGridLimit = 4;

        static readonly HashSet<string> GridNames = new(StringComparer.Ordinal)
        {
            "density", "emission", "temperature", "vel"
        };

        public static bool IsProductGridName(string name) => name != null && GridNames.Contains(name);

        public static bool IsSupportedReaderType(uint fileVersion, string treeType)
        {
            if (fileVersion == WriterFileVersion)
                return treeType == FloatTreeType || treeType == VectorTreeType;
            if (fileVersion == NativeHalfVectorFileVersion)
                return treeType == FloatTreeType || treeType == VectorTreeType || treeType == HalfVectorTreeType;
            return false;
        }

        public static void ValidateCanonicalWriterType(string gridName, string treeType)
        {
            if (!IsProductGridName(gridName))
                throw new InvalidDataException($"Grid '{gridName}' is outside the BrazeFX OpenVDB profile.");
            string expected = gridName == "vel" ? VectorTreeType : FloatTreeType;
            if (!string.Equals(treeType, expected, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Grid '{gridName}' must use canonical tree type '{expected}', not '{treeType}'.");
        }
    }
}
