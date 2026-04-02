using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public interface IKubeActionExecutionService
{
    Task<KubeActionExecuteResponse> ExecuteAsync(KubeActionExecuteRequest request, CancellationToken cancellationToken);

    Task<KubeActionExecuteResponse> ExecuteAsync(
        KubeActionExecuteRequest request,
        Action<KubeActionExecutionProgressUpdate>? reportProgress,
        CancellationToken cancellationToken);
}
