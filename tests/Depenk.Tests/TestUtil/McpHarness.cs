using System.IO.Pipelines;
using System.Text.Json;
using Depenk.Mcp;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Depenk.Tests.TestUtil;

public sealed class McpHarness : IAsyncDisposable
{
    private readonly Pipe _c2s = new(), _s2c = new();
    private readonly CancellationTokenSource _cts = new();
    private ServiceProvider _sp = null!;
    private Task _serverTask = Task.CompletedTask;

    public McpClient Client { get; private set; } = null!;
    public GraphStore Store { get; private set; } = null!;

    public static async Task<McpHarness> StartAsync(string workspace)
    {
        var h = new McpHarness { Store = new GraphStore(workspace) };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDepenkMcpServer(h.Store).WithStreamServerTransport(h._c2s.Reader.AsStream(), h._s2c.Writer.AsStream());
        h._sp = services.BuildServiceProvider();
        h._serverTask = h._sp.GetRequiredService<McpServer>().RunAsync(h._cts.Token);
        h.Client = await McpClient.CreateAsync(new StreamClientTransport(h._c2s.Writer.AsStream(), h._s2c.Reader.AsStream()));
        return h;
    }

    public async Task<(bool IsError, string Text)> CallAsync(string tool, Dictionary<string, object?>? args = null)
    {
        var r = await Client.CallToolAsync(tool, args ?? []);
        return (r.IsError == true, string.Join("\n", r.Content.OfType<TextContentBlock>().Select(b => b.Text)));
    }

    public async Task<JsonElement> DataAsync(string tool, Dictionary<string, object?>? args = null)
    {
        var (isError, text) = await CallAsync(tool, args);
        Assert.False(isError, text);
        using var doc = JsonDocument.Parse(text);
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("summary").GetString()));
        Assert.Equal(JsonValueKind.False, doc.RootElement.GetProperty("stale").ValueKind);
        return doc.RootElement.GetProperty("data").Clone();
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _cts.CancelAsync();
        _c2s.Writer.Complete();
        _s2c.Writer.Complete();
        try { await _serverTask; } catch (OperationCanceledException) { }
        await _sp.DisposeAsync();
        _cts.Dispose();
    }
}
