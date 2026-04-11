using System.Reflection;
using System.Text.Json;
using System.Text;
using System.Diagnostics;
using MemShack.Application.Chunking;
using MemShack.Application.Compression;
using MemShack.Application.Deduplication;
using MemShack.Application.Entities;
using MemShack.Application.Extraction;
using MemShack.Application.Hooks;
using MemShack.Application.Layers;
using MemShack.Application.Migration;
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
using MemShack.Infrastructure.VectorStore;
using MemShack.Infrastructure.VectorStore.Collections;

namespace MemShack.Cli;

public sealed class CliApp
{
    private const string DefaultCommandName = "mems";
    private static readonly JsonSerializerOptions EntityConfigJsonOptions = new()
    {
        WriteIndented = true,
    };
    private static readonly string CurrentVersion = typeof(CliApp)
        .Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion
        ?.Split('+', 2)[0] ?? "0.0.0";
    private readonly string? _configDirectory;
    private readonly Func<Action<ChromaDownloadProgress>?, ChromaSidecarManager> _chromaSidecarManagerFactory;
    private readonly string _commandName;
    private readonly IConfigStore _configStore;

    public CliApp(
        IConfigStore? configStore = null,
        string? configDirectory = null,
        string? commandName = null,
        Func<Action<ChromaDownloadProgress>?, ChromaSidecarManager>? chromaSidecarManagerFactory = null)
    {
        _configStore = configStore ?? new FileConfigStore();
        _configDirectory = configDirectory;
        _commandName = string.IsNullOrWhiteSpace(commandName) ? DefaultCommandName : commandName;
        _chromaSidecarManagerFactory = chromaSidecarManagerFactory ?? (downloadProgress => new ChromaSidecarManager(downloadProgress: downloadProgress));
    }

    public async Task<int> RunAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken = default) =>
        await RunAsync(args, Console.In, stdout, stderr, cancellationToken);

    public async Task<int> RunAsync(
        string[] args,
        TextReader stdin,
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
                "__where-chroma" => await RunWhereChromaAsync(stdout),
                "__count-human-messages" => await RunCountHumanMessagesAsync(commandArgs, stdout, stderr),
                "init" => await RunInitAsync(commandArgs, stdin, stdout, stderr, cancellationToken),
                "mine" => await RunMineAsync(commandArgs, globalOptions, stdout, stderr, cancellationToken),
                "migrate" => await RunMigrateAsync(commandArgs, globalOptions, stdout, stderr, cancellationToken),
                "dedup" => await RunDedupAsync(commandArgs, globalOptions, stdout, stderr, cancellationToken),
                "split" => await RunSplitAsync(commandArgs, stdout, stderr, cancellationToken),
                "search" => await RunSearchAsync(commandArgs, globalOptions, stdout, stderr, cancellationToken),
                "hook" => await RunHookAsync(commandArgs, stdout, stderr, cancellationToken),
                "instructions" => await RunInstructionsAsync(commandArgs, stdout, stderr, cancellationToken),
                "mcp" => await RunMcpAsync(commandArgs, globalOptions, stdout, stderr, cancellationToken),
                "compress" => await RunCompressAsync(commandArgs, globalOptions, stdout, stderr, cancellationToken),
                "wake-up" => await RunWakeUpAsync(commandArgs, globalOptions, stdout, stderr, cancellationToken),
                "shutdowndb" => await RunShutdownDbAsync(commandArgs, globalOptions, stdout, stderr, cancellationToken),
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
        TextReader stdin,
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

        await RunEntityDetectionAsync(projectDirectory, projectPath, yes, stdin, stdout, cancellationToken);

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
            source = "fallback (flat project)";
        }

        var fileCount = scanner.ScanProject(projectPath).Count;
        var projectName = NormalizeWingName(PathUtilities.GetLeafName(projectPath));
        var configPath = Path.Combine(projectPath, ConfigFileNames.MempalaceYaml);

        await PrintProposedStructureAsync(stdout, projectName, rooms, fileCount, source);
        var approvedRooms = yes
            ? rooms
            : await ApproveRoomsAsync(rooms, stdin, stdout, cancellationToken);

        await File.WriteAllTextAsync(configPath, RenderProjectConfig(projectName, approvedRooms), cancellationToken);
        _configStore.Initialize(_configDirectory);

        await stdout.WriteLineAsync($"\n  Config saved: {configPath}");
        await stdout.WriteLineAsync("\n  Next step:");
        await stdout.WriteLineAsync($"    {_commandName} mine {projectDirectory}");
        await stdout.WriteLineAsync($"\n{new string('=', 55)}\n");
        return 0;
    }

    private static async Task RunEntityDetectionAsync(
        string projectDirectory,
        string projectPath,
        bool yes,
        TextReader stdin,
        TextWriter stdout,
        CancellationToken cancellationToken)
    {
        var detector = new EntityDetector(includeCamelCaseCandidates: false);
        await stdout.WriteLineAsync($"\n  Scanning for entities in: {projectDirectory}");

        var files = detector.ScanForDetection(projectPath, prioritizeRelevantFiles: true);
        if (files.Count == 0)
        {
            return;
        }

        await stdout.WriteLineAsync($"  Reading {files.Count} files...");
        var detected = detector.DetectEntities(files);
        var total = detected.People.Count + detected.Projects.Count + detected.Uncertain.Count;
        if (total == 0)
        {
            await stdout.WriteLineAsync("  No entities detected \u2014 proceeding with directory-based rooms.");
            return;
        }

        var confirmed = await ConfirmEntitiesAsync(detected, yes, stdin, stdout, cancellationToken);
        if (!confirmed.HasAny)
        {
            return;
        }

        var entitiesPath = Path.Combine(projectPath, ConfigFileNames.EntitiesJson);
        var entityConfig = new
        {
            people = confirmed.People,
            projects = confirmed.Projects,
            entities = BuildEntityCodes(confirmed.People, confirmed.Projects),
            skip_names = Array.Empty<string>(),
        };

        await File.WriteAllTextAsync(
            entitiesPath,
            JsonSerializer.Serialize(entityConfig, EntityConfigJsonOptions),
            cancellationToken);
        await stdout.WriteLineAsync($"  Entities saved: {entitiesPath}");
    }

    private static async Task<ConfirmedEntities> ConfirmEntitiesAsync(
        DetectedEntities detected,
        bool yes,
        TextReader stdin,
        TextWriter stdout,
        CancellationToken cancellationToken)
    {
        await stdout.WriteLineAsync($"\n{new string('=', 58)}");
        await stdout.WriteLineAsync("  MemShack - Entity Detection");
        await stdout.WriteLineAsync(new string('=', 58));
        await stdout.WriteLineAsync("\n  Scanned your files. Here's what we found:\n");

        await PrintEntityListAsync(stdout, detected.People, "PEOPLE");
        await PrintEntityListAsync(stdout, detected.Projects, "PROJECTS");

        if (detected.Uncertain.Count > 0)
        {
            await PrintEntityListAsync(stdout, detected.Uncertain, "UNCERTAIN (need your call)");
        }

        var confirmedPeople = detected.People.Select(entity => entity.Name).ToList();
        var confirmedProjects = detected.Projects.Select(entity => entity.Name).ToList();

        if (yes)
        {
            await stdout.WriteLineAsync(
                $"\n  Auto-accepting {confirmedPeople.Count} people, {confirmedProjects.Count} projects.");
            return new ConfirmedEntities(confirmedPeople, confirmedProjects);
        }

        await stdout.WriteLineAsync($"\n{new string('\u2500', 58)}");
        await stdout.WriteLineAsync("  Options:");
        await stdout.WriteLineAsync("    [enter]  Accept all");
        await stdout.WriteLineAsync("    [edit]   Remove wrong entries or reclassify uncertain");
        await stdout.WriteLineAsync("    [add]    Add missing people or projects");
        await stdout.WriteLineAsync();

        var choice = (await PromptAsync(stdin, stdout, "  Your choice [enter/edit/add]: ", cancellationToken)).ToLowerInvariant();

        if (choice == "edit")
        {
            if (detected.Uncertain.Count > 0)
            {
                await stdout.WriteLineAsync("\n  Uncertain entities \u2014 classify each:");
                foreach (var entity in detected.Uncertain)
                {
                    var answer = (await PromptAsync(
                        stdin,
                        stdout,
                        $"    {entity.Name} \u2014 (p)erson, (r)roject, or (s)kip? ",
                        cancellationToken)).ToLowerInvariant();

                    if (answer == "p")
                    {
                        confirmedPeople.Add(entity.Name);
                    }
                    else if (answer == "r")
                    {
                        confirmedProjects.Add(entity.Name);
                    }
                }
            }

            await PrintSelectionAsync(stdout, confirmedPeople, "Current people");
            var removePeople = await PromptAsync(
                stdin,
                stdout,
                "  Numbers to REMOVE from people (comma-separated, or enter to skip): ",
                cancellationToken);
            confirmedPeople = RemoveSelected(confirmedPeople, removePeople);

            await PrintSelectionAsync(stdout, confirmedProjects, "Current projects");
            var removeProjects = await PromptAsync(
                stdin,
                stdout,
                "  Numbers to REMOVE from projects (comma-separated, or enter to skip): ",
                cancellationToken);
            confirmedProjects = RemoveSelected(confirmedProjects, removeProjects);
        }

        var addMissing = choice == "add";
        if (!addMissing)
        {
            var answer = (await PromptAsync(stdin, stdout, "\n  Add any missing? [y/N]: ", cancellationToken)).ToLowerInvariant();
            addMissing = answer == "y";
        }

        if (addMissing)
        {
            while (true)
            {
                var name = await PromptAsync(stdin, stdout, "  Name (or enter to stop): ", cancellationToken);
                if (string.IsNullOrWhiteSpace(name))
                {
                    break;
                }

                var kind = (await PromptAsync(
                    stdin,
                    stdout,
                    $"  Is '{name}' a (p)erson or p(r)oject? ",
                    cancellationToken)).ToLowerInvariant();
                if (kind == "p")
                {
                    confirmedPeople.Add(name);
                }
                else if (kind == "r")
                {
                    confirmedProjects.Add(name);
                }
            }
        }

        confirmedPeople = confirmedPeople
            .Distinct(StringComparer.Ordinal)
            .ToList();
        confirmedProjects = confirmedProjects
            .Distinct(StringComparer.Ordinal)
            .ToList();

        await stdout.WriteLineAsync($"\n{new string('=', 58)}");
        await stdout.WriteLineAsync("  Confirmed:");
        await stdout.WriteLineAsync($"  People:   {FormatEntitySummary(confirmedPeople)}");
        await stdout.WriteLineAsync($"  Projects: {FormatEntitySummary(confirmedProjects)}");
        await stdout.WriteLineAsync($"{new string('=', 58)}\n");

        return new ConfirmedEntities(confirmedPeople, confirmedProjects);
    }

    private static async Task PrintEntityListAsync(TextWriter stdout, IReadOnlyList<DetectedEntity> entities, string label)
    {
        await stdout.WriteLineAsync($"\n  {label}:");
        if (entities.Count == 0)
        {
            await stdout.WriteLineAsync("    (none detected)");
            return;
        }

        for (var index = 0; index < entities.Count; index++)
        {
            var entity = entities[index];
            var signals = entity.Signals.Count > 0
                ? string.Join(", ", entity.Signals.Take(2))
                : string.Empty;
            await stdout.WriteLineAsync(
                $"    {index + 1,2}. {entity.Name,-20} [{RenderConfidenceBar(entity.Confidence)}] {signals}".TrimEnd());
        }
    }

    private static async Task PrintSelectionAsync(TextWriter stdout, IReadOnlyList<string> values, string label)
    {
        await stdout.WriteLineAsync($"\n  {label}:");
        if (values.Count == 0)
        {
            await stdout.WriteLineAsync("    (none)");
            return;
        }

        for (var index = 0; index < values.Count; index++)
        {
            await stdout.WriteLineAsync($"    {index + 1,2}. {values[index]}");
        }
    }

    private static async Task PrintProposedStructureAsync(
        TextWriter stdout,
        string projectName,
        IReadOnlyList<RoomDefinition> rooms,
        int totalFiles,
        string source)
    {
        await stdout.WriteLineAsync($"\n{new string('=', 55)}");
        await stdout.WriteLineAsync("  MemShack Init \u2014 Local setup");
        await stdout.WriteLineAsync(new string('=', 55));
        await stdout.WriteLineAsync($"\n  WING: {projectName}");
        await stdout.WriteLineAsync($"  ({totalFiles} files found, rooms detected from {source})\n");

        foreach (var room in rooms)
        {
            await stdout.WriteLineAsync($"    ROOM: {room.Name}");
            await stdout.WriteLineAsync($"          {room.Description}");
        }

        await stdout.WriteLineAsync($"\n{new string('\u2500', 55)}");
    }

    private static async Task<IReadOnlyList<RoomDefinition>> ApproveRoomsAsync(
        IReadOnlyList<RoomDefinition> rooms,
        TextReader stdin,
        TextWriter stdout,
        CancellationToken cancellationToken)
    {
        await stdout.WriteLineAsync("  Review the proposed rooms above.");
        await stdout.WriteLineAsync("  Options:");
        await stdout.WriteLineAsync("    [enter]  Accept all rooms");
        await stdout.WriteLineAsync("    [edit]   Remove or rename rooms");
        await stdout.WriteLineAsync("    [add]    Add a room manually");
        await stdout.WriteLineAsync();

        var workingRooms = rooms.ToList();
        var choice = (await PromptAsync(stdin, stdout, "  Your choice [enter/edit/add]: ", cancellationToken)).ToLowerInvariant();

        if (choice == "edit")
        {
            await stdout.WriteLineAsync("\n  Current rooms:");
            for (var index = 0; index < workingRooms.Count; index++)
            {
                await stdout.WriteLineAsync($"    {index + 1}. {workingRooms[index].Name} \u2014 {workingRooms[index].Description}");
            }

            var remove = await PromptAsync(
                stdin,
                stdout,
                "\n  Room numbers to REMOVE (comma-separated, or enter to skip): ",
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(remove))
            {
                var toRemove = remove
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(value => int.TryParse(value, out var parsed) ? parsed - 1 : -1)
                    .Where(index => index >= 0)
                    .ToHashSet();

                workingRooms = workingRooms
                    .Where((_, index) => !toRemove.Contains(index))
                    .ToList();
            }
        }

        var addMissing = choice == "add";
        if (!addMissing)
        {
            var answer = (await PromptAsync(stdin, stdout, "\n  Add any missing rooms? [y/N]: ", cancellationToken)).ToLowerInvariant();
            addMissing = answer == "y";
        }

        if (addMissing)
        {
            while (true)
            {
                var newName = (await PromptAsync(stdin, stdout, "  New room name (or enter to stop): ", cancellationToken))
                    .ToLowerInvariant()
                    .Replace(" ", "_", StringComparison.Ordinal);
                if (string.IsNullOrWhiteSpace(newName))
                {
                    break;
                }

                var description = await PromptAsync(stdin, stdout, $"  Description for '{newName}': ", cancellationToken);
                workingRooms.Add(new RoomDefinition(newName, description, [newName]));
                await stdout.WriteLineAsync($"  Added: {newName}");
            }
        }

        return workingRooms;
    }

    private static string RenderConfidenceBar(double confidence)
    {
        var filled = Math.Clamp((int)(confidence * 5), 0, 5);
        return new string('\u25CF', filled) + new string('\u25CB', 5 - filled);
    }

    private static async Task<string> PromptAsync(
        TextReader stdin,
        TextWriter stdout,
        string prompt,
        CancellationToken cancellationToken)
    {
        await stdout.WriteAsync(prompt);
        await stdout.FlushAsync();
        var response = await stdin.ReadLineAsync(cancellationToken);
        return response?.Trim() ?? string.Empty;
    }

    private static List<string> RemoveSelected(IReadOnlyList<string> items, string rawIndexes)
    {
        if (string.IsNullOrWhiteSpace(rawIndexes))
        {
            return items.ToList();
        }

        var selected = rawIndexes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var parsed) ? parsed - 1 : -1)
            .Where(index => index >= 0)
            .ToHashSet();

        return items
            .Where((_, index) => !selected.Contains(index))
            .ToList();
    }

    private static string FormatEntitySummary(IReadOnlyList<string> values) =>
        values.Count == 0 ? "(none)" : string.Join(", ", values);

    private static IReadOnlyDictionary<string, string> BuildEntityCodes(
        IReadOnlyList<string> people,
        IReadOnlyList<string> projects)
    {
        var codes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in people.Concat(projects))
        {
            if (string.IsNullOrWhiteSpace(name) || codes.ContainsKey(name))
            {
                continue;
            }

            var normalized = name.Trim();
            var codeLength = Math.Min(normalized.Length, 3);
            var code = normalized[..codeLength].ToUpperInvariant();
            var index = Math.Min(normalized.Length, 4);

            while (codes.Values.Contains(code, StringComparer.Ordinal))
            {
                code = normalized[..Math.Min(normalized.Length, index)].ToUpperInvariant();
                index = Math.Min(normalized.Length, index + 1);
                if (index == normalized.Length && codes.Values.Contains(code, StringComparer.Ordinal))
                {
                    code = $"{code}{codes.Count + 1}";
                    break;
                }
            }

            codes[normalized] = code;
        }

        return codes;
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
        var store = CreateVectorStore(palacePath, stderr);
        var progressReporter = string.Equals(mode, "projects", StringComparison.Ordinal)
            ? null
            : CreateMiningProgressReporter(stdout);
        Action<MiningProgressUpdate>? progress = progressReporter is null ? null : progressReporter.Report;
        MiningRunResult result;
        try
        {
            result = string.Equals(mode, "convos", StringComparison.Ordinal)
                ? await new ConversationMiner(
                        new TranscriptNormalizer(new TranscriptSpellchecker(_configDirectory)),
                        new ConversationChunker(),
                        new GeneralMemoryExtractor(),
                        store)
                    .MineAsync(directory, wing, agent, limit, dryRun, extract, progress: progress, cancellationToken: cancellationToken)
                : await new ProjectMiner(
                        new YamlProjectPalaceConfigLoader(),
                        new ProjectScanner(),
                        new TextChunker(),
                        store)
                    .MineAsync(directory, wing, agent, limit, dryRun, !noGitignore, ExpandIncludeIgnored(includeIgnored), progress: progress, cancellationToken: cancellationToken);
        }
        finally
        {
            if (progressReporter is not null)
            {
                await progressReporter.ClearAsync();
            }
        }

        if (string.Equals(mode, "projects", StringComparison.Ordinal))
        {
            await WriteProjectMineOutputAsync(
                directory,
                wing,
                noGitignore,
                includeIgnored,
                palacePath,
                store,
                result,
                stdout);
            return 0;
        }

        await stdout.WriteLineAsync($"\n  MemShack Mine");
        await stdout.WriteLineAsync($"  Mode: {mode}");
        await stdout.WriteLineAsync($"  Palace: {palacePath}");
        await stdout.WriteLineAsync($"  Store: {DescribeVectorStore(store)}");
        if (store is ChromaCompatibilityVectorStore compatibilityStore)
        {
            var collectionPath = Path.Combine(compatibilityStore.CollectionsPath, $"{CollectionNames.Drawers}.json");
            await stdout.WriteLineAsync($"  Collection file: {collectionPath}");
        }
        else if (store is ChromaHttpVectorStore chromaStore)
        {
            await stdout.WriteLineAsync($"  Chroma URL: {chromaStore.BaseUrl}");
        }
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

    private async Task WriteProjectMineOutputAsync(
        string directory,
        string? wingOverride,
        bool noGitignore,
        IReadOnlyList<string> includeIgnored,
        string palacePath,
        IVectorStore store,
        MiningRunResult result,
        TextWriter stdout)
    {
        var config = new YamlProjectPalaceConfigLoader().Load(directory);
        var wing = string.IsNullOrWhiteSpace(wingOverride) ? config.Wing : wingOverride;

        await stdout.WriteLineAsync($"\n{new string('=', 55)}");
        await stdout.WriteLineAsync("  MemShack Mine");
        await stdout.WriteLineAsync(new string('=', 55));
        await stdout.WriteLineAsync($"  Wing:    {wing}");
        await stdout.WriteLineAsync($"  Rooms:   {string.Join(", ", config.Rooms.Select(room => room.Name))}");
        await stdout.WriteLineAsync($"  Files:   {result.FilesDiscovered}");
        await stdout.WriteLineAsync($"  Palace:  {palacePath}");
        await stdout.WriteLineAsync($"  Store:   {DescribeVectorStore(store)}");
        if (result.DryRun)
        {
            await stdout.WriteLineAsync("  DRY RUN - nothing will be filed");
        }

        if (noGitignore)
        {
            await stdout.WriteLineAsync("  .gitignore: DISABLED");
        }

        var included = ExpandIncludeIgnored(includeIgnored).ToArray();
        if (included.Length > 0)
        {
            await stdout.WriteLineAsync($"  Include: {string.Join(", ", included.OrderBy(value => value, StringComparer.Ordinal))}");
        }

        await stdout.WriteLineAsync(new string('\u2500', 55));
        await stdout.WriteLineAsync();

        if (result.DryRun)
        {
            foreach (var file in result.FileResults)
            {
                await stdout.WriteLineAsync(
                    $"    [DRY RUN] {Path.GetFileName(file.SourceFile)} -> room:{file.Room} ({file.DrawersFiled} drawers)");
            }
        }
        else
        {
            foreach (var file in result.FileResults)
            {
                var fileName = Path.GetFileName(file.SourceFile);
                if (fileName.Length > 50)
                {
                    fileName = fileName[..50];
                }

                await stdout.WriteLineAsync(
                    $"  \u2713 [{file.FileIndex,4}/{result.FilesDiscovered}] {fileName,-50} +{file.DrawersFiled}");
            }
        }

        await stdout.WriteLineAsync($"\n{new string('=', 55)}");
        await stdout.WriteLineAsync("  Done.");
        await stdout.WriteLineAsync($"  Files processed: {result.FilesProcessed}");
        await stdout.WriteLineAsync($"  Files skipped (already filed): {result.FilesSkipped}");
        await stdout.WriteLineAsync($"  Drawers filed: {result.DrawersFiled}");
        await stdout.WriteLineAsync("\n  By room:");
        foreach (var room in result.RoomCounts.OrderByDescending(entry => entry.Value).ThenBy(entry => entry.Key, StringComparer.Ordinal))
        {
            await stdout.WriteLineAsync($"    {room.Key,-20} {room.Value} files");
        }

        await stdout.WriteLineAsync($"\n  Next: {_commandName} search \"what you're looking for\"");
        await stdout.WriteLineAsync($"{new string('=', 55)}\n");
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
        var service = new MemorySearchService(CreateVectorStore(palacePath, stderr), palacePath);
        var result = await service.SearchMemoriesAsync(string.Join(' ', queryParts), wing, room, results, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            if (result.Error.StartsWith("No palace found at ", StringComparison.Ordinal))
            {
                await stderr.WriteLineAsync($"\n  {result.Error}");
                await stderr.WriteLineAsync($"  Run: {_commandName} init <dir> then {_commandName} mine <dir>");
            }
            else
            {
                await stderr.WriteLineAsync(result.Error);
            }

            return 1;
        }

        await stdout.WriteAsync(await service.FormatSearchAsync(result.Query, wing, room, results, cancellationToken));
        return 0;
    }

    private async Task<int> RunMigrateAsync(
        IReadOnlyList<string> args,
        GlobalOptions globalOptions,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var parser = new ArgumentParser(args);
        var dryRun = false;

        while (parser.TryReadNext(out var token))
        {
            switch (token)
            {
                case "--dry-run":
                    dryRun = true;
                    break;
                default:
                    throw new CliUsageException($"Usage: {_commandName} migrate [--dry-run]");
            }
        }

        var palacePath = ResolvePalacePath(globalOptions.PalacePath);
        var config = LoadConfigSnapshotForPalace(palacePath);
        var migrationService = new PalaceMigrationService(
            targetPalacePath => CreateVectorStore(config with { PalacePath = targetPalacePath }, stderr),
            async (targetPalacePath, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(config.ChromaUrl))
                {
                    return;
                }

                try
                {
                    new ChromaSidecarManager().Shutdown(config with { PalacePath = targetPalacePath });
                }
                catch (InvalidOperationException)
                {
                }

                await Task.CompletedTask;
            });

        PalaceMigrationResult result;
        try
        {
            await stdout.WriteLineAsync($"\n{new string('=', 60)}");
            await stdout.WriteLineAsync("  MemShack Migrate");
            await stdout.WriteLineAsync(new string('=', 60));
            result = await migrationService.MigrateAsync(
                palacePath,
                config.CollectionName,
                dryRun,
                progress: progress =>
                {
                    if (dryRun)
                    {
                        return;
                    }

                    if (progress.DrawersImported == progress.TotalDrawers ||
                        progress.DrawersImported == 1 ||
                        progress.DrawersImported % 500 == 0)
                    {
                        stdout.WriteLine($"  Imported {progress.DrawersImported}/{progress.TotalDrawers} drawers...");
                    }
                },
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            await stderr.WriteLineAsync(exception.Message);
            return 1;
        }

        var dbSizeBytes = new FileInfo(result.DatabasePath).Length;
        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync($"  Palace:    {result.PalacePath}");
        await stdout.WriteLineAsync($"  Database:  {result.DatabasePath}");
        await stdout.WriteLineAsync($"  DB size:   {FormatMegabytes(dbSizeBytes):F1} MB");
        await stdout.WriteLineAsync($"  Source:    ChromaDB {result.SourceVersion}");
        await stdout.WriteLineAsync($"  Target:    {DescribeVectorStore(CreateVectorStore(config, stderr))}");
        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync($"  Extracted {result.DrawersExtracted} drawers from SQLite");

        if (result.Wings.Count > 0)
        {
            await stdout.WriteLineAsync("\n  Summary:");
            foreach (var wing in result.Wings)
            {
                await stdout.WriteLineAsync($"    WING: {wing.Wing} ({wing.DrawerCount} drawers)");
                foreach (var room in wing.Rooms)
                {
                    await stdout.WriteLineAsync($"      ROOM: {room.Room,-30} {room.DrawerCount,5}");
                }
            }
        }

        if (result.DryRun)
        {
            await stdout.WriteLineAsync("\n  DRY RUN — no changes made.");
            await stdout.WriteLineAsync($"  Would migrate {result.DrawersExtracted} drawers.");
            await stdout.WriteLineAsync($"{new string('=', 60)}\n");
            return 0;
        }

        await stdout.WriteLineAsync("\n  Migration complete.");
        await stdout.WriteLineAsync($"  Drawers migrated: {result.DrawersImported}");
        if (!string.IsNullOrWhiteSpace(result.BackupPath))
        {
            await stdout.WriteLineAsync($"  Backup at: {result.BackupPath}");
        }

        if (result.DrawersImported != result.DrawersExtracted)
        {
            await stdout.WriteLineAsync($"  WARNING: Expected {result.DrawersExtracted}, got {result.DrawersImported}");
        }

        await stdout.WriteLineAsync($"\n{new string('=', 60)}\n");
        return 0;
    }

    private async Task<int> RunDedupAsync(
        IReadOnlyList<string> args,
        GlobalOptions globalOptions,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var parser = new ArgumentParser(args);
        var threshold = DuplicateCleanupService.DefaultSimilarityThreshold;
        var dryRun = false;
        var statsOnly = false;
        string? wing = null;
        string? sourcePattern = null;

        while (parser.TryReadNext(out var token))
        {
            switch (token)
            {
                case "--threshold":
                    threshold = double.Parse(parser.RequireValue(token), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--stats":
                    statsOnly = true;
                    break;
                case "--wing":
                    wing = parser.RequireValue(token);
                    break;
                case "--source":
                    sourcePattern = parser.RequireValue(token);
                    break;
                default:
                    throw new CliUsageException($"Usage: {_commandName} dedup [--dry-run] [--threshold <0-1>] [--stats] [--wing <name>] [--source <pattern>]");
            }
        }

        threshold = Math.Clamp(threshold, 0d, 1d);

        var palacePath = ResolvePalacePath(globalOptions.PalacePath);
        var store = CreateVectorStore(palacePath, stderr);
        var collections = await store.ListCollectionsAsync(cancellationToken);
        if (!collections.Contains(CollectionNames.Drawers, StringComparer.Ordinal))
        {
            await stderr.WriteLineAsync($"\n  No palace found at {palacePath}");
            await stderr.WriteLineAsync($"  Run: {_commandName} init <dir> then {_commandName} mine <dir>");
            return 1;
        }

        var service = new DuplicateCleanupService(store);

        await stdout.WriteLineAsync($"\n{new string('=', 55)}");
        await stdout.WriteLineAsync("  MemShack Dedup");
        await stdout.WriteLineAsync(new string('=', 55));
        await stdout.WriteLineAsync($"  Palace: {palacePath}");
        await stdout.WriteLineAsync($"  Store: {DescribeVectorStore(store)}");

        if (wing is not null)
        {
            await stdout.WriteLineAsync($"  Wing: {wing}");
        }

        if (sourcePattern is not null)
        {
            await stdout.WriteLineAsync($"  Source filter: {sourcePattern}");
        }

        if (statsOnly)
        {
            var stats = await service.GetStatsAsync(CollectionNames.Drawers, wing, sourcePattern, cancellationToken: cancellationToken);
            await stdout.WriteLineAsync($"  Sources with {DuplicateCleanupService.DefaultMinimumGroupSize}+ drawers: {stats.SourceGroupCount}");
            await stdout.WriteLineAsync($"  Total drawers in those sources: {stats.DrawersInCandidateGroups:N0}");
            await stdout.WriteLineAsync("\n  Top 15 by drawer count:");
            foreach (var group in stats.LargestGroups)
            {
                await stdout.WriteLineAsync($"    {group.DrawerCount,4}  {group.SourceFile}");
            }

            await stdout.WriteLineAsync($"\n  Estimated duplicates (groups > 20): ~{stats.EstimatedDuplicates:N0}");
            await stdout.WriteLineAsync($"{new string('=', 55)}\n");
            return 0;
        }

        await stdout.WriteLineAsync($"  Threshold: {threshold:F2} similarity");
        await stdout.WriteLineAsync($"  Mode: {(dryRun ? "DRY RUN" : "LIVE")}");
        await stdout.WriteLineAsync(new string('\u2500', 55));

        var result = await service.DeduplicateAsync(
            CollectionNames.Drawers,
            threshold,
            dryRun,
            wing,
            sourcePattern,
            cancellationToken: cancellationToken);

        await stdout.WriteLineAsync($"\n  Sources to check: {result.SourceGroupCount}");
        foreach (var group in result.GroupResults.Where(group => group.DeletedCount > 0))
        {
            await stdout.WriteLineAsync(
                $"  {group.SourceFile,-50} {group.OriginalCount,4} \u2192 {group.KeptCount,4}  (-{group.DeletedCount})");
        }

        if (result.GroupResults.All(group => group.DeletedCount == 0))
        {
            await stdout.WriteLineAsync("  No near-duplicate drawers met the current threshold.");
        }

        await stdout.WriteLineAsync($"\n{new string('\u2500', 55)}");
        await stdout.WriteLineAsync(
            $"  Drawers: {result.TotalFilteredDrawers:N0} \u2192 {result.TotalFilteredDrawers - result.DeletedCount:N0}  (-{result.DeletedCount:N0} removed)");
        await stdout.WriteLineAsync($"  Palace after: {result.TotalDrawersAfter:N0} drawers");
        await stdout.WriteLineAsync("  Threshold semantics: similarity 0-1 (higher = stricter).");
        await stdout.WriteLineAsync("  This differs from upstream Python dedup, which documents Chroma cosine distance.");

        if (dryRun)
        {
            await stdout.WriteLineAsync("\n  [DRY RUN] No changes written. Re-run without --dry-run to apply.");
        }

        await stdout.WriteLineAsync($"{new string('=', 55)}\n");
        return 0;
    }

    private async Task<int> RunHookAsync(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parser = new ArgumentParser(args);
        string? outputDirectory = null;

        while (parser.TryReadNext(out var token))
        {
            switch (token)
            {
                case "--output-dir":
                    outputDirectory = parser.RequireValue(token);
                    break;
                default:
                    throw new CliUsageException($"Usage: {_commandName} hook [--output-dir <dir>]");
            }
        }

        var assetDirectory = ResolveAssetDirectory("hooks");
        if (assetDirectory is null)
        {
            await stderr.WriteLineAsync("Could not find packaged MemShack hook assets.");
            return 1;
        }

        var materializedDirectory = string.IsNullOrWhiteSpace(outputDirectory)
            ? assetDirectory
            : ExportAssetDirectory(assetDirectory, outputDirectory);
        var saveHookPath = Path.Combine(materializedDirectory, "memshack_save_hook.sh");
        var precompactHookPath = Path.Combine(materializedDirectory, "memshack_precompact_hook.sh");
        var saveHookCommand = BuildBashHookCommand(saveHookPath);
        var precompactHookCommand = BuildBashHookCommand(precompactHookPath);

        await stdout.WriteLineAsync("MemShack hook setup:");
        await stdout.WriteLineAsync($"  Hook assets: {materializedDirectory}");
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            await stdout.WriteLineAsync("  Exported the hook files above so you can point your editor at a stable path.");
        }

        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync("  Claude Code (.claude/settings.local.json):");
        await stdout.WriteLineAsync("  {");
        await stdout.WriteLineAsync("    \"hooks\": {");
        await stdout.WriteLineAsync("      \"Stop\": [{");
        await stdout.WriteLineAsync("        \"matcher\": \"*\",");
        await stdout.WriteLineAsync("        \"hooks\": [{");
        await stdout.WriteLineAsync("          \"type\": \"command\",");
        await stdout.WriteLineAsync($"          \"command\": {JsonSerializer.Serialize(saveHookCommand)},");
        await stdout.WriteLineAsync("          \"timeout\": 30");
        await stdout.WriteLineAsync("        }]");
        await stdout.WriteLineAsync("      }],");
        await stdout.WriteLineAsync("      \"PreCompact\": [{");
        await stdout.WriteLineAsync("        \"hooks\": [{");
        await stdout.WriteLineAsync("          \"type\": \"command\",");
        await stdout.WriteLineAsync($"          \"command\": {JsonSerializer.Serialize(precompactHookCommand)},");
        await stdout.WriteLineAsync("          \"timeout\": 30");
        await stdout.WriteLineAsync("        }]");
        await stdout.WriteLineAsync("      }]");
        await stdout.WriteLineAsync("    }");
        await stdout.WriteLineAsync("  }");

        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync("  Codex CLI (.codex/hooks.json):");
        await stdout.WriteLineAsync("  {");
        await stdout.WriteLineAsync("    \"Stop\": [{");
        await stdout.WriteLineAsync("      \"type\": \"command\",");
        await stdout.WriteLineAsync($"      \"command\": {JsonSerializer.Serialize(saveHookCommand)},");
        await stdout.WriteLineAsync("      \"timeout\": 30");
        await stdout.WriteLineAsync("    }],");
        await stdout.WriteLineAsync("    \"PreCompact\": [{");
        await stdout.WriteLineAsync("      \"type\": \"command\",");
        await stdout.WriteLineAsync($"      \"command\": {JsonSerializer.Serialize(precompactHookCommand)},");
        await stdout.WriteLineAsync("      \"timeout\": 30");
        await stdout.WriteLineAsync("    }]");
        await stdout.WriteLineAsync("  }");

        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync("  Notes:");
        await stdout.WriteLineAsync("  - These are Bash hook scripts. On Windows, run them through Git Bash or WSL.");
        await stdout.WriteLineAsync($"  - If you prefer tool-only integration, use: {_commandName} mcp");
        await stdout.WriteLineAsync($"  - See {Path.Combine(materializedDirectory, "README.md")} for details.");
        return 0;
    }

    private async Task<int> RunInstructionsAsync(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parser = new ArgumentParser(args);
        string? outputDirectory = null;

        while (parser.TryReadNext(out var token))
        {
            switch (token)
            {
                case "--output-dir":
                    outputDirectory = parser.RequireValue(token);
                    break;
                default:
                    throw new CliUsageException($"Usage: {_commandName} instructions [--output-dir <dir>]");
            }
        }

        var assetDirectory = ResolveAssetDirectory("instructions");
        if (assetDirectory is null)
        {
            await stderr.WriteLineAsync("Could not find packaged MemShack instruction assets.");
            return 1;
        }

        var materializedDirectory = string.IsNullOrWhiteSpace(outputDirectory)
            ? assetDirectory
            : ExportAssetDirectory(assetDirectory, outputDirectory);

        await stdout.WriteLineAsync("MemShack instructions setup:");
        await stdout.WriteLineAsync($"  Instruction assets: {materializedDirectory}");
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            await stdout.WriteLineAsync("  Exported the instruction files above so you can copy them into your editor/tool setup.");
        }

        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync("  Files:");
        await stdout.WriteLineAsync($"  - {Path.Combine(materializedDirectory, "README.md")}");
        await stdout.WriteLineAsync($"  - {Path.Combine(materializedDirectory, "codex.md")}");
        await stdout.WriteLineAsync($"  - {Path.Combine(materializedDirectory, "claude-code.md")}");
        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync($"  Use {_commandName} wake-up for runtime context and {_commandName} mcp for tool integration.");
        await stdout.WriteLineAsync("  These instruction assets document the repo-local setup and the current Bash-hook/plugin limitations.");
        return 0;
    }

    private async Task<int> RunMcpAsync(
        IReadOnlyList<string> args,
        GlobalOptions globalOptions,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (args.Count > 0)
        {
            throw new CliUsageException($"Usage: {_commandName} mcp");
        }

        var palacePath = globalOptions.PalacePath;
        var projectPath = TryResolveMcpServerProjectPath();
        var baseCommand = projectPath is null
            ? "dotnet run --project src/MemShack.McpServer --"
            : $"dotnet run --project {QuoteCommandArgument(projectPath)} --";
        var fullCommand = string.IsNullOrWhiteSpace(palacePath)
            ? baseCommand
            : $"{baseCommand} --palace {QuoteCommandArgument(Path.GetFullPath(PathUtilities.ExpandHome(palacePath)))}";

        await stdout.WriteLineAsync("MemShack MCP quick setup:");
        await stdout.WriteLineAsync($"  claude mcp add mempalace -- {fullCommand}");
        await stdout.WriteLineAsync();
        await stdout.WriteLineAsync("Run the server directly:");
        await stdout.WriteLineAsync($"  {fullCommand}");

        var openClawAssetDirectory = ResolveAssetDirectory(Path.Combine("integrations", "openclaw"));
        if (openClawAssetDirectory is not null)
        {
            await stdout.WriteLineAsync();
            await stdout.WriteLineAsync($"OpenClaw / ClawHub skill asset: {Path.Combine(openClawAssetDirectory, "SKILL.md")}");
        }

        if (projectPath is null)
        {
            await stdout.WriteLineAsync();
            await stdout.WriteLineAsync("Note: run this from the MemShack repo root so the relative project path resolves.");
        }

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
        var store = CreateVectorStore(palacePath, stderr);
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
        var stack = new MemoryStack(CreateVectorStore(palacePath, stderr), palacePath, ResolveIdentityPath());
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
        var store = CreateVectorStore(palacePath, stderr);
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
        await stdout.WriteLineAsync($"  Store: {DescribeVectorStore(store)}");
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

    private async Task<int> RunShutdownDbAsync(
        IReadOnlyList<string> args,
        GlobalOptions globalOptions,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (args.Count > 0)
        {
            throw new CliUsageException($"Usage: {_commandName} shutdowndb");
        }

        var palacePath = ResolvePalacePath(globalOptions.PalacePath);
        var config = _configStore.Load(_configDirectory) with { PalacePath = palacePath };
        await stdout.WriteLineAsync($"\n{new string('=', 55)}");
        await stdout.WriteLineAsync("  MemShack ShutdownDb");
        await stdout.WriteLineAsync($"{new string('=', 55)}");
        await stdout.WriteLineAsync($"  Palace: {palacePath}");

        if (!string.IsNullOrWhiteSpace(config.ChromaUrl))
        {
            await stdout.WriteLineAsync($"  Chroma URL: {config.ChromaUrl}");
            await stdout.WriteLineAsync("  Configured to use an external Chroma server. 'shutdowndb' only stops managed local Chroma sidecars.");
            await stdout.WriteLineAsync($"{new string('=', 55)}\n");
            return 0;
        }

        ChromaSidecarShutdownResult result;
        try
        {
            result = new ChromaSidecarManager().Shutdown(config);
        }
        catch (InvalidOperationException exception)
        {
            throw new CliUsageException(exception.Message);
        }

        if (!string.IsNullOrWhiteSpace(result.BaseUrl))
        {
            await stdout.WriteLineAsync($"  Chroma URL: {result.BaseUrl}");
        }

        if (result.ProcessId is int processId && processId > 0)
        {
            await stdout.WriteLineAsync($"  Process: {processId}");
        }

        if (!result.HadRecordedSidecar)
        {
            if (result.UsedProcessDiscovery && result.MatchingProcessCount > 1)
            {
                await stdout.WriteLineAsync($"  Found {result.MatchingProcessCount} managed Chroma processes for this installation and could not safely choose which one belongs to this palace.");
                await stdout.WriteLineAsync("  Stop them manually or rerun after narrowing the active palaces.");
                await stdout.WriteLineAsync($"{new string('=', 55)}\n");
                return 1;
            }

            if (result.UsedProcessDiscovery && result.WasRunning)
            {
                await stdout.WriteLineAsync("  Managed Chroma sidecar stopped after recovering it from the installed binary path.");
                await stdout.WriteLineAsync($"{new string('=', 55)}\n");
                return 0;
            }

            await stdout.WriteLineAsync("  No managed Chroma sidecar is recorded for this palace.");
            await stdout.WriteLineAsync($"{new string('=', 55)}\n");
            return 0;
        }

        await stdout.WriteLineAsync(
            result.WasRunning
                ? "  Managed Chroma sidecar stopped."
                : "  Managed Chroma sidecar was already stopped. Cleared the recorded sidecar state.");
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
        var store = CreateVectorStore(palacePath, stderr);
        var collections = await store.ListCollectionsAsync(cancellationToken);
        if (!collections.Contains(CollectionNames.Drawers, StringComparer.Ordinal))
        {
            await stdout.WriteLineAsync($"\n  No palace found at {palacePath}");
            return 0;
        }

        var backupPath = $"{palacePath}.backup";
        if (store is ChromaCompatibilityVectorStore)
        {
            if (Directory.Exists(backupPath))
            {
                Directory.Delete(backupPath, recursive: true);
            }

            CopyDirectory(palacePath, backupPath);
        }

        await stdout.WriteLineAsync($"\n  MemShack Repair");
        await stdout.WriteLineAsync($"  Palace: {palacePath}");
        await stdout.WriteLineAsync($"  Store: {DescribeVectorStore(store)}");
        if (store is ChromaCompatibilityVectorStore)
        {
            await stdout.WriteLineAsync($"  Backup: {backupPath}");
        }
        else if (store is ChromaHttpVectorStore chromaStore)
        {
            await stdout.WriteLineAsync($"  Chroma URL: {chromaStore.BaseUrl}");
            await stdout.WriteLineAsync("  Backup: not available for HTTP-backed Chroma stores");
        }

        foreach (var collection in collections)
        {
            var drawers = await store.GetDrawersAsync(collection, cancellationToken: cancellationToken);
            await store.EnsureCollectionAsync(collection, cancellationToken);
            foreach (var drawer in drawers)
            {
                await store.DeleteDrawerAsync(collection, drawer.Id, cancellationToken);
            }

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

    private static async Task<int> RunCountHumanMessagesAsync(
        IReadOnlyList<string> args,
        TextWriter stdout,
        TextWriter stderr)
    {
        if (args.Count != 1)
        {
            await stderr.WriteLineAsync("Usage: mems __count-human-messages <transcript-path>");
            return 1;
        }

        await stdout.WriteLineAsync(HookTranscriptCounter.CountHumanMessages(args[0]).ToString(System.Globalization.CultureInfo.InvariantCulture));
        return 0;
    }

    private static async Task<int> RunWhereChromaAsync(TextWriter stdout)
    {
        var candidate = ChromaSidecarManager.GetBundledBinaryCandidatePath(AppContext.BaseDirectory);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return await WriteAndReturnAsync(stdout, "unsupported-platform", 0);
        }

        await stdout.WriteLineAsync(candidate);
        return 0;
    }

    private static async Task<int> WriteAndReturnAsync(TextWriter writer, string text, int code)
    {
        await writer.WriteLineAsync(text);
        return code;
    }

    private static IEnumerable<string> ExpandIncludeIgnored(IEnumerable<string> rawValues) =>
        rawValues.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string? TryResolveMcpServerProjectPath()
    {
        var repoRoot = TryResolveRepoRootPath();
        if (repoRoot is null)
        {
            return null;
        }

        var candidate = Path.Combine(repoRoot, "src", "MemShack.McpServer", "MemShack.McpServer.csproj");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string QuoteCommandArgument(string value) =>
        value.IndexOfAny([' ', '\t', '"']) >= 0
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;

    private static string? ResolveAssetDirectory(string assetDirectoryName)
    {
        var packagedCandidate = Path.Combine(Path.GetFullPath(AppContext.BaseDirectory), assetDirectoryName);
        if (Directory.Exists(packagedCandidate))
        {
            return packagedCandidate;
        }

        var repoRoot = TryResolveRepoRootPath();
        if (repoRoot is null)
        {
            return null;
        }

        var repoCandidate = Path.Combine(repoRoot, assetDirectoryName);
        return Directory.Exists(repoCandidate) ? repoCandidate : null;
    }

    private static string ExportAssetDirectory(string sourceDirectory, string outputDirectory)
    {
        var destination = Path.GetFullPath(PathUtilities.ExpandHome(outputDirectory));
        Directory.CreateDirectory(destination);
        CopyDirectory(sourceDirectory, destination);
        return destination;
    }

    private static string BuildBashHookCommand(string scriptPath)
    {
        var normalizedPath = Path.GetFullPath(scriptPath);
        return $"bash {QuoteCommandArgument(normalizedPath)}";
    }

    private static string? TryResolveRepoRootPath()
    {
        var current = new DirectoryInfo(Path.GetFullPath(AppContext.BaseDirectory));
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "MemShack.Cli", "MemShack.Cli.csproj");
            if (File.Exists(candidate))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private MempalaceConfigSnapshot LoadConfigSnapshotForPalace(string palacePath) =>
        _configStore.Load(_configDirectory) with { PalacePath = palacePath };

    private IVectorStore CreateVectorStore(string palacePath, TextWriter? progressWriter = null) =>
        CreateVectorStore(LoadConfigSnapshotForPalace(palacePath), progressWriter);

    private IVectorStore CreateVectorStore(MempalaceConfigSnapshot config, TextWriter? progressWriter = null)
    {
        try
        {
            var progress = progressWriter is null ? null : CreateChromaDownloadProgressReporter(progressWriter);
            return VectorStoreFactory.Create(config, _chromaSidecarManagerFactory(progress));
        }
        catch (InvalidOperationException exception)
        {
            throw new CliUsageException(exception.Message);
        }
    }

    private static Action<ChromaDownloadProgress> CreateChromaDownloadProgressReporter(TextWriter writer)
    {
        var reporter = new ChromaDownloadProgressReporter(writer);
        return reporter.Report;
    }

    private static MiningProgressReporter? CreateMiningProgressReporter(TextWriter writer)
    {
        if (!ReferenceEquals(writer, Console.Out) || Console.IsOutputRedirected)
        {
            return null;
        }

        return new MiningProgressReporter(writer);
    }

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

    private static string DescribeVectorStore(IVectorStore store) =>
        store switch
        {
            ChromaHttpVectorStore chromaStore when IsLoopbackAddress(chromaStore.BaseUrl) => "managed local Chroma",
            ChromaHttpVectorStore chromaStore => $"external Chroma ({chromaStore.BaseUrl})",
            ChromaCompatibilityVectorStore => "legacy compatibility JSON",
            _ => store.GetType().Name,
        };

    private static bool IsLoopbackAddress(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Equals("127.0.0.1", StringComparison.Ordinal) ||
               uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

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

    private static double FormatMegabytes(long bytes) => bytes / 1024d / 1024d;

    private sealed class MiningProgressReporter
    {
        private const string ClearLine = "\u001b[2K\r";
        private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(100);
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly TextWriter _writer;
        private TimeSpan _lastRender = TimeSpan.MinValue;
        private bool _rendered;

        public MiningProgressReporter(TextWriter writer)
        {
            _writer = writer;
        }

        public void Report(MiningProgressUpdate update)
        {
            if (!_ShouldRender(update))
            {
                return;
            }

            var message = $"{ClearLine}  {update.FilesProcessed}/{update.FilesDiscovered} files processed";
            if (update.DrawersFiled > 0)
            {
                message += $" | {update.DrawersFiled} drawers filed";
            }

            if (update.FilesSkipped > 0 && !update.DryRun)
            {
                message += $" | {update.FilesSkipped} skipped";
            }

            _writer.Write(message);
            _writer.Flush();
            _lastRender = _stopwatch.Elapsed;
            _rendered = true;
        }

        public async Task ClearAsync()
        {
            if (!_rendered)
            {
                return;
            }

            await _writer.WriteAsync(ClearLine);
            await _writer.FlushAsync();
        }

        private bool _ShouldRender(MiningProgressUpdate update)
        {
            if (update.FilesProcessed >= update.FilesDiscovered)
            {
                return true;
            }

            if (!_rendered)
            {
                return true;
            }

            return _stopwatch.Elapsed - _lastRender >= RenderInterval;
        }
    }

    private sealed class ChromaDownloadProgressReporter
    {
        private const string ClearLine = "\u001b[2K\r";
        private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(100);
        private readonly bool _interactive;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly TextWriter _writer;
        private TimeSpan _lastRender = TimeSpan.MinValue;
        private bool _started;

        public ChromaDownloadProgressReporter(TextWriter writer)
        {
            _writer = writer;
            _interactive =
                (ReferenceEquals(writer, Console.Error) && !Console.IsErrorRedirected) ||
                (ReferenceEquals(writer, Console.Out) && !Console.IsOutputRedirected);
        }

        public void Report(ChromaDownloadProgress progress)
        {
            if (_interactive)
            {
                ReportInteractive(progress);
                return;
            }

            ReportPlain(progress);
        }

        private void ReportInteractive(ChromaDownloadProgress progress)
        {
            if (!ShouldRender(progress))
            {
                return;
            }

            var message = $"{ClearLine}  Downloading Chroma: {FormatBytes(progress.BytesDownloaded)}";
            if (progress.TotalBytes is long totalBytes && totalBytes > 0)
            {
                var percent = Math.Clamp((int)Math.Round(progress.BytesDownloaded * 100d / totalBytes), 0, 100);
                message += $" / {FormatBytes(totalBytes)} ({percent}%)";
            }
            else
            {
                message += " downloaded";
            }

            if (progress.IsCompleted)
            {
                message += " - done";
            }

            _writer.Write(message);
            if (progress.IsCompleted)
            {
                _writer.WriteLine();
            }

            _writer.Flush();
            _lastRender = _stopwatch.Elapsed;
            _started = true;
        }

        private void ReportPlain(ChromaDownloadProgress progress)
        {
            if (!_started)
            {
                _writer.WriteLine($"  Downloading Chroma ({progress.AssetName})...");
                _writer.Flush();
                _started = true;
            }

            if (!progress.IsCompleted)
            {
                return;
            }

            var completionLine = progress.TotalBytes is long totalBytes && totalBytes > 0
                ? $"  Downloaded Chroma: {FormatBytes(progress.BytesDownloaded)} / {FormatBytes(totalBytes)}"
                : $"  Downloaded Chroma: {FormatBytes(progress.BytesDownloaded)}";
            _writer.WriteLine(completionLine);
            _writer.Flush();
        }

        private bool ShouldRender(ChromaDownloadProgress progress)
        {
            if (progress.IsCompleted)
            {
                return true;
            }

            if (!_started)
            {
                return true;
            }

            return _stopwatch.Elapsed - _lastRender >= RenderInterval;
        }

        private static string FormatBytes(long bytes)
        {
            const double kilo = 1024d;
            const double mega = kilo * 1024d;
            const double giga = mega * 1024d;

            if (bytes >= giga)
            {
                return $"{bytes / giga:F1} GB";
            }

            if (bytes >= mega)
            {
                return $"{bytes / mega:F1} MB";
            }

            if (bytes >= kilo)
            {
                return $"{bytes / kilo:F1} KB";
            }

            return $"{bytes} B";
        }
    }

    private sealed record ConfirmedEntities(IReadOnlyList<string> People, IReadOnlyList<string> Projects)
    {
        public bool HasAny => People.Count > 0 || Projects.Count > 0;
    }

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
  {{_commandName}} migrate [--dry-run]
  {{_commandName}} dedup [--dry-run] [--threshold <0-1>] [--stats] [--wing <name>]
                    [--source <pattern>]
  {{_commandName}} search <query> [--wing <name>] [--room <name>] [--results <n>]
  {{_commandName}} hook
  {{_commandName}} instructions
  {{_commandName}} mcp
  {{_commandName}} compress [--wing <name>] [--dry-run] [--config <path>]
  {{_commandName}} wake-up [--wing <name>]
  {{_commandName}} shutdowndb
  {{_commandName}} repair
  {{_commandName}} status

Global options:
  --palace <path>    Override the palace path
""";
}
