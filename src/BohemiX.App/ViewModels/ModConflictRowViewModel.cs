using System.Globalization;
using BohemiX.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BohemiX.App.ViewModels;

public sealed partial class ModConflictRowViewModel : ObservableObject
{
    public ModConflictRowViewModel(ModConflict conflict, ModConflictReview? review = null)
    {
        Fingerprint = conflict.Fingerprint;
        NormalizedVirtualPath = conflict.NormalizedVirtualPath;
        ParticipantCount = conflict.ModIds.Count;
        ModIdsText = string.Join(", ", conflict.ModIds);
        LoadOrderChainText = string.Join(" -> ", conflict.LoadOrderModIds);
        WinningModId = conflict.WinningModId;
        isReviewed = review?.IsReviewed == true;
    }

    public string Fingerprint { get; }

    public string NormalizedVirtualPath { get; }

    public int ParticipantCount { get; }

    public string ModIdsText { get; }

    public string LoadOrderChainText { get; }

    public string LoadOrderChainSummary => $"Override chain: {LoadOrderChainText}";

    public string WinningModId { get; }

    public string WinnerSummary => $"Winner: {WinningModId}";

    public string ParticipantSummary => $"{ParticipantCount.ToString(CultureInfo.InvariantCulture)} mods";

    public string ReviewStatusText => IsReviewed ? "Reviewed" : "Pending review";

    [ObservableProperty]
    private bool isReviewed;

    partial void OnIsReviewedChanged(bool value)
    {
        OnPropertyChanged(nameof(ReviewStatusText));
    }
}
