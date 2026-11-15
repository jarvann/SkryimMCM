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
            })
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync();
    }
}
