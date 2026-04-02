using System.Net;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public static class KubeContextProbeClassifier
{
    public static (KubeContextStatus Status, string? StatusMessage) ClassifyProbeFailure(string contextName, HttpStatusCode? statusCode, string? message)
    {
        var normalizedMessage = string.IsNullOrWhiteSpace(message)
            ? "The agent could not validate this kube context during startup."
            : message.Trim();

        if (statusCode is HttpStatusCode.Unauthorized || LooksLikeExpiredAuthentication(normalizedMessage))
        {
            return (
                KubeContextStatus.AuthenticationExpired,
                $"The cluster rejected the stored credentials for '{contextName}'. Refresh or replace the kubeconfig entry, then retry.");
        }

        if (statusCode is HttpStatusCode.Forbidden)
        {
            return (
                KubeContextStatus.Configured,
                $"The startup probe for '{contextName}' was forbidden by RBAC. The context may still be usable within its allowed scope.");
        }

        if (LooksLikeConnectivityFailure(normalizedMessage))
        {
            return (
                KubeContextStatus.ConfigurationError,
                $"The agent could not reach the cluster for '{contextName}' during startup: {normalizedMessage}");
        }

        return (
            KubeContextStatus.ConfigurationError,
            normalizedMessage);
    }

    private static bool LooksLikeExpiredAuthentication(string message)
    {
        return message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("provide credentials", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("token", StringComparison.OrdinalIgnoreCase) && message.Contains("expired", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("refresh", StringComparison.OrdinalIgnoreCase) && message.Contains("expired", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeConnectivityFailure(string message)
    {
        return message.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("no such host", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("name or service not known", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("actively refused", StringComparison.OrdinalIgnoreCase);
    }
}
