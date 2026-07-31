using BohemiX.Core.Models.Saves;

namespace BohemiX.Modules.SaveManager.Models;

public sealed class SaveBackupNodeListItem
{
    public SaveBackupNodeListItem(SaveBackupNode node, bool useEnglish)
    {
        Node = node;
        var fallback = Path.GetFileNameWithoutExtension(node.SourceRelativePath);
        Title = string.IsNullOrWhiteSpace(node.GameSaveName) ? fallback : node.GameSaveName;
        TypeText = node.Type switch
        {
            SaveType.Potion => useEnglish ? "Manual" : "手动",
            SaveType.Bed => useEnglish ? "Sleep" : "睡眠",
            SaveType.Auto => useEnglish ? "Auto" : "自动",
            SaveType.Exit => useEnglish ? "Exit" : "退出",
            _ => useEnglish ? "Save" : "存档"
        };
        ImportanceText = node.IsImportant
            ? (useEnglish ? "Important" : "重要")
            : (useEnglish ? "Recent" : "近期");
        HealthText = node.Health == SaveBackupNodeHealth.Healthy
            ? string.Empty
            : (useEnglish ? "Damaged" : "损坏");
    }

    public SaveBackupNode Node { get; }
    public Guid Id => Node.Id;
    public bool IsImportant => Node.IsImportant;
    public bool IsHealthy => Node.Health == SaveBackupNodeHealth.Healthy;
    public string Title { get; }
    public string TypeText { get; }
    public string ImportanceText { get; }
    public string HealthText { get; }
    public string FileName => Node.FileName;
    public string SavedText => (Node.LastSavedAtUtc ?? Node.FirstSeenAtUtc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string SizeText => FormatBytes(Node.TotalBytes);
    public string DetailText => string.IsNullOrWhiteSpace(Node.DisplayData?.CurrentLocation)
        ? FileName
        : Node.DisplayData.CurrentLocation;

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(bytes, 0);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }
}
