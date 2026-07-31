using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Persistence;
using Dapper;
using Serilog;

namespace BohemiX.Infrastructure.Services;

public sealed class ModConflictReviewService : IModConflictReviewService
{
    private readonly SqliteConnectionFactory connectionFactory;
    private readonly ILogger logger;

    public ModConflictReviewService(SqliteConnectionFactory connectionFactory, ILogger logger)
    {
        this.connectionFactory = connectionFactory;
        this.logger = logger.ForContext<ModConflictReviewService>();
    }

    public async Task<IReadOnlyDictionary<string, ModConflictReview>> LoadReviewsAsync(
        IEnumerable<string> fingerprints,
        CancellationToken cancellationToken = default)
    {
        var keys = fingerprints
            .Where(fingerprint => !string.IsNullOrWhiteSpace(fingerprint))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (keys.Length == 0)
        {
            return new Dictionary<string, ModConflictReview>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            await using var connection = connectionFactory.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            var rows = await connection.QueryAsync<ConflictReviewRow>(new CommandDefinition(
                """
                SELECT
                    fingerprint AS Fingerprint,
                    is_reviewed AS IsReviewed,
                    updated_utc AS UpdatedUtc
                FROM mod_conflict_reviews
                WHERE fingerprint IN @Fingerprints
                """,
                new { Fingerprints = keys },
                cancellationToken: cancellationToken));

            return rows.ToDictionary(
                row => row.Fingerprint,
                row => new ModConflictReview(
                    row.Fingerprint,
                    row.IsReviewed != 0,
                    DateTimeOffset.Parse(row.UpdatedUtc)),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            logger.Warning(ex, "Unable to load mod conflict review states");
            return new Dictionary<string, ModConflictReview>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public async Task SaveReviewAsync(string fingerprint, bool isReviewed, CancellationToken cancellationToken = default)
    {
        await SaveReviewsAsync(
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [fingerprint] = isReviewed
            },
            cancellationToken);
    }

    public async Task SaveReviewsAsync(
        IReadOnlyDictionary<string, bool> reviews,
        CancellationToken cancellationToken = default)
    {
        if (reviews.Count == 0)
        {
            return;
        }

        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var review in reviews)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(review.Key))
            {
                continue;
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO mod_conflict_reviews (fingerprint, is_reviewed, updated_utc)
                VALUES (@Fingerprint, @IsReviewed, @UpdatedUtc)
                ON CONFLICT(fingerprint) DO UPDATE SET
                    is_reviewed = excluded.is_reviewed,
                    updated_utc = excluded.updated_utc
                """,
                new
                {
                    Fingerprint = review.Key,
                    IsReviewed = review.Value ? 1 : 0,
                    UpdatedUtc = DateTimeOffset.UtcNow.ToString("O")
                },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        logger.Information("Saved review state for {ConflictReviewCount} mod conflicts", reviews.Count);
    }

    private sealed class ConflictReviewRow
    {
        public string Fingerprint { get; init; } = string.Empty;

        public int IsReviewed { get; init; }

        public string UpdatedUtc { get; init; } = string.Empty;
    }
}
