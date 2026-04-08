using MemShack.Core.Models;

namespace MemShack.Core.Interfaces;

public interface IConfigStore
{
    MempalaceConfigSnapshot Load(string? configDirectory = null);

    string Initialize(string? configDirectory = null);

    string SavePeopleMap(IReadOnlyDictionary<string, string> peopleMap, string? configDirectory = null);
}
