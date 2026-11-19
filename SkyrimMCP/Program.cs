using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace SkyrimMCP;

class Program
{
    static async Task Main(string[] args)
    {
        // FEAT-265: `dotnet SkyrimMCP.dll --scan-save <file.ess> [...]` prints the native save scan as JSON and exits (used to
        // check the scanner against ReSaver on real saves without the game).
        if (args.Length >= 2 && args[0] == "--scan-save")
        {
            foreach (var path in args.Skip(1))
            {
                try
                {
                    var scan = Saves.EssScanner.Scan(path);
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(scan, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
                catch (Exception ex) { Console.WriteLine($"{{\"file\": \"{Path.GetFileName(path)}\", \"error\": \"{ex.GetType().Name}: {ex.Message}\"}}"); }
            }
            return;
        }

        var builder = Host.CreateApplicationBuilder(args);

        // Configure logging to stderr (stdout is for MCP protocol)
        builder.Logging.AddConsole(consoleLogOptions =>
        {
            consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
        });

        // Register pipe client as singleton (concrete + interface)
        builder.Services.AddSingleton<PipeClient>();
        builder.Services.AddSingleton<IPipeClient>(sp => sp.GetRequiredService<PipeClient>());

        // Configure MCP server with stdio transport and auto-discover tools
        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = "SkyLink AI",
                    Version = typeof(Program).Assembly
                        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown"
                };
                // Tells the AI to check the guidebook (UESP) before answering game help questions (FEAT-221).
                options.ServerInstructions = Tools.GuidebookTools.ServerInstructions;
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
    }
}
