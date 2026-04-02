using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public static class KubeActionGuardrailProfileAdjuster
{
    public static KubeActionPreviewResponse Apply(
        KubeActionPreviewResponse preview,
        KubeActionGuardrailProfile profile)
    {
        if (profile is KubeActionGuardrailProfile.Standard)
        {
            return preview;
        }

        var adjustedGuardrails = Adjust(preview, profile);
        return preview with { Guardrails = adjustedGuardrails };
    }

    private static KubeActionGuardrailDecision Adjust(
        KubeActionPreviewResponse preview,
        KubeActionGuardrailProfile profile)
    {
        var current = preview.Guardrails;
        if (current.IsExecutionBlocked)
        {
            return current;
        }

        var blocked = current.IsExecutionBlocked;
        var confirmationLevel = current.ConfirmationLevel;
        var reasons = current.Reasons.ToList();

        switch (profile)
        {
            case KubeActionGuardrailProfile.Conservative:
                confirmationLevel = ElevateConfirmation(confirmationLevel);
                reasons.Add("The active guardrail profile is Conservative, so confirmation is tighter than the default preview.");
                break;

            case KubeActionGuardrailProfile.Strict:
                confirmationLevel = KubeActionConfirmationLevel.TypedConfirmationWithScope;
                blocked |= preview.Confidence is KubeActionPreviewConfidence.Low ||
                           current.RiskLevel is KubeActionRiskLevel.High or KubeActionRiskLevel.Critical or KubeActionRiskLevel.Unsupported;
                reasons.Add("The active guardrail profile is Strict, so execution slows down further or stays blocked on high-risk and low-confidence previews.");
                break;
        }

        return new KubeActionGuardrailDecision(
            RiskLevel: current.RiskLevel,
            ConfirmationLevel: confirmationLevel,
            IsExecutionBlocked: blocked,
            Summary: BuildSummary(blocked, confirmationLevel, profile),
            AcknowledgementHint: confirmationLevel is KubeActionConfirmationLevel.TypedConfirmation or KubeActionConfirmationLevel.TypedConfirmationWithScope
                ? $"Type {GetConfirmationScope(preview)} to confirm later."
                : null,
            Reasons: reasons);
    }

    private static KubeActionConfirmationLevel ElevateConfirmation(KubeActionConfirmationLevel current)
    {
        return current switch
        {
            KubeActionConfirmationLevel.InlineSummary => KubeActionConfirmationLevel.ExplicitReview,
            KubeActionConfirmationLevel.ExplicitReview => KubeActionConfirmationLevel.TypedConfirmation,
            KubeActionConfirmationLevel.TypedConfirmation => KubeActionConfirmationLevel.TypedConfirmationWithScope,
            _ => KubeActionConfirmationLevel.TypedConfirmationWithScope
        };
    }

    private static string BuildSummary(
        bool blocked,
        KubeActionConfirmationLevel confirmationLevel,
        KubeActionGuardrailProfile profile)
    {
        if (blocked)
        {
            return $"Execution is blocked by the active {profile.ToString().ToLowerInvariant()} guardrail profile until a safer or clearer action path is chosen.";
        }

        return confirmationLevel switch
        {
            KubeActionConfirmationLevel.InlineSummary => "A short inline review would be enough before execution.",
            KubeActionConfirmationLevel.ExplicitReview => "Execution should require an explicit review step first.",
            KubeActionConfirmationLevel.TypedConfirmation => "Execution should require typed confirmation of the exact target.",
            _ => "Execution should require typed confirmation plus an explicit scope summary."
        };
    }

    private static string GetConfirmationScope(KubeActionPreviewResponse preview)
    {
        var resourceType = preview.Resource.Kind switch
        {
            KubeResourceKind.Deployment => "deployment",
            KubeResourceKind.StatefulSet => "statefulset",
            KubeResourceKind.DaemonSet => "daemonset",
            KubeResourceKind.Pod => "pod",
            KubeResourceKind.Job => "job",
            KubeResourceKind.CronJob => "cronjob",
            KubeResourceKind.Node => "node",
            _ => "resource"
        };

        return $"{resourceType}/{preview.Resource.Name}";
    }
}
