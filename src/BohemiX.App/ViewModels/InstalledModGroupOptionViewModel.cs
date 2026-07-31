namespace BohemiX.App.ViewModels;

public sealed record InstalledModGroupOptionViewModel(string? GroupKey, string TitleText)
{
    public bool IsAutomatic => GroupKey is null;
}
