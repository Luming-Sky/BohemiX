namespace BohemiX.Core.Models;

public sealed record SaveFileChange(
    string FullPath,
    SaveFileChangeKind Kind,
    DateTimeOffset ObservedAt);

