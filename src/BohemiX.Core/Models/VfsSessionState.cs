namespace BohemiX.Core.Models;

public enum VfsSessionState
{
    Idle = 0,
    Mounted = 1,
    Unmounting = 2,
    Faulted = 3,
    Mounting = 4
}
