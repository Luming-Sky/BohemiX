namespace BohemiX.Core.Services.Saves;

/// <summary>
/// Creates switch-safety coordinators after the UI has resolved the official KCD2 save directory.
/// </summary>
public interface IProcessCoordinatorFactory
{
    /// <summary>
    /// Creates a coordinator for the official save path that CryEngine reads and writes.
    /// </summary>
    IProcessCoordinator Create(string officialSavePath);
}
