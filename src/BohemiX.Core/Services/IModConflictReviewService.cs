using BohemiX.Core.Models;

namespace BohemiX.Core.Services;

/// <summary>
/// Persists user review state for conflict fingerprints without changing mod files or load order.
/// </summary>
public interface IModConflictReviewService
{
    /// <summary>
    /// Loads persisted review states for the requested conflict fingerprints.
    /// </summary>
    Task<IReadOnlyDictionary<string, ModConflictReview>> LoadReviewsAsync(
        IEnumerable<string> fingerprints,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists one conflict review state.
    /// </summary>
    Task SaveReviewAsync(string fingerprint, bool isReviewed, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists multiple conflict review states in one transaction.
    /// </summary>
    Task SaveReviewsAsync(
        IReadOnlyDictionary<string, bool> reviews,
        CancellationToken cancellationToken = default);
}
