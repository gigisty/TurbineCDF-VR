using System;
using System.Diagnostics;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>Finds one stable local-space bound for every selected cache frame. Sparse base-brick occupancy is
    /// unioned across the sequence, so empty container space is removed without the per-frame recentering that erases
    /// authored translation. One brick of safety margin covers filtering and Render Detail displacement.</summary>
    internal sealed class BrazeFlipbookSequenceBounds : IDisposable
    {
        const int SafetyBrickMargin = 1;
        readonly BrazeFluidCacheFormat.Reader reader;
        readonly int[] sourceFrames;
        readonly int[] frameBrickCounts;
        readonly uint[] indices;
        uint[] blocksScratch;
        byte[] decodeScratch;
        Vector3Int minimumVoxel;
        Vector3Int maximumVoxel;
        int cursor;
        bool anyBaseBrick;

        internal BrazeFlipbookSequenceBounds(string cachePath, int[] selectedFrames)
        {
            if (string.IsNullOrEmpty(cachePath)) throw new ArgumentException("Cache path is required.", nameof(cachePath));
            if (selectedFrames == null || selectedFrames.Length == 0)
                throw new ArgumentException("At least one source frame is required.", nameof(selectedFrames));

            reader = new BrazeFluidCacheFormat.Reader(cachePath);
            try
            {
                sourceFrames = (int[])selectedFrames.Clone();
                frameBrickCounts = reader.ReadFrameBrickCounts();
                int maximumBricks = 0;
                for (int i = 0; i < sourceFrames.Length; i++)
                {
                    int frame = sourceFrames[i];
                    if (frame < 0 || frame >= frameBrickCounts.Length)
                        throw new ArgumentOutOfRangeException(nameof(selectedFrames), frame,
                            $"Selected cache frame {frame} is outside 0–{frameBrickCounts.Length - 1}.");
                    maximumBricks = Mathf.Max(maximumBricks, frameBrickCounts[frame]);
                }
                indices = new uint[Mathf.Max(1, maximumBricks)];
                minimumVoxel = reader.header.baseRes;
                maximumVoxel = Vector3Int.zero;
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }

        internal float Progress => sourceFrames.Length > 0 ? cursor / (float)sourceFrames.Length : 1f;
        internal bool Complete => cursor >= sourceFrames.Length;

        /// <summary>Process as many lightweight index planes as fit inside a small Editor-update budget.</summary>
        internal bool Step(double budgetMilliseconds = 6d)
        {
            if (Complete) return true;
            var timer = Stopwatch.StartNew();
            do
            {
                ProcessFrame(sourceFrames[cursor++]);
            }
            while (!Complete && timer.Elapsed.TotalMilliseconds < Math.Max(0.25d, budgetMilliseconds));
            return Complete;
        }

        void ProcessFrame(int frame)
        {
            int count = reader.ReadFrameBrickIndicesInto(frame, indices, ref blocksScratch, ref decodeScratch);
            Vector3Int grid = reader.header.BrickGrid;
            long baseBrickCount = (long)grid.x * grid.y * grid.z;
            int brickSize = reader.header.brickSize;
            for (int i = 0; i < count; i++)
            {
                uint encoded = indices[i];
                if ((encoded & BrazeFluidCacheFormat.WarpBrickBit) != 0 || encoded >= baseBrickCount) continue;
                int linear = (int)encoded;
                int z = linear / (grid.x * grid.y);
                int remainder = linear - z * grid.x * grid.y;
                int y = remainder / grid.x;
                int x = remainder - y * grid.x;
                Vector3Int voxelMin = new(x * brickSize, y * brickSize, z * brickSize);
                Vector3Int voxelMax = Vector3Int.Min(voxelMin + Vector3Int.one * brickSize,
                    reader.header.baseRes);
                minimumVoxel = Vector3Int.Min(minimumVoxel, voxelMin);
                maximumVoxel = Vector3Int.Max(maximumVoxel, voxelMax);
                anyBaseBrick = true;
            }
        }

        internal bool TryGetBounds(out Bounds bounds)
        {
            if (!Complete) throw new InvalidOperationException("Sequence bounds are not complete.");
            if (!anyBaseBrick)
            {
                bounds = new Bounds(Vector3.zero, reader.header.worldSize);
                return false;
            }

            int margin = reader.header.brickSize * SafetyBrickMargin;
            Vector3Int lo = Vector3Int.Max(Vector3Int.zero, minimumVoxel - Vector3Int.one * margin);
            Vector3Int hi = Vector3Int.Min(reader.header.baseRes, maximumVoxel + Vector3Int.one * margin);
            bounds = LocalBounds(lo, hi, reader.header.baseRes, reader.header.worldSize);
            return true;
        }

        internal static Bounds LocalBounds(Vector3Int minimum, Vector3Int maximum, Vector3Int resolution,
            Vector3 worldSize)
        {
            Vector3 lo = new(
                (minimum.x / (float)Mathf.Max(1, resolution.x) - 0.5f) * worldSize.x,
                (minimum.y / (float)Mathf.Max(1, resolution.y) - 0.5f) * worldSize.y,
                (minimum.z / (float)Mathf.Max(1, resolution.z) - 0.5f) * worldSize.z);
            Vector3 hi = new(
                (maximum.x / (float)Mathf.Max(1, resolution.x) - 0.5f) * worldSize.x,
                (maximum.y / (float)Mathf.Max(1, resolution.y) - 0.5f) * worldSize.y,
                (maximum.z / (float)Mathf.Max(1, resolution.z) - 0.5f) * worldSize.z);
            return new Bounds((lo + hi) * 0.5f, Vector3.Max(hi - lo, Vector3.one * 0.001f));
        }

        public void Dispose() => reader?.Dispose();
    }
}
