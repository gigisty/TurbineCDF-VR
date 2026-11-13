using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using BrazeFX.CFD;

// Estructura para almacenar cada fila parseada del archivo CSV
[System.Serializable]
public struct WeatherDataPoint
{
    public string timeStamp;
    public float windSpeed;
    public float airDensity;
}

public class CSVTurbineController : MonoBehaviour
{
    [Header("BrazeFX References")]
    public BrazeCfdWindTunnel windTunnel; // Componente original para la velocidad del aire[cite: 1]

    [Header("Data Import (Ruta 1)")]
    [Tooltip("Arrastra tu archivo CSV (ej. datos_clima.csv) desde la carpeta de Unity aquí")]
    public TextAsset csvDataFile;

    [Header("UI Elements")]
    public Slider timeIndexSlider; // Controlador principal del tiempo
    public TextMeshProUGUI timeDisplayText; // Muestra la hora actual (Ej: "08:00 AM")
    public TextMeshProUGUI formulaText; // Panel de la fórmula de potencia[cite: 2]

    [Header("Parámetros Fijos de Turbina")]
    public float rotorRadius = 5f; // Radio en metros[cite: 2]
    public float powerCoefficient = 0.4f; // 40% de eficiencia aerodinámica[cite: 2]
    public float generatorEfficiency = 0.8f; // 80% de eficiencia del generador[cite: 2]

    private List<WeatherDataPoint> dataPoints = new List<WeatherDataPoint>();

    void Start()
    {
        LoadCSVData();

        if (timeIndexSlider != null && dataPoints.Count > 0)
        {
            // El slider navega desde la primera hasta la última fila del CSV
            timeIndexSlider.minValue = 0;
            timeIndexSlider.maxValue = dataPoints.Count - 1;

            // Forzamos números enteros para evitar saltos entre índices
            timeIndexSlider.wholeNumbers = true;

            // Usamos lambda para ignorar el float y castearlo a entero[cite: 3]
            timeIndexSlider.onValueChanged.AddListener((val) => UpdateSimulationFromIndex((int)val));

            // Forzar la primera actualización visual en el tiempo 0
            UpdateSimulationFromIndex(0);
        }
    }

    private void LoadCSVData()
    {
        if (csvDataFile == null)
        {
            Debug.LogError("No hay un archivo CSV asignado en MenuValuesEditor.");
            return;
        }

        // Divide el texto completo por saltos de línea
        string[] rows = csvDataFile.text.Split('\n');

        // El bucle inicia en i = 1 asumiendo que la fila 0 contiene los encabezados (Time, WindSpeed, AirDensity)
        for (int i = 1; i < rows.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(rows[i])) continue;

            string[] columns = rows[i].Split(',');
            if (columns.Length >= 3)
            {
                WeatherDataPoint point = new WeatherDataPoint();
                point.timeStamp = columns[0];

                // Parsea los textos a variables numéricas float
                if (float.TryParse(columns[1], out float speed)) point.windSpeed = speed;
                if (float.TryParse(columns[2], out float density)) point.airDensity = density;

                dataPoints.Add(point);
            }
        }
    }

    public void UpdateSimulationFromIndex(int index)
    {
        if (index < 0 || index >= dataPoints.Count) return;

        WeatherDataPoint currentData = dataPoints[index];

        // 1. Actualizar la visualización de la hora en el panel
        if (timeDisplayText != null) timeDisplayText.text = "Tiempo: " + currentData.timeStamp;

        // 2. Actualizar la velocidad real en el simulador BrazeFX volumétrico[cite: 1, 5]
        if (windTunnel != null)
        {
            windTunnel.airSpeed = currentData.windSpeed;
        }

        // 3. Actualizar el cálculo de la fórmula
        UpdatePowerFormula(currentData);
    }

    private void UpdatePowerFormula(WeatherDataPoint data)
    {
        if (formulaText == null) return;

        // Calcula el área de barrido del rotor (A = pi * r²)[cite: 2]
        float sweptArea = Mathf.PI * Mathf.Pow(rotorRadius, 2);
        
        // Aplica la fórmula principal de potencia en Vatios[cite: 2]
        float powerWatts = 0.5f * data.airDensity * sweptArea * Mathf.Pow(data.windSpeed, 3) * powerCoefficient * generatorEfficiency;
        
        // Convierte a Kilovatios (kW)[cite: 2]
        float powerKW = powerWatts / 1000f;

        // Formatea el texto limitando los decimales con ToString("F1") y "F2"[cite: 1, 2]
        formulaText.text = "<b>Fórmula Base:</b>\n" +
                           "P = ½ · ρ · A · v³ · Cp · η\n\n" +
                           "<b>Cálculo Dinámico:</b>\n" +
                           $"P = 0.5 · {data.airDensity} · {sweptArea.ToString("F1")}m² · <b>{data.windSpeed.ToString("F1")}³</b> · {powerCoefficient} · {generatorEfficiency}\n\n" +
                           $"<b>Potencia Generada:</b> <color=#55FF55>{powerKW.ToString("F2")} kW</color>";
    }
}
