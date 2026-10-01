using Depenk.Core.Model;

namespace Depenk.Analysis.Endpoints;

public interface IEndpointFinder
{
    IEnumerable<EndpointNode> Find(SourceSet src);
}
