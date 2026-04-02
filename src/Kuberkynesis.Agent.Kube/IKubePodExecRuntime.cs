using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public interface IKubePodExecRuntime : IAsyncDisposable
{
    KubeResourceIdentity Resource { get; }

    string? ContainerName { get; }

    IReadOnlyList<string> Command { get; }

    IReadOnlyList<KubectlCommandPreview> TransparencyCommands { get; }

    Stream StdOut { get; }

    Stream StdErr { get; }

    Stream Status { get; }

    Task SendInputAsync(string text, CancellationToken cancellationToken);
}
