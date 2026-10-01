using System.Text;

namespace Depenk.Tests.TestUtil;

/// <summary>
/// N repos; each has an Api project (C controllers × E endpoints) and a Client project (C clients × E methods,
/// hand-written HttpClient style), plus a consumer class calling the clients of the next 3 repos.
/// </summary>
public static class SyntheticWorkspace
{
    public static TempWorkspace Create(int repos = 50, int controllers = 10, int endpointsPerController = 10)
    {
        var ws = new TempWorkspace();
        for (var r = 0; r < repos; r++)
        {
            var name = $"svc{r:D2}";
            ws.Repo(name);
            var deps = Enumerable.Range(1, 3).Select(k => $"svc{(r + k) % repos:D2}").ToList();

            ws.File($"{name}/src/{name}.Client/{name}.Client.csproj",
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><PackageId>{name}.Client</PackageId><Version>1.0.0</Version><IsPackable>true</IsPackable></PropertyGroup></Project>");
            ws.File($"{name}/src/{name}.Api/{name}.Api.csproj",
                $"<Project Sdk=\"Microsoft.NET.Sdk.Web\"><ItemGroup>{string.Concat(deps.Select(d => $"<PackageReference Include=\"{d}.Client\" Version=\"1.0.0\" />"))}<ProjectReference Include=\"../{name}.Client/{name}.Client.csproj\" /></ItemGroup></Project>");

            for (var c = 0; c < controllers; c++)
            {
                var ctl = new StringBuilder($"namespace {name}.Api;\n[ApiController]\n[Route(\"api/r{c}\")]\npublic class R{c}Controller : ControllerBase\n{{\n");
                var cli = new StringBuilder($"namespace {name}.Client;\npublic record R{c}Dto(Guid Id, string Name, List<R{c}LineDto> Lines);\npublic record R{c}LineDto(int Qty);\npublic class R{c}Client(HttpClient http)\n{{\n");
                for (var e = 0; e < endpointsPerController; e++)
                {
                    ctl.Append($"    [HttpGet(\"e{e}/{{id}}\")] public Task<ActionResult<R{c}Dto>> E{e}(Guid id) => null!;\n");
                    cli.Append($"    public Task<R{c}Dto?> E{e}Async(Guid id) => http.GetFromJsonAsync<R{c}Dto>($\"api/r{c}/e{e}/{{id}}\");\n");
                }
                ws.File($"{name}/src/{name}.Api/Controllers/R{c}Controller.cs", ctl.Append("}\n").ToString());
                ws.File($"{name}/src/{name}.Client/R{c}Client.cs", cli.Append("}\n").ToString());
            }

            var consumer = new StringBuilder($"namespace {name}.Api;\npublic class Consumer({string.Join(", ", deps.Select((d, i) => $"{d}.Client.R0Client c{i}"))})\n{{\n    public async Task Run()\n    {{\n");
            for (var i = 0; i < deps.Count; i++) consumer.Append($"        await c{i}.E0Async(Guid.Empty);\n");
            ws.File($"{name}/src/{name}.Api/Consumer.cs", consumer.Append("    }\n}\n").ToString());
        }
        return ws;
    }
}
