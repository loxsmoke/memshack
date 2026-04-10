using System.Text.Json;
using System.Text.Json.Nodes;
using MemShack.Core.Constants;
using MemShack.Core.Interfaces;
using MemShack.Core.Models;
using MemShack.Core.Utilities;

namespace MemShack.Infrastructure.Config;

public sealed class FileConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public MempalaceConfigSnapshot Load(string? configDirectory = null)
    {
        var resolvedConfigDirectory = ResolveConfigDirectory(configDirectory);
        var configFile = Path.Combine(resolvedConfigDirectory, ConfigFileNames.ConfigJson);
        var peopleMapFile = Path.Combine(resolvedConfigDirectory, ConfigFileNames.PeopleMapJson);
        var homeDirectory = PathUtilities.GetHomeDirectory();

        var fileConfig = ReadJsonObject(configFile);

        var palacePath = Environment.GetEnvironmentVariable("MEMPALACE_PALACE_PATH")
            ?? Environment.GetEnvironmentVariable("MEMPAL_PALACE_PATH")
            ?? fileConfig?[ "palace_path" ]?.GetValue<string>()
            ?? MempalaceDefaults.GetDefaultPalacePath(homeDirectory);

        var chromaUrl = Environment.GetEnvironmentVariable("MEMPALACE_CHROMA_URL")
            ?? Environment.GetEnvironmentVariable("MEMPAL_CHROMA_URL")
            ?? Environment.GetEnvironmentVariable("MEMSHACK_CHROMA_URL")
            ?? fileConfig?["chroma_url"]?.GetValue<string>();

        var chromaTenant = Environment.GetEnvironmentVariable("MEMPALACE_CHROMA_TENANT")
            ?? Environment.GetEnvironmentVariable("MEMPAL_CHROMA_TENANT")
            ?? Environment.GetEnvironmentVariable("MEMSHACK_CHROMA_TENANT")
            ?? fileConfig?["chroma_tenant"]?.GetValue<string>()
            ?? "default_tenant";

        var chromaDatabase = Environment.GetEnvironmentVariable("MEMPALACE_CHROMA_DATABASE")
            ?? Environment.GetEnvironmentVariable("MEMPAL_CHROMA_DATABASE")
            ?? Environment.GetEnvironmentVariable("MEMSHACK_CHROMA_DATABASE")
            ?? fileConfig?["chroma_database"]?.GetValue<string>()
            ?? "default_database";

        var chromaBinaryPath = Environment.GetEnvironmentVariable("MEMPALACE_CHROMA_BINARY_PATH")
            ?? Environment.GetEnvironmentVariable("MEMPAL_CHROMA_BINARY_PATH")
            ?? Environment.GetEnvironmentVariable("MEMSHACK_CHROMA_BINARY_PATH")
            ?? fileConfig?["chroma_binary_path"]?.GetValue<string>();

        var chromaAutoInstall = ReadBoolean(Environment.GetEnvironmentVariable("MEMPALACE_CHROMA_AUTO_INSTALL"))
            ?? ReadBoolean(Environment.GetEnvironmentVariable("MEMPAL_CHROMA_AUTO_INSTALL"))
            ?? ReadBoolean(Environment.GetEnvironmentVariable("MEMSHACK_CHROMA_AUTO_INSTALL"))
            ?? ReadBoolean(fileConfig?["chroma_auto_install"])
            ?? true;

        var vectorStoreBackend = Environment.GetEnvironmentVariable("MEMPALACE_VECTOR_STORE_BACKEND")
            ?? Environment.GetEnvironmentVariable("MEMPAL_VECTOR_STORE_BACKEND")
            ?? Environment.GetEnvironmentVariable("MEMSHACK_VECTOR_STORE_BACKEND")
            ?? fileConfig?["vector_store_backend"]?.GetValue<string>()
            ?? "chroma";

        var collectionName = fileConfig?["collection_name"]?.GetValue<string>() ?? CollectionNames.Drawers;
        var peopleMap = ReadStringDictionary(peopleMapFile) ?? ReadStringDictionary(fileConfig?["people_map"]) ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var topicWings = ReadStringList(fileConfig?["topic_wings"]) ?? MempalaceDefaults.TopicWings.ToArray();
        var hallKeywords = ReadDictionaryOfLists(fileConfig?["hall_keywords"]) ?? CloneHallKeywords(MempalaceDefaults.HallKeywords);

        return new MempalaceConfigSnapshot(
            palacePath,
            collectionName,
            peopleMap,
            topicWings,
            hallKeywords,
            vectorStoreBackend,
            chromaUrl,
            chromaTenant,
            chromaDatabase,
            chromaBinaryPath,
            chromaAutoInstall,
            resolvedConfigDirectory);
    }

    public string Initialize(string? configDirectory = null)
    {
        var resolvedConfigDirectory = ResolveConfigDirectory(configDirectory);
        var configFile = Path.Combine(resolvedConfigDirectory, ConfigFileNames.ConfigJson);
        Directory.CreateDirectory(resolvedConfigDirectory);
        EnsureSecureConfigDirectoryPermissions(resolvedConfigDirectory);

        if (!File.Exists(configFile))
        {
            var homeDirectory = PathUtilities.GetHomeDirectory();
            var defaultConfig = new JsonObject
            {
                ["palace_path"] = MempalaceDefaults.GetDefaultPalacePath(homeDirectory),
                ["collection_name"] = CollectionNames.Drawers,
                ["vector_store_backend"] = "chroma",
                ["chroma_auto_install"] = true,
                ["topic_wings"] = new JsonArray(MempalaceDefaults.TopicWings.Select(value => JsonValue.Create(value)!).ToArray()),
                ["hall_keywords"] = ToJsonObject(MempalaceDefaults.HallKeywords),
            };

            File.WriteAllText(configFile, defaultConfig.ToJsonString(JsonOptions));
        }

        EnsureSecureConfigFilePermissions(configFile);

        return configFile;
    }

    public string SavePeopleMap(IReadOnlyDictionary<string, string> peopleMap, string? configDirectory = null)
    {
        var resolvedConfigDirectory = ResolveConfigDirectory(configDirectory);
        Directory.CreateDirectory(resolvedConfigDirectory);
        EnsureSecureConfigDirectoryPermissions(resolvedConfigDirectory);

        var peopleMapFile = Path.Combine(resolvedConfigDirectory, ConfigFileNames.PeopleMapJson);
        var json = JsonSerializer.Serialize(peopleMap, JsonOptions);
        File.WriteAllText(peopleMapFile, json);
        EnsureSecureConfigFilePermissions(peopleMapFile);
        return peopleMapFile;
    }

    private static string ResolveConfigDirectory(string? configDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configDirectory))
        {
            return Path.GetFullPath(PathUtilities.ExpandHome(configDirectory));
        }

        return MempalaceDefaults.GetDefaultConfigDirectory(PathUtilities.GetHomeDirectory());
    }

    private static JsonObject? ReadJsonObject(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(filePath)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string>? ReadStringDictionary(string filePath)
    {
        var jsonObject = ReadJsonObject(filePath);
        return jsonObject is null ? null : ReadStringDictionary(jsonObject);
    }

    private static IReadOnlyDictionary<string, string>? ReadStringDictionary(JsonNode? node)
    {
        if (node is not JsonObject jsonObject)
        {
            return null;
        }

        var dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in jsonObject)
        {
            var value = item.Value?.GetValue<string>();
            if (value is not null)
            {
                dictionary[item.Key] = value;
            }
        }

        return dictionary;
    }

    private static IReadOnlyList<string>? ReadStringList(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        return array
            .Select(item => item?.GetValue<string>())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>>? ReadDictionaryOfLists(JsonNode? node)
    {
        if (node is not JsonObject jsonObject)
        {
            return null;
        }

        var dictionary = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var item in jsonObject)
        {
            var values = ReadStringList(item.Value);
            if (values is not null)
            {
                dictionary[item.Key] = values;
            }
        }

        return dictionary;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> CloneHallKeywords(IReadOnlyDictionary<string, IReadOnlyList<string>> source)
    {
        return source.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<string>)item.Value.ToArray(),
            StringComparer.Ordinal);
    }

    private static bool? ReadBoolean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => null,
        };
    }

    private static bool? ReadBoolean(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue jsonValue)
        {
            try
            {
                return jsonValue.GetValue<bool>();
            }
            catch (InvalidOperationException)
            {
            }
            catch (FormatException)
            {
            }
        }

        return ReadBoolean(node.ToString());
    }

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, IReadOnlyList<string>> values)
    {
        var jsonObject = new JsonObject();
        foreach (var item in values)
        {
            jsonObject[item.Key] = new JsonArray(item.Value.Select(value => JsonValue.Create(value)!).ToArray());
        }

        return jsonObject;
    }

    private static void EnsureSecureConfigDirectoryPermissions(string directoryPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                directoryPath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private static void EnsureSecureConfigFilePermissions(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                filePath,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite);
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}
