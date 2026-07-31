using System;
using System.Globalization;
using System.Linq;
using BohemiX.Core.Models;

namespace BohemiX.App.ViewModels;

public sealed class ModConflictDetailViewModel
{
    public ModConflictDetailViewModel(
        ModConflict conflict,
        ModConflictReview? review,
        string selectedModId,
        Func<string, string> translate)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedModId);
        ArgumentNullException.ThrowIfNull(translate);

        NormalizedVirtualPath = conflict.NormalizedVirtualPath;
        ParticipantCount = conflict.ModIds.Count;
        ModIdsText = string.Join(", ", conflict.ModIds);
        LoadOrderChainText = string.Join(" -> ", conflict.LoadOrderModIds);
        WinningModId = conflict.WinningModId;
        EffectiveModText = conflict.WinningModId;
        OverriddenModsText = CreateOverriddenModsText(conflict, translate);
        IsReviewed = review?.IsReviewed == true;
        IsSelectedModWinner = string.Equals(conflict.WinningModId, selectedModId, StringComparison.OrdinalIgnoreCase);

        ParticipantSummary = string.Format(
            CultureInfo.CurrentCulture,
            translate("ConflictParticipantCount"),
            ParticipantCount);
        LoadOrderChainSummary = string.Format(
            CultureInfo.CurrentCulture,
            translate("ConflictLoadOrderChain"),
            LoadOrderChainText);
        WinnerSummary = string.Format(
            CultureInfo.CurrentCulture,
            translate("ConflictWinnerDetail"),
            WinningModId);
        OutcomeTitleText = IsSelectedModWinner
            ? translate("ConflictOutcomeSelectedWinsTitle")
            : translate("ConflictOutcomeSelectedLosesTitle");
        OutcomeDescriptionText = IsSelectedModWinner
            ? translate("ConflictOutcomeSelectedWinsDescription")
            : string.Format(
                CultureInfo.CurrentCulture,
                translate("ConflictOutcomeSelectedLosesDescription"),
                WinningModId);
        ReviewStatusText = IsReviewed
            ? translate("ConflictReviewReviewed")
            : translate("ConflictReviewPending");
        SelectedModRoleText = IsSelectedModWinner
            ? translate("ConflictRoleWinner")
            : translate("ConflictRoleOverridden");
        FilePathLabelText = translate("ConflictFilePathLabel");
        OverriddenModsLabelText = translate("ConflictOverriddenModsLabel");
        EffectiveModLabelText = translate("ConflictEffectiveModLabel");
        ConflictFlowCaptionText = string.Format(
            CultureInfo.CurrentCulture,
            translate("ConflictFlowCaption"),
            ParticipantCount);
    }

    public string NormalizedVirtualPath { get; }

    public int ParticipantCount { get; }

    public string ParticipantSummary { get; }

    public string ModIdsText { get; }

    public string LoadOrderChainText { get; }

    public string LoadOrderChainSummary { get; }

    public string WinningModId { get; }

    public string EffectiveModText { get; }

    public string OverriddenModsText { get; }

    public string WinnerSummary { get; }

    public string OutcomeTitleText { get; }

    public string OutcomeDescriptionText { get; }

    public bool IsReviewed { get; }

    public bool IsSelectedModWinner { get; }

    public string ReviewStatusText { get; }

    public string SelectedModRoleText { get; }

    public string FilePathLabelText { get; }

    public string OverriddenModsLabelText { get; }

    public string EffectiveModLabelText { get; }

    public string ConflictFlowCaptionText { get; }

    private static string CreateOverriddenModsText(ModConflict conflict, Func<string, string> translate)
    {
        var overriddenMods = conflict.LoadOrderModIds
            .Where(modId => !string.Equals(modId, conflict.WinningModId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return overriddenMods.Length == 0
            ? translate("ConflictNoOverriddenMods")
            : string.Join(" / ", overriddenMods);
    }
}
