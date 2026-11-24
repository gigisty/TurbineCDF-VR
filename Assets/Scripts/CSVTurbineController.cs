using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using BrazeFX.CFD;

[System.Serializable]
public struct WeatherDataPoint
{
    public string timeStamp;
    public float actualPowerKW;       // Columna 1: LV ActivePower (kW)
    public float windSpeed;           // Columna 2: Wind Speed (m/s)
    public float theoreticalPowerKWh; // Columna 3: Theoretical_Power_Curve (KWh)
    public float windDirection;       // Columna 4: Wind Direction (°)
}

public class CSVTurbineController : MonoBehaviour
{
    [Header("BrazeFX References")]
    public BrazeCfdWindTunnel windTunnel;
    [Tooltip("Opcional: rotar el túnel según Wind Direction (°) del CSV")]
    public bool applyWindDirection = false;

    [Header("Data Import (T1.csv)")]
    public TextAsset csvDataFile;
    [Tooltip("Limita cuántas filas cargar para que el Slider sea manejable en VR (144 = 1 día cada 10 min. Usa 0 para cargar las 50,530 filas)")]
    public int maxRowsToLoad = 144;
    [Tooltip("Salto entre filas (1 = cada 10 min, 6 = cada hora)")]
    [Min(1)] public int sampleStep = 1;

    [Header("UI Elements")]
    public Slider timeIndexSlider;
    public TextMeshProUGUI timeDisplayText;
    public TextMeshProUGUI formulaText;

    [Header("Parámetros Fijos de Turbina")]
    public float airDensity = 1.225f;        // kg/m³ (T1.csv no incluye densidad)
    public float rotorRadius = 45f;          // Radio en metros (~45-50m corresponde a esta turbina de 3.6 MW)
    public float powerCoefficient = 0.4f;    // Cp aerodinámico
    public float generatorEfficiency = 0.85f;// Eficiencia del generador

    private List<WeatherDataPoint> dataPoints = new List<WeatherDataPoint>();

    void Start()
    {
        LoadCSVData();

        if (timeIndexSlider != null && dataPoints.Count > 0)
        {
            timeIndexSlider.minValue = 0;
            timeIndexSlider.maxValue = dataPoints.Count - 1;
            timeIndexSlider.wholeNumbers = true;

            timeIndexSlider.onValueChanged.AddListener((val) => UpdateSimulationFromIndex((int)val));
            UpdateSimulationFromIndex(0);
        }
    }

    private void LoadCSVData()
    {
        dataPoints.Clear();

        if (csvDataFile == null)
        {
            Debug.LogError("No hay un archivo CSV asignado en CSVTurbineController.");
            return;
        }

        // Maneja tanto \r\n (Windows) como \n (Unix) y elimina líneas vacías
        string[] rows = csvDataFile.text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        int step = Mathf.Max(1, sampleStep);

        // Inicia en i = 1 para saltar los encabezados
        for (int i = 1; i < rows.Length; i += step)
        {
            if (maxRowsToLoad > 0 && dataPoints.Count >= maxRowsToLoad)
                break;

            string[] columns = rows[i].Split(',');
            if (columns.Length < 5) continue;

            WeatherDataPoint point = new WeatherDataPoint();
            point.timeStamp = columns[0].Trim();

            // CultureInfo.InvariantCulture garantiza que el punto decimal '.' se lea bien en cualquier PC
            float.TryParse(columns[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out point.actualPowerKW);
            float.TryParse(columns[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out point.windSpeed);
            float.TryParse(columns[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out point.theoreticalPowerKWh);
            float.TryParse(columns[4].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out point.windDirection);

            // Evita potencias negativas menores cuando la turbina está en reposo (ej. -2.47 kW en T1.csv)
            point.actualPowerKW = Mathf.Max(0f, point.actualPowerKW);

            dataPoints.Add(point);
        }

        Debug.Log($"CSV cargado exitosamente: {dataPoints.Count} registros listos para el Slider.");
    }

    public void UpdateSimulationFromIndex(int index)
    {
        if (index < 0 || index >= dataPoints.Count) return;

        WeatherDataPoint currentData = dataPoints[index];

        if (timeDisplayText != null)
        {
            timeDisplayText.text = $"{currentData.timeStamp} | Dir: {currentData.windDirection:F0}°";
        }

        if (windTunnel != null)
        {
            windTunnel.airSpeed = currentData.windSpeed;

            if (applyWindDirection)
            {
                Vector3 currentEuler = windTunnel.transform.localEulerAngles;
                windTunnel.transform.localEulerAngles = new Vector3(currentEuler.x, currentData.windDirection, currentEuler.z);
            }
        }

        UpdatePowerFormula(currentData);
    }

    private void UpdatePowerFormula(WeatherDataPoint data)
    {
        if (formulaText == null) return;

        float sweptArea = Mathf.PI * Mathf.Pow(rotorRadius, 2);
        float powerWatts = 0.5f * airDensity * sweptArea * Mathf.Pow(data.windSpeed, 3) * powerCoefficient * generatorEfficiency;
        float powerKW = powerWatts / 1000f;

        formulaText.text = "<b>Fórmula Base:</b>\n" +
                           "P = ½ · ρ · A · v³ · Cp · η\n\n" +
                           "<b>Cálculo Dinámico:</b>\n" +
                           $"P = 0.5 · {airDensity:F3} · {sweptArea:F1}m² · <b>{data.windSpeed:F2}³</b> · {powerCoefficient} · {generatorEfficiency}\n\n" +
                           $"<b>Potencia Calculada:</b> <color=#55FF55>{powerKW:F2} kW</color>\n" +
                           $"<b>Potencia Real SCADA (CSV):</b> <color=#55CCFF>{data.actualPowerKW:F2} kW</color>";
    }
}