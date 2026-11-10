using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static BrazeFX.BrazeMotion;
using TMPro;

public class FormulaCalculator : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private TextMeshProUGUI formulaText;
    [SerializeField] private MenuValuesEditor vR;
    [SerializeField] private Slider airDensitysldr;
    [SerializeField] private TMP_Text airText;
    [SerializeField] private Slider rotorRadioussldr;
    [SerializeField] private TMP_Text rotorText;
    [SerializeField] private Slider powerCoefficientsldr;
    [SerializeField] private TMP_Text powerText;
    [SerializeField] private Slider generatorEfficiencysldr;
    [SerializeField] private TMP_Text generatorText;

    [Space(5)]

    [Header("Wind Turbine Parameters")]
    [SerializeField] private float airDensity = 1.225f; //kg/m^3
    [SerializeField] private float rotorRadius = 5f; //m^2
    [SerializeField] private float powerCoefficient = 0.4f; //40% of aerodynamic efficiency 
    [SerializeField] private float generatorEfficiency = 0.8f; //80% of generator efficiency


    void Start()
    {
        if (airDensitysldr != null && rotorRadioussldr != null && powerCoefficientsldr != null && generatorEfficiencysldr != null)
        {
            // 1. Asigna los valores mínimo y máximo del Slider por código
            airDensitysldr.minValue = 1f;
            airDensitysldr.maxValue = 1.225f;

            rotorRadioussldr.minValue = 3f;
            rotorRadioussldr.maxValue = 6f;

            powerCoefficientsldr.minValue = 0.2f;
            powerCoefficientsldr.maxValue = 0.5f;

            generatorEfficiencysldr.minValue = 0.5f;
            generatorEfficiencysldr.maxValue = 0.9f;

            // 2. Sincroniza la posición del Slider con el valor actual de airSpeed
            airDensitysldr.value = airDensity;
            airText.text = airDensity.ToString();

            rotorRadioussldr.value = rotorRadius;
            rotorText.text = rotorRadius.ToString();

            powerCoefficientsldr.value = powerCoefficient;
            powerText.text = powerCoefficient.ToString();

            generatorEfficiencysldr.value = generatorEfficiency;
            generatorText.text = generatorEfficiency.ToString();

            // 3. Escucha los cambios del Slider
            airDensitysldr.onValueChanged.AddListener(UpdateAirDensity);
            rotorRadioussldr.onValueChanged.AddListener(UpdateRotorRadius);
            powerCoefficientsldr.onValueChanged.AddListener(UpdatePowerCoefficient);
            generatorEfficiencysldr.onValueChanged.AddListener(UpdateGeneratorEfficiency);
        }
        UpdatePowerFormulaDisplay();
    }

    private void UpdateAirDensity(float newDensity)
    {
        airDensity = newDensity;
        airText.text = newDensity.ToString("F2");

        UpdatePowerFormulaDisplay();
    }

    private void UpdateRotorRadius(float newRadius)
    {
        rotorRadius = newRadius;
        rotorText.text = newRadius.ToString("F2");

        UpdatePowerFormulaDisplay();
    }

    private void UpdatePowerCoefficient(float newCoefficient)
    {
        powerCoefficient = newCoefficient;
        powerText.text = newCoefficient.ToString("F2");

        UpdatePowerFormulaDisplay();
    }
    private void UpdateGeneratorEfficiency(float newEfficiency)
    {
        generatorEfficiency = newEfficiency;
        generatorText.text = newEfficiency.ToString("F2");

        UpdatePowerFormulaDisplay();
    }
    public void UpdatePowerFormulaDisplay()
    {
        if (formulaText == null) return;

        // 1. A = pi * r²
        float sweptArea = Mathf.PI * Mathf.Pow(rotorRadius, 2);

        // 2. Potency in Wats
        float powerWatts = 0.5f * airDensity * sweptArea * Mathf.Pow(vR.windTunnel.airSpeed, 3) * powerCoefficient * generatorEfficiency;

        // Conversion to KiloWats (kW)
        float powerKW = powerWatts / 1000f;

        // 3. Formating for Canvas in Unity
        formulaText.text = $"P = 0.5 · {airDensity} · {sweptArea.ToString("F1")}m² · <b>{vR.windTunnel.airSpeed.ToString("F1")}³</b> · {powerCoefficient} · {generatorEfficiency}\n\n" +
                           $"<b>Generated Potency:</b> <color=#55FF55>{powerKW.ToString("F2")} kW</color>";
    }
}
