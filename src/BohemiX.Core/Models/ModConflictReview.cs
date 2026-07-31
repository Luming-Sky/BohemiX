namespace BohemiX.Core.Models;

public sealed record ModConflictReview(
    string Fingerprint,
    bool IsReviewed,
    DateTimeOffset UpdatedUtc);
