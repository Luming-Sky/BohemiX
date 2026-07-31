using BohemiX.Core.Models.Saves;

namespace BohemiX.Modules.SaveManager.Models;

public sealed class SaveSnapshotListItem
{
    private readonly string sizeLabel;

    public SaveSnapshotListItem(SaveSnapshot snapshot, SaveManagerText text)
    {
        Snapshot = snapshot;
        sizeLabel = ReferenceEquals(text, SaveManagerText.English) ? "Restore size" : "恢复大小";
        TriggerText = snapshot.Trigger switch
        {
            SaveSnapshotTrigger.Manual => text.TriggerManual,
            SaveSnapshotTrigger.BeforeSwitch => text.TriggerBeforeSwitch,
            SaveSnapshotTrigger.GameExit => text.TriggerGameExit,
            SaveSnapshotTrigger.BeforeRestore => text.TriggerBeforeRestore,
            _ => snapshot.Trigger.ToString()
        };
    }

    public SaveSnapshot Snapshot { get; }
    public Guid Id => Snapshot.Id;
    public string TriggerText { get; }
    public string CreatedText => Snapshot.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string SizeText => $"{sizeLabel} {FormatBytes(Snapshot.TotalBytes)}";
    public string NoteText => string.IsNullOrWhiteSpace(Snapshot.Note) ? TriggerText : Snapshot.Note;

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
