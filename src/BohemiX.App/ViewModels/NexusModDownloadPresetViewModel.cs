using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BohemiX.App.ViewModels;

public sealed partial class NexusModDownloadPresetViewModel : ViewModelBase
{
    public NexusModDownloadPresetViewModel(
        string key,
        string title,
        string description,
        IEnumerable<int> fileIds,
        string fileCountText,
        bool isRecommended = false)
    {
        Key = key;
        Title = title;
        Description = description;
        FileIds = fileIds.Distinct().ToHashSet();
        IsRecommended = isRecommended;
        FileCountText = fileCountText;
    }

    public string Key { get; }

    public string Title { get; }

    public string Description { get; }

    public HashSet<int> FileIds { get; }

    public bool IsRecommended { get; }

    public string FileCountText { get; }

    [ObservableProperty]
    private bool isSelected;
}
