using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// C4D / Blender-cell-fracture-style Voronoi fracture of a CLOSED mesh. Each fragment = the mesh solid intersected
    /// with one convex Voronoi cell. Built to scale (thousands of cells in ms) by NEVER touching the whole mesh per
    /// cell — the convex cell is the backbone, and only the thin shell of surface-touching cells does any mesh work:
    ///
    ///   1. CONVEX CELL — clip the mesh bounding box by the bisector planes of the cell's k nearest seeds (found via a
    ///      metric-space seed grid). Faces split into BISECTOR faces (interior cut faces = caps, shared with a
    ///      neighbour) and BOX faces (the cell reached the box → it faces OUT of the object → the real surface is there).
    ///      The mesh is not touched. INTERIOR cells stop here: their bisector faces alone close them. This is the vast
    ///      majority of cells at high counts, and it is almost free — that is what makes 6400 cells fast.
    ///   2. SURFACE SKIN — a uniform triangle grid (built once) pulls only the cell's LOCAL triangles; those are clipped
    ///      by the cell's bisector planes → the conforming surface skin (skin = 1, real UV/normal), replacing box faces.
    ///   3. CAP TRIM — where the surface crosses a bisector face, the skin-clip leaves cut edges ON that plane. Each
    ///      bisector face is trimmed (in-plane half-space clips) to the solid side of those cut edges, so the cap stops
    ///      exactly at the surface instead of overshooting to the bounding box. Box faces are dropped (skin covers them).
    ///
    /// So caps and skin meet along the SAME cut edges → watertight, surface-conforming fragments with no box overshoot,
    /// while interior cells stay pure convex polytopes. Requires a reasonably watertight source (seed rejection keeps
    /// all seeds inside the solid).
    /// </summary>
    internal sealed class BrazeFractureClipper
    {
        public readonly struct SealedMesh
        {
            public readonly Vector3[] vertices;
            public readonly Vector3[] normals;
            public readonly Vector2[] uv;
            public readonly int[] triangles;
            public readonly int boundaryEdges;
            public readonly int nonManifoldEdges;
            public readonly int sealedLoops;
            public readonly int openChains;
            public readonly int capTriangles;

            public SealedMesh(Vector3[] vertices, Vector3[] normals, Vector2[] uv, int[] triangles,
                              int boundaryEdges, int nonManifoldEdges, int sealedLoops, int openChains, int capTriangles)
            {
                this.vertices = vertices;
                this.normals = normals;
                this.uv = uv;
                this.triangles = triangles;
                this.boundaryEdges = boundaryEdges;
                this.nonManifoldEdges = nonManifoldEdges;
                this.sealedLoops = sealedLoops;
                this.openChains = openChains;
                this.capTriangles = capTriangles;
            }

            public bool HasCaps => capTriangles > 0;
        }

        public sealed class Frag
        {
            public readonly List<Vector3> pos = new();   // triangle soup (3 verts per tri)
            public readonly List<Vector3> nrm = new();
            public readonly List<Vector2> uv = new();
            public readonly List<float> skin = new();    // 1 = original surface (real UV), 0 = cut-face cap (triplanar)
            public readonly List<int> neighbors = new(); // cell ids sharing a cap face (structural adjacency)
            public Vector3 centroid;
            public float radius;
            public int TriCount => pos.Count / 3;
        }

        // ---- a convex polygon face of the working polytope ----
        sealed class Face
        {
            public readonly List<Vector3> poly = new(8);
            public Vector3 nrm;        // outward normal
            public int neighbor;       // seed id of the bisector that made this face, or -1 for a bounding-box face
        }

        // mesh (local space), set per Build
        Vector3[] _mv, _mn;
        Vector2[] _muv;
        int[] _mt;
        bool _hasNrm, _hasUv;
        float _onPlaneTol;

        // bisector ordering scratch
        int[] _order;
        float[] _dist;

        // ---- per-cell bisector planes (precomputed once, reused by polytope build / skin clip / cap trim) ----
        Vector3[] _pa;     // half-space normal a (a·x + w <= 0 = inside)
        float[] _pw;
        Vector3[] _pn;     // outward (cap) normal = a.normalized
        int _planeN;
        Vector3 _cellSeed;
        bool[] _seedInside;   // optional: a cell whose seed is OUTSIDE the solid is skipped (no floating exterior cells)

        // polytope working sets + pool
        List<Face> _faces = new(32);
        List<Face> _facesNext = new(32);
        readonly Stack<Face> _facePool = new();
        readonly List<Vector3> _cut = new(32);

        // ---- surface-skin scratch (clip one triangle) ----
        readonly List<Vector3> _sp = new(8);
        readonly List<Vector3> _sn = new(8);
        readonly List<Vector2> _su = new(8);
        readonly List<Vector3> _tp = new(8);
        readonly List<Vector3> _tn = new(8);
        readonly List<Vector2> _tu = new(8);

        // ---- per-cell cut edges that landed on each bisector plane (keyed by plane index k) → cap trim boundary ----
        readonly Dictionary<int, List<Vector3>> _planeCuts = new();   // k → flat list of segment endpoint pairs
        readonly List<Vector3> _capPoly = new(16);

        // ---- uniform triangle grid ----
        Vector3 _gridMin;
        Vector3 _gridInvCell;
        Vector3Int _gridDims;
        List<int>[] _grid;
        int[] _visit;
        int _visitTick;
        readonly List<int> _localTris = new(256);

        public int CapsCulledOutside { get; private set; }
        public int CapTrisCulledOutside { get; private set; }

        // ---- uniform seed grid (in the same scaled metric used by the Voronoi distance) ----
        Vector3[] _seedMetric;
        Vector3 _seedGridMin;
        Vector3 _seedGridCell;
        Vector3 _seedGridInvCell;
        Vector3Int _seedGridDims;
        List<int>[] _seedGrid;

        public Frag[] Build(Vector3[] mv, Vector3[] mn, Vector2[] muv, int[] mt,
                            Vector4[] seeds, int neighborK, Vector3 scaleCells, float offset,
                            bool[] seedInside = null, Quaternion cellOrientation = default)
        {
            _seedInside = seedInside;
            _mv = mv; _mn = mn; _muv = muv; _mt = mt;
            _hasUv = muv != null && muv.Length == mv.Length;
            _hasNrm = mn != null && mn.Length == mv.Length;

            int n = seeds.Length;
            var frags = new Frag[n];

            if (Quaternion.Dot(cellOrientation, cellOrientation) < 0.5f) cellOrientation = Quaternion.identity;
            Vector3 invScale = new(1f / Mathf.Max(Mathf.Abs(scaleCells.x), 1e-4f),
                                   1f / Mathf.Max(Mathf.Abs(scaleCells.y), 1e-4f),
                                   1f / Mathf.Max(Mathf.Abs(scaleCells.z), 1e-4f));
            // T maps target-local positions into the distance metric. M=T^T*T is the corresponding symmetric
            // quadratic form, allowing the formerly axis-locked anisotropy to follow an arbitrary material frame.
            Matrix4x4 toMetric = Matrix4x4.Scale(invScale) * Matrix4x4.Rotate(Quaternion.Inverse(cellOrientation));
            Matrix4x4 metric = toMetric.transpose * toMetric;
            // candidate pool for each cell's bisectors; the security-radius test below stops clipping as soon as the
            // cell is provably closed, so this is just an upper bound (kept generous so the test, not the cap, ends it).
            int planeCap = Mathf.Min(n - 1, Mathf.Max(neighborK, 96));
            if (_order == null || _order.Length != n) { _order = new int[n]; _dist = new float[n]; }
            if (_pa == null || _pa.Length < planeCap) { _pa = new Vector3[planeCap]; _pw = new float[planeCap]; _pn = new Vector3[planeCap]; }

            // mesh AABB (the polytope's bounding box) + a small inflate so box faces sit just outside the surface
            Vector3 lo = new(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 hi = new(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < mv.Length; i++) { lo = Vector3.Min(lo, mv[i]); hi = Vector3.Max(hi, mv[i]); }
            Vector3 bc = (lo + hi) * 0.5f, be = (hi - lo) * 0.5f + Vector3.one * 1e-5f;
            Vector3 boxLo = bc - be, boxHi = bc + be;
            _onPlaneTol = Mathf.Max((hi - lo).magnitude * 1e-5f, 1e-6f);

            BuildSeedGrid(seeds, toMetric);
            BuildGrid(boxLo, boxHi);
            CapsCulledOutside = 0;
            CapTrisCulledOutside = 0;

            for (int i = 0; i < n; i++)
                frags[i] = BuildCell(i, seeds, metric, toMetric, planeCap, boxLo, boxHi, offset);

            return frags;
        }

        Frag BuildCell(int ci, Vector4[] seeds, Matrix4x4 metric, Matrix4x4 toMetric,
                       int planeCap, Vector3 boxLo, Vector3 boxHi, float offset)
        {
            var frag = new Frag();
            Vector3 si = seeds[ci];
            _cellSeed = si;

            // a seed that ended up OUTSIDE the solid (seed rejection couldn't place it) would otherwise emit its full
            // convex polytope as a floating exterior chunk (the "spikes"). Skip it entirely.
            if (_seedInside != null && ci < _seedInside.Length && !_seedInside[ci])
            {
                frag.centroid = si; frag.radius = 0f;
                return frag;
            }

            // 1) convex polytope = box clipped by the cell's nearest bisectors, nearest-first, with the Voro++
            // SECURITY-RADIUS early-out: a seed at metric distance r bisects at r/2, so once r/2 ≥ the cell's current
            // farthest vertex (in the same metric) no farther seed can ever cut the cell → stop. This makes each cell
            // EXACT (fully bounded, no neighbour overlap) using the minimum number of planes — fixes the over-fill that
            // a fixed nearest-K caused, and stays fast (dense cells close after only a handful of planes).
            int nearestCount = FindNearestSeeds(ci, planeCap);
            ResetToBox(boxLo, boxHi);
            float maxMDist2 = MaxMetricDist2(si, toMetric);
            _planeN = 0;
            for (int k = 0; k < nearestCount; k++)
            {
                int j = _order[k];
                if (_dist[j] >= 4f * maxMDist2) break;   // security radius reached → cell is provably closed
                Vector3 sj = seeds[j];
                Vector3 a = 2f * metric.MultiplyVector(sj - si);
                if (a.sqrMagnitude < 1e-20f) continue;
                _pa[_planeN] = a;
                _pw[_planeN] = Vector3.Dot(si, metric.MultiplyVector(si))
                             - Vector3.Dot(sj, metric.MultiplyVector(sj));
                _pn[_planeN] = a.normalized;
                _order[_planeN] = j;
                ClipByBisector(_planeN);
                _planeN++;
                if (_faces.Count == 0) break;
                maxMDist2 = MaxMetricDist2(si, toMetric);
            }

            // cell AABB (for the triangle grid query) while marking which bisector faces survived
            Vector3 aabbLo = new(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 aabbHi = new(float.MinValue, float.MinValue, float.MinValue);
            for (int f = 0; f < _faces.Count; f++)
            {
                var face = _faces[f];
                for (int v = 0; v < face.poly.Count; v++) { aabbLo = Vector3.Min(aabbLo, face.poly[v]); aabbHi = Vector3.Max(aabbHi, face.poly[v]); }
            }

            // 2) surface skin = local triangles clipped by the cell's bisectors (skin = 1); records cut edges per plane
            foreach (var kv in _planeCuts) kv.Value.Clear();
            QueryGrid(aabbLo, aabbHi);
            for (int t = 0; t < _localTris.Count; t++)
                ClipTriToCell(frag, _localTris[t]);

            // 3) caps = bisector faces, trimmed to the solid side of the surface cut edges (box faces are dropped)
            for (int f = 0; f < _faces.Count; f++)
            {
                var face = _faces[f];
                if (face.neighbor < 0) continue;                 // box face → surface direction, skin covers it
                EmitCap(frag, face);
            }

            FinalizeFrag(frag, offset);
            return frag;
        }

        // ---------------- convex polytope ----------------

        // Cell's farthest vertex from the seed, squared, in the Voronoi (scaleCells) metric — drives the security radius.
        float MaxMetricDist2(Vector3 si, Matrix4x4 toMetric)
        {
            float m = 0f;
            for (int f = 0; f < _faces.Count; f++)
            {
                var poly = _faces[f].poly;
                for (int v = 0; v < poly.Count; v++)
                {
                    Vector3 mdv = toMetric.MultiplyVector(poly[v] - si);
                    float md = mdv.sqrMagnitude;
                    if (md > m) m = md;
                }
            }
            return m;
        }

        void ResetToBox(Vector3 lo, Vector3 hi)
        {
            for (int i = 0; i < _faces.Count; i++) _facePool.Push(_faces[i]);
            _faces.Clear();

            AddBoxFace(new Vector3(lo.x, lo.y, lo.z), new Vector3(lo.x, hi.y, lo.z), new Vector3(lo.x, hi.y, hi.z), new Vector3(lo.x, lo.y, hi.z), Vector3.left);
            AddBoxFace(new Vector3(hi.x, lo.y, lo.z), new Vector3(hi.x, lo.y, hi.z), new Vector3(hi.x, hi.y, hi.z), new Vector3(hi.x, hi.y, lo.z), Vector3.right);
            AddBoxFace(new Vector3(lo.x, lo.y, lo.z), new Vector3(lo.x, lo.y, hi.z), new Vector3(hi.x, lo.y, hi.z), new Vector3(hi.x, lo.y, lo.z), Vector3.down);
            AddBoxFace(new Vector3(lo.x, hi.y, lo.z), new Vector3(hi.x, hi.y, lo.z), new Vector3(hi.x, hi.y, hi.z), new Vector3(lo.x, hi.y, hi.z), Vector3.up);
            AddBoxFace(new Vector3(lo.x, lo.y, lo.z), new Vector3(hi.x, lo.y, lo.z), new Vector3(hi.x, hi.y, lo.z), new Vector3(lo.x, hi.y, lo.z), Vector3.back);
            AddBoxFace(new Vector3(lo.x, lo.y, hi.z), new Vector3(lo.x, hi.y, hi.z), new Vector3(hi.x, hi.y, hi.z), new Vector3(hi.x, lo.y, hi.z), Vector3.forward);
        }

        void AddBoxFace(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 nrm)
        {
            Face f = RentFace();
            f.neighbor = -1; f.nrm = nrm;
            f.poly.Add(a); f.poly.Add(b); f.poly.Add(c); f.poly.Add(d);
            _faces.Add(f);
        }

        Face RentFace() { Face f = _facePool.Count > 0 ? _facePool.Pop() : new Face(); f.poly.Clear(); return f; }

        // Clip the polytope by precomputed bisector half-space k: keep a·x + w <= 0 (closer to si).
        void ClipByBisector(int k)
        {
            Vector3 a = _pa[k];
            float w = _pw[k];
            Vector3 outwardN = _pn[k];
            int neighbor = _order[k];

            _cut.Clear();
            for (int i = 0; i < _facesNext.Count; i++) _facePool.Push(_facesNext[i]);
            _facesNext.Clear();

            for (int f = 0; f < _faces.Count; f++)
            {
                Face face = _faces[f];
                Face clipped = RentFace();
                clipped.neighbor = face.neighbor; clipped.nrm = face.nrm;

                int cnt = face.poly.Count;
                for (int i = 0; i < cnt; i++)
                {
                    Vector3 A = face.poly[i], B = face.poly[(i + 1) % cnt];
                    float da = a.x * A.x + a.y * A.y + a.z * A.z + w;
                    float db = a.x * B.x + a.y * B.y + a.z * B.z + w;
                    bool ain = da <= 0f, bin = db <= 0f;
                    if (ain) clipped.poly.Add(A);
                    if (ain != bin)
                    {
                        Vector3 X = Vector3.LerpUnclamped(A, B, da / (da - db));
                        clipped.poly.Add(X);
                        _cut.Add(X);
                    }
                }

                if (clipped.poly.Count >= 3) _facesNext.Add(clipped);
                else _facePool.Push(clipped);
            }

            if (_cut.Count >= 3)
            {
                Face nf = RentFace();
                nf.neighbor = neighbor; nf.nrm = outwardN;
                DedupAndOrder(_cut, outwardN, nf.poly);
                if (nf.poly.Count >= 3) _facesNext.Add(nf); else _facePool.Push(nf);
            }

            (_faces, _facesNext) = (_facesNext, _faces);
        }

        void DedupAndOrder(List<Vector3> pts, Vector3 nrm, List<Vector3> outPoly)
        {
            const float eps2 = 1e-10f;
            outPoly.Clear();
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 p = pts[i];
                bool dup = false;
                for (int j = 0; j < outPoly.Count; j++) if ((outPoly[j] - p).sqrMagnitude < eps2) { dup = true; break; }
                if (!dup) outPoly.Add(p);
            }
            if (outPoly.Count < 3) return;

            Vector3 c = Vector3.zero;
            for (int i = 0; i < outPoly.Count; i++) c += outPoly[i];
            c /= outPoly.Count;

            Vector3 u = Mathf.Abs(nrm.x) < 0.9f ? Vector3.Cross(nrm, Vector3.right) : Vector3.Cross(nrm, Vector3.up);
            u.Normalize();
            Vector3 v = Vector3.Cross(nrm, u);
            outPoly.Sort((p, q) =>
            {
                Vector3 dp = p - c, dq = q - c;
                float ap = Mathf.Atan2(Vector3.Dot(dp, v), Vector3.Dot(dp, u));
                float aq = Mathf.Atan2(Vector3.Dot(dq, v), Vector3.Dot(dq, u));
                return ap.CompareTo(aq);
            });
        }

        // ---------------- surface skin ----------------

        // Clip one mesh triangle by the cell's bisector half-spaces; keep the inside polygon as skin; record the edges
        // that ended up lying ON each bisector plane (the cap-trim boundary).
        void ClipTriToCell(Frag frag, int triStart)
        {
            int i0 = _mt[triStart], i1 = _mt[triStart + 1], i2 = _mt[triStart + 2];
            _sp.Clear(); _sn.Clear(); _su.Clear();
            _sp.Add(_mv[i0]); _sp.Add(_mv[i1]); _sp.Add(_mv[i2]);
            _sn.Add(_hasNrm ? _mn[i0] : Vector3.zero); _sn.Add(_hasNrm ? _mn[i1] : Vector3.zero); _sn.Add(_hasNrm ? _mn[i2] : Vector3.zero);
            _su.Add(_hasUv ? _muv[i0] : default); _su.Add(_hasUv ? _muv[i1] : default); _su.Add(_hasUv ? _muv[i2] : default);

            for (int k = 0; k < _planeN && _sp.Count >= 3; k++)
                ClipPolyHalfSpace(_pa[k], _pw[k]);
            if (_sp.Count < 3) return;

            // record skin edges that lie on a bisector plane → cap trim boundary for that plane. Store the surface
            // OUTWARD normal with each chord so the cap is trimmed to the exact solid side (behind the surface),
            // independent of any seed-position heuristic.
            Vector3 triN = Vector3.Cross(_mv[i1] - _mv[i0], _mv[i2] - _mv[i0]);
            if (triN.sqrMagnitude > 1e-18f) triN = triN.normalized; else triN = Vector3.up;
            int m = _sp.Count;
            for (int e = 0; e < m; e++)
            {
                Vector3 A = _sp[e], B = _sp[(e + 1) % m];
                for (int k = 0; k < _planeN; k++)
                {
                    float ga = (_pa[k].x * A.x + _pa[k].y * A.y + _pa[k].z * A.z + _pw[k]);
                    float gb = (_pa[k].x * B.x + _pa[k].y * B.y + _pa[k].z * B.z + _pw[k]);
                    float inv = 1f / Mathf.Max(_pa[k].magnitude, 1e-12f);
                    if (Mathf.Abs(ga) * inv < _onPlaneTol && Mathf.Abs(gb) * inv < _onPlaneTol)
                    {
                        var list = GetPlaneCut(k);
                        list.Add(A); list.Add(B); list.Add(triN);
                        break;   // an edge lies on at most one plane
                    }
                }
            }

            // fan-triangulate the clipped surface polygon (skin = 1)
            for (int k = 1; k + 1 < _sp.Count; k++)
            {
                AddSkinVert(frag, _sp[0], _sn[0], _su[0]);
                AddSkinVert(frag, _sp[k], _sn[k], _su[k]);
                AddSkinVert(frag, _sp[k + 1], _sn[k + 1], _su[k + 1]);
            }
        }

        List<Vector3> GetPlaneCut(int k)
        {
            if (!_planeCuts.TryGetValue(k, out var list)) { list = new List<Vector3>(8); _planeCuts[k] = list; }
            return list;
        }

        void ClipPolyHalfSpace(Vector3 a, float w)
        {
            _tp.Clear(); _tn.Clear(); _tu.Clear();
            int cnt = _sp.Count;
            for (int i = 0; i < cnt; i++)
            {
                int ni = (i + 1) % cnt;
                Vector3 A = _sp[i], B = _sp[ni];
                float da = a.x * A.x + a.y * A.y + a.z * A.z + w;
                float db = a.x * B.x + a.y * B.y + a.z * B.z + w;
                bool ain = da <= 0f, bin = db <= 0f;
                if (ain) { _tp.Add(A); _tn.Add(_sn[i]); _tu.Add(_su[i]); }
                if (ain != bin)
                {
                    float t = da / (da - db);
                    _tp.Add(Vector3.LerpUnclamped(A, B, t));
                    _tn.Add(Vector3.LerpUnclamped(_sn[i], _sn[ni], t));
                    _tu.Add(Vector2.LerpUnclamped(_su[i], _su[ni], t));
                }
            }
            _sp.Clear(); _sp.AddRange(_tp);
            _sn.Clear(); _sn.AddRange(_tn);
            _su.Clear(); _su.AddRange(_tu);
        }

        static void AddSkinVert(Frag frag, Vector3 p, Vector3 n, Vector2 uv)
        {
            frag.pos.Add(p);
            frag.nrm.Add(n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up);
            frag.uv.Add(uv);
            frag.skin.Add(1f);
        }

        // ---------------- caps (bisector faces, trimmed to the surface) ----------------

        // Find this bisector face's plane index (so we can look up its surface cut edges).
        int PlaneIndexOf(int neighborSeed)
        {
            for (int k = 0; k < _planeN; k++) if (_order[k] == neighborSeed) return k;
            return -1;
        }

        void EmitCap(Frag frag, Face face)
        {
            int k = PlaneIndexOf(face.neighbor);
            List<Vector3> cuts = (k >= 0 && _planeCuts.TryGetValue(k, out var c)) ? c : null;

            if (cuts != null && cuts.Count > 0)
            {
                if (cuts.Count >= 9 && EmitCapCutLoops(frag, face, cuts))
                    frag.neighbors.Add(face.neighbor);
                return;
            }

            _capPoly.Clear();
            _capPoly.AddRange(face.poly);

            if (_capPoly.Count < 3) return;
            if (!CapFaceInterior(_capPoly))
            {
                CapsCulledOutside++;
                return;
            }
            for (int t = 1; t + 1 < _capPoly.Count; t++)
                AddCapTri(frag, _capPoly[0], _capPoly[t], _capPoly[t + 1], face.nrm);
            frag.neighbors.Add(face.neighbor);
        }

        bool EmitCapCutLoops(Frag frag, Face face, List<Vector3> cuts)
        {
            Vector3 nrm = face.nrm;
            Vector3 origin = face.poly.Count > 0 ? face.poly[0] : _cellSeed;
            Vector3 u = Mathf.Abs(nrm.x) < 0.9f ? Vector3.Cross(nrm, Vector3.right) : Vector3.Cross(nrm, Vector3.up);
            if (u.sqrMagnitude < 1e-14f) u = Vector3.right;
            u.Normalize();
            Vector3 v = Vector3.Cross(nrm, u);
            float weld = Mathf.Max(_onPlaneTol * 8f, 1e-6f);
            float weld2 = weld * weld;

            var pts3 = new List<Vector3>(cuts.Count / 2);
            var pts2 = new List<Vector2>(cuts.Count / 2);
            var edges = new HashSet<EdgeKey>();
            var adj = new Dictionary<int, List<int>>();

            int segs = cuts.Count / 3;
            for (int s = 0; s < segs; s++)
            {
                Vector3 a = cuts[s * 3], b = cuts[s * 3 + 1];
                if ((a - b).sqrMagnitude <= weld2) continue;
                int ia = AddCapPoint(pts3, pts2, a, origin, u, v, weld2);
                int ib = AddCapPoint(pts3, pts2, b, origin, u, v, weld2);
                if (ia == ib) continue;
                var edge = new EdgeKey(ia, ib);
                if (!edges.Add(edge)) continue;
                AddAdj(adj, ia, ib);
                AddAdj(adj, ib, ia);
            }

            if (edges.Count < 3) return false;

            var used = new HashSet<EdgeKey>();
            var triLocal = new List<int>(32);
            var loopIds = new List<int>(32);
            var loop2 = new List<Vector2>(32);
            var loop3 = new List<Vector3>(32);
            bool emitted = false;

            foreach (var edge in edges)
            {
                if (used.Contains(edge)) continue;
                loopIds.Clear();
                if (!TraceCapLoop(edge.a, edge.b, adj, used, loopIds, edges.Count)) continue;
                CleanCapLoop(loopIds, pts2, weld2);
                if (loopIds.Count < 3) continue;

                loop2.Clear();
                loop3.Clear();
                for (int i = 0; i < loopIds.Count; i++)
                {
                    loop2.Add(pts2[loopIds[i]]);
                    loop3.Add(pts3[loopIds[i]]);
                }
                if (SignedArea(loop2) < 0f)
                {
                    loop2.Reverse();
                    loop3.Reverse();
                }
                if (!TriangulateEarClip(loop2, triLocal)) continue;

                for (int i = 0; i < triLocal.Count; i += 3)
                {
                    if (!CapTriInterior(loop3[triLocal[i]], loop3[triLocal[i + 1]], loop3[triLocal[i + 2]]))
                    {
                        CapTrisCulledOutside++;
                        continue;
                    }
                    AddCapTri(frag, loop3[triLocal[i]], loop3[triLocal[i + 1]], loop3[triLocal[i + 2]], nrm);
                    emitted = true;
                }
            }

            return emitted;
        }

        bool CapFaceInterior(List<Vector3> poly)
        {
            if (poly.Count < 3) return false;

            Vector3 c = Vector3.zero;
            for (int i = 0; i < poly.Count; i++) c += poly[i];
            c /= poly.Count;
            if (!InsideAccel(c)) return false;

            for (int i = 0; i < poly.Count; i++)
            {
                Vector3 a = poly[i], b = poly[(i + 1) % poly.Count];
                if (!InsideAccel(Vector3.LerpUnclamped(a, c, 0.35f))) return false;
                if (!InsideAccel(Vector3.LerpUnclamped(a, b, 0.5f))) return false;
            }
            return true;
        }

        bool CapTriInterior(Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 cen = (a + b + c) / 3f;
            if (!InsideAccel(cen)) return false;
            if (!InsideAccel(Vector3.LerpUnclamped(a, cen, 0.5f))) return false;
            if (!InsideAccel(Vector3.LerpUnclamped(b, cen, 0.5f))) return false;
            if (!InsideAccel(Vector3.LerpUnclamped(c, cen, 0.5f))) return false;
            return true;
        }

        static void AddCapTri(Frag frag, Vector3 a, Vector3 b, Vector3 c, Vector3 nrm)
        {
            frag.pos.Add(a); frag.nrm.Add(nrm); frag.uv.Add(Vector2.zero); frag.skin.Add(0f);
            frag.pos.Add(b); frag.nrm.Add(nrm); frag.uv.Add(Vector2.zero); frag.skin.Add(0f);
            frag.pos.Add(c); frag.nrm.Add(nrm); frag.uv.Add(Vector2.zero); frag.skin.Add(0f);
        }

        static int AddCapPoint(List<Vector3> pts3, List<Vector2> pts2, Vector3 p, Vector3 origin, Vector3 u, Vector3 v, float weld2)
        {
            for (int i = 0; i < pts3.Count; i++)
                if ((pts3[i] - p).sqrMagnitude <= weld2)
                    return i;

            int id = pts3.Count;
            pts3.Add(p);
            Vector3 d = p - origin;
            pts2.Add(new Vector2(Vector3.Dot(d, u), Vector3.Dot(d, v)));
            return id;
        }

        static bool TraceCapLoop(int start, int firstNext, Dictionary<int, List<int>> adj, HashSet<EdgeKey> used, List<int> loop, int guardMax)
        {
            int prev = -1;
            int cur = start;
            int next = firstNext;
            loop.Add(start);

            for (int guard = 0; guard <= guardMax + 4; guard++)
            {
                used.Add(new EdgeKey(cur, next));
                prev = cur;
                cur = next;
                if (cur == start) return true;
                loop.Add(cur);

                if (!adj.TryGetValue(cur, out var neighbors)) return false;
                int chosen = -1;
                for (int i = 0; i < neighbors.Count; i++)
                {
                    int n = neighbors[i];
                    if (n == prev) continue;
                    if (!used.Contains(new EdgeKey(cur, n))) { chosen = n; break; }
                }
                if (chosen < 0)
                {
                    for (int i = 0; i < neighbors.Count; i++)
                    {
                        int n = neighbors[i];
                        if (!used.Contains(new EdgeKey(cur, n))) { chosen = n; break; }
                    }
                }
                if (chosen < 0) return false;
                next = chosen;
            }

            return false;
        }

        static void CleanCapLoop(List<int> ids, List<Vector2> pts, float weld2)
        {
            for (int pass = 0; pass < 4 && ids.Count >= 3; pass++)
            {
                bool changed = false;
                for (int i = ids.Count - 1; i >= 0; i--)
                {
                    int prev = ids[(i + ids.Count - 1) % ids.Count];
                    int cur = ids[i];
                    int next = ids[(i + 1) % ids.Count];
                    Vector2 a = pts[prev], b = pts[cur], c = pts[next];
                    if ((a - b).sqrMagnitude <= weld2 || Mathf.Abs(Cross2(b - a, c - b)) <= 1e-9f)
                    {
                        ids.RemoveAt(i);
                        changed = true;
                    }
                }
                if (!changed) break;
            }
        }

        // ---------------- finalize ----------------

        void FinalizeFrag(Frag frag, float offset)
        {
            int vc = frag.pos.Count;
            if (vc < 3) { frag.centroid = _cellSeed; frag.radius = 0f; return; }

            Vector3 cen = Vector3.zero;
            for (int i = 0; i < vc; i++) cen += frag.pos[i];
            cen /= vc;
            float rad = 0f;
            for (int i = 0; i < vc; i++) rad = Mathf.Max(rad, (frag.pos[i] - cen).sqrMagnitude);
            rad = Mathf.Sqrt(rad);
            float shrink = (offset > 0f && rad > 1e-5f) ? Mathf.Clamp01(offset / rad) : 0f;
            if (shrink > 0f)
                for (int i = 0; i < vc; i++) frag.pos[i] = cen + (frag.pos[i] - cen) * (1f - shrink);

            frag.centroid = cen;
            frag.radius = rad;
        }

        // ---------------- uniform seed grid ----------------

        void BuildSeedGrid(Vector4[] seeds, Matrix4x4 toMetric)
        {
            int n = seeds.Length;
            if (_seedMetric == null || _seedMetric.Length != n) _seedMetric = new Vector3[n];

            Vector3 lo = new(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 hi = new(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < n; i++)
            {
                Vector3 p = toMetric.MultiplyPoint3x4(seeds[i]);
                _seedMetric[i] = p;
                lo = Vector3.Min(lo, p);
                hi = Vector3.Max(hi, p);
            }
            InflateTinyAxes(ref lo, ref hi, 1e-5f);

            Vector3 size = hi - lo;
            float vol = Mathf.Max(size.x * size.y * size.z, 1e-12f);
            const float targetSeedsPerVoxel = 4f;
            float targetVoxels = Mathf.Max(1f, n / targetSeedsPerVoxel);
            float k = Mathf.Pow(targetVoxels / vol, 1f / 3f);
            _seedGridDims = new Vector3Int(
                Mathf.Clamp(Mathf.CeilToInt(size.x * k), 1, 128),
                Mathf.Clamp(Mathf.CeilToInt(size.y * k), 1, 128),
                Mathf.Clamp(Mathf.CeilToInt(size.z * k), 1, 128));
            _seedGridMin = lo;
            _seedGridCell = new Vector3(size.x / _seedGridDims.x, size.y / _seedGridDims.y, size.z / _seedGridDims.z);
            _seedGridInvCell = new Vector3(_seedGridDims.x / size.x, _seedGridDims.y / size.y, _seedGridDims.z / size.z);

            int voxels = _seedGridDims.x * _seedGridDims.y * _seedGridDims.z;
            _seedGrid = new List<int>[voxels];
            for (int i = 0; i < n; i++)
            {
                Vector3Int v = SeedVoxelOf(_seedMetric[i]);
                int idx = SeedVoxelIndex(v.x, v.y, v.z);
                (_seedGrid[idx] ??= new List<int>(4)).Add(i);
            }
        }

        static void InflateTinyAxes(ref Vector3 lo, ref Vector3 hi, float minSize)
        {
            Vector3 size = hi - lo;
            if (size.x < minSize) { float c = (lo.x + hi.x) * 0.5f; lo.x = c - minSize * 0.5f; hi.x = c + minSize * 0.5f; }
            if (size.y < minSize) { float c = (lo.y + hi.y) * 0.5f; lo.y = c - minSize * 0.5f; hi.y = c + minSize * 0.5f; }
            if (size.z < minSize) { float c = (lo.z + hi.z) * 0.5f; lo.z = c - minSize * 0.5f; hi.z = c + minSize * 0.5f; }
        }

        Vector3Int SeedVoxelOf(Vector3 p)
        {
            int x = Mathf.Clamp((int)((p.x - _seedGridMin.x) * _seedGridInvCell.x), 0, _seedGridDims.x - 1);
            int y = Mathf.Clamp((int)((p.y - _seedGridMin.y) * _seedGridInvCell.y), 0, _seedGridDims.y - 1);
            int z = Mathf.Clamp((int)((p.z - _seedGridMin.z) * _seedGridInvCell.z), 0, _seedGridDims.z - 1);
            return new Vector3Int(x, y, z);
        }

        int SeedVoxelIndex(int x, int y, int z) => (z * _seedGridDims.y + y) * _seedGridDims.x + x;

        int FindNearestSeeds(int ci, int planeCap)
        {
            if (planeCap <= 0) return 0;

            Vector3 p = _seedMetric[ci];
            Vector3Int home = SeedVoxelOf(p);
            int found = 0;
            int maxRing = Mathf.Max(_seedGridDims.x, Mathf.Max(_seedGridDims.y, _seedGridDims.z));
            for (int ring = 0; ring < maxRing; ring++)
            {
                VisitSeedRing(p, home, ring, ci, planeCap, ref found);
                if (found >= planeCap && WorstNearestDist(planeCap) <= MinDistToUnvisitedSeedSpace(p, home, ring))
                    break;
            }

            SortNearestSeeds(found);
            return found;
        }

        void VisitSeedRing(Vector3 p, Vector3Int home, int ring, int self, int limit, ref int found)
        {
            int minX = Mathf.Max(0, home.x - ring), maxX = Mathf.Min(_seedGridDims.x - 1, home.x + ring);
            int minY = Mathf.Max(0, home.y - ring), maxY = Mathf.Min(_seedGridDims.y - 1, home.y + ring);
            int minZ = Mathf.Max(0, home.z - ring), maxZ = Mathf.Min(_seedGridDims.z - 1, home.z + ring);

            for (int z = minZ; z <= maxZ; z++)
                for (int y = minY; y <= maxY; y++)
                    for (int x = minX; x <= maxX; x++)
                    {
                        int dx = Mathf.Abs(x - home.x), dy = Mathf.Abs(y - home.y), dz = Mathf.Abs(z - home.z);
                        if (Mathf.Max(dx, Mathf.Max(dy, dz)) != ring) continue;

                        var bucket = _seedGrid[SeedVoxelIndex(x, y, z)];
                        if (bucket == null) continue;
                        for (int i = 0; i < bucket.Count; i++)
                        {
                            int j = bucket[i];
                            if (j == self) continue;
                            Vector3 d = _seedMetric[j] - p;
                            float dsq = d.x * d.x + d.y * d.y + d.z * d.z;
                            AddNearestSeed(j, dsq, limit, ref found);
                        }
                    }
        }

        void AddNearestSeed(int seed, float dsq, int limit, ref int found)
        {
            if (found < limit)
            {
                _order[found++] = seed;
                _dist[seed] = dsq;
                return;
            }

            int worst = 0;
            float worstDist = _dist[_order[0]];
            for (int i = 1; i < limit; i++)
            {
                float d = _dist[_order[i]];
                if (d > worstDist) { worst = i; worstDist = d; }
            }
            if (dsq >= worstDist) return;
            _order[worst] = seed;
            _dist[seed] = dsq;
        }

        float WorstNearestDist(int count)
        {
            float worst = _dist[_order[0]];
            for (int i = 1; i < count; i++) worst = Mathf.Max(worst, _dist[_order[i]]);
            return worst;
        }

        float MinDistToUnvisitedSeedSpace(Vector3 p, Vector3Int home, int ring)
        {
            int minX = Mathf.Max(0, home.x - ring), maxX = Mathf.Min(_seedGridDims.x - 1, home.x + ring);
            int minY = Mathf.Max(0, home.y - ring), maxY = Mathf.Min(_seedGridDims.y - 1, home.y + ring);
            int minZ = Mathf.Max(0, home.z - ring), maxZ = Mathf.Min(_seedGridDims.z - 1, home.z + ring);

            float best = float.PositiveInfinity;
            if (minX > 0) best = Mathf.Min(best, Sqr(p.x - (_seedGridMin.x + minX * _seedGridCell.x)));
            if (maxX < _seedGridDims.x - 1) best = Mathf.Min(best, Sqr(_seedGridMin.x + (maxX + 1) * _seedGridCell.x - p.x));
            if (minY > 0) best = Mathf.Min(best, Sqr(p.y - (_seedGridMin.y + minY * _seedGridCell.y)));
            if (maxY < _seedGridDims.y - 1) best = Mathf.Min(best, Sqr(_seedGridMin.y + (maxY + 1) * _seedGridCell.y - p.y));
            if (minZ > 0) best = Mathf.Min(best, Sqr(p.z - (_seedGridMin.z + minZ * _seedGridCell.z)));
            if (maxZ < _seedGridDims.z - 1) best = Mathf.Min(best, Sqr(_seedGridMin.z + (maxZ + 1) * _seedGridCell.z - p.z));
            return best;
        }

        static float Sqr(float x) => x * x;

        void SortNearestSeeds(int count)
        {
            for (int i = 1; i < count; i++)
            {
                int seed = _order[i];
                float d = _dist[seed];
                int j = i - 1;
                while (j >= 0 && _dist[_order[j]] > d)
                {
                    _order[j + 1] = _order[j];
                    j--;
                }
                _order[j + 1] = seed;
            }
        }

        // ---------------- uniform triangle grid ----------------

        void BuildGrid(Vector3 lo, Vector3 hi)
        {
            int triCount = _mt.Length / 3;
            Vector3 size = hi - lo;
            float vol = Mathf.Max(size.x * size.y * size.z, 1e-9f);
            float cell = Mathf.Pow(vol / Mathf.Max(triCount, 1), 1f / 3f);
            cell = Mathf.Max(cell, 1e-4f);
            _gridDims = new Vector3Int(
                Mathf.Clamp(Mathf.CeilToInt(size.x / cell), 1, 128),
                Mathf.Clamp(Mathf.CeilToInt(size.y / cell), 1, 128),
                Mathf.Clamp(Mathf.CeilToInt(size.z / cell), 1, 128));
            _gridMin = lo;
            _gridInvCell = new Vector3(_gridDims.x / size.x, _gridDims.y / size.y, _gridDims.z / size.z);

            int voxels = _gridDims.x * _gridDims.y * _gridDims.z;
            _grid = new List<int>[voxels];
            _visit = new int[triCount];
            _visitTick = 0;

            for (int t = 0; t < _mt.Length; t += 3)
            {
                Vector3 p0 = _mv[_mt[t]], p1 = _mv[_mt[t + 1]], p2 = _mv[_mt[t + 2]];
                Vector3 tlo = Vector3.Min(p0, Vector3.Min(p1, p2));
                Vector3 thi = Vector3.Max(p0, Vector3.Max(p1, p2));
                Vector3Int a = VoxelOf(tlo), b = VoxelOf(thi);
                for (int z = a.z; z <= b.z; z++)
                    for (int y = a.y; y <= b.y; y++)
                        for (int x = a.x; x <= b.x; x++)
                        {
                            int idx = (z * _gridDims.y + y) * _gridDims.x + x;
                            (_grid[idx] ??= new List<int>(4)).Add(t);
                        }
            }
        }

        Vector3Int VoxelOf(Vector3 p)
        {
            int x = Mathf.Clamp((int)((p.x - _gridMin.x) * _gridInvCell.x), 0, _gridDims.x - 1);
            int y = Mathf.Clamp((int)((p.y - _gridMin.y) * _gridInvCell.y), 0, _gridDims.y - 1);
            int z = Mathf.Clamp((int)((p.z - _gridMin.z) * _gridInvCell.z), 0, _gridDims.z - 1);
            return new Vector3Int(x, y, z);
        }

        void QueryGrid(Vector3 lo, Vector3 hi)
        {
            _localTris.Clear();
            if (lo.x > hi.x) return;   // empty cell
            _visitTick++;
            Vector3Int a = VoxelOf(lo), b = VoxelOf(hi);
            for (int z = a.z; z <= b.z; z++)
                for (int y = a.y; y <= b.y; y++)
                    for (int x = a.x; x <= b.x; x++)
                    {
                        var bucket = _grid[(z * _gridDims.y + y) * _gridDims.x + x];
                        if (bucket == null) continue;
                        for (int i = 0; i < bucket.Count; i++)
                        {
                            int t = bucket[i];
                            int ti = t / 3;
                            if (_visit[ti] == _visitTick) continue;
                            _visit[ti] = _visitTick;
                            _localTris.Add(t);
                        }
                    }
        }

        // ---------------- helpers ----------------

        // ---- point-in-mesh (clean watertight mesh): +X ray parity test, for seed rejection ----
        public static bool PointInside(Vector3 p, Vector3[] mv, int[] mt)
        {
            int hits = 0;
            for (int t = 0; t < mt.Length; t += 3)
            {
                Vector3 a = mv[mt[t]], b = mv[mt[t + 1]], c = mv[mt[t + 2]];
                if (RayHitsTri(p, a, b, c)) hits++;
            }
            return (hits & 1) == 1;
        }

        static bool RayHitsTri(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            float d = (b.y - c.y) * (a.z - c.z) - (b.z - c.z) * (a.y - c.y);
            if (Mathf.Abs(d) < 1e-12f) return false;
            float u = ((b.y - c.y) * (p.z - c.z) - (b.z - c.z) * (p.y - c.y)) / d;
            float v = ((c.y - a.y) * (p.z - c.z) - (c.z - a.z) * (p.y - c.y)) / d;
            float w = 1f - u - v;
            if (u < 0f || v < 0f || w < 0f) return false;
            float hitX = u * a.x + v * b.x + w * c.x;
            return hitX > p.x;
        }

        // ---- accelerated inside-test: a (y,z) grid so a +X ray only tests the few triangles in its column ----
        Vector3[] _itMv; int[] _itMt;
        float _itYMin, _itZMin, _itInvCy, _itInvCz;
        int _itNy, _itNz;
        List<int>[] _itGrid;

        public void BuildInsideAccel(Vector3[] mv, int[] mt)
        {
            _itMv = mv; _itMt = mt;
            float ylo = float.MaxValue, yhi = float.MinValue, zlo = float.MaxValue, zhi = float.MinValue;
            for (int i = 0; i < mv.Length; i++)
            {
                if (mv[i].y < ylo) ylo = mv[i].y; if (mv[i].y > yhi) yhi = mv[i].y;
                if (mv[i].z < zlo) zlo = mv[i].z; if (mv[i].z > zhi) zhi = mv[i].z;
            }
            int triCount = mt.Length / 3;
            int res = Mathf.Clamp(Mathf.CeilToInt(Mathf.Sqrt(triCount)), 1, 256);
            _itNy = res; _itNz = res;
            float sy = Mathf.Max(yhi - ylo, 1e-6f), sz = Mathf.Max(zhi - zlo, 1e-6f);
            _itYMin = ylo; _itZMin = zlo;
            _itInvCy = _itNy / sy; _itInvCz = _itNz / sz;
            _itGrid = new List<int>[_itNy * _itNz];

            for (int t = 0; t < mt.Length; t += 3)
            {
                Vector3 a = mv[mt[t]], b = mv[mt[t + 1]], c = mv[mt[t + 2]];
                float tylo = Mathf.Min(a.y, Mathf.Min(b.y, c.y)), tyhi = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
                float tzlo = Mathf.Min(a.z, Mathf.Min(b.z, c.z)), tzhi = Mathf.Max(a.z, Mathf.Max(b.z, c.z));
                int y0 = ItCellY(tylo), y1 = ItCellY(tyhi), z0 = ItCellZ(tzlo), z1 = ItCellZ(tzhi);
                for (int z = z0; z <= z1; z++)
                    for (int y = y0; y <= y1; y++)
                        (_itGrid[z * _itNy + y] ??= new List<int>(4)).Add(t);
            }
        }

        int ItCellY(float y) => Mathf.Clamp((int)((y - _itYMin) * _itInvCy), 0, _itNy - 1);
        int ItCellZ(float z) => Mathf.Clamp((int)((z - _itZMin) * _itInvCz), 0, _itNz - 1);

        public bool InsideAccel(Vector3 p)
        {
            if (_itGrid == null) return PointInside(p, _itMv ?? System.Array.Empty<Vector3>(), _itMt ?? System.Array.Empty<int>());
            var bucket = _itGrid[ItCellZ(p.z) * _itNy + ItCellY(p.y)];
            if (bucket == null) return false;
            int hits = 0;
            for (int i = 0; i < bucket.Count; i++)
            {
                int t = bucket[i];
                if (RayHitsTri(p, _itMv[_itMt[t]], _itMv[_itMt[t + 1]], _itMv[_itMt[t + 2]])) hits++;
            }
            return (hits & 1) == 1;
        }

        public static SealedMesh SealOpenBoundaries(Vector3[] mv, Vector3[] mn, Vector2[] muv, int[] mt)
        {
            if (mv == null || mt == null || mv.Length == 0 || mt.Length < 3)
                return new SealedMesh(mv, mn, muv, mt, 0, 0, 0, 0, 0);

            Vector3 lo = new(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 hi = new(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < mv.Length; i++) { lo = Vector3.Min(lo, mv[i]); hi = Vector3.Max(hi, mv[i]); }
            Vector3 center = (lo + hi) * 0.5f;
            float diag = Mathf.Max((hi - lo).magnitude, 1e-4f);
            float weldTol = Mathf.Max(diag * 1e-6f, 1e-6f);
            float invWeldTol = 1f / weldTol;

            var weldMap = new Dictionary<WeldKey, int>(mv.Length);
            var weldPos = new List<Vector3>(mv.Length);
            var weldCount = new List<int>(mv.Length);
            var weldId = new int[mv.Length];
            for (int i = 0; i < mv.Length; i++)
            {
                var key = WeldKey.From(mv[i], invWeldTol);
                if (!weldMap.TryGetValue(key, out int id))
                {
                    id = weldPos.Count;
                    weldMap.Add(key, id);
                    weldPos.Add(mv[i]);
                    weldCount.Add(1);
                }
                else
                {
                    weldPos[id] = weldPos[id] + mv[i];
                    weldCount[id] = weldCount[id] + 1;
                }
                weldId[i] = id;
            }
            for (int i = 0; i < weldPos.Count; i++) weldPos[i] = weldPos[i] / Mathf.Max(1, weldCount[i]);

            var edgeCounts = new Dictionary<EdgeKey, int>(mt.Length);
            for (int t = 0; t + 2 < mt.Length; t += 3)
            {
                int a = weldId[mt[t]], b = weldId[mt[t + 1]], c = weldId[mt[t + 2]];
                if (a == b || b == c || c == a) continue;
                CountEdge(edgeCounts, a, b);
                CountEdge(edgeCounts, b, c);
                CountEdge(edgeCounts, c, a);
            }

            var boundary = new List<EdgeKey>();
            int nonManifold = 0;
            foreach (var kv in edgeCounts)
            {
                if (kv.Value == 1) boundary.Add(kv.Key);
                else if (kv.Value > 2) nonManifold++;
            }
            if (boundary.Count == 0)
                return new SealedMesh(mv, mn, muv, mt, 0, nonManifold, 0, 0, 0);

            var adj = new Dictionary<int, List<int>>();
            for (int i = 0; i < boundary.Count; i++)
            {
                EdgeKey e = boundary[i];
                AddAdj(adj, e.a, e.b);
                AddAdj(adj, e.b, e.a);
            }

            var verts = new List<Vector3>(mv.Length + boundary.Count);
            var norms = new List<Vector3>(mv.Length + boundary.Count);
            var uvs = (muv != null && muv.Length == mv.Length) ? new List<Vector2>(mv.Length + boundary.Count) : null;
            var tris = new List<int>(mt.Length + boundary.Count * 3);
            verts.AddRange(mv);
            if (mn != null && mn.Length == mv.Length) norms.AddRange(mn);
            else for (int i = 0; i < mv.Length; i++) norms.Add(Vector3.up);
            if (uvs != null) uvs.AddRange(muv);
            tris.AddRange(mt);

            var used = new HashSet<EdgeKey>();
            var loop = new List<int>(128);
            int sealedLoops = 0, openChains = 0, capTris = 0;
            for (int i = 0; i < boundary.Count; i++)
            {
                EdgeKey first = boundary[i];
                if (used.Contains(first)) continue;

                loop.Clear();
                bool closed = TraceBoundaryLoop(first, adj, used, loop, boundary.Count);
                if (!closed || loop.Count < 3)
                {
                    openChains++;
                    continue;
                }

                int added = AddCap(loop, weldPos, center, weldTol, verts, norms, uvs, tris);
                if (added > 0)
                {
                    sealedLoops++;
                    capTris += added;
                }
            }

            Vector2[] outUv = uvs != null ? uvs.ToArray() : muv;
            return new SealedMesh(verts.ToArray(), norms.ToArray(), outUv, tris.ToArray(),
                                  boundary.Count, nonManifold, sealedLoops, openChains, capTris);
        }

        static void CountEdge(Dictionary<EdgeKey, int> edges, int a, int b)
        {
            var key = new EdgeKey(a, b);
            edges.TryGetValue(key, out int count);
            edges[key] = count + 1;
        }

        static void AddAdj(Dictionary<int, List<int>> adj, int a, int b)
        {
            if (!adj.TryGetValue(a, out var list)) { list = new List<int>(4); adj[a] = list; }
            list.Add(b);
        }

        static bool TraceBoundaryLoop(EdgeKey first, Dictionary<int, List<int>> adj, HashSet<EdgeKey> used, List<int> loop, int guardMax)
        {
            int start = first.a;
            int prev = -1;
            int cur = first.a;
            int next = first.b;
            loop.Add(start);

            for (int guard = 0; guard <= guardMax + 4; guard++)
            {
                used.Add(new EdgeKey(cur, next));
                prev = cur;
                cur = next;
                if (cur == start) return true;
                loop.Add(cur);

                if (!adj.TryGetValue(cur, out var neighbors)) return false;
                int chosen = -1;
                for (int i = 0; i < neighbors.Count; i++)
                {
                    int n = neighbors[i];
                    if (n == prev) continue;
                    if (!used.Contains(new EdgeKey(cur, n))) { chosen = n; break; }
                }
                if (chosen < 0)
                {
                    for (int i = 0; i < neighbors.Count; i++)
                    {
                        int n = neighbors[i];
                        if (!used.Contains(new EdgeKey(cur, n))) { chosen = n; break; }
                    }
                }
                if (chosen < 0) return false;
                next = chosen;
            }
            return false;
        }

        static int AddCap(List<int> loopIds, List<Vector3> weldPos, Vector3 meshCenter, float weldTol,
                          List<Vector3> verts, List<Vector3> norms, List<Vector2> uvs, List<int> tris)
        {
            var pts = new List<Vector3>(loopIds.Count);
            float minEdge2 = weldTol * weldTol;
            for (int i = 0; i < loopIds.Count; i++)
            {
                Vector3 p = weldPos[loopIds[i]];
                if (pts.Count == 0 || (pts[^1] - p).sqrMagnitude > minEdge2) pts.Add(p);
            }
            if (pts.Count > 1 && (pts[0] - pts[^1]).sqrMagnitude <= minEdge2) pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3) return 0;

            Vector3 polyCenter = Vector3.zero;
            for (int i = 0; i < pts.Count; i++) polyCenter += pts[i];
            polyCenter /= pts.Count;

            Vector3 nrm = NewellNormal(pts);
            if (nrm.sqrMagnitude < 1e-14f) nrm = FallbackNormal(pts);
            if (nrm.sqrMagnitude < 1e-14f) return 0;
            nrm.Normalize();
            if (Vector3.Dot(nrm, polyCenter - meshCenter) < 0f)
            {
                pts.Reverse();
                nrm = -nrm;
            }

            Vector3 u = Mathf.Abs(nrm.x) < 0.9f ? Vector3.Cross(nrm, Vector3.right) : Vector3.Cross(nrm, Vector3.up);
            if (u.sqrMagnitude < 1e-14f) u = Vector3.right;
            u.Normalize();
            Vector3 v = Vector3.Cross(nrm, u);

            var p2 = new List<Vector2>(pts.Count);
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 d = pts[i] - polyCenter;
                p2.Add(new Vector2(Vector3.Dot(d, u), Vector3.Dot(d, v)));
            }

            int baseV = verts.Count;
            for (int i = 0; i < pts.Count; i++)
            {
                verts.Add(pts[i]);
                norms.Add(nrm);
                uvs?.Add(p2[i]);
            }

            var localTris = new List<int>(Mathf.Max(3, (pts.Count - 2) * 3));
            if (!TriangulateEarClip(p2, localTris))
            {
                int centerIndex = verts.Count;
                verts.Add(polyCenter);
                norms.Add(nrm);
                uvs?.Add(Vector2.zero);
                for (int i = 0; i < pts.Count; i++)
                {
                    tris.Add(centerIndex);
                    tris.Add(baseV + i);
                    tris.Add(baseV + ((i + 1) % pts.Count));
                }
                return pts.Count;
            }

            for (int i = 0; i < localTris.Count; i++) tris.Add(baseV + localTris[i]);
            return localTris.Count / 3;
        }

        static bool TriangulateEarClip(List<Vector2> pts, List<int> tris)
        {
            tris.Clear();
            int n = pts.Count;
            if (n < 3) return false;

            float area = SignedArea(pts);
            if (Mathf.Abs(area) < 1e-12f) return false;
            bool ccw = area > 0f;

            var ids = new List<int>(n);
            for (int i = 0; i < n; i++) ids.Add(i);

            int guard = n * n;
            while (ids.Count > 3 && guard-- > 0)
            {
                bool clipped = false;
                for (int i = 0; i < ids.Count; i++)
                {
                    int ia = ids[(i + ids.Count - 1) % ids.Count];
                    int ib = ids[i];
                    int ic = ids[(i + 1) % ids.Count];
                    if (!IsConvex(pts[ia], pts[ib], pts[ic], ccw)) continue;

                    bool blocked = false;
                    for (int j = 0; j < ids.Count; j++)
                    {
                        int ip = ids[j];
                        if (ip == ia || ip == ib || ip == ic) continue;
                        if (PointInTri2D(pts[ip], pts[ia], pts[ib], pts[ic], ccw)) { blocked = true; break; }
                    }
                    if (blocked) continue;

                    tris.Add(ia); tris.Add(ib); tris.Add(ic);
                    ids.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) return false;
            }

            if (ids.Count != 3) return false;
            tris.Add(ids[0]); tris.Add(ids[1]); tris.Add(ids[2]);
            return true;
        }

        static float SignedArea(List<Vector2> pts)
        {
            float a = 0f;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector2 p = pts[i], q = pts[(i + 1) % pts.Count];
                a += p.x * q.y - q.x * p.y;
            }
            return a * 0.5f;
        }

        static bool IsConvex(Vector2 a, Vector2 b, Vector2 c, bool ccw)
        {
            float cross = Cross2(b - a, c - b);
            return ccw ? cross > 1e-8f : cross < -1e-8f;
        }

        static bool PointInTri2D(Vector2 p, Vector2 a, Vector2 b, Vector2 c, bool ccw)
        {
            float ab = Cross2(b - a, p - a);
            float bc = Cross2(c - b, p - b);
            float ca = Cross2(a - c, p - c);
            const float eps = 1e-8f;
            return ccw ? (ab >= -eps && bc >= -eps && ca >= -eps)
                       : (ab <= eps && bc <= eps && ca <= eps);
        }

        static float Cross2(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        static Vector3 NewellNormal(List<Vector3> pts)
        {
            Vector3 n = Vector3.zero;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector3 p = pts[i], q = pts[(i + 1) % pts.Count];
                n.x += (p.y - q.y) * (p.z + q.z);
                n.y += (p.z - q.z) * (p.x + q.x);
                n.z += (p.x - q.x) * (p.y + q.y);
            }
            return n;
        }

        static Vector3 FallbackNormal(List<Vector3> pts)
        {
            for (int i = 0; i < pts.Count - 2; i++)
                for (int j = i + 1; j < pts.Count - 1; j++)
                    for (int k = j + 1; k < pts.Count; k++)
                    {
                        Vector3 n = Vector3.Cross(pts[j] - pts[i], pts[k] - pts[i]);
                        if (n.sqrMagnitude > 1e-14f) return n;
                    }
            return Vector3.zero;
        }

        readonly struct WeldKey : System.IEquatable<WeldKey>
        {
            readonly long _x, _y, _z;
            WeldKey(long x, long y, long z) { _x = x; _y = y; _z = z; }
            public static WeldKey From(Vector3 p, float invTol)
                => new((long)System.Math.Round(p.x * invTol),
                       (long)System.Math.Round(p.y * invTol),
                       (long)System.Math.Round(p.z * invTol));
            public bool Equals(WeldKey other) => _x == other._x && _y == other._y && _z == other._z;
            public override bool Equals(object obj) => obj is WeldKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = _x.GetHashCode();
                    h = (h * 397) ^ _y.GetHashCode();
                    h = (h * 397) ^ _z.GetHashCode();
                    return h;
                }
            }
        }

        readonly struct EdgeKey : System.IEquatable<EdgeKey>
        {
            public readonly int a, b;
            public EdgeKey(int x, int y)
            {
                if (x < y) { a = x; b = y; }
                else { a = y; b = x; }
            }
            public bool Equals(EdgeKey other) => a == other.a && b == other.b;
            public override bool Equals(object obj) => obj is EdgeKey other && Equals(other);
            public override int GetHashCode() { unchecked { return (a * 397) ^ b; } }
        }
    }
}
