using System.Text.Json.Serialization;

namespace BohemiX.Core.Models.Saves;

/// <summary>
/// Kingdom Come: Deliverance II save origin classification.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SaveType
{
    Potion,
    Bed,
    Auto,
    Exit
}
