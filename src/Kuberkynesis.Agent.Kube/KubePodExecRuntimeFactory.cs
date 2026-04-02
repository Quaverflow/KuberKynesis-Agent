using System.Text;
using k8s;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubePodExecRuntimeFactory : IKubePodExecRuntimeFactory
{
    private readonly IKubeConfigLoader kubeConfigLoader;

    public KubePodExecRuntimeFactory(IKubeConfigLoader kubeConfigLoader)
    {
        this.kubeConfigLoader = kubeConfigLoader;
    }

    public async Task<IKubePodExecRuntime> CreateAsync(KubePodExecStartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ContextName))
        {
            throw new ArgumentException("A kube context name is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            throw new ArgumentException("A namespace is required for pod exec.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.PodName))
        {
            throw new ArgumentException("A pod name is required for pod exec.", nameof(request));
        }

        if (request.Command.Count is 0 || request.Command.All(static part => string.IsNullOrWhiteSpace(part)))
        {
            throw new ArgumentException("At least one exec command segment is required.", nameof(request));
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

        var client = kubeConfigLoader.CreateClient(loadResult, context.Name);

        try
        {
            var pod = await client.ReadNamespacedPodAsync(
                request.PodName.Trim(),
                request.Namespace.Trim(),
                cancellationToken: cancellationToken);
            var availableContainers = KubePodLogService.GetAvailableContainers(pod);
            var resolvedContainerName = KubePodLogService.ResolveContainerName(request.ContainerName, availableContainers);
            var command = request.Command
                .Where(static part => !string.IsNullOrWhiteSpace(part))
                .Select(static part => part.Trim())
                .ToArray();

            var demuxer = await client.MuxedStreamNamespacedPodExecAsync(
                request.PodName.Trim(),
                request.Namespace.Trim(),
                command,
                container: resolvedContainerName,
                stderr: true,
                stdin: true,
                stdout: true,
                tty: false,
                cancellationToken: cancellationToken);

            demuxer.Start();

            return new KubePodExecRuntime(
                client,
                demuxer,
                new KubeResourceIdentity(context.Name, KubeResourceKind.Pod, request.Namespace.Trim(), request.PodName.Trim()),
                resolvedContainerName,
                command,
                KubectlTransparencyFactory.CreateForPodExec(
                    context.Name,
                    request.Namespace.Trim(),
                    request.PodName.Trim(),
                    resolvedContainerName,
                    command));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private sealed class KubePodExecRuntime : IKubePodExecRuntime
    {
        private readonly Kubernetes client;
        private readonly IStreamDemuxer demuxer;
        private readonly Stream stdin;

        public KubePodExecRuntime(
            Kubernetes client,
            IStreamDemuxer demuxer,
            KubeResourceIdentity resource,
            string? containerName,
            IReadOnlyList<string> command,
            IReadOnlyList<KubectlCommandPreview> transparencyCommands)
        {
            this.client = client;
            this.demuxer = demuxer;
            Resource = resource;
            ContainerName = containerName;
            Command = command;
            TransparencyCommands = transparencyCommands;
            stdin = demuxer.GetStream(inputIndex: null, outputIndex: ChannelIndex.StdIn);
            StdOut = demuxer.GetStream(inputIndex: ChannelIndex.StdOut, outputIndex: null);
            StdErr = demuxer.GetStream(inputIndex: ChannelIndex.StdErr, outputIndex: null);
            Status = demuxer.GetStream(inputIndex: ChannelIndex.Error, outputIndex: null);
        }

        public KubeResourceIdentity Resource { get; }

        public string? ContainerName { get; }

        public IReadOnlyList<string> Command { get; }

        public IReadOnlyList<KubectlCommandPreview> TransparencyCommands { get; }

        public Stream StdOut { get; }

        public Stream StdErr { get; }

        public Stream Status { get; }

        public async Task SendInputAsync(string text, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(text);

            if (text.Length is 0)
            {
                return;
            }

            var buffer = Encoding.UTF8.GetBytes(text);
            await stdin.WriteAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            await stdin.FlushAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await stdin.DisposeAsync();
            }
            catch
            {
            }

            try
            {
                await StdOut.DisposeAsync();
            }
            catch
            {
            }

            try
            {
                await StdErr.DisposeAsync();
            }
            catch
            {
            }

            try
            {
                await Status.DisposeAsync();
            }
            catch
            {
            }

            demuxer.Dispose();
            client.Dispose();
        }
    }
}
