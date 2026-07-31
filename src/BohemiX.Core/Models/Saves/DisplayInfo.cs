namespace BohemiX.Core.Models.Saves;

/// <summary>
/// Small UI-facing snapshot stored beside the large save payload.
/// </summary>
public sealed class DisplayInfo
{
    public int HenryLevel { get; set; }

    public string CurrentLocation { get; set; } = string.Empty;

    public string ActiveQuest { get; set; } = string.Empty;

    public int GroschenCount { get; set; }

    public List<string> PlayerStatusEffects { get; set; } = [];
}
