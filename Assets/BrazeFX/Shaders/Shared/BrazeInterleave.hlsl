#ifndef BRAZE_INTERLEAVE_INCLUDED
#define BRAZE_INTERLEAVE_INCLUDED

// Match BrazeSliceRendererPasses.InterleaveTileSize. Keeping the live march and temporal resolve on this one
// schedule is important: a mismatch would preserve stale tiles indefinitely.
#define BRAZE_INTERLEAVE_TILE_SIZE 8

// Reprojection trust radius in working-resolution pixels per frame. The skipped-tile motion estimate samples
// velocity at ONE depth; a volumetric integral with fast depth-varying motion cannot be tracked by a single
// screen vector, so beyond this displacement a skipped pixel forfeits its schedule discount and marches fresh
// (and the resolve consumes its current). The march and resolve MUST share this constant and the motion value
// it is tested against, or the resolve shows history over pixels that hold live data (or worse, empty current).
#define BRAZE_INTERLEAVE_FAST_REPROJECT_PX 4.0

bool BrazeCoherentInterleavePixelValid(int2 pixel, int mode, int phase)
{
    uint2 tile = uint2(pixel) / uint(BRAZE_INTERLEAVE_TILE_SIZE);
    if (mode == 1)
        return ((tile.x + tile.y + uint(phase)) & 1u) == 0u;
    if (mode == 2)
        return ((tile.x & 1u) + ((tile.y & 1u) << 1u)) == uint(phase);
    return true;
}

#endif
