using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BohemiX.Core.Models;
using BohemiX.Core.Models.Saves;
using BohemiX.Core.Services;
using BohemiX.Core.Services.Saves;
using BohemiX.Infrastructure.Persistence;
using BohemiX.Infrastructure.Services;
using BohemiX.Infrastructure.Services.Saves;
using Microsoft.Data.Sqlite;
using Serilog;

namespace BohemiX.Core.Tests;

public sealed class SaveProfileServiceTests
{
    [Fact]
    public async Task GameExitThumbnail_IsStoredInsideActiveProfileAndExposedAsThumbnailPath()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Exit frame", "save");
        harness.Router.TargetPath = profile.PhysicalPath;
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScL4xQAAAABJRU5ErkJggg==");

        var saved = await harness.Service.SaveGameExitThumbnailAsync(png);
        var updated = (await harness.Service.GetProfilesAsync()).Single(item => item.Id == profile.Id);

        Assert.True(saved);
        Assert.Equal(".bohemix\\exit-thumbnail.png", updated.ThumbnailPath);
        Assert.True(File.Exists(Path.Combine(profile.PhysicalPath, ".bohemix", "exit-thumbnail.png")));
    }

    [Fact]
    public async Task CharacterSnapshot_BackfillsProfileAndPersistsIntoSnapshot()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Character data", "save");
        var savedAt = DateTimeOffset.UtcNow.AddSeconds(-10);
        var whsPath = Path.Combine(profile.PhysicalPath, "manual.whs");
        await File.WriteAllTextAsync(whsPath, "not a real save");
        File.SetLastWriteTimeUtc(whsPath, savedAt.UtcDateTime);
        await harness.WriteCharacterEventAsync(savedAt, level: 17, groschen: 2345);

        var backfilled = (await harness.Service.GetProfilesAsync()).Single(item => item.Id == profile.Id);
        Assert.NotNull(backfilled.DisplayData);
        Assert.Equal(17, backfilled.DisplayData!.HenryLevel);
        Assert.Equal(2345, backfilled.DisplayData.GroschenCount);

        var snapshot = await harness.Service.CreateSnapshotAsync(profile.Id, SaveSnapshotTrigger.Manual);
        Assert.NotNull(snapshot);
        Assert.Equal(17, snapshot!.DisplayData?.HenryLevel);
        Assert.Equal(2345, snapshot.DisplayData?.GroschenCount);

        var persistedProfile = (await harness.Service.GetProfilesAsync()).Single(item => item.Id == profile.Id);
        var persistedSnapshot = Assert.Single(await harness.Service.GetSnapshotsAsync(profile.Id));
        Assert.Equal(17, persistedProfile.DisplayData?.HenryLevel);
        Assert.Equal(2345, persistedProfile.DisplayData?.GroschenCount);
        Assert.Equal(17, persistedSnapshot.DisplayData?.HenryLevel);
        Assert.Equal(2345, persistedSnapshot.DisplayData?.GroschenCount);
    }

    [Fact]
    public async Task RecentCharacterSnapshot_BackfillsTheActiveProfileBeforeAnotherSave()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Active character data", "save");
        harness.Router.TargetPath = profile.PhysicalPath;
        await harness.WriteCharacterEventAsync(DateTimeOffset.UtcNow, level: 18, groschen: 4567, eventType: "CHARACTER_SNAPSHOT");

        var backfilled = (await harness.Service.GetProfilesAsync()).Single(item => item.Id == profile.Id);

        Assert.Equal(18, backfilled.DisplayData?.HenryLevel);
        Assert.Equal(4567, backfilled.DisplayData?.GroschenCount);
    }

    [Fact]
    public async Task GameLogCharacterSnapshot_BackfillsTheActiveProfileWhenBridgeIsEmpty()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Game log character data", "save");
        harness.Router.TargetPath = profile.PhysicalPath;
        await harness.WriteGameLogCharacterEventAsync(DateTimeOffset.UtcNow, level: 19, groschen: 5678);

        var backfilled = (await harness.Service.GetProfilesAsync()).Single(item => item.Id == profile.Id);

        Assert.Equal(19, backfilled.DisplayData?.HenryLevel);
        Assert.Equal(5678, backfilled.DisplayData?.GroschenCount);
    }

    [Fact]
    public async Task CharacterSnapshotReader_RejectsStaleAndNonSaveEvents()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX-CharacterSnapshotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = DateTimeOffset.UtcNow;
            var bridge = Path.Combine(root, "bridge_events.jsonl");
            await File.WriteAllLinesAsync(bridge,
            [
                $$"""{"session_id":"old","type":"GAME_SAVED","timestamp":"{{target.AddMinutes(-4):O}}","henry_level":40,"groschen":9999}""",
                $$"""{"session_id":"current","type":"SESSION_END","timestamp":"{{target:O}}","henry_level":20,"groschen":500}"""
            ]);

            var result = await TrackerCharacterSnapshotReader.FindClosestAsync(bridge, [target]);

            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CharacterSnapshotReader_AcceptsPeriodicCharacterSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX-CharacterSnapshotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = DateTimeOffset.UtcNow;
            var bridge = Path.Combine(root, "bridge_events.jsonl");
            await File.WriteAllTextAsync(
                bridge,
                $$"""{"session_id":"current","type":"CHARACTER_SNAPSHOT","timestamp":"{{target.AddSeconds(-5):O}}","henry_level":22,"groschen":6789}""");

            var result = await TrackerCharacterSnapshotReader.FindClosestAsync(bridge, [target]);

            Assert.NotNull(result);
            Assert.Equal(22, result!.HenryLevel);
            Assert.Equal(6789, result.GroschenCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CharacterSnapshotReader_FallsBackToPrefixedGameLogEvents()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX-CharacterSnapshotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = DateTimeOffset.UtcNow;
            var bridge = Path.Combine(root, "bridge_events.jsonl");
            var gameLog = Path.Combine(root, "kcd.log");
            await File.WriteAllTextAsync(bridge, string.Empty);
            await File.WriteAllLinesAsync(gameLog,
            [
                "ordinary game log line",
                $"[BohemiXTrackerEvent] {{\"session_id\":\"current\",\"type\":\"CHARACTER_SNAPSHOT\",\"timestamp\":\"{target:O}\",\"henry_level\":23,\"groschen\":7890}}"
            ]);

            var result = await TrackerCharacterSnapshotReader.FindClosestAsync(bridge, gameLog, [target]);

            Assert.NotNull(result);
            Assert.Equal(23, result!.HenryLevel);
            Assert.Equal(7890, result.GroschenCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CharacterSnapshotReader_AcceptsUnixTimestampFromSandboxedLua()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX-CharacterSnapshotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = DateTimeOffset.UtcNow;
            var bridge = Path.Combine(root, "bridge_events.jsonl");
            await File.WriteAllTextAsync(
                bridge,
                $"{{\"session_id\":\"current\",\"type\":\"CHARACTER_SNAPSHOT\",\"timestamp_unix\":{target.ToUnixTimeSeconds()},\"henry_level\":24,\"groschen\":8901}}");

            var result = await TrackerCharacterSnapshotReader.FindClosestAsync(bridge, [target]);

            Assert.NotNull(result);
            Assert.Equal(24, result!.HenryLevel);
            Assert.Equal(8901, result.GroschenCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CharacterSnapshotReader_AcceptsScientificNotationUnixTimestampFromLua()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX-CharacterSnapshotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = DateTimeOffset.FromUnixTimeSeconds(1_784_700_000);
            var bridge = Path.Combine(root, "bridge_events.jsonl");
            await File.WriteAllTextAsync(
                bridge,
                "{\"session_id\":\"current\",\"type\":\"CHARACTER_SNAPSHOT\",\"timestamp_unix\":1.7847e+09,\"henry_level\":25,\"groschen\":9012}");

            var result = await TrackerCharacterSnapshotReader.FindClosestAsync(bridge, [target]);

            Assert.NotNull(result);
            Assert.Equal(25, result!.HenryLevel);
            Assert.Equal(9012, result.GroschenCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CharacterSnapshotReader_CanMatchRecentActiveProfileUsingGameLogWriteTime()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX-CharacterSnapshotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = DateTimeOffset.UtcNow;
            var bridge = Path.Combine(root, "bridge_events.jsonl");
            var gameLog = Path.Combine(root, "kcd.log");
            await File.WriteAllTextAsync(bridge, string.Empty);
            await File.WriteAllTextAsync(
                gameLog,
                "[BohemiXTrackerEvent] {\"session_id\":\"current\",\"type\":\"CHARACTER_SNAPSHOT\",\"timestamp_unix\":1.7847e+09,\"henry_level\":21,\"groschen\":1663}");
            File.SetLastWriteTimeUtc(gameLog, target.UtcDateTime);

            var result = await TrackerCharacterSnapshotReader.FindClosestAsync(
                bridge,
                gameLog,
                [target],
                matchGameLogUsingFileTime: true);

            Assert.NotNull(result);
            Assert.Equal(21, result!.HenryLevel);
            Assert.Equal(1663, result.GroschenCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Initialize_UpgradesExistingSaveSlotsRowInPlace()
    {
        using var harness = new Harness();
        var id = Guid.NewGuid();
        await harness.CreateLegacyProfileBeforeInitializationAsync(id, "Legacy campaign", "Slot_legacy");

        await harness.Service.InitializeAsync();

        var profile = Assert.Single(await harness.Service.GetProfilesAsync());
        Assert.Equal(id, profile.Id);
        Assert.Equal("Legacy campaign", profile.DisplayName);
        Assert.True(Directory.Exists(profile.PhysicalPath));

        await using var connection = harness.OpenConnection();
        var columns = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(SaveSlots);";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Contains("IsFavorite", columns);
        Assert.Contains("LastActivatedAtUtc", columns);
        Assert.Contains("ContentFingerprint", columns);
    }

    [Fact]
    public async Task AutomaticSnapshot_SkipsUnchangedContentButDetectsSameLengthChange()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Fingerprint", "AAAA");

        var first = await harness.Service.CreateSnapshotAsync(profile.Id, SaveSnapshotTrigger.GameExit);
        var unchanged = await harness.Service.CreateSnapshotAsync(profile.Id, SaveSnapshotTrigger.GameExit);
        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "save.bin"), "BBBB");
        var changed = await harness.Service.CreateSnapshotAsync(profile.Id, SaveSnapshotTrigger.GameExit);

        Assert.NotNull(first);
        Assert.Null(unchanged);
        Assert.NotNull(changed);
        Assert.Equal(2, (await harness.Service.GetSnapshotsAsync(profile.Id)).Count);
    }

    [Fact]
    public async Task BackupLibrary_FirstScanKeepsRecentWindowAndAllCriticalDecisions()
    {
        using var harness = new Harness(backupRetention: 10);
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Node seed", "profile");
        var baseTime = DateTime.UtcNow.AddHours(-2);
        for (var index = 1; index <= 15; index++)
        {
            var path = Path.Combine(profile.PhysicalPath, $"autosave{index}.whs");
            await File.WriteAllTextAsync(path, $"auto-{index}");
            File.SetLastWriteTimeUtc(path, baseTime.AddMinutes(index));
        }

        var critical = Path.Combine(profile.PhysicalPath, "crucialdecision3.whs");
        await File.WriteAllTextAsync(critical, "critical");
        File.SetLastWriteTimeUtc(critical, baseTime);

        var nodes = await harness.Service.ReconcileBackupNodesAsync(profile.Id);

        Assert.Equal(11, nodes.Count);
        Assert.Contains(nodes, node => node.FileName == "crucialdecision3.whs" && node.IsImportant);
        Assert.DoesNotContain(nodes, node => node.FileName == "autosave1.whs");
        Assert.Contains(nodes, node => node.FileName == "autosave15.whs");
    }

    [Fact]
    public async Task BackupLibrary_PrunesOnlyOrdinaryNodesAndKeepsAutomaticImportantNodes()
    {
        using var harness = new Harness(backupRetention: 2);
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Node retention", "profile");
        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "autosave1.whs"), "auto-1");
        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "autosave2.whs"), "auto-2");
        await harness.Service.ReconcileBackupNodesAsync(profile.Id);

        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "autosave3.whs"), "auto-3");
        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "save4.whs"), "manual-4");
        var nodes = await harness.Service.ReconcileBackupNodesAsync(profile.Id);

        Assert.Equal(3, nodes.Count);
        Assert.Equal(2, nodes.Count(node => !node.IsImportant));
        Assert.Contains(nodes, node => node.FileName == "save4.whs" && node.IsImportant);
        Assert.DoesNotContain(nodes, node => node.FileName == "autosave1.whs");
    }

    [Fact]
    public async Task BackupNodeRestore_IsNonDestructiveAndProtectsConflictingContent()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Node restore", "profile");
        var source = Path.Combine(profile.PhysicalPath, "autosave1.whs");
        var unrelated = Path.Combine(profile.PhysicalPath, "autosave2.whs");
        await File.WriteAllTextAsync(source, "old progress");
        await File.WriteAllTextAsync(unrelated, "other progress");
        var original = (await harness.Service.ReconcileBackupNodesAsync(profile.Id))
            .Single(node => node.FileName == "autosave1.whs");

        var validation = await harness.Service.ValidateBackupNodeAsync(original.Id);
        Assert.True(validation.CanRestore);
        Assert.Equal(original.ContentSha256, validation.VerifiedFingerprint);

        await File.WriteAllTextAsync(source, "current progress");
        await harness.Service.RestoreBackupNodeAsync(original.Id);

        Assert.Equal("old progress", await File.ReadAllTextAsync(source));
        Assert.Equal("other progress", await File.ReadAllTextAsync(unrelated));
        var nodes = await harness.Service.GetBackupNodesAsync(profile.Id);
        Assert.Contains(nodes, node => node.ImportanceSource == SaveBackupImportanceSource.RestoreSafety);
    }

    [Fact]
    public async Task BackupLibrary_DuplicateContentDoesNotCreateAnotherNodeOrObject()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Node deduplication", "profile");
        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "autosave1.whs"), "identical");
        await harness.Service.ReconcileBackupNodesAsync(profile.Id);
        var objectRoot = Path.Combine(harness.Service.GetSnapshotsRootPath(), "objects", "v1");
        var objectCount = Directory.EnumerateFiles(objectRoot, "*.bxc", SearchOption.AllDirectories).Count();

        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "autosave2.whs"), "identical");
        var nodes = await harness.Service.ReconcileBackupNodesAsync(profile.Id);

        Assert.Single(nodes);
        Assert.Equal(objectCount, Directory.EnumerateFiles(objectRoot, "*.bxc", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task GameExitProtection_CreatesNodesWithoutCreatingAFullSnapshot()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Exit protection", "profile");
        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "exit.whs"), "exit progress");
        harness.Router.MountJunction(harness.Service.GetOfficialSavePath(), profile.PhysicalPath);

        var legacyResult = await harness.Service.CreateGameExitSnapshotAsync();

        Assert.Null(legacyResult);
        Assert.Single(await harness.Service.GetBackupNodesAsync(profile.Id));
        Assert.Empty(await harness.Service.GetSnapshotsAsync(profile.Id));
    }

    [Fact]
    public async Task BackupNodeRestore_CorruptObjectLeavesCurrentSaveUntouched()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Node integrity", "profile");
        var path = Path.Combine(profile.PhysicalPath, "autosave1.whs");
        await File.WriteAllTextAsync(path, "protected progress");
        var node = Assert.Single(await harness.Service.ReconcileBackupNodesAsync(profile.Id));
        var objectPath = Directory.EnumerateFiles(
            Path.Combine(harness.Service.GetSnapshotsRootPath(), "objects", "v1"), "*.bxc", SearchOption.AllDirectories).Single();
        await File.WriteAllBytesAsync(objectPath, [1, 2, 3, 4]);
        await File.WriteAllTextAsync(path, "current progress");

        var validation = await harness.Service.ValidateBackupNodeAsync(node.Id);
        Assert.False(validation.CanRestore);

        await Assert.ThrowsAnyAsync<Exception>(() => harness.Service.RestoreBackupNodeAsync(node.Id));

        Assert.Equal("current progress", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task FullSnapshotConversion_KeepsRecommendedNodesThenDeletesSnapshot()
    {
        using var harness = new Harness(backupRetention: 2);
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Convert", "profile");
        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "autosave1.whs"), "auto-1");
        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "autosave2.whs"), "auto-2");
        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "autosave3.whs"), "auto-3");
        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "crucialdecision1.whs"), "critical");
        var snapshot = await harness.Service.CreateSnapshotAsync(profile.Id, SaveSnapshotTrigger.Manual);
        var preview = await harness.Service.PreviewFullSnapshotConversionAsync(snapshot!.Id);
        var selected = preview.Files.Where(file => file.IsRecommended).Select(file => file.RelativePath).ToList();

        var nodes = await harness.Service.ConvertFullSnapshotToNodesAsync(snapshot.Id, selected);

        Assert.Empty(await harness.Service.GetSnapshotsAsync(profile.Id));
        Assert.Equal(3, nodes.Count);
        Assert.Contains(nodes, node => node.FileName == "crucialdecision1.whs" && node.IsImportant);
        Assert.Equal(2, nodes.Count(node => !node.IsImportant));
    }

    [Fact]
    public async Task BackupNode_UserUnmarkIsNotReappliedByAutomaticRule()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Importance override", "profile");
        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "save1.whs"), "manual");
        var node = Assert.Single(await harness.Service.ReconcileBackupNodesAsync(profile.Id));
        Assert.True(node.IsImportant);

        await harness.Service.SetBackupNodeImportanceAsync(node.Id, false);
        await harness.Service.ReconcileBackupNodesAsync(profile.Id);

        Assert.False(Assert.Single(await harness.Service.GetBackupNodesAsync(profile.Id)).IsImportant);
    }

    [Fact]
    public async Task DeleteProfile_RemovesNodeManifestsAndUnreferencedObjects()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Delete nodes", "profile");
        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "autosave1.whs"), "node data");
        await harness.Service.ReconcileBackupNodesAsync(profile.Id);

        await harness.Service.DeleteProfileAsync(profile.Id);

        Assert.Empty(await harness.Service.GetBackupNodesAsync(profile.Id));
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(harness.Service.GetSnapshotsRootPath(), "objects", "v1"), "*.bxc", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(
            harness.Service.GetSnapshotsRootPath(), "manifests", "nodes", profile.Id.ToString("D"))));
    }

    [Fact]
    public async Task ImportExistingBackupNode_RejectsPathOutsideProfile()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Path safety", "profile");
        var outside = Path.Combine(harness.Root, "outside.whs");
        await File.WriteAllTextAsync(outside, "outside");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            harness.Service.ImportExistingBackupNodesAsync(profile.Id, [outside]));

        Assert.Empty(await harness.Service.GetBackupNodesAsync(profile.Id));
    }

    [Fact]
    public async Task BackupNodePackageV1_RoundTripsAsARecoverableProfile()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Node package", "profile");
        var source = Path.Combine(profile.PhysicalPath, "playline1", "save1.whs");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "portable node");
        var node = Assert.Single(await harness.Service.ReconcileBackupNodesAsync(profile.Id));
        var package = Path.Combine(harness.Root, "node.bohemix-save.zip");

        await harness.Service.ExportBackupNodeAsync(node.Id, package);
        var imported = await harness.Service.ImportPackageAsync(package);

        Assert.Equal("portable node", await File.ReadAllTextAsync(Path.Combine(imported.PhysicalPath, "playline1", "save1.whs")));
    }

    [Fact]
    public async Task Retention_PrunesOnlyAutomaticSnapshotsAndKeepsManualSnapshot()
    {
        using var harness = new Harness(retention: 2);
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Retention", "save");

        await harness.Service.CreateSnapshotAsync(profile.Id, SaveSnapshotTrigger.Manual);
        await harness.Service.CreateSnapshotAsync(profile.Id, SaveSnapshotTrigger.BeforeRestore);
        await harness.Service.CreateSnapshotAsync(profile.Id, SaveSnapshotTrigger.BeforeRestore);
        await harness.Service.CreateSnapshotAsync(profile.Id, SaveSnapshotTrigger.BeforeRestore);

        var snapshots = await harness.Service.GetSnapshotsAsync(profile.Id);
        Assert.Equal(3, snapshots.Count);
        Assert.Single(snapshots, item => item.Trigger == SaveSnapshotTrigger.Manual);
        Assert.Equal(2, snapshots.Count(item => item.Trigger != SaveSnapshotTrigger.Manual));
    }

    [Fact]
    public async Task Restore_CreatesSafetySnapshotBeforeReplacingProfile()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Restore", "original");
        var snapshot = await harness.Service.CreateSnapshotAsync(profile.Id, SaveSnapshotTrigger.Manual);
        Assert.NotNull(snapshot);
        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "save.bin"), "new progress");

        await harness.Service.RestoreSnapshotAsync(snapshot!.Id);

        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(profile.PhysicalPath, "save.bin")));
        var snapshots = await harness.Service.GetSnapshotsAsync(profile.Id);
        Assert.Contains(snapshots, item => item.Trigger == SaveSnapshotTrigger.BeforeRestore);
        var safety = snapshots.Single(item => item.Trigger == SaveSnapshotTrigger.BeforeRestore);
        await harness.Service.RestoreSnapshotAsync(safety.Id);
        Assert.Equal("new progress", await File.ReadAllTextAsync(Path.Combine(profile.PhysicalPath, "save.bin")));
    }

    [Fact]
    public async Task Snapshot_UsesChunkedManifestAndReusesObjectsForIdenticalManualSnapshots()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Deduplicated", new string('A', 2 * 1024 * 1024));

        var first = await harness.Service.CreateSnapshotAsync(profile.Id, SaveSnapshotTrigger.Manual);
        var objectRoot = Path.Combine(harness.Service.GetSnapshotsRootPath(), "objects", "v1");
        var objectsAfterFirst = Directory.EnumerateFiles(objectRoot, "*.bxc", SearchOption.AllDirectories).ToList();
        var second = await harness.Service.CreateSnapshotAsync(profile.Id, SaveSnapshotTrigger.Manual);
        var objectsAfterSecond = Directory.EnumerateFiles(objectRoot, "*.bxc", SearchOption.AllDirectories).ToList();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(SaveSnapshotStorageKind.ChunkedManifest, first!.StorageKind);
        Assert.True(File.Exists(first.PhysicalPath));
        Assert.Equal(objectsAfterFirst.Count, objectsAfterSecond.Count);
        var stats = await harness.Service.GetSnapshotStorageStatsAsync();
        Assert.True(stats.PhysicalBytes < stats.LogicalBytes);
    }

    [Fact]
    public async Task DeleteSnapshot_KeepsSharedObjectsUntilLastReferenceIsDeleted()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Shared chunks", "first");
        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "shared.bin"), new string('S', 600_000));
        var first = await harness.Service.CreateSnapshotAsync(profile.Id, SaveSnapshotTrigger.Manual);
        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "save.bin"), "second");
        var second = await harness.Service.CreateSnapshotAsync(profile.Id, SaveSnapshotTrigger.Manual);
        var objectRoot = Path.Combine(harness.Service.GetSnapshotsRootPath(), "objects", "v1");

        await harness.Service.DeleteSnapshotAsync(first!.Id);
        Assert.NotEmpty(Directory.EnumerateFiles(objectRoot, "*.bxc", SearchOption.AllDirectories));
        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "shared.bin"), "changed");
        await harness.Service.RestoreSnapshotAsync(second!.Id);
        Assert.Equal(600_000, new FileInfo(Path.Combine(profile.PhysicalPath, "shared.bin")).Length);

        var snapshots = await harness.Service.GetSnapshotsAsync(profile.Id);
        foreach (var snapshot in snapshots)
        {
            await harness.Service.DeleteSnapshotAsync(snapshot.Id);
        }
        Assert.Empty(Directory.EnumerateFiles(objectRoot, "*.bxc", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Restore_CorruptObjectAbortsBeforeReplacingCurrentProfile()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Integrity", "snapshot content");
        var snapshot = await harness.Service.CreateSnapshotAsync(profile.Id, SaveSnapshotTrigger.Manual);
        var objectPath = Directory.EnumerateFiles(
            Path.Combine(harness.Service.GetSnapshotsRootPath(), "objects", "v1"), "*.bxc", SearchOption.AllDirectories).Single();
        await File.WriteAllBytesAsync(objectPath, [1, 2, 3, 4]);
        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "save.bin"), "current content");

        var validation = await harness.Service.ValidateSnapshotAsync(snapshot!.Id);
        Assert.False(validation.CanRestore);
        Assert.Contains(validation.Issues, issue => issue.IsBlocking);

        await Assert.ThrowsAnyAsync<Exception>(() => harness.Service.RestoreSnapshotAsync(snapshot.Id));

        Assert.Equal("current content", await File.ReadAllTextAsync(Path.Combine(profile.PhysicalPath, "save.bin")));
        Assert.DoesNotContain(
            await harness.Service.GetSnapshotsAsync(profile.Id),
            item => item.Trigger == SaveSnapshotTrigger.BeforeRestore);
    }

    [Fact]
    public async Task RestoreValidation_VerifiesSnapshotWithoutChangingProfile()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Validation", "snapshot content");
        var snapshot = await harness.Service.CreateSnapshotAsync(profile.Id, SaveSnapshotTrigger.Manual);
        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "save.bin"), "current content");

        var validation = await harness.Service.ValidateSnapshotAsync(snapshot!.Id);

        Assert.True(validation.CanRestore);
        Assert.Equal(snapshot.ContentFingerprint, validation.VerifiedFingerprint);
        Assert.Equal(1, validation.FileCount);
        Assert.Equal("current content", await File.ReadAllTextAsync(Path.Combine(profile.PhysicalPath, "save.bin")));
    }

    [Fact]
    public async Task LegacySnapshot_MigratesAfterFullFingerprintVerification()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Legacy", "profile");
        var legacyId = await harness.AddLegacySnapshotAsync(profile.Id, "legacy snapshot");

        var migrated = await harness.Service.MigrateOneLegacySnapshotAsync(CancellationToken.None, enforceFreeSpace: false);
        var snapshot = (await harness.Service.GetSnapshotsAsync(profile.Id)).Single(item => item.Id == legacyId);

        Assert.True(migrated);
        Assert.Equal(SaveSnapshotStorageKind.ChunkedManifest, snapshot.StorageKind);
        Assert.True(File.Exists(snapshot.PhysicalPath));
        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "save.bin"), "changed");
        await harness.Service.RestoreSnapshotAsync(snapshot.Id);
        Assert.Equal("legacy snapshot", await File.ReadAllTextAsync(Path.Combine(profile.PhysicalPath, "save.bin")));
    }

    [Fact]
    public async Task PackageV1_RoundTripsChunkedSnapshotHistory()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Package history", "historical content");
        await harness.Service.CreateSnapshotAsync(profile.Id, SaveSnapshotTrigger.Manual, "checkpoint");
        await File.WriteAllTextAsync(Path.Combine(profile.PhysicalPath, "save.bin"), "current content");
        var package = Path.Combine(harness.Root, "history.bohemix-save.zip");

        await harness.Service.ExportProfileAsync(profile.Id, package);
        var imported = await harness.Service.ImportPackageAsync(package);
        var importedSnapshot = Assert.Single(await harness.Service.GetSnapshotsAsync(imported.Id));
        await harness.Service.RestoreSnapshotAsync(importedSnapshot.Id);

        Assert.Equal("historical content", await File.ReadAllTextAsync(Path.Combine(imported.PhysicalPath, "save.bin")));
        Assert.Equal(SaveSnapshotStorageKind.ChunkedManifest, importedSnapshot.StorageKind);
    }

    [Fact]
    public async Task FastCdc_MiddleInsertionAddsLessThanFivePercentForLargeIncompressibleFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX-ChunkingTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var snapshots = Path.Combine(root, "snapshots");
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(source);
        try
        {
            const int originalLength = 64 * 1024 * 1024;
            var original = new byte[originalLength];
            new Random(0xB0E).NextBytes(original);
            var filePath = Path.Combine(source, "save.whs");
            await File.WriteAllBytesAsync(filePath, original);
            var store = new ChunkedSnapshotStore(snapshots, staging);
            var profileId = Guid.NewGuid();
            await store.CaptureAsync(source, profileId, Guid.NewGuid(), null, CancellationToken.None);
            var objectRoot = Path.Combine(snapshots, "objects", "v1");
            var firstBytes = Directory.EnumerateFiles(objectRoot, "*.bxc", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);

            var inserted = new byte[originalLength + 1];
            var insertionPoint = originalLength / 2;
            Buffer.BlockCopy(original, 0, inserted, 0, insertionPoint);
            inserted[insertionPoint] = 0x5A;
            Buffer.BlockCopy(original, insertionPoint, inserted, insertionPoint + 1, originalLength - insertionPoint);
            await File.WriteAllBytesAsync(filePath, inserted);
            await store.CaptureAsync(source, profileId, Guid.NewGuid(), null, CancellationToken.None);
            var secondBytes = Directory.EnumerateFiles(objectRoot, "*.bxc", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);

            Assert.True(secondBytes - firstBytes < originalLength * 0.05,
                $"Insertion added {secondBytes - firstBytes:N0} bytes for a {originalLength:N0}-byte file.");
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
    public async Task ChunkManifest_PathTraversalIsRejectedBeforeMaterialization()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX-ManifestTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var destination = Path.Combine(root, "restore");
        var snapshots = Path.Combine(root, "snapshots");
        Directory.CreateDirectory(source);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "save.bin"), "safe");
            var store = new ChunkedSnapshotStore(snapshots, Path.Combine(root, "staging"));
            var capture = await store.CaptureAsync(source, Guid.NewGuid(), Guid.NewGuid(), null, CancellationToken.None);
            var manifestPath = store.GetManifestFullPath(capture.ManifestRelativePath);
            var json = await File.ReadAllTextAsync(manifestPath);
            await File.WriteAllTextAsync(manifestPath, json.Replace("save.bin", "../escape.bin", StringComparison.Ordinal));

            await Assert.ThrowsAsync<InvalidDataException>(() => store.MaterializeAsync(
                capture.ManifestRelativePath, destination, null, "Restoring", CancellationToken.None));

            Assert.False(File.Exists(Path.Combine(root, "escape.bin")));
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
    public async Task StartupReconciliation_RemovesOrphanManifestAndObjects()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX-ReconcileTests", Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        var snapshots = Path.Combine(root, "snapshots");
        Directory.CreateDirectory(source);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(source, "save.bin"), "orphan");
            var store = new ChunkedSnapshotStore(snapshots, Path.Combine(root, "staging"));
            var capture = await store.CaptureAsync(source, Guid.NewGuid(), Guid.NewGuid(), null, CancellationToken.None);
            Assert.True(File.Exists(store.GetManifestFullPath(capture.ManifestRelativePath)));

            await store.ReconcileOrphanManifestsAsync([], CancellationToken.None);

            Assert.False(File.Exists(store.GetManifestFullPath(capture.ManifestRelativePath)));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(snapshots, "objects", "v1"), "*.bxc", SearchOption.AllDirectories));
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
    public async Task DeleteProfile_BlocksActiveProfile()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var profile = await harness.AddProfileAsync("Active", "save");
        harness.Router.TargetPath = profile.PhysicalPath;

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.DeleteProfileAsync(profile.Id));

        Assert.True(Directory.Exists(profile.PhysicalPath));
        Assert.Single(await harness.Service.GetProfilesAsync());
    }

    [Fact]
    public async Task PackageImport_GeneratesNewIdsAndSuffixesDuplicateNames()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var source = await harness.AddProfileAsync("Campaign", "save package");
        var package = Path.Combine(harness.Root, "campaign.bohemix-save.zip");
        await harness.Service.ExportProfileAsync(source.Id, package);

        var first = await harness.Service.ImportPackageAsync(package);
        var second = await harness.Service.ImportPackageAsync(package);

        Assert.Equal("Campaign (2)", first.DisplayName);
        Assert.Equal("Campaign (3)", second.DisplayName);
        Assert.NotEqual(source.Id, first.Id);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.PhysicalPath, second.PhysicalPath);
    }

    [Fact]
    public async Task PackageImport_RejectsPathTraversalBeforeExtraction()
    {
        using var harness = new Harness();
        await harness.Service.InitializeAsync();
        var package = Path.Combine(harness.Root, "unsafe.bohemix-save.zip");
        var content = Encoding.UTF8.GetBytes("bad");
        var hash = Convert.ToHexString(SHA256.HashData(content));
        var manifest = $$"""
            {
              "SchemaVersion": 1,
              "Profile": { "DisplayName": "Unsafe", "IsFavorite": false, "CreatedAtUtc": "2026-01-01T00:00:00Z" },
              "Snapshots": [],
              "Files": [ { "Path": "../escape.bin", "Sha256": "{{hash}}", "Length": 3 } ]
            }
            """;
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            var manifestEntry = archive.CreateEntry("manifest.json");
            await using (var writer = new StreamWriter(manifestEntry.Open()))
            {
                await writer.WriteAsync(manifest);
            }

            var payload = archive.CreateEntry("../escape.bin");
            await using var stream = payload.Open();
            await stream.WriteAsync(content);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => harness.Service.ImportPackageAsync(package));
        Assert.False(File.Exists(Path.Combine(harness.Root, "saves", "escape.bin")));
    }

    [Fact]
    public async Task HiddenSaveManagerRow_MigratesWithoutDeletingSource()
    {
        using var harness = new Harness();
        var id = Guid.NewGuid();
        var source = Path.Combine(harness.Root, "old-module", id.ToString("N"));
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "save.bin"), "legacy data");
        await harness.CreateHiddenModuleRowAsync(id, "Hidden profile", source);

        await harness.Service.InitializeAsync();

        var profile = Assert.Single(await harness.Service.GetProfilesAsync());
        Assert.Equal(id, profile.Id);
        Assert.Equal("legacy data", await File.ReadAllTextAsync(Path.Combine(profile.PhysicalPath, "save.bin")));
        Assert.True(File.Exists(Path.Combine(source, "save.bin")));
        Assert.Empty(harness.Service.MigrationWarnings);
    }

    private sealed class Harness : IDisposable
    {
        private readonly ApplicationPathService pathService;

        public Harness(int retention = 20, int backupRetention = 10)
        {
            Root = Path.Combine(Path.GetTempPath(), "BohemiX-SaveProfileTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            var official = Path.Combine(Root, "official");
            Directory.CreateDirectory(official);
            pathService = new ApplicationPathService(Root);
            Router = new FakeJunctionRouter();
            var settings = new AppSettings(
                Root,
                null,
                Path.Combine(Root, "mods"),
                OfficialSavePath: official,
                AutoSnapshotRetention: retention,
                BackupNodeRetention: backupRetention);
            Service = new SaveProfileService(
                pathService,
                new FakeSettingsService(settings),
                new SqliteConnectionFactory(pathService),
                new UnusedSlotManagerFactory(),
                new SafeCoordinatorFactory(),
                Router,
                new SaveParser(),
                new LoggerConfiguration().CreateLogger());
        }

        public string Root { get; }
        public SaveProfileService Service { get; }
        public FakeJunctionRouter Router { get; }

        public async Task WriteCharacterEventAsync(DateTimeOffset occurredAtUtc, int level, int groschen, string eventType = "GAME_SAVED")
        {
            var bridgePath = pathService.GetPaths().TrackerBridgeEventsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(bridgePath)!);
            await File.AppendAllTextAsync(
                bridgePath,
                $$"""{"event_id":"character-{{Guid.NewGuid():N}}","session_id":"test-session","type":"{{eventType}}","timestamp":"{{occurredAtUtc:O}}","entity_id":"save","entity_name":"Game saved","henry_level":{{level}},"groschen":{{groschen}}}"""
                + Environment.NewLine);
        }

        public Task WriteGameLogCharacterEventAsync(DateTimeOffset occurredAtUtc, int level, int groschen)
        {
            return File.AppendAllTextAsync(
                Path.Combine(Root, "kcd.log"),
                $"[BohemiXTrackerEvent] {{\"event_id\":\"character-{Guid.NewGuid():N}\",\"session_id\":\"test-session\",\"type\":\"CHARACTER_SNAPSHOT\",\"timestamp\":\"{occurredAtUtc:O}\",\"henry_level\":{level},\"groschen\":{groschen}}}"
                + Environment.NewLine);
        }

        public async Task<SaveProfile> AddProfileAsync(string displayName, string content)
        {
            var id = Guid.NewGuid();
            var physicalName = $"Profile_{id:N}";
            var path = Path.Combine(Root, "saves", "vault", physicalName);
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "save.bin"), content);
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO SaveSlots
                (Id, PhysicalName, DisplayName, CreatedAtUtc, UpdatedAtUtc, IsFavorite)
                VALUES ($id, $physicalName, $displayName, $now, $now, 0);
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$physicalName", physicalName);
            command.Parameters.AddWithValue("$displayName", displayName);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
            return (await Service.GetProfilesAsync()).Single(item => item.Id == id);
        }

        public async Task<Guid> AddLegacySnapshotAsync(Guid profileId, string content)
        {
            var id = Guid.NewGuid();
            var path = Path.Combine(Root, "saves", "snapshots", profileId.ToString("D"), id.ToString("D"));
            Directory.CreateDirectory(path);
            var filePath = Path.Combine(path, "save.bin");
            await File.WriteAllTextAsync(filePath, content);
            var fileHash = SHA256.HashData(await File.ReadAllBytesAsync(filePath));
            using var directoryHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            directoryHash.AppendData(Encoding.UTF8.GetBytes("SAVE.BIN"));
            directoryHash.AppendData([0]);
            directoryHash.AppendData(fileHash);
            var fingerprint = Convert.ToHexString(directoryHash.GetHashAndReset());

            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO SaveSnapshots
                (Id, ProfileId, PhysicalName, Note, Trigger, CreatedAtUtc, ContentFingerprint, FileCount, TotalBytes,
                 StorageFormat)
                VALUES ($id, $profileId, $physicalName, NULL, 'Manual', $createdAtUtc, $fingerprint, 1, $totalBytes,
                        'LegacyDirectory');
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$profileId", profileId.ToString("D"));
            command.Parameters.AddWithValue("$physicalName", id.ToString("D"));
            command.Parameters.AddWithValue("$createdAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            command.Parameters.AddWithValue("$totalBytes", new FileInfo(filePath).Length);
            await command.ExecuteNonQueryAsync();
            return id;
        }

        public async Task CreateLegacyProfileBeforeInitializationAsync(Guid id, string displayName, string physicalName)
        {
            var path = Path.Combine(Root, "saves", "vault", physicalName);
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, "save.bin"), "legacy");
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE SaveSlots (
                    Id TEXT PRIMARY KEY NOT NULL, PhysicalName TEXT NOT NULL UNIQUE, DisplayName TEXT NOT NULL,
                    CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL, GameSaveName TEXT NULL,
                    PlayTimeSeconds INTEGER NULL, LastSavedAtUtc TEXT NULL, HenryLevel INTEGER NULL,
                    CurrentLocation TEXT NULL, ActiveQuest TEXT NULL, GroschenCount INTEGER NULL,
                    PlayerStatusEffects TEXT NULL, SaveType TEXT NULL, GameVersion TEXT NULL, ThumbnailPath TEXT NULL
                );
                INSERT INTO SaveSlots (Id, PhysicalName, DisplayName, CreatedAtUtc, UpdatedAtUtc)
                VALUES ($id, $physicalName, $displayName, $now, $now);
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$physicalName", physicalName);
            command.Parameters.AddWithValue("$displayName", displayName);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        public async Task CreateHiddenModuleRowAsync(Guid id, string displayName, string source)
        {
            await using var connection = OpenConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE KCD2_Saves (
                    Id TEXT PRIMARY KEY, DisplayName TEXT NOT NULL, PhysicalPath TEXT NOT NULL,
                    CreatedTime INTEGER NOT NULL, IsFavorite INTEGER NOT NULL DEFAULT 0,
                    GameTime TEXT NULL, Level INTEGER NULL, IsDeleted INTEGER NOT NULL DEFAULT 0, DeletedTime INTEGER NULL
                );
                INSERT INTO KCD2_Saves (Id, DisplayName, PhysicalPath, CreatedTime, IsFavorite, IsDeleted)
                VALUES ($id, $displayName, $source, $created, 1, 0);
                """;
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$displayName", displayName);
            command.Parameters.AddWithValue("$source", source);
            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync();
        }

        public SqliteConnection OpenConnection()
        {
            var path = pathService.GetPaths().DatabasePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            Service.Dispose();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class FakeSettingsService(AppSettings settings) : IAppSettingsService
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);
        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SafeCoordinatorFactory : IProcessCoordinatorFactory
    {
        public IProcessCoordinator Create(string officialSavePath) => new SafeCoordinator();
    }

    private sealed class SafeCoordinator : IProcessCoordinator
    {
        public Task<bool> IsSafeToSwitch(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeJunctionRouter : IJunctionRouter
    {
        public string? TargetPath { get; set; }

        public void MountJunction(string linkPath, string targetPath) => TargetPath = targetPath;
        public void UnmountJunction(string linkPath) => TargetPath = null;
        public JunctionInfo? TryGetJunctionInfo(string linkPath) =>
            TargetPath is null ? null : new JunctionInfo(linkPath, TargetPath, TargetPath, TargetPath);
    }

    private sealed class UnusedSlotManagerFactory : ISlotManagerFactory
    {
        public ISlotManager Create(string officialSavePath, string? vaultRoot = null, string? databasePath = null) =>
            throw new NotSupportedException();
    }
}
