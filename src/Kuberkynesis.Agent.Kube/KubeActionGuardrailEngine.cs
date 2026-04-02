using k8s;
using k8s.Autorest;
using k8s.Models;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubeActionGuardrailEngine
{
    public async Task<KubeActionPreviewResponse> FinalizeAsync(
        Kubernetes client,
        KubeActionPreviewRequest request,
        KubeActionPreviewResponse preview,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(preview);

        var executionAccess = await ProbeExecutionAccessAsync(client, request, cancellationToken);
        return Finalize(preview, request, executionAccess);
    }

    internal KubeActionPreviewResponse Finalize(
        KubeActionPreviewResponse preview,
        KubeActionPreviewRequest request,
        KubeActionExecutionAccess executionAccess)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(executionAccess);

        var adjustedPreview = KubeActionGuardrailProfileAdjuster.Apply(preview, request.GuardrailProfile);
        var permissionBlockers = adjustedPreview.PermissionBlockers
            .Concat(BuildPermissionBlockers(request, executionAccess))
            .Distinct()
            .ToArray();

        return adjustedPreview with
        {
            ExecutionAccess = executionAccess,
            PermissionBlockers = permissionBlockers
        };
    }

    private static async Task<KubeActionExecutionAccess> ProbeExecutionAccessAsync(
        Kubernetes client,
        KubeActionPreviewRequest request,
        CancellationToken cancellationToken)
    {
        var resourceAttributes = BuildExecutionAccessAttributes(request);

        if (resourceAttributes is null)
        {
            return new KubeActionExecutionAccess(
                State: KubeActionExecutionAccessState.Unknown,
                Summary: "Kubernetes RBAC preflight is not available for this action.",
                Detail: null);
        }

        try
        {
            var review = await client.CreateSelfSubjectAccessReviewAsync(
                new V1SelfSubjectAccessReview
                {
                    Spec = new V1SelfSubjectAccessReviewSpec
                    {
                        ResourceAttributes = resourceAttributes
                    }
                },
                cancellationToken: cancellationToken);

            if (review.Status?.Allowed == true)
            {
                return new KubeActionExecutionAccess(
                    State: KubeActionExecutionAccessState.Allowed,
                    Summary: "Kubernetes RBAC currently allows this action for the active identity.",
                    Detail: string.IsNullOrWhiteSpace(review.Status.Reason) ? null : review.Status.Reason);
            }

            if (review.Status?.Denied == true || review.Status?.Allowed == false)
            {
                return new KubeActionExecutionAccess(
                    State: KubeActionExecutionAccessState.Denied,
                    Summary: "Kubernetes RBAC currently denies this action for the active identity.",
                    Detail: FirstNonEmpty(review.Status.Reason, review.Status.EvaluationError));
            }

            return new KubeActionExecutionAccess(
                State: KubeActionExecutionAccessState.Unknown,
                Summary: "Kubernetes RBAC preflight could not confirm whether this action is allowed.",
                Detail: FirstNonEmpty(review.Status?.EvaluationError, review.Status?.Reason));
        }
        catch (HttpOperationException exception)
        {
            return new KubeActionExecutionAccess(
                State: KubeActionExecutionAccessState.Unknown,
                Summary: "Kubernetes RBAC preflight is unavailable for this cluster or identity.",
                Detail: exception.Message);
        }
    }

    private static V1ResourceAttributes? BuildExecutionAccessAttributes(KubeActionPreviewRequest request)
    {
        var trimmedNamespace = request.Namespace?.Trim();
        var trimmedName = request.Name.Trim();

        return request.Action switch
        {
            KubeActionKind.ScaleDeployment when request.Kind is KubeResourceKind.Deployment => new V1ResourceAttributes
            {
                Group = "apps",
                Resource = "deployments",
                Subresource = "scale",
                Verb = "update",
                NamespaceProperty = trimmedNamespace,
                Name = trimmedName
            },
            KubeActionKind.RestartDeploymentRollout when request.Kind is KubeResourceKind.Deployment => new V1ResourceAttributes
            {
                Group = "apps",
                Resource = "deployments",
                Verb = "patch",
                NamespaceProperty = trimmedNamespace,
                Name = trimmedName
            },
            KubeActionKind.RollbackDeploymentRollout when request.Kind is KubeResourceKind.Deployment => new V1ResourceAttributes
            {
                Group = "apps",
                Resource = "deployments",
                Verb = "patch",
                NamespaceProperty = trimmedNamespace,
                Name = trimmedName
            },
            KubeActionKind.DeletePod when request.Kind is KubeResourceKind.Pod => new V1ResourceAttributes
            {
                Group = string.Empty,
                Resource = "pods",
                Verb = "delete",
                NamespaceProperty = trimmedNamespace,
                Name = trimmedName
            },
            KubeActionKind.ScaleStatefulSet when request.Kind is KubeResourceKind.StatefulSet => new V1ResourceAttributes
            {
                Group = "apps",
                Resource = "statefulsets",
                Subresource = "scale",
                Verb = "update",
                NamespaceProperty = trimmedNamespace,
                Name = trimmedName
            },
            KubeActionKind.RestartDaemonSetRollout when request.Kind is KubeResourceKind.DaemonSet => new V1ResourceAttributes
            {
                Group = "apps",
                Resource = "daemonsets",
                Verb = "patch",
                NamespaceProperty = trimmedNamespace,
                Name = trimmedName
            },
            KubeActionKind.DeleteJob when request.Kind is KubeResourceKind.Job => new V1ResourceAttributes
            {
                Group = "batch",
                Resource = "jobs",
                Verb = "delete",
                NamespaceProperty = trimmedNamespace,
                Name = trimmedName
            },
            KubeActionKind.SuspendCronJob or KubeActionKind.ResumeCronJob when request.Kind is KubeResourceKind.CronJob => new V1ResourceAttributes
            {
                Group = "batch",
                Resource = "cronjobs",
                Verb = "patch",
                NamespaceProperty = trimmedNamespace,
                Name = trimmedName
            },
            KubeActionKind.CordonNode or KubeActionKind.UncordonNode when request.Kind is KubeResourceKind.Node => new V1ResourceAttributes
            {
                Group = string.Empty,
                Resource = "nodes",
                Verb = "patch",
                Name = trimmedName
            },
            _ => null
        };
    }

    private static IReadOnlyList<KubeActionPermissionBlocker> BuildPermissionBlockers(
        KubeActionPreviewRequest request,
        KubeActionExecutionAccess executionAccess)
    {
        if (executionAccess.State is not KubeActionExecutionAccessState.Denied)
        {
            return [];
        }

        return
        [
            new KubeActionPermissionBlocker(
                Scope: BuildPermissionBlockerScope(request),
                Summary: "Kubernetes RBAC denied this action for the requested target.",
                Detail: FirstNonEmpty(executionAccess.Detail, executionAccess.Summary))
        ];
    }

    private static string BuildPermissionBlockerScope(KubeActionPreviewRequest request)
    {
        var resourceType = request.Kind.ToString();
        var namespaceName = string.IsNullOrWhiteSpace(request.Namespace)
            ? null
            : request.Namespace.Trim();

        return string.IsNullOrWhiteSpace(namespaceName)
            ? $"{resourceType}/{request.Name.Trim()} in {request.ContextName.Trim()}"
            : $"{resourceType}/{request.Name.Trim()} in namespace {namespaceName} on {request.ContextName.Trim()}";
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
