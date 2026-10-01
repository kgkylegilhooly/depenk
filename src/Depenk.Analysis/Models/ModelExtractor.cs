using Depenk.Core;
using Depenk.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Depenk.Analysis.SyntaxHelpers;

namespace Depenk.Analysis.Models;

public sealed class ModelExtractor(
    IReadOnlyList<SourceSet> sources, Func<string, IReadOnlyList<string>> referencedProjectIds, int maxDepth = 6)
{
    private readonly TypeIndex _index = new(sources);

    public void Extract(DepGraph graph, IReadOnlySet<string> contractProjectIds)
    {
        var models = graph.Models.ToDictionary(m => m.Id);
        var edges = new HashSet<Edge>(graph.Edges);
        void AddEdge(Edge e) { if (edges.Add(e)) graph.Edges.Add(e); }

        string Ensure(TypeRef tr, string fromProjectId, int depth)
        {
            var decl = _index.Resolve(tr.Name, fromProjectId, referencedProjectIds(fromProjectId));
            if (decl is null)
            {
                var opaqueId = Ids.Model("?", tr.Name);
                if (!models.ContainsKey(opaqueId))
                {
                    models[opaqueId] = new ModelNode(opaqueId, "", null, tr.Name, ModelKind.Opaque, [], null, null);
                    graph.Models.Add(models[opaqueId]);
                }
                return opaqueId;
            }
            return EnsureDecl(decl, depth);
        }

        string EnsureDecl(TypeDecl decl, int depth)
        {
            var id = Ids.Model(decl.Source.ProjectName, decl.FullName);
            if (models.ContainsKey(id)) return id;

            var (kind, fields, enumValues) = Describe(decl);
            var node = new ModelNode(id, decl.Source.Repo, decl.Source.ProjectId, decl.FullName, kind, fields, enumValues,
                new SourceLocation(decl.Doc.RelativePath, Line(decl.Node.Identifier)));
            models[id] = node;          // register before recursing: cycles terminate
            graph.Models.Add(node);

            if (depth >= maxDepth) return id;
            foreach (var f in fields)
            foreach (var child in TypeUnwrapper.Unwrap(f.TypeName))
                AddEdge(new Edge(EdgeKind.FieldOf, id, Ensure(child, decl.Source.ProjectId, depth + 1), Confidence.High)
                    { FieldName = f.Name });
            return id;
        }

        foreach (var ep in graph.Endpoints.ToList())
        {
            foreach (var p in ep.Parameters)
            foreach (var tr in TypeUnwrapper.Unwrap(p.TypeName))
                AddEdge(new Edge(EdgeKind.Accepts, ep.Id, Ensure(tr, ep.ProjectId, 1), Confidence.High) { Source = p.Source });
            foreach (var r in ep.Responses)
            foreach (var tr in TypeUnwrapper.Unwrap(r.TypeName))
                AddEdge(new Edge(EdgeKind.Returns, ep.Id, Ensure(tr, ep.ProjectId, 1), Confidence.High) { StatusCode = r.StatusCode });
        }

        foreach (var decl in _index.All.Where(d => contractProjectIds.Contains(d.Source.ProjectId)
                                                  && d.Node.Modifiers.Any(SyntaxKind.PublicKeyword)))
            EnsureDecl(decl, 1);
    }

    private (ModelKind, List<ModelField>, List<string>?) Describe(TypeDecl decl)
    {
        if (decl.Node is EnumDeclarationSyntax en)
            return (ModelKind.Enum, [], en.Members.Select(m => m.Identifier.Text).ToList());

        var type = (TypeDeclarationSyntax)decl.Node;
        var kind = type switch
        {
            RecordDeclarationSyntax => ModelKind.Record,
            StructDeclarationSyntax => ModelKind.Struct,
            _ => ModelKind.Class,
        };
        var fields = new List<ModelField>();
        if (type.ParameterList is { } positional)
            fields.AddRange(positional.Parameters.Where(p => p.Type is not null).Select(p => Field(p.Identifier.Text, p.Type!)));
        fields.AddRange(PublicProperties(type));

        var baseDecl = DeclaredTypes.BaseTypes(type)
            .Select(b => _index.Resolve(b.Split('<')[0], decl.Source.ProjectId, referencedProjectIds(decl.Source.ProjectId)))
            .FirstOrDefault(d => d?.Node is ClassDeclarationSyntax or RecordDeclarationSyntax);
        if (baseDecl?.Node is TypeDeclarationSyntax baseType)
            fields.AddRange(PublicProperties(baseType).Where(bf => fields.All(f => f.Name != bf.Name)));
        return (kind, fields, null);
    }

    private static IEnumerable<ModelField> PublicProperties(TypeDeclarationSyntax t) =>
        t.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Modifiers.Any(SyntaxKind.PublicKeyword) && !p.Modifiers.Any(SyntaxKind.StaticKeyword)
                        && (p.ExpressionBody is not null
                            || p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) == true))
            .Select(p => Field(p.Identifier.Text, p.Type));

    private static ModelField Field(string name, TypeSyntax type)
    {
        var text = TypeName(type);
        return new ModelField(name, text, text.EndsWith('?'), TypeUnwrapper.Unwrap(text).Any(r => r.Collection));
    }
}
