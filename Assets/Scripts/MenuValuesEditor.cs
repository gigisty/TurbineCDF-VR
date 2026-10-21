using BrazeFX.CFD;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class MenuValuesEditor : MonoBehaviour
{
    [Header("References")]
    public BrazeCfdDomain cFDDomain;
    public BrazeCfdTestBody testBody;
    public BrazeCfdWindTunnel windTunnel;

    [Header("UI Elements")]
    public Slider windSpeedSlider;
    public TMP_Text windSpeedText;
    public Button toggleBuoyancyButton;

    private bool isBuoyancyActive = false;

    void Start()
    {
        if (windSpeedSlider != null && windTunnel != null)
        {
            // 1. Asigna los valores mínimo y máximo del Slider por código
            windSpeedSlider.minValue = 0f;
            windSpeedSlider.maxValue = 10f;

            // 2. Sincroniza la posición del Slider con el valor actual de airSpeed
            windSpeedSlider.value = windTunnel.airSpeed;
            windSpeedText.text = windTunnel.airSpeed.ToString();

            // 3. Escucha los cambios del Slider
            windSpeedSlider.onValueChanged.AddListener(UpdateAirSpeed);
        }
    }

    public void UpdateAirSpeed(float newSpeed)
    {
        if (windTunnel != null)
        {
            windTunnel.airSpeed = newSpeed;
            windSpeedText.text = newSpeed.ToString("F2");
        }
    }
}