using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

internal static class KubeActionPolicyEvaluator
{
    public static KubeActionGuardrailDecision Evaluate(KubeActionPolicyContext context)
    {
        var reasons = context.Reasons.ToList();
        var effectiveRisk = context.BaseRiskLevel;
        var blocked = false;

        if (context.Environment is KubeActionEnvironmentKind.Production &&
            effectiveRisk < KubeActionRiskLevel.Medium)
        {
            effectiveRisk = KubeActionRiskLevel.Medium;
            reasons.Add("The target scope is production, so policy raises this above low-risk execution.");
        }
        else if (context.Environment is KubeActionEnvironmentKind.Unknown &&
                 effectiveRisk < KubeActionRiskLevel.Medium)
        {
            effectiveRisk = KubeActionRiskLevel.Medium;
            reasons.Add("The target scope could not be confidently classified, so policy treats it closer to production than development.");
        }

        if (context.MultiResource &&
            effectiveRisk < KubeActionRiskLevel.High)
        {
            effectiveRisk = KubeActionRiskLevel.High;
            reasons.Add("This action touches more than one resource, so policy raises it to a high-risk path.");
        }

        if (IsDangerousBulkScope(context))
        {
            if (effectiveRisk < KubeActionRiskLevel.High)
            {
                effectiveRisk = KubeActionRiskLevel.High;
            }

            reasons.Add($"This preview directly models {context.DirectTargetCount} targets, so policy treats it as a dangerous bulk scope.");

            if (context.AffectedNamespaceCount > 1)
            {
                reasons.Add($"The directly modeled scope spans {context.AffectedNamespaceCount} namespaces.");
            }

            if (context.IncludesSystemNamespaces)
            {
                reasons.Add("System namespaces are included in the directly modeled scope.");
            }
        }

        if ((context.HasSharedDependencies || context.DependencyImpactUnresolved) &&
            effectiveRisk < KubeActionRiskLevel.Medium)
        {
            effectiveRisk = KubeActionRiskLevel.Medium;
            reasons.Add("Dependency impact is shared or only partially resolved from current evidence.");
        }

        if (context.Confidence is KubeActionPreviewConfidence.Low &&
            effectiveRisk < KubeActionRiskLevel.High)
        {
            effectiveRisk = KubeActionRiskLevel.High;
            reasons.Add("Preview confidence is low, so policy requires a higher-friction review path.");
        }

        switch (context.Availability)
        {
            case KubeActionAvailability.PreviewOnly:
                blocked = true;
                reasons.Add("This action is preview-only in the current slice.");
                break;

            case KubeActionAvailability.Unsupported:
                blocked = true;
                effectiveRisk = KubeActionRiskLevel.Unsupported;
                reasons.Add("This action is outside the supported typed-client surface.");
                break;
        }

        if (!context.CanExecuteSafelyFromCurrentEvidence)
        {
            blocked = true;
            reasons.Add("Current cluster evidence is not strong enough to support safe direct execution yet.");
        }

        var confirmationLevel = context.Availability switch
        {
            KubeActionAvailability.PreviewOnly => KubeActionConfirmationLevel.ExplicitReview,
            KubeActionAvailability.Unsupported => KubeActionConfirmationLevel.ExplicitReview,
            _ when blocked => KubeActionConfirmationLevel.TypedConfirmationWithScope,
            _ => effectiveRisk switch
            {
                KubeActionRiskLevel.Informational => KubeActionConfirmationLevel.InlineSummary,
                KubeActionRiskLevel.Low => KubeActionConfirmationLevel.InlineSummary,
                KubeActionRiskLevel.Medium => KubeActionConfirmationLevel.TypedConfirmation,
                KubeActionRiskLevel.High => KubeActionConfirmationLevel.TypedConfirmationWithScope,
                KubeActionRiskLevel.Critical => KubeActionConfirmationLevel.TypedConfirmationWithScope,
                _ => KubeActionConfirmationLevel.TypedConfirmationWithScope
            }
        };

        return new KubeActionGuardrailDecision(
            RiskLevel: effectiveRisk,
            ConfirmationLevel: confirmationLevel,
            IsExecutionBlocked: blocked,
            Summary: BuildSummary(context.Availability, blocked, confirmationLevel),
            AcknowledgementHint: !blocked &&
                                 confirmationLevel is KubeActionConfirmationLevel.TypedConfirmation or KubeActionConfirmationLevel.TypedConfirmationWithScope
                ? $"Type {BuildConfirmationScope(context.Resource)} to confirm later."
                : null,
            Reasons: reasons);
    }

    private static string BuildSummary(
        KubeActionAvailability availability,
        bool blocked,
        KubeActionConfirmationLevel confirmationLevel)
    {
        if (availability is KubeActionAvailability.PreviewOnly)
        {
            return "This action currently stops at preview. Review the impact, but do not expect direct execution yet.";
        }

        if (availability is KubeActionAvailability.Unsupported)
        {
            return "This action is outside the supported typed-client surface right now.";
        }

        if (blocked)
        {
            return "Execution is blocked until the current evidence or policy path becomes safer.";
        }

        return confirmationLevel switch
        {
            KubeActionConfirmationLevel.InlineSummary => "A standard confirm path is enough for this action.",
            KubeActionConfirmationLevel.TypedConfirmation => "Execution requires typed acknowledgement of the exact target.",
            _ => "Execution requires the strongest available typed acknowledgement path in the current UI."
        };
    }

    private static string BuildConfirmationScope(KubeResourceIdentity resource)
    {
        var resourceType = resource.Kind switch
        {
            KubeResourceKind.Deployment => "deployment",
            KubeResourceKind.StatefulSet => "statefulset",
            KubeResourceKind.DaemonSet => "daemonset",
            KubeResourceKind.Pod => "pod",
            KubeResourceKind.Job => "job",
            KubeResourceKind.CronJob => "cronjob",
            KubeResourceKind.Node => "node",
            null => "resource",
            _ => resource.Kind.Value.ToString().ToLowerInvariant()
        };

        return $"{resourceType}/{resource.Name}";
    }

    private static bool IsDangerousBulkScope(KubeActionPolicyContext context)
    {
        return context.DirectTargetCount >= 8 ||
               context.AffectedNamespaceCount > 1 ||
               context.IncludesSystemNamespaces;
    }
}
