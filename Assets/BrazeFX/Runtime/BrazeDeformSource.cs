using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace BrazeFX
{
    /// <summary>
    /// Mesh Effects — evaluated-surface sampler, GPU-resident. Wraps one renderer and exposes its CURRENT
    /// final vertices as clean world-space position + normal GraphicsBuffers, plus the triangle index buffer.
    ///
    /// It reads the engine's EVALUATED deformed vertex buffer straight off the GPU (SkinnedMeshRenderer /
    /// Mesh GetVertexBuffer — skin + blendshapes already applied by GPU skinning, for free) and transforms it
    /// to world space in a compute pass. No per-frame CPU skinning, no upload. Skinning, blend shapes, and
    /// procedural edits all collapse to the same contract: final verts/normals/faces — growth binds by
    /// triangle+barycentric and stays agnostic to the deformer.
    ///
    /// The skinned buffer's coordinate space is renderer/root-bone relative, so it is auto-calibrated ONCE
    /// into an anchor-local space from BakeMesh ground truth. Each frame then applies the live anchor matrix,
    /// which keeps root-motion/skinned animation attached without per-frame CPU skinning. If GetVertexBuffer
    /// is unavailable (e.g. some cloth setups), it falls back to a CPU BakeMesh snapshot.
    /// </summary>
    public sealed class BrazeDeformSource : System.IDisposable
    {
        // Kept for serialized-component compat. Auto: skinned → calibrated GPU space, mesh → renderer-local.
        public enum SpaceMode { Auto, Calibrated, World, RendererLocal }

        public GraphicsBuffer Positions => posBuf;   // StructuredBuffer<float3>, world space
        public GraphicsBuffer Normals => nrmBuf;     // StructuredBuffer<float3>, world space, unit
        public GraphicsBuffer Indices => idxBuf;     // StructuredBuffer<uint>, 3*TriangleCount
        public int VertexCount { get; private set; }
        public int TriangleCount { get; private set; }
        public int TopologyVersion { get; private set; }
        public int[] TrianglesCPU { get; private set; }
        public Vector3[] PositionsCPU => cpuPos;      // populated only on the CPU fallback path (else null)
        public Vector3[] NormalsCPU => cpuNrm;
        public bool Valid => rend != null && extractCS != null;
        public bool ExtractedOnce { get; private set; }
        public bool Calibrated => calibrated || !WantCalibrate;
        public bool UsingGpuPath { get; private set; }

        public SpaceMode space = SpaceMode.Auto;

        readonly Renderer rend;
        readonly SkinnedMeshRenderer smr;
        readonly MeshFilter mf;
        readonly ComputeShader extractCS;
        readonly int kExtract;

        Mesh topologyMesh;
        GraphicsBuffer posBuf, nrmBuf, idxBuf;
        GraphicsBuffer pendingSrc;     // deformed GPU buffer from last frame, disposed next Refresh
        Vector3[] cpuPos, cpuNrm;      // fallback-path scratch (and topology-version source)
        readonly List<Vector3> localPos = new();
        readonly List<Vector3> localNrm = new();
        int topologyIndexCount = -1;

        // Vertex-stream layout of the deformed GPU buffer (parsed once per topology).
        int srcStride, posOff, nrmOff;
        bool hasNrm, layoutOK;

        // One-time skinned buffer→anchor-local calibration. The live anchor matrix is applied every frame.
        Matrix4x4 calibMatrix = Matrix4x4.identity;
        bool calibrated, calibInFlight;
        Vector3[] calibLocal;
        Matrix4x4 calibL2W;
        Mesh bakeMesh;
        GraphicsBuffer calibPosBuf;    // identity-extracted positions for the fit (released after)
        uint resourceGeneration;
        bool disposed;
        bool WantCalibrate => smr != null && (space == SpaceMode.Auto || space == SpaceMode.Calibrated);

        const int MaxGroupsPerDim = 65535;

        public BrazeDeformSource(Renderer renderer, SpaceMode space = SpaceMode.Auto)
        {
            rend = renderer;
            smr = renderer as SkinnedMeshRenderer;
            mf = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
            this.space = space;

            if (rend == null) { Debug.LogError("[BrazeFX] BrazeDeformSource: no renderer."); return; }
            if (smr == null && mf == null)
            {
                Debug.LogError($"[BrazeFX] BrazeDeformSource: '{renderer.name}' is not a SkinnedMeshRenderer or MeshFilter renderer.");
                return;
            }

            extractCS = Resources.Load<ComputeShader>("BrazeMeshExtract");
            if (extractCS == null) { Debug.LogError("[BrazeFX] Could not load 'BrazeMeshExtract' compute."); return; }
            kExtract = extractCS.FindKernel("Extract");
            if (smr != null) smr.vertexBufferTarget |= GraphicsBuffer.Target.Raw;

            Refresh();
        }

        Mesh TopologyOf() => smr != null ? smr.sharedMesh : (mf != null ? mf.sharedMesh : null);

        /// <summary>Refresh final world-space positions/normals + current faces. Call after the frame's
        /// animation/deformation has evaluated (LateUpdate).</summary>
        public void Refresh()
        {
            if (disposed) return;
            Mesh topo = TopologyOf();
            if (topo == null) return;
            bool topologyCanChange = smr == null;   // procedural meshes may swap faces
            if (!SyncTopology(topo, topologyCanChange)) return;

            pendingSrc?.Dispose(); pendingSrc = null;

            GraphicsBuffer src = AcquireDeformed();
            if (src != null && layoutOK)
            {
                pendingSrc = src;
                if (WantCalibrate && !calibrated)
                {
                    // Space not solved yet: render via the CPU snapshot (correct now), and in parallel
                    // identity-extract + BakeMesh-fit the buffer→anchor transform. One or two frames only.
                    CalibrateStep(src);
                    BakeFallback(topo);
                    UsingGpuPath = false;
                }
                else
                {
                    DispatchExtract(src, ResolveMatrix(), posBuf, nrmBuf);
                    UsingGpuPath = true;
                    ExtractedOnce = true;
                }
            }
            else
            {
                src?.Dispose();       // not retained in pendingSrc → release the GetVertexBuffer() buffer (else it leaks each frame)
                BakeFallback(topo);   // GetVertexBuffer unavailable (e.g. cloth) → CPU snapshot
                UsingGpuPath = false;
            }
        }

        Matrix4x4 ResolveMatrix()
        {
            switch (space)
            {
                case SpaceMode.World: return Matrix4x4.identity;
                case SpaceMode.RendererLocal: return rend.localToWorldMatrix;
                case SpaceMode.Calibrated: return AnchorLocalToWorld() * calibMatrix;
                default: return smr != null ? AnchorLocalToWorld() * calibMatrix : rend.localToWorldMatrix;   // Auto
            }
        }

        Matrix4x4 AnchorLocalToWorld()
        {
            Transform anchor = smr != null && smr.rootBone != null ? smr.rootBone : rend.transform;
            return anchor.localToWorldMatrix;
        }

        Matrix4x4 WorldToAnchorLocal()
        {
            Transform anchor = smr != null && smr.rootBone != null ? smr.rootBone : rend.transform;
            return anchor.worldToLocalMatrix;
        }

        // ── GPU extract ───────────────────────────────────────────────────────────────
        void DispatchExtract(GraphicsBuffer src, Matrix4x4 m, GraphicsBuffer outPos, GraphicsBuffer outNrm)
        {
            extractCS.SetBuffer(kExtract, "_Src", src);
            extractCS.SetBuffer(kExtract, "_OutPos", outPos);
            extractCS.SetBuffer(kExtract, "_OutNrm", outNrm);
            extractCS.SetInt("_VertCount", VertexCount);
            extractCS.SetInt("_DstBase", 0);
            extractCS.SetInt("_Stride", srcStride);
            extractCS.SetInt("_PosOffset", posOff);
            extractCS.SetInt("_NrmOffset", nrmOff);
            extractCS.SetInt("_HasNrm", hasNrm ? 1 : 0);
            extractCS.SetMatrix("_LocalToWorld", m);
            extractCS.SetMatrix("_NormalMatrix", m.inverse.transpose);
            Dispatch(VertexCount);
        }

        GraphicsBuffer AcquireDeformed()
        {
            if (smr != null) return smr.GetVertexBuffer();   // null until first GPU skin
            var mesh = mf.sharedMesh;
            mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
            return mesh.GetVertexBuffer(0);
        }

        // ── One-time calibration (skinned) ────────────────────────────────────────────
        void CalibrateStep(GraphicsBuffer src)
        {
            if (calibInFlight) return;

            // Capture BakeMesh ground truth for THIS pose, then identity-extract the GPU buffer and read it
            // back so the readback and the ground truth correspond. We solve into anchor-local space; the
            // anchor's live localToWorld is applied every frame, so animated root-bone motion stays attached.
            bakeMesh ??= new Mesh { hideFlags = HideFlags.DontSave };
            smr.BakeMesh(bakeMesh, true);
            calibLocal = bakeMesh.vertices;
            calibL2W = WorldToAnchorLocal() * rend.localToWorldMatrix;
            if (calibLocal.Length != VertexCount) return;

            if (calibPosBuf == null || calibPosBuf.count != VertexCount)
            {
                calibPosBuf?.Release();
                calibPosBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, VertexCount, sizeof(float) * 3);
            }
            DispatchExtract(src, Matrix4x4.identity, calibPosBuf, nrmBuf);   // nrmBuf = scratch (BakeFallback overwrites)

            calibInFlight = true;
            uint generation = resourceGeneration;
            AsyncGPUReadback.Request(calibPosBuf, req =>
            {
                if (disposed || generation != resourceGeneration) return;
                calibInFlight = false;
                if (req.hasError) return;
                calibMatrix = SolveBufferToWorld(req.GetData<Vector3>(), calibLocal, calibL2W, VertexCount);
                calibrated = true;
                calibLocal = null;
                calibPosBuf?.Release(); calibPosBuf = null;
#if BRAZEFX_VERBOSE
                Debug.Log($"[BrazeFX] Calibrated skinned buffer→world for '{rend.name}' (GPU extract path active).");
#endif
            });
        }

        // ── CPU fallback (evaluated-mesh snapshot) ────────────────────────────────────
        void BakeFallback(Mesh topo)
        {
            Mesh snapshot;
            if (smr != null)
            {
                bakeMesh ??= new Mesh { name = "BrazeFX_DeformSnapshot", hideFlags = HideFlags.DontSave };
                bakeMesh.MarkDynamic();
                smr.BakeMesh(bakeMesh, true);   // useScale → then ×localToWorld below gives final world verts
                snapshot = bakeMesh;
            }
            else snapshot = topo;
            if (snapshot.vertexCount != VertexCount) return;

            snapshot.GetVertices(localPos);
            bool hn = snapshot.HasVertexAttribute(VertexAttribute.Normal);
            if (hn) snapshot.GetNormals(localNrm);
            hn = hn && localNrm.Count == VertexCount;

            var l2w = rend.localToWorldMatrix;
            var n2w = rend.worldToLocalMatrix.transpose;
            EnsureCpuArrays(VertexCount);
            for (int i = 0; i < VertexCount; i++) cpuPos[i] = l2w.MultiplyPoint3x4(localPos[i]);
            if (hn)
                for (int i = 0; i < VertexCount; i++)
                {
                    Vector3 n = n2w.MultiplyVector(localNrm[i]);
                    cpuNrm[i] = n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up;
                }
            else RebuildNormalsFromFaces();

            posBuf.SetData(cpuPos, 0, 0, VertexCount);
            nrmBuf.SetData(cpuNrm, 0, 0, VertexCount);
            ExtractedOnce = true;
        }

        // ── Topology ──────────────────────────────────────────────────────────────────
        bool SyncTopology(Mesh mesh, bool forceTriangleRefresh)
        {
            int indexCount = IndexCount(mesh);
            bool structural = mesh != topologyMesh || mesh.vertexCount != VertexCount || indexCount != topologyIndexCount;
            if (!structural && !forceTriangleRefresh) return true;

            int[] tris = mesh.triangles;
            topologyMesh = mesh;
            topologyIndexCount = tris.Length;
            VertexCount = mesh.vertexCount;
            TriangleCount = tris.Length / 3;
            TrianglesCPU = tris;
            if (structural)
            {
                TopologyVersion++;
                ParseLayout(mesh);
                EnsureVertexBuffers(VertexCount);
                EnsureIndexBuffer(tris);
                Debug.Log($"[BrazeFX] DeformSource '{mesh.name}': verts={VertexCount} tris={TriangleCount} " +
                          $"stride={srcStride} posOff={posOff} nrm={(hasNrm ? nrmOff.ToString() : "none")}");
            }
            else if (forceTriangleRefresh) EnsureIndexBuffer(tris);
            return true;
        }

        void ParseLayout(Mesh mesh)
        {
            int posStream = mesh.GetVertexAttributeStream(VertexAttribute.Position);
            layoutOK = posStream >= 0;
            if (!layoutOK) return;
            srcStride = mesh.GetVertexBufferStride(posStream);
            posOff = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
            hasNrm = mesh.HasVertexAttribute(VertexAttribute.Normal)
                     && mesh.GetVertexAttributeStream(VertexAttribute.Normal) == posStream;
            nrmOff = hasNrm ? mesh.GetVertexAttributeOffset(VertexAttribute.Normal) : 0;
            if (mesh.GetVertexAttributeFormat(VertexAttribute.Position) != VertexAttributeFormat.Float32)
                Debug.LogWarning($"[BrazeFX] '{mesh.name}' position not Float32 — GPU extract assumes Float32x3 (CPU fallback still works).");
        }

        static int IndexCount(Mesh mesh)
        {
            long total = 0;
            for (int i = 0; i < mesh.subMeshCount; i++) total += (long)mesh.GetIndexCount(i);
            return total > int.MaxValue ? int.MaxValue : (int)total;
        }

        void EnsureVertexBuffers(int count)
        {
            if (posBuf != null && posBuf.count == count) return;
            posBuf?.Release(); nrmBuf?.Release();
            posBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 3);
            nrmBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(float) * 3);
        }

        void EnsureIndexBuffer(int[] tris)
        {
            int count = Mathf.Max(1, tris.Length);
            if (idxBuf == null || idxBuf.count != count)
            {
                idxBuf?.Release();
                idxBuf = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, sizeof(uint));
            }
            var idx = new uint[count];
            for (int i = 0; i < tris.Length; i++) idx[i] = (uint)tris[i];
            idxBuf.SetData(idx);
        }

        void EnsureCpuArrays(int count)
        {
            if (cpuPos == null || cpuPos.Length != count) { cpuPos = new Vector3[count]; cpuNrm = new Vector3[count]; }
        }

        void RebuildNormalsFromFaces()
        {
            for (int i = 0; i < VertexCount; i++) cpuNrm[i] = Vector3.zero;
            if (TrianglesCPU != null)
                for (int i = 0; i + 2 < TrianglesCPU.Length; i += 3)
                {
                    int a = TrianglesCPU[i], b = TrianglesCPU[i + 1], c = TrianglesCPU[i + 2];
                    if ((uint)a >= VertexCount || (uint)b >= VertexCount || (uint)c >= VertexCount) continue;
                    Vector3 n = Vector3.Cross(cpuPos[b] - cpuPos[a], cpuPos[c] - cpuPos[a]);
                    cpuNrm[a] += n; cpuNrm[b] += n; cpuNrm[c] += n;
                }
            for (int i = 0; i < VertexCount; i++)
                cpuNrm[i] = cpuNrm[i].sqrMagnitude > 1e-12f ? cpuNrm[i].normalized : Vector3.up;
        }

        void Dispatch(int threads)
        {
            if (threads <= 0) return;
            int groups = Mathf.CeilToInt(threads / 64f);
            int gx = groups <= MaxGroupsPerDim ? groups : Mathf.Min(MaxGroupsPerDim, Mathf.CeilToInt(Mathf.Sqrt(groups)));
            int gy = Mathf.CeilToInt((float)groups / gx);
            extractCS.SetInt("_RowThreads", gx * 64);
            extractCS.Dispatch(kExtract, gx, gy, 1);
        }

        // ── buffer→world affine fit (least squares, translation-only fallback) ─────────
        static Matrix4x4 SolveBufferToWorld(Unity.Collections.NativeArray<Vector3> buffer, Vector3[] local, Matrix4x4 l2w, int n)
        {
            int step = Mathf.Max(1, n / 4000);
            var A = new double[4, 4];
            double[] cx = new double[4], cy = new double[4], cz = new double[4];
            Vector3 meanB = Vector3.zero, meanW = Vector3.zero; int cnt = 0;
            for (int i = 0; i < n; i += step)
            {
                Vector3 b = buffer[i], w = l2w.MultiplyPoint3x4(local[i]);
                double[] bb = { b.x, b.y, b.z, 1.0 };
                for (int r = 0; r < 4; r++)
                {
                    for (int c = 0; c < 4; c++) A[r, c] += bb[r] * bb[c];
                    cx[r] += w.x * bb[r]; cy[r] += w.y * bb[r]; cz[r] += w.z * bb[r];
                }
                meanB += b; meanW += w; cnt++;
            }
            if (Solve4x3(A, cx, cy, cz, out var mx, out var my, out var mz))
            {
                var M = Matrix4x4.identity;
                M.SetRow(0, new Vector4((float)mx[0], (float)mx[1], (float)mx[2], (float)mx[3]));
                M.SetRow(1, new Vector4((float)my[0], (float)my[1], (float)my[2], (float)my[3]));
                M.SetRow(2, new Vector4((float)mz[0], (float)mz[1], (float)mz[2], (float)mz[3]));
                return M;
            }
            return Matrix4x4.Translate(cnt > 0 ? (meanW - meanB) / cnt : Vector3.zero);
        }

        static bool Solve4x3(double[,] A, double[] cx, double[] cy, double[] cz,
                             out double[] sx, out double[] sy, out double[] sz)
        {
            var m = new double[4, 7];
            for (int r = 0; r < 4; r++)
            {
                for (int c = 0; c < 4; c++) m[r, c] = A[r, c];
                m[r, 4] = cx[r]; m[r, 5] = cy[r]; m[r, 6] = cz[r];
            }
            for (int col = 0; col < 4; col++)
            {
                int piv = col; double best = System.Math.Abs(m[col, col]);
                for (int r = col + 1; r < 4; r++) { double v = System.Math.Abs(m[r, col]); if (v > best) { best = v; piv = r; } }
                if (best < 1e-9) { sx = sy = sz = null; return false; }
                if (piv != col) for (int c = 0; c < 7; c++) (m[col, c], m[piv, c]) = (m[piv, c], m[col, c]);
                double d = m[col, col];
                for (int c = 0; c < 7; c++) m[col, c] /= d;
                for (int r = 0; r < 4; r++)
                {
                    if (r == col) continue;
                    double f = m[r, col];
                    for (int c = 0; c < 7; c++) m[r, c] -= f * m[col, c];
                }
            }
            sx = new[] { m[0, 4], m[1, 4], m[2, 4], m[3, 4] };
            sy = new[] { m[0, 5], m[1, 5], m[2, 5], m[3, 5] };
            sz = new[] { m[0, 6], m[1, 6], m[2, 6], m[3, 6] };
            return true;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            resourceGeneration++;
            calibInFlight = false;
            pendingSrc?.Dispose(); pendingSrc = null;
            posBuf?.Release(); posBuf = null;
            nrmBuf?.Release(); nrmBuf = null;
            idxBuf?.Release(); idxBuf = null;
            calibPosBuf?.Release(); calibPosBuf = null;
            if (bakeMesh != null)
            {
                if (Application.isPlaying) Object.Destroy(bakeMesh); else Object.DestroyImmediate(bakeMesh);
                bakeMesh = null;
            }
            calibLocal = null;
            cpuPos = null; cpuNrm = null; TrianglesCPU = null;
            ExtractedOnce = false; TopologyVersion = 0;
        }
    }
}
