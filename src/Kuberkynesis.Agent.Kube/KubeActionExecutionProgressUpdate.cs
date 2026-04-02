namespace Kuberkynesis.Agent.Kube;

public sealed record KubeActionExecutionProgressUpdate(
    string StatusText,
    string Summary)
{
    public bool CanCancel { get; init; } = true;
}
