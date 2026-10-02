using Depenk.Analysis;
using Depenk.Core.Model;
using Depenk.Query;

namespace Depenk.Tests.TestUtil;

/// <summary>The Plan 1 fixture workspace, scanned once per test run and shared read-only.</summary>
public static class FixtureGraph
{
    private static readonly Lazy<DepGraph> Graph = new(() =>
    {
        using var ws = FixtureScanTests.CopyFixture();
        return new ScanOrchestrator().Scan(ws.Root);
    });

    public static DepGraph Value => Graph.Value;
    public static GraphIndex Index => new(Graph.Value);
    public static QueryService Query => new(Index);
}
