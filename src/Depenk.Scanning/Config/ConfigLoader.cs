using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Depenk.Scanning.Config;

public sealed class ConfigException(string message) : Exception(message);

public static class ConfigLoader
{
    public const string FileName = "depenk.yml";

    public static DepenkConfig Load(string workspace)
    {
        var path = Path.Combine(workspace, FileName);
        if (!File.Exists(path)) return new DepenkConfig();

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        try
        {
            return deserializer.Deserialize<DepenkConfig>(File.ReadAllText(path)) ?? new DepenkConfig();
        }
        catch (YamlException ex)
        {
            throw new ConfigException($"{FileName}({ex.Start.Line}): {ex.InnerException?.Message ?? ex.Message}");
        }
    }
}
