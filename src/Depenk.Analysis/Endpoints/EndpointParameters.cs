using Depenk.Core.Model;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Depenk.Analysis.SyntaxHelpers;

namespace Depenk.Analysis.Endpoints;

internal static class EndpointParameters
{
    private static readonly HashSet<string> Skipped = ["CancellationToken", "HttpContext", "HttpRequest", "ClaimsPrincipal"];
    private static readonly HashSet<string> Simple =
        ["string", "int", "long", "short", "bool", "decimal", "double", "float", "Guid", "DateTime", "DateTimeOffset",
         "DateOnly", "TimeOnly", "byte", "char", "uint", "ulong"];
    private static readonly Dictionary<string, string> BindingAttrs = new()
    {
        ["FromBody"] = "body", ["FromQuery"] = "query", ["FromRoute"] = "route",
        ["FromHeader"] = "header", ["FromForm"] = "form",
    };

    public static List<EndpointParameter> From(IEnumerable<ParameterSyntax> ps, string verb, string route)
    {
        var list = new List<EndpointParameter>();
        foreach (var p in ps)
        {
            if (p.Type is null) continue;
            var type = TypeName(p.Type);
            var attrs = Attrs(p).Select(AttrName).ToList();
            if (attrs.Contains("FromServices") || Skipped.Contains(type.TrimEnd('?'))) continue;

            if (IsDiService(type, attrs)) continue;

            var name = p.Identifier.Text;
            var source = attrs.Select(a => BindingAttrs.GetValueOrDefault(a)).FirstOrDefault(s => s is not null)
                         ?? (RouteHas(route, name) ? "route"
                             : verb is "POST" or "PUT" or "PATCH" && !Simple.Contains(type.TrimEnd('?')) ? "body"
                             : "query");
            var required = p.Default is null && !type.EndsWith('?');
            list.Add(new EndpointParameter(name, source, type, required, p.Default?.Value.ToString()));
        }
        return list;
    }

    private static readonly HashSet<string> NotServices =
        ["IFormFile", "IFormFileCollection", "IEnumerable", "IList", "ICollection", "IReadOnlyList", "IReadOnlyCollection",
         "IDictionary", "IReadOnlyDictionary", "ISet", "IAsyncEnumerable"];

    /// <summary>An unattributed interface-typed parameter is a DI service, not request data.</summary>
    private static bool IsDiService(string type, List<string> attrs)
    {
        if (attrs.Any(BindingAttrs.ContainsKey)) return false;
        var t = type.TrimEnd('?');
        var lt = t.IndexOf('<');
        if (lt >= 0) t = t[..lt];
        t = t[(t.LastIndexOf('.') + 1)..];
        return t.Length > 1 && t[0] == 'I' && char.IsUpper(t[1]) && !NotServices.Contains(t);
    }

    private static bool RouteHas(string route, string name) =>
        route.Contains("{" + name + "}", StringComparison.OrdinalIgnoreCase)
        || route.Contains("{" + name + ":", StringComparison.OrdinalIgnoreCase)
        || route.Contains("{" + name + "?}", StringComparison.OrdinalIgnoreCase);
}
