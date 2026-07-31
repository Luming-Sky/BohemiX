namespace BohemiX.Core.Models;

public sealed record TrackerDiagnosticsResult(
    bool IsSuccessful,
    string Message,
    int EventsWritten,
    int EventsProcessed);
