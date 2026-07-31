using BohemiX.Core.Models;
using BohemiX.Infrastructure.Services;

namespace BohemiX.Core.Tests;

public sealed class ModHealthAnalyzerTests
{
    [Fact]
    public void Analyze_ReportsPackageIssuesAndConflictParticipation()
    {
        var root = Path.Combine(Path.GetTempPath(), "BohemiX.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var alphaRoot = Path.Combine(root, "alpha");
            var betaRoot = Path.Combine(root, "beta");
            Directory.CreateDirectory(alphaRoot);
            Directory.CreateDirectory(betaRoot);

            var mods = new[]
            {
                new ModManifest(
                    "alpha",
                    "Alpha",
                    "1.0.0",
                    alphaRoot,
                    0,
                    true,
                    [
                        new ModFileEntry(@"Data\Config\shared.xml", 10, "alpha-a"),
                        new ModFileEntry(@"data/config/shared.xml", 11, "alpha-b")
                    ]),
                new ModManifest(
                    "beta",
                    "Beta",
                    "1.0.0",
                    betaRoot,
                    1,
                    true,
                    [
                        new ModFileEntry(@"Data\Config\shared.xml", 20, "beta")
                    ]),
                new ModManifest(
                    "gamma",
                    "Gamma",
                    "1.0.0",
                    Path.Combine(root, "missing"),
                    2,
                    false,
                    [])
            };
            var mountPlan = new ModMountPlanBuilder(new ModConflictAnalyzer()).BuildPlan(mods);

            var report = new ModHealthAnalyzer().Analyze(mods, mountPlan);
            var alpha = report.Mods.Single(mod => mod.ModId == "alpha");
            var beta = report.Mods.Single(mod => mod.ModId == "beta");
            var gamma = report.Mods.Single(mod => mod.ModId == "gamma");

            Assert.Equal(1, alpha.ConflictPathCount);
            Assert.Equal(0, alpha.WinningConflictPathCount);
            Assert.Equal(1, alpha.ShadowedConflictPathCount);
            Assert.Equal(1, alpha.DuplicateVirtualPathCount);
            Assert.Contains(alpha.Issues, issue => issue.Code == "mod.duplicate_virtual_paths");
            Assert.Contains(alpha.Issues, issue => issue.Code == "mod.shadowed_conflicts");

            Assert.Equal(1, beta.ConflictPathCount);
            Assert.Equal(1, beta.WinningConflictPathCount);
            Assert.Equal(0, beta.ShadowedConflictPathCount);
            Assert.False(beta.HasIssues);

            Assert.True(gamma.HasErrors);
            Assert.Contains(gamma.Issues, issue => issue.Code == "mod.root_missing");
            Assert.Contains(gamma.Issues, issue => issue.Code == "mod.empty");
            Assert.Contains(gamma.Issues, issue => issue.Code == "mod.disabled");
            Assert.Equal(1, report.ErrorModCount);
            Assert.Equal(1, report.WarningModCount);
            Assert.Equal(1, report.HealthyModCount);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
