using System.Diagnostics;

namespace BohemiX.Core.Tests;

public sealed class PublishWinX64ScriptTests
{
    private const string TestUsvfsVersion = "0.5.6-test";
    private const string TestSha256 = "0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public async Task PublishScript_FailsWhenRootLicenseIsMissing()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var repository = Path.Combine(root, "repository");
            var bundle = Path.Combine(root, "native-bundle");
            Directory.CreateDirectory(repository);
            Directory.CreateDirectory(bundle);
            await File.WriteAllTextAsync(Path.Combine(repository, "THIRD-PARTY-NOTICES.md"), "notices");

            var result = await RunScriptAsync(repository, bundle);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("root LICENSE", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PublishScript_FailsWhenUsvfsLicenseIsMissing()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var (repository, bundle) = await CreateLicensedFixtureAsync(root);
            File.Delete(Path.Combine(bundle, "LICENSE.txt"));

            var result = await RunScriptAsync(repository, bundle);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("upstream LICENSE", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PublishScript_FailsWhenUsvfsBinaryIsMissing()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var (repository, bundle) = await CreateLicensedFixtureAsync(root);

            var result = await RunScriptAsync(repository, bundle);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("exactly one of usvfs.dll or usvfs_x64.dll", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PublishScript_FailsWhenUsvfsIsNotAnX64PeDll()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var (repository, bundle) = await CreateLicensedFixtureAsync(root);
            await File.WriteAllTextAsync(Path.Combine(bundle, "usvfs.dll"), "not-a-pe-file");

            var result = await RunScriptAsync(repository, bundle);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("not a valid PE file", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PublishScript_FailsWhenUsvfsSha256DoesNotMatch()
    {
        var root = CreateTemporaryRoot();
        try
        {
            var (repository, bundle) = await CreateLicensedFixtureAsync(root);
            await WriteMinimalX64PeDllAsync(Path.Combine(bundle, "usvfs.dll"));

            var result = await RunScriptAsync(repository, bundle);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("SHA-256 mismatch", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(string Repository, string Bundle)> CreateLicensedFixtureAsync(string root)
    {
        var repository = Path.Combine(root, "repository");
        var bundle = Path.Combine(root, "native-bundle");
        Directory.CreateDirectory(repository);
        Directory.CreateDirectory(bundle);
        await File.WriteAllTextAsync(Path.Combine(repository, "LICENSE"), "project license fixture");
        await File.WriteAllTextAsync(Path.Combine(repository, "THIRD-PARTY-NOTICES.md"), "notices fixture");
        await File.WriteAllTextAsync(Path.Combine(bundle, "LICENSE.txt"), "upstream license fixture");
        await File.WriteAllTextAsync(Path.Combine(bundle, "USVFS-VERSION.txt"), TestUsvfsVersion);
        return (repository, bundle);
    }

    private static async Task<ScriptResult> RunScriptAsync(string repository, string bundle)
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
        startInfo.ArgumentList.Add(FindRepositoryFile(Path.Combine("tools", "publish-win-x64.ps1")));
        startInfo.ArgumentList.Add("-RepositoryRoot");
        startInfo.ArgumentList.Add(repository);
        startInfo.ArgumentList.Add("-NativeBundlePath");
        startInfo.ArgumentList.Add(bundle);
        startInfo.ArgumentList.Add("-UsvfsVersion");
        startInfo.ArgumentList.Add(TestUsvfsVersion);
        startInfo.ArgumentList.Add("-UsvfsSha256");
        startInfo.ArgumentList.Add(TestSha256);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start PowerShell.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ScriptResult(process.ExitCode, (await stdout) + Environment.NewLine + (await stderr));
    }

    private static Task WriteMinimalX64PeDllAsync(string path)
    {
        var bytes = new byte[512];
        WriteUInt16(bytes, 0, 0x5a4d);
        WriteUInt32(bytes, 0x3c, 0x80);
        WriteUInt32(bytes, 0x80, 0x00004550);
        WriteUInt16(bytes, 0x84, 0x8664);
        WriteUInt16(bytes, 0x86, 1);
        WriteUInt16(bytes, 0x94, 0x00f0);
        WriteUInt16(bytes, 0x96, 0x2002);
        WriteUInt16(bytes, 0x98, 0x020b);
        return File.WriteAllBytesAsync(path, bytes);
    }

    private static void WriteUInt16(byte[] bytes, int offset, ushort value) =>
        BitConverter.GetBytes(value).CopyTo(bytes, offset);

    private static void WriteUInt32(byte[] bytes, int offset, uint value) =>
        BitConverter.GetBytes(value).CopyTo(bytes, offset);

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
        var root = Path.Combine(Path.GetTempPath(), "BohemiX.PublishScript.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed record ScriptResult(int ExitCode, string Output);
}
