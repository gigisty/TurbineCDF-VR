using System.Collections.Generic;
using UnityEngine;

namespace BrazeFX
{
    /// <summary>Shared, generated unit meshes used by blast flipbooks.</summary>
    internal static class BrazeFlipbookMeshLibrary
    {
        static readonly Dictionary<BrazeFlipbookPlayer.RenderShape, Mesh> Meshes = new();
        static readonly List<Vector3> Vertices = new();
        static readonly List<Vector2> Uvs = new();
        static readonly List<int> Triangles = new();

        internal static Mesh Get(BrazeFlipbookPlayer.RenderShape shape)
        {
            if (Meshes.TryGetValue(shape, out Mesh mesh) && mesh != null) return mesh;

            Vertices.Clear();
            Uvs.Clear();
            Triangles.Clear();
            switch (shape)
            {
                case BrazeFlipbookPlayer.RenderShape.Plane: BuildPlane(); break;
                case BrazeFlipbookPlayer.RenderShape.Box: BuildBox(); break;
                case BrazeFlipbookPlayer.RenderShape.Sphere: BuildSphere(24, 12); break;
                case BrazeFlipbookPlayer.RenderShape.Torus: BuildTorus(24, 12); break;
                case BrazeFlipbookPlayer.RenderShape.Capsule: BuildCapsule(24, 8, 3); break;
                default: BuildQuad(); break;
            }

            mesh = new Mesh
            {
                name = $"Braze Blast {shape}",
                hideFlags = HideFlags.HideAndDontSave,
            };
            mesh.SetVertices(Vertices);
            mesh.SetUVs(0, Uvs);
            mesh.SetTriangles(Triangles, 0, true);
            mesh.RecalculateBounds();
            Meshes[shape] = mesh;
            return mesh;
        }

        static void BuildQuad()
        {
            AddFace(
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f));
        }

        static void BuildPlane()
        {
            AddFace(
                new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 0f, -0.5f),
                new Vector3(-0.5f, 0f, 0.5f), new Vector3(0.5f, 0f, 0.5f));
        }

        static void BuildBox()
        {
            AddFace(new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(-0.5f, 0.5f, 0.5f), new Vector3(0.5f, 0.5f, 0.5f));
            AddFace(new Vector3(0.5f, -0.5f, -0.5f), new Vector3(-0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f));
            AddFace(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(-0.5f, -0.5f, 0.5f),
                new Vector3(-0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, 0.5f));
            AddFace(new Vector3(0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, 0.5f), new Vector3(0.5f, 0.5f, -0.5f));
            AddFace(new Vector3(-0.5f, 0.5f, 0.5f), new Vector3(0.5f, 0.5f, 0.5f),
                new Vector3(-0.5f, 0.5f, -0.5f), new Vector3(0.5f, 0.5f, -0.5f));
            AddFace(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f),
                new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f));
        }

        static void BuildSphere(int longitude, int latitude)
        {
            for (int y = 0; y <= latitude; y++)
            {
                float v = y / (float)latitude;
                float phi = Mathf.Lerp(-Mathf.PI * 0.5f, Mathf.PI * 0.5f, v);
                float ring = Mathf.Cos(phi);
                float height = Mathf.Sin(phi);
                for (int x = 0; x <= longitude; x++)
                {
                    float u = x / (float)longitude;
                    float theta = u * Mathf.PI * 2f;
                    Vertices.Add(new Vector3(Mathf.Cos(theta) * ring, height, Mathf.Sin(theta) * ring) * 0.5f);
                    Uvs.Add(new Vector2(u, v));
                }
            }
            AddGridTriangles(longitude, latitude);
        }

        static void BuildTorus(int majorSegments, int tubeSegments)
        {
            const float majorRadius = 0.36f;
            const float tubeRadius = 0.14f;
            for (int major = 0; major <= majorSegments; major++)
            {
                float u = major / (float)majorSegments;
                float a = u * Mathf.PI * 2f;
                float ca = Mathf.Cos(a);
                float sa = Mathf.Sin(a);
                for (int tube = 0; tube <= tubeSegments; tube++)
                {
                    float v = tube / (float)tubeSegments;
                    float b = v * Mathf.PI * 2f;
                    float radial = majorRadius + Mathf.Cos(b) * tubeRadius;
                    Vertices.Add(new Vector3(ca * radial, Mathf.Sin(b) * tubeRadius, sa * radial));
                    Uvs.Add(new Vector2(u, v));
                }
            }
            AddGridTriangles(tubeSegments, majorSegments);
        }

        static void BuildCapsule(int longitude, int hemisphereRings, int cylinderRings)
        {
            const float radius = 0.25f;
            const float cylinderHalfHeight = 0.25f;
            var rings = new List<Vector2>(); // x = y, y = radial size
            for (int i = 0; i <= hemisphereRings; i++)
            {
                float angle = Mathf.Lerp(-Mathf.PI * 0.5f, 0f, i / (float)hemisphereRings);
                rings.Add(new Vector2(-cylinderHalfHeight + Mathf.Sin(angle) * radius, Mathf.Cos(angle) * radius));
            }
            for (int i = 1; i < cylinderRings; i++)
                rings.Add(new Vector2(Mathf.Lerp(-cylinderHalfHeight, cylinderHalfHeight, i / (float)cylinderRings), radius));
            for (int i = 0; i <= hemisphereRings; i++)
            {
                float angle = Mathf.Lerp(0f, Mathf.PI * 0.5f, i / (float)hemisphereRings);
                rings.Add(new Vector2(cylinderHalfHeight + Mathf.Sin(angle) * radius, Mathf.Cos(angle) * radius));
            }

            int rows = rings.Count - 1;
            for (int row = 0; row < rings.Count; row++)
            {
                float v = row / (float)rows;
                for (int x = 0; x <= longitude; x++)
                {
                    float u = x / (float)longitude;
                    float angle = u * Mathf.PI * 2f;
                    Vertices.Add(new Vector3(Mathf.Cos(angle) * rings[row].y, rings[row].x,
                        Mathf.Sin(angle) * rings[row].y));
                    Uvs.Add(new Vector2(u, v));
                }
            }
            AddGridTriangles(longitude, rows);
        }

        static void AddFace(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int start = Vertices.Count;
            Vertices.Add(a); Vertices.Add(b); Vertices.Add(c); Vertices.Add(d);
            Uvs.Add(new Vector2(0f, 0f)); Uvs.Add(new Vector2(1f, 0f));
            Uvs.Add(new Vector2(0f, 1f)); Uvs.Add(new Vector2(1f, 1f));
            Triangles.Add(start); Triangles.Add(start + 2); Triangles.Add(start + 1);
            Triangles.Add(start + 1); Triangles.Add(start + 2); Triangles.Add(start + 3);
        }

        static void AddGridTriangles(int columns, int rows)
        {
            int stride = columns + 1;
            int start = Vertices.Count - stride * (rows + 1);
            for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
            {
                int a = start + y * stride + x;
                int b = a + 1;
                int c = a + stride;
                int d = c + 1;
                Triangles.Add(a); Triangles.Add(c); Triangles.Add(b);
                Triangles.Add(b); Triangles.Add(c); Triangles.Add(d);
            }
        }
    }
}
