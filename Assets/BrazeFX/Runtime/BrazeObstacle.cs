using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>
    /// A solid obstacle the smoke flows around. Place it ANYWHERE in the scene — it registers itself
    /// and every <see cref="BrazeContainer"/> voxelizes it into its solid mask by world position.
    /// </summary>
    [AddComponentMenu("BrazeFX/Fire/Braze Obstacle", 2)]
    [ExecuteAlways]   // self-register in edit mode too, so the timeline's edit-mode sim sees it
    public sealed class BrazeObstacle : MonoBehaviour
    {
        /// <summary>All enabled obstacles in the scene (registry — no hierarchy constraint).</summary>
        public static readonly List<BrazeObstacle> Active = new();

        // Values are the GPU shape codes. Keep existing Sphere/Box values stable for serialized scenes and caches.
        public enum Shape
        {
            Sphere = 0,
            Box = 1,
            Cylinder = 2,
            Cone = 3,
            Capsule = 4,
            Torus = 5,
        }

        public Shape shape = Shape.Sphere;
        [Tooltip("Build the collision from Unity Sphere, Box and Capsule Colliders on this object + children " +
                 "(the standard compound-collider workflow). Primitives union automatically.")]
        public bool useColliders;
        [Tooltip("Sphere/Capsule/Cylinder/Cone radius, or the Torus major radius, in world units before Transform scale.")]
        [Min(0f), BrazeAnimatable("SHAPE/Radius", 0f, 30f)]
        public float radius = 0.2f;
        [Tooltip("Full Capsule/Cylinder/Cone height in world units before Transform scale. Capsule height includes its caps.")]
        [Min(0f), BrazeAnimatable("SHAPE/Height", 0f, 60f)]
        public float height = 1f;
        [Tooltip("Torus tube radius in world units before Transform scale.")]
        [Min(0f), BrazeAnimatable("SHAPE/Tube Radius", 0f, 30f)]
        public float tubeRadius = 0.08f;
        [Tooltip("Draw the collision shape in the Scene view. Off = no gizmo at all (even when selected).")]
        public bool showGizmo = true;

        /// <summary>One voxelizable primitive in world space.</summary>
        public struct Prim
        {
            public Shape shape;
            public Vector3 center;
            public float radius;     // sphere/radial radius; torus major radius
            public Vector3 half;     // box half-extents; axial half-height in y; torus minor radius in x
            public Quaternion rot;   // local +Y is the axial direction for capsule/cylinder/cone/torus

            /// <summary>Legacy source compatibility for callers that only distinguished sphere and box.</summary>
            public bool box
            {
                get => shape == Shape.Box;
                set
                {
                    if (value) shape = Shape.Box;
                    else if (shape == Shape.Box) shape = Shape.Sphere;
                }
            }
        }

        static readonly List<Collider> Cols = new();

        void OnEnable() => BrazeRuntimeRegistry.AddUnique(Active, this);
        void OnDisable() => BrazeRuntimeRegistry.Remove(Active, this);

        void OnValidate()
        {
            radius = Mathf.Max(0f, radius);
            height = Mathf.Max(0f, height);
            tubeRadius = Mathf.Max(0f, tubeRadius);
        }

        /// <summary>Emit this obstacle's primitives — a compound of colliders, or one native shape.</summary>
        public void CollectPrimitives(List<Prim> outList)
        {
            if (outList == null) return;
            if (useColliders)
            {
                GetComponentsInChildren(false, Cols);
                bool any = false;
                for (int i = 0; i < Cols.Count; i++)
                {
                    Collider col = Cols[i];
                    if (col == null || !col.enabled) continue;
                    if (col is SphereCollider sphereCollider)
                    {
                        Bounds b = sphereCollider.bounds; // rotation-invariant; conservatively handles nonuniform scale
                        outList.Add(new Prim
                        {
                            shape = Shape.Sphere,
                            center = b.center,
                            radius = MaxComponent(b.extents),
                            rot = Quaternion.identity,
                        });
                        any = true;
                    }
                    else if (col is BoxCollider boxCollider)
                    {
                        Transform ct = boxCollider.transform;
                        outList.Add(new Prim
                        {
                            shape = Shape.Box,
                            center = ct.TransformPoint(boxCollider.center),
                            half = Vector3.Scale(boxCollider.size * 0.5f, AbsVec(ct.lossyScale)),
                            rot = ct.rotation,
                        });
                        any = true;
                    }
                    else if (col is CapsuleCollider capsuleCollider)
                    {
                        Transform ct = capsuleCollider.transform;
                        Vector3 scale = AbsVec(ct.lossyScale);
                        int direction = Mathf.Clamp(capsuleCollider.direction, 0, 2);
                        float axialScale = Component(scale, direction);
                        float radialScale = direction == 0 ? Mathf.Max(scale.y, scale.z)
                                          : direction == 1 ? Mathf.Max(scale.x, scale.z)
                                                           : Mathf.Max(scale.x, scale.y);
                        float worldRadius = Mathf.Max(0f, capsuleCollider.radius * radialScale);
                        float worldHalfHeight = Mathf.Max(capsuleCollider.height * axialScale * 0.5f, worldRadius);
                        Vector3 axis = direction == 0 ? Vector3.right
                                     : direction == 1 ? Vector3.up : Vector3.forward;
                        outList.Add(new Prim
                        {
                            shape = Shape.Capsule,
                            center = ct.TransformPoint(capsuleCollider.center),
                            radius = worldRadius,
                            half = new Vector3(worldRadius, worldHalfHeight, worldRadius),
                            rot = ct.rotation * Quaternion.FromToRotation(Vector3.up, axis),
                        });
                        any = true;
                    }
                }
                if (any) return; // fall through only when no supported enabled collider exists
            }

            outList.Add(SinglePrimitive());
        }

        Prim SinglePrimitive()
        {
            if (MeshWorldBounds(out Bounds bounds))
            {
                Vector3 h = bounds.extents;
                float radial = Mathf.Max(h.x, h.z);
                switch (shape)
                {
                    case Shape.Box:
                        return new Prim { shape = shape, center = bounds.center, half = h, rot = Quaternion.identity };
                    case Shape.Capsule:
                        return new Prim
                        {
                            shape = shape, center = bounds.center, radius = radial,
                            half = new Vector3(radial, Mathf.Max(h.y, radial), radial), rot = Quaternion.identity,
                        };
                    case Shape.Cylinder:
                    case Shape.Cone:
                        return new Prim
                        {
                            shape = shape, center = bounds.center, radius = radial,
                            half = new Vector3(radial, h.y, radial), rot = Quaternion.identity,
                        };
                    case Shape.Torus:
                    {
                        float minor = Mathf.Min(h.y, radial);
                        return new Prim
                        {
                            shape = shape, center = bounds.center, radius = Mathf.Max(0f, radial - minor),
                            half = new Vector3(minor, minor, minor), rot = Quaternion.identity,
                        };
                    }
                    default:
                        return new Prim
                        {
                            shape = Shape.Sphere, center = bounds.center,
                            radius = MaxComponent(h), rot = Quaternion.identity,
                        };
                }
            }

            Vector3 s = AbsLossy();
            float radialScale = Mathf.Max(s.x, s.z);
            float worldRadius = Mathf.Max(0f, radius) * radialScale;
            float halfHeight = Mathf.Max(0f, height) * s.y * 0.5f;
            Prim p = new Prim { shape = shape, center = transform.position, rot = transform.rotation };
            switch (shape)
            {
                case Shape.Box:
                    p.half = s * 0.5f;
                    break;
                case Shape.Capsule:
                    p.radius = worldRadius;
                    p.half = new Vector3(worldRadius, Mathf.Max(halfHeight, worldRadius), worldRadius);
                    break;
                case Shape.Cylinder:
                case Shape.Cone:
                    p.radius = worldRadius;
                    p.half = new Vector3(worldRadius, halfHeight, worldRadius);
                    break;
                case Shape.Torus:
                {
                    float minor = Mathf.Max(0f, tubeRadius) * Mathf.Max(radialScale, s.y);
                    p.radius = worldRadius;
                    p.half = new Vector3(minor, minor, minor);
                    break;
                }
                default:
                    p.shape = Shape.Sphere;
                    p.radius = Mathf.Max(0f, radius) * MaxComponent(s);
                    p.rot = Quaternion.identity;
                    break;
            }
            return p;
        }

        static float Component(Vector3 v, int axis) => axis == 0 ? v.x : axis == 1 ? v.y : v.z;
        static float MaxComponent(Vector3 v) => Mathf.Max(v.x, Mathf.Max(v.y, v.z));
        static Vector3 AbsVec(Vector3 s) => new(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
        Vector3 AbsLossy() => AbsVec(transform.lossyScale);

        // World-space mesh AABB if this object has a mesh (Renderer.bounds is already world; else MeshFilter).
        bool MeshWorldBounds(out Bounds world)
        {
            if (TryGetComponent(out Renderer rend)) { world = rend.bounds; return true; }
            if (TryGetComponent(out MeshFilter mf) && mf.sharedMesh != null)
            {
                Bounds lb = mf.sharedMesh.bounds;
                world = new Bounds(transform.TransformPoint(lb.center), Vector3.Scale(lb.size, AbsLossy()));
                return true;
            }
            world = default;
            return false;
        }

        /// <summary>World-space centre of the collision shape (mesh centre when fitting).</summary>
        public Vector3 WorldCenter() => SinglePrimitive().center;

        /// <summary>Conservative world-axis half-extents of the selected primitive.</summary>
        public Vector3 WorldHalfExtents()
        {
            Prim p = SinglePrimitive();
            Vector3 local = LocalBounds(p);
            if (p.shape == Shape.Sphere) return local;
            Matrix4x4 m = Matrix4x4.Rotate(p.rot);
            return new Vector3(
                Mathf.Abs(m.m00) * local.x + Mathf.Abs(m.m01) * local.y + Mathf.Abs(m.m02) * local.z,
                Mathf.Abs(m.m10) * local.x + Mathf.Abs(m.m11) * local.y + Mathf.Abs(m.m12) * local.z,
                Mathf.Abs(m.m20) * local.x + Mathf.Abs(m.m21) * local.y + Mathf.Abs(m.m22) * local.z);
        }

        /// <summary>Sphere radius, or the largest world-axis extent for the other primitives.</summary>
        public float WorldRadius()
        {
            Prim p = SinglePrimitive();
            return p.shape == Shape.Sphere ? p.radius : MaxComponent(WorldHalfExtents());
        }

        static Vector3 LocalBounds(Prim p)
        {
            switch (p.shape)
            {
                case Shape.Box: return p.half;
                case Shape.Capsule: return new Vector3(p.radius, Mathf.Max(p.half.y, p.radius), p.radius);
                case Shape.Cylinder:
                case Shape.Cone: return new Vector3(p.radius, p.half.y, p.radius);
                case Shape.Torus:
                {
                    float minor = p.half.x;
                    float outer = p.radius + minor;
                    return new Vector3(outer, minor, outer);
                }
                default: return Vector3.one * p.radius;
            }
        }

        static readonly List<Prim> GizmoPrims = new();
        static readonly Color GizmoTint = new(0.42f, 0.70f, 1f); // cool blue (collision)
        const int GizmoSegments = 20;

        void DrawShape(float fillAlpha, float wireAlpha)
        {
            GizmoPrims.Clear();
            CollectPrimitives(GizmoPrims);
            foreach (Prim p in GizmoPrims)
            {
                Gizmos.matrix = Matrix4x4.TRS(p.center, p.rot, Vector3.one);
                Gizmos.color = new Color(GizmoTint.r, GizmoTint.g, GizmoTint.b, fillAlpha);
                if (p.shape == Shape.Box)
                    Gizmos.DrawCube(Vector3.zero, p.half * 2f);
                else if (p.shape == Shape.Sphere)
                    Gizmos.DrawSphere(Vector3.zero, p.radius);

                Gizmos.color = new Color(GizmoTint.r, GizmoTint.g, GizmoTint.b, wireAlpha);
                switch (p.shape)
                {
                    case Shape.Box: Gizmos.DrawWireCube(Vector3.zero, p.half * 2f); break;
                    case Shape.Sphere: Gizmos.DrawWireSphere(Vector3.zero, p.radius); break;
                    case Shape.Capsule: DrawWireCapsule(p.radius, Mathf.Max(p.half.y, p.radius)); break;
                    case Shape.Cylinder: DrawWireCylinder(p.radius, p.half.y); break;
                    case Shape.Cone: DrawWireCone(p.radius, p.half.y); break;
                    case Shape.Torus: DrawWireTorus(p.radius, p.half.x); break;
                }
            }
            Gizmos.matrix = Matrix4x4.identity;
        }

        static void DrawWireCylinder(float r, float h)
        {
            DrawCircleY(-h, r, GizmoSegments);
            DrawCircleY(h, r, GizmoSegments);
            for (int i = 0; i < 4; i++)
            {
                float a = i * Mathf.PI * 0.5f;
                Vector3 radial = new(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                Gizmos.DrawLine(radial + Vector3.down * h, radial + Vector3.up * h);
            }
        }

        static void DrawWireCone(float r, float h)
        {
            DrawCircleY(-h, r, GizmoSegments);
            Vector3 tip = Vector3.up * h;
            for (int i = 0; i < 8; i++)
            {
                float a = i * Mathf.PI * 0.25f;
                Gizmos.DrawLine(new Vector3(Mathf.Cos(a) * r, -h, Mathf.Sin(a) * r), tip);
            }
        }

        static void DrawWireCapsule(float r, float halfHeight)
        {
            float lineHalf = Mathf.Max(halfHeight - r, 0f);
            DrawCircleY(-lineHalf, r, GizmoSegments);
            DrawCircleY(lineHalf, r, GizmoSegments);
            for (int direction = 0; direction < 4; direction++)
            {
                float a = direction * Mathf.PI * 0.5f;
                Vector3 radial = new(Mathf.Cos(a), 0f, Mathf.Sin(a));
                Gizmos.DrawLine(radial * r + Vector3.down * lineHalf,
                                radial * r + Vector3.up * lineHalf);
                DrawHemisphereArc(radial, lineHalf, r, true);
                DrawHemisphereArc(radial, lineHalf, r, false);
            }
        }

        static void DrawHemisphereArc(Vector3 radial, float lineHalf, float r, bool top)
        {
            Vector3 previous = radial * r + Vector3.up * (top ? lineHalf : -lineHalf);
            for (int i = 1; i <= GizmoSegments / 4; i++)
            {
                float a = i / (float)(GizmoSegments / 4) * Mathf.PI * 0.5f;
                float y = (top ? lineHalf : -lineHalf) + (top ? 1f : -1f) * Mathf.Sin(a) * r;
                Vector3 current = radial * (Mathf.Cos(a) * r) + Vector3.up * y;
                Gizmos.DrawLine(previous, current);
                previous = current;
            }
        }

        static void DrawWireTorus(float major, float minor)
        {
            int majorSegments = 16;
            int minorSegments = 10;
            for (int ring = 0; ring < majorSegments; ring++)
            {
                float u = ring / (float)majorSegments * Mathf.PI * 2f;
                Vector3 radial = new(Mathf.Cos(u), 0f, Mathf.Sin(u));
                Vector3 previous = radial * (major + minor);
                for (int j = 1; j <= minorSegments; j++)
                {
                    float v = j / (float)minorSegments * Mathf.PI * 2f;
                    Vector3 current = radial * (major + Mathf.Cos(v) * minor) + Vector3.up * (Mathf.Sin(v) * minor);
                    Gizmos.DrawLine(previous, current);
                    previous = current;
                }
            }
            DrawCircleY(0f, major + minor, GizmoSegments);
            DrawCircleY(0f, Mathf.Max(major - minor, 0f), GizmoSegments);
        }

        static void DrawCircleY(float y, float r, int segments)
        {
            if (r <= 0f) return;
            Vector3 previous = new(r, y, 0f);
            for (int i = 1; i <= segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                Vector3 current = new(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r);
                Gizmos.DrawLine(previous, current);
                previous = current;
            }
        }

        // Show Gizmo gates ALL drawing — off means no gizmo even when selected.
        void OnDrawGizmos() { if (showGizmo) DrawShape(0.15f, 0.7f); }
    }
}
