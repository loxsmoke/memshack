using MemShack.Core.Models;

namespace MemShack.Core.Interfaces;

public interface IProjectPalaceConfigLoader
{
    ProjectPalaceConfig Load(string projectDirectory);
}
