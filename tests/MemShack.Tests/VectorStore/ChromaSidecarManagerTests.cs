using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using MemShack.Core.Constants;
using MemShack.Core.Models;
using MemShack.Infrastructure.VectorStore;
using MemShack.Infrastructure.VectorStore.Collections;
using MemShack.Tests.Utilities;

namespace MemShack.Tests.VectorStore;

[TestClass]
public sealed class ChromaSidecarManagerTests
{
    [TestMethod]
    public void TryGetOrStart_UsesConfiguredBinaryPathAndWritesState()
    {
        using var temp = new TemporaryDirectory();
        var palacePath = temp.GetPath("palace");
        Directory.CreateDirectory(palacePath);
        var binaryPath = temp.WriteFile("tools/chroma.exe", "fake");
        using var httpClient = new HttpClient(new HealthyChromaHandler("http://127.0.0.1:43123"));
        ProcessStartInfo? capturedStartInfo = null;

        var manager = new ChromaSidecarManager(
            applicationBaseDirectory: temp.Root,
            httpClient: httpClient,
            processStarter: startInfo =>
            {
                capturedStartInfo = startInfo;
                return new FakeSidecarProcess(id: 8124);
            },
            portAllocator: () => 43123,
            startupTimeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.Zero);

        var url = manager.TryGetOrStart(CreateConfig(palacePath, chromaBinaryPath: binaryPath));

        Assert.Equal("http://127.0.0.1:43123", url);
        var startInfo = Assert.NotNull(capturedStartInfo);
        Assert.Equal(Path.GetFullPath(binaryPath), Path.GetFullPath(startInfo.FileName));
        Assert.Contains("--host 127.0.0.1", startInfo.Arguments, StringComparison.Ordinal);
        Assert.Contains("--port 43123", startInfo.Arguments, StringComparison.Ordinal);
        Assert.Contains($"--path {palacePath}", startInfo.Arguments, StringComparison.Ordinal);

        var statePath = Path.Combine(palacePath, ConfigFileNames.ChromaSidecarStateJson);
        Assert.True(File.Exists(statePath));
        var state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        Assert.Equal("http://127.0.0.1:43123", state["BaseUrl"]!.GetValue<string>());
        Assert.Equal(8124, state["ProcessId"]!.GetValue<int>());
    }

    [TestMethod]
    public void TryGetOrStart_ReusesHealthyRecordedSidecarWithoutRestarting()
    {
        using var temp = new TemporaryDirectory();
        var palacePath = temp.GetPath("palace");
        Directory.CreateDirectory(palacePath);
        File.WriteAllText(
            Path.Combine(palacePath, ConfigFileNames.ChromaSidecarStateJson),
            """
            {
              "BaseUrl": "http://127.0.0.1:43210",
              "Port": 43210,
              "ProcessId": 5000,
              "BinaryPath": "C:\\tools\\chroma.exe",
              "DataPath": "placeholder",
              "StartedAtUtc": "2026-04-09T00:00:00+00:00"
            }
            """.Replace("placeholder", palacePath.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal));

        var startCount = 0;
        using var httpClient = new HttpClient(new HealthyChromaHandler("http://127.0.0.1:43210"));
        var manager = new ChromaSidecarManager(
            applicationBaseDirectory: temp.Root,
            httpClient: httpClient,
            processStarter: _ =>
            {
                startCount++;
                return new FakeSidecarProcess(id: 1);
            });

        var url = manager.TryGetOrStart(CreateConfig(palacePath));

        Assert.Equal("http://127.0.0.1:43210", url);
        Assert.Equal(0, startCount);
    }

    [TestMethod]
    public void TryGetOrStart_ReturnsNullWhenNoBundledOrConfiguredBinaryExists()
    {
        using var temp = new TemporaryDirectory();
        var palacePath = temp.GetPath("palace");

        var manager = new ChromaSidecarManager(
            applicationBaseDirectory: temp.Root,
            httpClient: new HttpClient(new HealthyChromaHandler("http://127.0.0.1:49999")));

        var url = manager.TryGetOrStart(CreateConfig(palacePath) with { ChromaAutoInstall = false, ConfigDirectory = temp.GetPath("config") });

        Assert.Null(url);
    }

    [TestMethod]
    public void GetBundledBinaryCandidatePath_UsesKnownToolLayout()
    {
        using var temp = new TemporaryDirectory();

        var candidate = ChromaSidecarManager.GetBundledBinaryCandidatePath(temp.Root);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.Combine(temp.Root, "chroma", "win-x64", "chroma.exe"), candidate);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(Path.Combine(temp.Root, "chroma", "linux-x64", "chroma"), candidate);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(Path.Combine(temp.Root, "chroma", "osx-arm64", "chroma"), candidate);
            return;
        }

        Assert.Null(candidate);
    }

    [TestMethod]
    public void ResolveBinaryFromPath_FindsChromaExecutableOnPath()
    {
        using var temp = new TemporaryDirectory();
        var binDirectory = temp.GetPath("bin");
        Directory.CreateDirectory(binDirectory);
        var fileName = OperatingSystem.IsWindows() ? "chroma.exe" : "chroma";
        var chromaPath = temp.WriteFile(Path.Combine("bin", fileName), "fake");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", binDirectory);

        try
        {
            var resolved = ChromaSidecarManager.ResolveBinaryFromPath();

            Assert.Equal(Path.GetFullPath(chromaPath), Path.GetFullPath(Assert.NotNull(resolved)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [TestMethod]
    public void VectorStoreFactory_UsesManagedSidecarWhenAvailable()
    {
        using var temp = new TemporaryDirectory();
        var palacePath = temp.GetPath("palace");
        Directory.CreateDirectory(palacePath);
        var binaryPath = temp.WriteFile("tools/chroma.exe", "fake");
        using var httpClient = new HttpClient(new HealthyChromaHandler("http://127.0.0.1:47890"));
        var manager = new ChromaSidecarManager(
            applicationBaseDirectory: temp.Root,
            httpClient: httpClient,
            processStarter: _ => new FakeSidecarProcess(id: 9001),
            portAllocator: () => 47890,
            startupTimeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.Zero);

        var store = VectorStoreFactory.Create(CreateConfig(palacePath, chromaBinaryPath: binaryPath), manager);

        Assert.True(store is ChromaHttpVectorStore);
    }

    [TestMethod]
    public void VectorStoreFactory_UsesCompatibilityStoreWhenConfigured()
    {
        using var temp = new TemporaryDirectory();

        var store = VectorStoreFactory.Create(
            CreateConfig(temp.GetPath("palace")) with { VectorStoreBackend = "compatibility", ConfigDirectory = temp.GetPath("config") },
            new ChromaSidecarManager(applicationBaseDirectory: temp.Root, httpClient: new HttpClient(new HealthyChromaHandler("http://127.0.0.1:49999"))));

        Assert.True(store is ChromaCompatibilityVectorStore);
    }

    [TestMethod]
    public void VectorStoreFactory_DefaultChromaBackendThrowsWhenUnavailable()
    {
        using var temp = new TemporaryDirectory();
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", string.Empty);

        try
        {
            InvalidOperationException? exception = null;
            try
            {
                _ = VectorStoreFactory.Create(
                    CreateConfig(temp.GetPath("palace")) with { ChromaAutoInstall = false, ConfigDirectory = temp.GetPath("config") },
                    new ChromaSidecarManager(applicationBaseDirectory: temp.Root, httpClient: new HttpClient(new HealthyChromaHandler("http://127.0.0.1:49999"))));
            }
            catch (InvalidOperationException caught)
            {
                exception = caught;
            }

            var actualException = Assert.NotNull(exception);
            Assert.Contains("managed Chroma database", actualException.Message);
            Assert.Contains("auto-install it to the default config location", actualException.Message);
            Assert.Contains("vector_store_backend", actualException.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [TestMethod]
    public void TryGetOrStart_AutoInstallsBinaryToConfigDirectoryWhenAllowed()
    {
        using var temp = new TemporaryDirectory();
        var palacePath = temp.GetPath("palace");
        var configDirectory = temp.GetPath("config");
        Directory.CreateDirectory(palacePath);
        Directory.CreateDirectory(configDirectory);
        using var downloadClient = new HttpClient(new BootstrapChromaHandler("cli-9.9.9", "pretend-binary"));
        using var healthClient = new HttpClient(new HealthyChromaHandler("http://127.0.0.1:43123"));
        ProcessStartInfo? capturedStartInfo = null;

        var manager = new ChromaSidecarManager(
            applicationBaseDirectory: temp.Root,
            httpClient: healthClient,
            downloadHttpClient: downloadClient,
            processStarter: startInfo =>
            {
                capturedStartInfo = startInfo;
                return new FakeSidecarProcess(id: 8125);
            },
            portAllocator: () => 43123,
            startupTimeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.Zero);

        var url = manager.TryGetOrStart(CreateConfig(palacePath) with { ConfigDirectory = configDirectory });

        var expectedBinary = Path.Combine(configDirectory, "chroma", "bin", OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : "osx-arm64", OperatingSystem.IsWindows() ? "chroma.exe" : "chroma");
        Assert.Equal("http://127.0.0.1:43123", url);
        Assert.True(File.Exists(expectedBinary));
        Assert.Equal(Path.GetFullPath(expectedBinary), Path.GetFullPath(Assert.NotNull(capturedStartInfo).FileName));
    }

    [TestMethod]
    public void Shutdown_KillsRecordedProcessAndClearsState()
    {
        using var temp = new TemporaryDirectory();
        var palacePath = temp.GetPath("palace");
        Directory.CreateDirectory(palacePath);
        File.WriteAllText(
            Path.Combine(palacePath, ConfigFileNames.ChromaSidecarStateJson),
            """
            {
              "BaseUrl": "http://127.0.0.1:43210",
              "Port": 43210,
              "ProcessId": 5000,
              "BinaryPath": "C:\\tools\\chroma.exe",
              "DataPath": "placeholder",
              "StartedAtUtc": "2026-04-09T00:00:00+00:00"
            }
            """.Replace("placeholder", palacePath.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal));
        var killedProcessIds = new List<int>();
        var processAlive = true;
        var manager = new ChromaSidecarManager(
            applicationBaseDirectory: temp.Root,
            isProcessAlive: processId => processId == 5000 && processAlive,
            tryKillProcessById: processId =>
            {
                killedProcessIds.Add(processId);
                processAlive = false;
                return true;
            },
            startupTimeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.Zero);

        var result = manager.Shutdown(CreateConfig(palacePath));

        Assert.True(result.HadRecordedSidecar);
        Assert.True(result.WasRunning);
        Assert.Equal(5000, result.ProcessId);
        Assert.Equal("http://127.0.0.1:43210", result.BaseUrl);
        Assert.Equal([5000], killedProcessIds);
        Assert.False(File.Exists(Path.Combine(palacePath, ConfigFileNames.ChromaSidecarStateJson)));
    }

    [TestMethod]
    public void Shutdown_ReturnsNoRecordedSidecarWhenStateIsMissing()
    {
        using var temp = new TemporaryDirectory();
        var palacePath = temp.GetPath("palace");
        Directory.CreateDirectory(palacePath);
        var manager = new ChromaSidecarManager(
            applicationBaseDirectory: temp.Root,
            listRunningChromaProcesses: () => []);

        var result = manager.Shutdown(CreateConfig(palacePath) with { ConfigDirectory = temp.GetPath("config") });

        Assert.False(result.HadRecordedSidecar);
        Assert.False(result.WasRunning);
        Assert.Null(result.ProcessId);
        Assert.Null(result.BaseUrl);
    }

    [TestMethod]
    public void Shutdown_UsesManagedBinaryDiscoveryWhenStateIsMissing()
    {
        using var temp = new TemporaryDirectory();
        var palacePath = temp.GetPath("palace");
        var configDirectory = temp.GetPath("config");
        Directory.CreateDirectory(palacePath);
        Directory.CreateDirectory(configDirectory);
        var managedBinaryPath = temp.WriteFile(
            Path.Combine("config", "chroma", "bin", OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : "osx-arm64", OperatingSystem.IsWindows() ? "chroma.exe" : "chroma"),
            "fake");
        var processAlive = true;
        var killedProcessIds = new List<int>();
        var manager = new ChromaSidecarManager(
            applicationBaseDirectory: temp.Root,
            isProcessAlive: processId => processId == 7123 && processAlive,
            listRunningChromaProcesses: () =>
            [
                new ChromaRunningProcess(
                    7123,
                    managedBinaryPath,
                    $"\"{managedBinaryPath}\" run --host 127.0.0.1 --port 43123 --path {palacePath}"),
                new ChromaRunningProcess(
                    9001,
                    temp.GetPath("other", OperatingSystem.IsWindows() ? "chroma.exe" : "chroma"),
                    $"\"{temp.GetPath("other", OperatingSystem.IsWindows() ? "chroma.exe" : "chroma")}\" run --host 127.0.0.1 --port 43124 --path {temp.GetPath("other-palace")}")
            ],
            tryKillProcessById: processId =>
            {
                killedProcessIds.Add(processId);
                processAlive = false;
                return true;
            },
            startupTimeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.Zero);

        var result = manager.Shutdown(CreateConfig(palacePath) with { ConfigDirectory = configDirectory });

        Assert.False(result.HadRecordedSidecar);
        Assert.True(result.UsedProcessDiscovery);
        Assert.Equal(1, result.MatchingProcessCount);
        Assert.True(result.WasRunning);
        Assert.Equal(7123, result.ProcessId);
        Assert.Equal([7123], killedProcessIds);
    }

    private static MempalaceConfigSnapshot CreateConfig(string palacePath, string? chromaBinaryPath = null) =>
        new(
            PalacePath: palacePath,
            CollectionName: CollectionNames.Drawers,
            PeopleMap: new Dictionary<string, string>(StringComparer.Ordinal),
            TopicWings: [],
            HallKeywords: new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
            ChromaBinaryPath: chromaBinaryPath);

    private sealed class FakeSidecarProcess(int id, bool hasExited = false, int? exitCode = null) : IChromaSidecarProcess
    {
        public int Id { get; } = id;

        public bool HasExited { get; private set; } = hasExited;

        public int? ExitCode { get; } = exitCode;

        public void Kill()
        {
            HasExited = true;
        }
    }

    private sealed class HealthyChromaHandler(string healthyBaseUrl) : HttpMessageHandler
    {
        private readonly string _healthyBaseUrl = healthyBaseUrl.TrimEnd('/');

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestUrl = request.RequestUri?.ToString()?.TrimEnd('/');
            if (requestUrl is not null &&
                (requestUrl.Equals($"{_healthyBaseUrl}/api/v1/heartbeat", StringComparison.Ordinal) ||
                 requestUrl.Equals($"{_healthyBaseUrl}/api/v2/heartbeat", StringComparison.Ordinal) ||
                 requestUrl.Equals($"{_healthyBaseUrl}/api/v2/tenants/default_tenant", StringComparison.Ordinal)))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class BootstrapChromaHandler(string release, string binaryContent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (url.Contains("install.ps1", StringComparison.Ordinal) || url.Contains("install.sh", StringComparison.Ordinal))
            {
                var script = OperatingSystem.IsWindows()
                    ? $"""$release = "{release}" """
                    : $"""RELEASE="{release}" """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(script),
                });
            }

            if (url.Contains($"/releases/download/{release}/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(binaryContent)),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
