using System.Text;
using System.Reflection;
using MemShack.Application.Chunking;
using MemShack.Application.Compression;
using MemShack.Application.Extraction;
using MemShack.Application.Layers;
using MemShack.Application.Mining;
using MemShack.Application.Normalization;
using MemShack.Application.Rooms;
using MemShack.Application.Scanning;
using MemShack.Application.Search;
using MemShack.Application.Spellcheck;
using MemShack.Application.Splitting;
using MemShack.Core.Constants;
using MemShack.Core.Interfaces;
using MemShack.Core.Models;
using MemShack.Core.Utilities;
using MemShack.Infrastructure.Config;
using MemShack.Infrastructure.Config.Projects;
using MemShack.Infrastructure.VectorStore.Collections;

namespace MemShack.Cli;

public sealed class CliApp
{
    private const string DefaultCommandName = "mems";
    private static readonly string CurrentVersion = typeof(CliApp)
        .Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion
        ?.Split('+', 2)[0] ?? "0.0.0";
    private readonly string? _configDirectory;
    private readonly string _commandName;
    private readonly IConfigStore _configStore;

    public CliApp(IConfigStore? configStore = null, string? configDirectory = null, string? commandName = null)
    {
        _configStore = configStore ?? new FileConfigStore();
        _configDirectory = configDirectory;
        _commandName = string.IsNullOrWhiteSpace(commandName) ? DefaultCommandName : commandName;
    }

    public async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (globalOptions, remainingArgs) = ParseGlobalOptions(args);
            if (remainingArgs.Count == 0 || IsHelpRequest(remainingArgs))
            {
                await stdout.WriteAsync(HelpText);
                return 0;
            }

            var command = remainingArgs[0];
            var commandArgs = remainingArgs.Skip(1).ToArray();

            return command switch
            {
                "init" => await RunInitAsync(commandArgs, stdout, stderr, cancellationToken),
                "mine" => await RunMineAsync(commandArgs, globalOptions, stdout, stderr, cancellationToken),
                "split" => await RunSplitAsync(commandArgs, stdout, stderr, cancellationToken),
                "search" => await RunSearchAsync(commandArgs, globalOptions, stdout, stderr, cancellationToken),
                "compress" => await RunCompressAsync(commandArgs, globalOptions, stdout, stderr, cancellationToken),
                "wake-up" => await RunWakeUpAsync(commandArgs, globalOptions, stdout, stderr, cancellationToken),
                "status" => await RunStatusAsync(commandArgs, globalOptions, stdout, stderr, cancellationToken),
                "repair" => await RunRepairAsync(commandArgs, globalOptions, stdout, stderr, cancellationToken),
                _ => await RunUnknownCommandAsync(command, stdout, stderr),
            };
        }
        catch (CliUsageException exception)
        {
            await stderr.WriteLineAsync(exception.Message);
            return 1;
        }
    }

    private async Task<int> RunInitAsync(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var parser = new ArgumentParser(args);
        var yes = false;
        string? projectDirectory = null;

        while (parser.TryReadNext(out var token))
        {
            switch (token)
            {
                case "--yes":
                    yes = true;
                    break;
                case string option when option.StartsWith("--", StringComparison.Ordinal):
                    throw new CliUsageException($"Unknown option for init: {token}");
                default:
                    projectDirectory ??= token;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            throw new CliUsageException($"Usage: {_commandName} init <dir> [--yes]");
        }

        var projectPath = Path.GetFullPath(PathUtilities.ExpandHome(projectDirectory));
        if (!Directory.Exists(projectPath))
        {
            await stderr.WriteLineAsync($"Directory not found: {projectDirectory}");
            return 1;
        }

        var detector = new LocalRoomDetector();
        var scanner = new ProjectScanner();
        var rooms = detector.DetectRoomsFromFolders(projectPath);
        var source = "folder structure";

        if (rooms.Count <= 1)
        {
            rooms = detector.DetectRoomsFromFiles(projectPath);
            source = "filename patterns";
        }

        if (rooms.Count == 0)
        {
            rooms = [new RoomDefinition("general", "All project files", [])];
            source = "fallback";
        }

        var fileCount = scanner.ScanProject(projectPath, respectGitignore: false).Count;
        var projectName = NormalizeWingName(Path.GetFileName(projectPath));
        var configPath = Path.Combine(projectPath, ConfigFileNames.MempalaceYaml);

        await File.WriteAllTextAsync(configPath, RenderProjectConfig(projectName, rooms), cancellationToken);
        var globalConfig = _configStore.Initialize(_configDirectory);

        await stdout.WriteLineAsync($"\n  MemShack Init");
        await stdout.WriteLineAsync($"  Wing: {projectName}");
        await stdout.WriteLineAsync($"  Rooms detected from {source}: {string.Join(", ", rooms.Select(room => room.Name))}");
        await stdout.WriteLineAsync($"  Files found: {fileCount}");
        if (yes)
        {
            await stdout.WriteLineAsync("  --yes supplied: auto-accepting detected rooms.");
        }

        await stdout.WriteLineAsync($"  Project config saved: {configPath}");
        await stdout.WriteLineAsync($"  Global config initialized: {globalConfig}");
        return 0;
    }

    private async Task<int> RunMineAsync(
        IReadOnlyList<string> args,
        GlobalOptions globalOptions,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var parser = new ArgumentParser(args);
        string? directory = null;
        var mode = "projects";
        string? wing = null;
        var noGitignore = false;
        var includeIgnored = new List<string>();
        var agent = "mempalace";
        var limit = 0;
        var dryRun = false;
        var extract = "exchange";

        while (parser.TryReadNext(out var token))
        {
            switch (token)
            {
                case "--mode":
                    mode = parser.RequireValue(token);
                    break;
                case "--wing":
                    wing = parser.RequireValue(token);
                    break;
                case "--no-gitignore":
                    noGitignore = true;
                    break;
                case "--include-ignored":
                    includeIgnored.Add(parser.RequireValue(token));
                    break;
                case "--agent":
                    agent = parser.RequireValue(token);
                    break;
                case "--limit":
                    limit = parser.RequireInt(token);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--extract":
                    extract = parser.RequireValue(token);
                    break;
                case string option when option.StartsWith("--", StringComparison.Ordinal):
                    throw new CliUsageException($"Unknown option for mine: {token}");
                default:
                    directory ??= token;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new CliUsageException($"Usage: {_commandName} mine <dir> [--mode projects|convos]");
        }

        var palacePath = ResolvePalacePath(globalOptions.PalacePath);
        var store = CreateVectorStore(palacePath);
        var result = string.Equals(mode, "convos", StringComparison.Ordinal)
            ? await new ConversationMiner(
                    new TranscriptNormalizer(new TranscriptSpellchecker(_configDirectory)),
                    new ConversationChunker(),
                    new GeneralMemoryExtractor(),
                    store)
                .MineAsync(directory, wing, agent, limit, dryRun, extract, cancellationToken: cancellationToken)
            : await new ProjectMiner(
                    new YamlProjectPalaceConfigLoader(),
                    new ProjectScanner(),
                    new TextChunker(),
                    store)
                .MineAsync(directory, wing, agent, limit, dryRun, !noGitignore, ExpandIncludeIgnored(includeIgnored), cancellationToken: cancellationToken);

        await stdout.WriteLineAsync($"\n  MemShack Mine");
        await stdout.WriteLineAsync($"  Mode: {mode}");
        await stdout.WriteLineAsync($"  Palace: {palacePath}");
        await stdout.WriteLineAsync($"  Files: {result.FilesDiscovered}");
        await stdout.WriteLineAsync($"  Files processed: {result.FilesProcessed}");
        await stdout.WriteLineAsync($"  Files skipped: {result.FilesSkipped}");
        await stdout.WriteLineAsync($"  Drawers filed: {result.DrawersFiled}");
        if (dryRun)
        {
            await stdout.WriteLineAsync("  Dry run: nothing was written.");
        }

        foreach (var room in result.RoomCounts.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal))
        {
            await stdout.WriteLineAsync($"    {room.Key}: {room.Value}");
        }

        return 0;
    }

    private async Task<int> RunSplitAsync(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var parser = new ArgumentParser(args);
        string? directory = null;
        string? outputDirectory = null;
        var dryRun = false;
        var minSessions = 2;

        while (parser.TryReadNext(out var token))
        {
            switch (token)
            {
                case "--output-dir":
                    outputDirectory = parser.RequireValue(token);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--min-sessions":
                    minSessions = parser.RequireInt(token);
                    break;
                case string option when option.StartsWith("--", StringComparison.Ordinal):
                    throw new CliUsageException($"Unknown option for split: {token}");
                default:
                    directory ??= token;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new CliUsageException($"Usage: {_commandName} split <dir> [--output-dir <dir>] [--dry-run] [--min-sessions <n>]");
        }

        var resolvedDirectory = Path.GetFullPath(PathUtilities.ExpandHome(directory));
        if (!Directory.Exists(resolvedDirectory))
        {
            await stderr.WriteLineAsync($"Directory not found: {directory}");
            return 1;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var splitter = new MegaFileSplitter(_configDirectory);
        var result = splitter.Split(resolvedDirectory, outputDirectory, dryRun, minSessions);

        await stdout.WriteLineAsync($"\n  Mega-file splitter {(dryRun ? "DRY RUN" : "SPLITTING")}");
        await stdout.WriteLineAsync($"  Source: {resolvedDirectory}");
        await stdout.WriteLineAsync($"  Output: {result.OutputDirectory ?? "same dir as source"}");
        await stdout.WriteLineAsync($"  Mega-files: {result.MegaFileCount}");

        if (result.MegaFileCount == 0)
        {
            await stdout.WriteLineAsync($"  No mega-files found in {resolvedDirectory} (min {minSessions} sessions).");
            return 0;
        }

        foreach (var file in result.Files)
        {
            await stdout.WriteLineAsync($"  {Path.GetFileName(file.SourceFile)} ({file.SessionCount} sessions)");
            foreach (var session in file.Sessions)
            {
                await stdout.WriteLineAsync($"    {(session.Written ? "wrote" : "would write")} {Path.GetFileName(session.OutputPath)} ({session.LineCount} lines)");
            }

            if (!string.IsNullOrWhiteSpace(file.BackupPath))
            {
                await stdout.WriteLineAsync($"    backup: {Path.GetFileName(file.BackupPath)}");
            }
        }

        await stdout.WriteLineAsync(
            dryRun
                ? $"  DRY RUN - would create {result.SessionsCreated} files from {result.MegaFileCount} mega-files"
                : $"  Done - created {result.SessionsCreated} files from {result.MegaFileCount} mega-files");
        return 0;
    }

    private async Task<int> RunSearchAsync(
        IReadOnlyList<string> args,
        GlobalOptions globalOptions,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var parser = new ArgumentParser(args);
        var queryParts = new List<string>();
        string? wing = null;
        string? room = null;
        var results = 5;

        while (parser.TryReadNext(out var token))
        {
            switch (token)
            {
                case "--wing":
                    wing = parser.RequireValue(token);
                    break;
                case "--room":
                    room = parser.RequireValue(token);
                    break;
                case "--results":
                    results = parser.RequireInt(token);
                    break;
                case string option when option.StartsWith("--", StringComparison.Ordinal):
                    throw new CliUsageException($"Unknown option for search: {token}");
                default:
                    queryParts.Add(token);
                    break;
            }
        }

        if (queryParts.Count == 0)
        {
            throw new CliUsageException($"Usage: {_commandName} search <query> [--wing NAME] [--room NAME]");
        }

        var palacePath = ResolvePalacePath(globalOptions.PalacePath);
        var service = new MemorySearchService(CreateVectorStore(palacePath), palacePath);
        var result = await service.SearchMemoriesAsync(string.Join(' ', queryParts), wing, room, results, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            await stderr.WriteLineAsync(result.Error);
            return 1;
        }

        await stdout.WriteAsync(await service.FormatSearchAsync(result.Query, wing, room, results, cancellationToken));
        return 0;
    }

    private async Task<int> RunCompressAsync(
        IReadOnlyList<string> args,
        GlobalOptions globalOptions,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var parser = new ArgumentParser(args);
        string? wing = null;
        string? configPath = null;
        var dryRun = false;

        while (parser.TryReadNext(out var token))
        {
            switch (token)
            {
                case "--wing":
                    wing = parser.RequireValue(token);
                    break;
                case "--config":
                    configPath = parser.RequireValue(token);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case string option when option.StartsWith("--", StringComparison.Ordinal):
                    throw new CliUsageException($"Unknown option for compress: {token}");
                default:
                    throw new CliUsageException($"Usage: {_commandName} compress [--wing <name>] [--dry-run] [--config <path>]");
            }
        }

        var palacePath = ResolvePalacePath(globalOptions.PalacePath);
        var store = CreateVectorStore(palacePath);
        var collections = await store.ListCollectionsAsync(cancellationToken);
        if (!collections.Contains(CollectionNames.Drawers, StringComparer.Ordinal))
        {
            await stderr.WriteLineAsync($"\n  No palace found at {palacePath}");
            await stderr.WriteLineAsync($"  Run: {_commandName} init <dir> then {_commandName} mine <dir>");
            return 1;
        }

        var resolvedConfigPath = ResolveCompressionConfigPath(configPath, palacePath);
        AaakDialect dialect;
        try
        {
            dialect = resolvedConfigPath is null ? new AaakDialect() : AaakDialect.FromConfig(resolvedConfigPath);
        }
        catch (Exception exception)
        {
            await stderr.WriteLineAsync($"Could not load compression config: {exception.Message}");
            return 1;
        }

        var service = new DrawerCompressionService(store, dialect);
        var result = await service.RunAsync(wing, dryRun, cancellationToken);
        if (result.DrawersScanned == 0)
        {
            var wingLabel = wing is null ? string.Empty : $" in wing '{wing}'";
            await stdout.WriteLineAsync($"\n  No drawers found{wingLabel}.");
            return 0;
        }

        if (resolvedConfigPath is not null)
        {
            await stdout.WriteLineAsync($"  Loaded entity config: {resolvedConfigPath}");
        }

        await stdout.WriteLineAsync(
            $"\n  Compressing {result.DrawersScanned} drawers{(wing is null ? string.Empty : $" in wing '{wing}'")}...");

        if (dryRun)
        {
            foreach (var entry in result.Entries)
            {
                await stdout.WriteLineAsync($"  [{entry.Wing}/{entry.Room}] {Path.GetFileName(entry.SourceFile)}");
                await stdout.WriteLineAsync(
                    $"    {entry.Stats.OriginalTokens}t -> {entry.Stats.CompressedTokens}t ({entry.Stats.Ratio:F1}x)");
                await stdout.WriteLineAsync($"    {entry.CompressedText}");
            }
        }
        else
        {
            await stdout.WriteLineAsync(
                $"  Stored {result.DrawersCompressed} compressed drawers in '{CollectionNames.Compressed}' collection.");
        }

        var totalRatio = result.TotalOriginalTokens / (double)Math.Max(result.TotalCompressedTokens, 1);
        await stdout.WriteLineAsync(
            $"  Total: {result.TotalOriginalTokens:N0}t -> {result.TotalCompressedTokens:N0}t ({totalRatio:F1}x compression)");
        if (dryRun)
        {
            await stdout.WriteLineAsync("  (dry run -- nothing stored)");
        }

        return 0;
    }

    private async Task<int> RunWakeUpAsync(
        IReadOnlyList<string> args,
        GlobalOptions globalOptions,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var parser = new ArgumentParser(args);
        string? wing = null;

        while (parser.TryReadNext(out var token))
        {
            switch (token)
            {
                case "--wing":
                    wing = parser.RequireValue(token);
                    break;
                case string option when option.StartsWith("--", StringComparison.Ordinal):
                    throw new CliUsageException($"Unknown option for wake-up: {token}");
                default:
                    throw new CliUsageException($"Usage: {_commandName} wake-up [--wing NAME]");
            }
        }

        var palacePath = ResolvePalacePath(globalOptions.PalacePath);
        var stack = new MemoryStack(CreateVectorStore(palacePath), palacePath, ResolveIdentityPath());
        var text = await stack.WakeUpAsync(wing, cancellationToken);
        var tokens = text.Length / 4;

        await stdout.WriteLineAsync($"Wake-up text (~{tokens} tokens):");
        await stdout.WriteLineAsync(new string('=', 50));
        await stdout.WriteLineAsync(text);
        return 0;
    }

    private async Task<int> RunStatusAsync(
        IReadOnlyList<string> args,
        GlobalOptions globalOptions,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (args.Count > 0)
        {
            throw new CliUsageException($"Usage: {_commandName} status");
        }

        var palacePath = ResolvePalacePath(globalOptions.PalacePath);
        var store = CreateVectorStore(palacePath);
        var collections = await store.ListCollectionsAsync(cancellationToken);
        if (!collections.Contains(CollectionNames.Drawers, StringComparer.Ordinal))
        {
            await stdout.WriteLineAsync($"\n  No palace found at {palacePath}");
            await stdout.WriteLineAsync($"  Run: {_commandName} init <dir> then {_commandName} mine <dir>");
            return 0;
        }

        var drawers = await store.GetDrawersAsync(CollectionNames.Drawers, cancellationToken: cancellationToken);
        var grouped = drawers
            .GroupBy(drawer => drawer.Metadata.Wing, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);

        await stdout.WriteLineAsync($"\n{new string('=', 55)}");
        await stdout.WriteLineAsync($"  MemShack Status - {drawers.Count} drawers");
        await stdout.WriteLineAsync($"{new string('=', 55)}\n");

        foreach (var wing in grouped)
        {
            await stdout.WriteLineAsync($"  WING: {wing.Key}");
            foreach (var room in wing.GroupBy(drawer => drawer.Metadata.Room, StringComparer.Ordinal).OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal))
            {
                await stdout.WriteLineAsync($"    ROOM: {room.Key,-20} {room.Count(),5} drawers");
            }

            await stdout.WriteLineAsync(string.Empty);
        }

        await stdout.WriteLineAsync($"{new string('=', 55)}\n");
        return 0;
    }

    private async Task<int> RunRepairAsync(
        IReadOnlyList<string> args,
        GlobalOptions globalOptions,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (args.Count > 0)
        {
            throw new CliUsageException($"Usage: {_commandName} repair");
        }

        var palacePath = ResolvePalacePath(globalOptions.PalacePath);
        var store = CreateVectorStore(palacePath);
        var collections = await store.ListCollectionsAsync(cancellationToken);
        if (!collections.Contains(CollectionNames.Drawers, StringComparer.Ordinal))
        {
            await stdout.WriteLineAsync($"\n  No palace found at {palacePath}");
            return 0;
        }

        var backupPath = $"{palacePath}.backup";
        if (Directory.Exists(backupPath))
        {
            Directory.Delete(backupPath, recursive: true);
        }

        CopyDirectory(palacePath, backupPath);
        await stdout.WriteLineAsync($"\n  MemShack Repair");
        await stdout.WriteLineAsync($"  Palace: {palacePath}");
        await stdout.WriteLineAsync($"  Backup: {backupPath}");

        foreach (var collection in collections)
        {
            var drawers = await store.GetDrawersAsync(collection, cancellationToken: cancellationToken);
            var collectionFile = Path.Combine(store.CollectionsPath, $"{collection}.json");
            if (File.Exists(collectionFile))
            {
                File.Delete(collectionFile);
            }

            await store.EnsureCollectionAsync(collection, cancellationToken);
            foreach (var drawer in drawers.OrderBy(drawer => drawer.Id, StringComparer.Ordinal))
            {
                await store.AddDrawerAsync(collection, drawer, cancellationToken);
            }

            await stdout.WriteLineAsync($"  Rebuilt collection '{collection}' ({drawers.Count} drawers)");
        }

        await stdout.WriteLineAsync("  Repair complete.");
        return 0;
    }

    private async Task<int> RunUnknownCommandAsync(string command, TextWriter stdout, TextWriter stderr)
    {
        await stderr.WriteLineAsync($"Unknown command: {command}");
        await stdout.WriteAsync(HelpText);
        return 1;
    }

    private static async Task<int> WriteAndReturnAsync(TextWriter writer, string text, int code)
    {
        await writer.WriteLineAsync(text);
        return code;
    }

    private static IEnumerable<string> ExpandIncludeIgnored(IEnumerable<string> rawValues) =>
        rawValues.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private ChromaCompatibilityVectorStore CreateVectorStore(string palacePath) => new(palacePath);

    private string ResolvePalacePath(string? palacePath)
    {
        if (!string.IsNullOrWhiteSpace(palacePath))
        {
            return Path.GetFullPath(PathUtilities.ExpandHome(palacePath));
        }

        return _configStore.Load(_configDirectory).PalacePath;
    }

    private string ResolveIdentityPath()
    {
        if (!string.IsNullOrWhiteSpace(_configDirectory))
        {
            return Path.Combine(Path.GetFullPath(PathUtilities.ExpandHome(_configDirectory)), ConfigFileNames.IdentityText);
        }

        return Path.Combine(
            MempalaceDefaults.GetDefaultConfigDirectory(PathUtilities.GetHomeDirectory()),
            ConfigFileNames.IdentityText);
    }

    private static string? ResolveCompressionConfigPath(string? configPath, string palacePath)
    {
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            var candidate = Path.GetFullPath(PathUtilities.ExpandHome(configPath));
            return File.Exists(candidate) ? candidate : null;
        }

        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.CurrentDirectory, ConfigFileNames.EntitiesJson),
                     Path.Combine(palacePath, ConfigFileNames.EntitiesJson),
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string RenderProjectConfig(string wing, IReadOnlyList<RoomDefinition> rooms)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"wing: {wing}");
        builder.AppendLine("rooms:");
        foreach (var room in rooms)
        {
            builder.AppendLine($"  - name: {room.Name}");
            builder.AppendLine($"    description: {room.Description}");
            if (room.Keywords.Count > 0)
            {
                builder.AppendLine("    keywords:");
                foreach (var keyword in room.Keywords)
                {
                    builder.AppendLine($"      - {keyword}");
                }
            }
        }

        return builder.ToString();
    }

    private static string NormalizeWingName(string name) =>
        name.ToLowerInvariant().Replace(" ", "_", StringComparison.Ordinal).Replace("-", "_", StringComparison.Ordinal);

    private static (GlobalOptions GlobalOptions, List<string> RemainingArgs) ParseGlobalOptions(IReadOnlyList<string> args)
    {
        var remaining = new List<string>();
        string? palacePath = null;

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg == "--palace")
            {
                if (index + 1 >= args.Count)
                {
                    throw new CliUsageException("Missing value for --palace");
                }

                palacePath = args[++index];
                continue;
            }

            if (arg.StartsWith("--palace=", StringComparison.Ordinal))
            {
                palacePath = arg["--palace=".Length..];
                continue;
            }

            remaining.Add(arg);
        }

        return (new GlobalOptions(palacePath), remaining);
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(file));
            File.Copy(file, destination, overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            var destination = Path.Combine(destinationDirectory, Path.GetFileName(directory));
            CopyDirectory(directory, destination);
        }
    }

    private static bool IsHelpRequest(IReadOnlyList<string> args) =>
        args.Count == 1 &&
        (string.Equals(args[0], "--help", StringComparison.Ordinal) ||
         string.Equals(args[0], "-h", StringComparison.Ordinal) ||
         string.Equals(args[0], "help", StringComparison.Ordinal));

    private sealed record GlobalOptions(string? PalacePath);

    private sealed class ArgumentParser
    {
        private readonly IReadOnlyList<string> _args;
        private int _index;

        public ArgumentParser(IReadOnlyList<string> args)
        {
            _args = args;
        }

        public bool TryReadNext(out string token)
        {
            if (_index >= _args.Count)
            {
                token = string.Empty;
                return false;
            }

            token = _args[_index++];
            return true;
        }

        public string RequireValue(string optionName)
        {
            if (_index >= _args.Count)
            {
                throw new CliUsageException($"Missing value for {optionName}");
            }

            return _args[_index++];
        }

        public int RequireInt(string optionName)
        {
            var value = RequireValue(optionName);
            if (!int.TryParse(value, out var parsed))
            {
                throw new CliUsageException($"Invalid integer for {optionName}: {value}");
            }

            return parsed;
        }
    }

    private sealed class CliUsageException : Exception
    {
        public CliUsageException(string message)
            : base(message)
        {
        }
    }

    private string HelpText => $$"""
MemShack v{{CurrentVersion}} - Give your AI a memory. No API key required.
C# port of MemPalace - The highest-scoring AI memory system ever benchmarked

Commands:
  {{_commandName}} init <dir> [--yes]
  {{_commandName}} split <dir> [--output-dir <dir>] [--dry-run] [--min-sessions <n>]
  {{_commandName}} mine <dir> [--mode projects|convos] [--wing <name>] [--no-gitignore]
                   [--include-ignored <path>] [--agent <name>] [--limit <n>]
                   [--dry-run] [--extract exchange|general]
  {{_commandName}} search <query> [--wing <name>] [--room <name>] [--results <n>]
  {{_commandName}} compress [--wing <name>] [--dry-run] [--config <path>]
  {{_commandName}} wake-up [--wing <name>]
  {{_commandName}} repair
  {{_commandName}} status

Global options:
  --palace <path>    Override the palace path
""";
}
