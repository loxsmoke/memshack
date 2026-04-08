using System.Collections.ObjectModel;

namespace MemShack.Core.Constants;

public static class MempalaceDefaults
{
    public static readonly IReadOnlyList<string> TopicWings =
    [
        "emotions",
        "consciousness",
        "memory",
        "technical",
        "identity",
        "family",
        "creative",
    ];

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> HallKeywords =
        new ReadOnlyDictionary<string, IReadOnlyList<string>>(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["emotions"] =
                [
                    "scared",
                    "afraid",
                    "worried",
                    "happy",
                    "sad",
                    "love",
                    "hate",
                    "feel",
                    "cry",
                    "tears",
                ],
                ["consciousness"] =
                [
                    "consciousness",
                    "conscious",
                    "aware",
                    "real",
                    "genuine",
                    "soul",
                    "exist",
                    "alive",
                ],
                ["memory"] = ["memory", "remember", "forget", "recall", "archive", "palace", "store"],
                ["technical"] =
                [
                    "code",
                    "python",
                    "script",
                    "bug",
                    "error",
                    "function",
                    "api",
                    "database",
                    "server",
                ],
                ["identity"] = ["identity", "name", "who am i", "persona", "self"],
                ["family"] = ["family", "kids", "children", "daughter", "son", "parent", "mother", "father"],
                ["creative"] = ["game", "gameplay", "player", "app", "design", "art", "music", "story"],
            });

    public static string GetDefaultConfigDirectory(string homeDirectory) =>
        Path.Combine(homeDirectory, ".mempalace");

    public static string GetDefaultPalacePath(string homeDirectory) =>
        Path.Combine(GetDefaultConfigDirectory(homeDirectory), "palace");

    public static string GetDefaultKnowledgeGraphPath(string homeDirectory) =>
        Path.Combine(GetDefaultConfigDirectory(homeDirectory), ConfigFileNames.KnowledgeGraphSqlite);
}
