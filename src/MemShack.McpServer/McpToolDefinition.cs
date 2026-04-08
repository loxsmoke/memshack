using System.Text.Json.Nodes;

namespace MemShack.McpServer;

public sealed class McpToolDefinition
{
    public McpToolDefinition(
        string name,
        string description,
        JsonObject inputSchema,
        Func<JsonObject, CancellationToken, Task<object?>> handler)
    {
        Name = name;
        Description = description;
        InputSchema = inputSchema;
        Handler = handler;
    }

    public string Name { get; }

    public string Description { get; }

    public JsonObject InputSchema { get; }

    public Func<JsonObject, CancellationToken, Task<object?>> Handler { get; }

    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["description"] = Description,
        ["inputSchema"] = InputSchema.DeepClone(),
    };
}
