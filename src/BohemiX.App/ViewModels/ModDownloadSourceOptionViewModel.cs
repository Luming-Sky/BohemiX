using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BohemiX.App.ViewModels;

public sealed partial class ModDownloadSourceOptionViewModel : ViewModelBase
{
    public const string NexusKey = "Nexus";
    public const string SteamWorkshopKey = "SteamWorkshop";
    public const string ModPackKey = "ModPack";

    public ModDownloadSourceOptionViewModel(string key, string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        Key = key;
        DisplayName = displayName;
    }

    public string Key { get; }

    public bool IsSteamWorkshop => string.Equals(Key, SteamWorkshopKey, StringComparison.Ordinal);

    public bool IsNexus => string.Equals(Key, NexusKey, StringComparison.Ordinal);

    public bool IsModPack => string.Equals(Key, ModPackKey, StringComparison.Ordinal);

    [ObservableProperty]
    private string displayName;
}
