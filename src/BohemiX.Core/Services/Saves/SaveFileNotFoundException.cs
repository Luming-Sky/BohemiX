namespace BohemiX.Core.Services.Saves;

/// <summary>
/// Raised when a requested KCD2 save payload cannot be found on disk.
/// </summary>
public sealed class SaveFileNotFoundException : FileNotFoundException
{
    public SaveFileNotFoundException(string message, string fileName)
        : base(message, fileName)
    {
    }
}
