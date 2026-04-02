using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubeActionImpactEngine
{
    public KubeActionImpactReport Build(KubeActionPreviewResponse preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return KubeActionImpactReportFactory.Build(preview);
    }

    public KubeActionPreviewResponse Attach(KubeActionPreviewResponse preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return preview with
        {
            ImpactReport = Build(preview)
        };
    }
}
