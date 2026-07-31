namespace BohemiX.Core.Models.Saves;

/// <summary>
/// A three-dimensional world coordinate reserved for future binary save parsing.
/// </summary>
public readonly record struct WorldCoordinate(float X, float Y, float Z);

/// <summary>
/// Parsed world-state data extracted from binary save content.
/// </summary>
public sealed record KcdWorldState(
    string? SourceFile,
    byte[] Signature,
    IReadOnlyList<WorldCoordinate> DiscoveredLocations,
    IReadOnlyList<int> QuestProgressIds);
