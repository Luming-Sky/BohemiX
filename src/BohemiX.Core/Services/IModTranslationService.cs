namespace BohemiX.Core.Services;

public enum ModTranslationFailureKind
{
    Network,
    Service,
    QuotaExceeded,
    InvalidResponse
}

public sealed class ModTranslationException : Exception
{
    public ModTranslationException(
        ModTranslationFailureKind failureKind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
    }

    public ModTranslationFailureKind FailureKind { get; }
}

public interface IModTranslationService
{
    Task<string> TranslateAsync(
        string text,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default);
}
