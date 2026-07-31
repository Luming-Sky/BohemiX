using System.Text.Json;
using System.Text.Json.Serialization;

namespace BohemiX.Core.Models;

public static class ForgeHostProtocol
{
    public const int Version = 1;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed record ForgeHostMessage(
    int Version,
    string Type,
    long? OwnerHandle = null,
    int? X = null,
    int? Y = null,
    int? Width = null,
    int? Height = null,
    int? Dpi = null,
    bool? Visible = null,
    string? Quality = null,
    string? Error = null)
{
    public static ForgeHostMessage Create(string type) => new(ForgeHostProtocol.Version, type);
}
