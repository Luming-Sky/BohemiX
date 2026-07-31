namespace BohemiX.Core.Models;

public sealed record ModCatalogHealthReport(IReadOnlyList<ModHealthReport> Mods)
{
    public int HealthyModCount => Mods.Count(mod => !mod.HasIssues);

    public int WarningModCount => Mods.Count(mod => mod.HasWarnings && !mod.HasErrors);

    public int ErrorModCount => Mods.Count(mod => mod.HasErrors);
}
