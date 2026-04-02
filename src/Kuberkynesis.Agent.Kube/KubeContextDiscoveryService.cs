using k8s;
using k8s.Autorest;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubeContextDiscoveryService
{
    private readonly IKubeConfigLoader kubeConfigLoader;

    public KubeContextDiscoveryService(IKubeConfigLoader kubeConfigLoader)
    {
        this.kubeConfigLoader = kubeConfigLoader;
    }

    public async Task<KubeContextsResponse> GetContextsAsync(CancellationToken cancellationToken = default)
    {
        var loadResult = kubeConfigLoader.Load();
        var contexts = await ProbeContextsAsync(loadResult, cancellationToken);
        var contextSummaries = contexts
            .Select(static context => new KubeContextSummary(
                context.Name,
                context.IsCurrent,
                context.ClusterName,
                context.Namespace,
                context.UserName,
                context.Server,
                context.Status,
                context.StatusMessage))
            .ToArray();

        return new KubeContextsResponse(
            CurrentContextName: loadResult.CurrentContextName,
            SourcePaths: loadResult.SourcePaths.Select(path => path.FullName).ToArray(),
            Contexts: contextSummaries,
            Warnings: loadResult.Warnings)
        {
            Clusters = KubeClusterSummaryFactory.Build(contextSummaries)
        };
    }

    private async Task<IReadOnlyList<DiscoveredKubeContext>> ProbeContextsAsync(
        KubeConfigLoadResult loadResult,
        CancellationToken cancellationToken)
    {
        if (loadResult.Configuration is null || loadResult.Contexts.Count is 0)
        {
            return loadResult.Contexts;
        }

        var probedContexts = new List<DiscoveredKubeContext>(loadResult.Contexts.Count);

        foreach (var context in loadResult.Contexts)
        {
            if (context.Status is not KubeContextStatus.Configured)
            {
                probedContexts.Add(context);
                continue;
            }

            try
            {
                using var client = kubeConfigLoader.CreateClient(loadResult, context.Name);
                using var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeCancellation.CancelAfter(TimeSpan.FromSeconds(5));

                await client.ListNamespaceAsync(limit: 1, cancellationToken: probeCancellation.Token);
                probedContexts.Add(context with { StatusMessage = null });
            }
            catch (HttpOperationException exception)
            {
                var classified = KubeContextProbeClassifier.ClassifyProbeFailure(
                    context.Name,
                    exception.Response.StatusCode,
                    exception.Message);

                probedContexts.Add(context with
                {
                    Status = classified.Status,
                    StatusMessage = classified.StatusMessage
                });
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var classified = KubeContextProbeClassifier.ClassifyProbeFailure(
                    context.Name,
                    statusCode: null,
                    message: "The startup probe timed out.");

                probedContexts.Add(context with
                {
                    Status = classified.Status,
                    StatusMessage = classified.StatusMessage
                });
            }
            catch (Exception exception)
            {
                var classified = KubeContextProbeClassifier.ClassifyProbeFailure(
                    context.Name,
                    statusCode: null,
                    message: exception.Message);

                probedContexts.Add(context with
                {
                    Status = classified.Status,
                    StatusMessage = classified.StatusMessage
                });
            }
        }

        return probedContexts;
    }
}
