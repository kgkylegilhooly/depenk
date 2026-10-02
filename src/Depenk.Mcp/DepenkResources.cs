using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Depenk.Mcp;

[McpServerResourceType]
public sealed class DepenkResources(GraphStore store)
{
    [McpServerResource(UriTemplate = "depenk://overview", Name = "overview", MimeType = "text/markdown")]
    [Description("Markdown summary of the workspace: repos, service links, hotspots and diagnostic counts. Read it at the start of a session.")]
    public string Overview()
    {
        var snapshot = DepenkTools.CurrentSnapshot(store);
        try { return snapshot.Query.Overview(); }
        catch (Exception e) when (DepenkTools.IsToolFailure(e)) { throw DepenkTools.Fail(e, duringScan: false); }
    }
}
