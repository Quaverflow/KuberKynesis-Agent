using System.Net;
using k8s.Autorest;
using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeResourceDetailServiceTests
{
    [Fact]
    public async Task TryLoadOptionalRelatedResourcesAsync_ReturnsResourcesWithoutWarningsWhenQuerySucceeds()
    {
        IReadOnlyList<KubeRelatedResource> relatedResources =
        [
            new KubeRelatedResource(
                Relationship: "Selected by service",
                Kind: KubeResourceKind.Service,
                ApiVersion: "v1",
                Name: "orders-api",
                Namespace: "orders-prod",
                Status: "Active",
                Summary: "ClusterIP / 80/TCP")
        ];

        var result = await KubeResourceDetailService.TryLoadOptionalRelatedResourcesAsync(
            "kind-kuberkynesis-lab",
            "service relationships for pod 'orders-api'",
            () => Task.FromResult<IReadOnlyList<KubeRelatedResource>>(relatedResources));

        Assert.Single(result.RelatedResources);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task TryLoadOptionalRelatedResourcesAsync_ReturnsWarningWhenQueryIsForbidden()
    {
        var result = await KubeResourceDetailService.TryLoadOptionalRelatedResourcesAsync(
            "kind-kuberkynesis-lab",
            "ingress relationships for pod 'orders-api'",
            () => throw new HttpOperationException("Forbidden")
            {
                Response = new HttpResponseMessageWrapper(
                    new HttpResponseMessage(HttpStatusCode.Forbidden),
                    null)
            });

        Assert.Empty(result.RelatedResources);
        var warning = Assert.Single(result.Warnings);
        Assert.Equal("kind-kuberkynesis-lab", warning.ContextName);
        Assert.Contains("ingress relationships for pod 'orders-api'", warning.Message, StringComparison.Ordinal);
        Assert.Contains("not allowed", warning.Message, StringComparison.OrdinalIgnoreCase);
    }
}
