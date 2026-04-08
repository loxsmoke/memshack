using MemShack.Core.Models;

namespace MemShack.Core.Interfaces;

public interface IRoomDetector
{
    IReadOnlyList<RoomDefinition> DetectRoomsFromFolders(string projectDirectory);

    IReadOnlyList<RoomDefinition> DetectRoomsFromFiles(string projectDirectory);
}
