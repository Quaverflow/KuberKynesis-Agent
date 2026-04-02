using k8s.Models;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

internal static class KubeActionEnvironmentClassifier
{
    public static KubeActionEnvironmentKind Classify(
        V1ObjectMeta? metadata,
        string? contextName = null,
        KubeActionLocalEnvironmentRules? localRules = null)
    {
        return Classify(
            contextName,
            metadata?.NamespaceProperty,
            metadata?.Labels,
            metadata?.Annotations,
            localRules);
    }

    public static KubeActionEnvironmentKind Classify(
        string? contextName,
        string? namespaceName,
        IDictionary<string, string>? labels,
        IDictionary<string, string>? annotations = null,
        KubeActionLocalEnvironmentRules? localRules = null)
    {
        if (TryClassifyFromMap(annotations, out var annotationEnvironment))
        {
            return annotationEnvironment;
        }

        if (TryClassifyFromNamespace(namespaceName, out var namespaceEnvironment))
        {
            return namespaceEnvironment;
        }

        if (TryClassifyFromMap(labels, out var labelEnvironment))
        {
            return labelEnvironment;
        }

        if (TryClassifyFromLocalRules(contextName, namespaceName, localRules, out var localEnvironment))
        {
            return localEnvironment;
        }

        return KubeActionEnvironmentKind.Unknown;
    }

    private static bool TryClassifyFromNamespace(string? namespaceName, out KubeActionEnvironmentKind environment)
    {
        environment = KubeActionEnvironmentKind.Unknown;

        if (string.IsNullOrWhiteSpace(namespaceName))
        {
            return false;
        }

        return TryClassifyValue(namespaceName, out environment);
    }

    private static bool TryClassifyFromMap(
        IDictionary<string, string>? values,
        out KubeActionEnvironmentKind environment)
    {
        environment = KubeActionEnvironmentKind.Unknown;

        if (values is null || values.Count is 0)
        {
            return false;
        }

        foreach (var key in new[]
                 {
                     "kuberkynesis.io/environment",
                     "environment",
                     "env",
                     "app.kubernetes.io/environment"
                 })
        {
            if (values.TryGetValue(key, out var value) &&
                TryClassifyValue(value, out environment))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryClassifyFromLocalRules(
        string? contextName,
        string? namespaceName,
        KubeActionLocalEnvironmentRules? localRules,
        out KubeActionEnvironmentKind environment)
    {
        environment = KubeActionEnvironmentKind.Unknown;

        if (localRules is null)
        {
            return false;
        }

        if (MatchesAny(localRules.ProductionMatchers, contextName, namespaceName))
        {
            environment = KubeActionEnvironmentKind.Production;
            return true;
        }

        if (MatchesAny(localRules.StagingMatchers, contextName, namespaceName))
        {
            environment = KubeActionEnvironmentKind.Staging;
            return true;
        }

        if (MatchesAny(localRules.DevelopmentMatchers, contextName, namespaceName))
        {
            environment = KubeActionEnvironmentKind.Development;
            return true;
        }

        return false;
    }

    private static bool MatchesAny(
        IEnumerable<string> matchers,
        params string?[] candidates)
    {
        var normalizedMatchers = matchers
            .Where(static matcher => !string.IsNullOrWhiteSpace(matcher))
            .Select(static matcher => matcher.Trim())
            .Where(static matcher => matcher.Length > 0)
            .ToArray();

        if (normalizedMatchers.Length is 0)
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            foreach (var matcher in normalizedMatchers)
            {
                if (candidate.Contains(matcher, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryClassifyValue(string? rawValue, out KubeActionEnvironmentKind environment)
    {
        environment = KubeActionEnvironmentKind.Unknown;

        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return false;
        }

        var normalized = rawValue.Trim().ToLowerInvariant();

        if (normalized.Contains("prod", StringComparison.Ordinal))
        {
            environment = KubeActionEnvironmentKind.Production;
            return true;
        }

        if (normalized.Contains("stage", StringComparison.Ordinal))
        {
            environment = KubeActionEnvironmentKind.Staging;
            return true;
        }

        if (normalized.Contains("dev", StringComparison.Ordinal) ||
            normalized.Contains("test", StringComparison.Ordinal) ||
            normalized.Contains("sandbox", StringComparison.Ordinal))
        {
            environment = KubeActionEnvironmentKind.Development;
            return true;
        }

        return false;
    }
}
