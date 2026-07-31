using BohemiX.App.ViewModels;
using BohemiX.Core.Models;

namespace BohemiX.App.Tests;

public sealed class DashboardProjectionTests
{
    [Fact]
    public void ConflictProjection_ExcludesReviewedRowsAndLimitsToThree()
    {
        var pending = Enumerable.Range(0, 4)
            .Select(index => CreateRow($"data/path-{index}.pak", reviewed: false));
        var reviewed = CreateRow("data/reviewed.pak", reviewed: true);

        var result = MainWindowViewModel.SelectDashboardConflictRows([reviewed, .. pending]);

        Assert.Equal(3, result.Count);
        Assert.DoesNotContain(result, row => row.IsReviewed);
        Assert.Equal(
            ["data/path-0.pak", "data/path-1.pak", "data/path-2.pak"],
            result.Select(row => row.NormalizedVirtualPath));
    }

    private static ModConflictRowViewModel CreateRow(string path, bool reviewed)
    {
        var conflict = new ModConflict(
            path,
            ["mod-a", "mod-b"],
            ["mod-a", "mod-b"],
            "mod-b");
        var review = reviewed
            ? new ModConflictReview(conflict.Fingerprint, true, DateTimeOffset.UtcNow)
            : null;
        return new ModConflictRowViewModel(conflict, review);
    }
}
