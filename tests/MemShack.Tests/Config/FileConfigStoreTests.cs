using System.Text.Json.Nodes;
using MemShack.Core.Constants;
using MemShack.Core.Utilities;
using MemShack.Infrastructure.Config;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.Config;

[TestClass]
public sealed class FileConfigStoreTests
{
    private readonly FileConfigStore _store = new();

    [TestMethod]
    public void Initialize_CreatesDefaultConfigFile()
    {
        using var temp = new TemporaryDirectory();

        var path = _store.Initialize(temp.Root);

        Assert.True(File.Exists(path));
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal("mempalace_drawers", json["collection_name"]!.GetValue<string>());
    }

    [TestMethod]
    public void Load_UsesPeopleMapFileOverInlineConfig()
    {
        using var temp = new TemporaryDirectory();
        temp.WriteFile(ConfigFileNames.ConfigJson, """
            {
              "collection_name": "custom",
              "people_map": {
                "liz": "Elizabeth"
              }
            }
            """);
        temp.WriteFile(ConfigFileNames.PeopleMapJson, """
            {
              "liz": "Liz",
              "sam": "Samuel"
            }
            """);

        var snapshot = _store.Load(temp.Root);

        Assert.Equal("custom", snapshot.CollectionName);
        Assert.Equal("Liz", snapshot.PeopleMap["liz"]);
        Assert.Equal("Samuel", snapshot.PeopleMap["sam"]);
    }

    [TestMethod]
    public void Load_UsesEnvironmentOverrideForPalacePath()
    {
        using var temp = new TemporaryDirectory();
        temp.WriteFile(ConfigFileNames.ConfigJson, """
            {
              "palace_path": "/from-config"
            }
            """);

        var original = Environment.GetEnvironmentVariable("MEMPALACE_PALACE_PATH");
        Environment.SetEnvironmentVariable("MEMPALACE_PALACE_PATH", "/from-env");

        try
        {
            var snapshot = _store.Load(temp.Root);
            Assert.Equal("/from-env", snapshot.PalacePath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MEMPALACE_PALACE_PATH", original);
        }
    }

    [TestMethod]
    public void Load_FallsBackToDefaultsWhenFilesAreMissing()
    {
        using var temp = new TemporaryDirectory();

        var snapshot = _store.Load(temp.Root);

        Assert.Equal("mempalace_drawers", snapshot.CollectionName);
        Assert.Equal(MempalaceDefaults.GetDefaultPalacePath(PathUtilities.GetHomeDirectory()), snapshot.PalacePath);
        Assert.Equal(MempalaceDefaults.TopicWings, snapshot.TopicWings);
        Assert.Empty(snapshot.PeopleMap);
    }

    [TestMethod]
    public void Load_ReadsOptionalChromaSettings()
    {
        using var temp = new TemporaryDirectory();
        temp.WriteFile(ConfigFileNames.ConfigJson, """
            {
              "chroma_url": "http://localhost:8000",
              "chroma_tenant": "tenant_a",
              "chroma_database": "database_a"
            }
            """);

        var snapshot = _store.Load(temp.Root);

        Assert.Equal("http://localhost:8000", snapshot.ChromaUrl);
        Assert.Equal("tenant_a", snapshot.ChromaTenant);
        Assert.Equal("database_a", snapshot.ChromaDatabase);
    }
}
