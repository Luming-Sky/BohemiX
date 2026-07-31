using System.Diagnostics;

namespace BohemiX.Core.Tests;

public sealed class TrimWinX64OutputScriptTests
{
    [Fact]
    public async Task Script_RemovesOnlyUnsupportedGeneratedPayloads()
    {
        var root = CreateTemporaryRoot();
        try
        {
            await WriteFixtureAsync(root, "BohemiX.App.exe");
            await WriteFixtureAsync(root, "Library.dll");
            await WriteFixtureAsync(root, "Library.xml");
            await WriteFixtureAsync(root, "runtime-data.xml");
            await WriteFixtureAsync(root, "symbols.pdb");
            await WriteFixtureAsync(root, Path.Combine("runtimes", "win", "native", "windows.dll"));
            await WriteFixtureAsync(root, Path.Combine("runtimes", "win-x64", "native", "windows-x64.dll"));
            await WriteFixtureAsync(root, Path.Combine("runtimes", "linux-x64", "native", "linux.so"));
            await WriteFixtureAsync(root, Path.Combine("libvlc", "win-x64", "libvlc.dll"));
            await WriteFixtureAsync(root, Path.Combine("libvlc", "win-x86", "libvlc.dll"));
            await WriteFixtureAsync(root, Path.Combine("win-x64", "publish", "stale.dll"));
            await WriteFixtureAsync(root, Path.Combine("forge-host", "BohemiX.ForgeHost.exe"));
            await WriteFixtureAsync(root, Path.Combine("forge-host", "runtimes", "osx", "native", "mac.dylib"));
            await WriteFixtureAsync(root, Path.Combine("forge-host", "runtimes", "win-x64", "native", "forge.dll"));
            File.Copy(
                Path.Combine(root, "forge-host", "runtimes", "win-x64", "native", "forge.dll"),
                Path.Combine(root, "forge-host", "forge.dll"));

            var result = await RunScriptAsync(root, removeSymbols: true);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(root, "BohemiX.App.exe")));
            Assert.True(File.Exists(Path.Combine(root, "Library.dll")));
            Assert.True(File.Exists(Path.Combine(root, "runtime-data.xml")));
            Assert.True(File.Exists(Path.Combine(root, "runtimes", "win", "native", "windows.dll")));
            Assert.True(File.Exists(Path.Combine(root, "runtimes", "win-x64", "native", "windows-x64.dll")));
            Assert.True(File.Exists(Path.Combine(root, "libvlc", "win-x64", "libvlc.dll")));
            Assert.True(File.Exists(Path.Combine(root, "forge-host", "BohemiX.ForgeHost.exe")));
            Assert.True(File.Exists(Path.Combine(root, "forge-host", "forge.dll")));
            Assert.False(Directory.Exists(Path.Combine(root, "runtimes", "linux-x64")));
            Assert.False(Directory.Exists(Path.Combine(root, "libvlc", "win-x86")));
            Assert.False(Directory.Exists(Path.Combine(root, "win-x64")));
            Assert.False(Directory.Exists(Path.Combine(root, "forge-host", "runtimes", "osx")));
            Assert.False(Directory.Exists(Path.Combine(root, "forge-host", "runtimes", "win-x64")));
            Assert.False(File.Exists(Path.Combine(root, "symbols.pdb")));
            Assert.False(File.Exists(Path.Combine(root, "Library.xml")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Script_PreservesSymbolsUnlessRequested()
    {
        var root = CreateTemporaryRoot();
        try
        {
            await WriteFixtureAsync(root, "symbols.pdb");

            var result = await RunScriptAsync(root, removeSymbols: false);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(Path.Combine(root, "symbols.pdb")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WriteFixtureAsync(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, relativePath);
    }

    private static async Task<ScriptResult> RunScriptAsync(string outputDirectory, bool removeSymbols)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(FindRepositoryFile(Path.Combine("tools", "trim-win-x64-output.ps1")));
        startInfo.ArgumentList.Add("-OutputDirectory");
        startInfo.ArgumentList.Add(outputDirectory);
        if (removeSymbols)
        {
            startInfo.ArgumentList.Add("-RemoveSymbols");
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start PowerShell.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ScriptResult(process.ExitCode, (await stdout) + Environment.NewLine + (await stderr));
    }

    private static string FindRepositoryFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX.TrimOutput.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed record ScriptResult(int ExitCode, string Output);
}
