using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Depenk.Mcp;

public static class DepenkMcpServer
{
    public const string Instructions =
        "depenk answers cross-repo questions about C# services that call each other through API client NuGet packages. " +
        "Read depenk://overview first. Before changing a controller, route, DTO/model field or client package, call impact_of_change. " +
        "To find who calls an endpoint use get_endpoint (callers) or trace with direction \"up\". To integrate with another service use how_to_call. " +
        "Ids look like repo:orders, ep:orders:GET:/api/orders/{id}, model:Orders.Client:Acme.Orders.Client.OrderDto; endpoints also accept \"VERB /route\" and models a simple name. " +
        "Every result is {summary, stale, truncated, data}; low/medium confidence links are heuristic. If stale is true a background rescan is running. " +
        "Tool errors are JSON {code, message, hint, suggestions} with code not_found, ambiguous, invalid_argument, outside_workspace or scan_failed.";

    public static string Version =>
        typeof(DepenkMcpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static IReadOnlyList<string> ToolNames { get; } = typeof(DepenkTools).GetMethods()
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name).OfType<string>().ToList();

    public static IMcpServerBuilder AddDepenkMcpServer(this IServiceCollection services, GraphStore store)
    {
        services.AddSingleton(store);
        return services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation { Name = "depenk", Version = Version };
                o.ServerInstructions = Instructions;
            })
            .WithTools<DepenkTools>()
            .WithResources<DepenkResources>();
    }
}
