using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depenk.Analysis.Models;

public sealed record TypeDecl(SourceSet Source, SourceDoc Doc, BaseTypeDeclarationSyntax Node, string FullName);

public sealed class TypeIndex
{
    private readonly ILookup<string, TypeDecl> _byName;

    public TypeIndex(IEnumerable<SourceSet> sources) =>
        _byName = sources
            .SelectMany(s => s.All<BaseTypeDeclarationSyntax>()
                .Where(t => t.Node is ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax or EnumDeclarationSyntax)
                .Select(t => new TypeDecl(s, t.Doc, t.Node, FullNameOf(t.Node))))
            .ToLookup(d => d.Node.Identifier.Text, StringComparer.Ordinal);

    public IEnumerable<TypeDecl> All => _byName.SelectMany(g => g);

    public TypeDecl? Resolve(string simpleName, string fromProjectId, IReadOnlyList<string> referencedProjectIds)
    {
        var candidates = _byName[simpleName].ToList();
        if (candidates.Count == 0) return null;
        var fromRepo = RepoOf(fromProjectId);
        return candidates.FirstOrDefault(c => c.Source.ProjectId == fromProjectId)
               ?? referencedProjectIds.Select(r => candidates.FirstOrDefault(c => c.Source.ProjectId == r)).FirstOrDefault(c => c is not null)
               ?? candidates.FirstOrDefault(c => c.Source.Repo == fromRepo)
               ?? candidates[0];
    }

    /// <summary>"proj:{repo}/{name}" to repo.</summary>
    private static string RepoOf(string projectId) => projectId["proj:".Length..].Split('/')[0];

    public static string FullNameOf(BaseTypeDeclarationSyntax t)
    {
        var parts = new List<string> { t.Identifier.Text };
        foreach (var anc in t.Ancestors())
        {
            switch (anc)
            {
                case BaseTypeDeclarationSyntax outer: parts.Add(outer.Identifier.Text); break;
                case BaseNamespaceDeclarationSyntax ns: parts.Add(ns.Name.ToString()); break;
            }
        }
        parts.Reverse();
        return string.Join('.', parts);
    }
}
