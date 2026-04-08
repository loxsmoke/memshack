using System.Text.Json;
using System.Text.Json.Nodes;
using MemShack.Core.Interfaces;
using MemShack.Application.Spellcheck;

namespace MemShack.Application.Normalization;

public sealed class TranscriptNormalizer : ITranscriptNormalizer
{
    private readonly TranscriptSpellchecker _spellchecker;

    public TranscriptNormalizer(TranscriptSpellchecker? spellchecker = null)
    {
        _spellchecker = spellchecker ?? new TranscriptSpellchecker();
    }

    public string NormalizeFromFile(string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            return NormalizeContent(content, Path.GetExtension(filePath));
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException($"Could not read {filePath}: {exception.Message}", exception);
        }
    }

    public string NormalizeContent(string content, string? extension = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        var lines = content.Split('\n');
        if (lines.Count(line => line.TrimStart().StartsWith('>')) >= 3)
        {
            return _spellchecker.SpellcheckTranscript(content);
        }

        var normalizedExtension = extension?.ToLowerInvariant();
        var firstCharacter = content.TrimStart().FirstOrDefault();
        if (normalizedExtension is ".json" or ".jsonl" || firstCharacter is '{' or '[')
        {
            var normalized = TryNormalizeJson(content);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return content;
    }

    private string? TryNormalizeJson(string content)
    {
        var normalized = TryClaudeCodeJsonl(content);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        normalized = TryCodexJsonl(content);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        JsonNode? data;
        try
        {
            data = JsonNode.Parse(content);
        }
        catch (JsonException)
        {
            return null;
        }

        foreach (var parser in new Func<JsonNode?, string?>[] { TryClaudeAiJson, TryChatGptJson, TrySlackJson })
        {
            normalized = parser(data);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return null;
    }

    private string? TryClaudeCodeJsonl(string content)
    {
        var messages = new List<(string Role, string Text)>();
        foreach (var line in SplitJsonlLines(content))
        {
            JsonNode? entry;
            try
            {
                entry = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry is not JsonObject entryObject)
            {
                continue;
            }

            var messageType = entryObject["type"]?.GetValue<string>() ?? string.Empty;
            var message = entryObject["message"];
            var text = ExtractContent(message?["content"]);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (messageType is "human" or "user")
            {
                messages.Add(("user", text));
            }
            else if (messageType == "assistant")
            {
                messages.Add(("assistant", text));
            }
        }

        return messages.Count >= 2 ? MessagesToTranscript(messages) : null;
    }

    private string? TryCodexJsonl(string content)
    {
        var messages = new List<(string Role, string Text)>();
        var hasSessionMeta = false;

        foreach (var line in SplitJsonlLines(content))
        {
            JsonNode? entry;
            try
            {
                entry = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry is not JsonObject entryObject)
            {
                continue;
            }

            var entryType = entryObject["type"]?.GetValue<string>() ?? string.Empty;
            if (entryType == "session_meta")
            {
                hasSessionMeta = true;
                continue;
            }

            if (entryType != "event_msg" || entryObject["payload"] is not JsonObject payload)
            {
                continue;
            }

            var payloadType = payload["type"]?.GetValue<string>() ?? string.Empty;
            var message = payload["message"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            if (payloadType == "user_message")
            {
                messages.Add(("user", message));
            }
            else if (payloadType == "agent_message")
            {
                messages.Add(("assistant", message));
            }
        }

        return messages.Count >= 2 && hasSessionMeta ? MessagesToTranscript(messages) : null;
    }

    private string? TryClaudeAiJson(JsonNode? data)
    {
        var root = data switch
        {
            JsonObject obj => obj["messages"] ?? obj["chat_messages"],
            _ => data,
        };

        if (root is not JsonArray messageArray)
        {
            return null;
        }

        if (messageArray.Count > 0 &&
            messageArray[0] is JsonObject firstObject &&
            firstObject["chat_messages"] is JsonArray)
        {
            var allMessages = new List<(string Role, string Text)>();
            foreach (var conversationNode in messageArray)
            {
                if (conversationNode is not JsonObject conversation ||
                    conversation["chat_messages"] is not JsonArray chatMessages)
                {
                    continue;
                }

                foreach (var chatMessageNode in chatMessages)
                {
                    AddClaudeMessage(chatMessageNode, allMessages);
                }
            }

            return allMessages.Count >= 2 ? MessagesToTranscript(allMessages) : null;
        }

        var messages = new List<(string Role, string Text)>();
        foreach (var item in messageArray)
        {
            AddClaudeMessage(item, messages);
        }

        return messages.Count >= 2 ? MessagesToTranscript(messages) : null;
    }

    private string? TryChatGptJson(JsonNode? data)
    {
        if (data is not JsonObject root || root["mapping"] is not JsonObject mapping)
        {
            return null;
        }

        string? rootId = null;
        string? fallbackRoot = null;

        foreach (var item in mapping)
        {
            if (item.Value is not JsonObject node)
            {
                continue;
            }

            if (node["parent"] is not null)
            {
                continue;
            }

            if (node["message"] is null)
            {
                rootId = item.Key;
                break;
            }

            fallbackRoot ??= item.Key;
        }

        rootId ??= fallbackRoot;
        if (string.IsNullOrWhiteSpace(rootId))
        {
            return null;
        }

        var currentId = rootId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var messages = new List<(string Role, string Text)>();

        while (!string.IsNullOrWhiteSpace(currentId) && visited.Add(currentId))
        {
            if (mapping[currentId] is not JsonObject node)
            {
                break;
            }

            if (node["message"] is JsonObject message)
            {
                var role = message["author"]?["role"]?.GetValue<string>() ?? string.Empty;
                var parts = message["content"]?["parts"] as JsonArray;
                var text = parts is null
                    ? string.Empty
                    : string.Join(" ", parts.Select(part => part?.GetValue<string>()).Where(part => !string.IsNullOrWhiteSpace(part)));

                if (role == "user" && text.Length > 0)
                {
                    messages.Add(("user", text));
                }
                else if (role == "assistant" && text.Length > 0)
                {
                    messages.Add(("assistant", text));
                }
            }

            currentId = (node["children"] as JsonArray)?.FirstOrDefault()?.GetValue<string>();
        }

        return messages.Count >= 2 ? MessagesToTranscript(messages) : null;
    }

    private string? TrySlackJson(JsonNode? data)
    {
        if (data is not JsonArray messagesArray)
        {
            return null;
        }

        var seenUsers = new Dictionary<string, string>(StringComparer.Ordinal);
        string? lastRole = null;
        var messages = new List<(string Role, string Text)>();

        foreach (var item in messagesArray)
        {
            if (item is not JsonObject messageObject || messageObject["type"]?.GetValue<string>() != "message")
            {
                continue;
            }

            var userId = messageObject["user"]?.GetValue<string>() ?? messageObject["username"]?.GetValue<string>() ?? string.Empty;
            var text = messageObject["text"]?.GetValue<string>()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!seenUsers.ContainsKey(userId))
            {
                seenUsers[userId] = seenUsers.Count == 0
                    ? "user"
                    : lastRole == "user"
                        ? "assistant"
                        : "user";
            }

            lastRole = seenUsers[userId];
            messages.Add((lastRole, text));
        }

        return messages.Count >= 2 ? MessagesToTranscript(messages) : null;
    }

    private static void AddClaudeMessage(JsonNode? item, ICollection<(string Role, string Text)> messages)
    {
        if (item is not JsonObject message)
        {
            return;
        }

        var role = message["role"]?.GetValue<string>() ?? string.Empty;
        var text = ExtractContent(message["content"]);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (role is "user" or "human")
        {
            messages.Add(("user", text));
        }
        else if (role is "assistant" or "ai")
        {
            messages.Add(("assistant", text));
        }
    }

    private static string ExtractContent(JsonNode? content)
    {
        return content switch
        {
            null => string.Empty,
            JsonValue value when value.TryGetValue<string>(out var text) => text.Trim(),
            JsonArray array => string.Join(
                    " ",
                    array.SelectMany(item => item switch
                    {
                        JsonValue stringValue when stringValue.TryGetValue<string>(out var stringText) => [stringText],
                        JsonObject objectValue when objectValue["type"]?.GetValue<string>() == "text" => [objectValue["text"]?.GetValue<string>() ?? string.Empty],
                        _ => Array.Empty<string>(),
                    }))
                .Trim(),
            JsonObject obj => obj["text"]?.GetValue<string>()?.Trim() ?? string.Empty,
            _ => string.Empty,
        };
    }

    private string MessagesToTranscript(IReadOnlyList<(string Role, string Text)> messages)
    {
        var lines = new List<string>();
        var index = 0;

        while (index < messages.Count)
        {
            var (role, text) = messages[index];
            if (role == "user")
            {
                lines.Add($"> {_spellchecker.SpellcheckUserText(text)}");
                if (index + 1 < messages.Count && messages[index + 1].Role == "assistant")
                {
                    lines.Add(messages[index + 1].Text);
                    index += 2;
                }
                else
                {
                    index++;
                }
            }
            else
            {
                lines.Add(text);
                index++;
            }

            lines.Add(string.Empty);
        }

        return string.Join('\n', lines);
    }

    private static IEnumerable<string> SplitJsonlLines(string content) =>
        content
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);
}
