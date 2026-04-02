using k8s;
using k8s.Models;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubePodLogService
{
    private const int DefaultTailLines = 200;
    private const int MaxTailLines = 1000;

    private readonly IKubeConfigLoader kubeConfigLoader;

    public KubePodLogService(IKubeConfigLoader kubeConfigLoader)
    {
        this.kubeConfigLoader = kubeConfigLoader;
    }

    public async Task<KubePodLogResponse> GetLogsAsync(KubePodLogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ContextName))
        {
            throw new ArgumentException("A kube context name is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            throw new ArgumentException("A namespace is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.PodName))
        {
            throw new ArgumentException("A pod name is required.", nameof(request));
        }

        var loadResult = kubeConfigLoader.Load();

        if (loadResult.Contexts.Count is 0)
        {
            throw new ArgumentException("No kube contexts were found.");
        }

        var context = KubeResourceQueryService.ResolveTargetContexts([request.ContextName], loadResult).Single();

        if (context.Status is KubeContextStatus.ConfigurationError)
        {
            throw new ArgumentException(context.StatusMessage ?? $"The kube context '{context.Name}' is invalid.");
        }

        using var client = kubeConfigLoader.CreateClient(loadResult, context.Name);
        var pod = await client.ReadNamespacedPodAsync(request.PodName.Trim(), request.Namespace.Trim(), cancellationToken: cancellationToken);
        var availableContainers = GetAvailableContainers(pod);
        var resolvedContainerName = ResolveContainerName(request.ContainerName, availableContainers);
        var tailLines = NormalizeTailLines(request.TailLines);
        await using var logStream = await client.ReadNamespacedPodLogAsync(
            name: request.PodName.Trim(),
            namespaceParameter: request.Namespace.Trim(),
            container: resolvedContainerName,
            timestamps: true,
            tailLines: tailLines,
            cancellationToken: cancellationToken);
        using var reader = new StreamReader(logStream);
        var logContent = await reader.ReadToEndAsync(cancellationToken);

        return new KubePodLogResponse(
            ContextName: context.Name,
            Namespace: request.Namespace.Trim(),
            PodName: request.PodName.Trim(),
            ContainerName: resolvedContainerName,
            TailLinesApplied: tailLines,
            AvailableContainers: availableContainers,
            Content: logContent,
            TransparencyCommands: KubectlTransparencyFactory.CreateForPodLogs(
                request with
                {
                    ContextName = context.Name,
                    Namespace = request.Namespace.Trim(),
                    PodName = request.PodName.Trim(),
                    TailLines = tailLines
                },
                resolvedContainerName,
                containerWasAutoSelected: string.IsNullOrWhiteSpace(request.ContainerName)),
            Warnings: []);
    }

    internal static int NormalizeTailLines(int requestedTailLines)
    {
        if (requestedTailLines <= 0)
        {
            return DefaultTailLines;
        }

        return Math.Min(requestedTailLines, MaxTailLines);
    }

    internal static IReadOnlyList<string> GetAvailableContainers(V1Pod pod)
    {
        return (pod.Spec?.Containers ?? [])
            .Concat(pod.Spec?.InitContainers ?? [])
            .Select(container => container.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    internal static string ResolveContainerName(string? requestedContainerName, IReadOnlyList<string> availableContainers)
    {
        if (availableContainers.Count is 0)
        {
            throw new ArgumentException("The selected pod has no readable containers.");
        }

        if (string.IsNullOrWhiteSpace(requestedContainerName))
        {
            return availableContainers[0];
        }

        var match = availableContainers.FirstOrDefault(container =>
            string.Equals(container, requestedContainerName.Trim(), StringComparison.Ordinal));

        return match ?? throw new ArgumentException($"The pod does not contain a container named '{requestedContainerName}'.");
    }
}
