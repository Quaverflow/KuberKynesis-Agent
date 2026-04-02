using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubeResourceGraphService(KubeResourceDetailService detailService)
{
    public async Task<KubeResourceGraphResponse> GetGraphAsync(
        KubeResourceGraphRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var detail = await detailService.GetDetailAsync(
            new KubeResourceDetailRequest
            {
                ContextName = request.ContextName,
                Kind = request.Kind,
                Namespace = request.Namespace,
                Name = request.Name
            },
            cancellationToken);

        return KubeResourceGraphFactory.Create(
            detail,
            KubectlTransparencyFactory.CreateForGraph(
                new KubeResourceDetailRequest
                {
                    ContextName = request.ContextName,
                    Kind = request.Kind,
                    Namespace = request.Namespace,
                    Name = request.Name
                }));
    }
}
