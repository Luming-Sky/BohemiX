using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Persistence;
using BohemiX.Infrastructure.Services;
using System.IO.Compression;
using Serilog;

namespace BohemiX.Core.Tests;

public sealed class RuntimeFoundationTests
{
    [Fact]
    public async Task AppSettings_RoundTripsInstalledModGrouping()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var pathService = new ApplicationPathService(root);
            var logger = new LoggerConfiguration().CreateLogger();
            var connectionFactory = new SqliteConnectionFactory(pathService);
            var settingsService = new AppSettingsService(pathService, connectionFactory, logger);
            var startupService = new AppStartupService(pathService, settingsService, connectionFactory, logger);

            await startupService.InitializeAsync();
            var existing = await settingsService.LoadAsync();
            await settingsService.SaveAsync(existing with
            {
                InstalledModGroupNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["modpack:test-pack"] = "Core gameplay",
                    ["standalone"] = "Loose mods",
                    ["custom:essentials"] = "Essentials"
                },
                InstalledModGroupAssignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["nexus-42"] = "custom:essentials"
                }
            });

            var reloaded = await settingsService.LoadAsync();

            Assert.NotNull(reloaded.InstalledModGroupNames);
            Assert.Equal("Core gameplay", reloaded.InstalledModGroupNames["MODPACK:TEST-PACK"]);
            Assert.Equal("Loose mods", reloaded.InstalledModGroupNames["standalone"]);
            Assert.NotNull(reloaded.InstalledModGroupAssignments);
            Assert.Equal("custom:essentials", reloaded.InstalledModGroupAssignments["NEXUS-42"]);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task InitializeAndScanMods_CreatesDatabaseAndFindsConflicts()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var pathService = new ApplicationPathService(root);
            var logger = new LoggerConfiguration().CreateLogger();
            var connectionFactory = new SqliteConnectionFactory(pathService);
            var settingsService = new AppSettingsService(pathService, connectionFactory, logger);
            var startupService = new AppStartupService(pathService, settingsService, connectionFactory, logger);

            var paths = await startupService.InitializeAsync();
            Directory.CreateDirectory(Path.Combine(paths.ModsDirectory, "alpha", "Data", "Textures"));
            Directory.CreateDirectory(Path.Combine(paths.ModsDirectory, "beta", "data", "textures"));
            Directory.CreateDirectory(Path.Combine(paths.ModsDirectory, "downloads"));
            await File.WriteAllTextAsync(Path.Combine(paths.ModsDirectory, "alpha", "Data", "Textures", "Armor.dds"), "alpha");
            await File.WriteAllTextAsync(Path.Combine(paths.ModsDirectory, "beta", "data", "textures", "armor.dds"), "beta");
            await File.WriteAllTextAsync(Path.Combine(paths.ModsDirectory, "downloads", "source-package.zip"), "archive");

            IModCatalogService catalogService = new ModCatalogService(pathService, connectionFactory, logger);
            IModConflictAnalyzer analyzer = new ModConflictAnalyzer();
            IModConflictReviewService conflictReviewService = new ModConflictReviewService(connectionFactory, logger);
            IModHealthAnalyzer healthAnalyzer = new ModHealthAnalyzer();
            IModMountPlanBuilder mountPlanBuilder = new ModMountPlanBuilder(analyzer);
            IModManagementSnapshotService snapshotService = new ModManagementSnapshotService(
                catalogService,
                conflictReviewService,
                healthAnalyzer,
                mountPlanBuilder);
            IModLaunchPreflightService preflightService = new ModLaunchPreflightService(snapshotService);

            var mods = await catalogService.LoadInstalledModsAsync();
            var conflicts = analyzer.AnalyzeConflicts(mods);
            var snapshot = await snapshotService.LoadSnapshotAsync();

            Assert.True(File.Exists(paths.DatabasePath));
            Assert.Equal(2, mods.Count);
            Assert.DoesNotContain(mods, mod => string.Equals(mod.Id, "downloads", StringComparison.OrdinalIgnoreCase));
            Assert.All(mods, mod => Assert.NotEmpty(mod.Files));
            Assert.All(mods, mod => Assert.True(mod.IsEnabled));
            Assert.Single(conflicts);
            Assert.Equal(2, snapshot.TotalModCount);
            Assert.Equal(2, snapshot.EnabledModCount);
            Assert.Equal(0, snapshot.DisabledModCount);
            Assert.Single(snapshot.Conflicts);
            Assert.Equal(2, snapshot.HealthReport.Mods.Count);
            Assert.Equal(0, snapshot.HealthReport.ErrorModCount);
            Assert.Equal(0, snapshot.ReviewedConflictCount);
            Assert.Equal(1, snapshot.PendingConflictCount);
            Assert.Equal("beta", snapshot.MountPlan.Entries.Single(entry => entry.NormalizedVirtualPath == "DATA/TEXTURES/ARMOR.DDS").WinningModId);

            var blockedPreflight = await preflightService.EvaluateAsync(new ModLaunchPreflightOptions(
                @"D:\Games\KCD2\Bin\Win64MasterMasterSteamPGO\KingdomCome.exe",
                RequireReviewedConflicts: true,
                EnableVfs: true));
            Assert.False(blockedPreflight.CanLaunch);
            Assert.Null(blockedPreflight.MountRequest);
            Assert.Equal(2, blockedPreflight.EnabledMods.Count);
            Assert.Equal(1, blockedPreflight.Snapshot.PendingConflictCount);

            await conflictReviewService.SaveReviewAsync(snapshot.Conflicts.Single().Fingerprint, isReviewed: true);
            var reviewedSnapshot = await snapshotService.LoadSnapshotAsync();
            Assert.Equal(1, reviewedSnapshot.ReviewedConflictCount);
            Assert.Equal(0, reviewedSnapshot.PendingConflictCount);

            var allowedPreflight = await preflightService.EvaluateAsync(new ModLaunchPreflightOptions(
                @"D:\Games\KCD2\Bin\Win64MasterMasterSteamPGO\KingdomCome.exe",
                RequireReviewedConflicts: true,
                EnableVfs: true));
            Assert.True(allowedPreflight.CanLaunch);
            Assert.NotNull(allowedPreflight.MountRequest);
            Assert.Equal(2, allowedPreflight.EnabledMods.Count);
            Assert.Equal(0, allowedPreflight.Snapshot.PendingConflictCount);
            Assert.Equal(allowedPreflight.Snapshot.MountPlan, allowedPreflight.MountRequest.MountPlan);

            var disabledVfsPreference = await preflightService.EvaluateAsync(new ModLaunchPreflightOptions(
                @"D:\Games\KCD2\Bin\Win64MasterMasterSteamPGO\KingdomCome.exe",
                RequireReviewedConflicts: true,
                EnableVfs: false));
            Assert.True(disabledVfsPreference.CanLaunch);
            Assert.NotNull(disabledVfsPreference.MountRequest);
            Assert.Equal(2, disabledVfsPreference.EnabledMods.Count);

            await catalogService.SaveLoadOrderAsync(["beta", "alpha"]);
            await catalogService.SaveModEnabledStateAsync("beta", isEnabled: false);
            var reorderedMods = await catalogService.LoadInstalledModsAsync();

            Assert.Equal("beta", reorderedMods[0].Id);
            Assert.Equal("alpha", reorderedMods[1].Id);
            Assert.False(reorderedMods[0].IsEnabled);
            Assert.True(reorderedMods[1].IsEnabled);

            await catalogService.SaveModEnabledStatesAsync(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["alpha"] = false,
                ["beta"] = true
            });
            var enabledStateMods = await catalogService.LoadInstalledModsAsync();

            Assert.True(enabledStateMods.Single(mod => mod.Id == "beta").IsEnabled);
            Assert.False(enabledStateMods.Single(mod => mod.Id == "alpha").IsEnabled);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task VerifyManualPath_SavesVerifiedExecutableToInstallationCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var pathService = new ApplicationPathService(root);
            var logger = new LoggerConfiguration().CreateLogger();
            var connectionFactory = new SqliteConnectionFactory(pathService);
            var settingsService = new AppSettingsService(pathService, connectionFactory, logger);
            var startupService = new AppStartupService(pathService, settingsService, connectionFactory, logger);
            var store = new GameInstallationStore(connectionFactory, logger);
            var discoveryService = new GameDiscoveryService(logger, store);

            var paths = await startupService.InitializeAsync();

            var executableDirectory = Path.Combine(root, "FakeKcd2", "Bin", "Win64MasterMasterSteamPGO");
            Directory.CreateDirectory(executableDirectory);
            var executablePath = Path.Combine(executableDirectory, "KingdomCome.exe");
            await File.WriteAllTextAsync(executablePath, "fake executable");

            var game = await discoveryService.VerifyManualPathAsync(executablePath);
            var cachedGames = await store.LoadAsync();

            Assert.NotNull(game);
            Assert.True(game.IsVerified);
            Assert.Equal(executablePath, game.ExecutablePath);
            Assert.Contains(cachedGames, cached => cached.ExecutablePath == executablePath && cached.IsVerified);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TrackerProjection_IngestsJsonlAndPreservesPartialLine()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var pathService = new ApplicationPathService(root);
            var logger = new LoggerConfiguration().CreateLogger();
            var connectionFactory = new SqliteConnectionFactory(pathService);
            var settingsService = new AppSettingsService(pathService, connectionFactory, logger);
            var startupService = new AppStartupService(pathService, settingsService, connectionFactory, logger);
            var trackerService = new TrackerProjectionService(pathService, connectionFactory, logger);
            var achievementRuleService = new AchievementRuleService(logger);
            var trackerAchievementService = new TrackerAchievementService(pathService, achievementRuleService, connectionFactory, logger);

            var paths = await startupService.InitializeAsync();
            await File.AppendAllTextAsync(
                paths.TrackerBridgeEventsPath,
                string.Join(
                    "\n",
                    """{"event_id":"e1","session_id":"s1","type":"QUEST_START","timestamp":"2026-06-06T10:00:00Z","entity_id":"q_intro","entity_name":"Awakening"}""",
                    "not-json",
                    """{"event_id":"e2","session_id":"s1","type":"ITEM_ACQUIRED","timestamp":"2026-06-06T10:01:00Z","entity_id":"coin","entity_name":"Groschen"}""",
                    """{"event_id":"e3","session_id":"s1","type":"POS_UPDATE","timestamp":"2026-06-06T10:02:00Z","x":10.5,"y":20.25,"z":3}""")
                + "\n"
                + "{\"event_id\":\"partial\",\"session_id\":\"s1\",\"type\":\"QUEST_COMPLETED\"");

            var first = await trackerService.IngestBridgeFileAsync();
            Assert.Equal(3, first.ProcessedEvents);
            Assert.Equal(1, first.SkippedLines);
            Assert.Equal(3, first.Summary.VisibleEntities);
            Assert.Equal(1, first.Summary.ActiveQuests);
            Assert.Equal(1, first.Summary.CompletedEntities);
            Assert.Equal(1, first.Summary.UnconfirmedSessions);
            Assert.Equal(10.5, first.Summary.PlayerX);
            Assert.Equal(3, (await trackerService.LoadVisibleEntitiesAsync()).Count);
            Assert.Contains(await trackerService.LoadRecentEventLabelsAsync(3), label => label.Contains("PositionUpdated"));

            await File.AppendAllTextAsync(
                paths.TrackerBridgeEventsPath,
                """
                ,"entity_id":"q_intro","entity_name":"Awakening"}
                {"event_id":"e4","session_id":"s1","type":"GAME_SAVED","timestamp":"2026-06-06T10:04:00Z","entity_id":"save","entity_name":"Game saved"}

                """);

            var second = await trackerService.IngestBridgeFileAsync();
            Assert.Equal(2, second.ProcessedEvents);
            Assert.Equal(0, second.Summary.ActiveQuests);
            Assert.Equal(2, second.Summary.CompletedEntities);
            Assert.Equal(0, second.Summary.UnconfirmedSessions);

            var achievements = await trackerAchievementService.EvaluateAsync();
            var persistedAchievements = await trackerAchievementService.LoadProgressAsync();

            Assert.True(File.Exists(paths.TrackerAchievementRulesPath));
            Assert.Contains(achievements, item => item.AchievementId == "tracker.first_trace" && item.IsUnlocked);
            Assert.Contains(achievements, item => item.AchievementId == "tracker.quest_completed" && item.IsUnlocked);
            Assert.Equal(achievements.Count, persistedAchievements.Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TrackerModPackage_PreparesLocalCatalogPackage()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var pathService = new ApplicationPathService(root);
            var logger = new LoggerConfiguration().CreateLogger();
            var connectionFactory = new SqliteConnectionFactory(pathService);
            var settingsService = new AppSettingsService(pathService, connectionFactory, logger);
            var startupService = new AppStartupService(pathService, settingsService, connectionFactory, logger);
            var packageService = new TrackerModPackageService(pathService, logger);
            var installService = new TrackerModInstallService(packageService, logger);
            var healthService = new TrackerModHealthService(pathService, logger);
            IModCatalogService catalogService = new ModCatalogService(pathService, connectionFactory, logger);

            var paths = await startupService.InitializeAsync();
            var result = await packageService.PrepareAsync();
            var mods = await catalogService.LoadInstalledModsAsync();
            var localHealth = await healthService.CheckAsync(null);
            var fakeGameRoot = Path.Combine(root, "KCD2");
            var fakeExecutable = Path.Combine(fakeGameRoot, "Bin", "Win64MasterMasterSteamPGO", "KingdomCome.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(fakeExecutable)!);
            await File.WriteAllTextAsync(fakeExecutable, "fake executable");
            Directory.CreateDirectory(Path.Combine(fakeGameRoot, "Mods"));
            await File.WriteAllLinesAsync(Path.Combine(fakeGameRoot, "Mods", "mod_order.txt"), ["existing-mod"]);
            var installResult = await installService.InstallAsync(new DiscoveredGame(
                "Kingdom Come: Deliverance II",
                fakeGameRoot,
                fakeExecutable,
                GameInstallSource.Manual,
                true));
            var installedHealth = await healthService.CheckAsync(new DiscoveredGame(
                "Kingdom Come: Deliverance II",
                fakeGameRoot,
                fakeExecutable,
                GameInstallSource.Manual,
                true));

            Assert.True(File.Exists(result.LuaScriptPath));
            Assert.True(File.Exists(result.ManifestPath));
            Assert.True(File.Exists(result.BridgeEventsPath));
            var pakPath = Path.Combine(result.PackageDirectory, "Data", "bohemix-tracker.pak");
            Assert.True(File.Exists(pakPath));
            using (var archive = ZipFile.OpenRead(pakPath))
            {
                var scriptEntry = archive.GetEntry("Scripts/Mods/bohemix-tracker.lua");
                Assert.NotNull(scriptEntry);
                Assert.NotNull(archive.GetEntry("Scripts/Mods/bohemix_tracker_config.lua"));
                Assert.Contains("<name>bohemix-tracker</name>", await File.ReadAllTextAsync(result.ManifestPath));

                using var scriptReader = new StreamReader(scriptEntry.Open());
                var script = await scriptReader.ReadToEndAsync();
                Assert.Contains($"tracker.bridge_path = \"{paths.TrackerBridgeEventsPath.Replace("\\", "\\\\", StringComparison.Ordinal)}\"", script);
                Assert.Contains("GetStatLevel(\"storyProgress\")", script);
                Assert.Contains("GetStatLevel(stat_name)", script);
                Assert.Contains("\"strength\", \"agility\", \"vitality\", \"speech\"", script);
                Assert.Contains("inventory:GetMoney()", script);
                Assert.Contains("henry_level", script);
                Assert.Contains("groschen", script);
                Assert.Contains("TRACKER_LOADED", script);
                Assert.Contains("[BohemiXTrackerEvent]", script);
                Assert.Contains("timestamp_unix", script);
                Assert.Contains("string.format(\"%.0f\", timestamp_unix)", script);
                Assert.Contains("emit failed for", script);
                Assert.DoesNotContain("os.date", script);
                Assert.DoesNotContain("os.clock", script);
                Assert.Contains("tracker.SamplePlayerPosition()", script);
                Assert.Contains("emit_character_event(\"CHARACTER_SNAPSHOT\", \"player\", \"Henry\")", script);
            }

            Assert.True(localHealth.IsLocalPackagePrepared);
            Assert.True(localHealth.BridgeFileExists);
            Assert.Null(localHealth.BridgeLastWriteUtc);
            Assert.Contains(mods, mod => mod.Id == "bohemix-tracker" && mod.Files.Any(file => file.RelativePath.EndsWith("bohemix_tracker.lua")));
            await catalogService.SaveModEnabledStateAsync("bohemix-tracker", isEnabled: false);
            var trackerAfterDisableAttempt = (await catalogService.LoadInstalledModsAsync())
                .Single(mod => mod.Id == "bohemix-tracker");
            Assert.True(trackerAfterDisableAttempt.IsEnabled);
            Assert.True(installResult.IsInstalled);
            Assert.True(File.Exists(Path.Combine(fakeGameRoot, "Mods", "bohemix-tracker", "Data", "Scripts", "BohemiX", "bohemix_tracker.lua")));
            Assert.True(File.Exists(Path.Combine(fakeGameRoot, "Mods", "bohemix-tracker", "Data", "bohemix-tracker.pak")));
            Assert.Equal(["existing-mod", "bohemix-tracker"], await File.ReadAllLinesAsync(Path.Combine(fakeGameRoot, "Mods", "mod_order.txt")));
            Assert.True(installedHealth.IsInstalledToGame);
            Assert.True(installedHealth.IsListedInModOrder);
            Assert.Contains("empty", installedHealth.Message, StringComparison.OrdinalIgnoreCase);

            var installedScript = Path.Combine(fakeGameRoot, "Mods", "bohemix-tracker", "Data", "Scripts", "BohemiX", "bohemix_tracker.lua");
            await File.AppendAllTextAsync(installedScript, "\n-- stale tracker version\n");
            var staleHealth = await healthService.CheckAsync(new DiscoveredGame(
                "Kingdom Come: Deliverance II",
                fakeGameRoot,
                fakeExecutable,
                GameInstallSource.Manual,
                true));
            Assert.False(staleHealth.IsInstalledToGame);
            Assert.Contains("install", staleHealth.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TrackerDiagnostics_SelfTestProcessesAndCleansSyntheticProjection()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var pathService = new ApplicationPathService(root);
            var logger = new LoggerConfiguration().CreateLogger();
            var connectionFactory = new SqliteConnectionFactory(pathService);
            var settingsService = new AppSettingsService(pathService, connectionFactory, logger);
            var startupService = new AppStartupService(pathService, settingsService, connectionFactory, logger);
            var trackerService = new TrackerProjectionService(pathService, connectionFactory, logger);
            var diagnosticsService = new TrackerDiagnosticsService(pathService, trackerService, connectionFactory, logger);

            await startupService.InitializeAsync();
            var result = await diagnosticsService.RunBridgeSelfTestAsync();
            var summary = await trackerService.LoadSummaryAsync();

            Assert.True(result.IsSuccessful);
            Assert.Equal(4, result.EventsWritten);
            Assert.Equal(4, result.EventsProcessed);
            Assert.Equal(0, summary.TotalEvents);
            Assert.Equal(0, summary.VisibleEntities);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task VfsMount_UsesProvidedMountPlanWhenNativeLibraryIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var pathService = new ApplicationPathService(root);
            var logger = new LoggerConfiguration().CreateLogger();
            var suppliedPlan = new ModMountPlan(
                1,
                1,
                0,
                1,
                1,
                0,
                [],
                [
                    new ModMountPlanEntry(
                        "DATA/CONFIG/SETTINGS.XML",
                        "alpha",
                        ["alpha"],
                        128,
                        "alpha-settings")
                ]);
            var vfsService = new VfsSessionService(logger, pathService, new ThrowingMountPlanBuilder());

            await vfsService.MountAsync(new VfsMountRequest(
                @"D:\Games\KCD2\Bin\Win64MasterMasterSteamPGO\KingdomCome.exe",
                [],
                suppliedPlan));

            Assert.Equal(VfsSessionState.Faulted, vfsService.CurrentState);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class ThrowingMountPlanBuilder : IModMountPlanBuilder
    {
        public ModMountPlan BuildPlan(IEnumerable<ModManifest> mods)
        {
            throw new InvalidOperationException("The supplied mount plan should be used directly.");
        }
    }
}
