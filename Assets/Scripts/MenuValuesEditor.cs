using BrazeFX.CFD;
using BrazeFX;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class MenuValuesEditor : MonoBehaviour
{
    [Header("References")]
    public BrazeCfdDomain cFDDomain;
    public BrazeCfdTestBody testBody;
    public BrazeCfdWindTunnel windTunnel;
    public BrazeMotion motionBody;
    public FormulaCalculator formulaCalc;

    [Header("UI Elements")]
    public Slider windSpeedSlider;
    public TMP_Text windSpeedText;
    public Slider motionSpeedSlider;
    public TMP_Text motionSpeedText;
    public Button toggleBuoyancyButton;

    private bool isBuoyancyActive = false;

    void Start()
    {
        if (windSpeedSlider != null && windTunnel != null)
        {
            // 1. Asigna los valores mínimo y máximo del Slider por código
            windSpeedSlider.minValue = 0f;
            windSpeedSlider.maxValue = 10f;

            motionSpeedSlider.minValue = 0f;
            motionSpeedSlider.maxValue = 2f;

            // 2. Sincroniza la posición del Slider con el valor actual de airSpeed
            windSpeedSlider.value = windTunnel.airSpeed;
            windSpeedText.text = windTunnel.airSpeed.ToString();

            motionSpeedSlider.value = motionBody.strength;
            motionSpeedText.text = motionBody.strength.ToString();

            // 3. Escucha los cambios del Slider
            windSpeedSlider.onValueChanged.AddListener(UpdateAirSpeed);
            motionSpeedSlider.onValueChanged.AddListener(UpdateMotionSpeed);
        }
    }

    public void UpdateAirSpeed(float newSpeed)
    {
        if (windTunnel != null)
        {
            windTunnel.airSpeed = newSpeed;
            windSpeedText.text = newSpeed.ToString("F2");
            if (newSpeed <= 2f)
            {
                motionBody.strength = newSpeed;
                motionSpeedSlider.value = newSpeed;
                motionSpeedText.text = newSpeed.ToString("F2");
            }
            formulaCalc.UpdatePowerFormulaDisplay();
        }
    }

    public void UpdateMotionSpeed(float newStrenght)
    {
        if (motionBody != null)
        {
            motionBody.strength = newStrenght;
            motionSpeedText.text = newStrenght.ToString("F2");
            formulaCalc.UpdatePowerFormulaDisplay();
        }
    }
}