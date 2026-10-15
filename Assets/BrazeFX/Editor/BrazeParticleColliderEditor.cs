using UnityEditor;
using UnityEngine;

namespace BrazeFX.EditorTools
{
    /// <summary>
    /// IMGUI inspector for <see cref="BrazeParticleCollider"/> — Shape (Plane/Sphere/Box) + Response
    /// (bounce/friction/strength/radius) + Targets. Matches the force/emitter inspector look.
    /// </summary>
    [CustomEditor(typeof(BrazeParticleCollider))]
    [CanEditMultipleObjects]
    public sealed class BrazeParticleColliderEditor : Editor
    {
        const string K = "BrazeFX.Collider.";

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            BrazeEditorGUI.Header("BRAZE PARTICLE COLLIDER", "collider");

            SerializedProperty P(string n) => serializedObject.FindProperty(n);

            if (BrazeEditorGUI.Section(K + "shape", "Shape"))
            {
                BrazeEditorGUI.PillsEnum("Shape", P("shape"));
                var shape = (BrazeParticleCollider.Shape)P("shape").enumValueIndex;
                if (shape == BrazeParticleCollider.Shape.Mesh)
                    BrazeEditorGUI.PropertyField(P("meshRenderer"), new GUIContent("Renderer"));
                BrazeEditorGUI.Hint(shape switch
                {
                    BrazeParticleCollider.Shape.Plane  => "Infinite surface. Move & ROTATE the Transform to place it (normal = up axis); scale sizes the gizmo.",
                    BrazeParticleCollider.Shape.Sphere => "Use the Transform SCALE to set the radius (largest axis), and Move to position it.",
                    BrazeParticleCollider.Shape.Box    => "Use the Transform to MOVE, ROTATE and SCALE the box (unit cube × scale).",
                    _                                  => "Particles collide with the actual (deforming) surface of the assigned Renderer. One mesh collider per system.",
                });
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "response", "Response"))
            {
                BrazeEditorGUI.PropertyField(P("bounce"));
                BrazeEditorGUI.PropertyField(P("friction"));
                BrazeEditorGUI.PropertyField(P("strength"));
                BrazeEditorGUI.Hint("Bounce 0 = slide, 1 = elastic. Friction 0 = slippery, 1 = grippy. Strength = how firmly the surface grabs velocity (0 still slides — never sticks). Particles are always pushed out of the surface.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "variation", "Variation"))
            {
                BrazeEditorGUI.PropertyField(P("bounceJitter"), new GUIContent("Bounce ±"));
                BrazeEditorGUI.PropertyField(P("frictionJitter"), new GUIContent("Friction ±"));
                BrazeEditorGUI.PropertyField(P("strengthJitter"), new GUIContent("Strength ±"));
                BrazeEditorGUI.Hint("Per-particle ± randomness on each response value (fraction). Breaks up uniform bouncing.");
            }
            BrazeEditorGUI.EndSection();

            if (BrazeEditorGUI.Section(K + "debug", "Debug"))
                BrazeEditorGUI.ToggleButton("Show Gizmo", P("showGizmo"));
            BrazeEditorGUI.EndSection();

            serializedObject.ApplyModifiedProperties();
        }
    }
}
