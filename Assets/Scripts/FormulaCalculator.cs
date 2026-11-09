using UnityEngine;
using TMPro;

public class FormulaCalculator : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private TextMeshProUGUI formulaText;
    [SerializeField] private MenuValuesEditor vR;

    [Space(5)]

    [Header("Wind Turbine Parameters")]
    [SerializeField] private float airDensity = 1.225f; //kg/m^3
    [SerializeField] private float rotorRadius = 5f; //m^2
    [SerializeField] private float powerCoefficient = 0.4f; //40% of aerodynamic efficiency 
    [SerializeField] private float generatorEfficiency = 0.8f; //80% of generator efficiency


    void Start()
    {
        UpdatePowerFormulaDisplay(vR.windTunnel.airSpeed);
    }

    public void UpdatePowerFormulaDisplay(float currentWindSpeed)
    {
        if (formulaText == null) return;

        // 1. A = pi * r²
        float sweptArea = Mathf.PI * Mathf.Pow(rotorRadius, 2);

        // 2. Potency in Wats
        float powerWatts = 0.5f * airDensity * sweptArea * Mathf.Pow(currentWindSpeed, 3) * powerCoefficient * generatorEfficiency;

        // Conversion to KiloWats (kW)
        float powerKW = powerWatts / 1000f;

        // 3. Formating for Canvas in Unity
        formulaText.text = $"P = 0.5 · {airDensity} · {sweptArea.ToString("F1")}m² · <b>{currentWindSpeed.ToString("F1")}³</b> · {powerCoefficient} · {generatorEfficiency}\n\n" +
                           $"<b>Generated Potency:</b> <color=#55FF55>{powerKW.ToString("F2")} kW</color>";
    }
}
