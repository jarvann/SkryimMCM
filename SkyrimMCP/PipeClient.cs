using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;

namespace SkyrimMCP;

/// <summary>
/// Named pipe client that communicates with the SKSE plugin running inside Skyrim
/// </summary>
public class PipeClient : IPipeClient
{
    private const string PipeName = "SkyrimMCP";
    private const int ConnectTimeoutMs = 3000;
    private const int ResponseTimeoutMs = 10000;

    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsConnected => _pipe?.IsConnected ?? false;

    /// <summary>
    /// Try to connect to the SKSE plugin's named pipe
    /// </summary>
    public async Task<bool> TryConnectAsync()
    {
        try
        {
            Disconnect();

            _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            await _pipe.ConnectAsync(ConnectTimeoutMs);

            _reader = new StreamReader(_pipe);
            _writer = new StreamWriter(_pipe) { AutoFlush = true };

            return true;
        }
        catch (TimeoutException)
        {
            Disconnect();
            return false;
        }
        catch (Exception)
        {
            Disconnect();
            return false;
        }
    }

    // BUG-088: ask the newly connected game which documents folder it uses (Steam and GOG differ). Runs inside the send lock,
    // right after connecting. Older plugins don't know the action: the default folder stays.
    private async Task LearnGameFolderAsync()
    {
        try
        {
            var request = new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["action"] = "get_game_folder", ["params"] = new JsonObject() };
            await _writer!.WriteLineAsync(request.ToJsonString());
            await _writer.FlushAsync();
            using var cts = new CancellationTokenSource(5000);
            var line = await _reader!.ReadLineAsync(cts.Token);
            var data = string.IsNullOrEmpty(line) ? null : JsonNode.Parse(line)?["data"];
            var folder = data?["folder"]?.GetValue<string>();
            var game = data?["gameFolder"]?.GetValue<string>();
            SkyLinkSettings.GameDocumentsFolder = string.IsNullOrWhiteSpace(folder) ? null : folder;
            SkyLinkSettings.GameInstallFolder = string.IsNullOrWhiteSpace(game) ? null : game;
        }
        catch (Exception) { /* keep the default folder */ }
    }

    /// <summary>
    /// Send a request to the SKSE plugin and wait for a response
    /// </summary>
    public async Task<JsonNode?> SendRequestAsync(string action, JsonObject? parameters = null)
    {
        await _sendLock.WaitAsync();
        try
        {
            var request = new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["action"] = action,
                ["params"] = parameters ?? new JsonObject()
            };
            var requestLine = request.ToJsonString();

            // A pipe left over from a previous game session still reports itself as
            // connected until we write to it. If the write fails, the request never
            // reached the plugin, so it is safe to reconnect and send it once more.
            // A failure after the write is not retried: the action may have run.
            for (int attempt = 0; ; attempt++)
            {
                if (!IsConnected)
                {
                    bool connected = await TryConnectAsync();
                    if (!connected)
                    {
                        throw new McpException(DescribeConnectFailure());
                    }
                    await LearnGameFolderAsync();
                }

                try
                {
                    await _writer!.WriteLineAsync(requestLine);
                    await _writer.FlushAsync();
                    break;
                }
                catch (IOException) when (attempt == 0)
                {
                    Disconnect();
                }
            }

            // Read response with timeout
            using var cts = new CancellationTokenSource(ResponseTimeoutMs);
            var responseLine = await _reader!.ReadLineAsync(cts.Token);

            if (string.IsNullOrEmpty(responseLine))
            {
                Disconnect();
                throw new McpException("Empty response from SKSE plugin - connection may have been lost");
            }

            var response = JsonNode.Parse(responseLine);
            if (response == null)
            {
                throw new McpException("Failed to parse response from SKSE plugin");
            }

            // Errors are thrown as McpException because the MCP SDK passes only that
            // exception's message back to the caller. Any other exception type
            // reaches the AI as a bare "An error occurred invoking '<tool>'".
            var success = response["success"]?.GetValue<bool>() ?? false;
            if (!success)
            {
                var error = response["error"]?.GetValue<string>() ?? "Unknown error from SKSE plugin";
                if (error.Contains("timed out", StringComparison.OrdinalIgnoreCase) && GameDownNote() is string down) error = down + " (" + error + ")";
                throw new McpException(error);
            }

            return response["data"];
        }
        catch (OperationCanceledException)
        {
            Disconnect();
            throw new McpException(GameDownNote() ?? "Timed out waiting for response from SKSE plugin - game may be loading or paused");
        }
        catch (IOException)
        {
            Disconnect();
            throw new McpException(GameDownNote() ?? "Lost connection to Skyrim - the game may have been closed");
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Why the pipe didn't answer, as far as the server can tell from outside the game (FEAT-210).
    /// </summary>
    /// <summary>
    /// Why the game stopped answering, if it's the game itself: gone (closed or crashed) or not responding (frozen, or writing a
    /// crash report). Null when Skyrim is running normally. After a crash the plugin's pipe can still answer for a while and
    /// report game-thread timeouts, which read like a slow load (seen 2026-11-19).
    /// </summary>
    internal static string? GameDownNote()
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("SkyrimSE");
            if (procs.Length == 0) return "Skyrim isn't running any more: it closed or crashed (Crash Logger writes its report to Documents\\My Games\\<game>\\SKSE; GetCrashLog reads it).";
            foreach (var p in procs)
            {
                p.Refresh();
                if (!p.HasExited && p.MainWindowHandle != IntPtr.Zero && !p.Responding)
                    return "Skyrim isn't responding (frozen, or crashed and writing a crash report). If it doesn't recover within a minute, it has crashed: GetCrashLog reads the report.";
            }
        }
        catch (Exception) { }
        return null;
    }

    internal static string DescribeConnectFailure()
    {
        bool running;
        try
        {
            running = System.Diagnostics.Process.GetProcessesByName("SkyrimSE").Length > 0
                   || System.Diagnostics.Process.GetProcessesByName("SkyrimVR").Length > 0;
        }
        catch
        {
            return "Cannot connect to Skyrim. Make sure the game is running with SKSE and the SkyrimMCPPlugin is loaded. " +
                   "Call WaitForSkyrim to wait for it.";
        }

        if (!running)
        {
            return "Skyrim is not running. Start it through SKSE (skse64_loader.exe or your mod manager); SkyLink connects " +
                   "once the game reaches the main menu. Call WaitForSkyrim to wait for it instead of retrying.";
        }

        return "Skyrim is running but SkyLink's plugin isn't answering. Usually the game is still starting: the plugin " +
               "opens its connection when the main menu appears, so call WaitForSkyrim. If it never connects, check that " +
               "SkyrimMCPPlugin.dll is in Data\\SKSE\\Plugins and listed as loaded in skse64.log, and that no other " +
               "SkyLink client (another Claude Desktop window or a fork's in-game chat) is holding the connection.";
    }

    /// <summary>
    /// Check if the SKSE plugin is reachable
    /// </summary>
    public async Task<bool> PingAsync()
    {
        try
        {
            await SendRequestAsync("ping");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Disconnect()
    {
        // Disposing a writer on a broken pipe tries to flush and throws; that must
        // not replace the error we are already reporting.
        try { _writer?.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
        try { _reader?.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
        try { _pipe?.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
        _writer = null;
        _reader = null;
        _pipe = null;
    }

    public void Dispose()
    {
        Disconnect();
        _sendLock.Dispose();
    }
}
