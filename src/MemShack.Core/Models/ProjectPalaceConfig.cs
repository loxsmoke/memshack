namespace MemShack.Core.Models;

public sealed record ProjectPalaceConfig(string Wing, IReadOnlyList<RoomDefinition> Rooms);
