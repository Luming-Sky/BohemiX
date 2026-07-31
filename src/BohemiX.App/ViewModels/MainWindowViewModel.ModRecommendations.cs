using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using BohemiX.Core.Models;
using CommunityToolkit.Mvvm.Input;

namespace BohemiX.App.ViewModels;

public partial class MainWindowViewModel
{
    internal Task EnsureLauncherDetailsDataLoadedAsync() => TryLoadDailyModRecommendationAsync();

    private async Task EnsureModRecommendationsLoadedAsync()
    {
        if (ModRecommendationRows.Count > 0 || IsLoadingModRecommendations)
        {
            return;
        }

        await RefreshModRecommendationsAsync();
    }

    private async Task TryLoadDailyModRecommendationAsync()
    {
        if (IsDailyModRecommendationLoading)
        {
            return;
        }

        DailyModRecommendationDateText = FormatDailyModRecommendationDate(DateTimeOffset.Now);
        if (ModRecommendationRows.Count > 0)
        {
            UpdateDailyModRecommendation();
            return;
        }

        var boundAccount = await nexusAccountService.GetBoundAccountAsync();
        var apiKey = await nexusApiKeyStore.GetApiKeyAsync();
        if (boundAccount is null && string.IsNullOrWhiteSpace(apiKey))
        {
            DailyModRecommendationStatusText = T("DailyModRecommendationConnect");
            return;
        }

        try
        {
            IsDailyModRecommendationLoading = true;
            DailyModRecommendationStatusText = T("DailyModRecommendationLoading");
            nexusApiKeyStore.SetApiKey(NexusApiKey);
            EnsureModRecommendationRefreshState();
            var candidates = await LoadModRecommendationCandidatesAsync();
            BuildModRecommendations(candidates);
            DailyModRecommendationStatusText = HasDailyModRecommendation
                ? T("DailyModRecommendationReady")
                : T("DailyModRecommendationUnavailable");
        }
        catch (Exception ex) when (ex is NexusModsException or HttpRequestException or InvalidOperationException)
        {
            DailyModRecommendationStatusText = T("DailyModRecommendationUnavailable");
        }
        finally
        {
            IsDailyModRecommendationLoading = false;
        }
    }

    private void UpdateDailyModRecommendation()
    {
        var nextRecommendation = SelectDailyModRecommendationForDate(
            ModRecommendationRows,
            DateOnly.FromDateTime(DateTime.Today));
        var recommendationChanged = !ReferenceEquals(
            DailyModRecommendation?.Mod,
            nextRecommendation?.Mod);

        DailyModRecommendation = nextRecommendation;
        DailyModRecommendationDateText = FormatDailyModRecommendationDate(DateTimeOffset.Now);
        DailyModRecommendationStatusText = HasDailyModRecommendation
            ? T("DailyModRecommendationReady")
            : T("DailyModRecommendationUnavailable");

        if (!recommendationChanged && DailyModRecommendation?.Mod.HasThumbnail == true)
        {
            DailyModRecommendation?.Mod.ActivateVisualResources();
            return;
        }

        dailyModCoverGeneration++;
        dailyModCoverCancellation?.Cancel();
        dailyModCoverCancellation?.Dispose();
        dailyModCoverCancellation = new CancellationTokenSource();

        if (nextRecommendation is not null)
        {
            _ = EnsureDailyModCoverAsync(
                nextRecommendation.Mod,
                dailyModCoverGeneration,
                dailyModCoverCancellation.Token);
        }
    }

    private async Task EnsureDailyModCoverAsync(
        NexusModSearchRowViewModel mod,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(mod.RemoteThumbnailUrl))
            {
                return;
            }

            var cachedPath = modCoverCacheService.GetCachedCoverPath(mod.ThumbnailCacheId);
            for (var attempt = 0; cachedPath is null && attempt < DailyModCoverRetryCount; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(700 * attempt), cancellationToken);
                }

                cachedPath = await modCoverCacheService.CacheCoverAsync(
                    mod.ThumbnailCacheId,
                    mod.RemoteThumbnailUrl,
                    cancellationToken);
            }

            await Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    if (generation != dailyModCoverGeneration
                        || cancellationToken.IsCancellationRequested
                        || !ReferenceEquals(DailyModRecommendation?.Mod, mod))
                    {
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(cachedPath))
                    {
                        mod.SetThumbnailPath(cachedPath, activateVisualResources: false);
                    }

                    if (launcherDetailsVisualResourcesActive)
                    {
                        mod.ActivateVisualResources();
                    }
                },
                DispatcherPriority.Background,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal static ModRecommendationRowViewModel? SelectDailyModRecommendationForDate(
        IReadOnlyList<ModRecommendationRowViewModel> recommendations,
        DateOnly date)
    {
        if (recommendations.Count == 0)
        {
            return null;
        }

        var topRecommendations = recommendations.Take(5).ToArray();
        var recommendationsWithArtwork = topRecommendations
            .Where(item => !string.IsNullOrWhiteSpace(item.Mod.RemoteThumbnailUrl))
            .ToArray();
        var candidates = recommendationsWithArtwork.Length > 0
            ? recommendationsWithArtwork
            : topRecommendations;
        var index = Math.Abs(date.DayNumber % candidates.Length);
        return candidates[index];
    }

    private string FormatDailyModRecommendationDate(DateTimeOffset date)
    {
        return string.Equals(SelectedLanguage, SimplifiedChineseLanguage, StringComparison.OrdinalIgnoreCase)
            ? date.ToLocalTime().ToString("M月d日", CultureInfo.GetCultureInfo("zh-CN"))
            : date.ToLocalTime().ToString("MMM d", CultureInfo.GetCultureInfo("en-US"));
    }

    [RelayCommand]
    private void OpenDailyModRecommendation()
    {
        if (DailyModRecommendation is not null)
        {
            ViewNexusModDetails(DailyModRecommendation.Mod);
            return;
        }

        OpenDailyModRecommendations();
    }

    [RelayCommand]
    private void OpenDailyModRecommendations()
    {
        NavigateCore("Mods", silent: false);
        SelectModManagerPage("Recommendation");
    }

    [RelayCommand]
    private async Task RefreshModRecommendationsAsync()
    {
        if (IsLoadingModRecommendations)
        {
            return;
        }

        try
        {
            IsLoadingModRecommendations = true;
            ModRecommendationStatusText = T("LoadingModRecommendations");
            StatusText = ModRecommendationStatusText;

            if (!await EnsureNexusAccountBoundAsync())
            {
                return;
            }

            nexusApiKeyStore.SetApiKey(NexusApiKey);
            EnsureModRecommendationRefreshState();
            var candidates = await LoadModRecommendationCandidatesAsync();
            BuildModRecommendations(candidates);
            ModRecommendationStatusText = string.Format(T("ModRecommendationsReady"), ModRecommendationRows.Count);
            StatusText = ModRecommendationStatusText;
            LastActionText = StatusText;
        }
        catch (Exception ex) when (ex is NexusModsException or HttpRequestException or InvalidOperationException)
        {
            var fallbackCandidates = CreateFallbackModRecommendationCandidates();
            BuildModRecommendations(fallbackCandidates);
            ModRecommendationStatusText = string.Format(T("ModRecommendationsFallback"), ex.Message);
            StatusText = ModRecommendationStatusText;
            LastActionText = StatusText;
        }
        finally
        {
            IsLoadingModRecommendations = false;
        }
    }

    [RelayCommand]
    private async Task SelectModRecommendationStrategyAsync(string strategyKey)
    {
        SelectedModRecommendationStrategy = NormalizeModRecommendationStrategy(strategyKey);
        RefreshModRecommendationStrategyFilters();
        ResetModRecommendationRefreshState();
        await RefreshModRecommendationsAsync();
    }

    [RelayCommand]
    private void ToggleModRecommendationQualifier(ModRecommendationQualifierViewModel? qualifier)
    {
        if (qualifier is null)
        {
            return;
        }

        if (qualifier.IsSelected)
        {
            RemoveModRecommendationQualifierByValue(qualifier.Value);
            return;
        }

        AddModRecommendationQualifier(qualifier.Value, qualifier.DisplayName);
    }

    [RelayCommand(CanExecute = nameof(CanAddModRecommendationQualifier))]
    private void AddModRecommendationQualifier()
    {
        AddModRecommendationQualifier(RecommendationQualifierInput, null);
    }

    private void AddModRecommendationQualifier(string value, string? displayName)
    {
        var qualifier = NormalizeModRecommendationQualifier(value);
        if (string.IsNullOrWhiteSpace(qualifier))
        {
            RecommendationQualifierInput = string.Empty;
            return;
        }

        if (!ModRecommendationQualifiers.Any(row => string.Equals(row.Value, qualifier, StringComparison.OrdinalIgnoreCase)))
        {
            ModRecommendationQualifiers.Add(new ModRecommendationQualifierViewModel(qualifier, displayName));
        }

        RecommendationQualifierInput = string.Empty;
        RefreshModRecommendationQualifierState();
        ResetModRecommendationRefreshState();
    }

    [RelayCommand]
    private void RemoveModRecommendationQualifier(ModRecommendationQualifierViewModel? qualifier)
    {
        if (qualifier is null)
        {
            return;
        }

        ModRecommendationQualifiers.Remove(qualifier);
        RefreshModRecommendationQualifierState();
        ResetModRecommendationRefreshState();
    }

    [RelayCommand]
    private void ClearModRecommendationQualifiers()
    {
        if (ModRecommendationQualifiers.Count == 0)
        {
            return;
        }

        ModRecommendationQualifiers.Clear();
        RefreshModRecommendationQualifierState();
        ResetModRecommendationRefreshState();
    }

    private void RemoveModRecommendationQualifierByValue(string value)
    {
        var normalized = NormalizeModRecommendationQualifier(value);
        var qualifier = ModRecommendationQualifiers.FirstOrDefault(row => string.Equals(row.Value, normalized, StringComparison.OrdinalIgnoreCase));
        if (qualifier is null)
        {
            return;
        }

        ModRecommendationQualifiers.Remove(qualifier);
        RefreshModRecommendationQualifierState();
        ResetModRecommendationRefreshState();
    }

    private bool CanAddModRecommendationQualifier()
    {
        return !string.IsNullOrWhiteSpace(RecommendationQualifierInput);
    }

    [RelayCommand]
    private void SelectModRecommendation(ModRecommendationRowViewModel? recommendation)
    {
        if (recommendation is null)
        {
            return;
        }

        SelectedModRecommendation = recommendation;
    }

    private void EnsureModRecommendationRefreshState()
    {
        var signature = BuildModRecommendationSignature();
        if (!string.Equals(activeModRecommendationSignature, signature, StringComparison.Ordinal))
        {
            ResetModRecommendationRefreshState();
            activeModRecommendationSignature = signature;
        }
    }

    private void ResetModRecommendationRefreshState(bool keepCurrentSignature = false)
    {
        modRecommendationRefreshPage = 0;
        shownModRecommendationIdentities.Clear();
        if (!keepCurrentSignature)
        {
            activeModRecommendationSignature = BuildModRecommendationSignature();
        }
    }

    private string BuildModRecommendationSignature()
    {
        var qualifiers = ModRecommendationQualifiers
            .Select(row => NormalizeModRecommendationQualifier(row.Value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Order(StringComparer.OrdinalIgnoreCase);

       return $"{NormalizeModRecommendationStrategy(SelectedModRecommendationStrategy)}:{string.Join('|', qualifiers)}";
   }
    private async Task<IReadOnlyList<NexusModSearchRowViewModel>> LoadModRecommendationCandidatesAsync()
    {
        var recommendationQueries = BuildModRecommendationQueries(SelectedModRecommendationStrategy);
        var candidates = new List<NexusModSearchRowViewModel>();
        for (var attempt = 0; attempt < ModRecommendationPageRetryCount && candidates.Count == 0; attempt++)
        {
            var page = modRecommendationRefreshPage++;
            var strategyCandidates = await LoadModRecommendationQueryCandidatesAsync(recommendationQueries, page);

            candidates.AddRange(strategyCandidates
                .Where(row => !shownModRecommendationIdentities.Contains(GetModSearchRowIdentity(row))));

            foreach (var filtered in strategyCandidates.Where(row => shownModRecommendationIdentities.Contains(GetModSearchRowIdentity(row))))
            {
                filtered.Dispose();
            }
        }

        return candidates
            .GroupBy(GetModSearchRowIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private async Task<IReadOnlyList<NexusModSearchRowViewModel>> LoadModRecommendationQueryCandidatesAsync(IReadOnlyList<string> queries, int page)
    {
        using var throttle = new SemaphoreSlim(ModRecommendationQueryConcurrency);
        var offset = Math.Max(0, page) * ModRecommendationCandidateCount;
        var tasks = queries.Select(async query =>
        {
            await throttle.WaitAsync();
            try
            {
                var result = await nexusModService.SearchModsAsync(new NexusModSearchRequest(
                    "kingdomcomedeliverance2",
                    query,
                    offset,
                    ModRecommendationCandidateCount));

                return result.Mods.Select(mod => new NexusModSearchRowViewModel(mod)).ToArray();
            }
            finally
            {
                throttle.Release();
            }
        });

        var batches = await Task.WhenAll(tasks);
        return batches.SelectMany(batch => batch).ToArray();
    }

    private void BuildModRecommendations(IReadOnlyList<NexusModSearchRowViewModel> candidates)
    {
        var previousMods = ModRecommendationRows
            .Select(row => row.Mod)
            .Append(DailyModRecommendation?.Mod)
            .Where(row => row is not null)
            .Cast<NexusModSearchRowViewModel>()
            .Distinct()
            .ToArray();
        var strategy = NormalizeModRecommendationStrategy(SelectedModRecommendationStrategy);
        SelectedModRecommendationStrategy = strategy;
        RefreshModRecommendationStrategyFilters();

        var installedProfile = BuildInstalledModProfile();
        var qualifierTokens = GetModRecommendationQualifierTokens();
        var candidateRows = candidates
            .Where(row => row.ModId > 0 && !row.IsSteamWorkshop)
            .Where(row => !IsModSearchRowDownloaded(row))
            .GroupBy(GetModSearchRowIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (candidateRows.Length == 0)
        {
            var emptyRetainedMods = new HashSet<NexusModSearchRowViewModel>(ReferenceEqualityComparer.Instance);
            ModRecommendationRows.Clear();
            HasModRecommendations = false;
            IsModRecommendationEmpty = true;
            SelectedModRecommendation = null;
            DailyModRecommendation = null;
            DailyModRecommendationStatusText = T("DailyModRecommendationUnavailable");
            DisposeUnretainedModRecommendationCandidates(candidates, emptyRetainedMods);
            DisposeUnretainedModRecommendationCandidates(previousMods, emptyRetainedMods);
            RefreshModRecommendationDownloadStates();
            return;
        }

        var maxLogDownloads = Math.Max(1, candidateRows.Max(row => Math.Log10(row.Downloads + 1)));
        var scoredRows = candidateRows
            .Select(row => CreateModRecommendation(row, strategy, installedProfile, qualifierTokens, maxLogDownloads))
            .OrderByDescending(row => row.Score)
            .ThenByDescending(row => row.QualityScore)
            .ThenByDescending(row => row.StabilityScore)
            .ThenByDescending(row => row.Mod.Downloads)
            .ThenByDescending(row => row.Mod.UpdatedAt ?? DateTimeOffset.MinValue)
            .ToArray();
        var rows = SelectDiverseModRecommendations(scoredRows).ToList();
        var retainedMods = new HashSet<NexusModSearchRowViewModel>(
            rows.Select(row => row.Mod),
            ReferenceEqualityComparer.Instance);

        for (var index = 0; index < rows.Count; index++)
        {
            rows[index].RankText = (index + 1).ToString("00", CultureInfo.InvariantCulture);
            rows[index].ApplyLocalization(T);
            rows[index].UseSummaryLanguage(GetModSummaryTargetLanguage(), T);
            rows[index].ApplyDownloadState(IsModSearchRowDownloaded(rows[index].Mod), T);
        }

        ModRecommendationRows.Clear();
        foreach (var row in rows)
        {
            ModRecommendationRows.Add(row);
            shownModRecommendationIdentities.Add(GetModSearchRowIdentity(row.Mod));
        }

        HasModRecommendations = ModRecommendationRows.Count > 0;
        IsModRecommendationEmpty = !HasModRecommendations;
        SelectedModRecommendation = ModRecommendationRows.FirstOrDefault();
        UpdateDailyModRecommendation();
        CacheNewModCovers(ModRecommendationRows.Select(row => row.Mod).ToArray());
        RefreshModRecommendationDownloadStates();
        DisposeUnretainedModRecommendationCandidates(candidates, retainedMods);
        DisposeUnretainedModRecommendationCandidates(previousMods, retainedMods);
    }

    private static void DisposeUnretainedModRecommendationCandidates(
        IEnumerable<NexusModSearchRowViewModel> candidates,
        IReadOnlySet<NexusModSearchRowViewModel> retained)
    {
        foreach (var candidate in candidates.Distinct<NexusModSearchRowViewModel>(ReferenceEqualityComparer.Instance))
        {
            if (!retained.Contains(candidate))
            {
                candidate.Dispose();
            }
        }
    }

    private ModRecommendationRowViewModel CreateModRecommendation(
        NexusModSearchRowViewModel mod,
        string strategy,
        IReadOnlySet<string> installedProfile,
        IReadOnlySet<string> qualifierTokens,
        double maxLogDownloads)
    {
        var category = DetectModRecommendationCategory(mod);
        var tokens = TokenizeRecommendationText($"{mod.Name} {mod.Summary} {mod.Author} {mod.MetaText}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var breakdown = CreateModRecommendationScoreBreakdown(mod, category, installedProfile, qualifierTokens, tokens, maxLogDownloads);
        var score = CalculateModRecommendationScore(strategy, breakdown);

        var strategyText = GetModRecommendationStrategyDisplay(strategy);
        return new ModRecommendationRowViewModel(
            mod,
            strategy,
            strategyText,
            GetModDownloadCategoryDisplay(category),
            score,
            breakdown.ContentScore,
            breakdown.PopularityScore,
            breakdown.QualityScore,
            breakdown.FreshnessScore,
            breakdown.StabilityScore,
            BuildModRecommendationReason(strategy, category, breakdown),
            BuildModRecommendationReasonDetail(mod, strategy, category, breakdown));
    }

    internal static IReadOnlyList<ModRecommendationRowViewModel> RankModRecommendationCandidatesForTesting(
        IReadOnlyList<NexusModSearchRowViewModel> candidates,
        string strategy,
        IReadOnlyList<string> qualifiers)
    {
        var normalizedStrategy = NormalizeModRecommendationStrategy(strategy);
        var installedProfile = new[] { "ui", "inventory", "visual", "performance", "fix", "quality", "save" }
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var qualifierTokens = BuildModRecommendationQualifierTokens(qualifiers);
        var candidateRows = candidates
            .Where(row => row.ModId > 0 && !row.IsSteamWorkshop)
            .GroupBy(GetModSearchRowIdentity, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var maxLogDownloads = Math.Max(1, candidateRows.Max(row => Math.Log10(row.Downloads + 1)));

        return candidateRows
            .Select(row => CreateModRecommendationForScoring(row, normalizedStrategy, installedProfile, qualifierTokens, maxLogDownloads, key => key))
            .OrderByDescending(row => row.Score)
            .ThenByDescending(row => row.QualityScore)
            .ThenByDescending(row => row.StabilityScore)
            .ThenByDescending(row => row.Mod.Downloads)
            .ThenByDescending(row => row.Mod.UpdatedAt ?? DateTimeOffset.MinValue)
            .ToArray();
    }

    private static ModRecommendationRowViewModel CreateModRecommendationForScoring(
        NexusModSearchRowViewModel mod,
        string strategy,
        IReadOnlySet<string> installedProfile,
        IReadOnlySet<string> qualifierTokens,
        double maxLogDownloads,
        Func<string, string> translate)
    {
        var category = DetectModRecommendationCategory(mod);
        var tokens = TokenizeRecommendationText($"{mod.Name} {mod.Summary} {mod.Author} {mod.MetaText}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var breakdown = CreateModRecommendationScoreBreakdown(mod, category, installedProfile, qualifierTokens, tokens, maxLogDownloads);
        var score = CalculateModRecommendationScore(strategy, breakdown);

        return new ModRecommendationRowViewModel(
            mod,
            strategy,
            GetModRecommendationStrategyDisplay(strategy, translate),
            GetModDownloadCategoryDisplay(category, translate),
            score,
            breakdown.ContentScore,
            breakdown.PopularityScore,
            breakdown.QualityScore,
            breakdown.FreshnessScore,
            breakdown.StabilityScore,
            BuildModRecommendationReason(strategy, category, breakdown, translate),
            BuildModRecommendationReasonDetail(mod, strategy, category, breakdown, translate));
    }

    private IReadOnlySet<string> BuildInstalledModProfile()
    {
        var tokens = currentModManifests
            .SelectMany(mod => TokenizeRecommendationText($"{mod.Id} {mod.DisplayName} {Path.GetFileName(mod.RootPath)}"))
            .Where(token => token.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (tokens.Count == 0)
        {
            tokens.UnionWith(["ui", "inventory", "visual", "performance", "fix", "quality", "save"]);
        }

        return tokens;
    }

    private IReadOnlySet<string> GetModRecommendationQualifierTokens()
    {
        return BuildModRecommendationQualifierTokens(ModRecommendationQualifiers.Select(row => row.Value));
    }

    internal static IReadOnlySet<string> BuildModRecommendationQualifierTokens(IEnumerable<string> qualifiers)
    {
        return qualifiers
            .Select(NormalizeModRecommendationQualifier)
            .SelectMany(ExpandModRecommendationQualifierTokens)
            .Where(token => token.Length >= 2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ExpandModRecommendationQualifierTokens(string qualifier)
    {
        foreach (var token in TokenizeRecommendationText(qualifier))
        {
            yield return token;
        }

        foreach (var token in qualifier.ToLowerInvariant() switch
        {
            "performance" => new[] { "performance", "fps", "optimized", "optimised", "stutter", "lag", "fix", "patch" },
            "ui" => new[] { "ui", "hud", "interface", "menu", "inventory", "map", "readability" },
            "chinese" => new[] { "chinese", "china", "cn", "simplified", "translation", "localization", "localisation" },
            "visual" => new[] { "visual", "graphics", "texture", "lighting", "reshade", "model", "appearance" },
            "combat" => new[] { "combat", "battle", "weapon", "sword", "bow", "enemy", "ai" },
            "save" => new[] { "save", "saving", "autosave", "checkpoint", "slot" },
            "compatibility" => new[] { "compatibility", "compatible", "stable", "stability", "patch", "fix" },
            "low risk" => new[] { "low", "risk", "stable", "stability", "compatible", "fix", "patch" },
            "utility" => new[] { "utility", "tool", "framework", "manager", "script", "helper" },
            "items" => new[] { "item", "items", "weapon", "armor", "equipment", "clothing" },
            _ => Array.Empty<string>()
        })
        {
           yield return token;
       }
   }
    private static RecommendationScoreBreakdown CreateModRecommendationScoreBreakdown(
        NexusModSearchRowViewModel mod,
        string category,
        IReadOnlySet<string> installedProfile,
        IReadOnlySet<string> qualifierTokens,
        IReadOnlySet<string> tokens,
        double maxLogDownloads)
    {
        var contentScore = CalculateContentRecommendationScore(category, installedProfile, qualifierTokens, tokens);
        var popularityScore = CalculatePopularityRecommendationScore(mod, maxLogDownloads);
        var qualityScore = CalculateQualityRecommendationScore(mod);
        var freshnessScore = CalculateFreshnessRecommendationScore(mod);
        var stabilityScore = CalculateStabilityRecommendationScore(mod, tokens);
        var installScore = mod.DirectDownloadEnabled ? 100 : 38;
        var riskPenalty = CalculateRiskPenalty(tokens);
        var reasons = BuildRecommendationReasonSignals(mod, tokens, qualifierTokens, freshnessScore, stabilityScore, riskPenalty);

        return new RecommendationScoreBreakdown(
            contentScore,
            popularityScore,
            qualityScore,
            freshnessScore,
            stabilityScore,
            installScore,
            riskPenalty,
            reasons);
    }

    private static double CalculateModRecommendationScore(string strategy, RecommendationScoreBreakdown breakdown)
    {
        var score = NormalizeModRecommendationStrategy(strategy) switch
        {
            "Content" => breakdown.ContentScore * 0.40
                + breakdown.QualityScore * 0.20
                + breakdown.StabilityScore * 0.16
                + breakdown.InstallConvenienceScore * 0.10
                + breakdown.FreshnessScore * 0.08
                + breakdown.PopularityScore * 0.06,
            "PopularityQuality" => breakdown.QualityScore * 0.30
                + breakdown.StabilityScore * 0.22
                + breakdown.PopularityScore * 0.20
                + breakdown.FreshnessScore * 0.12
                + breakdown.InstallConvenienceScore * 0.10
                + breakdown.ContentScore * 0.06,
            _ => breakdown.StabilityScore * 0.28
                + breakdown.QualityScore * 0.24
                + breakdown.InstallConvenienceScore * 0.16
                + breakdown.ContentScore * 0.14
                + breakdown.FreshnessScore * 0.10
                + breakdown.PopularityScore * 0.08
        };

        return Math.Clamp(score - breakdown.RiskPenalty, 0, 100);
    }

    private static double CalculateContentRecommendationScore(
        string category,
        IReadOnlySet<string> installedProfile,
        IReadOnlySet<string> qualifierTokens,
        IReadOnlySet<string> tokens)
    {
        var profileMatches = tokens.Count(installedProfile.Contains);
        var categoryMatches = GetModRecommendationCategoryKeywords(category).Count(tokens.Contains);
        var starterMatches = GetStarterFriendlyRecommendationKeywords().Count(tokens.Contains);
        var qualifierMatches = qualifierTokens.Count(tokens.Contains);
        var baseScore = Math.Clamp(30 + profileMatches * 6 + categoryMatches * 9 + starterMatches * 6, 0, 86);
        return Math.Clamp(baseScore + qualifierMatches * 14, 0, 100);
    }

    private static double CalculatePopularityRecommendationScore(NexusModSearchRowViewModel mod, double maxLogDownloads)
    {
        var logDownloads = Math.Log10(Math.Max(0, mod.Downloads) + 1);
        return Math.Clamp(logDownloads / Math.Max(1, maxLogDownloads) * 100d, 0, 100);
    }

    private static double CalculateQualityRecommendationScore(NexusModSearchRowViewModel mod)
    {
        var downloads = Math.Max(0, mod.Downloads);
        var endorsements = Math.Max(0, mod.Endorsements);
        const double priorDownloads = 5000d;
        const double priorRate = 0.045d;
        var bayesianEndorsementRate = (endorsements + priorDownloads * priorRate) / Math.Max(1d, downloads + priorDownloads);
        var endorsementScore = Math.Clamp(bayesianEndorsementRate / 0.075d * 58d, 0, 58);
        var sampleConfidence = Math.Clamp(Math.Log10(downloads + 1) / 5d * 18d, 0, 18);
        var statusBonus = mod.Status.Contains("published", StringComparison.OrdinalIgnoreCase)
            || mod.Status.Contains("workshop", StringComparison.OrdinalIgnoreCase)
            || mod.Status == "-"
                ? 14
                : 5;
        return Math.Clamp(18 + endorsementScore + sampleConfidence + statusBonus, 0, 100);
    }

    private static double CalculateFreshnessRecommendationScore(NexusModSearchRowViewModel mod)
    {
        if (mod.UpdatedAt is null)
        {
            return 48;
        }

        var ageDays = Math.Max(0, (DateTimeOffset.UtcNow.Date - mod.UpdatedAt.Value.ToUniversalTime().Date).TotalDays);
        return ageDays switch
        {
            <= 30 => 100,
            <= 90 => 88 - (ageDays - 30) * 0.20,
            <= 180 => 76 - (ageDays - 90) * 0.18,
            <= 365 => 60 - (ageDays - 180) * 0.08,
            _ => 38
        };
    }

    private static double CalculateStabilityRecommendationScore(NexusModSearchRowViewModel mod, IReadOnlySet<string> tokens)
    {
        var stableMatches = GetStarterFriendlyRecommendationKeywords().Count(tokens.Contains);
        var riskPenalty = CalculateRiskPenalty(tokens);
        var statusBonus = mod.Status.Contains("published", StringComparison.OrdinalIgnoreCase) || mod.Status == "-" ? 8 : 0;
        return Math.Clamp(58 + stableMatches * 7 + statusBonus + (mod.DirectDownloadEnabled ? 7 : 0) - riskPenalty * 0.85, 0, 100);
    }

    private static double CalculateRiskPenalty(IReadOnlySet<string> tokens)
    {
        var exactRisk = GetRiskyRecommendationKeywords().Count(tokens.Contains);
        var phraseRisk = tokens.Contains("total") && tokens.Contains("conversion") ? 1 : 0;
        return Math.Clamp(exactRisk * 10 + phraseRisk * 14, 0, 34);
    }

    private static IReadOnlyList<string> BuildRecommendationReasonSignals(
        NexusModSearchRowViewModel mod,
        IReadOnlySet<string> tokens,
        IReadOnlySet<string> qualifierTokens,
        double freshnessScore,
        double stabilityScore,
        double riskPenalty)
    {
        var signals = new List<string>(4);
        if (stabilityScore >= 78)
        {
            signals.Add("stable-first");
        }

        if (mod.DirectDownloadEnabled)
        {
            signals.Add("manager-download");
        }

        if (tokens.Any(qualifierTokens.Contains))
        {
            signals.Add("qualifier-match");
        }

        if (freshnessScore >= 72)
        {
            signals.Add("recently-maintained");
        }

        if (GetStarterFriendlyRecommendationKeywords().Any(tokens.Contains))
        {
            signals.Add("starter-friendly");
        }

        signals.Add(riskPenalty <= 0 ? "low-risk" : "risk-filtered");
        return signals.Distinct(StringComparer.Ordinal).Take(4).ToArray();
    }

    private string BuildModRecommendationReason(
        string strategy,
        string category,
        RecommendationScoreBreakdown breakdown)
    {
        return BuildModRecommendationReason(strategy, category, breakdown, T);
    }

    private static string BuildModRecommendationReason(
        string strategy,
        string category,
        RecommendationScoreBreakdown breakdown,
        Func<string, string> translate)
    {
        var signals = string.Join(" / ", breakdown.ReasonSignals.Select(signal => TranslateRecommendationReasonSignal(signal, translate)));
        return strategy switch
        {
            "Content" => $"{GetModDownloadCategoryDisplay(category, translate)} · {breakdown.ContentScore:0}% match · {signals}",
            "Starter" => $"{breakdown.StabilityScore:0}% stable · {signals}",
            _ => $"{breakdown.QualityScore:0}% quality · {breakdown.PopularityScore:0}% popular · {signals}"
        };
    }

    private string BuildModRecommendationReasonDetail(
        NexusModSearchRowViewModel mod,
        string strategy,
        string category,
        RecommendationScoreBreakdown breakdown)
    {
        return BuildModRecommendationReasonDetail(mod, strategy, category, breakdown, T);
    }

    private static string BuildModRecommendationReasonDetail(
        NexusModSearchRowViewModel mod,
        string strategy,
        string category,
        RecommendationScoreBreakdown breakdown,
        Func<string, string> translate)
    {
        var source = mod.DirectDownloadEnabled ? translate("ManagerDownload") : translate("BrowserSignInOrApiKeyRequired");
        return string.Format(
            translate("RecommendationReasonDetail"),
            GetModRecommendationStrategyDisplay(strategy, translate),
            GetModDownloadCategoryDisplay(category, translate),
            breakdown.FreshnessScore,
            breakdown.StabilityScore,
            source);
    }

    private static string DetectModRecommendationCategory(NexusModSearchRowViewModel mod)
    {
        var scores = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Gameplay"] = GetModCategoryMatchScore(mod, "Gameplay"),
            ["Visuals"] = GetModCategoryMatchScore(mod, "Visuals"),
            ["Interface"] = GetModCategoryMatchScore(mod, "Interface"),
            ["Items"] = GetModCategoryMatchScore(mod, "Items"),
            ["Utilities"] = GetModCategoryMatchScore(mod, "Utilities")
        };

        var best = scores.OrderByDescending(pair => pair.Value).First();
        return best.Value > 0 ? best.Key : "Gameplay";
    }

    private IReadOnlyList<string> BuildModRecommendationQueries(string strategy)
    {
        return BuildModRecommendationQueries(strategy, ModRecommendationQualifiers.Select(row => row.Value));
    }

    internal static IReadOnlyList<string> BuildModRecommendationQueriesForTesting(string strategy, IEnumerable<string> qualifiers)
    {
        return BuildModRecommendationQueries(strategy, qualifiers);
    }

    private static IReadOnlyList<string> BuildModRecommendationQueries(string strategy, IEnumerable<string> selectedQualifiers)
    {
        string[] queries = NormalizeModRecommendationStrategy(strategy) switch
        {
            "Content" => ["inventory ui", "quality of life", "visual texture", "combat gameplay", "interface", "gameplay"],
            "Starter" => ["fix performance", "quality of life", "inventory ui", "save", "compatibility", "performance"],
            _ => ["popular", "quality of life", "fix performance", "visual", "interface", "gameplay"]
        };

        var qualifiers = selectedQualifiers
            .Select(NormalizeModRecommendationQualifier)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
        if (qualifiers.Length == 0)
        {
            return queries;
        }

        var strategyAnchors = NormalizeModRecommendationStrategy(strategy) switch
        {
            "Content" => new[] { "quality of life", "interface", "gameplay" },
            "Starter" => new[] { "fix", "performance", "compatibility", "quality of life" },
            _ => new[] { "popular", "quality", "essential" }
        };

        return qualifiers
            .Concat(qualifiers.SelectMany(qualifier => strategyAnchors.Select(anchor => $"{qualifier} {anchor}")))
            .Concat(queries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
    }

    internal static string BuildModRecommendationCandidateCacheKeyForTesting(string strategy, IReadOnlyList<string> queries, int page)
    {
        return BuildModRecommendationCandidateCacheKey(strategy, queries, page);
    }

    private static string BuildModRecommendationCandidateCacheKey(string strategy, IReadOnlyList<string> queries, int page)
    {
        return $"{NormalizeModRecommendationStrategy(strategy)}:{page}:{string.Join('|', queries.Order(StringComparer.OrdinalIgnoreCase))}";
    }

    private static string NormalizeModRecommendationQualifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(' ', value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private void RefreshModRecommendationQualifierState()
    {
        HasRecommendationQualifiers = ModRecommendationQualifiers.Count > 0;
        foreach (var available in AvailableModRecommendationQualifiers)
        {
            available.IsSelected = ModRecommendationQualifiers.Any(selected => string.Equals(selected.Value, available.Value, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void RefreshAvailableModRecommendationQualifiers()
    {
        var selectedValues = ModRecommendationQualifiers
            .Select(row => row.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        AvailableModRecommendationQualifiers.Clear();
        foreach (var qualifier in CreateAvailableModRecommendationQualifiers())
        {
            qualifier.IsSelected = selectedValues.Contains(qualifier.Value);
            AvailableModRecommendationQualifiers.Add(qualifier);
        }
    }

    private IReadOnlyList<ModRecommendationQualifierViewModel> CreateAvailableModRecommendationQualifiers()
    {
        return
        [
            new("performance", T("RecommendationQualifierPerformance")),
            new("ui", T("RecommendationQualifierUi")),
            new("chinese", T("RecommendationQualifierChinese")),
            new("visual", T("RecommendationQualifierVisual")),
            new("combat", T("RecommendationQualifierCombat")),
            new("save", T("RecommendationQualifierSave")),
            new("compatibility", T("RecommendationQualifierCompatibility")),
            new("low risk", T("RecommendationQualifierLowRisk")),
            new("utility", T("RecommendationQualifierUtility")),
            new("items", T("RecommendationQualifierItems"))
        ];
    }

    private static string GetModSearchRowIdentity(NexusModSearchRowViewModel row)
    {
        return row.ModId > 0 ? $"{row.SourceKey}:{row.ModId}" : $"{row.SourceKey}:{row.DisplayId}";
    }

    private static IReadOnlyList<ModRecommendationRowViewModel> SelectDiverseModRecommendations(IReadOnlyList<ModRecommendationRowViewModel> rows)
    {
        var selected = new List<ModRecommendationRowViewModel>(ModRecommendationMaxResults);
        var categoryCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (selected.Count >= ModRecommendationMaxResults)
            {
                break;
            }

            var category = row.CategoryText;
            categoryCounts.TryGetValue(category, out var count);
            if (count >= ModRecommendationMaxPerCategory)
            {
                continue;
            }

            selected.Add(row);
            categoryCounts[category] = count + 1;
        }

        foreach (var row in rows)
        {
            if (selected.Count >= ModRecommendationMaxResults)
            {
                break;
            }

            if (!selected.Contains(row))
            {
                selected.Add(row);
            }
        }

        return selected;
    }

    private IReadOnlyList<NexusModSearchRowViewModel> CreateFallbackModRecommendationCandidates()
    {
        return NexusModSearchRows
            .Where(row => row.ModId > 0 && !row.IsSteamWorkshop)
            .ToArray();
    }

    private static IEnumerable<string> TokenizeRecommendationText(string value)
    {
        return value
            .Split([' ', '-', '_', '.', ',', ';', ':', '/', '\\', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.ToLowerInvariant())
            .Where(token => token.Length > 1);
    }

    private static IReadOnlyList<string> GetStarterFriendlyRecommendationKeywords()
    {
        return
        [
            "fix",
            "patch",
            "stable",
            "stability",
            "performance",
            "optimized",
            "optimised",
            "compatibility",
            "compatible",
            "qol",
            "quality",
            "ui",
            "hud",
            "inventory",
            "save",
            "readability",
            "starter",
            "essential"
        ];
    }

    private static IReadOnlyList<string> GetRiskyRecommendationKeywords()
    {
        return
        [
            "overhaul",
            "experimental",
            "beta",
            "alpha",
            "hardcore",
            "rebalance",
            "conversion",
            "cheat",
            "difficulty"
        ];
    }

    private string TranslateRecommendationReasonSignal(string signal)
    {
        return TranslateRecommendationReasonSignal(signal, T);
    }

    private static string TranslateRecommendationReasonSignal(string signal, Func<string, string> translate)
    {
        return signal switch
        {
            "stable-first" => translate("RecommendationSignalStableFirst"),
            "manager-download" => translate("RecommendationSignalManagerDownload"),
            "recently-maintained" => translate("RecommendationSignalRecentlyMaintained"),
            "starter-friendly" => translate("RecommendationSignalStarterFriendly"),
            "qualifier-match" => translate("RecommendationSignalQualifierMatch"),
            "risk-filtered" => translate("RecommendationSignalRiskFiltered"),
            _ => translate("RecommendationSignalLowRisk")
        };
    }

    private static IReadOnlyList<string> GetModRecommendationCategoryKeywords(string categoryKey)
    {
        return categoryKey switch
        {
            "Gameplay" => ["gameplay", "balance", "combat", "perk", "skill", "difficulty", "ai"],
            "Visuals" => ["visual", "graphics", "texture", "lighting", "reshade", "model", "appearance", "overhaul"],
            "Interface" => ["ui", "hud", "interface", "menu", "map", "inventory"],
            "Items" => ["item", "weapon", "armor", "equipment", "sword", "bow", "clothing"],
            "Utilities" => ["utility", "tool", "framework", "fix", "patch", "manager", "script", "save"],
            _ => []
        };
    }

    private string GetModRecommendationStrategyDisplay(string strategyKey)
    {
        return GetModRecommendationStrategyDisplay(strategyKey, T);
    }

    private static string GetModRecommendationStrategyDisplay(string strategyKey, Func<string, string> translate)
    {
        return NormalizeModRecommendationStrategy(strategyKey) switch
        {
            "Content" => translate("ContentRecommendation"),
            "Starter" => translate("StarterRecommendation"),
            _ => translate("PopularityQualityRecommendation")
        };
    }

    private static string NormalizeModRecommendationStrategy(string? strategyKey)
    {
        return strategyKey switch
        {
            "Content" => "Content",
            "Starter" => "Starter",
            _ => "PopularityQuality"
        };
    }
    private void RefreshModRecommendationStrategyFilters()
    {
        var strategy = NormalizeModRecommendationStrategy(SelectedModRecommendationStrategy);
        var strategyKeys = new[] { "PopularityQuality", "Content", "Starter" };

        ModRecommendationStrategyFilters.Clear();
        foreach (var key in strategyKeys)
        {
            ModRecommendationStrategyFilters.Add(new ModDownloadCategoryFilterViewModel(key, GetModRecommendationStrategyDisplay(key))
            {
                IsSelected = string.Equals(key, strategy, StringComparison.Ordinal)
            });
        }
    }

}
