using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Depenk.Mcp;

/// <summary>`depenk query`: hosts the MCP server in-process and calls one tool through a real MCP client.</summary>
public static class QueryRunner
{
    public static async Task<int> RunAsync(string workspace, string tool, string? json, TextWriter stdout, TextWriter stderr,
        CancellationToken ct = default)
    {
        Dictionary<string, object?> args;
        try
        {
            args = string.IsNullOrWhiteSpace(json)
                ? []
                : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
        }
        catch (JsonException ex)
        {
            await stderr.WriteLineAsync($"depenk: --json must be a JSON object: {ex.Message}");
            return 2;
        }

        Pipe c2s = new(), s2c = new();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDepenkMcpServer(new GraphStore(workspace)).WithStreamServerTransport(c2s.Reader.AsStream(), s2c.Writer.AsStream());
        await using var sp = services.BuildServiceProvider();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var serverTask = sp.GetRequiredService<McpServer>().RunAsync(cts.Token);
        try
        {
            await using var client = await McpClient.CreateAsync(
                new StreamClientTransport(c2s.Writer.AsStream(), s2c.Reader.AsStream()), cancellationToken: ct);
            if (tool == "tools")
            {
                foreach (var t in (await client.ListToolsAsync(cancellationToken: ct)).OrderBy(t => t.Name, StringComparer.Ordinal))
                    await stdout.WriteLineAsync($"{t.Name}\t{t.Description}");
                return 0;
            }
            var result = await client.CallToolAsync(tool, args, cancellationToken: ct);
            var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
            if (result.IsError == true)
            {
                await stderr.WriteLineAsync(text);
                return 1;
            }
            await stdout.WriteLineAsync(text);
            return 0;
        }
        catch (McpException ex)
        {
            await stderr.WriteLineAsync($"depenk: {ex.Message} (run `depenk query tools` to list tools)");
            return 1;
        }
        finally
        {
            await cts.CancelAsync();
            c2s.Writer.Complete();
            s2c.Writer.Complete();
            try { await serverTask; } catch (OperationCanceledException) { }
        }
    }
}
