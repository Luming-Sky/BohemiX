using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace BohemiX.App.Services;

public sealed class GamePathPickerService : IGamePathPickerService
{
    public async Task<string?> PickGameExecutableAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            return null;
        }

        var files = await mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select KingdomCome.exe",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Windows executable")
                {
                    Patterns = ["*.exe"]
                }
            ]
        });

        return files.Count == 0 ? null : files[0].Path.LocalPath;
    }

    public async Task<string?> PickGameDirectoryAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            return null;
        }

        var folders = await mainWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select KCD2 game directory",
            AllowMultiple = false
        });

        return folders.Count == 0 ? null : folders[0].Path.LocalPath;
    }

    public async Task<string?> PickSaveDirectoryAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            return null;
        }

        var folders = await mainWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select KCD2 official save directory",
            AllowMultiple = false
        });

        return folders.Count == 0 ? null : folders[0].Path.LocalPath;
    }

    public async Task<string?> PickModPackageAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            return null;
        }

        var files = await mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select mod package",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Mod package")
                {
                    Patterns = ["*.zip", "*.pak", "*.rar", "*.7z"]
                }
            ]
        });

        return files.Count == 0 ? null : files[0].Path.LocalPath;
    }

    public async Task<string?> PickModPackAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            return null;
        }

        var files = await mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select mod pack archive",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Mod pack archive")
                {
                    Patterns = ["*.zip", "*.7z", "*.rar"]
                }
            ]
        });

        return files.Count == 0 ? null : files[0].Path.LocalPath;
    }

    public async Task<string?> PickAvatarAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            return null;
        }

        var files = await mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select player avatar",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Image")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"]
                }
            ]
        });

        return files.Count == 0 ? null : files[0].Path.LocalPath;
    }

    public async Task<string?> PickAppearanceMediaAsync(bool animated)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } mainWindow })
        {
            return null;
        }

        var files = await mainWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = animated ? "Select animated background media" : "Select background image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(animated ? "Animated image or video" : "Image")
                {
                    Patterns = animated
                        ? ["*.gif", "*.apng", "*.png", "*.webp", "*.mp4", "*.webm", "*.mkv", "*.avi", "*.mov", "*.wmv", "*.m4v"]
                        : ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"]
                }
            ]
        });

        return files.Count == 0 ? null : files[0].Path.LocalPath;
    }
}
