using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SkyrimMCP;

// BUG-023: a call with a parameter the tool doesn't have came back as an error with no text. This filter names the unknown
// parameter and lists the valid ones, before the tool runs; and an error that escapes a tool with an empty message gets one.
internal static class ToolParameterCheck
{
    static readonly Dictionary<string, string[]> Parameters = Build();

    static Dictionary<string, string[]> Build()
    {
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var type in typeof(ToolParameterCheck).Assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() != null))
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                var attr = m.GetCustomAttribute<McpServerToolAttribute>();
                if (attr == null) continue;
                var name = string.IsNullOrEmpty(attr.Name) ? m.Name : attr.Name;
                map[name] = m.GetParameters()
                    .Where(p => p.ParameterType != typeof(CancellationToken) && !p.ParameterType.Name.StartsWith("RequestContext", StringComparison.Ordinal)
                                && !p.ParameterType.Name.StartsWith("IMcpServer", StringComparison.Ordinal) && !p.ParameterType.Name.StartsWith("McpServer", StringComparison.Ordinal)
                                && p.ParameterType != typeof(IServiceProvider) && p.ParameterType != typeof(IPipeClient))
                    .Select(p => p.Name!).ToArray();
            }
        return map;
    }

    public static McpRequestHandler<CallToolRequestParams, CallToolResult> Filter(McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
        async (context, cancellationToken) =>
        {
            var p = context.Params;
            if (p != null && Parameters.TryGetValue(p.Name, out var valid) && p.Arguments != null)
            {
                var unknown = p.Arguments.Keys.Where(k => !valid.Contains(k, StringComparer.Ordinal)).ToList();
                if (unknown.Count > 0)
                {
                    var hints = unknown.Select(u => valid.FirstOrDefault(v => v.Equals(u, StringComparison.OrdinalIgnoreCase)) is string near
                        ? $"'{u}' (did you mean '{near}'?)" : $"'{u}'");
                    return Error($"{p.Name} has no parameter {string.Join(", ", hints)}. Its parameters: " +
                                 (valid.Length == 0 ? "none" : string.Join(", ", valid)) + ".");
                }
            }
            try
            {
                return await next(context, cancellationToken);
            }
            catch (Exception ex) when (string.IsNullOrWhiteSpace(ex.Message) && ex is not OperationCanceledException)
            {
                return Error($"{p?.Name} failed ({ex.GetType().Name}) without a message.");
            }
        };

    static CallToolResult Error(string text) => new() { Content = [new TextContentBlock { Text = text }], IsError = true };
}
