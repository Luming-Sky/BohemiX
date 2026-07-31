using BohemiX.App.Services;

namespace BohemiX.App.Tests;

public sealed class MemoryEffectsBudgetTests
{
    [Fact]
    public void Budget_UsesHysteresisBeforeRestoringEffects()
    {
        var budget = new MemoryEffectsBudget();
        var now = DateTimeOffset.UtcNow;

        Assert.True(budget.Update(MemoryEffectsBudget.SuppressThresholdBytes, now));
        Assert.True(budget.IsSuppressed);
        Assert.True(budget.Update(MemoryEffectsBudget.RestoreThresholdBytes, now + TimeSpan.FromSeconds(59)));
        Assert.False(budget.Update(MemoryEffectsBudget.RestoreThresholdBytes, now + TimeSpan.FromSeconds(119)));
        Assert.False(budget.IsSuppressed);
    }

    [Fact]
    public void Budget_AbortsRecoveryWhenWorkingSetRises()
    {
        var budget = new MemoryEffectsBudget();
        var now = DateTimeOffset.UtcNow;

        budget.RecordHighLoadExit();
        Assert.True(budget.Update(MemoryEffectsBudget.RestoreThresholdBytes, now));
        Assert.True(budget.Update(MemoryEffectsBudget.RestoreThresholdBytes + 1, now + TimeSpan.FromSeconds(40)));
        Assert.True(budget.Update(MemoryEffectsBudget.RestoreThresholdBytes, now + TimeSpan.FromSeconds(80)));
        Assert.True(budget.IsSuppressed);
    }
}
