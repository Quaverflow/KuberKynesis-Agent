using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public interface IKubePodExecRuntimeFactory
{
    Task<IKubePodExecRuntime> CreateAsync(KubePodExecStartRequest request, CancellationToken cancellationToken);
}
