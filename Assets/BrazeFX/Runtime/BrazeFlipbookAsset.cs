using System;
using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>Authored flipbook delivery asset: one or more view atlases plus the timing, layout, bounds, and colour
    /// metadata needed for deterministic playback.</summary>
    [CreateAssetMenu(fileName = "Braze Flipbook", menuName = "BrazeFX/Flipbook Asset", order = 52)]
    public sealed class BrazeFlipbookAsset : ScriptableObject
    {
        public enum ViewDirection { Front, Back, Left, Right, Top, Bottom, Isometric }

        [Serializable]
        public sealed class View
        {
            public string name;
            public ViewDirection direction;
            public Texture2D texture;
            [Tooltip("World-space width and height represented by the complete atlas cell.")]
            public Vector2 frameWorldSize;
        }

        public int version = 1;
        [Min(1)] public int frameCount = 1;
        [Min(0.01f)] public float fps = 30f;
        [Min(1)] public int columns = 1;
        [Min(1)] public int rows = 1;
        public Vector2Int frameResolution = new(512, 512);
        public Vector3 worldSize = Vector3.one;
        [Tooltip("Local-space centre shared by every frame. Auto Bounds derives this from clip-wide occupancy.")]
        public Vector3 boundsCenter;
        public Vector3 pivot;
        public bool linear = true;
        public bool hdr;
        public bool premultipliedAlpha = true;
        public bool looping;
        [Header("Provenance")]
        public string sourceFingerprint;
        public string createdUtc;
        public string projection;
        public List<View> views = new();

        public bool IsValid => frameCount > 0 && fps > 0f && columns > 0 && rows > 0 &&
                               views != null && views.Count > 0 && views[0]?.texture != null;

        public View GetView(int index)
            => views != null && views.Count > 0 ? views[Mathf.Clamp(index, 0, views.Count - 1)] : null;
    }
}
