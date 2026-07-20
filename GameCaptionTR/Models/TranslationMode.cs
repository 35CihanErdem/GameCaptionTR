namespace GameCaptionTR.Models;

public enum TranslationMode
{
    /// <summary>Her zaman çevrimiçi (Google / MyMemory).</summary>
    Online,

    /// <summary>Yerel ONNX modeli — internet gerekmez (şu an en→tr).</summary>
    Offline,

    /// <summary>İnternet yoksa yerel, varsa çevrimiçi.</summary>
    Auto
}
