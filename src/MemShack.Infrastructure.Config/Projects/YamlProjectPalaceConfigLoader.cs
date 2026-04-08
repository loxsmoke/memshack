using MemShack.Core.Constants;
using MemShack.Core.Interfaces;
using MemShack.Core.Models;
using MemShack.Core.Utilities;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace MemShack.Infrastructure.Config.Projects;

public sealed class YamlProjectPalaceConfigLoader : IProjectPalaceConfigLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public ProjectPalaceConfig Load(string projectDirectory)
    {
        var projectPath = Path.GetFullPath(PathUtilities.ExpandHome(projectDirectory));
        var configPath = ResolveConfigPath(projectPath);
        var yaml = File.ReadAllText(configPath);
        var config = _deserializer.Deserialize<ProjectConfigDocument>(yaml)
            ?? throw new InvalidOperationException($"Could not parse project config at {configPath}");

        if (string.IsNullOrWhiteSpace(config.Wing))
        {
            throw new InvalidOperationException($"Project config at {configPath} is missing 'wing'.");
        }

        var rooms = (config.Rooms ?? [])
            .Select(room => new RoomDefinition(
                room.Name ?? "general",
                room.Description ?? "All project files",
                room.Keywords?.Where(keyword => !string.IsNullOrWhiteSpace(keyword)).ToArray() ?? []))
            .ToList();

        if (rooms.Count == 0)
        {
            rooms.Add(new RoomDefinition("general", "All project files", []));
        }

        return new ProjectPalaceConfig(config.Wing, rooms);
    }

    private static string ResolveConfigPath(string projectPath)
    {
        var primary = Path.Combine(projectPath, ConfigFileNames.MempalaceYaml);
        if (File.Exists(primary))
        {
            return primary;
        }

        var legacy = Path.Combine(projectPath, ConfigFileNames.LegacyMempalYaml);
        if (File.Exists(legacy))
        {
            return legacy;
        }

        throw new FileNotFoundException($"No {ConfigFileNames.MempalaceYaml} or {ConfigFileNames.LegacyMempalYaml} found in {projectPath}");
    }

    private sealed class ProjectConfigDocument
    {
        public string Wing { get; init; } = string.Empty;

        public List<ProjectRoomDocument>? Rooms { get; init; }
    }

    private sealed class ProjectRoomDocument
    {
        public string? Name { get; init; }

        public string? Description { get; init; }

        public List<string>? Keywords { get; init; }
    }
}
