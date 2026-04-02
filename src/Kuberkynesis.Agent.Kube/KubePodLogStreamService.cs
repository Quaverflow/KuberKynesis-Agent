using k8s;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubePodLogStreamService
{
    private const int MaxBufferedLogLinesPerMessage = 20;
    private static readonly TimeSpan LogBatchWindow = TimeSpan.FromMilliseconds(75);

    private readonly IKubeConfigLoader kubeConfigLoader;
    private readonly KubePodLogService podLogService;

    public KubePodLogStreamService(IKubeConfigLoader kubeConfigLoader, KubePodLogService podLogService)
    {
        this.kubeConfigLoader = kubeConfigLoader;
        this.podLogService = podLogService;
    }

    public async Task StreamAsync(
        KubePodLogRequest request,
        Func<KubePodLogStreamMessage, CancellationToken, Task> onMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(onMessage);

        var snapshot = await podLogService.GetLogsAsync(request, cancellationToken);
        await onMessage(
            new KubePodLogStreamMessage(
                MessageType: KubePodLogStreamMessageType.Snapshot,
                OccurredAtUtc: DateTimeOffset.UtcNow,
                Snapshot: snapshot,
                AppendContent: null,
                ErrorMessage: null),
            cancellationToken);

        var loadResult = kubeConfigLoader.Load();

        if (loadResult.Contexts.Count is 0)
        {
            return;
        }

        var context = KubeResourceQueryService.ResolveTargetContexts([request.ContextName], loadResult).Single();

        if (context.Status is KubeContextStatus.ConfigurationError)
        {
            throw new ArgumentException(context.StatusMessage ?? $"The kube context '{context.Name}' is invalid.");
        }

        using var client = kubeConfigLoader.CreateClient(loadResult, context.Name);
        var pod = await client.ReadNamespacedPodAsync(
            request.PodName.Trim(),
            request.Namespace.Trim(),
            cancellationToken: cancellationToken);
        var availableContainers = KubePodLogService.GetAvailableContainers(pod);
        var containerName = KubePodLogService.ResolveContainerName(request.ContainerName, availableContainers);

        await using var stream = await client.ReadNamespacedPodLogAsync(
            name: request.PodName.Trim(),
            namespaceParameter: request.Namespace.Trim(),
            container: containerName,
            follow: true,
            sinceSeconds: 1,
            timestamps: true,
            cancellationToken: cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await ReadBufferedAppendAsync(reader, cancellationToken);

            if (batch is null)
            {
                break;
            }

            await onMessage(
                new KubePodLogStreamMessage(
                    MessageType: KubePodLogStreamMessageType.Append,
                    OccurredAtUtc: DateTimeOffset.UtcNow,
                    Snapshot: null,
                    AppendContent: batch.Content,
                    ErrorMessage: null,
                    BatchedLineCount: batch.LineCount),
                cancellationToken);
        }
    }

    internal static async Task<BufferedLogAppend?> ReadBufferedAppendAsync(TextReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var firstLine = await reader.ReadLineAsync(cancellationToken);

            if (firstLine is null)
            {
                return null;
            }

            if (firstLine.Length is 0)
            {
                continue;
            }

            return await ReadBufferedAppendAsync(reader, firstLine, cancellationToken);
        }
    }

    internal static async Task<BufferedLogAppend> ReadBufferedAppendAsync(
        TextReader reader,
        string firstLine,
        CancellationToken cancellationToken)
    {
        var lines = new List<string> { firstLine };

        while (lines.Count < MaxBufferedLogLinesPerMessage)
        {
            using var batchWindowCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            batchWindowCancellation.CancelAfter(LogBatchWindow);

            try
            {
                var nextLine = await reader.ReadLineAsync(batchWindowCancellation.Token);

                if (nextLine is null)
                {
                    break;
                }

                if (nextLine.Length is 0)
                {
                    continue;
                }

                lines.Add(nextLine);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        return new BufferedLogAppend(
            Content: string.Join(Environment.NewLine, lines) + Environment.NewLine,
            LineCount: lines.Count);
    }

    internal sealed record BufferedLogAppend(string Content, int LineCount);
}
