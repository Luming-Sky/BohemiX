namespace BohemiX.Core.Models.Saves;

/// <summary>
/// Describes a verified Windows directory junction and its resolved target.
/// </summary>
public sealed record JunctionInfo(
    string LinkPath,
    string TargetPath,
    string SubstituteName,
    string PrintName);
