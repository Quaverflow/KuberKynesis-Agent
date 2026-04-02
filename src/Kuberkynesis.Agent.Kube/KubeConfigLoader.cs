using k8s;
using k8s.KubeConfigModels;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubeConfigLoader : IKubeConfigLoader
{
    public KubeConfigLoadResult Load()
    {
        var sourcePaths = ResolveKubeConfigPaths();

        if (sourcePaths.Count is 0)
        {
            return new KubeConfigLoadResult(
                Configuration: null,
                SourcePaths: [],
                CurrentContextName: null,
                Contexts: [],
                Warnings:
                [
                    "No kubeconfig file was found. Set KUBECONFIG or create the default kubeconfig at ~/.kube/config."
                ]);
        }

        try
        {
            var configuration = LoadMergedKubeConfig(sourcePaths);

            var contexts = BuildDiscoveredContexts(configuration);

            return new KubeConfigLoadResult(
                Configuration: configuration,
                SourcePaths: sourcePaths,
                CurrentContextName: configuration.CurrentContext,
                Contexts: contexts,
                Warnings: []);
        }
        catch (Exception exception)
        {
            return new KubeConfigLoadResult(
                Configuration: null,
                SourcePaths: sourcePaths,
                CurrentContextName: null,
                Contexts: [],
                Warnings:
                [
                    $"Unable to load kubeconfig: {exception.Message}"
                ]);
        }
    }

    public Kubernetes CreateClient(KubeConfigLoadResult loadResult, string contextName)
    {
        if (loadResult.Configuration is null)
        {
            throw new InvalidOperationException("Kubeconfig is not available.");
        }

        var configuration = KubernetesClientConfiguration.BuildConfigFromConfigObject(
            loadResult.Configuration,
            currentContext: contextName,
            masterUrl: null);

        return new Kubernetes(configuration);
    }

    private static K8SConfiguration LoadMergedKubeConfig(IReadOnlyList<FileInfo> sourcePaths)
    {
        var configuration = KubernetesClientConfiguration.LoadKubeConfig(
            sourcePaths[0].FullName,
            useRelativePaths: true);

        for (var index = 1; index < sourcePaths.Count; index++)
        {
            var additionalConfiguration = KubernetesClientConfiguration.LoadKubeConfig(
                sourcePaths[index].FullName,
                useRelativePaths: true);

            configuration = MergeKubeConfig(configuration, additionalConfiguration);
        }

        return configuration;
    }

    private static K8SConfiguration MergeKubeConfig(K8SConfiguration primary, K8SConfiguration secondary)
    {
        return new K8SConfiguration
        {
            ApiVersion = primary.ApiVersion ?? secondary.ApiVersion,
            Kind = primary.Kind ?? secondary.Kind,
            CurrentContext = !string.IsNullOrWhiteSpace(primary.CurrentContext)
                ? primary.CurrentContext
                : secondary.CurrentContext,
            Preferences = MergePreferences(primary.Preferences, secondary.Preferences),
            Clusters = MergeNamed(primary.Clusters, secondary.Clusters, static cluster => cluster.Name),
            Contexts = MergeNamed(primary.Contexts, secondary.Contexts, static context => context.Name),
            Users = MergeNamed(primary.Users, secondary.Users, static user => user.Name),
            Extensions = MergeNamed(primary.Extensions, secondary.Extensions, static extension => extension.Name),
            FileName = primary.FileName ?? secondary.FileName
        };
    }

    private static IDictionary<string, object> MergePreferences(
        IDictionary<string, object>? primary,
        IDictionary<string, object>? secondary)
    {
        var merged = new Dictionary<string, object>(StringComparer.Ordinal);

        if (primary is not null)
        {
            foreach (var pair in primary)
            {
                merged[pair.Key] = pair.Value;
            }
        }

        if (secondary is not null)
        {
            foreach (var pair in secondary)
            {
                merged.TryAdd(pair.Key, pair.Value);
            }
        }

        return merged;
    }

    private static IReadOnlyList<T> MergeNamed<T>(
        IEnumerable<T>? primary,
        IEnumerable<T>? secondary,
        Func<T, string?> getName)
    {
        var comparer = StringComparerFromPlatform;
        var merged = new List<T>();
        var seenNames = new HashSet<string>(comparer);

        AddDistinct(primary);
        AddDistinct(secondary);

        return merged;

        void AddDistinct(IEnumerable<T>? items)
        {
            if (items is null)
            {
                return;
            }

            foreach (var item in items)
            {
                var name = getName(item);

                if (string.IsNullOrWhiteSpace(name) || seenNames.Add(name))
                {
                    merged.Add(item);
                }
            }
        }
    }

    internal static IReadOnlyList<FileInfo> ResolveKubeConfigPaths()
    {
        var configuredPaths = Environment.GetEnvironmentVariable("KUBECONFIG");
        var candidates = string.IsNullOrWhiteSpace(configuredPaths)
            ? [GetDefaultKubeConfigPath()]
            : configuredPaths
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static path => new FileInfo(path))
                .ToArray();

        return candidates
            .Where(static file => file.Exists)
            .Distinct(FileInfoPathComparer.Instance)
            .ToArray();
    }

    private static FileInfo GetDefaultKubeConfigPath()
    {
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new FileInfo(Path.Combine(homeDirectory, ".kube", "config"));
    }

    private static IReadOnlyList<DiscoveredKubeContext> BuildDiscoveredContexts(K8SConfiguration configuration)
    {
        var clusters = configuration.Clusters.ToDictionary(cluster => cluster.Name, StringComparer.Ordinal);
        var users = configuration.Users.ToDictionary(user => user.Name, StringComparer.Ordinal);

        return configuration.Contexts
            .Select(context =>
            {
                clusters.TryGetValue(context.ContextDetails.Cluster, out var cluster);
                users.TryGetValue(context.ContextDetails.User, out var user);

                var status = KubeContextStatus.Configured;
                string? statusMessage = null;

                try
                {
                    _ = KubernetesClientConfiguration.BuildConfigFromConfigObject(
                        configuration,
                        currentContext: context.Name,
                        masterUrl: null);
                }
                catch (Exception exception)
                {
                    status = KubeContextStatus.ConfigurationError;
                    statusMessage = exception.Message;
                }

                return new DiscoveredKubeContext(
                    Name: context.Name,
                    IsCurrent: string.Equals(context.Name, configuration.CurrentContext, StringComparison.Ordinal),
                    ClusterName: context.ContextDetails.Cluster,
                    Namespace: context.ContextDetails.Namespace,
                    UserName: context.ContextDetails.User,
                    Server: cluster?.ClusterEndpoint.Server,
                    Status: status,
                    StatusMessage: statusMessage ?? BuildImplicitStatusMessage(cluster, user));
            })
            .OrderByDescending(static context => context.IsCurrent)
            .ThenBy(static context => context.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? BuildImplicitStatusMessage(Cluster? cluster, User? user)
    {
        if (cluster is null)
        {
            return "The referenced cluster entry is missing from kubeconfig.";
        }

        if (user is null)
        {
            return "The referenced user entry is missing from kubeconfig.";
        }

        return null;
    }

    private sealed class FileInfoPathComparer : IEqualityComparer<FileInfo>
    {
        public static FileInfoPathComparer Instance { get; } = new();

        public bool Equals(FileInfo? x, FileInfo? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null)
            {
                return false;
            }

            return string.Equals(
                x.FullName,
                y.FullName,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        public int GetHashCode(FileInfo obj)
        {
            return StringComparerFromPlatform.GetHashCode(obj.FullName);
        }
    }

    private static StringComparer StringComparerFromPlatform =>
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
}
