using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace BrazeFX
{
    /// <summary>
    /// Allocation-free-after-warmup projectile helper for BrazeFractureImpactSource prefabs. The spawner owns one
    /// central movement loop, so pooled bullets need no Rigidbody, Collider, or per-instance Update component.
    /// Gameplay code may call Fire; Auto Fire is useful for previews and simple weapons.
    /// </summary>
    [AddComponentMenu("BrazeFX/Helpers/Fracture Bullet Spawner", 62)]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(0)] // BrazeFractureImpactDriver runs at 100 and sweeps after these transforms move.
    public sealed class BrazeFractureBulletSpawner : MonoBehaviour
    {
        [Header("PREFAB")]
        [Tooltip("Projectile prefab containing a Braze Fracture Impact Source on its root or a child.")]
        public GameObject bulletPrefab;
        [Tooltip("Optional muzzle. Empty uses this component's Transform.")]
        public Transform muzzle;

        [Header("FIRING")]
        [Tooltip("Continuously fire at Rounds / Second while this component is enabled.")]
        public bool autoFire;
        [Min(0.01f)] public float roundsPerSecond = 8f;
        [Min(0f)] [Tooltip("Projectile speed in world units per second.")]
        public float speed = 60f;
        [Range(0f, 45f)] [Tooltip("Random cone half-angle in degrees.")]
        public float spread = 0f;
        [Tooltip("Aim the projectile prefab's forward axis along its travel direction.")]
        public bool alignToVelocity = true;

        [Header("SURFACE AIM")]
        [Tooltip("Choose a new random point on Target Mesh for every shot.")]
        public bool aimAtMesh;
        [Tooltip("Scene mesh whose triangle surface receives automatic fire. The Mesh Filter provides its world transform.")]
        public MeshFilter targetMesh;
        [Tooltip("Smoothly rotate the assigned Muzzle toward a held random surface point. Empty Muzzle rotates this component's Transform.")]
        public bool aimMuzzle = true;
        [Min(0f)] [Tooltip("Maximum muzzle rotation speed in degrees per second.")]
        public float muzzleAimSpeed = 240f;
        [Min(0.02f)] [Tooltip("Seconds before choosing another random point on the target surface.")]
        public float aimRetargetInterval = 0.4f;

        [Header("LIFECYCLE")]
        [Min(0.01f)] [Tooltip("Seconds before a missed projectile returns to the pool.")]
        public float lifetime = 5f;
        [Tooltip("Return a projectile to the pool as soon as its impact source submits any accepted hit.")]
        public bool recycleOnImpact = true;

        [Header("POOL")]
        [Min(1)] public int poolSize = 24;
        [Tooltip("Create another projectile when every pooled instance is busy.")]
        public bool autoExpand = true;
        [Min(1)] public int maxPoolSize = 128;

        sealed class Bullet
        {
            public GameObject gameObject;
            public Transform transform;
            public BrazeFractureImpactSource impact;
            public BrazeRibbonRenderer[] ribbons;
            public TrailRenderer[] trails;
            public Vector3 velocity;
            public float age;
            public float recycleAt;
            public uint impactVersion;
            public bool active;
            public bool retiring;
        }

        readonly List<Bullet> bullets = new();
        Transform poolRoot;
        GameObject pooledPrefab;
        float nextShotTime;
        bool prefabErrorLogged;
        uint firedCount;
        uint acceptedImpactCount;
        uint lifetimeRecycleCount;
        uint capacityRecycleCount;
        MeshFilter cachedTargetMesh;
        Mesh cachedAimMesh;
        Vector3[] cachedAimVertices;
        int[] cachedAimIndices;
        float[] cachedAimAreas;
        float cachedAimTotalArea;
        [SerializeField, HideInInspector] Mesh bakedAimMesh;
        [SerializeField, HideInInspector] Vector3[] bakedAimVertices;
        [SerializeField, HideInInspector] int[] bakedAimIndices;
        [SerializeField, HideInInspector] float[] bakedAimAreas;
        [SerializeField, HideInInspector] float bakedAimTotalArea;
        MeshFilter heldAimMesh;
        Vector3 heldAimLocalPoint;
        float nextAimRetargetTime;
        bool hasHeldAimPoint;

        public int ActiveBulletCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < bullets.Count; i++) if (bullets[i].active) count++;
                return count;
            }
        }

        public int PoolCount => bullets.Count;
        public uint FiredCount => firedCount;
        public uint AcceptedImpactCount => acceptedImpactCount;
        public uint LifetimeRecycleCount => lifetimeRecycleCount;
        public uint CapacityRecycleCount => capacityRecycleCount;
        public int AimTriangleCount => bakedAimAreas != null ? bakedAimAreas.Length : 0;

        void OnEnable()
        {
            nextShotTime = Time.time;
            hasHeldAimPoint = false;
            heldAimMesh = null;
        }

        void Start()
        {
            EnsurePool();
            UpdateMuzzleAim(0f);
        }

        void Update()
        {
            float dt = Mathf.Max(0f, Time.deltaTime);
            UpdateMuzzleAim(dt);
            for (int i = bullets.Count - 1; i >= 0; i--)
            {
                Bullet bullet = bullets[i];
                if (bullet.gameObject == null)
                {
                    bullets.RemoveAt(i);
                    continue;
                }
                if (!bullet.active) continue;
                if (!bullet.gameObject.activeSelf)
                {
                    bullet.active = false;
                    bullet.retiring = false;
                    continue;
                }

                if (bullet.retiring)
                {
                    // Keep ageing while the ribbon fades. A frozen age made spent projectiles look "younger" than
                    // live ones, so capacity eviction preferred bullets still in flight.
                    bullet.age += dt;
                    if (Time.time >= bullet.recycleAt) Recycle(bullet);
                    continue;
                }

                if (recycleOnImpact && bullet.impact.AcceptedImpactVersion != bullet.impactVersion)
                {
                    acceptedImpactCount++;
                    float visualLinger = VisualLinger(bullet);
                    if (visualLinger <= 0f) Recycle(bullet);
                    else
                    {
                        // Stop submitting impacts immediately, but leave the GameObject alive long enough for its
                        // authored trail history to fade. Disabling the whole projectile here erased fast shots.
                        bullet.impact.enabled = false;
                        bullet.velocity = Vector3.zero;
                        bullet.retiring = true;
                        bullet.recycleAt = Time.time + visualLinger;
                        StopEmitting(bullet);
                    }
                    continue;
                }

                if ((bullet.age += dt) >= Mathf.Max(0.01f, lifetime))
                {
                    lifetimeRecycleCount++;
                    Recycle(bullet);
                    continue;
                }

                bullet.transform.position += bullet.velocity * dt;
                if (alignToVelocity && bullet.velocity.sqrMagnitude > 1e-8f)
                    bullet.transform.rotation = Quaternion.LookRotation(bullet.velocity.normalized, Vector3.up);
            }

            if (!autoFire || bulletPrefab == null || Time.time < nextShotTime) return;
            Fire();
            nextShotTime = Time.time + 1f / Mathf.Max(0.01f, roundsPerSecond);
        }

        void OnDisable()
        {
            for (int i = 0; i < bullets.Count; i++)
                if (bullets[i].active) Recycle(bullets[i]);
        }

        void OnDestroy()
        {
            for (int i = 0; i < bullets.Count; i++)
            {
                GameObject projectile = bullets[i].gameObject;
                if (projectile != null) Destroy(projectile);
            }
            bullets.Clear();
            if (poolRoot != null) Destroy(poolRoot.gameObject);
        }

        /// <summary>Spawn one projectile from the configured muzzle.</summary>
        public GameObject Fire()
        {
            Transform origin = muzzle != null ? muzzle : transform;
            Vector3 forward = origin.forward.sqrMagnitude > 1e-8f ? origin.forward.normalized : Vector3.forward;
            if (aimAtMesh && !aimMuzzle && TryGetRandomAimPoint(out Vector3 targetPoint))
            {
                Vector3 toTarget = targetPoint - origin.position;
                if (toTarget.sqrMagnitude > 1e-8f) forward = toTarget.normalized;
            }
            return Fire(origin.position, ApplySpread(forward));
        }

        /// <summary>Spawn one projectile from an explicit world-space pose.</summary>
        public GameObject Fire(Vector3 position, Vector3 direction)
        {
            if (!Application.isPlaying || bulletPrefab == null || !EnsurePool()) return null;
            Bullet bullet = Rent();
            if (bullet == null) return null;

            Vector3 travel = direction.sqrMagnitude > 1e-8f ? direction.normalized : transform.forward;
            // worldPositionStays must be false on BOTH reparents. Unparenting with true baked the pool root's lossy
            // scale into localScale, which Recycle's SetParent(poolRoot, false) then kept — compounding the
            // projectile's scale by the spawner's on every reuse. Keeping local values fixed pins it to the prefab's.
            bullet.transform.SetParent(null, false);
            bullet.transform.SetPositionAndRotation(position,
                alignToVelocity ? Quaternion.LookRotation(travel, Vector3.up) : bulletPrefab.transform.rotation);
            bullet.velocity = travel * Mathf.Max(0f, speed);
            bullet.age = 0f;
            bullet.recycleAt = 0f;
            bullet.active = true;
            bullet.retiring = false;
            bullet.impact.enabled = true;
            bullet.gameObject.SetActive(true);
            bullet.impact.activation = BrazeFractureImpactSource.ActivationMode.TransformSweep;
            bullet.impact.Rearm();
            bullet.impactVersion = bullet.impact.AcceptedImpactVersion;
            for (int i = 0; i < bullet.ribbons.Length; i++)
                if (bullet.ribbons[i] != null) bullet.ribbons[i].ResetHistory();
            // A Unity TrailRenderer keeps its world-space point history across SetActive, so a reused projectile
            // would otherwise streak a line from wherever it died back to the muzzle. Clear after the transform is
            // in place and the object is live, or the trail re-seeds from the stale pose.
            for (int i = 0; i < bullet.trails.Length; i++)
            {
                TrailRenderer trail = bullet.trails[i];
                if (trail == null) continue;
                trail.Clear();
                trail.emitting = true;
            }
            firedCount++;
            return bullet.gameObject;
        }

        /// <summary>Pre-create the configured pool. Safe to call repeatedly.</summary>
        public void WarmPool() => EnsurePool();

        Vector3 ApplySpread(Vector3 forward)
        {
            forward = forward.sqrMagnitude > 1e-8f ? forward.normalized : Vector3.forward;
            if (spread <= 0f) return forward;
            Vector3 reference = Mathf.Abs(forward.y) < 0.99f ? Vector3.up : Vector3.right;
            Vector3 right = Vector3.Cross(reference, forward).normalized;
            Vector3 up = Vector3.Cross(forward, right).normalized;
            Vector2 disc = Random.insideUnitCircle * Mathf.Tan(Mathf.Deg2Rad * Mathf.Clamp(spread, 0f, 89f));
            return (forward + right * disc.x + up * disc.y).normalized;
        }

        void UpdateMuzzleAim(float dt)
        {
            if (!aimAtMesh || !aimMuzzle || targetMesh == null)
            {
                hasHeldAimPoint = false;
                heldAimMesh = null;
                return;
            }

            if (heldAimMesh != targetMesh) hasHeldAimPoint = false;
            if ((!hasHeldAimPoint || Time.time >= nextAimRetargetTime) && !RetargetMuzzle()) return;

            Transform origin = muzzle != null ? muzzle : transform;
            Vector3 targetPoint = heldAimMesh.transform.TransformPoint(heldAimLocalPoint);
            Vector3 direction = targetPoint - origin.position;
            if (direction.sqrMagnitude <= 1e-8f) return;
            direction.Normalize();
            // Reference the spawner's up, not the muzzle's own. Feeding origin.up into the LookRotation that then
            // overwrites origin.rotation is a self-referential loop: roll is only ever defined by last frame's roll,
            // so it drifts and anything parented to the muzzle (a barrel model) slowly barrel-rolls.
            Vector3 up = transform.up;
            if (Mathf.Abs(Vector3.Dot(direction, up)) > 0.999f) up = transform.right;
            Quaternion targetRotation = Quaternion.LookRotation(direction, up);
            origin.rotation = Quaternion.RotateTowards(
                origin.rotation, targetRotation, Mathf.Max(0f, muzzleAimSpeed) * dt);
        }

        bool RetargetMuzzle()
        {
            if (!TryGetRandomAimPoint(out Vector3 worldPoint))
            {
                hasHeldAimPoint = false;
                heldAimMesh = null;
                return false;
            }

            heldAimMesh = targetMesh;
            heldAimLocalPoint = heldAimMesh.transform.InverseTransformPoint(worldPoint);
            nextAimRetargetTime = Time.time + Mathf.Max(0.02f, aimRetargetInterval);
            hasHeldAimPoint = true;
            return true;
        }

        /// <summary>Pick an area-weighted random point on the configured target mesh in world space.</summary>
        public bool TryGetRandomAimPoint(out Vector3 worldPoint)
        {
            worldPoint = default;
            if (!EnsureAimSurface()) return false;

            float pick = Random.value * cachedAimTotalArea;
            int lo = 0;
            int hi = cachedAimAreas.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (pick <= cachedAimAreas[mid]) hi = mid;
                else lo = mid + 1;
            }

            int triangle = lo * 3;
            Vector3 a = cachedAimVertices[cachedAimIndices[triangle]];
            Vector3 b = cachedAimVertices[cachedAimIndices[triangle + 1]];
            Vector3 c = cachedAimVertices[cachedAimIndices[triangle + 2]];
            float root = Mathf.Sqrt(Random.value);
            float v = Random.value;
            Vector3 localPoint = a * (1f - root) + b * (root * (1f - v)) + c * (root * v);
            worldPoint = targetMesh.transform.TransformPoint(localPoint);
            return true;
        }

        bool EnsureAimSurface()
        {
            Mesh mesh = targetMesh != null ? targetMesh.sharedMesh : null;
            if (targetMesh == cachedTargetMesh && mesh == cachedAimMesh)
                return cachedAimAreas != null && cachedAimAreas.Length > 0 && cachedAimTotalArea > 0f;

            ClearAimSurface();
            cachedTargetMesh = targetMesh;
            cachedAimMesh = mesh;
            if (mesh == null) return false;

            if (bakedAimMesh == mesh && bakedAimVertices != null && bakedAimIndices != null
                && bakedAimAreas != null && bakedAimIndices.Length == bakedAimAreas.Length * 3
                && bakedAimTotalArea > 0f)
            {
                cachedAimVertices = bakedAimVertices;
                cachedAimIndices = bakedAimIndices;
                cachedAimAreas = bakedAimAreas;
                cachedAimTotalArea = bakedAimTotalArea;
                return true;
            }

            if (!mesh.isReadable) return false;
            using Mesh.MeshDataArray meshDataArray = Mesh.AcquireReadOnlyMeshData(mesh);
            return BuildAimSurface(meshDataArray[0], out cachedAimVertices, out cachedAimIndices,
                out cachedAimAreas, out cachedAimTotalArea);
        }

        internal static bool BuildAimSurface(Mesh.MeshData meshData, out Vector3[] vertices,
                                             out int[] triangleIndices, out float[] cumulativeAreas,
                                             out float totalArea)
        {
            using var vertexData = new NativeArray<Vector3>(meshData.vertexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            meshData.GetVertices(vertexData);
            vertices = vertexData.ToArray();
            var indicesOut = new List<int>();
            var areas = new List<float>();
            totalArea = 0f;
            for (int submesh = 0; submesh < meshData.subMeshCount; submesh++)
            {
                SubMeshDescriptor descriptor = meshData.GetSubMesh(submesh);
                if (descriptor.topology != MeshTopology.Triangles || descriptor.indexCount < 3) continue;
                using var indices = new NativeArray<int>(descriptor.indexCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                meshData.GetIndices(indices, submesh, true);
                for (int i = 0; i + 2 < indices.Length; i += 3)
                {
                    int ia = indices[i];
                    int ib = indices[i + 1];
                    int ic = indices[i + 2];
                    if ((uint)ia >= (uint)vertices.Length || (uint)ib >= (uint)vertices.Length
                        || (uint)ic >= (uint)vertices.Length) continue;
                    Vector3 a = vertices[ia];
                    Vector3 b = vertices[ib];
                    Vector3 c = vertices[ic];
                    float area = Vector3.Cross(b - a, c - a).magnitude * 0.5f;
                    if (!(area > 1e-10f) || float.IsInfinity(area) || float.IsNaN(area)) continue;
                    totalArea += area;
                    indicesOut.Add(ia);
                    indicesOut.Add(ib);
                    indicesOut.Add(ic);
                    areas.Add(totalArea);
                }
            }

            triangleIndices = indicesOut.ToArray();
            cumulativeAreas = areas.ToArray();
            return cumulativeAreas.Length > 0 && totalArea > 0f;
        }

        internal bool AimSurfaceBakeMatches(Mesh mesh)
            => mesh == null ? bakedAimMesh == null : bakedAimMesh == mesh && bakedAimVertices != null
               && bakedAimIndices != null && bakedAimAreas != null
               && bakedAimIndices.Length == bakedAimAreas.Length * 3;

        internal void SetAimSurfaceBake(Mesh mesh, Vector3[] vertices, int[] triangleIndices,
                                        float[] cumulativeAreas, float totalArea)
        {
            bakedAimMesh = mesh;
            bakedAimVertices = vertices;
            bakedAimIndices = triangleIndices;
            bakedAimAreas = cumulativeAreas;
            bakedAimTotalArea = totalArea;
            ClearAimSurface();
        }

        void ClearAimSurface()
        {
            cachedTargetMesh = null;
            cachedAimMesh = null;
            cachedAimVertices = null;
            cachedAimIndices = null;
            cachedAimAreas = null;
            cachedAimTotalArea = 0f;
        }

        bool EnsurePool()
        {
            if (!Application.isPlaying || bulletPrefab == null) return false;
            if (pooledPrefab != bulletPrefab)
            {
                ClearPool();
                pooledPrefab = bulletPrefab;
                prefabErrorLogged = false;
            }
            PruneDestroyed();
            EnsurePoolRoot();
            int desired = Mathf.Clamp(poolSize, 1, Mathf.Max(1, maxPoolSize));
            while (bullets.Count < desired)
                if (CreateBullet() == null) return false;
            return bullets.Count > 0;
        }

        void EnsurePoolRoot()
        {
            if (poolRoot != null) return;
            var root = new GameObject($"{name} — Bullet Pool") { hideFlags = HideFlags.HideInHierarchy };
            poolRoot = root.transform;
            poolRoot.SetParent(transform, false);
        }

        Bullet CreateBullet()
        {
            if (bulletPrefab == null || bullets.Count >= Mathf.Max(1, maxPoolSize)) return null;
            GameObject instance = Instantiate(bulletPrefab, poolRoot);
            instance.name = $"{bulletPrefab.name} [{bullets.Count}]";
            BrazeFractureImpactSource impact = instance.GetComponentInChildren<BrazeFractureImpactSource>(true);
            if (impact == null)
            {
                if (!prefabErrorLogged)
                {
                    prefabErrorLogged = true;
                    Debug.LogError("[BrazeFX] Fracture Bullet Spawner prefab requires a Braze Fracture Impact Source.", this);
                }
                Destroy(instance);
                return null;
            }
            instance.SetActive(false);
            var bullet = new Bullet
            {
                gameObject = instance,
                transform = instance.transform,
                impact = impact,
                ribbons = instance.GetComponentsInChildren<BrazeRibbonRenderer>(true),
                trails = instance.GetComponentsInChildren<TrailRenderer>(true),
            };
            bullets.Add(bullet);
            return bullet;
        }

        Bullet Rent()
        {
            PruneDestroyed();
            for (int i = 0; i < bullets.Count; i++)
                if (!bullets[i].active) return bullets[i];

            if (autoExpand && bullets.Count < Mathf.Max(1, maxPoolSize))
                return CreateBullet();

            // A capacity limit should cap memory, not silently stop the weapon. When every projectile is busy,
            // reuse the oldest one. This also makes deliberately small fixed pools useful for rapid-fire weapons.
            // Spent projectiles lingering for their ribbon fade go first: they have already delivered their impact,
            // so cutting one short only clips a trail, while evicting a live round cancels a hit that never landed.
            Bullet oldest = null;
            for (int i = 0; i < bullets.Count; i++)
            {
                Bullet candidate = bullets[i];
                if (oldest == null
                    || (candidate.retiring && !oldest.retiring)
                    || (candidate.retiring == oldest.retiring && candidate.age > oldest.age))
                    oldest = candidate;
            }
            if (oldest != null)
            {
                capacityRecycleCount++;
                Recycle(oldest);
            }
            return oldest;
        }

        /// <summary>Seconds a spent projectile must stay alive for its authored trail history to fade out.</summary>
        static float VisualLinger(Bullet bullet)
        {
            float linger = 0f;
            for (int i = 0; i < bullet.ribbons.Length; i++)
            {
                BrazeRibbonRenderer ribbon = bullet.ribbons[i];
                if (ribbon != null && ribbon.enabled)
                    linger = Mathf.Max(linger, Mathf.Max(0f, ribbon.ribbonTime));
            }
            for (int i = 0; i < bullet.trails.Length; i++)
            {
                TrailRenderer trail = bullet.trails[i];
                if (trail != null && trail.enabled)
                    linger = Mathf.Max(linger, Mathf.Max(0f, trail.time));
            }
            return linger;
        }

        static void StopEmitting(Bullet bullet)
        {
            for (int i = 0; i < bullet.trails.Length; i++)
                if (bullet.trails[i] != null) bullet.trails[i].emitting = false;
        }

        void PruneDestroyed()
        {
            for (int i = bullets.Count - 1; i >= 0; i--)
                if (bullets[i].gameObject == null) bullets.RemoveAt(i);
        }

        void Recycle(Bullet bullet)
        {
            if (bullet == null) return;
            bullet.active = false;
            bullet.velocity = Vector3.zero;
            bullet.age = 0f;
            bullet.recycleAt = 0f;
            bullet.retiring = false;
            if (bullet.gameObject == null) return;
            bullet.gameObject.SetActive(false);
            if (poolRoot != null) bullet.transform.SetParent(poolRoot, false);
        }

        void ClearPool()
        {
            for (int i = 0; i < bullets.Count; i++)
                if (bullets[i].gameObject != null) Destroy(bullets[i].gameObject);
            bullets.Clear();
            if (poolRoot != null) Destroy(poolRoot.gameObject);
            poolRoot = null;
        }

        void OnValidate()
        {
            roundsPerSecond = Mathf.Max(0.01f, roundsPerSecond);
            speed = Mathf.Max(0f, speed);
            spread = Mathf.Clamp(spread, 0f, 45f);
            muzzleAimSpeed = Mathf.Max(0f, muzzleAimSpeed);
            aimRetargetInterval = Mathf.Max(0.02f, aimRetargetInterval);
            lifetime = Mathf.Max(0.01f, lifetime);
            poolSize = Mathf.Max(1, poolSize);
            maxPoolSize = Mathf.Max(poolSize, maxPoolSize);
            ClearAimSurface();
        }

        void OnDrawGizmosSelected()
        {
            Transform origin = muzzle != null ? muzzle : transform;
            float length = Mathf.Max(0.5f, speed * 0.08f);
            Gizmos.color = new Color(1f, 0.32f, 0.08f, 0.9f);
            if (aimAtMesh && targetMesh != null && targetMesh.sharedMesh != null)
            {
                Mesh mesh = targetMesh.sharedMesh;
                Vector3 aimPoint = hasHeldAimPoint && heldAimMesh == targetMesh
                    ? targetMesh.transform.TransformPoint(heldAimLocalPoint)
                    : targetMesh.transform.TransformPoint(mesh.bounds.center);
                Gizmos.DrawLine(origin.position, aimPoint);
                Gizmos.DrawSphere(aimPoint, Mathf.Max(0.015f, length * 0.025f));
                Matrix4x4 previous = Gizmos.matrix;
                Gizmos.matrix = targetMesh.transform.localToWorldMatrix;
                Gizmos.DrawWireCube(mesh.bounds.center, mesh.bounds.size);
                Gizmos.matrix = previous;
            }
            else Gizmos.DrawLine(origin.position, origin.position + origin.forward * length);
        }
    }
}
