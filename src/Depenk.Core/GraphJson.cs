using System.Text.Json;
using System.Text.Json.Serialization;
using Depenk.Core.Model;

namespace Depenk.Core;

public static class GraphJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(DepGraph g) => JsonSerializer.Serialize(g, Options);

    public static DepGraph Deserialize(string json) =>
        JsonSerializer.Deserialize<DepGraph>(json, Options) ?? throw new InvalidDataException("Empty graph JSON");

    public static void Save(DepGraph g, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Serialize(g));
    }

    public static DepGraph Load(string path) => Deserialize(File.ReadAllText(path));
}
