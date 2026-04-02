using Xunit;

namespace Kuberkynesis.Agent.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AgentIntegrationCollection
{
    public const string Name = "Agent integration";
}
