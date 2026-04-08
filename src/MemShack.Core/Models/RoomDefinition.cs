namespace MemShack.Core.Models;

public sealed record RoomDefinition(string Name, string Description, IReadOnlyList<string> Keywords);
