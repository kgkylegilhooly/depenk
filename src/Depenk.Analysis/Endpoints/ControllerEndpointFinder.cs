using Depenk.Analysis.Routes;
using Depenk.Core;
using Depenk.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Depenk.Analysis.SyntaxHelpers;

namespace Depenk.Analysis.Endpoints;

public sealed class ControllerEndpointFinder : IEndpointFinder
{
    private static readonly Dictionary<string, string> VerbAttrs = new()
    {
        ["HttpGet"] = "GET", ["HttpPost"] = "POST", ["HttpPut"] = "PUT", ["HttpDelete"] = "DELETE", ["HttpPatch"] = "PATCH",
    };

    public IEnumerable<EndpointNode> Find(SourceSet src)
    {
        var classes = src.All<ClassDeclarationSyntax>().ToList();
        var byName = classes.ToLookup(c => c.Node.Identifier.Text, c => c.Node, StringComparer.Ordinal);

        foreach (var (doc, cls) in classes)
        {
            // abstract controllers are not endpoints themselves, but still lend their [Route] to derived classes
            if (cls.Modifiers.Any(SyntaxKind.AbstractKeyword)) continue;
            var bases = BaseChain(cls, byName);
            if (!IsController(cls) && !bases.Any(IsController)) continue;
            var className = cls.Identifier.Text;
            // [Route] is inherited: use the nearest class in the chain (self first) that declares one;
            // [controller] still expands to the derived class name.
            var classRoute = new[] { cls }.Concat(bases)
                .Select(c => Attrs(c).Where(a => AttrName(a) == "Route").Select(FirstStringArg).FirstOrDefault(r => r is not null))
                .FirstOrDefault(r => r is not null);

            foreach (var m in cls.Members.OfType<MethodDeclarationSyntax>())
            {
                if (!m.Modifiers.Any(SyntaxKind.PublicKeyword) || m.Modifiers.Any(SyntaxKind.StaticKeyword)) continue;
                var attrs = Attrs(m).ToList();
                if (attrs.Any(a => AttrName(a) == "NonAction")) continue;
                var methodRoute = attrs.Where(a => AttrName(a) == "Route").Select(FirstStringArg).FirstOrDefault();
                foreach (var verbAttr in attrs.Where(a => VerbAttrs.ContainsKey(AttrName(a))))
                {
                    var verb = VerbAttrs[AttrName(verbAttr)];
                    var template = FirstStringArg(verbAttr) ?? methodRoute;
                    var prefix = classRoute is null ? null : RouteNormalizer.ReplaceTokens(classRoute, className, m.Identifier.Text);
                    var tmpl = template is null ? null : RouteNormalizer.ReplaceTokens(template, className, m.Identifier.Text);
                    var route = RouteNormalizer.Combine(prefix, tmpl);

                    yield return new EndpointNode(
                        Ids.Endpoint(src.Repo, verb, route), src.Repo, src.ProjectId, verb, route,
                        RouteNormalizer.Normalize(route), $"{className}.{m.Identifier.Text}",
                        EndpointParameters.From(m.ParameterList.Parameters, verb, route),
                        Responses(m, attrs),
                        new SourceLocation(doc.RelativePath, Line(m.Identifier)));
                }
            }
        }
    }

    /// <summary>Base classes declared in the same source set, nearest first (cycle-safe).</summary>
    private static List<ClassDeclarationSyntax> BaseChain(ClassDeclarationSyntax cls, ILookup<string, ClassDeclarationSyntax> byName)
    {
        var chain = new List<ClassDeclarationSyntax>();
        var seen = new HashSet<ClassDeclarationSyntax> { cls };
        for (var current = cls; ;)
        {
            var next = current.BaseList?.Types.Select(t => SimpleName(t.Type))
                .Select(n => n is null ? null : byName[n].FirstOrDefault())
                .FirstOrDefault(c => c is not null);
            if (next is null || !seen.Add(next)) return chain;
            chain.Add(next);
            current = next;
        }
    }

    private static string? SimpleName(TypeSyntax t) => t switch
    {
        QualifiedNameSyntax q => SimpleName(q.Right),
        AliasQualifiedNameSyntax a => SimpleName(a.Name),
        GenericNameSyntax g => g.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        _ => null,
    };

    private static bool IsController(ClassDeclarationSyntax c) =>
        c.Identifier.Text.EndsWith("Controller", StringComparison.Ordinal)
        || Attrs(c).Any(a => AttrName(a) == "ApiController")
        || c.BaseList?.Types.Any(t => t.Type.ToString() is "ControllerBase" or "Controller") == true;

    private static List<ResponseType> Responses(MethodDeclarationSyntax m, List<AttributeSyntax> attrs)
    {
        var declared = attrs.Where(a => AttrName(a) == "ProducesResponseType").Select(a =>
        {
            var args = a.ArgumentList?.Arguments ?? default;
            var status = args.Select(x => StatusOf(x.Expression)).FirstOrDefault(s => s is not null) ?? 200;
            var type = args.Select(x => x.Expression).OfType<TypeOfExpressionSyntax>().Select(t => TypeName(t.Type)).FirstOrDefault()
                       ?? (a.Name as GenericNameSyntax)?.TypeArgumentList.Arguments.Select(TypeName).FirstOrDefault()
                       ?? "";
            return new ResponseType(status, type);
        }).ToList();
        if (declared.Count > 0) return declared;

        var ret = TypeName(m.ReturnType);
        return ret is "void" or "Task" or "ValueTask" or "IActionResult" or "Task<IActionResult>" or "ValueTask<IActionResult>"
            or "ActionResult" or "ValueTask<ActionResult>" or "ValueTask<IResult>"
            or "Task<ActionResult>" or "IResult" or "Task<IResult>"
            ? [] : [new ResponseType(200, ret)];
    }

    /// <summary>Numeric literal, or a member/identifier like StatusCodes.Status404NotFound.</summary>
    private static int? StatusOf(ExpressionSyntax e)
    {
        if (e is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.NumericLiteralExpression) && l.Token.Value is int n) return n;
        var name = e switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
            IdentifierNameSyntax id => id.Identifier.Text,
            _ => null,
        };
        var match = name is null ? null : System.Text.RegularExpressions.Regex.Match(name, @"^Status(\d{3})");
        return match is { Success: true } ? int.Parse(match.Groups[1].Value) : null;
    }
}
