using System;
using System.IO;
using Avalonia.Media.Imaging;
using BohemiX.Core.PlayerProfiles;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BohemiX.App.PlayerProfiles;

public sealed partial class PlayerProfileItemViewModel : ObservableObject, IDisposable
{
    private const int AvatarDecodeWidth = 320;

    public PlayerProfileItemViewModel(PlayerProfile profile, bool isCurrent, string providerDisplayName)
    {
        Profile = profile;
        IsCurrent = isCurrent;
        ProviderDisplayName = providerDisplayName;
        AvatarImage = TryLoadAvatar(profile.Avatar);
    }

    public PlayerProfile Profile { get; }

    public Guid Id => Profile.Id;

    public string DisplayName => Profile.DisplayName;

    public string ProviderDisplayName { get; }

    // Kept temporarily for compiled bindings in the existing host. These values describe the
    // account provider and profile bio, never a game platform or installation path.
    public string PlatformDisplayName => ProviderDisplayName;

    public bool IsSteamPlatform => false;

    public bool IsGogPlatform => false;

    public string BioDisplay => string.IsNullOrWhiteSpace(Profile.Bio)
        ? "未填写个人简介"
        : Profile.Bio;

    public string GameInstallPathDisplay => BioDisplay;

    public string CreatedTimeDisplay => Profile.CreatedTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string LastLoginTimeDisplay => Profile.LastLoginTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string Initial => string.IsNullOrWhiteSpace(Profile.DisplayName)
        ? "?"
        : char.ToUpperInvariant(Profile.DisplayName.Trim()[0]).ToString();

    public Bitmap? AvatarImage { get; }

    public bool HasAvatar => AvatarImage is not null;

    [ObservableProperty]
    private bool isCurrent;

    public void Dispose()
    {
        AvatarImage?.Dispose();
    }

    private static Bitmap? TryLoadAvatar(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, AvatarDecodeWidth, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
