using BohemiX.Core.Models;
using BohemiX.Core.Services;
using BohemiX.Infrastructure.Services;
using Serilog;

var arguments = ProbeArguments.Parse(args);
if (arguments.ShowHelp)
{
    PrintUsage();
    return 0;
}

var probeDataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "BohemiX",
    "workshop-probe");

Directory.CreateDirectory(probeDataDirectory);

using var logger = new LoggerConfiguration().CreateLogger();
using var workshopService = new WorkshopService(
    new ApplicationPathService(probeDataDirectory),
    new WorkshopOptions(),
    logger);

try
{
    await workshopService.InitializeAsync();
    Console.WriteLine("InitializeAsync=OK");

    if (arguments.DetailPublishedFileId is not null)
    {
        await PrintDetailsAsync(workshopService, arguments.DetailPublishedFileId.Value);
    }
    else
    {
        var first = await PrintSearchAsync(workshopService, arguments);
        if (first is not null)
        {
            await PrintDetailsAsync(workshopService, first.PublishedFileId);
        }
    }

    if (arguments.InstallPublishedFileId is not null)
    {
        if (string.IsNullOrWhiteSpace(arguments.ModsDirectory))
        {
            Console.Error.WriteLine("Install requires --mods-dir <path>.");
            return 3;
        }

        await InstallAsync(workshopService, arguments);
    }
}
catch (WorkshopException ex)
{
    Console.Error.WriteLine($"WorkshopException.Kind={ex.FailureKind}");
    Console.Error.WriteLine($"WorkshopException.Message={ex.Message}");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

return 0;

static async Task<WorkshopModInfo?> PrintSearchAsync(IWorkshopService workshopService, ProbeArguments arguments)
{
    var search = await workshopService.SearchModsAsync(
        new WorkshopSearchRequest(
            arguments.Query,
            arguments.Page,
            arguments.PageSize,
            arguments.SortOrder));

    Console.WriteLine($"Search.Query={arguments.Query ?? "<none>"}");
    Console.WriteLine($"Search.Sort={arguments.SortOrder}");
    if (arguments.SortOrder == WorkshopSortOrder.Updated)
    {
        Console.WriteLine("Search.SortNote=Steam UGC has no Facepunch 2.3.3 last-updated query enum; this probe verifies the returned page sorted locally by TimeUpdated.");
    }

    Console.WriteLine($"Search.Page={search.Page}");
    Console.WriteLine($"Search.PageSize={search.PageSize}");
    Console.WriteLine($"Search.TotalCount={search.TotalCount}");
    Console.WriteLine($"Search.Returned={search.Mods.Count}");

    for (var index = 0; index < search.Mods.Count; index++)
    {
        var mod = search.Mods[index];
        Console.WriteLine(
            $"Search.Mod[{index}]={mod.PublishedFileId}|{mod.Name}|Deps={mod.DependencyPublishedFileIds.Count}|Thumb={(string.IsNullOrWhiteSpace(mod.ThumbnailUrl) ? "none" : "present")}");
    }

    return search.Mods.FirstOrDefault();
}

static async Task PrintDetailsAsync(IWorkshopService workshopService, ulong publishedFileId)
{
    var detail = await workshopService.GetModInfoAsync(publishedFileId);
    Console.WriteLine($"Detail.Id={detail.PublishedFileId}");
    Console.WriteLine($"Detail.Name={detail.Name}");
    Console.WriteLine($"Detail.Author={detail.Author}");
    Console.WriteLine($"Detail.SummaryLength={detail.Summary.Length}");
    Console.WriteLine($"Detail.FileSize={detail.FileSizeInBytes?.ToString() ?? "<unknown>"}");
    Console.WriteLine($"Detail.Subscriptions={detail.Subscriptions}");
    Console.WriteLine($"Detail.VotesUp={detail.VotesUp}");
    Console.WriteLine($"Detail.DependencyCount={detail.DependencyPublishedFileIds.Count}");
    Console.WriteLine($"Detail.Thumbnail={(string.IsNullOrWhiteSpace(detail.ThumbnailUrl) ? "none" : "present")}");

    foreach (var dependencyId in detail.DependencyPublishedFileIds)
    {
        Console.WriteLine($"Detail.Dependency={dependencyId}");
    }
}

static async Task InstallAsync(IWorkshopService workshopService, ProbeArguments arguments)
{
    var progress = new Progress<WorkshopInstallProgress>(progress =>
    {
        Console.WriteLine($"Install.Progress={progress.PublishedFileId}|{progress.Stage}|{progress.Message}");
    });

    var result = await workshopService.SubscribeAndInstallAsync(
        new WorkshopInstallRequest(
            arguments.InstallPublishedFileId!.Value,
            arguments.ModsDirectory!,
            arguments.DeployMode,
            arguments.IncludeDependencies),
        progress);

    Console.WriteLine($"Install.Success={result.Success}");
    Console.WriteLine($"Install.PublishedFileId={result.PublishedFileId}");
    Console.WriteLine($"Install.WorkshopContentPath={result.WorkshopContentPath ?? "<none>"}");
    Console.WriteLine($"Install.InstalledPath={result.InstalledPath ?? "<none>"}");
    Console.WriteLine($"Install.DependencyCount={result.InstalledDependencyPublishedFileIds.Count}");
    Console.WriteLine($"Install.Message={result.Message}");
}

static void PrintUsage()
{
    Console.WriteLine("BohemiX Workshop Probe");
    Console.WriteLine();
    Console.WriteLine("Default: initialize Steam, search hot Workshop items, and inspect the first result.");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --query <text>              Search text.");
    Console.WriteLine("  --sort <hot|updated>        Search sort order. Default: hot.");
    Console.WriteLine("  --page <n>                  Search page. Default: 1.");
    Console.WriteLine("  --page-size <n>             Search page size. Default: 5.");
    Console.WriteLine("  --details <publishedFileId> Inspect one Workshop item instead of default first result.");
    Console.WriteLine("  --install <publishedFileId> Subscribe, wait for Steam download, and deploy item.");
    Console.WriteLine("  --mods-dir <path>           Required with --install.");
    Console.WriteLine("  --deploy <copy|prefer-hardlink|hardlink>  Deploy mode. Default: copy.");
    Console.WriteLine("  --no-dependencies           Do not recursively install Workshop dependencies.");
    Console.WriteLine("  --help                      Show this help.");
}

internal sealed record ProbeArguments(
    string? Query,
    int Page,
    int PageSize,
    WorkshopSortOrder SortOrder,
    ulong? DetailPublishedFileId,
    ulong? InstallPublishedFileId,
    string? ModsDirectory,
    WorkshopDeployMode DeployMode,
    bool IncludeDependencies,
    bool ShowHelp)
{
    public static ProbeArguments Parse(string[] args)
    {
        var result = new ProbeArguments(
            Query: null,
            Page: 1,
            PageSize: 5,
            SortOrder: WorkshopSortOrder.Hot,
            DetailPublishedFileId: null,
            InstallPublishedFileId: null,
            ModsDirectory: null,
            DeployMode: WorkshopDeployMode.Copy,
            IncludeDependencies: true,
            ShowHelp: false);

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--help":
                case "-h":
                    result = result with { ShowHelp = true };
                    break;
                case "--query":
                    result = result with { Query = ReadValue(args, ref index, arg) };
                    break;
                case "--page":
                    result = result with { Page = Math.Max(1, int.Parse(ReadValue(args, ref index, arg))) };
                    break;
                case "--page-size":
                    result = result with { PageSize = Math.Clamp(int.Parse(ReadValue(args, ref index, arg)), 1, 50) };
                    break;
                case "--sort":
                    result = result with { SortOrder = ParseSortOrder(ReadValue(args, ref index, arg)) };
                    break;
                case "--details":
                    result = result with { DetailPublishedFileId = ulong.Parse(ReadValue(args, ref index, arg)) };
                    break;
                case "--install":
                    result = result with { InstallPublishedFileId = ulong.Parse(ReadValue(args, ref index, arg)) };
                    break;
                case "--mods-dir":
                    result = result with { ModsDirectory = ReadValue(args, ref index, arg) };
                    break;
                case "--deploy":
                    result = result with { DeployMode = ParseDeployMode(ReadValue(args, ref index, arg)) };
                    break;
                case "--no-dependencies":
                    result = result with { IncludeDependencies = false };
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {arg}");
            }
        }

        return result;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        index++;
        return args[index];
    }

    private static WorkshopSortOrder ParseSortOrder(string value)
    {
        return value.Equals("updated", StringComparison.OrdinalIgnoreCase)
            ? WorkshopSortOrder.Updated
            : WorkshopSortOrder.Hot;
    }

    private static WorkshopDeployMode ParseDeployMode(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "prefer-hardlink" => WorkshopDeployMode.PreferHardLink,
            "hardlink" => WorkshopDeployMode.HardLink,
            _ => WorkshopDeployMode.Copy
        };
    }
}
