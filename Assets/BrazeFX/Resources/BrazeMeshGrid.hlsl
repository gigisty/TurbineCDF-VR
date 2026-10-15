// BrazeFX Mesh Effects — GPU triangle GRID (hashed uniform grid) for nearest-surface queries.
//
// Built fresh each frame over a deforming mesh's CURRENT world-space verts + faces (from BrazeDeformSource).
// A fixed-capacity HASHED grid: triangles are scattered into buckets for their covered cells with atomics
// (no prefix-sum, no sort) → rebuild is a single parallel pass. Queries gather a bounded neighbour ring
// and keep the nearest closest-point-on-triangle within a snap range — returning the triangle, barycentric
// coords, surface point and normal, which is exactly what growth needs to snap a tip and bake its binding.
//
// Hash collisions (distinct cells → same bucket) only add candidate triangles; the per-triangle distance test
// filters them, so results stay correct. Bucket overflow past _GridBucketCap drops triangles from that bucket
// (rare on uniform surface meshes) — sized generously by the CPU wrapper.

#ifndef BRAZE_MESH_GRID_INCLUDED
#define BRAZE_MESH_GRID_INCLUDED

StructuredBuffer<float3> _GridPos;        // deformed world-space vertex positions
StructuredBuffer<float3> _GridNrm;        // deformed world-space vertex normals
StructuredBuffer<uint>   _GridIdx;        // triangle indices (3 per tri)

#ifdef BRAZE_GRID_WRITABLE
RWStructuredBuffer<uint> _GridCellCount;  // per-bucket triangle count
RWStructuredBuffer<uint> _GridCellItems;  // per-bucket triangle ids, _GridBucketCap slots each
#else
StructuredBuffer<uint> _GridCellCount;    // per-bucket triangle count
StructuredBuffer<uint> _GridCellItems;    // per-bucket triangle ids, _GridBucketCap slots each
#endif

uint  _GridTriCount;
uint  _GridTableSize;     // bucket count, power of two (cheap modulo via & (size-1))
uint  _GridBucketCap;     // max triangles stored per bucket
float _GridCellSize;      // world units; floored by the CPU wrapper so the search ring stays bounded
int   _GridSearchCells;   // neighbour ring radius in cells (1 = 3x3x3, 2 = 5x5x5)
uint  _GridRowThreads;    // 2D-dispatch row width → linear thread index past the 65535 group cap

uint BrazeGridFlatIndex(uint3 gid) { return gid.x + gid.y * _GridRowThreads; }

int3 braze_grid_cell(float3 p, float cs) { return (int3)floor(p / cs); }

uint braze_grid_hash(int3 c, uint tableSize)
{
    uint h = ((uint)(c.x) * 73856093u) ^ ((uint)(c.y) * 19349663u) ^ ((uint)(c.z) * 83492791u);
    return h & (tableSize - 1u);
}

// Closest point on triangle abc to p (Ericson, Real-Time Collision Detection, section 5.1.5), with barycentric out.
float3 braze_closest_on_tri(float3 p, float3 a, float3 b, float3 c, out float3 bary)
{
    bary = float3(1.0, 0.0, 0.0);
    float3 closest = a;
    float3 ab = b - a, ac = c - a, ap = p - a;
    float d1 = dot(ab, ap), d2 = dot(ac, ap);
    if (!(d1 <= 0.0 && d2 <= 0.0))
    {
        float3 bp = p - b;
        float d3 = dot(ab, bp), d4 = dot(ac, bp);
        if (d3 >= 0.0 && d4 <= d3)
        {
            bary = float3(0.0, 1.0, 0.0);
            closest = b;
        }
        else
        {
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0.0 && d1 >= 0.0 && d3 <= 0.0)
            {
                float v = d1 / (d1 - d3);
                bary = float3(1.0 - v, v, 0.0);
                closest = a + v * ab;
            }
            else
            {
                float3 cp = p - c;
                float d5 = dot(ab, cp), d6 = dot(ac, cp);
                if (d6 >= 0.0 && d5 <= d6)
                {
                    bary = float3(0.0, 0.0, 1.0);
                    closest = c;
                }
                else
                {
                    float vb = d5 * d2 - d1 * d6;
                    if (vb <= 0.0 && d2 >= 0.0 && d6 <= 0.0)
                    {
                        float w = d2 / (d2 - d6);
                        bary = float3(1.0 - w, 0.0, w);
                        closest = a + w * ac;
                    }
                    else
                    {
                        float va = d3 * d6 - d5 * d4;
                        if (va <= 0.0 && (d4 - d3) >= 0.0 && (d5 - d6) >= 0.0)
                        {
                            float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                            bary = float3(0.0, 1.0 - w, w);
                            closest = b + w * (c - b);
                        }
                        else
                        {
                            float denom = 1.0 / (va + vb + vc);
                            float v = vb * denom, w = vc * denom;
                            bary = float3(1.0 - v - w, v, w);
                            closest = a + ab * v + ac * w;
                        }
                    }
                }
            }
        }
    }

    return closest;
}

// Squared distance from p to ONE triangle, with its closest point / barycentric / interpolated normal
// (un-normalized). Shared by the ring query and warm-start hint paths.
float braze_tri_closest(uint tri, float3 p, out float3 bary, out float3 pos, out float3 nrm)
{
    uint i0 = _GridIdx[tri * 3 + 0], i1 = _GridIdx[tri * 3 + 1], i2 = _GridIdx[tri * 3 + 2];
    float3 a = _GridPos[i0], b = _GridPos[i1], c = _GridPos[i2];
    pos = braze_closest_on_tri(p, a, b, c, bary);
    nrm = bary.x * _GridNrm[i0] + bary.y * _GridNrm[i1] + bary.z * _GridNrm[i2];
    float3 d = pos - p;
    return dot(d, d);
}

// Nearest triangle within `range` of p. Returns false if none. Outputs triangle id, barycentric, the surface
// point, and the bary-interpolated normal.
//
// Cells are scanned in EXPANDING Chebyshev shells with an early exit: a triangle whose closest point lies
// within shell k was scattered into a shell-≤k bucket (triangles register in every cell they cover), so once
// the best hit is nearer than k·cellSize nothing in a farther shell can beat it. Glued/near-surface queries —
// the common case for surface flow and collision — terminate after the 3×3×3 core instead of the full ring.
bool braze_grid_query(float3 p, float range, out int triId, out float3 bary, out float3 pos, out float3 nrm)
{
    triId = -1; bary = float3(1, 0, 0); pos = p; nrm = float3(0, 1, 0);
    float best = range * range;
    int3 baseCell = braze_grid_cell(p, _GridCellSize);
    int R = max(0, _GridSearchCells);

    for (int k = 0; k <= R; k++)
    {
        for (int dz = -k; dz <= k; dz++)
        for (int dy = -k; dy <= k; dy++)
        for (int dx = -k; dx <= k; dx++)
        {
            if (max(abs(dx), max(abs(dy), abs(dz))) != k) continue;   // shell surface only (inner shells done)
            uint h = braze_grid_hash(baseCell + int3(dx, dy, dz), _GridTableSize);
            uint cnt = min(_GridCellCount[h], _GridBucketCap);
            for (uint s = 0; s < cnt; s++)
            {
                uint tri = _GridCellItems[h * _GridBucketCap + s];
                float3 bc, cpp, nn;
                float dd = braze_tri_closest(tri, p, bc, cpp, nn);
                if (dd < best)
                {
                    best = dd; triId = (int)tri; bary = bc; pos = cpp; nrm = nn;
                }
            }
        }
        float bound = k * _GridCellSize;   // farther shells cannot hold anything nearer than this
        if (best <= bound * bound) break;
    }
    if (triId >= 0) { float l = length(nrm); nrm = l > 1e-8 ? nrm / l : float3(0, 1, 0); }
    return triId >= 0;
}

#endif // BRAZE_MESH_GRID_INCLUDED
