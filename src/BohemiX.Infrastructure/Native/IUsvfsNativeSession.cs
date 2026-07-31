namespace BohemiX.Infrastructure.Native;

internal interface IUsvfsNativeSessionFactory
{
    IUsvfsNativeSession Create(string libraryPath, string instanceName, string crashDumpDirectory);
}

internal interface IUsvfsNativeSession : IDisposable
{
    string Version { get; }

    void ClearMappings();

    bool LinkFile(string sourcePath, string destinationPath, uint flags);

    bool LinkDirectory(string sourcePath, string destinationPath, uint flags);

    int StartHookedProcess(string executablePath, string? arguments, string workingDirectory);
}

internal sealed class UsvfsNativeSessionFactory : IUsvfsNativeSessionFactory
{
    public IUsvfsNativeSession Create(string libraryPath, string instanceName, string crashDumpDirectory) =>
        new UsvfsNativeSession(libraryPath, instanceName, crashDumpDirectory);
}
