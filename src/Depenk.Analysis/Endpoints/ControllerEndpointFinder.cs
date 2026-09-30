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
        foreach (var (doc, cls) in src.All<ClassDeclarationSyntax>())
        {
            if (!IsController(cls)) continue;
            var className = cls.Identifier.Text;
            var classRoute = Attrs(cls).Where(a => AttrName(a) == "Route").Select(FirstStringArg).FirstOrDefault();

            foreach (var m in cls.Members.OfType<MethodDeclarationSyntax>())
            {
                if (!m.Modifiers.Any(SyntaxKind.PublicKeyword)) continue;
                var attrs = Attrs(m).ToList();
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
