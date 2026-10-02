using System.Reflection;
using System.Text.Json;
using Depenk.Scanning.Config;

namespace Depenk.Tests.Scanning;

/// <summary>The published schema must describe exactly the properties DepenkConfig binds — no more, no fewer.</summary>
public class ConfigSchemaTests
{
    private static JsonElement Schema()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Depenk.sln"))) dir = dir.Parent;
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "schemas", "depenk.schema.json"))).RootElement;
    }

    private static string Camel(string s) => char.ToLowerInvariant(s[0]) + s[1..];

    private static string[] PropertyNames(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => Camel(p.Name)).Order().ToArray();

    private static string[] SchemaNames(JsonElement obj) =>
        obj.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order().ToArray();

    [Fact]
    public void TopLevel_MatchesDepenkConfig()
    {
        var s = Schema();
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", s.GetProperty("$schema").GetString());
        Assert.False(s.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(PropertyNames(typeof(DepenkConfig)), SchemaNames(s));
    }

    [Theory]
    [InlineData("repos", typeof(RepoFilter))]
    [InlineData("projects", typeof(ProjectOptions))]
    [InlineData("packages", typeof(PackageOptions))]
    [InlineData("routes", typeof(RouteOptions))]
    public void Sections_MatchOptionClasses(string section, Type type)
    {
        var obj = Schema().GetProperty("properties").GetProperty(section);
        Assert.False(obj.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(PropertyNames(type), SchemaNames(obj));
    }

    [Fact]
    public void HttpWrapperItems_MatchHttpWrapperConfig()
    {
        var items = Schema().GetProperty("properties").GetProperty("httpWrappers").GetProperty("items");
        Assert.Equal(PropertyNames(typeof(HttpWrapperConfig)), SchemaNames(items));
        Assert.Equal(["methods", "type"], items.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).Order());
    }
}
