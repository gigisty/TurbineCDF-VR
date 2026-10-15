using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// Mesh Effects — GPU triangle grid wrapper. Owns the hashed-bucket buffers and rebuilds them each frame
    /// over a deforming mesh's current world-space verts + faces (from BrazeDeformSource), via a clear +
    /// atomic-scatter pass (no prefix-sum, no sort). A consumer (e.g. the GPU spread port) calls
    /// BindQuery() to wire the grid into their own compute kernel and use braze_grid_query() from
    /// BrazeMeshGrid.hlsl. See that file for the data model.
    /// </summary>
    public sealed class BrazeMeshGrid : System.IDisposable
    {
        readonly ComputeShader cs;
        readonly int kClear, kBuild;
        readonly int bucketCap;

        GraphicsBuffer cellCount, cellItems;
        int tableSize;

        // Geometry + params from the last Build (so BindQuery can rebind to a consumer kernel).
        GraphicsBuffer pos, nrm, idx;
        int triCount;
        public float CellSize { get; private set; }
        public int SearchCells { get; private set; }
        public int TableSize => tableSize;
        public bool Ready => cellCount != null && triCount > 0;

        const int MaxGroupsPerDim = 65535;
        const int MaxTableSize = 1 << 20;   // caps cellItems memory (×bucketCap×4 bytes)
        const int MaxSearchCells = 2;       // keep nearest-surface queries to a fixed 5x5x5 worst case

        public BrazeMeshGrid(ComputeShader gridCS, int bucketCapacity = 16)
        {
            cs = gridCS;
            kClear = cs.FindKernel("ClearGrid");
            kBuild = cs.FindKernel("BuildGrid");
            bucketCap = Mathf.Max(4, bucketCapacity);
        }

        /// <summary>Suggest a cell size around a couple of typical edge lengths, floored by callers to the
        /// snap range. Keeping cell size near the range avoids expensive wide-neighbour searches.</summary>
        public static float SuggestCellSize(Bounds worldBounds, int triCount)
        {
            float diag = Mathf.Max(1e-4f, worldBounds.size.magnitude);
            float edge = triCount > 0 ? diag / Mathf.Max(1f, Mathf.Sqrt(triCount)) : diag * 0.05f;
            return Mathf.Max(edge * 2f, diag * 1e-3f);
        }

        /// <summary>Rebuild the grid over the current deformed geometry. Cell size is floored from snapRange
        /// so queries stay fixed-cost instead of scanning larger and larger cell rings.</summary>
        public void Build(GraphicsBuffer positions, GraphicsBuffer normals, GraphicsBuffer indices,
                          int triangleCount, float cellSize, float snapRange)
        {
            pos = positions; nrm = normals; idx = indices; triCount = triangleCount;
            CellSize = Mathf.Max(1e-5f, cellSize, snapRange / MaxSearchCells);
            SearchCells = Mathf.Clamp(Mathf.CeilToInt(snapRange / CellSize), 1, MaxSearchCells);
            EnsureBuffers(triCount);
            if (cellCount == null) return;

            BindCommon(cs, kClear);
            Dispatch(kClear, tableSize);

            BindCommon(cs, kBuild);
            Dispatch(kBuild, triCount);
        }

        /// <summary>Bind the grid + geometry + params into a consumer's kernel so it can call braze_grid_query.</summary>
        public void BindQuery(ComputeShader consumer, int kernel)
        {
            BindCommon(consumer, kernel);
        }

        void BindCommon(ComputeShader target, int kernel)
        {
            target.SetBuffer(kernel, "_GridPos", pos);
            target.SetBuffer(kernel, "_GridNrm", nrm);
            target.SetBuffer(kernel, "_GridIdx", idx);
            target.SetBuffer(kernel, "_GridCellCount", cellCount);
            target.SetBuffer(kernel, "_GridCellItems", cellItems);
            target.SetInt("_GridTriCount", triCount);
            target.SetInt("_GridTableSize", tableSize);
            target.SetInt("_GridBucketCap", bucketCap);
            target.SetFloat("_GridCellSize", CellSize);
            target.SetInt("_GridSearchCells", SearchCells);
        }

        void EnsureBuffers(int triangleCount)
        {
            int want = NextPow2(Mathf.Clamp(triangleCount * 2, 16, MaxTableSize));
            if (cellCount != null && tableSize == want) return;
            tableSize = want;
            cellCount?.Release();
            cellItems?.Release();
            cellCount = new GraphicsBuffer(GraphicsBuffer.Target.Structured, tableSize, sizeof(uint));
            cellItems = new GraphicsBuffer(GraphicsBuffer.Target.Structured, tableSize * bucketCap, sizeof(uint));
        }

        void Dispatch(int kernel, int threads)
        {
            if (threads <= 0) return;
            int groups = Mathf.CeilToInt(threads / 64f);
            int gx = groups <= MaxGroupsPerDim ? groups : Mathf.Min(MaxGroupsPerDim, Mathf.CeilToInt(Mathf.Sqrt(groups)));
            int gy = Mathf.CeilToInt((float)groups / gx);
            cs.SetInt("_GridRowThreads", gx * 64);
            cs.Dispatch(kernel, gx, gy, 1);
        }

        static int NextPow2(int v)
        {
            int p = 1;
            while (p < v) p <<= 1;
            return p;
        }

        public void Dispose()
        {
            cellCount?.Release(); cellCount = null;
            cellItems?.Release(); cellItems = null;
        }
    }
}
