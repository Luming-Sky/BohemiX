using System.Runtime.CompilerServices;
using BohemiX.Core.Models;

namespace BohemiX.Core.Tests;

public sealed class ApplicationErrorReportTests
{
    [Fact]
    public void FromException_UsesFirstAvailableSourceLocationAndCopiesDetails()
    {
        var exception = Assert.Throws<InvalidOperationException>(ThrowForLocationTest);

        var report = ApplicationErrorReport.FromException(
            exception,
            "Test failure",
            exception.Message,
            "test runner",
            locationHint: "test stage",
            logPath: @"C:\logs");

        Assert.Contains(nameof(ThrowForLocationTest), report.Location);
        Assert.Contains("location-test", report.TechnicalDetails);
        Assert.Contains(@"C:\logs", report.ToClipboardText());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(unchecked((int)0xC0000005), true)]
    public void GameProcessExitResult_ClassifiesOnlyNonZeroCodesAsAbnormal(int exitCode, bool expected)
    {
        var result = new GameProcessExitResult(true, 42, exitCode);

        Assert.Equal(expected, result.IsAbnormalExit);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowForLocationTest() => throw new InvalidOperationException("location-test");
}
