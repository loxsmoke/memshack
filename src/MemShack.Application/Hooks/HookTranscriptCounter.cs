using System.Text.Json;

namespace MemShack.Application.Hooks;

public static class HookTranscriptCounter
{
    public static int CountHumanMessages(string transcriptPath)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath))
        {
            return 0;
        }

        var path = Path.GetFullPath(transcriptPath);
        if (!File.Exists(path))
        {
            return 0;
        }

        var count = 0;

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    if (IsClaudeUserMessage(document.RootElement) || IsCodexUserMessage(document.RootElement))
                    {
                        count++;
                    }
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }

        return count;
    }

    private static bool IsClaudeUserMessage(JsonElement entry)
    {
        if (!entry.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!message.TryGetProperty("role", out var role) ||
            role.ValueKind != JsonValueKind.String ||
            !string.Equals(role.GetString(), "user", StringComparison.Ordinal))
        {
            return false;
        }

        if (!message.TryGetProperty("content", out var content))
        {
            return true;
        }

        return !ContainsCommandMessage(content);
    }

    private static bool IsCodexUserMessage(JsonElement entry)
    {
        if (!entry.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String ||
            !string.Equals(type.GetString(), "event_msg", StringComparison.Ordinal))
        {
            return false;
        }

        if (!entry.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!payload.TryGetProperty("type", out var payloadType) ||
            payloadType.ValueKind != JsonValueKind.String ||
            !string.Equals(payloadType.GetString(), "user_message", StringComparison.Ordinal))
        {
            return false;
        }

        if (!payload.TryGetProperty("message", out var message))
        {
            return true;
        }

        return !ContainsCommandMessage(message);
    }

    private static bool ContainsCommandMessage(JsonElement content)
    {
        return content.ValueKind switch
        {
            JsonValueKind.String => ContainsCommandMessage(content.GetString()),
            JsonValueKind.Array => content.EnumerateArray().Any(
                item => item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("text", out var text) &&
                        text.ValueKind == JsonValueKind.String &&
                        ContainsCommandMessage(text.GetString())),
            _ => false,
        };
    }

    private static bool ContainsCommandMessage(string? content) =>
        !string.IsNullOrWhiteSpace(content) &&
        content.Contains("<command-message>", StringComparison.Ordinal);
}
