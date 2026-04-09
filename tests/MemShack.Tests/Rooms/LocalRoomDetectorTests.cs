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
    public void DetectRoomsFromFolders_DoesNotAddArbitraryNestedDirectories()
    {
        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(temp.GetPath("src", "My.Feature"));
        Directory.CreateDirectory(temp.GetPath("docs", "compatibility"));

        var rooms = _detector.DetectRoomsFromFolders(temp.Root);

        Assert.Contains(rooms, room => room.Name == "src");
        Assert.Contains(rooms, room => room.Name == "documentation");
        Assert.DoesNotContain(rooms, room => room.Name == "my.feature");
        Assert.DoesNotContain(rooms, room => room.Name == "compatibility");
    }

    [TestMethod]
    public void DetectRoomsFromFolders_UsesPythonStyleDisplayOrder()
    {
        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(temp.GetPath("assets"));
        Directory.CreateDirectory(temp.GetPath("docs"));
        Directory.CreateDirectory(temp.GetPath("fixtures"));
        Directory.CreateDirectory(temp.GetPath("src"));
        Directory.CreateDirectory(temp.GetPath("tests"));
        Directory.CreateDirectory(temp.GetPath("tools"));

        var rooms = _detector.DetectRoomsFromFolders(temp.Root)
            .Select(room => room.Name)
            .ToArray();

        Assert.Equal(
            ["src", "design", "scripts", "fixtures", "testing", "documentation", "general"],
            rooms);
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
