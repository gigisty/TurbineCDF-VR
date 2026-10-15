using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace BrazeFX
{
    /// <summary>
    /// A pure visual measuring stick for the scene — it does NOTHING to simulation, rendering, or gameplay.
    /// Drop it next to an asset (a human, a doorway, a mountain) and it draws a graduated ruler whose length is
    /// read out in true feet, so you can gauge the real-world scale of things that are otherwise hard to judge
    /// in 3D space. Measures height (up), width (right), or depth (forward). Editor gizmo only.
    ///
    /// Length is the ruler's physical size in world units; the readout converts it to feet via Units-Per-Metre
    /// (leave at 1 for a standard 1 unit = 1 m scene, set to 100 for a centimetre scene, etc.).
    /// </summary>
    [AddComponentMenu("BrazeFX/Helpers/Ruler", 61)]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class BrazeRuler : MonoBehaviour
    {
        public enum MeasureAxis { Height = 0, Width = 1, Depth = 2 }
        public enum UnitMode { Feet = 0, Meters = 1, Both = 2 }

        [Tooltip("Which local axis the ruler measures along. Height = up, Width = right, Depth = forward.")]
        public MeasureAxis axis = MeasureAxis.Height;

        [Min(0.0001f), Tooltip("Physical length of the ruler in world units — this is what the readout converts to feet.")]
        public float length = 1.8f;

        [Tooltip("Primary readout unit. Feet is the reference default; Both also shows metres in brackets.")]
        public UnitMode units = UnitMode.Feet;

        [Min(0.0001f), Tooltip("How many world units equal one real-world METRE. Leave at 1 for a standard " +
                               "1 unit = 1 m scene; set to 100 if the scene is authored in centimetres, etc.")]
        public float unitsPerMeter = 1f;

        [Header("APPEARANCE")]
        public Color color = new Color(1f, 0.82f, 0.2f, 1f);
        [Tooltip("Draw graduation ticks along the ruler (spacing auto-adapts so it never over-crowds).")]
        public bool ticks = true;
        [Min(0f), Tooltip("Length of the graduation ticks and end caps, in world units.")]
        public float tickSize = 0.12f;
        [Tooltip("Label each graduation with its running total in feet.")]
        public bool tickLabels = true;
        [Range(8, 40)] public int fontSize = 12;
        [Tooltip("Keep drawing the ruler even when it isn't selected.")]
        public bool alwaysShow = true;

        public const float FeetPerMeter = 3.28084f;

        public Vector3 AxisDir => (axis switch
        {
            MeasureAxis.Width => transform.right,
            MeasureAxis.Depth => transform.forward,
            _ => transform.up
        }).normalized;

        // A stable perpendicular the ticks, end caps and labels lay out along.
        public Vector3 SideDir
        {
            get
            {
                Vector3 s = axis == MeasureAxis.Height ? transform.right : transform.up;
                return s.sqrMagnitude > 1e-8f ? s.normalized : Vector3.right;
            }
        }

        public float Meters => length / Mathf.Max(unitsPerMeter, 1e-4f);
        public float Feet => Meters * FeetPerMeter;
        public Vector3 Base => transform.position;
        public Vector3 Tip => transform.position + AxisDir * length;

        /// <summary>Set the ruler's length from a real-world measurement in feet.</summary>
        public void SetFeet(float feet) => length = Mathf.Max(0.0001f, feet / FeetPerMeter * unitsPerMeter);

        public string Readout()
        {
            switch (units)
            {
                case UnitMode.Meters: return $"{Meters:0.00} m";
                case UnitMode.Both: return $"{FeetInches(Feet)}  ({Meters:0.00} m)";
                default: return FeetInches(Feet);
            }
        }

        static string FeetInches(float totalFeet)
        {
            int ft = Mathf.FloorToInt(totalFeet);
            int inch = Mathf.RoundToInt((totalFeet - ft) * 12f);
            if (inch >= 12) { ft++; inch -= 12; }
            return inch > 0 ? $"{ft}' {inch}\"" : $"{ft}'";
        }

        // Graduation step (in feet) chosen so the ruler never draws more than ~24 ticks at any scale.
        public int FeetStep()
        {
            int feet = Mathf.Max(1, Mathf.CeilToInt(Feet));
            int[] steps = { 1, 2, 5, 10, 25, 50, 100, 250, 500, 1000 };
            foreach (int st in steps) if (feet / st <= 24) return st;
            return feet;
        }

        float UnitsPerFoot() => unitsPerMeter / FeetPerMeter;

        void OnDrawGizmos()
        {
            if (alwaysShow) DrawRuler();
        }

        void OnDrawGizmosSelected()
        {
            if (!alwaysShow) DrawRuler();   // otherwise OnDrawGizmos already drew it
        }

        void DrawRuler()
        {
            Vector3 dir = AxisDir, side = SideDir;
            Vector3 a = Base, b = Tip;

            Gizmos.color = color;
            Gizmos.DrawLine(a, b);                                       // the stick
            Gizmos.DrawLine(a - side * tickSize, a + side * tickSize);   // base cap
            Gizmos.DrawLine(b - side * tickSize, b + side * tickSize);   // tip cap

            float upf = UnitsPerFoot();
            int step = FeetStep();
            if (ticks && upf > 1e-5f)
            {
                int feet = Mathf.FloorToInt(Feet);
                for (int f = step; f <= feet; f += step)
                {
                    Vector3 p = a + dir * (upf * f);
                    float ts = tickSize * (f % (step * 5) == 0 ? 1f : 0.6f);   // taller every 5th graduation
                    Gizmos.DrawLine(p, p + side * ts);
                }
            }

#if UNITY_EDITOR
            DrawLabels(a, b, dir, side, upf, step);
#endif
        }

#if UNITY_EDITOR
        GUIStyle totalStyle, tickStyle;

        void DrawLabels(Vector3 a, Vector3 b, Vector3 dir, Vector3 side, float upf, int step)
        {
            totalStyle ??= new GUIStyle();
            tickStyle ??= new GUIStyle();
            totalStyle.fontStyle = FontStyle.Bold;
            totalStyle.fontSize = fontSize;
            totalStyle.normal.textColor = color;
            tickStyle.fontSize = Mathf.Max(8, fontSize - 3);
            tickStyle.normal.textColor = color;

            Handles.Label(b + side * (tickSize * 1.6f) + dir * (tickSize * 0.5f), Readout(), totalStyle);

            if (tickLabels && ticks && upf > 1e-5f)
            {
                int feet = Mathf.FloorToInt(Feet);
                for (int f = step; f <= feet; f += step)
                    Handles.Label(a + dir * (upf * f) + side * (tickSize * 1.3f), $"{f}", tickStyle);
            }
        }
#endif
    }
}
