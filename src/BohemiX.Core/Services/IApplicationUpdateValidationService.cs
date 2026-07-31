namespace BohemiX.Core.Services;

public sealed record ApplicationUpdateValidationResult(
    bool IsReady,
    string? AssemblyFingerprint,
    IReadOnlyList<string> MissingFiles);

public interface IApplicationUpdateValidationService
{
    Task<ApplicationUpdateValidationResult> ValidateAsync(
        string applicationDirectory,
        string applicationName,
        CancellationToken cancellationToken = default);
}
