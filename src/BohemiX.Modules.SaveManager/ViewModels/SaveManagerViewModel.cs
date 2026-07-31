using System.Collections.ObjectModel;
using System.Diagnostics;
using BohemiX.Core.Models.Saves;
using BohemiX.Core.Services;
using BohemiX.Core.Services.Saves;
using BohemiX.Modules.SaveManager.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace BohemiX.Modules.SaveManager.ViewModels;

public sealed partial class SaveManagerViewModel : ObservableObject, IDisposable
{
    private readonly ISaveProfileService service;
    private readonly IAppSettingsService settingsService;
    private readonly IModCatalogService modCatalogService;
    private readonly ILogger logger;
    private readonly List<SaveProfileListItem> allProfiles = [];
    private readonly List<SaveBackupNode> allBackupNodes = [];
    private CancellationTokenSource? operationCancellation;
    private CancellationTokenSource? selectedDetailsCancellation;
    private long selectedDetailsGeneration;
    private bool initialized;
    private bool favoriteUpdatePending;
    private bool useEnglish;
    private bool isWorkspaceActive;
    private bool areDashboardVisualResourcesActive;

    public SaveManagerViewModel(
        ISaveProfileService service,
        IAppSettingsService settingsService,
        IModCatalogService modCatalogService,
        ILogger logger)
    {
        this.service = service;
        this.settingsService = settingsService;
        this.modCatalogService = modCatalogService;
        this.logger = logger.ForContext<SaveManagerViewModel>();
        Text = SaveManagerText.Chinese;
        StatusText = Text.Ready;
    }

    public ObservableCollection<SaveProfileListItem> Profiles { get; } = [];

    public IReadOnlyList<SaveProfileListItem> AllProfiles => allProfiles;

    public ObservableCollection<SaveProfileListItem> DashboardRecentProfiles { get; } = [];

    public bool HasDashboardRecentProfiles => DashboardRecentProfiles.Count > 0;

    internal bool IsSelectedDetailsLoadActive => selectedDetailsCancellation is not null;

    public bool HasNoDashboardRecentProfiles => !HasDashboardRecentProfiles;

    public ObservableCollection<SaveSnapshotListItem> SnapshotItems { get; } = [];

    public ObservableCollection<SaveBackupNodeListItem> BackupNodeItems { get; } = [];

    /// <summary>Provided by the Avalonia view so the VM remains testable and StorageProvider stays UI-owned.</summary>
    public Func<string, string, string, Task<string?>>? RequestTextAsync { get; set; }

    public Func<string, string, string, Task<bool>>? RequestConfirmationAsync { get; set; }

    public Func<Task<string?>>? RequestImportPathAsync { get; set; }

    public Func<string, Task<string?>>? RequestExportPathAsync { get; set; }

    public Func<Task<IReadOnlyList<string>>>? RequestExistingSavePathsAsync { get; set; }

    private Func<Task<bool>>? requestLaunchGameAsync;

    public Func<Task<bool>>? RequestLaunchGameAsync
    {
        get => requestLaunchGameAsync;
        set
        {
            if (ReferenceEquals(requestLaunchGameAsync, value))
            {
                return;
            }

            requestLaunchGameAsync = value;
            ContinueGameCommand.NotifyCanExecuteChanged();
        }
    }

    [ObservableProperty]
    private SaveManagerText text;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private int selectedFilterIndex;

    [ObservableProperty]
    private int selectedSortIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(IsSelectedProfileActive))]
    [NotifyPropertyChangedFor(nameof(SelectedQuest))]
    [NotifyPropertyChangedFor(nameof(SelectedLocation))]
    [NotifyPropertyChangedFor(nameof(SelectedLevel))]
    [NotifyPropertyChangedFor(nameof(SelectedLevelBadge))]
    [NotifyPropertyChangedFor(nameof(SelectedGameVersion))]
    [NotifyPropertyChangedFor(nameof(SelectedSavedAt))]
    [NotifyPropertyChangedFor(nameof(SelectedPlayTime))]
    [NotifyPropertyChangedFor(nameof(SelectedGroschen))]
    [NotifyPropertyChangedFor(nameof(SelectedCharacterSummary))]
    [NotifyPropertyChangedFor(nameof(SelectedSaveType))]
    [NotifyPropertyChangedFor(nameof(SelectedStatusEffects))]
    [NotifyPropertyChangedFor(nameof(HasSelectedStatusEffects))]
    [NotifyPropertyChangedFor(nameof(SelectedGameSaveName))]
    [NotifyPropertyChangedFor(nameof(ContinueGameHintText))]
    [NotifyPropertyChangedFor(nameof(SelectedPrimaryActionText))]
    [NotifyPropertyChangedFor(nameof(FavoriteActionText))]
    [NotifyCanExecuteChangedFor(nameof(SwitchProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ContinueGameCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleFavoriteCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(ProtectLatestSaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportExistingSavesCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportProfileCommand))]
    private SaveProfileListItem? selectedProfile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConvertSnapshotCommand))]
    private SaveSnapshotListItem? selectedSnapshot;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreBackupNodeCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteBackupNodeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportBackupNodeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleBackupNodeImportanceCommand))]
    [NotifyPropertyChangedFor(nameof(BackupImportanceActionText))]
    private SaveBackupNodeListItem? selectedBackupNode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecentBackupView))]
    [NotifyPropertyChangedFor(nameof(IsImportantBackupView))]
    [NotifyPropertyChangedFor(nameof(IsFullBackupView))]
    private int selectedBackupViewIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SafetyText))]
    [NotifyPropertyChangedFor(nameof(ContinueGameHintText))]
    [NotifyCanExecuteChangedFor(nameof(SwitchProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ContinueGameCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreBackupNodeCommand))]
    private bool isSafeToChange;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ContinueGameCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleFavoriteCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConvertSnapshotCommand))]
    [NotifyCanExecuteChangedFor(nameof(ProtectLatestSaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportExistingSavesCommand))]
    [NotifyCanExecuteChangedFor(nameof(RestoreBackupNodeCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteBackupNodeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportBackupNodeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleBackupNodeImportanceCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewProfileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportProfileCommand))]
    private bool isBusy;

    [ObservableProperty]
    private bool hasUnmanagedSave;

    [ObservableProperty]
    private int profileCount;

    [ObservableProperty]
    private int snapshotCount;

    [ObservableProperty]
    private string snapshotStorageSummary = string.Empty;

    [ObservableProperty]
    private string backupLibrarySummary = string.Empty;

    [ObservableProperty]
    private int backupNodeRetention = 10;

    [ObservableProperty]
    private string snapshotMigrationStatus = string.Empty;

    [ObservableProperty]
    private bool hasSnapshotMigrationStatus;

    [ObservableProperty]
    private string activeProfileName = "—";

    [ObservableProperty]
    private string statusText;

    [ObservableProperty]
    private double progressPercent;

    [ObservableProperty]
    private bool hasError;

    [ObservableProperty]
    private string errorText = string.Empty;

    [ObservableProperty]
    private string officialSavePath = string.Empty;

    [ObservableProperty]
    private string vaultRootPath = string.Empty;

    [ObservableProperty]
    private string migrationWarningText = string.Empty;

    [ObservableProperty]
    private bool showSecondaryColumns = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModProfileSummary))]
    private int enabledModCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModProfileSummary))]
    private bool isModSummaryAvailable;

    [ObservableProperty]
    private bool isCharacterExpanded;

    [ObservableProperty]
    private bool isSnapshotHistoryExpanded;

    public bool HasSelection => SelectedProfile is not null;

    public bool HasProfiles => Profiles.Count > 0;

    public bool HasSnapshots => SnapshotItems.Count > 0;

    public bool HasBackupNodes => BackupNodeItems.Count > 0;

    public bool IsRecentBackupView => SelectedBackupViewIndex == 0;

    public bool IsImportantBackupView => SelectedBackupViewIndex == 1;

    public bool IsFullBackupView => SelectedBackupViewIndex == 2;

    public string BackupLibraryTitle => useEnglish ? "Backups" : "备份";

    public string RecentNodesText => useEnglish ? "Recent" : "近期";

    public string ImportantNodesText => useEnglish ? "Pinned" : "重要";

    public string FullBackupsText => useEnglish ? "Full" : "完整";

    public string ProtectLatestText => useEnglish ? "Pin latest" : "保护最新";

    public string AddExistingText => useEnglish ? "Add" : "添加";

    public string BackupRetentionText => useEnglish ? "Keep" : "保留";

    public string CreateFullBackupText => useEnglish ? "Full backup" : "完整备份";

    public string ConvertFullBackupText => useEnglish ? "Convert" : "转换";

    public string BackupImportanceActionText => SelectedBackupNode?.IsImportant == true
        ? (useEnglish ? "Unpin" : "取消重要")
        : (useEnglish ? "Pin" : "标记重要");

    public string NoBackupNodesText => useEnglish ? "Nothing here" : "暂无节点";

    public bool IsSelectedProfileActive => SelectedProfile?.IsActive == true;

    public string SafetyText => IsSafeToChange ? Text.Safe : Text.Unsafe;

    public string SelectedQuest => SelectedProfile?.Quest ?? "—";

    public string SelectedLocation => SelectedProfile?.Location ?? "—";

    public string SelectedLevel => SelectedProfile?.Profile.DisplayData?.HenryLevel is > 0
        ? SelectedProfile.Profile.DisplayData.HenryLevel.ToString()
        : "—";

    public string SelectedLevelBadge => SelectedProfile?.Profile.DisplayData?.HenryLevel is > 0
        ? $"Lv. {SelectedProfile.Profile.DisplayData.HenryLevel}"
        : string.Empty;

    public string SelectedGameVersion => string.IsNullOrWhiteSpace(SelectedProfile?.Profile.GameVersion)
        ? "—"
        : SelectedProfile.Profile.GameVersion;

    public string SelectedSavedAt => SelectedProfile?.LastSavedText ?? "—";

    public string SelectedPlayTime => SelectedProfile?.PlayTimeText ?? "—";

    public string SelectedGroschen => SelectedProfile?.Profile.DisplayData?.GroschenCount is > 0
        ? SelectedProfile.Profile.DisplayData.GroschenCount.ToString("N0")
        : "—";

    public string SelectedCharacterSummary => $"{(string.IsNullOrWhiteSpace(SelectedLevelBadge) ? Text.Level + " —" : SelectedLevelBadge)} · {SelectedGroschen} {Text.Groschen}";

    public string SelectedSaveType => SelectedProfile?.SaveTypeText ?? Text.SaveTypeUnknown;

    public IReadOnlyList<string> SelectedStatusEffects => SelectedProfile?.Profile.DisplayData?.PlayerStatusEffects ?? [];

    public bool HasSelectedStatusEffects => SelectedStatusEffects.Count > 0;

    public string SelectedGameSaveName => string.IsNullOrWhiteSpace(SelectedProfile?.Profile.GameSaveName)
        ? "—"
        : SelectedProfile.Profile.GameSaveName;

    public string ModProfileSummary => IsModSummaryAvailable
        ? string.Format(Text.CurrentModConfigurationFormat, EnabledModCount)
        : Text.ModConfigurationUnavailable;

    public string ContinueGameHintText => IsSafeToChange ? Text.ContinueGameHint : Text.Unsafe;

    public string SelectedPrimaryActionText => IsSelectedProfileActive ? Text.InUse : Text.SwitchProfile;

    public string FavoriteActionText => SelectedProfile?.IsFavorite == true ? Text.Unfavorite : Text.Favorite;

    public async Task InitializeAsync()
    {
        var settings = await settingsService.LoadAsync().ConfigureAwait(true);
        UseLanguage(settings.SelectedLanguage);
        BackupNodeRetention = settings.BackupNodeRetention;
        await service.InitializeAsync().ConfigureAwait(true);
        initialized = true;

        await RefreshCoreAsync().ConfigureAwait(true);
    }

    public async Task HandleGameExitAsync(byte[]? exitThumbnail = null)
    {
        try
        {
            await service.CreateGameExitSnapshotAsync().ConfigureAwait(true);
            if (exitThumbnail is { Length: > 0 })
            {
                await service.SaveGameExitThumbnailAsync(exitThumbnail).ConfigureAwait(true);
            }
            await RefreshCoreAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
    }

    public void UseLanguage(string? language)
    {
        useEnglish = string.Equals(language, "English", StringComparison.OrdinalIgnoreCase);
        Text = SaveManagerText.ForLanguage(language);
        OnPropertyChanged(nameof(SafetyText));
        RebuildSnapshotItems();
        RebuildBackupNodeItems();
    }

    [RelayCommand]
    private Task RefreshAsync() => RefreshCoreAsync();

    [RelayCommand(CanExecute = nameof(CanCreateProfile))]
    private async Task NewProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var name = RequestTextAsync is null
            ? $"{SelectedProfile.DisplayName} copy"
            : await RequestTextAsync(Text.CloneTitle, Text.ClonePrompt, $"{SelectedProfile.DisplayName} copy").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await RunOperationAsync(
            token => service.CloneProfileAsync(SelectedProfile.Id, name, CreateProgress(), token),
            Text.NewProfile).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ManageCurrentAsync()
    {
        var defaultName = DateTime.Now.ToString("yyyy-MM-dd HH-mm");
        var name = RequestTextAsync is null
            ? defaultName
            : await RequestTextAsync(Text.NewProfile, Text.UnmanagedBody, defaultName).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await RunOperationAsync(
            token => service.ManageCurrentSaveAsync(name, CreateProgress(), token),
            Text.ManageCurrent).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ImportPackageAsync()
    {
        var path = RequestImportPathAsync is null ? null : await RequestImportPathAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await RunOperationAsync(
            token => service.ImportPackageAsync(path, CreateProgress(), token),
            Text.Import).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanExportProfile))]
    private async Task ExportProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var suggested = SanitizeFileName(SelectedProfile.DisplayName) + ".bohemix-save.zip";
        var path = RequestExportPathAsync is null ? null : await RequestExportPathAsync(suggested).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await RunOperationAsync(
            token => service.ExportProfileAsync(SelectedProfile.Id, path, new SavePackageExportOptions(true), CreateProgress(), token),
            Text.Export,
            refresh: false).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanSwitchProfile))]
    private Task SwitchProfileAsync()
    {
        return SelectedProfile is null
            ? Task.CompletedTask
            : RunOperationAsync(token => service.SwitchProfileAsync(SelectedProfile.Id, token), Text.SwitchProfile);
    }

    [RelayCommand(CanExecute = nameof(CanContinueGame))]
    private async Task ContinueGameAsync()
    {
        if (SelectedProfile is null || RequestLaunchGameAsync is null || IsBusy)
        {
            return;
        }

        var selectedId = SelectedProfile.Id;
        var switchedProfile = !SelectedProfile.IsActive;
        var launchFailed = false;

        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        IsBusy = true;
        HasError = false;
        StatusText = Text.Working;
        ProgressPercent = 0;

        try
        {
            if (switchedProfile)
            {
                await service.SwitchProfileAsync(selectedId, operationCancellation.Token).ConfigureAwait(true);
            }

            operationCancellation.Token.ThrowIfCancellationRequested();
            try
            {
                launchFailed = !await RequestLaunchGameAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Game launch failed after selecting save profile {ProfileId}", selectedId);
                launchFailed = true;
            }

            if (!launchFailed)
            {
                StatusText = Text.GameLaunchRequested;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = Text.Ready;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
            ProgressPercent = 0;
        }

        if (switchedProfile && !HasError)
        {
            await RefreshCoreAsync().ConfigureAwait(true);
        }

        if (!launchFailed && !HasError)
        {
            StatusText = Text.GameLaunchRequested;
        }

        if (launchFailed && !HasError)
        {
            HasError = true;
            ErrorText = switchedProfile ? Text.ProfileSwitchedLaunchFailed : Text.GameLaunchFailed;
            StatusText = ErrorText;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditProfile))]
    private async Task RenameProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var name = RequestTextAsync is null
            ? SelectedProfile.DisplayName
            : await RequestTextAsync(Text.RenameTitle, Text.RenamePrompt, SelectedProfile.DisplayName).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), SelectedProfile.DisplayName, StringComparison.Ordinal))
        {
            return;
        }

        await RunOperationAsync(token => service.RenameProfileAsync(SelectedProfile.Id, name, token), Text.Rename).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanToggleFavorite))]
    private async Task ToggleFavoriteAsync(SaveProfileListItem? profile)
    {
        profile ??= SelectedProfile;
        if (profile is null || favoriteUpdatePending)
        {
            return;
        }

        var isFavorite = !profile.IsFavorite;
        favoriteUpdatePending = true;
        ToggleFavoriteCommand.NotifyCanExecuteChanged();
        HasError = false;

        try
        {
            await service.SetFavoriteAsync(profile.Id, isFavorite).ConfigureAwait(true);
            profile.SetFavorite(isFavorite);
            if (SelectedProfile?.Id == profile.Id)
            {
                OnPropertyChanged(nameof(FavoriteActionText));
            }
            StatusText = isFavorite ? Text.Favorite : Text.Unfavorite;

            if (SelectedFilterIndex == 1 && !isFavorite)
            {
                ApplyFilters(profile.Id);
            }
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            favoriteUpdatePending = false;
            ToggleFavoriteCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        var confirmed = RequestConfirmationAsync is not null
            && await RequestConfirmationAsync(Text.DeleteProfileTitle, $"{SelectedProfile.DisplayName}\n\n{Text.DeleteProfileMessage}", Text.Delete).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await RunOperationAsync(token => service.DeleteProfileAsync(SelectedProfile.Id, token), Text.Delete).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanEditProfile))]
    private Task CreateSnapshotAsync()
    {
        return SelectedProfile is null
            ? Task.CompletedTask
            : RunOperationAsync(
                token => service.CreateSnapshotAsync(SelectedProfile.Id, SaveSnapshotTrigger.Manual, null, CreateProgress(), token),
                Text.CreateSnapshot);
    }

    [RelayCommand(CanExecute = nameof(CanEditProfile))]
    private Task ProtectLatestSaveAsync()
    {
        if (SelectedProfile is null)
        {
            return Task.CompletedTask;
        }

        var profileId = SelectedProfile.Id;
        return RunOperationAsync(
            async token =>
            {
                var nodes = await service.ReconcileBackupNodesAsync(profileId, token).ConfigureAwait(false);
                var latest = nodes.OrderByDescending(node => node.LastSavedAtUtc ?? node.FirstSeenAtUtc).FirstOrDefault()
                    ?? throw new InvalidOperationException(useEnglish ? "No game save file was found." : "没有找到可保护的游戏存档。");
                await service.SetBackupNodeImportanceAsync(latest.Id, true, cancellationToken: token).ConfigureAwait(false);
            },
            useEnglish ? "Latest save protected" : "最新存档已保护");
    }

    [RelayCommand]
    private async Task SaveBackupRetentionAsync()
    {
        var current = await settingsService.LoadAsync().ConfigureAwait(true);
        var value = Math.Clamp(BackupNodeRetention, 1, 200);
        BackupNodeRetention = value;
        await settingsService.SaveAsync(current with { BackupNodeRetention = value }).ConfigureAwait(true);
        if (SelectedProfile is not null)
        {
            await RunOperationAsync(
                token => service.ReconcileBackupNodesAsync(SelectedProfile.Id, token),
                useEnglish ? "Retention updated" : "保留数量已更新").ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditProfile))]
    private async Task ImportExistingSavesAsync()
    {
        if (SelectedProfile is null || RequestExistingSavePathsAsync is null)
        {
            return;
        }

        var paths = await RequestExistingSavePathsAsync().ConfigureAwait(true);
        if (paths.Count == 0)
        {
            return;
        }

        var profileId = SelectedProfile.Id;
        await RunOperationAsync(
            token => service.ImportExistingBackupNodesAsync(profileId, paths, token),
            useEnglish ? "Existing saves protected" : "已有存档已加入保护").ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRestoreBackupNode))]
    private async Task RestoreBackupNodeAsync()
    {
        if (SelectedBackupNode is null)
        {
            return;
        }

        var confirmed = RequestConfirmationAsync is not null
            && await RequestConfirmationAsync(
                useEnglish ? "Restore save node" : "恢复存档节点",
                useEnglish
                    ? "The selected save will be restored without deleting other current saves. A conflicting file is protected first."
                    : "所选存档将被放回当前档案，其他存档不会删除；同名冲突文件会先自动保护。",
                Text.Restore).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        var id = SelectedBackupNode.Id;
        await RunOperationAsync(
            token => service.RestoreBackupNodeAsync(id, token),
            Text.Restore).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanUseBackupNode))]
    private Task ToggleBackupNodeImportanceAsync()
    {
        if (SelectedBackupNode is null)
        {
            return Task.CompletedTask;
        }

        var node = SelectedBackupNode;
        return RunOperationAsync(
            token => service.SetBackupNodeImportanceAsync(node.Id, !node.IsImportant, cancellationToken: token),
            !node.IsImportant
                ? (useEnglish ? "Marked important" : "已标记为重要")
                : (useEnglish ? "Removed importance" : "已取消重要标记"));
    }

    [RelayCommand(CanExecute = nameof(CanUseBackupNode))]
    private async Task DeleteBackupNodeAsync()
    {
        if (SelectedBackupNode is null)
        {
            return;
        }

        var confirmed = RequestConfirmationAsync is not null
            && await RequestConfirmationAsync(
                useEnglish ? "Delete backup node" : "删除备份节点",
                useEnglish ? "This backup node will be permanently deleted." : "该备份节点将被永久删除。",
                Text.Delete).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        var id = SelectedBackupNode.Id;
        await RunOperationAsync(token => service.DeleteBackupNodeAsync(id, token), Text.Delete).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanUseBackupNode))]
    private async Task ExportBackupNodeAsync()
    {
        if (SelectedBackupNode is null || SelectedProfile is null)
        {
            return;
        }

        var suggested = $"{SanitizeFileName(SelectedProfile.DisplayName)}-{SanitizeFileName(SelectedBackupNode.FileName)}.bohemix-save.zip";
        var path = RequestExportPathAsync is null ? null : await RequestExportPathAsync(suggested).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var id = SelectedBackupNode.Id;
        await RunOperationAsync(
            token => service.ExportBackupNodeAsync(id, path, CreateProgress(), token),
            Text.Export,
            refresh: false).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRestoreSnapshot))]
    private async Task RestoreSnapshotAsync()
    {
        if (SelectedSnapshot is null)
        {
            return;
        }

        var profileName = SelectedProfile?.DisplayName ?? "—";
        var confirmed = RequestConfirmationAsync is not null
            && await RequestConfirmationAsync(
                Text.RestoreTitle,
                $"{Text.RestoreMessage}\n\n{profileName} ← {SelectedSnapshot.CreatedText}",
                Text.Restore).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await RunOperationAsync(
            token => service.RestoreSnapshotAsync(SelectedSnapshot.Id, CreateProgress(), token),
            Text.Restore).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSnapshot))]
    private async Task DeleteSnapshotAsync()
    {
        if (SelectedSnapshot is null)
        {
            return;
        }

        var confirmed = RequestConfirmationAsync is not null
            && await RequestConfirmationAsync(Text.DeleteSnapshotTitle, Text.DeleteSnapshotMessage, Text.Delete).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        await RunOperationAsync(token => service.DeleteSnapshotAsync(SelectedSnapshot.Id, token), Text.Delete).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSnapshot))]
    private async Task ExportSnapshotAsync()
    {
        if (SelectedSnapshot is null || SelectedProfile is null)
        {
            return;
        }

        var suggested = $"{SanitizeFileName(SelectedProfile.DisplayName)}-{SelectedSnapshot.Snapshot.CreatedAtUtc:yyyyMMdd-HHmm}.bohemix-save.zip";
        var path = RequestExportPathAsync is null ? null : await RequestExportPathAsync(suggested).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await RunOperationAsync(
            token => service.ExportSnapshotAsync(SelectedSnapshot.Id, path, CreateProgress(), token),
            Text.Export,
            refresh: false).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSnapshot))]
    private async Task ConvertSnapshotAsync()
    {
        if (SelectedSnapshot is null)
        {
            return;
        }

        var preview = await service.PreviewFullSnapshotConversionAsync(SelectedSnapshot.Id).ConfigureAwait(true);
        var selected = preview.Files.Where(file => file.IsRecommended).Select(file => file.RelativePath).ToList();
        var message = useEnglish
            ? $"Keep {selected.Count} save nodes ({FormatBytes(preview.SelectedLogicalBytes)}) and reclaim about {FormatBytes(preview.EstimatedReclaimableBytes)}. The full backup is deleted only after verification."
            : $"将保留 {selected.Count} 个存档项目（{FormatBytes(preview.SelectedLogicalBytes)}），预计释放约 {FormatBytes(preview.EstimatedReclaimableBytes)}。确认保留内容完整后，才会删除原始完整备份。";
        var confirmed = RequestConfirmationAsync is not null
            && await RequestConfirmationAsync(ConvertFullBackupText, message, ConvertFullBackupText).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        var snapshotId = SelectedSnapshot.Id;
        await RunOperationAsync(
            token => service.ConvertFullSnapshotToNodesAsync(snapshotId, selected, token),
            ConvertFullBackupText).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenVault() => OpenPath(VaultRootPath);

    [RelayCommand]
    private void OpenOfficialSave() => OpenPath(OfficialSavePath);

    [RelayCommand(CanExecute = nameof(CanEditProfile))]
    private void OpenProfileFolder()
    {
        if (SelectedProfile is not null)
        {
            OpenPath(SelectedProfile.Profile.PhysicalPath);
        }
    }

    [RelayCommand]
    private void ClearError()
    {
        HasError = false;
        ErrorText = string.Empty;
    }

    [RelayCommand]
    private void CancelOperation() => operationCancellation?.Cancel();

    private bool CanCreateProfile() => SelectedProfile is not null && !IsBusy;
    private bool CanExportProfile() => SelectedProfile is not null && !IsBusy;
    private bool CanEditProfile() => SelectedProfile is not null && !IsBusy;
    private bool CanToggleFavorite(SaveProfileListItem? profile) => (profile ?? SelectedProfile) is not null && !IsBusy && !favoriteUpdatePending;
    private bool CanDeleteProfile() => SelectedProfile is { IsActive: false } && !IsBusy;
    private bool CanSwitchProfile() => SelectedProfile is { IsActive: false } && IsSafeToChange && !IsBusy;
    private bool CanContinueGame() => SelectedProfile is not null && IsSafeToChange && !IsBusy && RequestLaunchGameAsync is not null;
    private bool CanRestoreSnapshot() => SelectedSnapshot is not null && IsSafeToChange && !IsBusy;
    private bool CanDeleteSnapshot() => SelectedSnapshot is not null && !IsBusy;
    private bool CanRestoreBackupNode() => SelectedBackupNode is { IsHealthy: true } && IsSafeToChange && !IsBusy;
    private bool CanUseBackupNode() => SelectedBackupNode is not null && !IsBusy;

    partial void OnSearchTextChanged(string value) => ApplyFilters();
    partial void OnSelectedFilterIndexChanged(int value) => ApplyFilters();
    partial void OnSelectedSortIndexChanged(int value) => ApplyFilters();
    partial void OnSelectedBackupViewIndexChanged(int value) => ApplyBackupNodeFilter();

    partial void OnSelectedProfileChanged(SaveProfileListItem? value)
    {
        SelectedSnapshot = null;
        SelectedBackupNode = null;
        OnPropertyChanged(nameof(HasSnapshots));
        OnPropertyChanged(nameof(HasBackupNodes));
        if (isWorkspaceActive)
        {
            _ = LoadSelectedSnapshotsAsync(value?.Id);
        }
    }

    partial void OnTextChanged(SaveManagerText value)
    {
        OnPropertyChanged(nameof(SafetyText));
        OnPropertyChanged(nameof(SelectedPrimaryActionText));
        OnPropertyChanged(nameof(FavoriteActionText));
        OnPropertyChanged(nameof(ModProfileSummary));
        OnPropertyChanged(nameof(ContinueGameHintText));
        OnPropertyChanged(nameof(SelectedCharacterSummary));
        OnPropertyChanged(nameof(SelectedSaveType));
        OnPropertyChanged(nameof(BackupLibraryTitle));
        OnPropertyChanged(nameof(RecentNodesText));
        OnPropertyChanged(nameof(ImportantNodesText));
        OnPropertyChanged(nameof(FullBackupsText));
        OnPropertyChanged(nameof(ProtectLatestText));
        OnPropertyChanged(nameof(AddExistingText));
        OnPropertyChanged(nameof(BackupRetentionText));
        OnPropertyChanged(nameof(CreateFullBackupText));
        OnPropertyChanged(nameof(ConvertFullBackupText));
        OnPropertyChanged(nameof(BackupImportanceActionText));
        OnPropertyChanged(nameof(NoBackupNodesText));
        UpdateProfilePresentation();
        RebuildBackupNodeItems();
    }

    private async Task RefreshCoreAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        HasError = false;
        var selectedId = SelectedProfile?.Id;
        try
        {
            await service.InitializeAsync().ConfigureAwait(true);
            var profiles = await service.GetProfilesAsync().ConfigureAwait(true);
            var snapshotTasks = profiles.ToDictionary(profile => profile.Id, profile => service.GetSnapshotsAsync(profile.Id));
            var storageStatsTask = service.GetSnapshotStorageStatsAsync();
            var backupStatsTask = service.GetBackupLibraryStatsAsync();
            await Task.WhenAll(snapshotTasks.Values.Cast<Task>().Append(storageStatsTask).Append(backupStatsTask)).ConfigureAwait(true);
            await LoadModSummaryAsync().ConfigureAwait(true);

            var previousItems = allProfiles.ToArray();
            allProfiles.Clear();
            allProfiles.AddRange(profiles.Select(profile => new SaveProfileListItem(profile, Text, ModProfileSummary)));
            ProfileCount = allProfiles.Count;
            SnapshotCount = snapshotTasks.Values.Sum(task => task.Result.Count) + backupStatsTask.Result.NodeCount;
            UpdateSnapshotStorageSummary(storageStatsTask.Result);
            UpdateBackupLibrarySummary(backupStatsTask.Result);
            ActiveProfileName = allProfiles.FirstOrDefault(profile => profile.IsActive)?.DisplayName ?? "—";
            HasUnmanagedSave = await service.HasUnmanagedSaveAsync().ConfigureAwait(true);
            IsSafeToChange = await service.IsSafeToChangeAsync().ConfigureAwait(true);
            OfficialSavePath = service.GetOfficialSavePath();
            VaultRootPath = service.GetVaultRootPath();
            MigrationWarningText = string.Join(Environment.NewLine, service.MigrationWarnings.Select(warning => warning.Message));

            ApplyFilters(selectedId);
            RefreshDashboardRecentProfiles();
            SyncProfileVisualResources();
            foreach (var item in previousItems)
            {
                item.Dispose();
            }
            StatusText = Text.Ready;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
            ProgressPercent = 0;
        }
    }

    private void ApplyFilters(Guid? preferredId = null)
    {
        preferredId ??= SelectedProfile?.Id;
        IEnumerable<SaveProfileListItem> query = allProfiles;

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            query = query.Where(profile =>
                profile.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || profile.Quest.Contains(search, StringComparison.OrdinalIgnoreCase)
                || profile.Location.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        query = SelectedFilterIndex switch
        {
            1 => query.Where(profile => profile.IsFavorite),
            2 => query.Where(profile => profile.IsActive),
            _ => query
        };

        query = SelectedSortIndex switch
        {
            1 => query.OrderBy(profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            2 => query.OrderByDescending(profile => profile.Profile.UpdatedAtUtc),
            _ => query.OrderByDescending(profile => profile.Profile.LastActivatedAtUtc ?? profile.Profile.UpdatedAtUtc)
        };

        Profiles.Clear();
        foreach (var profile in query)
        {
            Profiles.Add(profile);
        }

        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == preferredId)
            ?? Profiles.FirstOrDefault(profile => profile.IsActive)
            ?? Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(HasProfiles));
    }

    private void RefreshDashboardRecentProfiles()
    {
        DashboardRecentProfiles.Clear();
        foreach (var profile in SelectDashboardRecentProfiles(allProfiles))
        {
            DashboardRecentProfiles.Add(profile);
        }

        OnPropertyChanged(nameof(HasDashboardRecentProfiles));
        OnPropertyChanged(nameof(HasNoDashboardRecentProfiles));
    }

    internal static IReadOnlyList<SaveProfileListItem> SelectDashboardRecentProfiles(
        IEnumerable<SaveProfileListItem> profiles)
    {
        return profiles
            .OrderByDescending(profile => profile.Profile.LastSavedAtUtc
                                          ?? profile.Profile.LastActivatedAtUtc
                                          ?? profile.Profile.UpdatedAtUtc)
            .Take(3)
            .ToArray();
    }

    public void ActivateWorkspaceVisualResources()
    {
        if (isWorkspaceActive)
        {
            return;
        }

        isWorkspaceActive = true;
        SyncProfileVisualResources();
        _ = LoadSelectedSnapshotsAsync(SelectedProfile?.Id);
    }

    public void DeactivateWorkspaceVisualResources()
    {
        isWorkspaceActive = false;
        CancelSelectedDetailsLoad();
        SelectedSnapshot = null;
        SelectedBackupNode = null;
        SnapshotItems.Clear();
        BackupNodeItems.Clear();
        allBackupNodes.Clear();
        OnPropertyChanged(nameof(HasSnapshots));
        OnPropertyChanged(nameof(HasBackupNodes));
        SyncProfileVisualResources();
    }

    public void ActivateDashboardVisualResources()
    {
        areDashboardVisualResourcesActive = true;
        SyncProfileVisualResources();
    }

    public void DeactivateDashboardVisualResources()
    {
        areDashboardVisualResourcesActive = false;
        SyncProfileVisualResources();
    }

    private void SyncProfileVisualResources()
    {
        var dashboardProfiles = areDashboardVisualResourcesActive
            ? DashboardRecentProfiles.ToHashSet()
            : null;
        foreach (var profile in allProfiles)
        {
            if (isWorkspaceActive || dashboardProfiles?.Contains(profile) == true)
            {
                profile.ActivateVisualResources();
            }
            else
            {
                profile.DeactivateVisualResources();
            }
        }
    }

    private async Task LoadSelectedSnapshotsAsync(Guid? profileId)
    {
        var generation = ++selectedDetailsGeneration;
        selectedDetailsCancellation?.Cancel();
        selectedDetailsCancellation?.Dispose();
        selectedDetailsCancellation = null;
        SnapshotItems.Clear();
        BackupNodeItems.Clear();
        allBackupNodes.Clear();
        if (profileId is null || !initialized || !isWorkspaceActive)
        {
            OnPropertyChanged(nameof(HasSnapshots));
            OnPropertyChanged(nameof(HasBackupNodes));
            return;
        }

        var cancellation = new CancellationTokenSource();
        selectedDetailsCancellation = cancellation;
        try
        {
            var snapshots = await service.GetSnapshotsAsync(profileId.Value, cancellation.Token).ConfigureAwait(true);
            var nodes = await service.ReconcileBackupNodesAsync(profileId.Value, cancellation.Token).ConfigureAwait(true);
            if (generation != selectedDetailsGeneration
                || cancellation.IsCancellationRequested
                || !isWorkspaceActive
                || SelectedProfile?.Id != profileId)
            {
                return;
            }

            foreach (var snapshot in snapshots)
            {
                SnapshotItems.Add(new SaveSnapshotListItem(snapshot, Text));
            }

            allBackupNodes.AddRange(nodes);
            ApplyBackupNodeFilter();
            SelectedSnapshot = SnapshotItems.FirstOrDefault();
            OnPropertyChanged(nameof(HasSnapshots));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (generation == selectedDetailsGeneration && isWorkspaceActive)
            {
                ShowError(ex);
            }
        }
        finally
        {
            if (ReferenceEquals(selectedDetailsCancellation, cancellation))
            {
                selectedDetailsCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void CancelSelectedDetailsLoad()
    {
        selectedDetailsGeneration++;
        var cancellation = selectedDetailsCancellation;
        selectedDetailsCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void RebuildSnapshotItems()
    {
        var snapshots = SnapshotItems.Select(item => item.Snapshot).ToList();
        var selectedId = SelectedSnapshot?.Id;
        SnapshotItems.Clear();
        foreach (var snapshot in snapshots)
        {
            SnapshotItems.Add(new SaveSnapshotListItem(snapshot, Text));
        }

        SelectedSnapshot = SnapshotItems.FirstOrDefault(item => item.Id == selectedId) ?? SnapshotItems.FirstOrDefault();
    }

    private void RebuildBackupNodeItems()
    {
        ApplyBackupNodeFilter(SelectedBackupNode?.Id);
    }

    private void ApplyBackupNodeFilter(Guid? selectedId = null)
    {
        selectedId ??= SelectedBackupNode?.Id;
        IEnumerable<SaveBackupNode> nodes = allBackupNodes;
        if (SelectedBackupViewIndex == 1)
        {
            nodes = nodes.Where(node => node.IsImportant);
        }

        BackupNodeItems.Clear();
        foreach (var node in nodes.OrderByDescending(node => node.LastSavedAtUtc ?? node.FirstSeenAtUtc))
        {
            BackupNodeItems.Add(new SaveBackupNodeListItem(node, useEnglish));
        }

        SelectedBackupNode = BackupNodeItems.FirstOrDefault(item => item.Id == selectedId) ?? BackupNodeItems.FirstOrDefault();
        OnPropertyChanged(nameof(HasBackupNodes));
    }

    private async Task LoadModSummaryAsync()
    {
        try
        {
            var mods = await modCatalogService.LoadInstalledModsAsync().ConfigureAwait(true);
            EnabledModCount = mods.Count(mod => mod.IsEnabled);
            IsModSummaryAvailable = true;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Unable to load the current Mod configuration for the save manager");
            EnabledModCount = 0;
            IsModSummaryAvailable = false;
        }
    }

    private void UpdateProfilePresentation()
    {
        foreach (var profile in allProfiles)
        {
            profile.UpdatePresentation(Text, ModProfileSummary);
        }
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> action, string successText, bool refresh = true)
    {
        if (IsBusy)
        {
            return;
        }

        operationCancellation?.Dispose();
        operationCancellation = new CancellationTokenSource();
        IsBusy = true;
        HasError = false;
        StatusText = Text.Working;
        ProgressPercent = 0;
        try
        {
            await action(operationCancellation.Token).ConfigureAwait(true);
            StatusText = successText;
        }
        catch (OperationCanceledException)
        {
            StatusText = Text.Ready;
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            IsBusy = false;
            ProgressPercent = 0;
        }

        if (refresh && !HasError)
        {
            await RefreshCoreAsync().ConfigureAwait(true);
        }
    }

    private IProgress<SaveImportProgress> CreateProgress() =>
        new Progress<SaveImportProgress>(progress =>
        {
            ProgressPercent = progress.Percent;
            StatusText = string.IsNullOrWhiteSpace(progress.CurrentEntry)
                ? progress.Stage
                : $"{progress.Stage} · {progress.CurrentEntry}";
        });

    private void ShowError(Exception exception)
    {
        logger.Error(exception, "Save manager operation failed");
        HasError = true;
        ErrorText = exception.Message;
        StatusText = Text.ErrorTitle;
    }

    private static void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "save-profile" : result;
    }

    private void UpdateSnapshotStorageSummary(SaveSnapshotStorageStats stats)
    {
        SnapshotStorageSummary = useEnglish
            ? $"Disk {FormatBytes(stats.PhysicalBytes)} · recoverable {FormatBytes(stats.LogicalBytes)} · saved {FormatBytes(stats.SavedBytes)}"
            : $"实际 {FormatBytes(stats.PhysicalBytes)} · 可恢复 {FormatBytes(stats.LogicalBytes)} · 已节省 {FormatBytes(stats.SavedBytes)}";
        SnapshotMigrationStatus = stats.MaintenanceState switch
        {
            SaveSnapshotMaintenanceState.Migrating => useEnglish ? "Optimizing old snapshots" : "正在优化旧快照",
            SaveSnapshotMaintenanceState.PausedLowSpace => useEnglish ? "Optimization paused: low disk space" : "磁盘空间不足，优化已暂停",
            SaveSnapshotMaintenanceState.Faulted => useEnglish ? "Some old snapshots could not be optimized" : "部分旧快照暂时无法优化",
            _ when stats.PendingMigrationCount > 0 => useEnglish
                ? $"{stats.PendingMigrationCount} old snapshots pending"
                : $"{stats.PendingMigrationCount} 个旧快照待优化",
            _ => string.Empty
        };
        HasSnapshotMigrationStatus = !string.IsNullOrWhiteSpace(SnapshotMigrationStatus);
    }

    private void UpdateBackupLibrarySummary(SaveBackupLibraryStats stats)
    {
        BackupLibrarySummary = useEnglish
            ? $"{stats.NodeCount} · {stats.ImportantCount} pinned · {FormatBytes(stats.PhysicalBytes)}"
            : $"{stats.NodeCount} · {stats.ImportantCount} 重要 · {FormatBytes(stats.PhysicalBytes)}";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    public void Dispose()
    {
        operationCancellation?.Cancel();
        operationCancellation?.Dispose();
        CancelSelectedDetailsLoad();
        isWorkspaceActive = false;
        areDashboardVisualResourcesActive = false;
        foreach (var profile in allProfiles)
        {
            profile.Dispose();
        }
    }
}
