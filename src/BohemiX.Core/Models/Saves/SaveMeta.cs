namespace BohemiX.Core.Models.Saves;

/// <summary>
/// Lightweight metadata used for fast save-list discovery.
/// </summary>
public sealed class SaveMeta
{
    public string SaveID { get; set; } = string.Empty;

    public string PlaylineID { get; set; } = string.Empty;

    public SaveType Type { get; set; }

    public string GameVersion { get; set; } = string.Empty;

    public long Timestamp { get; set; }

    public double PlayTimeTotal { get; set; }

    public DisplayInfo DisplayData { get; set; } = new();

    public string ThumbnailPath { get; set; } = string.Empty;
}
