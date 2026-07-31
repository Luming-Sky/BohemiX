namespace BohemiX.Core.Models;

public sealed record ModFileEntry(
    string RelativePath,
    long SizeInBytes,
    string? ContentHash);

