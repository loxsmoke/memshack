using MemShack.Application.Rooms;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.Rooms;

[TestClass]
public sealed class LocalRoomDetectorTests
{
    private readonly LocalRoomDetector _detector = new();

    [TestMethod]
    public void DetectRoomsFromFolders_UsesTopLevelAndNestedDirectories()
    {
        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(temp.GetPath("frontend"));
        Directory.CreateDirectory(temp.GetPath("services", "api"));

        var rooms = _detector.DetectRoomsFromFolders(temp.Root);

        Assert.Contains(rooms, room => room.Name == "frontend");
        Assert.Contains(rooms, room => room.Name == "backend");
        Assert.Contains(rooms, room => room.Name == "general");
    }

    [TestMethod]
    public void DetectRoomsFromFiles_FallsBackToRecurringFilenamePatterns()
    {
        using var temp = new TemporaryDirectory();
        temp.WriteFile("ui_component.tsx", "export const Ui = () => null;");
        temp.WriteFile("page_ui.tsx", "export const Page = () => null;");
        temp.WriteFile("team_notes.md", "team sync");
        temp.WriteFile("staff_roster.md", "staff roster");

        var rooms = _detector.DetectRoomsFromFiles(temp.Root);

        Assert.Contains(rooms, room => room.Name == "frontend");
        Assert.Contains(rooms, room => room.Name == "team");
    }
}
