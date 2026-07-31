namespace BohemiX.Core.Models;

public enum TrackerEventType
{
    Unknown = 0,
    SessionStarted,
    QuestStarted,
    QuestCompleted,
    ItemAcquired,
    ItemRemoved,
    PositionUpdated,
    GameSaved,
    SessionEnded
}
