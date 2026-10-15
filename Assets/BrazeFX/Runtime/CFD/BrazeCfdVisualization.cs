using UnityEngine;

namespace BrazeFX.CFD
{
    /// <summary>Renderer-facing presentation contract shared by the simple Wind Tunnel facade and the
    /// boundary-free advanced Visualizer. It deliberately contains no boundary-condition authoring.</summary>
    internal interface IBrazeCfdVisualization
    {
        BrazeCfdDomain Domain { get; }
        bool SmokeEnabled { get; }
        bool SmokeTemperatureColor { get; }
        bool SmokeFlowSpeedColor { get; }
        bool TemperatureAutoRangeEnabled { get; }
        float SmokeColorContrast { get; }
        float SmokeFlowEmphasis { get; }
        float SmokeOpacity { get; }
        float SmokeBrightness { get; }
        float SmokeHeatMinimum { get; }
        float SmokeHeatInverseRange { get; }
        float EffectiveSmokePersistenceSeconds { get; }
        Texture2D SmokePreint { get; }
    }
}
