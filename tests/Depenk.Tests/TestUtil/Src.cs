using Depenk.Analysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Depenk.Tests.TestUtil;

public static class Src
{
    public static SourceSet Set(params (string Path, string Code)[] files) =>
        SetFor("orders", "Orders.Api", files);

    public static SourceSet SetFor(string repo, string project, params (string Path, string Code)[] files) =>
        new(repo, $"proj:{repo}/{project}", project,
            files.Select(f => new SourceDoc(f.Path, CSharpSyntaxTree.ParseText(f.Code, path: f.Path))).ToList());
}
