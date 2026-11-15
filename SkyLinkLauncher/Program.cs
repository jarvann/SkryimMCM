using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

namespace SkyLinkLauncher;

// SkyLink AI launcher: a stdio proxy that Claude Desktop starts instead of the MCP server.
//
// Claude Desktop runs "dotnet SkyrimMCP.dll" and keeps that DLL locked for as long as the
// app is open, so every server rebuild meant closing Claude. The launcher runs the server
// from a shadow copy instead, watches the deployed folder, and when a new build lands it
// restarts the server behind the same stdio connection:
//
//   Claude Desktop <--stdio--> SkyLinkLauncher <--stdio--> SkyrimMCP (shadow copy)
//
// On a restart it replays the client's MCP initialize handshake to the new server and
// sends notifications/tools/list_changed so the client can refresh its tool list.
//
// Usage: dotnet SkyLinkLauncher.dll [--server <path to SkyrimMCP.dll>]
// Default server: <launcher folder>\..\SkyLinkAI_Server\SkyrimMCP.dll
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? server = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--server" && i + 1 < args.Length) server = args[++i];
        }
        server ??= Path.Combine(AppContext.BaseDirectory, "..", "SkyLinkAI_Server", "SkyrimMCP.dll");

        var launcher = new Launcher(Path.GetFullPath(server));
        return await launcher.RunAsync();
    }
}

internal sealed class Launcher
{
    private const int DrainTimeoutMs = 15000;
    private const int ReplayTimeoutMs = 15000;
    private const int QuietPeriodMs = 2000;
    private const int MaxCrashes = 3;
    private static readonly TimeSpan CrashWindow = TimeSpan.FromSeconds(60);
    private const string RestartRetryMessage = "SkyLink server restarted for an update; please retry the call";

    private static readonly string Version = typeof(Launcher).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

    private readonly string _serverDll;
    private readonly string _serverDir;
    private readonly string _shadowRoot;
    private readonly string _dotnetHost;

    // Client side (Claude Desktop)
    private readonly StreamWriter _clientOut = new(Console.OpenStandardOutput(), new UTF8Encoding(false)) { NewLine = "\n" };
    private readonly object _clientOutLock = new();
    private static readonly object LogLock = new();

    // Child server
    private readonly object _gate = new();          // guards the fields below
    private Process? _child;
    private StreamWriter? _childIn;
    private Task? _childStdoutTask;
    private int _generation;
    private int _expectedExitGeneration = -1;
    private string? _shadowDir;
    private int _shadowCounter;
    private string _loadedStamp = "";
    private bool _restarting;
    private readonly Queue<string> _pending = new();
    private string? _failure;                       // set by the crash-loop guard
    private readonly List<DateTime> _crashTimes = new();

    // MCP handshake, replayed to every new server process
    private JsonNode? _initializeRequest;
    private string? _clientInitIdKey;
    private string? _initializedNotification;
    private string? _replayIdKey;
    private TaskCompletionSource<bool>? _replayTcs;

    // Client requests sent to the current child that have no response yet (key: id JSON)
    private readonly ConcurrentDictionary<string, JsonNode> _inFlight = new();

    private readonly SemaphoreSlim _restartLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();

    public Launcher(string serverDll)
    {
        _serverDll = serverDll;
        _serverDir = Path.GetDirectoryName(serverDll)!;

        var overrideRoot = Environment.GetEnvironmentVariable("SKYLINK_SHADOW_ROOT");
        var baseDir = !string.IsNullOrEmpty(overrideRoot) ? overrideRoot
            : OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyLinkAI", "server-shadow")
                : Path.Combine(Path.GetTempPath(), "SkyLinkAI", "server-shadow");
        _shadowRoot = baseDir;

        var host = Environment.ProcessPath;
        _dotnetHost = host != null && Path.GetFileNameWithoutExtension(host).StartsWith("dotnet", StringComparison.OrdinalIgnoreCase)
            ? host
            : "dotnet";
    }

    public async Task<int> RunAsync()
    {
        Log($"v{Version} starting; server {_serverDll}");
        if (!File.Exists(_serverDll))
        {
            Log($"Server DLL not found: {_serverDll}. Pass --server <path to SkyrimMCP.dll>.");
            return 2;
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillChildQuietly();
        CleanOldShadows();

        try
        {
            lock (_gate) StartChildLocked();
        }
        catch (Exception ex)
        {
            Log($"Could not start the server: {ex.Message}");
            return 3;
        }

        var watcher = Task.Run(() => WatchServerFolderAsync(_shutdown.Token));

        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        while (true)
        {
            string? line;
            try { line = await stdin.ReadLineAsync(); }
            catch (IOException) { line = null; }
            if (line == null) break;
            if (line.Length == 0) continue;
            HandleClientLine(line);
        }

        Log("Client closed stdin; shutting down");
        _shutdown.Cancel();
        await StopChildAsync(5000);
        try { await watcher; } catch (OperationCanceledException) { }
        if (_shadowDir != null) TryDeleteDirectory(_shadowDir);
        return 0;
    }

    // ---------------------------------------------------------------- client -> child

    private void HandleClientLine(string line)
    {
        JsonNode? node = TryParse(line);
        string? method = node?["method"]?.GetValue<string>();
        JsonNode? id = node?["id"];

        if (method == "initialize" && id != null)
        {
            _initializeRequest = node!.DeepClone();
            _clientInitIdKey = id.ToJsonString();
        }
        else if (method == "notifications/initialized")
        {
            _initializedNotification = line;
        }

        lock (_gate)
        {
            if (_failure != null)
            {
                if (method != null && id != null) WriteToClient(ErrorResponse(id, _failure));
                return;
            }
            if (_restarting || _childIn == null)
            {
                _pending.Enqueue(line);
                return;
            }
            SendToChildLocked(line, node);
        }
    }

    private void SendToChildLocked(string line, JsonNode? node)
    {
        string? method = node?["method"]?.GetValue<string>();
        JsonNode? id = node?["id"];
        bool isRequest = method != null && id != null;
        string? key = isRequest ? id!.ToJsonString() : null;

        if (isRequest) _inFlight[key!] = id!.DeepClone();
        try
        {
            _childIn!.WriteLine(line);
            _childIn.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The child is gone; its exit handler restarts it and this message goes out afterwards.
            if (isRequest) _inFlight.TryRemove(key!, out _);
            _childIn = null;
            _pending.Enqueue(line);
        }
    }

    // ---------------------------------------------------------------- child -> client

    private async Task PumpChildStdoutAsync(Process child, int generation)
    {
        try
        {
            while (true)
            {
                var line = await child.StandardOutput.ReadLineAsync();
                if (line == null) break;
                if (line.Length == 0) continue;

                var node = TryParse(line);
                var id = node?["id"];
                bool isResponse = id != null && node!["method"] == null;
                if (isResponse)
                {
                    var key = id!.ToJsonString();
                    if (key == _replayIdKey)
                    {
                        _replayTcs?.TrySetResult(true);
                        continue;   // the client never asked for this one
                    }
                    if (key == _clientInitIdKey && node!["result"] is JsonObject result)
                    {
                        // Advertise tools.listChanged so the client knows to refresh after a restart.
                        var caps = result["capabilities"] as JsonObject ?? new JsonObject();
                        result["capabilities"] = caps;
                        var tools = caps["tools"] as JsonObject ?? new JsonObject();
                        caps["tools"] = tools;
                        tools["listChanged"] = true;
                        line = node.ToJsonString();
                    }
                    _inFlight.TryRemove(key, out _);
                }
                WriteToClient(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        OnChildExited(generation);
    }

    private static async Task PumpChildStderrAsync(Process child)
    {
        try
        {
            while (true)
            {
                var line = await child.StandardError.ReadLineAsync();
                if (line == null) break;
                lock (LogLock) Console.Error.WriteLine("[server] " + line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private void WriteToClient(string line)
    {
        lock (_clientOutLock)
        {
            try
            {
                _clientOut.WriteLine(line);
                _clientOut.Flush();
            }
            catch (IOException) { /* client gone; stdin EOF ends the launcher */ }
        }
    }

    // ---------------------------------------------------------------- child lifecycle

    private void StartChildLocked()
    {
        var stamp = GetFolderStamp();
        var previousShadow = _shadowDir;
        _shadowDir = CopyToShadow();
        if (previousShadow != null) TryDeleteDirectory(previousShadow);

        var psi = new ProcessStartInfo(_dotnetHost)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = _shadowDir,
        };
        psi.ArgumentList.Add(Path.Combine(_shadowDir, Path.GetFileName(_serverDll)));
        psi.Environment["SKYLINK_LAUNCHER_VERSION"] = Version;
        psi.Environment["SKYLINK_SERVER_SOURCE"] = _serverDll;

        var child = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        int generation = ++_generation;
        _child = child;
        _childIn = child.StandardInput;
        _childIn.NewLine = "\n";
        _childIn.AutoFlush = false;
        _loadedStamp = stamp;
        _childStdoutTask = Task.Run(() => PumpChildStdoutAsync(child, generation));
        _ = Task.Run(() => PumpChildStderrAsync(child));
        Log($"Server started (pid {child.Id}, SkyrimMCP.dll written {ServerBuildTime()}) from {_shadowDir}");
    }

    private async Task StopChildAsync(int timeoutMs)
    {
        Process? child;
        Task? stdoutTask;
        lock (_gate)
        {
            child = _child;
            stdoutTask = _childStdoutTask;
            _expectedExitGeneration = _generation;
            try { _childIn?.Close(); } catch (Exception) { }   // MCP servers exit on stdin EOF
            _childIn = null;
            _child = null;
        }
        if (child == null) return;

        using var cts = new CancellationTokenSource(timeoutMs);
        try { await child.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            Log($"Server (pid {child.Id}) did not exit within {timeoutMs} ms; killing it");
            try { child.Kill(entireProcessTree: true); } catch (Exception) { }
        }
        if (stdoutTask != null) await Task.WhenAny(stdoutTask, Task.Delay(2000));
        child.Dispose();
    }

    private void KillChildQuietly()
    {
        try
        {
            var child = _child;
            if (child != null && !child.HasExited) child.Kill(entireProcessTree: true);
        }
        catch (Exception) { }
    }

    private void OnChildExited(int generation)
    {
        lock (_gate)
        {
            if (generation != _generation || generation == _expectedExitGeneration || _shutdown.IsCancellationRequested)
                return;   // a stop we asked for
            _replayTcs?.TrySetResult(false);

            int? code = null;
            try { if (_child is { HasExited: true }) code = _child.ExitCode; } catch (Exception) { }
            Log($"Server exited unexpectedly (exit code {code?.ToString() ?? "unknown"})");
            _childIn = null;

            var now = DateTime.UtcNow;
            _crashTimes.Add(now);
            _crashTimes.RemoveAll(t => now - t > CrashWindow);
            if (_crashTimes.Count >= MaxCrashes)
            {
                _failure = $"SkyLink server crashed {_crashTimes.Count} times within {CrashWindow.TotalSeconds:0} s and was not restarted. " +
                    "Check the server log (Claude Desktop MCP log), then deploy a fixed build; the launcher retries when the server files change.";
                Log(_failure);
                FailInFlight(_failure);
                FailPendingLocked(_failure);
                return;
            }
        }
        FailInFlight("SkyLink server exited unexpectedly and is being restarted; please retry the call");
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000);
            await RestartAsync("crash", drain: false);
        });
    }

    private async Task RestartAsync(string reason, bool drain)
    {
        await _restartLock.WaitAsync();
        try
        {
            if (_shutdown.IsCancellationRequested) return;
            lock (_gate)
            {
                if (_failure != null && reason == "crash") return;
                _restarting = true;
            }
            Log($"Restarting server ({reason})");

            if (drain)
            {
                var sw = Stopwatch.StartNew();
                while (!_inFlight.IsEmpty && sw.ElapsedMilliseconds < DrainTimeoutMs)
                    await Task.Delay(50);
                if (!_inFlight.IsEmpty) Log($"{_inFlight.Count} call(s) still pending after {DrainTimeoutMs} ms; answering them with a retry error");
            }
            FailInFlight(RestartRetryMessage);
            await StopChildAsync(3000);

            lock (_gate)
            {
                try
                {
                    StartChildLocked();
                }
                catch (Exception ex)
                {
                    Log($"Could not start the server: {ex.Message}");
                    _failure = $"SkyLink server could not be started: {ex.Message}. Deploy a working build; the launcher retries when the server files change.";
                    FailPendingLocked(_failure);
                    return;
                }
            }

            if (_initializeRequest != null && await ReplayHandshakeAsync())
            {
                WriteToClient("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}");
            }
        }
        finally
        {
            lock (_gate)
            {
                _restarting = false;
                if (_failure == null && _childIn != null)
                {
                    while (_pending.Count > 0)
                    {
                        var line = _pending.Dequeue();
                        SendToChildLocked(line, TryParse(line));
                        if (_childIn == null) break;
                    }
                }
            }
            _restartLock.Release();
        }
    }

    private async Task<bool> ReplayHandshakeAsync()
    {
        var request = _initializeRequest!.DeepClone();
        string replayId = $"skylink-launcher-init-{_generation}";
        request["id"] = replayId;
        _replayIdKey = JsonValue.Create(replayId)!.ToJsonString();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _replayTcs = tcs;

        lock (_gate)
        {
            if (_childIn == null) return false;
            try
            {
                _childIn.WriteLine(request.ToJsonString());
                _childIn.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return false; }
        }

        var done = await Task.WhenAny(tcs.Task, Task.Delay(ReplayTimeoutMs));
        if (done != tcs.Task || !tcs.Task.Result)
        {
            Log("New server did not answer the replayed initialize request");
            return false;
        }

        lock (_gate)
        {
            if (_childIn == null) return false;
            try
            {
                _childIn.WriteLine(_initializedNotification ?? "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
                _childIn.Flush();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
        Log("Handshake replayed to the new server");
        return true;
    }

    private void FailInFlight(string message)
    {
        foreach (var key in _inFlight.Keys.ToArray())
        {
            if (_inFlight.TryRemove(key, out var id)) WriteToClient(ErrorResponse(id, message));
        }
    }

    private void FailPendingLocked(string message)
    {
        while (_pending.Count > 0)
        {
            var node = TryParse(_pending.Dequeue());
            var id = node?["id"];
            if (id != null && node!["method"] != null) WriteToClient(ErrorResponse(id, message));
        }
    }

    // ---------------------------------------------------------------- watching for new builds

    private async Task WatchServerFolderAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await WatchOnceAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log($"Watcher error: {ex.Message}");
            }
        }
    }

    private async Task WatchOnceAsync(CancellationToken ct)
    {
        await Task.Delay(1000, ct);
        string stamp;
        try { stamp = GetFolderStamp(); } catch (Exception) { return; }
        if (stamp == _loadedStamp) return;

        // A publish writes many files; wait until the folder has been quiet for a while.
        while (true)
        {
            await Task.Delay(QuietPeriodMs, ct);
            string again;
            try { again = GetFolderStamp(); } catch (Exception) { continue; }
            if (again == stamp && IsReadable(_serverDll)) break;
            stamp = again;
        }

        Log($"New server build detected (SkyrimMCP.dll written {ServerBuildTime()})");
        lock (_gate)
        {
            // A new build is the fix for a crash loop: start over.
            _failure = null;
            _crashTimes.Clear();
        }
        await RestartAsync("new build", drain: true);
    }

    // Fingerprint of the deployed server: name, size and write time of every top-level .dll/.json.
    // Any difference counts as a new build, including a rollback to older files.
    private string GetFolderStamp()
    {
        var sb = new StringBuilder();
        foreach (var f in new DirectoryInfo(_serverDir).EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!f.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !f.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append(f.Name).Append('|').Append(f.Length).Append('|').Append(f.LastWriteTimeUtc.Ticks).Append(';');
        }
        return sb.ToString();
    }

    private string ServerBuildTime()
    {
        try { return File.GetLastWriteTimeUtc(_serverDll).ToString("yyyy-MM-dd HH:mm:ss") + " UTC"; }
        catch (Exception) { return "unknown"; }
    }

    private static bool IsReadable(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return true;
        }
        catch (Exception) { return false; }
    }

    // ---------------------------------------------------------------- shadow copies

    private string CopyToShadow()
    {
        Directory.CreateDirectory(_shadowRoot);
        var dest = Path.Combine(_shadowRoot, $"{Environment.ProcessId}-{++_shadowCounter}");
        if (Directory.Exists(dest)) TryDeleteDirectory(dest);
        Directory.CreateDirectory(dest);

        // Recursive: the publish output has runtimes\<rid>\lib\... folders that deps.json points into.
        foreach (var file in Directory.EnumerateFiles(_serverDir, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dest, Path.GetRelativePath(_serverDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Copy(file, target, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(300);   // a build may still be writing this file
                }
            }
        }
        return dest;
    }

    // Removes shadow folders left by launchers that are no longer running. Folders are named
    // <pid>-<n>; a folder whose process is still alive belongs to another running launcher
    // (Claude Desktop sometimes starts two) and is left alone.
    private void CleanOldShadows()
    {
        try
        {
            if (!Directory.Exists(_shadowRoot)) return;
            foreach (var dir in Directory.EnumerateDirectories(_shadowRoot))
            {
                var name = Path.GetFileName(dir);
                var dash = name.IndexOf('-');
                if (dash <= 0 || !int.TryParse(name[..dash], out var pid)) continue;
                if (pid == Environment.ProcessId || IsProcessAlive(pid)) continue;
                TryDeleteDirectory(dir);
            }
        }
        catch (Exception) { }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception) { return false; }
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
    }

    // ---------------------------------------------------------------- helpers

    private static JsonNode? TryParse(string line)
    {
        try { return JsonNode.Parse(line); } catch (Exception) { return null; }
    }

    private static string ErrorResponse(JsonNode id, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["error"] = new JsonObject { ["code"] = -32603, ["message"] = message },
    }.ToJsonString();

    private static void Log(string message)
    {
        lock (LogLock) Console.Error.WriteLine($"[SkyLinkLauncher] {DateTime.Now:HH:mm:ss} {message}");
    }
}
