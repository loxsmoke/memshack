using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MemShack.Core.Constants;
using MemShack.Core.Models;
using MemShack.Core.Utilities;

namespace MemShack.Infrastructure.VectorStore;

public sealed class ChromaSidecarManager
{
    private const string ChromaInstallScriptUnixUrl = "https://raw.githubusercontent.com/chroma-core/chroma/main/rust/cli/install/install.sh";
    private const string ChromaInstallScriptWindowsUrl = "https://raw.githubusercontent.com/chroma-core/chroma/main/rust/cli/install/install.ps1";
    private const string ChromaReleaseDownloadUrlFormat = "https://github.com/chroma-core/chroma/releases/download/{0}/{1}";
    private static readonly Regex ReleasePattern = new(@"(?im)(?:RELEASE|\$release)\s*=\s*[""'](?<release>[^""']+)[""']", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _applicationBaseDirectory;
    private readonly HttpClient _downloadHttpClient;
    private readonly HttpClient _httpClient;
    private readonly Action<ChromaDownloadProgress>? _downloadProgress;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly Func<IReadOnlyList<ChromaRunningProcess>> _listRunningChromaProcesses;
    private readonly Func<int> _portAllocator;
    private readonly Func<ProcessStartInfo, IChromaSidecarProcess> _processStarter;
    private readonly Func<int, bool> _tryKillProcessById;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _startupTimeout;

    public ChromaSidecarManager(
        string? applicationBaseDirectory = null,
        HttpClient? httpClient = null,
        HttpClient? downloadHttpClient = null,
        Func<ProcessStartInfo, IChromaSidecarProcess>? processStarter = null,
        Func<int>? portAllocator = null,
        Func<int, bool>? isProcessAlive = null,
        Func<IReadOnlyList<ChromaRunningProcess>>? listRunningChromaProcesses = null,
        Func<int, bool>? tryKillProcessById = null,
        Action<ChromaDownloadProgress>? downloadProgress = null,
        TimeSpan? startupTimeout = null,
        TimeSpan? pollInterval = null)
    {
        _applicationBaseDirectory = Path.GetFullPath(applicationBaseDirectory ?? AppContext.BaseDirectory);
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        _downloadHttpClient = downloadHttpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _downloadProgress = downloadProgress;
        _processStarter = processStarter ?? StartProcess;
        _portAllocator = portAllocator ?? GetFreePort;
        _isProcessAlive = isProcessAlive ?? IsProcessAlive;
        _listRunningChromaProcesses = listRunningChromaProcesses ?? ListRunningChromaProcesses;
        _tryKillProcessById = tryKillProcessById ?? TryKillProcessById;
        _startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(30);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);
    }

    public string? TryGetOrStart(MempalaceConfigSnapshot config)
    {
        var dataPath = Path.GetFullPath(PathUtilities.ExpandHome(config.PalacePath));
        Directory.CreateDirectory(dataPath);

        using var lockStream = AcquireLock(dataPath);
        var statePath = Path.Combine(dataPath, ConfigFileNames.ChromaSidecarStateJson);
        var state = LoadState(statePath);

        if (state is not null &&
            PathEquals(state.DataPath, dataPath) &&
            IsHealthyAsync(state.BaseUrl).GetAwaiter().GetResult())
        {
            return state.BaseUrl;
        }

        if (state is not null && state.ProcessId > 0 && _isProcessAlive(state.ProcessId))
        {
            throw new InvalidOperationException(
                $"The Chroma sidecar for '{dataPath}' is recorded as PID {state.ProcessId} at {state.BaseUrl}, but it is not responding.");
        }

        var explicitBinaryPath = ResolveConfiguredBinaryPath(config.ChromaBinaryPath);
        if (explicitBinaryPath is not null && !File.Exists(explicitBinaryPath))
        {
            throw new InvalidOperationException($"Configured Chroma binary was not found: {explicitBinaryPath}");
        }

        var binaryPath = explicitBinaryPath
            ?? ResolveBundledBinaryPath(_applicationBaseDirectory)
            ?? ResolveBinaryFromPath()
            ?? EnsureManagedBinaryInstalled(config);
        if (string.IsNullOrWhiteSpace(binaryPath))
        {
            return null;
        }

        var port = state is not null && state.Port > 0 && IsPortAvailable(state.Port)
            ? state.Port
            : _portAllocator();
        var baseUrl = $"http://127.0.0.1:{port}";
        var process = _processStarter(CreateStartInfo(binaryPath, dataPath, port));

        try
        {
            WaitForHealthyAsync(baseUrl, process).GetAwaiter().GetResult();
            File.WriteAllText(
                statePath,
                JsonSerializer.Serialize(
                    new ChromaSidecarState(baseUrl, port, process.Id, binaryPath, dataPath, DateTimeOffset.UtcNow),
                    StateJsonOptions));
            return baseUrl;
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    public ChromaSidecarShutdownResult Shutdown(MempalaceConfigSnapshot config)
    {
        var dataPath = Path.GetFullPath(PathUtilities.ExpandHome(config.PalacePath));
        if (!Directory.Exists(dataPath))
        {
            return new ChromaSidecarShutdownResult(dataPath, null, null, WasRunning: false, HadRecordedSidecar: false);
        }

        using var lockStream = AcquireLock(dataPath);
        var statePath = Path.Combine(dataPath, ConfigFileNames.ChromaSidecarStateJson);
        var state = LoadState(statePath);
        if (state is null)
        {
            var discoveredProcesses = DiscoverManagedProcesses(config);
            if (discoveredProcesses.Count == 1)
            {
                var discoveredProcess = discoveredProcesses[0];
                var discoveredWasRunning = StopProcessAndWait(discoveredProcess.ProcessId);
                return new ChromaSidecarShutdownResult(
                    dataPath,
                    null,
                    discoveredProcess.ProcessId,
                    WasRunning: discoveredWasRunning,
                    HadRecordedSidecar: false,
                    UsedProcessDiscovery: true,
                    MatchingProcessCount: 1);
            }

            return new ChromaSidecarShutdownResult(
                dataPath,
                null,
                null,
                WasRunning: false,
                HadRecordedSidecar: false,
                UsedProcessDiscovery: discoveredProcesses.Count > 0,
                MatchingProcessCount: discoveredProcesses.Count);
        }

        var wasRunning = StopProcessAndWait(state.ProcessId);
        TryDeleteFile(statePath);
        return new ChromaSidecarShutdownResult(
            dataPath,
            state.BaseUrl,
            state.ProcessId,
            WasRunning: wasRunning,
            HadRecordedSidecar: true);
    }

    public static string? ResolveBundledBinaryPath(string applicationBaseDirectory)
    {
        var candidate = GetBundledBinaryCandidatePath(applicationBaseDirectory);
        return candidate is not null && File.Exists(candidate) ? candidate : null;
    }

    public static string? ResolveBinaryFromPath()
    {
        var fileNames = OperatingSystem.IsWindows()
            ? new[] { "chroma.exe", "chroma.cmd", "chroma.bat" }
            : new[] { "chroma" };

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var rawDirectory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(rawDirectory))
            {
                continue;
            }

            string resolvedDirectory;
            try
            {
                resolvedDirectory = Path.GetFullPath(PathUtilities.ExpandHome(rawDirectory));
            }
            catch (Exception) when (rawDirectory.Length > 0)
            {
                continue;
            }

            foreach (var fileName in fileNames)
            {
                var candidate = Path.Combine(resolvedDirectory, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private string? EnsureManagedBinaryInstalled(MempalaceConfigSnapshot config)
    {
        if (!config.ChromaAutoInstall)
        {
            return null;
        }

        var installPath = GetManagedBinaryInstallPath(config);
        if (File.Exists(installPath))
        {
            return installPath;
        }

        var installDirectory = Path.GetDirectoryName(installPath);
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            return null;
        }

        Directory.CreateDirectory(installDirectory);
        using var installLock = AcquireLock(installDirectory, ConfigFileNames.ChromaInstallLock);
        if (File.Exists(installPath))
        {
            return installPath;
        }

        try
        {
            var release = GetOfficialCliReleaseAsync().GetAwaiter().GetResult();
            var asset = ResolveReleaseAssetName();
            if (string.IsNullOrWhiteSpace(release) || string.IsNullOrWhiteSpace(asset))
            {
                throw new InvalidOperationException("Could not determine the official Chroma CLI release or asset name for this platform.");
            }

            var downloadUrl = string.Format(ChromaReleaseDownloadUrlFormat, release, asset);
            var tempPath = Path.Combine(installDirectory, $"memshack-chroma-{Guid.NewGuid():N}.tmp");
            try
            {
                using var response = _downloadHttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength;

                using (var sourceStream = response.Content.ReadAsStream())
                using (var targetStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    CopyToWithProgress(sourceStream, targetStream, asset, totalBytes);
                    targetStream.Flush();
                }

                Directory.CreateDirectory(installDirectory);
                File.Move(tempPath, installPath, overwrite: true);
                TrySetExecutableBit(installPath);
                return installPath;
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"MemShack tried to install the official Chroma CLI automatically into '{installPath}', but it failed: {exception.Message}",
                exception);
        }
    }

    private async Task<string> GetOfficialCliReleaseAsync()
    {
        var installScriptUrl = OperatingSystem.IsWindows() ? ChromaInstallScriptWindowsUrl : ChromaInstallScriptUnixUrl;
        var script = await _downloadHttpClient.GetStringAsync(installScriptUrl).ConfigureAwait(false);
        var match = ReleasePattern.Match(script);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Could not parse the Chroma CLI release from {installScriptUrl}.");
        }

        return match.Groups["release"].Value;
    }

    private static string GetManagedBinaryInstallPath(MempalaceConfigSnapshot config)
    {
        var configDirectory = string.IsNullOrWhiteSpace(config.ConfigDirectory)
            ? MempalaceDefaults.GetDefaultConfigDirectory(PathUtilities.GetHomeDirectory())
            : Path.GetFullPath(PathUtilities.ExpandHome(config.ConfigDirectory));
        var rid = ResolveSupportedRid();
        var fileName = OperatingSystem.IsWindows() ? "chroma.exe" : "chroma";
        if (rid is null)
        {
            return Path.Combine(configDirectory, "chroma", "bin", fileName);
        }

        return Path.Combine(configDirectory, "chroma", "bin", rid, fileName);
    }

    private static string? ResolveReleaseAssetName()
    {
        if (OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "chroma-windows.exe";
        }

        if (OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "chroma-linux";
        }

        if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
        {
            return "chroma-macos-arm64";
        }

        if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "chroma-macos-intel";
        }

        return null;
    }

    public static string? GetBundledBinaryCandidatePath(string applicationBaseDirectory)
    {
        var rid = ResolveSupportedRid();
        if (rid is null)
        {
            return null;
        }

        var executableName = OperatingSystem.IsWindows() ? "chroma.exe" : "chroma";
        return Path.Combine(
            Path.GetFullPath(applicationBaseDirectory),
            "chroma",
            rid,
            executableName);
    }

    private static string? ResolveConfiguredBinaryPath(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(PathUtilities.ExpandHome(path));

    private static string? ResolveSupportedRid()
    {
        if (OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "win-x64";
        }

        if (OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            return "linux-x64";
        }

        if (OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64)
        {
            return "osx-arm64";
        }

        return null;
    }

    private void CopyToWithProgress(Stream sourceStream, Stream targetStream, string assetName, long? totalBytes)
    {
        var progress = _downloadProgress;
        progress?.Invoke(new ChromaDownloadProgress(assetName, 0, totalBytes, IsCompleted: false));

        var buffer = new byte[81920];
        long bytesDownloaded = 0;
        while (true)
        {
            var read = sourceStream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            targetStream.Write(buffer, 0, read);
            bytesDownloaded += read;
            progress?.Invoke(new ChromaDownloadProgress(assetName, bytesDownloaded, totalBytes, IsCompleted: false));
        }

        progress?.Invoke(new ChromaDownloadProgress(assetName, bytesDownloaded, totalBytes, IsCompleted: true));
    }

    private async Task WaitForHealthyAsync(string baseUrl, IChromaSidecarProcess process)
    {
        var startedAt = Stopwatch.StartNew();
        while (startedAt.Elapsed < _startupTimeout)
        {
            if (await IsHealthyAsync(baseUrl).ConfigureAwait(false))
            {
                return;
            }

            if (process.HasExited)
            {
                var exitCode = process.ExitCode is int code ? code.ToString() : "unknown";
                throw new InvalidOperationException($"The Chroma sidecar exited before it became ready (exit code {exitCode}).");
            }

            await Task.Delay(_pollInterval).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for the Chroma sidecar to become ready at {baseUrl}.");
    }

    private async Task<bool> IsHealthyAsync(string baseUrl)
    {
        foreach (var path in new[]
                 {
                     "/api/v1/heartbeat",
                     "/api/v2/heartbeat",
                     "/api/v2/tenants/default_tenant",
                     "/api/v1/tenants/default_tenant",
                 })
        {
            try
            {
                using var response = await _httpClient.GetAsync($"{baseUrl.TrimEnd('/')}{path}").ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException)
            {
            }
        }

        return false;
    }

    private static ProcessStartInfo CreateStartInfo(string binaryPath, string dataPath, int port)
    {
        return new ProcessStartInfo
        {
            FileName = binaryPath,
            Arguments = $"run --host 127.0.0.1 --port {port} --path {QuoteArgument(dataPath)}",
            WorkingDirectory = Path.GetDirectoryName(binaryPath) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    private static FileStream AcquireLock(string dataPath, string lockFileName = ConfigFileNames.ChromaSidecarLock)
    {
        var lockPath = Path.Combine(dataPath, lockFileName);
        var timeout = Stopwatch.StartNew();

        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }

        throw new TimeoutException($"Timed out waiting to acquire the Chroma sidecar lock for '{dataPath}'.");
    }

    private static ChromaSidecarState? LoadState(string statePath)
    {
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ChromaSidecarState>(File.ReadAllText(statePath));
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string QuoteArgument(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool StopProcessAndWait(int processId)
    {
        if (processId <= 0 || !_isProcessAlive(processId))
        {
            return false;
        }

        if (!_tryKillProcessById(processId))
        {
            return false;
        }

        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < _startupTimeout)
        {
            if (!_isProcessAlive(processId))
            {
                return true;
            }

            Thread.Sleep(_pollInterval > TimeSpan.Zero ? _pollInterval : TimeSpan.FromMilliseconds(10));
        }

        throw new InvalidOperationException($"Timed out waiting for the Chroma sidecar process {processId} to stop.");
    }

    private IReadOnlyList<ChromaRunningProcess> DiscoverManagedProcesses(MempalaceConfigSnapshot config)
    {
        var dataPath = Path.GetFullPath(PathUtilities.ExpandHome(config.PalacePath));
        var candidatePaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        var configuredPath = ResolveConfiguredBinaryPath(config.ChromaBinaryPath);
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            candidatePaths.Add(Path.GetFullPath(configuredPath));
        }

        var managedInstallPath = GetManagedBinaryInstallPath(config);
        if (File.Exists(managedInstallPath))
        {
            candidatePaths.Add(Path.GetFullPath(managedInstallPath));
        }

        var bundledPath = ResolveBundledBinaryPath(_applicationBaseDirectory);
        if (!string.IsNullOrWhiteSpace(bundledPath))
        {
            candidatePaths.Add(Path.GetFullPath(bundledPath));
        }

        if (candidatePaths.Count == 0)
        {
            return [];
        }

        return _listRunningChromaProcesses()
            .Where(process => !string.IsNullOrWhiteSpace(process.ExecutablePath) &&
                              candidatePaths.Contains(Path.GetFullPath(process.ExecutablePath!)) &&
                              CommandLineReferencesPath(process.CommandLine, dataPath))
            .OrderBy(process => process.ProcessId)
            .ToArray();
    }

    private static IReadOnlyList<ChromaRunningProcess> ListRunningChromaProcesses()
    {
        if (OperatingSystem.IsWindows())
        {
            var windowsProcesses = ListRunningChromaProcessesFromWindowsCim();
            if (windowsProcesses.Count > 0)
            {
                return windowsProcesses;
            }
        }

        var processes = new List<ChromaRunningProcess>();
        foreach (var process in Process.GetProcessesByName("chroma"))
        {
            try
            {
                processes.Add(new ChromaRunningProcess(process.Id, process.MainModule?.FileName, null));
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return processes;
    }

    private static IReadOnlyList<ChromaRunningProcess> ListRunningChromaProcessesFromWindowsCim()
    {
        try
        {
            var script = "Get-CimInstance Win32_Process -Filter \"Name = 'chroma.exe'\" | Select-Object ProcessId, ExecutablePath, CommandLine | ConvertTo-Json -Compress";
            var encodedScript = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedScript}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            if (string.IsNullOrWhiteSpace(output))
            {
                return [];
            }

            var node = JsonNode.Parse(output);
            if (node is null)
            {
                return [];
            }

            if (node is JsonArray array)
            {
                return array
                    .Select(ParseWindowsProcessNode)
                    .Where(processInfo => processInfo is not null)
                    .Cast<ChromaRunningProcess>()
                    .ToArray();
            }

            var single = ParseWindowsProcessNode(node);
            return single is null ? [] : [single];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ChromaRunningProcess? ParseWindowsProcessNode(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        var processId = node["ProcessId"]?.GetValue<int?>();
        if (processId is null)
        {
            return null;
        }

        return new ChromaRunningProcess(
            processId.Value,
            node["ExecutablePath"]?.GetValue<string?>(),
            node["CommandLine"]?.GetValue<string?>());
    }

    private static bool CommandLineReferencesPath(string? commandLine, string dataPath)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return false;
        }

        var quoted = $"--path \"{dataPath}\"";
        var unquoted = $"--path {dataPath}";
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return commandLine.Contains(quoted, comparison) || commandLine.Contains(unquoted, comparison);
    }

    private static IChromaSidecarProcess StartProcess(ProcessStartInfo startInfo)
    {
        var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException($"Failed to start the Chroma sidecar process: {startInfo.FileName}");
        }

        return new ProcessBackedChromaSidecarProcess(process);
    }

    private static void TryKill(IChromaSidecarProcess process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static bool TryKillProcessById(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TrySetExecutableBit(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ChromaSidecarState(
        string BaseUrl,
        int Port,
        int ProcessId,
        string BinaryPath,
        string DataPath,
        DateTimeOffset StartedAtUtc);

    private sealed class ProcessBackedChromaSidecarProcess(Process process) : IChromaSidecarProcess
    {
        public int Id => process.Id;

        public bool HasExited => process.HasExited;

        public int? ExitCode => process.HasExited ? process.ExitCode : null;

        public void Kill() => process.Kill(entireProcessTree: true);
    }
}

public sealed record ChromaDownloadProgress(
    string AssetName,
    long BytesDownloaded,
    long? TotalBytes,
    bool IsCompleted);

public interface IChromaSidecarProcess
{
    int Id { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    void Kill();
}

public sealed record ChromaSidecarShutdownResult(
    string DataPath,
    string? BaseUrl,
    int? ProcessId,
    bool WasRunning,
    bool HadRecordedSidecar,
    bool UsedProcessDiscovery = false,
    int MatchingProcessCount = 0);

public sealed record ChromaRunningProcess(int ProcessId, string? ExecutablePath, string? CommandLine);
