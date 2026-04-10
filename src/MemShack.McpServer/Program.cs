using MemShack.McpServer;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    string? palacePath = null;

    for (var index = 0; index < args.Length; index++)
    {
        var token = args[index];
        if (!string.Equals(token, "--palace", StringComparison.Ordinal))
        {
            continue;
        }

        if (index + 1 >= args.Length)
        {
            await Console.Error.WriteLineAsync("Missing value for --palace");
            return 1;
        }

        palacePath = args[++index];
    }

    await MemShackMcpServer
        .CreateDefault(palacePath: palacePath)
        .RunAsync(Console.In, Console.Out, Console.Error);

    return 0;
}
