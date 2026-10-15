using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    [CustomEditor(typeof(BrazeFractureBulletSpawner))]
    [CanEditMultipleObjects]
    public sealed class BrazeFractureBulletSpawnerEditor : Editor
    {
        const string K = "BrazeFX.FractureBulletSpawner.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            bool refreshAimSurface = false;
            BrazeEditorGUI.Header("FRACTURE BULLET SPAWNER", "bullet_spawner");
            SerializedProperty P(string name) => serializedObject.FindProperty(name);

            if (BrazeEditorGUI.Section(K + "prefab", "PROJECTILE"))
            {
                BrazeEditorGUI.PropertyField(P("bulletPrefab"), new GUIContent("Bullet Prefab"));
                BrazeEditorGUI.PropertyField(P("muzzle"));
                GameObject prefab = P("bulletPrefab").objectReferenceValue as GameObject;
                if (prefab != null && prefab.GetComponentInChildren<BrazeFractureImpactSource>(true) == null)
                    EditorGUILayout.HelpBox("The Bullet Prefab needs a Braze Fracture Impact Source on its root or a child.", MessageType.Error);
                BrazeEditorGUI.Hint("The prefab stays scene-reference free. Its impact source automatically sweeps against active Voronoi fractures and may leave Container empty for automatic fluid routing.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "firing", "FIRING"))
            {
                BrazeEditorGUI.ToggleButton("Auto Fire", P("autoFire"));
                BrazeEditorGUI.PropertyField(P("roundsPerSecond"), new GUIContent("Rounds / Second"));
                BrazeEditorGUI.PropertyField(P("speed"));
                BrazeEditorGUI.Slider(P("spread"), 0f, 45f, new GUIContent("Spread"));
                BrazeEditorGUI.ToggleButton("Align To Velocity", P("alignToVelocity"));
                BrazeEditorGUI.Hint("Auto Fire is optional. Gameplay weapons can call Fire() for one shot or Fire(position, direction) for an explicit trajectory.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "surfaceAim", "SURFACE AIM"))
            {
                BrazeEditorGUI.ToggleButton("Aim At Mesh", P("aimAtMesh"));
                if (P("aimAtMesh").boolValue)
                {
                    BrazeEditorGUI.PropertyField(P("targetMesh"), new GUIContent("Target Mesh"));
                    BrazeEditorGUI.ToggleButton("Aim Muzzle", P("aimMuzzle"));
                    if (P("aimMuzzle").boolValue)
                    {
                        BrazeEditorGUI.PropertyField(P("muzzleAimSpeed"), new GUIContent("Aim Speed"));
                        BrazeEditorGUI.PropertyField(P("aimRetargetInterval"), new GUIContent("Retarget Interval"));
                    }
                    using (new EditorGUI.DisabledScope(P("targetMesh").objectReferenceValue == null))
                        if (GUILayout.Button("Refresh Surface Points")) refreshAimSurface = true;
                    if (targets.Length == 1 && target is BrazeFractureBulletSpawner surfaceSpawner
                        && surfaceSpawner.targetMesh != null)
                        BrazeEditorGUI.SubLabel($"{surfaceSpawner.AimTriangleCount:n0} aim triangles cached");
                }
                BrazeEditorGUI.Hint("Aim Muzzle holds a random surface point for Retarget Interval and smoothly turns toward it; bullets follow the muzzle's actual forward axis. Without it, every shot aims directly at a new point.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "lifecycle", "LIFECYCLE"))
            {
                BrazeEditorGUI.PropertyField(P("lifetime"));
                BrazeEditorGUI.ToggleButton("Recycle On Impact", P("recycleOnImpact"));
                BrazeEditorGUI.Hint("An accepted impact stops the source immediately, then preserves any Braze Ribbon for its authored Ribbon Time before pooling. Misses return after Lifetime.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "pool", "POOL"))
            {
                BrazeEditorGUI.IntSlider(P("poolSize"), 1, Mathf.Max(1, P("maxPoolSize").intValue), new GUIContent("Warm Count"));
                BrazeEditorGUI.ToggleButton("Auto Expand", P("autoExpand"));
                BrazeEditorGUI.PropertyField(P("maxPoolSize"), new GUIContent("Maximum"));
                if (targets.Length == 1 && target is BrazeFractureBulletSpawner spawner && Application.isPlaying)
                {
                    BrazeEditorGUI.SubLabel($"{spawner.ActiveBulletCount} active  •  {spawner.PoolCount} instances  •  {spawner.FiredCount} fired");
                    BrazeEditorGUI.SubLabel($"{spawner.AcceptedImpactCount} impacts  •  {spawner.LifetimeRecycleCount} expired  •  {spawner.CapacityRecycleCount} capacity reuse");
                }
                BrazeEditorGUI.Hint("Instances are moved by one central loop—no Rigidbody, Collider, or per-bullet Update is required. At Maximum capacity, the oldest projectile is recycled so firing never silently stops.");
            }
            BrazeEditorGUI.EndSection();

            if (targets.Length == 1 && target is BrazeFractureBulletSpawner single)
            {
                using (new EditorGUI.DisabledScope(!Application.isPlaying || single.bulletPrefab == null))
                {
                    if (GUILayout.Button("Fire Now")) single.Fire();
                }
            }

            serializedObject.ApplyModifiedProperties();
            BakeAimSurfaces(refreshAimSurface);
        }

        void BakeAimSurfaces(bool force)
        {
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] is not BrazeFractureBulletSpawner spawner) continue;
                Mesh mesh = spawner.targetMesh != null ? spawner.targetMesh.sharedMesh : null;
                if (!force && spawner.AimSurfaceBakeMatches(mesh)) continue;
                if (mesh == null)
                {
                    spawner.SetAimSurfaceBake(null, null, null, null, 0f);
                    continue;
                }

                using Mesh.MeshDataArray data = MeshUtility.AcquireReadOnlyMeshData(mesh);
                BrazeFractureBulletSpawner.BuildAimSurface(data[0], out Vector3[] vertices,
                    out int[] triangleIndices, out float[] cumulativeAreas, out float totalArea);
                spawner.SetAimSurfaceBake(mesh, vertices, triangleIndices, cumulativeAreas, totalArea);
                EditorUtility.SetDirty(spawner);
            }
        }
    }
}
