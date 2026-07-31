using System.IO.Compression;
using BohemiX.Core.Models;
using BohemiX.Core.PlayerProfiles;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Services;
using Serilog;
using SharpCompress.Writers.SevenZip;

namespace BohemiX.Core.Tests;

public sealed class ModPackImportServiceTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "bohemix-mod-pack-import-tests", Guid.NewGuid().ToString("N"));

    public ModPackImportServiceTests()
    {
        Directory.CreateDirectory(tempRoot);
    }

    [Fact]
    public async Task PrepareAndImport_RecognizesWrappedModsAndAppliesAuthorOrder()
    {
        var packagePath = Path.Combine(tempRoot, "creator-pack.zip");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            AddEntry(archive, "Creator Pack/Mods/First/mod.manifest", Manifest("first", "1.0", "Author"));
            AddEntry(archive, "Creator Pack/Mods/First/Data/first.pak", "first");
            AddEntry(archive, "Creator Pack/Mods/Second/mod.manifest", Manifest("second", "2.0", "Author"));
            AddEntry(archive, "Creator Pack/Mods/Second/Data/second.pak", "second");
            AddEntry(archive, "Creator Pack/mod_order.txt", "second\nfirst\n");
            AddEntry(archive, "Creator Pack/README.txt", "ignored");
        }

        var catalog = new FakeCatalog([]);
        var installer = new FakePackageInstaller();
        var service = new ModPackImportService(
            new TestPathService(tempRoot),
            catalog,
            installer,
            Log.Logger);

        var prepared = await service.PrepareAsync(packagePath);

        Assert.Equal(ModPackImportPrepareStatus.Ready, prepared.Status);
        Assert.NotNull(prepared.Plan);
        var plan = prepared.Plan!;
        Assert.Equal(2, plan.Items.Count(item => item.State == ModPackImportItemState.New));
        Assert.Contains(plan.Warnings, warning => warning.Contains("Ignored", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["second", "first"], plan.AuthorLoadOrder);

        var result = await service.ImportAsync(
            plan.SessionId,
            plan.Items.Select(item => new ModPackImportSelection(item.Id, ModPackImportAction.Install)).ToArray(),
            applyAuthorLoadOrder: true);

        Assert.Equal(2, result.InstalledCount);
        Assert.Equal(["second", "first"], catalog.SavedLoadOrder);
        Assert.All(installer.Requests, request => Assert.Equal("LocalArchive", request.Source?.Platform));
    }

    [Fact]
    public async Task Prepare_RejectsPathTraversal()
    {
        var packagePath = Path.Combine(tempRoot, "unsafe.zip");
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            AddEntry(archive, "../escape/mod.manifest", Manifest("escape", "1", "bad"));
        }

        var service = new ModPackImportService(
            new TestPathService(tempRoot),
            new FakeCatalog([]),
            new FakePackageInstaller(),
            Log.Logger);

        var prepared = await service.PrepareAsync(packagePath);

        Assert.Equal(ModPackImportPrepareStatus.InvalidArchive, prepared.Status);
        Assert.Null(prepared.Plan);
    }

    [Fact]
    public async Task PrepareAndImport_RecognizesRealSevenZipArchive()
    {
        var packagePath = Path.Combine(tempRoot, "creator-pack.7z");
        await using (var archiveStream = File.Create(packagePath))
        using (var writer = new SevenZipWriter(archiveStream, new SevenZipWriterOptions()))
        {
            WriteEntry(writer, "Creator Pack/Mods/Seven Zip Mod/mod.manifest", Manifest("seven-zip-mod", "3.0", "Author"));
            WriteEntry(writer, "Creator Pack/Mods/Seven Zip Mod/Data/content.pak", "payload");
        }

        var installer = new FakePackageInstaller();
        var service = new ModPackImportService(
            new TestPathService(tempRoot),
            new FakeCatalog([]),
            installer,
            Log.Logger);

        var prepared = await service.PrepareAsync(packagePath);

        Assert.True(
            prepared.Status == ModPackImportPrepareStatus.Ready,
            $"Expected a ready 7Z plan, but got {prepared.Status}: {prepared.Message}");
        var item = Assert.Single(prepared.Plan!.Items.Where(candidate => candidate.State == ModPackImportItemState.New));
        Assert.Equal("seven-zip-mod", item.Id);

        var result = await service.ImportAsync(
            prepared.Plan.SessionId,
            [new ModPackImportSelection(item.Id, ModPackImportAction.Install)],
            applyAuthorLoadOrder: false);

        Assert.Equal(1, result.InstalledCount);
        Assert.Single(installer.Requests);
    }

    [Fact]
    public async Task Prepare_ExpandsOneNestedModArchive()
    {
        var packagePath = Path.Combine(tempRoot, "nested-pack.zip");
        await using var nestedStream = new MemoryStream();
        using (var nestedArchive = new ZipArchive(nestedStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(nestedArchive, "Nested Mod/mod.manifest", Manifest("nested-mod", "1.0", "Author"));
            AddEntry(nestedArchive, "Nested Mod/Data/content.pak", "payload");
        }

        nestedStream.Position = 0;
        using (var outerArchive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            var nestedEntry = outerArchive.CreateEntry("Creator Pack/Mods/nested-mod.zip");
            await using var entryStream = nestedEntry.Open();
            await nestedStream.CopyToAsync(entryStream);
        }

        var service = new ModPackImportService(
            new TestPathService(tempRoot),
            new FakeCatalog([]),
            new FakePackageInstaller(),
            Log.Logger);

        var prepared = await service.PrepareAsync(packagePath);

        Assert.Equal(ModPackImportPrepareStatus.Ready, prepared.Status);
        var item = Assert.Single(prepared.Plan!.Items.Where(candidate => candidate.State == ModPackImportItemState.New));
        Assert.Equal("nested-mod", item.Id);
        Assert.Contains(prepared.Plan.Warnings, warning => warning.Contains("nested mod archive", StringComparison.OrdinalIgnoreCase));

        await service.DiscardAsync(prepared.Plan.SessionId);
    }

    private static string Manifest(string name, string version, string author) =>
        $"<kcd_mod><info><name>{name}</name><version>{version}</version><author>{author}</author></info></kcd_mod>";

    private static void AddEntry(ZipArchive archive, string path, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(path).Open());
        writer.Write(content);
    }

    private static void WriteEntry(SevenZipWriter writer, string path, string content)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        writer.Write(path, stream, DateTime.UtcNow);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class FakePackageInstaller : IModPackageInstaller
    {
        public List<PreparedModPackageInstallRequest> Requests { get; } = [];

        public Task<ModPackageInstallResult> InstallAsync(ModPackageInstallRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ModPackageInstallResult> InstallPreparedDirectoryAsync(PreparedModPackageInstallRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(new ModPackageInstallResult(true, request.PayloadDirectory, $"Installed {request.DisplayName}."));
        }
    }

    private sealed class FakeCatalog(IReadOnlyList<ModManifest> installed) : IModCatalogService
    {
        public IReadOnlyList<string> SavedLoadOrder { get; private set; } = [];

        public Task<IReadOnlyList<ModManifest>> LoadInstalledModsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(installed);

        public Task SaveLoadOrderAsync(IReadOnlyList<string> orderedModIds, CancellationToken cancellationToken = default)
        {
            SavedLoadOrder = orderedModIds;
            return Task.CompletedTask;
        }

        public Task SaveModEnabledStateAsync(string modId, bool isEnabled, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveModEnabledStatesAsync(IReadOnlyDictionary<string, bool> enabledStates, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateModMetadataAsync(ModManifest mod, string displayName, string version, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestPathService(string root) : IApplicationPathService
    {
        public ApplicationPaths GetPaths() => new(
            root,
            Path.Combine(root, "bohemix.db"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "Mods"),
            Path.Combine(root, "native"),
            Path.Combine(root, "tracker"),
            Path.Combine(root, "tracker", "events.jsonl"),
            Path.Combine(root, "tracker", "rules.json"),
            CacheDirectory: Path.Combine(root, "cache"));

        public GlobalApplicationPaths GetGlobalPaths() => throw new NotSupportedException();
    }
}
