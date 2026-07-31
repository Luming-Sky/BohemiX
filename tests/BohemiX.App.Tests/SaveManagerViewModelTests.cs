using Avalonia.Headless.XUnit;
using BohemiX.Core.Models;
using BohemiX.Core.Models.Saves;
using BohemiX.Core.Services;
using BohemiX.Core.Services.Saves;
using BohemiX.Modules.SaveManager.Models;
using BohemiX.Modules.SaveManager.ViewModels;
using Serilog;

namespace BohemiX.App.Tests;

public sealed class SaveManagerViewModelTests
{
    [Fact]
    public async Task SearchAndFavoriteFilter_UseProfileMetadata()
    {
        var favorite = Profile("Pilgrimage", favorite: true, location: "Kuttenberg");
        var other = Profile("Blacksmith", location: "Trosky");
        var vm = CreateViewModel(new FakeProfileService([favorite, other]));
        await vm.InitializeAsync();

        vm.SearchText = "Kutten";
        Assert.Single(vm.Profiles);
        Assert.Equal(favorite.Id, vm.Profiles[0].Id);

        vm.SearchText = string.Empty;
        vm.SelectedFilterIndex = 1;
        Assert.Single(vm.Profiles);
        Assert.True(vm.Profiles[0].IsFavorite);
    }

    [Fact]
    public async Task Refresh_PreservesSelectedProfileById()
    {
        var first = Profile("First");
        var second = Profile("Second");
        var service = new FakeProfileService([first, second]);
        var vm = CreateViewModel(service);
        await vm.InitializeAsync();
        vm.SelectedProfile = vm.Profiles.Single(item => item.Id == second.Id);

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(second.Id, vm.SelectedProfile?.Id);
    }

    [Fact]
    public async Task ToggleFavorite_UpdatesInPlaceWithoutEnteringGlobalBusyState()
    {
        var profile = Profile("Stable row");
        var service = new FakeProfileService([profile])
        {
            FavoriteGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var vm = CreateViewModel(service);
        await vm.InitializeAsync();
        var selected = vm.SelectedProfile;

        var toggleTask = vm.ToggleFavoriteCommand.ExecuteAsync(null);
        await service.FavoriteStarted.Task;

        Assert.False(vm.IsBusy);
        Assert.Same(selected, vm.SelectedProfile);
        Assert.Single(vm.Profiles);

        service.FavoriteGate.SetResult(true);
        await toggleTask;

        Assert.True(selected!.IsFavorite);
        Assert.Same(selected, vm.SelectedProfile);
        Assert.Equal(1, service.GetProfilesCallCount);
    }

    [Fact]
    public async Task ToggleFavorite_CardParameterDoesNotChangeSelectionOrRebuildList()
    {
        var first = Profile("First");
        var second = Profile("Second");
        var service = new FakeProfileService([first, second]);
        var vm = CreateViewModel(service);
        await vm.InitializeAsync();
        var selected = vm.Profiles.Single(item => item.Id == first.Id);
        var target = vm.Profiles.Single(item => item.Id == second.Id);
        vm.SelectedProfile = selected;

        await vm.ToggleFavoriteCommand.ExecuteAsync(target);

        Assert.Same(selected, vm.SelectedProfile);
        Assert.True(target.IsFavorite);
        Assert.Equal(second.Id, service.LastFavoriteProfileId);
        Assert.Equal(1, service.GetProfilesCallCount);
    }

    [Fact]
    public async Task UnsafeState_DisablesSwitchCommand()
    {
        var profile = Profile("Paused");
        var vm = CreateViewModel(new FakeProfileService([profile]) { IsSafe = false });
        await vm.InitializeAsync();

        Assert.False(vm.SwitchProfileCommand.CanExecute(null));
        Assert.Equal(vm.Text.Unsafe, vm.SafetyText);
    }

    [Fact]
    public async Task ContinueGame_ActiveProfileLaunchesWithoutSwitching()
    {
        var service = new FakeProfileService([Profile("Active", active: true)]);
        var vm = CreateViewModel(service);
        await vm.InitializeAsync();
        vm.RequestLaunchGameAsync = () =>
        {
            service.Events.Add("launch");
            return Task.FromResult(true);
        };

        await vm.ContinueGameCommand.ExecuteAsync(null);

        Assert.Equal(["launch"], service.Events);
        Assert.Equal(vm.Text.GameLaunchRequested, vm.StatusText);
    }

    [Fact]
    public async Task ContinueGame_InactiveProfileSwitchesBeforeLaunching()
    {
        var service = new FakeProfileService([Profile("Inactive")]);
        var vm = CreateViewModel(service);
        await vm.InitializeAsync();
        vm.RequestLaunchGameAsync = () =>
        {
            service.Events.Add("launch");
            return Task.FromResult(true);
        };

        await vm.ContinueGameCommand.ExecuteAsync(null);

        Assert.Equal(["switch", "launch"], service.Events);
    }

    [Fact]
    public async Task ContinueGame_SwitchFailureDoesNotLaunch()
    {
        var service = new FakeProfileService([Profile("Broken")])
        {
            SwitchException = new InvalidOperationException("switch failed")
        };
        var vm = CreateViewModel(service);
        await vm.InitializeAsync();
        vm.RequestLaunchGameAsync = () =>
        {
            service.Events.Add("launch");
            return Task.FromResult(true);
        };

        await vm.ContinueGameCommand.ExecuteAsync(null);

        Assert.Equal(["switch"], service.Events);
        Assert.True(vm.HasError);
    }

    [Fact]
    public async Task ContinueGame_LaunchFailureKeepsCompletedSwitch()
    {
        var service = new FakeProfileService([Profile("Ready")]);
        var vm = CreateViewModel(service);
        await vm.InitializeAsync();
        vm.RequestLaunchGameAsync = () =>
        {
            service.Events.Add("launch");
            return Task.FromResult(false);
        };

        await vm.ContinueGameCommand.ExecuteAsync(null);

        Assert.Equal(["switch", "launch"], service.Events);
        Assert.Equal(1, service.SwitchCallCount);
        Assert.Equal(vm.Text.ProfileSwitchedLaunchFailed, vm.ErrorText);
    }

    [Fact]
    public async Task UnsafeState_DisablesContinueGame()
    {
        var vm = CreateViewModel(new FakeProfileService([Profile("Unsafe")]) { IsSafe = false });
        await vm.InitializeAsync();
        vm.RequestLaunchGameAsync = () => Task.FromResult(true);

        Assert.False(vm.ContinueGameCommand.CanExecute(null));
    }

    [Fact]
    public async Task ModSummary_LoadsEnabledCountAndDegradesIndependently()
    {
        var available = new FakeModCatalog
        {
            Mods =
            [
                new ModManifest("one", "One", "1", "C:\\mods\\one", 0, true, []),
                new ModManifest("two", "Two", "1", "C:\\mods\\two", 1, false, [])
            ]
        };
        var vm = CreateViewModel(new FakeProfileService([Profile("Mods")]), available);
        await vm.InitializeAsync();

        Assert.True(vm.IsModSummaryAvailable);
        Assert.Equal(1, vm.EnabledModCount);
        Assert.Contains("1", vm.ModProfileSummary);

        var unavailable = CreateViewModel(new FakeProfileService([Profile("Fallback")]), new FakeModCatalog { ThrowOnLoad = true });
        await unavailable.InitializeAsync();

        Assert.False(unavailable.IsModSummaryAvailable);
        Assert.Equal(unavailable.Text.ModConfigurationUnavailable, unavailable.ModProfileSummary);
        Assert.Single(unavailable.Profiles);
    }

    [AvaloniaFact]
    public async Task Thumbnail_ResolvesInsideProfileAndRejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bohemix-thumb-{Guid.NewGuid():N}");
        var profileRoot = Path.Combine(root, "profile");
        Directory.CreateDirectory(profileRoot);
        try
        {
            var thumbnailPath = Path.Combine(profileRoot, "thumb.png");
            await File.WriteAllBytesAsync(
                thumbnailPath,
                Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));

            using var valid = new SaveProfileListItem(
                Profile("Preview") with { PhysicalPath = profileRoot, ThumbnailPath = "thumb.png" },
                SaveManagerText.English,
                "Current configuration");

            Assert.Equal(Path.GetFullPath(thumbnailPath), valid.ThumbnailPath);
            Assert.Null(valid.ThumbnailImage);
            valid.ActivateVisualResources();
            Assert.NotNull(valid.ThumbnailImage);
            valid.DeactivateVisualResources();
            valid.DeactivateVisualResources();
            Assert.Null(valid.ThumbnailImage);
            valid.ActivateVisualResources();
            Assert.NotNull(valid.ThumbnailImage);

            var outsidePath = Path.Combine(root, "outside.png");
            File.Copy(thumbnailPath, outsidePath);
            using var escaped = new SaveProfileListItem(
                Profile("Escaped") with { PhysicalPath = profileRoot, ThumbnailPath = "..\\outside.png" },
                SaveManagerText.English,
                "Current configuration");

            Assert.False(escaped.HasThumbnail);
            Assert.Null(escaped.ThumbnailPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnglishSetting_LoadsEnglishModuleCopy()
    {
        var service = new FakeProfileService([]);
        var vm = new SaveManagerViewModel(
            service,
            new FakeSettingsService(new AppSettings(null, null, "mods", SelectedLanguage: "English")),
            new FakeModCatalog(),
            new LoggerConfiguration().CreateLogger());

        await vm.InitializeAsync();

        Assert.Equal("Saves", vm.Text.Title);
        Assert.Equal("No profiles", vm.Text.NoProfilesTitle);
    }

    [Fact]
    public void DashboardRecentProfiles_AreSortedBySaveTimeAndLimitedToThree()
    {
        var baseline = new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
        var items = Enumerable.Range(0, 5)
            .Select(index => new SaveProfileListItem(
                Profile($"Save {index}") with { LastSavedAtUtc = baseline.AddHours(index) },
                SaveManagerText.English))
            .ToArray();

        try
        {
            var result = SaveManagerViewModel.SelectDashboardRecentProfiles(items);

            Assert.Equal(3, result.Count);
            Assert.Equal(
                ["Save 4", "Save 3", "Save 2"],
                result.Select(item => item.DisplayName));
        }
        finally
        {
            foreach (var item in items)
            {
                item.Dispose();
            }
        }
    }

    [Fact]
    public async Task DeactivateWorkspace_CancelsPendingDetailsAndPreventsLateRefill()
    {
        var profile = Profile("Pending details");
        var service = new FakeProfileService([profile]);
        using var vm = CreateViewModel(service);
        await vm.InitializeAsync();
        service.SnapshotGate = new TaskCompletionSource<IReadOnlyList<SaveSnapshot>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        vm.ActivateWorkspaceVisualResources();
        await service.SnapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(vm.IsSelectedDetailsLoadActive);

        vm.DeactivateWorkspaceVisualResources();
        service.SnapshotGate.TrySetResult([]);
        for (var attempt = 0; attempt < 20 && vm.IsSelectedDetailsLoadActive; attempt++)
        {
            await Task.Delay(10);
        }

        Assert.False(vm.IsSelectedDetailsLoadActive);
        Assert.Empty(vm.SnapshotItems);
        Assert.Empty(vm.BackupNodeItems);
    }

    [Fact]
    public async Task Refresh_ShowsPhysicalLogicalAndMigrationStorageStatus()
    {
        var service = new FakeProfileService([])
        {
            StorageStats = new SaveSnapshotStorageStats(
                4 * 1024 * 1024,
                1024 * 1024,
                2 * 1024 * 1024,
                3,
                SaveSnapshotMaintenanceState.PausedLowSpace)
        };
        var vm = CreateViewModel(service);

        await vm.InitializeAsync();

        Assert.Contains("实际", vm.SnapshotStorageSummary);
        Assert.Contains("已节省", vm.SnapshotStorageSummary);
        Assert.True(vm.HasSnapshotMigrationStatus);
        Assert.Contains("空间不足", vm.SnapshotMigrationStatus);
    }

    private static SaveManagerViewModel CreateViewModel(FakeProfileService service, FakeModCatalog? modCatalog = null) => new(
        service,
        new FakeSettingsService(new AppSettings(null, null, "mods")),
        modCatalog ?? new FakeModCatalog(),
        new LoggerConfiguration().CreateLogger());

    private static SaveProfile Profile(string name, bool favorite = false, string location = "", bool active = false) => new(
        Guid.NewGuid(),
        $"Profile_{Guid.NewGuid():N}",
        name,
        Path.Combine("C:\\vault", Guid.NewGuid().ToString("N")),
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow,
        favorite,
        null,
        null,
        null,
        null,
        null,
        new DisplayInfo { CurrentLocation = location },
        IsActive: active);

    private sealed class FakeModCatalog : IModCatalogService
    {
        public IReadOnlyList<ModManifest> Mods { get; init; } = [];
        public bool ThrowOnLoad { get; init; }

        public Task<IReadOnlyList<ModManifest>> LoadInstalledModsAsync(CancellationToken cancellationToken = default) =>
            ThrowOnLoad
                ? Task.FromException<IReadOnlyList<ModManifest>>(new IOException("mod catalog unavailable"))
                : Task.FromResult(Mods);

        public Task SaveLoadOrderAsync(IReadOnlyList<string> orderedModIds, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveModEnabledStateAsync(string modId, bool isEnabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SaveModEnabledStatesAsync(IReadOnlyDictionary<string, bool> enabledStates, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UpdateModMetadataAsync(ModManifest mod, string displayName, string version, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeSettingsService(AppSettings settings) : IAppSettingsService
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeProfileService(IReadOnlyList<SaveProfile> profiles) : ISaveProfileService
    {
        public bool IsSafe { get; set; } = true;
        public int GetProfilesCallCount { get; private set; }
        public TaskCompletionSource<bool> FavoriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool>? FavoriteGate { get; init; }
        public TaskCompletionSource<IReadOnlyList<SaveSnapshot>>? SnapshotGate { get; set; }
        public TaskCompletionSource<bool> SnapshotStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Events { get; } = [];
        public Exception? SwitchException { get; init; }
        public int SwitchCallCount { get; private set; }
        public Guid? LastFavoriteProfileId { get; private set; }
        public SaveSnapshotStorageStats StorageStats { get; init; } =
            new(0, 0, 0, 0, SaveSnapshotMaintenanceState.Complete);
        public IReadOnlyList<SaveMigrationWarning> MigrationWarnings => [];
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SaveProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
        {
            GetProfilesCallCount++;
            return Task.FromResult(profiles);
        }
        public async Task<IReadOnlyList<SaveSnapshot>> GetSnapshotsAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            if (SnapshotGate is null)
            {
                return [];
            }

            SnapshotStarted.TrySetResult(true);
            return await SnapshotGate.Task.WaitAsync(cancellationToken);
        }
        public Task<SaveSnapshotStorageStats> GetSnapshotStorageStatsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(StorageStats);
        public Task<IReadOnlyList<SaveBackupNode>> GetBackupNodesAsync(Guid profileId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SaveBackupNode>>([]);
        public Task<SaveBackupLibraryStats> GetBackupLibraryStatsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SaveBackupLibraryStats(0, 0, 0, 0, 0, 10));
        public Task<IReadOnlyList<SaveBackupNode>> ReconcileBackupNodesAsync(Guid profileId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SaveBackupNode>>([]);
        public Task SetBackupNodeImportanceAsync(Guid nodeId, bool isImportant, string? note = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RestoreBackupNodeAsync(Guid nodeId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteBackupNodeAsync(Guid nodeId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ExportBackupNodeAsync(Guid nodeId, string destinationZipPath, IProgress<SaveImportProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<SaveBackupNode>> ImportExistingBackupNodesAsync(Guid profileId, IReadOnlyCollection<string> filePaths, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SaveBackupNode>>([]);
        public Task<SaveSnapshotConversionPreview> PreviewFullSnapshotConversionAsync(Guid snapshotId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SaveBackupNode>> ConvertFullSnapshotToNodesAsync(Guid snapshotId, IReadOnlyCollection<string> selectedRelativePaths, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SaveProfile?> GetActiveProfileAsync(CancellationToken cancellationToken = default) => Task.FromResult(profiles.FirstOrDefault(item => item.IsActive));
        public Task<bool> HasUnmanagedSaveAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> IsSafeToChangeAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsSafe);
        public Task<SaveProfile> ManageCurrentSaveAsync(string? displayName = null, IProgress<SaveImportProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SaveProfile> CloneProfileAsync(Guid sourceProfileId, string displayName, IProgress<SaveImportProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SwitchProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
        {
            SwitchCallCount++;
            Events.Add("switch");
            return SwitchException is null ? Task.CompletedTask : Task.FromException(SwitchException);
        }
        public Task<SaveSnapshot?> CreateSnapshotAsync(Guid profileId, SaveSnapshotTrigger trigger = SaveSnapshotTrigger.Manual, string? note = null, IProgress<SaveImportProgress>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult<SaveSnapshot?>(null);
        public Task RestoreSnapshotAsync(Guid snapshotId, IProgress<SaveImportProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RenameProfileAsync(Guid profileId, string displayName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task SetFavoriteAsync(Guid profileId, bool isFavorite, CancellationToken cancellationToken = default)
        {
            LastFavoriteProfileId = profileId;
            FavoriteStarted.TrySetResult(true);
            if (FavoriteGate is not null)
            {
                await FavoriteGate.Task.WaitAsync(cancellationToken);
            }
        }
        public Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ExportProfileAsync(Guid profileId, string destinationZipPath, SavePackageExportOptions? options = null, IProgress<SaveImportProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ExportSnapshotAsync(Guid snapshotId, string destinationZipPath, IProgress<SaveImportProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<SaveProfile> ImportPackageAsync(string packagePath, IProgress<SaveImportProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SaveSnapshot?> CreateGameExitSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult<SaveSnapshot?>(null);
        public Task<bool> SaveGameExitThumbnailAsync(byte[] pngData, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public string GetOfficialSavePath() => "C:\\official";
        public string GetVaultRootPath() => "C:\\vault";
        public string GetSnapshotsRootPath() => "C:\\snapshots";
    }
}
