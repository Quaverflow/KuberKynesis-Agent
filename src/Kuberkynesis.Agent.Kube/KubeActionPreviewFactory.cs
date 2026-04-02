using k8s.Models;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

internal static class KubeActionPreviewFactory
{
    public static KubeActionPreviewResponse CreateScaleDeploymentPreview(
        string contextName,
        V1Deployment deployment,
        IReadOnlyList<V1Pod> matchingPods,
        int targetReplicas,
        KubeActionLocalEnvironmentRules? localEnvironmentRules = null)
    {
        ArgumentNullException.ThrowIfNull(deployment);

        var resourceName = deployment.Metadata?.Name ?? string.Empty;
        var namespaceName = deployment.Metadata?.NamespaceProperty;
        var currentReplicas = deployment.Spec?.Replicas ?? 1;
        var readyReplicas = deployment.Status?.ReadyReplicas ?? 0;
        var availableReplicas = deployment.Status?.AvailableReplicas ?? 0;
        var updatedReplicas = deployment.Status?.UpdatedReplicas ?? 0;
        var delta = targetReplicas - currentReplicas;
        var selectorText = BuildSelectorSummary(deployment.Spec?.Selector?.MatchLabels);
        var summary = BuildSummary(resourceName, currentReplicas, targetReplicas, delta);
        var confidence = DetermineConfidence(deployment, currentReplicas, targetReplicas, readyReplicas);
        var environment = KubeActionEnvironmentClassifier.Classify(deployment.Metadata, contextName, localEnvironmentRules);
        var guardrails = BuildScaleGuardrails(deployment, environment, currentReplicas, targetReplicas, readyReplicas, confidence, namespaceName);
        var coverageSummary = BuildCoverageSummary(confidence);

        var facts = new List<KubeActionPreviewFact>
        {
            new("Current replicas", currentReplicas.ToString()),
            new("Target replicas", targetReplicas.ToString()),
            new("Change", delta > 0 ? $"+{delta}" : delta.ToString()),
            new("Ready now", $"{readyReplicas}/{currentReplicas}"),
            new("Available now", availableReplicas.ToString()),
            new("Updated now", updatedReplicas.ToString()),
            new("Matching pods", matchingPods.Count.ToString())
        };

        if (!string.IsNullOrWhiteSpace(selectorText))
        {
            facts.Add(new KubeActionPreviewFact("Selector", selectorText));
        }

        AppendDeploymentStrategyFacts(facts, deployment);

        var warnings = BuildWarnings(deployment, currentReplicas, targetReplicas, readyReplicas, matchingPods.Count).ToList();
        var notes = BuildNotes(currentReplicas, targetReplicas, matchingPods.Count);
        var saferAlternatives = BuildSaferAlternatives(currentReplicas, targetReplicas, readyReplicas);
        var affectedResources = matchingPods
            .Where(static pod => !string.IsNullOrWhiteSpace(pod.Metadata?.Name))
            .OrderBy(static pod => pod.Metadata!.Name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(static pod => new KubeRelatedResource(
                Relationship: "Current pod",
                Kind: KubeResourceKind.Pod,
                ApiVersion: pod.ApiVersion ?? "v1",
                Name: pod.Metadata!.Name!,
                Namespace: pod.Metadata?.NamespaceProperty,
                Status: pod.Status?.Phase,
                Summary: pod.Status?.PodIP))
            .ToArray();

        AppendBoundaryFacts(facts, contextName, namespaceName, affectedResources);
        warnings.AddRange(BuildBoundaryWarnings(namespaceName, affectedResources));

        return new KubeActionPreviewResponse(
            Action: KubeActionKind.ScaleDeployment,
            Resource: new KubeResourceIdentity(
                ContextName: contextName,
                Kind: KubeResourceKind.Deployment,
                Namespace: namespaceName,
                Name: resourceName),
            Summary: summary,
            Confidence: confidence,
            Guardrails: guardrails,
            CoverageSummary: coverageSummary,
            Facts: facts,
            Warnings: warnings,
            Notes: notes,
            SaferAlternatives: saferAlternatives,
            AffectedResources: affectedResources,
            TransparencyCommands: KubectlTransparencyFactory.CreateForActionPreview(
                new KubeActionPreviewRequest(
                    ContextName: contextName,
                    Kind: KubeResourceKind.Deployment,
                    Namespace: namespaceName,
                    Name: resourceName,
                    Action: KubeActionKind.ScaleDeployment,
                    TargetReplicas: targetReplicas)))
        {
            Availability = KubeActionAvailability.PreviewAndExecute,
            Environment = environment,
            CoverageLimits = BuildScaleCoverageLimits(deployment, currentReplicas, targetReplicas, readyReplicas)
        };
    }

    public static KubeActionPreviewResponse CreateRestartDeploymentPreview(
        string contextName,
        V1Deployment deployment,
        IReadOnlyList<V1Pod> matchingPods,
        KubePodDisruptionBudgetImpact disruptionBudgetImpact,
        KubeActionLocalEnvironmentRules? localEnvironmentRules = null)
    {
        ArgumentNullException.ThrowIfNull(deployment);

        var resourceName = deployment.Metadata?.Name ?? string.Empty;
        var namespaceName = deployment.Metadata?.NamespaceProperty;
        var currentReplicas = deployment.Spec?.Replicas ?? 1;
        var readyReplicas = deployment.Status?.ReadyReplicas ?? 0;
        var availableReplicas = deployment.Status?.AvailableReplicas ?? 0;
        var updatedReplicas = deployment.Status?.UpdatedReplicas ?? 0;
        var selectorText = BuildSelectorSummary(deployment.Spec?.Selector?.MatchLabels);
        var confidence = DetermineRestartConfidence(deployment, currentReplicas, readyReplicas);
        var environment = KubeActionEnvironmentClassifier.Classify(deployment.Metadata, contextName, localEnvironmentRules);
        var guardrails = BuildRestartGuardrails(deployment, environment, currentReplicas, readyReplicas, confidence, disruptionBudgetImpact, namespaceName);

        var facts = new List<KubeActionPreviewFact>
        {
            new("Desired replicas", currentReplicas.ToString()),
            new("Ready now", $"{readyReplicas}/{currentReplicas}"),
            new("Available now", availableReplicas.ToString()),
            new("Updated now", updatedReplicas.ToString()),
            new("Matching pods", matchingPods.Count.ToString())
        };

        if (!string.IsNullOrWhiteSpace(selectorText))
        {
            facts.Add(new KubeActionPreviewFact("Selector", selectorText));
        }

        AppendDeploymentStrategyFacts(facts, deployment);

        var warnings = new List<string>();

        if (deployment.Spec?.Paused == true)
        {
            warnings.Add("This deployment is paused. A rollout restart can queue a template change, but visible pod replacement may be delayed until the rollout resumes.");
        }

        if (readyReplicas < currentReplicas)
        {
            warnings.Add($"Only {readyReplicas}/{currentReplicas} replicas are ready right now. Restarting during an unhealthy rollout can compound instability.");
        }

        warnings.AddRange(BuildDeploymentStrategyWarnings(deployment));
        warnings.AddRange(BuildRestartDisruptionBudgetWarnings(disruptionBudgetImpact));

        var notes = new List<string>
        {
            "A rollout restart updates the deployment pod template so Kubernetes creates a fresh ReplicaSet and rotates pods over time.",
            "This preview cannot predict the exact pod replacement order, timing, or whether other controllers will interfere."
        };

        if (disruptionBudgetImpact.HasMatchedBudgets)
        {
            notes.Add("Matched PodDisruptionBudget objects are included below because they can constrain how quickly voluntary disruption proceeds during rollout.");
        }

        var saferAlternatives = new List<KubeActionPreviewAlternative>
        {
            new(
                Label: "Inspect current rollout health first",
                Reason: "Use signals, timeline, and logs to confirm whether a restart is safer than the current deployment state."),
            new(
                Label: "Preview a scale change instead",
                Reason: "If you are only trying to change capacity, a scale preview gives a more targeted read on that effect.")
        };

        var affectedResources = matchingPods
            .Where(static pod => !string.IsNullOrWhiteSpace(pod.Metadata?.Name))
            .OrderBy(static pod => pod.Metadata!.Name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(static pod => new KubeRelatedResource(
                Relationship: "Current pod",
                Kind: KubeResourceKind.Pod,
                ApiVersion: pod.ApiVersion ?? "v1",
                Name: pod.Metadata!.Name!,
                Namespace: pod.Metadata?.NamespaceProperty,
                Status: pod.Status?.Phase,
                Summary: pod.Status?.PodIP))
            .Concat(disruptionBudgetImpact.RelatedResources)
            .ToArray();

        AppendBoundaryFacts(facts, contextName, namespaceName, affectedResources);
        warnings.AddRange(BuildBoundaryWarnings(namespaceName, affectedResources));

        return new KubeActionPreviewResponse(
            Action: KubeActionKind.RestartDeploymentRollout,
            Resource: new KubeResourceIdentity(
                ContextName: contextName,
                Kind: KubeResourceKind.Deployment,
                Namespace: namespaceName,
                Name: resourceName),
            Summary: $"Deployment/{resourceName} would trigger a rollout restart across {currentReplicas} desired replica(s).",
            Confidence: confidence,
            Guardrails: guardrails,
            CoverageSummary: BuildCoverageSummary(confidence),
            Facts: facts,
            Warnings: warnings,
            Notes: notes,
            SaferAlternatives: saferAlternatives,
            AffectedResources: affectedResources,
            TransparencyCommands: KubectlTransparencyFactory.CreateForActionPreview(
                new KubeActionPreviewRequest(
                    ContextName: contextName,
                    Kind: KubeResourceKind.Deployment,
                    Namespace: namespaceName,
                    Name: resourceName,
                    Action: KubeActionKind.RestartDeploymentRollout)))
        {
            Availability = KubeActionAvailability.PreviewAndExecute,
            Environment = environment,
            CoverageLimits = BuildRestartCoverageLimits(deployment, readyReplicas, currentReplicas, disruptionBudgetImpact)
        };
    }

    public static KubeActionPreviewResponse CreateRollbackDeploymentPreview(
        string contextName,
        V1Deployment deployment,
        IReadOnlyList<V1Pod> matchingPods,
        KubeDeploymentRollbackResolution rollbackResolution,
        KubePodDisruptionBudgetImpact disruptionBudgetImpact,
        bool rollbackHistoryCoverageRestricted,
        KubeActionLocalEnvironmentRules? localEnvironmentRules = null)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(rollbackResolution);

        var resourceName = deployment.Metadata?.Name ?? string.Empty;
        var namespaceName = deployment.Metadata?.NamespaceProperty;
        var currentReplicas = deployment.Spec?.Replicas ?? 1;
        var readyReplicas = deployment.Status?.ReadyReplicas ?? 0;
        var availableReplicas = deployment.Status?.AvailableReplicas ?? 0;
        var updatedReplicas = deployment.Status?.UpdatedReplicas ?? 0;
        var selectorText = BuildSelectorSummary(deployment.Spec?.Selector?.MatchLabels);
        var confidence = DetermineRollbackConfidence(
            deployment,
            currentReplicas,
            readyReplicas,
            rollbackResolution,
            rollbackHistoryCoverageRestricted);
        var environment = KubeActionEnvironmentClassifier.Classify(deployment.Metadata, contextName, localEnvironmentRules);
        var guardrails = BuildRollbackGuardrails(
            deployment,
            environment,
            currentReplicas,
            readyReplicas,
            confidence,
            rollbackResolution,
            disruptionBudgetImpact,
            namespaceName,
            rollbackHistoryCoverageRestricted);

        var facts = new List<KubeActionPreviewFact>
        {
            new("Desired replicas", currentReplicas.ToString()),
            new("Ready now", $"{readyReplicas}/{currentReplicas}"),
            new("Available now", availableReplicas.ToString()),
            new("Updated now", updatedReplicas.ToString()),
            new("Matching pods", matchingPods.Count.ToString()),
            new("Current revision", rollbackResolution.CurrentRevision?.ToString() ?? "Unknown"),
            new(
                "Rollback target revision",
                rollbackResolution.PreviousRevision?.ToString() ??
                (rollbackHistoryCoverageRestricted ? "RBAC-limited" : "None retained")),
            new("Retained revisions", rollbackResolution.RetainedRevisionCount.ToString()),
            new("Current images", KubeDeploymentRollbackPlanner.GetTemplateImageSummary(deployment.Spec?.Template))
        };

        if (!string.IsNullOrWhiteSpace(selectorText))
        {
            facts.Add(new KubeActionPreviewFact("Selector", selectorText));
        }

        if (rollbackResolution.PreviousReplicaSet is { Metadata.Name: { Length: > 0 } previousReplicaSetName })
        {
            facts.Add(new KubeActionPreviewFact("Rollback target", $"ReplicaSet/{previousReplicaSetName}"));
            facts.Add(new KubeActionPreviewFact(
                "Rollback target images",
                KubeDeploymentRollbackPlanner.GetTemplateImageSummary(rollbackResolution.PreviousReplicaSet.Spec?.Template)));
        }

        if (!string.IsNullOrWhiteSpace(rollbackResolution.PreviousChangeCause))
        {
            facts.Add(new KubeActionPreviewFact("Target change cause", rollbackResolution.PreviousChangeCause));
        }

        AppendDeploymentStrategyFacts(facts, deployment);

        var warnings = new List<string>();

        if (rollbackHistoryCoverageRestricted)
        {
            warnings.Add("Retained rollout history could not be fully inspected under current RBAC, so direct rollback stays blocked from current evidence.");
        }
        else if (!rollbackResolution.CanRollback)
        {
            warnings.Add("No retained prior revision is currently available for direct rollout undo.");
        }

        if (rollbackResolution.UsedReplicaSetRevisionFallback)
        {
            warnings.Add("The current deployment revision was inferred from retained ReplicaSet history because deployment annotations were incomplete.");
        }

        if (deployment.Spec?.Paused == true)
        {
            warnings.Add("This deployment is paused. Rollout undo can patch the template, but visible pod replacement may still wait until the rollout resumes.");
        }

        if (readyReplicas < currentReplicas)
        {
            warnings.Add($"Only {readyReplicas}/{currentReplicas} replicas are ready right now. Rolling back while the deployment is already unhealthy can compound instability.");
        }

        warnings.AddRange(BuildRollbackDeploymentStrategyWarnings(deployment));
        warnings.AddRange(BuildRestartDisruptionBudgetWarnings(disruptionBudgetImpact));

        var notes = new List<string>
        {
            "Rollout undo restores the deployment pod template to a retained ReplicaSet revision instead of inventing a new one.",
            "This preview cannot guarantee the exact pod replacement order, timing, or whether other controllers will interfere while the rollback is in flight."
        };

        if (disruptionBudgetImpact.HasMatchedBudgets)
        {
            notes.Add("Matched PodDisruptionBudget objects are included below because they can still constrain voluntary disruption while the retained revision scales back up.");
        }

        if (!string.IsNullOrWhiteSpace(rollbackResolution.PreviousChangeCause))
        {
            notes.Add("The retained rollback target still carries an earlier recorded change cause, which is surfaced below as historical context rather than a guarantee of present intent.");
        }

        var saferAlternatives = new List<KubeActionPreviewAlternative>
        {
            new(
                Label: "Inspect current rollout health first",
                Reason: "Use signals, timeline, and logs to confirm whether rollback is safer than the current deployment state."),
            new(
                Label: "Preview rollout restart instead",
                Reason: "If you only need fresh pods and do not intend to restore an older template, restart is a narrower action."),
            new(
                Label: "Preview a scale change instead",
                Reason: "If you are only trying to change capacity, a scale preview gives a more targeted read on that effect.")
        };

        var affectedResources = new List<KubeRelatedResource>();

        if (rollbackResolution.PreviousReplicaSet is { Metadata.Name: { Length: > 0 } previousReplicaSetResourceName })
        {
            affectedResources.Add(new KubeRelatedResource(
                Relationship: "Rollback target",
                Kind: KubeResourceKind.ReplicaSet,
                ApiVersion: rollbackResolution.PreviousReplicaSet.ApiVersion ?? "apps/v1",
                Name: previousReplicaSetResourceName,
                Namespace: rollbackResolution.PreviousReplicaSet.Metadata?.NamespaceProperty ?? namespaceName,
                Status: rollbackResolution.PreviousRevision is null ? null : $"revision {rollbackResolution.PreviousRevision}",
                Summary: KubeDeploymentRollbackPlanner.GetTemplateImageSummary(rollbackResolution.PreviousReplicaSet.Spec?.Template)));
        }

        affectedResources.AddRange(matchingPods
            .Where(static pod => !string.IsNullOrWhiteSpace(pod.Metadata?.Name))
            .OrderBy(static pod => pod.Metadata!.Name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(static pod => new KubeRelatedResource(
                Relationship: "Current pod",
                Kind: KubeResourceKind.Pod,
                ApiVersion: pod.ApiVersion ?? "v1",
                Name: pod.Metadata!.Name!,
                Namespace: pod.Metadata?.NamespaceProperty,
                Status: pod.Status?.Phase,
                Summary: pod.Status?.PodIP)));
        affectedResources.AddRange(disruptionBudgetImpact.RelatedResources);

        AppendBoundaryFacts(facts, contextName, namespaceName, affectedResources);
        warnings.AddRange(BuildBoundaryWarnings(namespaceName, affectedResources));

        return new KubeActionPreviewResponse(
            Action: KubeActionKind.RollbackDeploymentRollout,
            Resource: new KubeResourceIdentity(
                ContextName: contextName,
                Kind: KubeResourceKind.Deployment,
                Namespace: namespaceName,
                Name: resourceName),
            Summary: BuildRollbackSummary(resourceName, currentReplicas, rollbackResolution, rollbackHistoryCoverageRestricted),
            Confidence: confidence,
            Guardrails: guardrails,
            CoverageSummary: BuildCoverageSummary(confidence),
            Facts: facts,
            Warnings: warnings,
            Notes: notes,
            SaferAlternatives: saferAlternatives,
            AffectedResources: affectedResources.ToArray(),
            TransparencyCommands: KubectlTransparencyFactory.CreateForActionPreview(
                new KubeActionPreviewRequest(
                    ContextName: contextName,
                    Kind: KubeResourceKind.Deployment,
                    Namespace: namespaceName,
                    Name: resourceName,
                    Action: KubeActionKind.RollbackDeploymentRollout)))
        {
            Availability = KubeActionAvailability.PreviewAndExecute,
            Environment = environment,
            CoverageLimits = BuildRollbackCoverageLimits(
                deployment,
                readyReplicas,
                currentReplicas,
                rollbackResolution,
                disruptionBudgetImpact,
                rollbackHistoryCoverageRestricted)
        };
    }

    public static KubeActionPreviewResponse CreateDeletePodPreview(
        string contextName,
        V1Pod pod,
        KubeRelatedResource? immediateOwner,
        KubeRelatedResource? rolloutOwner,
        bool replacementLikely,
        KubePodDisruptionBudgetImpact disruptionBudgetImpact,
        KubeActionLocalEnvironmentRules? localEnvironmentRules = null)
    {
        ArgumentNullException.ThrowIfNull(pod);

        var resourceName = pod.Metadata?.Name ?? string.Empty;
        var namespaceName = pod.Metadata?.NamespaceProperty;
        var phase = pod.Status?.Phase ?? "Unknown";
        var restartCount = pod.Status?.ContainerStatuses?.Sum(static status => status.RestartCount) ?? 0;
        var confidence = DetermineDeletePodConfidence(immediateOwner, rolloutOwner, replacementLikely);
        var environment = KubeActionEnvironmentClassifier.Classify(pod.Metadata, contextName, localEnvironmentRules);
        var guardrails = BuildDeletePodGuardrails(pod, environment, replacementLikely, immediateOwner, rolloutOwner, confidence, disruptionBudgetImpact);
        var coverageSummary = BuildCoverageSummary(confidence);
        var ownerSummary = rolloutOwner ?? immediateOwner;

        var facts = new List<KubeActionPreviewFact>
        {
            new("Phase", phase),
            new("Node", pod.Spec?.NodeName ?? "Unknown"),
            new("Pod IP", pod.Status?.PodIP ?? "Unknown"),
            new("Restarts", restartCount.ToString()),
            new("Replacement likely", replacementLikely ? "Yes" : "Unknown / unlikely")
        };

        if (ownerSummary is not null)
        {
            facts.Add(new KubeActionPreviewFact("Controlled by", $"{ownerSummary.Kind}/{ownerSummary.Name}"));
        }

        var warnings = new List<string>();

        if (!replacementLikely)
        {
            warnings.Add("No strong controller replacement path was confirmed. Deleting this pod may reduce live capacity until another actor recreates it.");
        }

        if (string.Equals(phase, "Pending", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("This pod is not fully running right now. Deleting it may hide an underlying scheduling or image-pull problem rather than resolve it.");
        }

        warnings.AddRange(BuildDeletePodDisruptionBudgetWarnings(disruptionBudgetImpact));

        var notes = new List<string>
        {
            "Deleting one pod acts on a single live instance, not the whole workload definition.",
            replacementLikely
                ? "A matching controller still appears to want this replica, so Kubernetes is likely to recreate a replacement pod."
                : "This preview cannot prove that another controller will recreate the pod after deletion."
        };

        if (disruptionBudgetImpact.HasMatchedBudgets)
        {
            notes.Add("Matched PodDisruptionBudget objects are shown as disruption context. Direct pod deletion can still bypass eviction-style budget enforcement.");
        }

        var saferAlternatives = new List<KubeActionPreviewAlternative>();

        if (rolloutOwner is { Kind: KubeResourceKind.Deployment })
        {
            saferAlternatives.Add(new KubeActionPreviewAlternative(
                Label: $"Preview rollout restart for {rolloutOwner.Name}",
                Reason: "Rollout restart is usually a clearer controller-level action than manually deleting one managed pod."));
        }

        saferAlternatives.Add(new KubeActionPreviewAlternative(
            Label: "Inspect logs and timeline first",
            Reason: "Use the existing signals, timeline, and logs to confirm whether deletion is actually the safest next step."));

        var affectedResources = new List<KubeRelatedResource>();

        if (immediateOwner is not null)
        {
            affectedResources.Add(immediateOwner with { Relationship = "Immediate owner" });
        }

        if (rolloutOwner is not null &&
            (immediateOwner is null ||
             rolloutOwner.Kind != immediateOwner.Kind ||
             !string.Equals(rolloutOwner.Name, immediateOwner.Name, StringComparison.Ordinal)))
        {
            affectedResources.Add(rolloutOwner with { Relationship = "Rollout owner" });
        }

        affectedResources.AddRange(disruptionBudgetImpact.RelatedResources);
        AppendBoundaryFacts(facts, contextName, namespaceName, affectedResources);
        warnings.AddRange(BuildBoundaryWarnings(namespaceName, affectedResources));

        return new KubeActionPreviewResponse(
            Action: KubeActionKind.DeletePod,
            Resource: new KubeResourceIdentity(
                ContextName: contextName,
                Kind: KubeResourceKind.Pod,
                Namespace: namespaceName,
                Name: resourceName),
            Summary: replacementLikely
                ? $"Pod/{resourceName} would be deleted, and its controller would likely create a replacement."
                : $"Pod/{resourceName} would be deleted, but replacement is not strongly guaranteed from current evidence.",
            Confidence: confidence,
            Guardrails: guardrails,
            CoverageSummary: coverageSummary,
            Facts: facts,
            Warnings: warnings,
            Notes: notes,
            SaferAlternatives: saferAlternatives,
            AffectedResources: affectedResources,
            TransparencyCommands: KubectlTransparencyFactory.CreateForActionPreview(
                new KubeActionPreviewRequest(
                    ContextName: contextName,
                    Kind: KubeResourceKind.Pod,
                    Namespace: namespaceName,
                    Name: resourceName,
                    Action: KubeActionKind.DeletePod)))
        {
            Availability = KubeActionAvailability.PreviewAndExecute,
            Environment = environment,
            CoverageLimits = BuildDeletePodCoverageLimits(immediateOwner, rolloutOwner, replacementLikely, disruptionBudgetImpact)
        };
    }

    public static KubeActionPreviewResponse CreateScaleStatefulSetPreview(
        string contextName,
        V1StatefulSet statefulSet,
        IReadOnlyList<V1Pod> matchingPods,
        int targetReplicas,
        KubeActionLocalEnvironmentRules? localEnvironmentRules = null)
    {
        ArgumentNullException.ThrowIfNull(statefulSet);

        var resourceName = statefulSet.Metadata?.Name ?? string.Empty;
        var namespaceName = statefulSet.Metadata?.NamespaceProperty;
        var currentReplicas = statefulSet.Spec?.Replicas ?? 1;
        var readyReplicas = statefulSet.Status?.ReadyReplicas ?? 0;
        var availableReplicas = statefulSet.Status?.AvailableReplicas ?? 0;
        var updatedReplicas = statefulSet.Status?.UpdatedReplicas ?? 0;
        var delta = targetReplicas - currentReplicas;
        var selectorText = BuildSelectorSummary(statefulSet.Spec?.Selector?.MatchLabels);
        var confidence = readyReplicas < currentReplicas || targetReplicas == 0
            ? KubeActionPreviewConfidence.Low
            : targetReplicas == currentReplicas
                ? KubeActionPreviewConfidence.High
                : KubeActionPreviewConfidence.Medium;
        var environment = KubeActionEnvironmentClassifier.Classify(statefulSet.Metadata, contextName, localEnvironmentRules);

        var facts = new List<KubeActionPreviewFact>
        {
            new("Current replicas", currentReplicas.ToString()),
            new("Target replicas", targetReplicas.ToString()),
            new("Change", delta > 0 ? $"+{delta}" : delta.ToString()),
            new("Ready now", $"{readyReplicas}/{currentReplicas}"),
            new("Available now", availableReplicas.ToString()),
            new("Updated now", updatedReplicas.ToString()),
            new("Matching pods", matchingPods.Count.ToString())
        };

        if (!string.IsNullOrWhiteSpace(selectorText))
        {
            facts.Add(new KubeActionPreviewFact("Selector", selectorText));
        }

        var warnings = new List<string>();

        if (targetReplicas == 0)
        {
            warnings.Add("Scaling a StatefulSet to zero removes every current ordinal pod and can interrupt stable identity consumers.");
        }
        else if (targetReplicas < currentReplicas)
        {
            warnings.Add("Scaling down a StatefulSet removes the highest ordinals first, but attached storage and peer identity expectations can still make the impact non-trivial.");
        }

        if (readyReplicas < currentReplicas)
        {
            warnings.Add($"Only {readyReplicas}/{currentReplicas} replicas are ready right now.");
        }

        var notes = new List<string>
        {
            "StatefulSet scale keeps stable identities and storage bindings, so this preview focuses on replica and ordinal impact rather than arbitrary pod turnover.",
            "Persistent volume lifecycle, quorum expectations, and downstream clients can still change the real outcome after the scale request."
        };

        var guardrails = BuildStatefulSetScaleGuardrails(
            statefulSet,
            environment,
            currentReplicas,
            targetReplicas,
            readyReplicas,
            confidence,
            namespaceName);

        var affectedResources = matchingPods
            .Where(static pod => !string.IsNullOrWhiteSpace(pod.Metadata?.Name))
            .OrderBy(static pod => pod.Metadata!.Name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(static pod => new KubeRelatedResource(
                Relationship: "Current pod",
                Kind: KubeResourceKind.Pod,
                ApiVersion: pod.ApiVersion ?? "v1",
                Name: pod.Metadata!.Name!,
                Namespace: pod.Metadata?.NamespaceProperty,
                Status: pod.Status?.Phase,
                Summary: pod.Status?.PodIP))
            .ToArray();

        AppendBoundaryFacts(facts, contextName, namespaceName, affectedResources);
        warnings.AddRange(BuildBoundaryWarnings(namespaceName, affectedResources));

        return new KubeActionPreviewResponse(
            Action: KubeActionKind.ScaleStatefulSet,
            Resource: new KubeResourceIdentity(contextName, KubeResourceKind.StatefulSet, namespaceName, resourceName),
            Summary: targetReplicas == currentReplicas
                ? $"StatefulSet/{resourceName} is already set to {currentReplicas} replicas, so scale would not change desired count."
                : $"StatefulSet/{resourceName} would scale from {currentReplicas} to {targetReplicas} replicas.",
            Confidence: confidence,
            Guardrails: guardrails,
            CoverageSummary: BuildCoverageSummary(confidence),
            Facts: facts,
            Warnings: warnings,
            Notes: notes,
            SaferAlternatives: BuildSaferAlternatives(currentReplicas, targetReplicas, readyReplicas),
            AffectedResources: affectedResources,
            TransparencyCommands: KubectlTransparencyFactory.CreateForActionPreview(
                new KubeActionPreviewRequest(
                    ContextName: contextName,
                    Kind: KubeResourceKind.StatefulSet,
                    Namespace: namespaceName,
                    Name: resourceName,
                    Action: KubeActionKind.ScaleStatefulSet,
                    TargetReplicas: targetReplicas)))
        {
            Availability = KubeActionAvailability.PreviewAndExecute,
            Environment = environment,
            CoverageLimits =
            [
                "This preview cannot fully model quorum sensitivity, persistent volume reclaim behavior, or workload-specific identity assumptions."
            ]
        };
    }

    public static KubeActionPreviewResponse CreateRestartDaemonSetPreview(
        string contextName,
        V1DaemonSet daemonSet,
        IReadOnlyList<V1Pod> matchingPods,
        KubePodDisruptionBudgetImpact disruptionBudgetImpact,
        KubeActionLocalEnvironmentRules? localEnvironmentRules = null)
    {
        ArgumentNullException.ThrowIfNull(daemonSet);

        var resourceName = daemonSet.Metadata?.Name ?? string.Empty;
        var namespaceName = daemonSet.Metadata?.NamespaceProperty;
        var desired = daemonSet.Status?.DesiredNumberScheduled ?? matchingPods.Count;
        var ready = daemonSet.Status?.NumberReady ?? 0;
        var available = daemonSet.Status?.NumberAvailable ?? 0;
        var unavailable = daemonSet.Status?.NumberUnavailable ?? 0;
        var confidence = ready < desired
            ? KubeActionPreviewConfidence.Low
            : KubeActionPreviewConfidence.Medium;
        var environment = KubeActionEnvironmentClassifier.Classify(daemonSet.Metadata, contextName, localEnvironmentRules);

        var facts = new List<KubeActionPreviewFact>
        {
            new("Desired scheduled", desired.ToString()),
            new("Ready now", ready.ToString()),
            new("Available now", available.ToString()),
            new("Unavailable now", unavailable.ToString()),
            new("Matching pods", matchingPods.Count.ToString())
        };

        AppendDaemonSetStrategyFacts(facts, daemonSet);

        var warnings = new List<string>();
        if (ready < desired)
        {
            warnings.Add("The DaemonSet is already degraded on at least one node.");
        }

        warnings.AddRange(BuildDaemonSetStrategyWarnings(daemonSet));
        warnings.AddRange(BuildDaemonSetDisruptionBudgetWarnings(disruptionBudgetImpact));

        var notes = new List<string>
        {
            "A DaemonSet rollout restart rotates pods across eligible nodes over time.",
            "This preview cannot guarantee restart ordering or account for node-local disruptions during rollout."
        };

        if (disruptionBudgetImpact.HasMatchedBudgets)
        {
            notes.Add("Matched PodDisruptionBudget objects are included because they can constrain disruption headroom for restarted pods.");
        }

        var affectedResources = matchingPods
            .Where(static pod => !string.IsNullOrWhiteSpace(pod.Metadata?.Name))
            .OrderBy(static pod => pod.Metadata!.Name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(static pod => new KubeRelatedResource(
                Relationship: "Current pod",
                Kind: KubeResourceKind.Pod,
                ApiVersion: pod.ApiVersion ?? "v1",
                Name: pod.Metadata!.Name!,
                Namespace: pod.Metadata?.NamespaceProperty,
                Status: pod.Status?.Phase,
                Summary: pod.Spec?.NodeName))
            .Concat(disruptionBudgetImpact.RelatedResources)
            .ToArray();

        AppendBoundaryFacts(facts, contextName, namespaceName, affectedResources);
        warnings.AddRange(BuildBoundaryWarnings(namespaceName, affectedResources));

        return new KubeActionPreviewResponse(
            Action: KubeActionKind.RestartDaemonSetRollout,
            Resource: new KubeResourceIdentity(contextName, KubeResourceKind.DaemonSet, namespaceName, resourceName),
            Summary: $"DaemonSet/{resourceName} would request a rollout restart across {desired} scheduled pod(s).",
            Confidence: confidence,
            Guardrails: BuildDaemonSetRestartGuardrails(daemonSet, environment, ready, desired, confidence, disruptionBudgetImpact, namespaceName),
            CoverageSummary: BuildCoverageSummary(confidence),
            Facts: facts,
            Warnings: warnings,
            Notes: notes,
            SaferAlternatives:
            [
                new KubeActionPreviewAlternative("Inspect node-local failures first", "Use logs, signals, and pod status to confirm whether a restart is better than stabilizing the current DaemonSet state.")
            ],
            AffectedResources: affectedResources,
            TransparencyCommands: KubectlTransparencyFactory.CreateForActionPreview(
                new KubeActionPreviewRequest(
                    ContextName: contextName,
                    Kind: KubeResourceKind.DaemonSet,
                    Namespace: namespaceName,
                    Name: resourceName,
                    Action: KubeActionKind.RestartDaemonSetRollout)))
        {
            Availability = KubeActionAvailability.PreviewAndExecute,
            Environment = environment,
            CoverageLimits =
            [
                "This preview cannot fully model per-node drain interactions, disruption budgets, or host-local workload dependencies during restart."
            ]
        };
    }

    public static KubeActionPreviewResponse CreateDeleteJobPreview(
        string contextName,
        V1Job job,
        KubeActionLocalEnvironmentRules? localEnvironmentRules = null)
    {
        ArgumentNullException.ThrowIfNull(job);

        var resourceName = job.Metadata?.Name ?? string.Empty;
        var namespaceName = job.Metadata?.NamespaceProperty;
        var active = job.Status?.Active ?? 0;
        var succeeded = job.Status?.Succeeded ?? 0;
        var failed = job.Status?.Failed ?? 0;
        var completions = job.Spec?.Completions ?? 1;
        var environment = KubeActionEnvironmentClassifier.Classify(job.Metadata, contextName, localEnvironmentRules);
        var confidence = active > 0
            ? KubeActionPreviewConfidence.Medium
            : KubeActionPreviewConfidence.High;

        var warnings = new List<string>();
        if (active > 0)
        {
            warnings.Add("The job still has active pod work, so deletion may interrupt in-flight execution.");
        }

        var notes = new List<string>
        {
            active > 0
                ? "Deleting a Job removes the workload object and can stop active retry or completion tracking."
                : "Deleting a Job removes the workload object and its current completion history from the cluster view.",
            "This preview cannot fully model any external automation that might recreate the job."
        };

        var saferAlternatives = new List<KubeActionPreviewAlternative>();

        if (active > 0)
        {
            saferAlternatives.Add(new KubeActionPreviewAlternative(
                "Inspect job pods first",
                "Use the inspector and logs to confirm whether deletion is safer than letting the remaining job attempt complete."));
        }
        else if (failed > 0 && succeeded is 0)
        {
            saferAlternatives.Add(new KubeActionPreviewAlternative(
                "Inspect failed job evidence first",
                "Use pod status, events, and logs to decide whether cleanup is safer than preserving the failed workload evidence."));
        }
        else if (succeeded > 0)
        {
            saferAlternatives.Add(new KubeActionPreviewAlternative(
                "Keep the completed job record",
                "If you still need completion evidence or troubleshooting context, keep the finished Job until that record is no longer useful."));
        }
        else
        {
            saferAlternatives.Add(new KubeActionPreviewAlternative(
                "Inspect job history first",
                "Use status and related pod evidence to confirm whether deleting this Job is safer than leaving the current record intact."));
        }

        return new KubeActionPreviewResponse(
            Action: KubeActionKind.DeleteJob,
            Resource: new KubeResourceIdentity(contextName, KubeResourceKind.Job, namespaceName, resourceName),
            Summary: $"Job/{resourceName} would be deleted from namespace {namespaceName}.",
            Confidence: confidence,
            Guardrails: BuildDeleteJobGuardrails(job, environment, active, confidence, namespaceName),
            CoverageSummary: BuildCoverageSummary(confidence),
            Facts: BuildBoundaryFacts(
            [
                new KubeActionPreviewFact("Active pods", active.ToString()),
                new KubeActionPreviewFact("Succeeded", succeeded.ToString()),
                new KubeActionPreviewFact("Failed", failed.ToString()),
                new KubeActionPreviewFact("Desired completions", completions.ToString())
            ], contextName, namespaceName),
            Warnings: warnings,
            Notes: notes,
            SaferAlternatives: saferAlternatives,
            AffectedResources: [],
            TransparencyCommands: KubectlTransparencyFactory.CreateForActionPreview(
                new KubeActionPreviewRequest(
                    ContextName: contextName,
                    Kind: KubeResourceKind.Job,
                    Namespace: namespaceName,
                    Name: resourceName,
                    Action: KubeActionKind.DeleteJob)))
        {
            Availability = KubeActionAvailability.PreviewAndExecute,
            Environment = environment,
            CoverageLimits =
            [
                "This preview cannot fully model downstream controllers or external automation that may recreate job work after deletion."
            ]
        };
    }

    public static KubeActionPreviewResponse CreateCronJobSuspendPreview(
        string contextName,
        V1CronJob cronJob,
        bool suspend,
        KubeActionLocalEnvironmentRules? localEnvironmentRules = null)
    {
        ArgumentNullException.ThrowIfNull(cronJob);

        var resourceName = cronJob.Metadata?.Name ?? string.Empty;
        var namespaceName = cronJob.Metadata?.NamespaceProperty;
        var currentlySuspended = cronJob.Spec?.Suspend ?? false;
        var schedule = cronJob.Spec?.Schedule ?? "unknown";
        var activeJobs = cronJob.Status?.Active?.Count ?? 0;
        var environment = KubeActionEnvironmentClassifier.Classify(cronJob.Metadata, contextName, localEnvironmentRules);
        var confidence = KubeActionPreviewConfidence.High;
        var isNoOp = (suspend && currentlySuspended) || (!suspend && !currentlySuspended);

        var warnings = new List<string>();

        if (suspend && currentlySuspended)
        {
            warnings.Add("The CronJob is already suspended, so this would not change execution state.");
        }
        else if (!suspend && !currentlySuspended)
        {
            warnings.Add("The CronJob is already active, so this would not change execution state.");
        }

        if (activeJobs > 0)
        {
            warnings.Add($"{activeJobs} active job(s) already exist. Changing suspend state affects future scheduling, not those active jobs.");
        }

        var saferAlternatives = new List<KubeActionPreviewAlternative>();
        if (activeJobs > 0)
        {
            saferAlternatives.Add(new KubeActionPreviewAlternative(
                "Inspect active jobs first",
                "Check whether active or recent job runs are healthy before changing the schedule state."));
        }
        else
        {
            saferAlternatives.Add(new KubeActionPreviewAlternative(
                "Inspect recent job history first",
                "Check recent runs, failures, and completion patterns before changing the future schedule state."));
        }

        var affectedResources = cronJob.Status?.Active?
            .Where(static reference => !string.IsNullOrWhiteSpace(reference.Name))
            .Select(reference => new KubeRelatedResource(
                Relationship: "Active job",
                Kind: KubeResourceKind.Job,
                ApiVersion: reference.ApiVersion ?? "batch/v1",
                Name: reference.Name!,
                Namespace: namespaceName,
                Status: null,
                Summary: null))
            .ToArray() ?? [];

        var facts = BuildBoundaryFacts(
        [
            new KubeActionPreviewFact("Schedule", schedule),
            new KubeActionPreviewFact("Currently suspended", currentlySuspended ? "Yes" : "No"),
            new KubeActionPreviewFact("Active jobs", activeJobs.ToString())
        ], contextName, namespaceName, affectedResources);

        warnings.AddRange(BuildBoundaryWarnings(namespaceName, affectedResources));

        return new KubeActionPreviewResponse(
            Action: suspend ? KubeActionKind.SuspendCronJob : KubeActionKind.ResumeCronJob,
            Resource: new KubeResourceIdentity(contextName, KubeResourceKind.CronJob, namespaceName, resourceName),
            Summary: isNoOp
                ? suspend
                    ? $"CronJob/{resourceName} is already suspended, so suspend would not change scheduling."
                    : $"CronJob/{resourceName} is already active, so resume would not change scheduling."
                : suspend
                    ? $"CronJob/{resourceName} would be suspended."
                    : $"CronJob/{resourceName} would resume scheduling on {schedule}.",
            Confidence: confidence,
            Guardrails: BuildCronJobSuspendGuardrails(cronJob, environment, suspend, namespaceName),
            CoverageSummary: BuildCoverageSummary(confidence),
            Facts: facts,
            Warnings: warnings,
            Notes:
            [
                "Changing suspend only affects future scheduling decisions for the CronJob.",
                "Already-created jobs remain separate resources after this patch."
            ],
            SaferAlternatives: saferAlternatives,
            AffectedResources: affectedResources,
            TransparencyCommands: KubectlTransparencyFactory.CreateForActionPreview(
                new KubeActionPreviewRequest(
                    ContextName: contextName,
                    Kind: KubeResourceKind.CronJob,
                    Namespace: namespaceName,
                    Name: resourceName,
                    Action: suspend ? KubeActionKind.SuspendCronJob : KubeActionKind.ResumeCronJob)))
        {
            Availability = KubeActionAvailability.PreviewAndExecute,
            Environment = environment,
            CoverageLimits =
            [
                "This preview cannot fully model missed run backfill behavior or any external automation that watches the same CronJob."
            ]
        };
    }

    public static KubeActionPreviewResponse CreateNodeSchedulingPreview(
        string contextName,
        V1Node node,
        IReadOnlyList<V1Pod> scheduledPods,
        bool cordon,
        KubeActionLocalEnvironmentRules? localEnvironmentRules = null)
    {
        ArgumentNullException.ThrowIfNull(node);

        var resourceName = node.Metadata?.Name ?? string.Empty;
        var unschedulable = node.Spec?.Unschedulable ?? false;
        var environment = KubeActionEnvironmentClassifier.Classify(node.Metadata, contextName, localEnvironmentRules);
        var confidence = KubeActionPreviewConfidence.Medium;

        var warnings = new List<string>
        {
            "Node scheduling changes can affect multiple workloads at once and should be treated as a cluster-shape mutation."
        };

        if (scheduledPods.Count > 0)
        {
            warnings.Add($"{scheduledPods.Count} pod(s) are currently scheduled on this node.");
        }

        if (cordon && unschedulable)
        {
            warnings.Add("The node is already cordoned, so this would not change schedulability.");
        }
        else if (!cordon && !unschedulable)
        {
            warnings.Add("The node is already schedulable, so this would not change schedulability.");
        }

        var affectedResources = scheduledPods
            .Where(static pod => !string.IsNullOrWhiteSpace(pod.Metadata?.Name))
            .OrderBy(static pod => pod.Metadata!.NamespaceProperty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static pod => pod.Metadata!.Name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(static pod => new KubeRelatedResource(
                Relationship: "Scheduled pod",
                Kind: KubeResourceKind.Pod,
                ApiVersion: pod.ApiVersion ?? "v1",
                Name: pod.Metadata!.Name!,
                Namespace: pod.Metadata?.NamespaceProperty,
                Status: pod.Status?.Phase,
                Summary: pod.Metadata?.NamespaceProperty))
            .ToArray();

        var facts = BuildBoundaryFacts(
        [
            new KubeActionPreviewFact("Currently unschedulable", unschedulable ? "Yes" : "No"),
            new KubeActionPreviewFact("Scheduled pods", scheduledPods.Count.ToString())
        ], contextName, null, affectedResources);

        warnings.AddRange(BuildBoundaryWarnings(null, affectedResources));

        return new KubeActionPreviewResponse(
            Action: cordon ? KubeActionKind.CordonNode : KubeActionKind.UncordonNode,
            Resource: new KubeResourceIdentity(contextName, KubeResourceKind.Node, null, resourceName),
            Summary: cordon switch
            {
                true when unschedulable => $"Node/{resourceName} is already cordoned, so cordon would not change schedulability.",
                false when !unschedulable => $"Node/{resourceName} is already schedulable, so uncordon would not change schedulability.",
                true => $"Node/{resourceName} would be cordoned so new pods stop scheduling there.",
                _ => $"Node/{resourceName} would be uncordoned so new pods can schedule there again."
            },
            Confidence: confidence,
            Guardrails: BuildNodeSchedulingGuardrails(
                node,
                environment,
                cordon,
                unschedulable,
                scheduledPods.Count,
                CountAffectedNamespaces(null, affectedResources),
                IncludesSystemNamespaces(null, affectedResources)),
            CoverageSummary: BuildCoverageSummary(confidence),
            Facts: facts,
            Warnings: warnings,
            Notes:
            [
                "Changing node schedulability only affects future placements. It does not evict or move pods that already live on the node.",
                "This preview cannot fully model downstream autoscaler or cluster-operator reactions to the node shape change."
            ],
            SaferAlternatives:
            [
                new KubeActionPreviewAlternative("Inspect node workload first", "Review the pods already scheduled on the node before changing schedulability.")
            ],
            AffectedResources: affectedResources,
            TransparencyCommands: KubectlTransparencyFactory.CreateForActionPreview(
                new KubeActionPreviewRequest(
                    ContextName: contextName,
                    Kind: KubeResourceKind.Node,
                    Namespace: null,
                    Name: resourceName,
                    Action: cordon ? KubeActionKind.CordonNode : KubeActionKind.UncordonNode)))
        {
            Availability = KubeActionAvailability.PreviewAndExecute,
            Environment = environment,
            CoverageLimits =
            [
                "This preview cannot fully model scheduler, autoscaler, or cluster-operator reactions after the node scheduling state changes."
            ]
        };
    }

    public static KubeActionPreviewResponse CreateDrainNodePreview(
        string contextName,
        V1Node node,
        IReadOnlyList<V1Pod> scheduledPods,
        KubePodDisruptionBudgetImpact disruptionBudgetImpact,
        KubeActionLocalEnvironmentRules? localEnvironmentRules = null)
    {
        ArgumentNullException.ThrowIfNull(node);

        var resourceName = node.Metadata?.Name ?? string.Empty;
        var environment = KubeActionEnvironmentClassifier.Classify(node.Metadata, contextName, localEnvironmentRules);
        var affectedResources = scheduledPods
            .Where(static pod => !string.IsNullOrWhiteSpace(pod.Metadata?.Name))
            .OrderBy(static pod => pod.Metadata!.NamespaceProperty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static pod => pod.Metadata!.Name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(static pod => new KubeRelatedResource(
                Relationship: "Scheduled pod",
                Kind: KubeResourceKind.Pod,
                ApiVersion: pod.ApiVersion ?? "v1",
                Name: pod.Metadata!.Name!,
                Namespace: pod.Metadata?.NamespaceProperty,
                Status: pod.Status?.Phase,
                Summary: pod.Spec?.PriorityClassName))
            .Concat(disruptionBudgetImpact.RelatedResources)
            .ToArray();

        var facts = BuildBoundaryFacts(
        [
            new KubeActionPreviewFact("Scheduled pods", scheduledPods.Count.ToString()),
            new KubeActionPreviewFact("Currently unschedulable", (node.Spec?.Unschedulable ?? false) ? "Yes" : "No"),
            new KubeActionPreviewFact("Matched PDBs", disruptionBudgetImpact.MatchedBudgetCount.ToString())
        ], contextName, null, affectedResources);

        var warnings = BuildDrainWarnings(disruptionBudgetImpact).ToList();
        warnings.AddRange(BuildBoundaryWarnings(null, affectedResources));

        return new KubeActionPreviewResponse(
            Action: KubeActionKind.DrainNode,
            Resource: new KubeResourceIdentity(contextName, KubeResourceKind.Node, null, resourceName),
            Summary: $"Node/{resourceName} drain stays preview-only in the current slice.",
            Confidence: KubeActionPreviewConfidence.Medium,
            Guardrails: BuildDrainNodeGuardrails(
                node,
                environment,
                scheduledPods.Count,
                disruptionBudgetImpact,
                CountAffectedNamespaces(null, affectedResources),
                IncludesSystemNamespaces(null, affectedResources)),
            CoverageSummary: "Drain impact can be previewed, but direct execution stays intentionally unavailable in this slice.",
            Facts: facts,
            Warnings: warnings,
            Notes:
            [
                "This preview is intentionally preview-only until the stronger guardrail and confirmation path is fully implemented.",
                "Use the preview to inspect the node scope and likely affected pods, not to execute the drain yet."
            ],
            SaferAlternatives:
            [
                new KubeActionPreviewAlternative("Cordon the node first", "Cordon is a narrower scheduling control that is already executable in this slice."),
                new KubeActionPreviewAlternative("Inspect scheduled pods first", "Review the pod set that would be subject to eviction pressure.")
            ],
            AffectedResources: affectedResources,
            TransparencyCommands: KubectlTransparencyFactory.CreateForActionPreview(
                new KubeActionPreviewRequest(
                    ContextName: contextName,
                    Kind: KubeResourceKind.Node,
                    Namespace: null,
                    Name: resourceName,
                    Action: KubeActionKind.DrainNode)))
        {
            Availability = KubeActionAvailability.PreviewOnly,
            Environment = environment,
            CoverageLimits =
            [
                "This preview cannot fully model disruption budgets, daemonset exclusions, mirror pods, or storage semantics across every scheduled workload."
            ]
        };
    }

    private static string BuildSummary(string resourceName, int currentReplicas, int targetReplicas, int delta)
    {
        if (delta == 0)
        {
            return $"Deployment/{resourceName} is already set to {currentReplicas} replicas, so scale would not change desired count.";
        }

        var direction = delta switch
        {
            > 0 => $"scale up by {delta}",
            < 0 => $"scale down by {Math.Abs(delta)}",
            _ => "keep the current replica count"
        };

        return $"Deployment/{resourceName} would {direction}: {currentReplicas} -> {targetReplicas} replicas.";
    }

    private static IReadOnlyList<string> BuildWarnings(
        V1Deployment deployment,
        int currentReplicas,
        int targetReplicas,
        int readyReplicas,
        int matchingPodCount)
    {
        var warnings = new List<string>();

        if (targetReplicas == currentReplicas)
        {
            warnings.Add("The requested target matches the current desired replica count, so this would not change scale.");
        }

        if (targetReplicas == 0 && currentReplicas > 0)
        {
            warnings.Add("Scaling to zero would remove all currently matching pods from this deployment.");
        }
        else if (targetReplicas < currentReplicas)
        {
            warnings.Add($"Scaling down by {currentReplicas - targetReplicas} may remove any of the {matchingPodCount} currently matching pod(s); Kubernetes does not guarantee which pod names go first.");
        }

        if (readyReplicas < currentReplicas)
        {
            warnings.Add($"Only {readyReplicas}/{currentReplicas} replicas are ready right now. Previewing scale during an unhealthy rollout can understate operational risk.");
        }

        if (deployment.Spec?.Paused == true)
        {
            warnings.Add("This deployment is paused. The desired replica count can still change, but rollout progression remains paused.");
        }

        return warnings;
    }

    private static IReadOnlyList<string> BuildNotes(int currentReplicas, int targetReplicas, int matchingPodCount)
    {
        var notes = new List<string>
        {
            "This preview is computed from the current deployment status, selector, and currently matching pod set.",
            "Autoscalers, disruption budgets, quotas, scheduling, and later controller decisions can still change the actual outcome."
        };

        if (targetReplicas > currentReplicas)
        {
            notes.Add($"The controller would aim to create {targetReplicas - currentReplicas} additional replica(s) beyond the {matchingPodCount} pod(s) visible right now.");
        }

        return notes;
    }

    private static IReadOnlyList<string> BuildScaleCoverageLimits(
        V1Deployment deployment,
        int currentReplicas,
        int targetReplicas,
        int readyReplicas)
    {
        var limits = new List<string>
        {
            "This preview cannot prove whether schedulers, quotas, or node capacity will admit every resulting replica.",
            "Autoscalers, disruption budgets, and later controller reactions can still change the live outcome after the scale request."
        };

        if (deployment.Spec?.Paused == true)
        {
            limits.Add("The deployment is paused, so rollout progression after the scale change is less predictable from current evidence.");
        }

        if (readyReplicas < currentReplicas)
        {
            limits.Add("Current rollout health is already degraded, so the preview cannot fully isolate the effect of the requested scale change.");
        }

        if (targetReplicas == 0)
        {
            limits.Add("Scaling to zero removes all visible replicas, but the preview cannot fully model downstream traffic handling or external failover behavior.");
        }

        return limits;
    }

    private static KubeActionPreviewConfidence DetermineConfidence(
        V1Deployment deployment,
        int currentReplicas,
        int targetReplicas,
        int readyReplicas)
    {
        if (deployment.Spec?.Paused == true || readyReplicas < currentReplicas || targetReplicas == 0)
        {
            return KubeActionPreviewConfidence.Low;
        }

        if (targetReplicas == currentReplicas)
        {
            return KubeActionPreviewConfidence.High;
        }

        return KubeActionPreviewConfidence.Medium;
    }

    private static KubeActionPreviewConfidence DetermineRestartConfidence(
        V1Deployment deployment,
        int currentReplicas,
        int readyReplicas)
    {
        if (deployment.Spec?.Paused == true || readyReplicas < currentReplicas)
        {
            return KubeActionPreviewConfidence.Low;
        }

        return KubeActionPreviewConfidence.Medium;
    }

    private static KubeActionPreviewConfidence DetermineRollbackConfidence(
        V1Deployment deployment,
        int currentReplicas,
        int readyReplicas,
        KubeDeploymentRollbackResolution rollbackResolution,
        bool rollbackHistoryCoverageRestricted)
    {
        if (rollbackHistoryCoverageRestricted ||
            !rollbackResolution.CanRollback ||
            deployment.Spec?.Paused == true ||
            readyReplicas < currentReplicas)
        {
            return KubeActionPreviewConfidence.Low;
        }

        return KubeActionPreviewConfidence.Medium;
    }

    private static KubeActionPreviewConfidence DetermineDeletePodConfidence(
        KubeRelatedResource? immediateOwner,
        KubeRelatedResource? rolloutOwner,
        bool replacementLikely)
    {
        if (rolloutOwner is not null || (replacementLikely && immediateOwner is not null))
        {
            return KubeActionPreviewConfidence.High;
        }

        if (immediateOwner is not null)
        {
            return KubeActionPreviewConfidence.Medium;
        }

        return KubeActionPreviewConfidence.Low;
    }

    private static KubeActionGuardrailDecision BuildScaleGuardrails(
        V1Deployment deployment,
        KubeActionEnvironmentKind environment,
        int currentReplicas,
        int targetReplicas,
        int readyReplicas,
        KubeActionPreviewConfidence confidence,
        string? namespaceName)
    {
        if (targetReplicas == currentReplicas)
        {
            return new KubeActionGuardrailDecision(
                RiskLevel: KubeActionRiskLevel.Informational,
                ConfirmationLevel: KubeActionConfirmationLevel.InlineSummary,
                IsExecutionBlocked: true,
                Summary: "The deployment is already at the requested replica count, so this preview stops at the current scale state.",
                AcknowledgementHint: null,
                Reasons:
                [
                    "The requested scale state already matches the live deployment."
                ]);
        }

        var reasons = new List<string>();
        var delta = Math.Abs(targetReplicas - currentReplicas);
        var risk = KubeActionRiskLevel.Low;

        if (targetReplicas == 0)
        {
            risk = KubeActionRiskLevel.High;
            reasons.Add("Scaling to zero would remove every currently matching pod.");
        }
        else if (targetReplicas < currentReplicas && delta >= Math.Max(2, currentReplicas / 2))
        {
            risk = KubeActionRiskLevel.Medium;
            reasons.Add("This change removes a large share of the current replica set.");
        }

        if (readyReplicas < currentReplicas)
        {
            risk = risk < KubeActionRiskLevel.High ? KubeActionRiskLevel.High : risk;
            reasons.Add($"Only {readyReplicas}/{currentReplicas} replicas are ready right now.");
        }

        return KubeActionPolicyEvaluator.Evaluate(
            new KubeActionPolicyContext(
                Action: KubeActionKind.ScaleDeployment,
                Resource: new KubeResourceIdentity("context", KubeResourceKind.Deployment, deployment.Metadata?.NamespaceProperty, deployment.Metadata?.Name ?? "unknown"),
                Environment: environment,
                Availability: KubeActionAvailability.PreviewAndExecute,
                Confidence: confidence,
                BaseRiskLevel: risk,
                Reasons: reasons,
                DirectTargetCount: Math.Max(currentReplicas, targetReplicas),
                AffectedNamespaceCount: CountAffectedNamespaces(namespaceName),
                IncludesSystemNamespaces: IncludesSystemNamespaces(namespaceName),
                CanExecuteSafelyFromCurrentEvidence: !(confidence is KubeActionPreviewConfidence.Low && targetReplicas == 0)));
    }

    private static KubeActionGuardrailDecision BuildRestartGuardrails(
        V1Deployment deployment,
        KubeActionEnvironmentKind environment,
        int currentReplicas,
        int readyReplicas,
        KubeActionPreviewConfidence confidence,
        KubePodDisruptionBudgetImpact disruptionBudgetImpact,
        string? namespaceName)
    {
        var reasons = new List<string>();
        var risk = KubeActionRiskLevel.Low;

        reasons.Add("A rollout restart rotates pods even though the replica count stays the same.");

        if (deployment.Spec?.Paused == true)
        {
            risk = KubeActionRiskLevel.High;
            reasons.Add("The deployment is paused, so restart timing may be less predictable.");
        }

        if (readyReplicas < currentReplicas)
        {
            risk = KubeActionRiskLevel.High;
            reasons.Add($"Only {readyReplicas}/{currentReplicas} replicas are ready right now.");
        }

        if (disruptionBudgetImpact.HasMatchedBudgets)
        {
            reasons.Add($"{disruptionBudgetImpact.MatchedBudgetCount} matched PodDisruptionBudget(s) can constrain disruption headroom during rollout.");
        }

        return KubeActionPolicyEvaluator.Evaluate(
            new KubeActionPolicyContext(
                Action: KubeActionKind.RestartDeploymentRollout,
                Resource: new KubeResourceIdentity("context", KubeResourceKind.Deployment, deployment.Metadata?.NamespaceProperty, deployment.Metadata?.Name ?? "unknown"),
                Environment: environment,
                Availability: KubeActionAvailability.PreviewAndExecute,
                Confidence: confidence,
                BaseRiskLevel: risk,
                Reasons: reasons,
                HasSharedDependencies: disruptionBudgetImpact.HasMatchedBudgets,
                DependencyImpactUnresolved: disruptionBudgetImpact.ZeroDisruptionsAllowedCount > 0 || disruptionBudgetImpact.UnknownAllowanceCount > 0,
                DirectTargetCount: currentReplicas,
                AffectedNamespaceCount: CountAffectedNamespaces(namespaceName, disruptionBudgetImpact.RelatedResources),
                IncludesSystemNamespaces: IncludesSystemNamespaces(namespaceName, disruptionBudgetImpact.RelatedResources),
                CanExecuteSafelyFromCurrentEvidence: confidence is not KubeActionPreviewConfidence.Low));
    }

    private static KubeActionGuardrailDecision BuildRollbackGuardrails(
        V1Deployment deployment,
        KubeActionEnvironmentKind environment,
        int currentReplicas,
        int readyReplicas,
        KubeActionPreviewConfidence confidence,
        KubeDeploymentRollbackResolution rollbackResolution,
        KubePodDisruptionBudgetImpact disruptionBudgetImpact,
        string? namespaceName,
        bool rollbackHistoryCoverageRestricted)
    {
        if (!rollbackResolution.CanRollback)
        {
            return new KubeActionGuardrailDecision(
                RiskLevel: rollbackHistoryCoverageRestricted ? KubeActionRiskLevel.High : KubeActionRiskLevel.Informational,
                ConfirmationLevel: rollbackHistoryCoverageRestricted
                    ? KubeActionConfirmationLevel.ExplicitReview
                    : KubeActionConfirmationLevel.InlineSummary,
                IsExecutionBlocked: true,
                Summary: rollbackHistoryCoverageRestricted
                    ? "Retained rollout history could not be fully inspected, so direct rollback stays blocked from current evidence."
                    : "No retained rollout history is available to undo from the current cluster view.",
                AcknowledgementHint: null,
                Reasons: rollbackHistoryCoverageRestricted ?
                [
                    "Kubernetes RBAC limited retained ReplicaSet visibility for this deployment.",
                    "No verified prior revision is available from current evidence."
                ]
                :
                [
                    "No retained prior deployment revision is currently available for direct rollback."
                ]);
        }

        var reasons = new List<string>
        {
            $"Rollback would restore retained revision {rollbackResolution.PreviousRevision} for the current deployment template."
        };
        var risk = KubeActionRiskLevel.Low;

        if (rollbackResolution.UsedReplicaSetRevisionFallback)
        {
            risk = KubeActionRiskLevel.Medium;
            reasons.Add("Deployment revision annotations were incomplete, so the rollback target was inferred from retained ReplicaSet history.");
        }

        if (deployment.Spec?.Paused == true)
        {
            risk = KubeActionRiskLevel.High;
            reasons.Add("The deployment is paused, so rollback timing may be less predictable.");
        }

        if (readyReplicas < currentReplicas)
        {
            risk = KubeActionRiskLevel.High;
            reasons.Add($"Only {readyReplicas}/{currentReplicas} replicas are ready right now.");
        }

        if (disruptionBudgetImpact.HasMatchedBudgets)
        {
            reasons.Add($"{disruptionBudgetImpact.MatchedBudgetCount} matched PodDisruptionBudget(s) can constrain disruption headroom during rollback.");
        }

        return KubeActionPolicyEvaluator.Evaluate(
            new KubeActionPolicyContext(
                Action: KubeActionKind.RollbackDeploymentRollout,
                Resource: new KubeResourceIdentity("context", KubeResourceKind.Deployment, deployment.Metadata?.NamespaceProperty, deployment.Metadata?.Name ?? "unknown"),
                Environment: environment,
                Availability: KubeActionAvailability.PreviewAndExecute,
                Confidence: confidence,
                BaseRiskLevel: risk,
                Reasons: reasons,
                HasSharedDependencies: disruptionBudgetImpact.HasMatchedBudgets,
                DependencyImpactUnresolved: disruptionBudgetImpact.ZeroDisruptionsAllowedCount > 0 || disruptionBudgetImpact.UnknownAllowanceCount > 0,
                DirectTargetCount: currentReplicas,
                AffectedNamespaceCount: CountAffectedNamespaces(namespaceName, disruptionBudgetImpact.RelatedResources),
                IncludesSystemNamespaces: IncludesSystemNamespaces(namespaceName, disruptionBudgetImpact.RelatedResources),
                CanExecuteSafelyFromCurrentEvidence: true));
    }

    private static IReadOnlyList<string> BuildRestartCoverageLimits(
        V1Deployment deployment,
        int readyReplicas,
        int currentReplicas,
        KubePodDisruptionBudgetImpact disruptionBudgetImpact)
    {
        var limits = new List<string>
        {
            "This preview cannot predict the exact pod replacement order, restart timing, or drain timing for each replica.",
            "Concurrent controller or operator changes can still alter the resulting rollout after the restart request is submitted."
        };

        if (deployment.Spec?.Paused == true)
        {
            limits.Add("The deployment is paused, so the preview cannot determine when the restarted template will actually roll forward.");
        }

        if (readyReplicas < currentReplicas)
        {
            limits.Add("The workload is already unhealthy, so the preview cannot cleanly separate restart impact from the current degraded state.");
        }

        if (disruptionBudgetImpact.HasMatchedBudgets)
        {
            limits.Add("Matched PodDisruptionBudget status can still change while the rollout is in flight, so current disruption headroom is only a point-in-time signal.");
        }

        return limits;
    }

    private static IReadOnlyList<string> BuildRollbackCoverageLimits(
        V1Deployment deployment,
        int readyReplicas,
        int currentReplicas,
        KubeDeploymentRollbackResolution rollbackResolution,
        KubePodDisruptionBudgetImpact disruptionBudgetImpact,
        bool rollbackHistoryCoverageRestricted)
    {
        var limits = new List<string>
        {
            "This preview cannot guarantee the exact pod replacement order, rollback timing, or whether the prior ReplicaSet will regain traffic exactly when you expect.",
            "Concurrent controller or operator changes can still alter the resulting rollout after the rollback request is submitted."
        };

        if (!rollbackResolution.CanRollback)
        {
            limits.Add(rollbackHistoryCoverageRestricted
                ? "Retained ReplicaSet history could not be fully inspected under current RBAC, so the preview cannot verify a direct rollback target."
                : "No retained prior ReplicaSet template is currently visible, so rollback availability depends on rollout history that is no longer present.");
        }

        if (rollbackResolution.UsedReplicaSetRevisionFallback)
        {
            limits.Add("The current deployment revision was inferred from retained ReplicaSet history because deployment annotations were incomplete.");
        }

        if (deployment.Spec?.Paused == true)
        {
            limits.Add("The deployment is paused, so the preview cannot determine when the restored template will actually roll forward.");
        }

        if (readyReplicas < currentReplicas)
        {
            limits.Add("The workload is already unhealthy, so the preview cannot cleanly separate rollback impact from the current degraded state.");
        }

        if (disruptionBudgetImpact.HasMatchedBudgets)
        {
            limits.Add("Matched PodDisruptionBudget status can still change while the rollback is in flight, so current disruption headroom is only a point-in-time signal.");
        }

        return limits;
    }

    private static IReadOnlyList<string> BuildRestartDisruptionBudgetWarnings(KubePodDisruptionBudgetImpact disruptionBudgetImpact)
    {
        if (!disruptionBudgetImpact.HasMatchedBudgets)
        {
            return [];
        }

        var warnings = new List<string>();

        if (disruptionBudgetImpact.ZeroDisruptionsAllowedCount > 0)
        {
            warnings.Add($"{disruptionBudgetImpact.ZeroDisruptionsAllowedCount} matched PodDisruptionBudget(s) currently allow 0 disruptions. Rollout restart may progress more slowly than the deployment strategy alone suggests.");
        }
        else
        {
            warnings.Add($"{disruptionBudgetImpact.MatchedBudgetCount} matched PodDisruptionBudget(s) can throttle how quickly pods rotate during rollout restart.");
        }

        if (disruptionBudgetImpact.UnknownAllowanceCount > 0)
        {
            warnings.Add("At least one matched PodDisruptionBudget has no current disruption allowance status, so disruption headroom is only partially resolved.");
        }

        return warnings;
    }

    private static IReadOnlyList<string> BuildRollbackDeploymentStrategyWarnings(V1Deployment deployment)
    {
        if (!string.Equals(deployment.Spec?.Strategy?.Type, "Recreate", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return
        [
            "This deployment uses Recreate strategy, so rollout undo can replace all replicas together instead of performing a rolling handoff."
        ];
    }

    private static KubeActionGuardrailDecision BuildDeletePodGuardrails(
        V1Pod pod,
        KubeActionEnvironmentKind environment,
        bool replacementLikely,
        KubeRelatedResource? immediateOwner,
        KubeRelatedResource? rolloutOwner,
        KubeActionPreviewConfidence confidence,
        KubePodDisruptionBudgetImpact disruptionBudgetImpact)
    {
        var reasons = new List<string>();
        var risk = replacementLikely
            ? KubeActionRiskLevel.Low
            : KubeActionRiskLevel.High;

        if (rolloutOwner is not null)
        {
            reasons.Add($"The pod appears to be managed by {rolloutOwner.Kind}/{rolloutOwner.Name}.");
        }
        else if (immediateOwner is not null)
        {
            reasons.Add($"The pod appears to be managed by {immediateOwner.Kind}/{immediateOwner.Name}.");
        }
        else
        {
            reasons.Add("No controller owner was confirmed from the current pod metadata.");
        }

        if (!replacementLikely)
        {
            reasons.Add("Replacement is not strongly guaranteed from current controller evidence.");
        }

        if (disruptionBudgetImpact.HasMatchedBudgets)
        {
            reasons.Add($"{disruptionBudgetImpact.MatchedBudgetCount} matched PodDisruptionBudget(s) advertise disruption constraints for this pod.");
        }

        return KubeActionPolicyEvaluator.Evaluate(
            new KubeActionPolicyContext(
                Action: KubeActionKind.DeletePod,
                Resource: new KubeResourceIdentity("context", KubeResourceKind.Pod, pod.Metadata?.NamespaceProperty, pod.Metadata?.Name ?? "unknown"),
                Environment: environment,
                Availability: KubeActionAvailability.PreviewAndExecute,
                Confidence: confidence,
                BaseRiskLevel: risk,
                Reasons: reasons,
                HasSharedDependencies: disruptionBudgetImpact.HasMatchedBudgets,
                DependencyImpactUnresolved: disruptionBudgetImpact.ZeroDisruptionsAllowedCount > 0 || disruptionBudgetImpact.UnknownAllowanceCount > 0,
                DirectTargetCount: 1,
                AffectedNamespaceCount: CountAffectedNamespaces(pod.Metadata?.NamespaceProperty, disruptionBudgetImpact.RelatedResources),
                IncludesSystemNamespaces: IncludesSystemNamespaces(pod.Metadata?.NamespaceProperty, disruptionBudgetImpact.RelatedResources),
                CanExecuteSafelyFromCurrentEvidence: replacementLikely || confidence is not KubeActionPreviewConfidence.Low));
    }

    private static IReadOnlyList<string> BuildDeletePodDisruptionBudgetWarnings(KubePodDisruptionBudgetImpact disruptionBudgetImpact)
    {
        if (!disruptionBudgetImpact.HasMatchedBudgets)
        {
            return [];
        }

        var warnings = new List<string>();

        if (disruptionBudgetImpact.ZeroDisruptionsAllowedCount > 0)
        {
            warnings.Add($"{disruptionBudgetImpact.ZeroDisruptionsAllowedCount} matched PodDisruptionBudget(s) currently allow 0 disruptions. The workload advertises stricter disruption constraints than this direct pod delete can guarantee.");
        }
        else
        {
            warnings.Add($"{disruptionBudgetImpact.MatchedBudgetCount} matched PodDisruptionBudget(s) advertise disruption limits for this pod.");
        }

        if (disruptionBudgetImpact.UnknownAllowanceCount > 0)
        {
            warnings.Add("At least one matched PodDisruptionBudget has no current disruption allowance status, so the preview cannot fully estimate disruption headroom.");
        }

        return warnings;
    }

    private static KubeActionGuardrailDecision BuildStatefulSetScaleGuardrails(
        V1StatefulSet statefulSet,
        KubeActionEnvironmentKind environment,
        int currentReplicas,
        int targetReplicas,
        int readyReplicas,
        KubeActionPreviewConfidence confidence,
        string? namespaceName)
    {
        if (targetReplicas == currentReplicas)
        {
            return new KubeActionGuardrailDecision(
                RiskLevel: KubeActionRiskLevel.Informational,
                ConfirmationLevel: KubeActionConfirmationLevel.InlineSummary,
                IsExecutionBlocked: true,
                Summary: "The StatefulSet is already at the requested replica count, so this preview stops at the current scale state.",
                AcknowledgementHint: null,
                Reasons:
                [
                    "The requested scale state already matches the live StatefulSet."
                ]);
        }

        var reasons = new List<string>();
        var risk = targetReplicas == 0 ? KubeActionRiskLevel.High : KubeActionRiskLevel.Low;

        if (targetReplicas < currentReplicas)
        {
            reasons.Add("StatefulSet scale-down removes the highest ordinals first and can affect stable identity assumptions.");
            if (targetReplicas < currentReplicas - 1)
            {
                risk = KubeActionRiskLevel.Medium;
            }
        }

        if (readyReplicas < currentReplicas)
        {
            risk = KubeActionRiskLevel.High;
            reasons.Add($"Only {readyReplicas}/{currentReplicas} replicas are ready right now.");
        }

        return KubeActionPolicyEvaluator.Evaluate(
            new KubeActionPolicyContext(
                Action: KubeActionKind.ScaleStatefulSet,
                Resource: new KubeResourceIdentity("context", KubeResourceKind.StatefulSet, statefulSet.Metadata?.NamespaceProperty, statefulSet.Metadata?.Name ?? "unknown"),
                Environment: environment,
                Availability: KubeActionAvailability.PreviewAndExecute,
                Confidence: confidence,
                BaseRiskLevel: risk,
                Reasons: reasons,
                DirectTargetCount: Math.Max(currentReplicas, targetReplicas),
                AffectedNamespaceCount: CountAffectedNamespaces(namespaceName),
                IncludesSystemNamespaces: IncludesSystemNamespaces(namespaceName),
                CanExecuteSafelyFromCurrentEvidence: !(confidence is KubeActionPreviewConfidence.Low && targetReplicas == 0)));
    }

    private static KubeActionGuardrailDecision BuildDaemonSetRestartGuardrails(
        V1DaemonSet daemonSet,
        KubeActionEnvironmentKind environment,
        int ready,
        int desired,
        KubeActionPreviewConfidence confidence,
        KubePodDisruptionBudgetImpact disruptionBudgetImpact,
        string? namespaceName)
    {
        var reasons = new List<string>
        {
            "A daemonset rollout restart can rotate pods across many nodes in one action."
        };
        var risk = KubeActionRiskLevel.High;

        if (ready < desired)
        {
            reasons.Add("The DaemonSet is already degraded on at least one node.");
        }

        if (disruptionBudgetImpact.HasMatchedBudgets)
        {
            reasons.Add($"{disruptionBudgetImpact.MatchedBudgetCount} matched PodDisruptionBudget(s) can constrain disruption headroom across the restarted nodes.");
        }

        return KubeActionPolicyEvaluator.Evaluate(
            new KubeActionPolicyContext(
                Action: KubeActionKind.RestartDaemonSetRollout,
                Resource: new KubeResourceIdentity("context", KubeResourceKind.DaemonSet, daemonSet.Metadata?.NamespaceProperty, daemonSet.Metadata?.Name ?? "unknown"),
                Environment: environment,
                Availability: KubeActionAvailability.PreviewAndExecute,
                Confidence: confidence,
                BaseRiskLevel: risk,
                Reasons: reasons,
                HasSharedDependencies: disruptionBudgetImpact.HasMatchedBudgets,
                DependencyImpactUnresolved: disruptionBudgetImpact.ZeroDisruptionsAllowedCount > 0 || disruptionBudgetImpact.UnknownAllowanceCount > 0,
                MultiResource: true,
                DirectTargetCount: desired,
                AffectedNamespaceCount: CountAffectedNamespaces(namespaceName, disruptionBudgetImpact.RelatedResources),
                IncludesSystemNamespaces: IncludesSystemNamespaces(namespaceName, disruptionBudgetImpact.RelatedResources),
                CanExecuteSafelyFromCurrentEvidence: confidence is not KubeActionPreviewConfidence.Low));
    }

    private static IReadOnlyList<string> BuildDaemonSetDisruptionBudgetWarnings(KubePodDisruptionBudgetImpact disruptionBudgetImpact)
    {
        if (!disruptionBudgetImpact.HasMatchedBudgets)
        {
            return [];
        }

        var warnings = new List<string>();

        if (disruptionBudgetImpact.ZeroDisruptionsAllowedCount > 0)
        {
            warnings.Add($"{disruptionBudgetImpact.ZeroDisruptionsAllowedCount} matched PodDisruptionBudget(s) currently allow 0 disruptions. DaemonSet rollout pace can still stall behind disruption headroom even when restart intent is clear.");
        }
        else
        {
            warnings.Add($"{disruptionBudgetImpact.MatchedBudgetCount} matched PodDisruptionBudget(s) can constrain disruption headroom for restarted DaemonSet pods.");
        }

        if (disruptionBudgetImpact.UnknownAllowanceCount > 0)
        {
            warnings.Add("At least one matched PodDisruptionBudget has no current disruption allowance status, so node-level disruption headroom is only partially resolved.");
        }

        return warnings;
    }

    private static KubeActionGuardrailDecision BuildDeleteJobGuardrails(
        V1Job job,
        KubeActionEnvironmentKind environment,
        int activePods,
        KubeActionPreviewConfidence confidence,
        string? namespaceName)
    {
        var reasons = new List<string>();
        var risk = activePods > 0 ? KubeActionRiskLevel.Medium : KubeActionRiskLevel.Low;

        if (activePods > 0)
        {
            reasons.Add("The job still has active work, so deletion can interrupt in-flight execution.");
        }

        return KubeActionPolicyEvaluator.Evaluate(
            new KubeActionPolicyContext(
                Action: KubeActionKind.DeleteJob,
                Resource: new KubeResourceIdentity("context", KubeResourceKind.Job, job.Metadata?.NamespaceProperty, job.Metadata?.Name ?? "unknown"),
                Environment: environment,
                Availability: KubeActionAvailability.PreviewAndExecute,
                Confidence: confidence,
                BaseRiskLevel: risk,
                Reasons: reasons,
                DirectTargetCount: Math.Max(activePods, 1),
                AffectedNamespaceCount: CountAffectedNamespaces(namespaceName),
                IncludesSystemNamespaces: IncludesSystemNamespaces(namespaceName)));
    }

    private static IReadOnlyList<string> BuildDrainWarnings(KubePodDisruptionBudgetImpact disruptionBudgetImpact)
    {
        var warnings = new List<string>
        {
            "Drain can evict multiple workloads and depends on disruption budgets, daemonset handling, and emptyDir semantics."
        };

        if (!disruptionBudgetImpact.HasMatchedBudgets)
        {
            return warnings;
        }

        warnings.Add($"{disruptionBudgetImpact.MatchedBudgetCount} matched PodDisruptionBudget(s) apply to pods on this node and can limit or block eviction during drain.");

        if (disruptionBudgetImpact.ZeroDisruptionsAllowedCount > 0)
        {
            warnings.Add($"{disruptionBudgetImpact.ZeroDisruptionsAllowedCount} matched PodDisruptionBudget(s) currently allow 0 disruptions.");
        }

        if (disruptionBudgetImpact.UnknownAllowanceCount > 0)
        {
            warnings.Add("At least one matched PodDisruptionBudget has no current disruption allowance status, so drain headroom is only partially resolved.");
        }

        return warnings;
    }

    private static KubeActionGuardrailDecision BuildCronJobSuspendGuardrails(
        V1CronJob cronJob,
        KubeActionEnvironmentKind environment,
        bool suspend,
        string? namespaceName)
    {
        var currentlySuspended = cronJob.Spec?.Suspend ?? false;
        if ((suspend && currentlySuspended) || (!suspend && !currentlySuspended))
        {
            return new KubeActionGuardrailDecision(
                RiskLevel: KubeActionRiskLevel.Informational,
                ConfirmationLevel: KubeActionConfirmationLevel.InlineSummary,
                IsExecutionBlocked: true,
                Summary: suspend
                    ? "The CronJob is already suspended, so this preview stops at the current state."
                    : "The CronJob is already active, so this preview stops at the current state.",
                AcknowledgementHint: null,
                Reasons:
                [
                    suspend
                        ? "The requested suspend state already matches the live CronJob."
                        : "The requested resume state already matches the live CronJob."
                ]);
        }

        var reasons = new List<string>
        {
            suspend
                ? "Suspending a CronJob changes future schedule behavior."
                : "Resuming a CronJob re-enables future schedule behavior."
        };

        return KubeActionPolicyEvaluator.Evaluate(
            new KubeActionPolicyContext(
                Action: suspend ? KubeActionKind.SuspendCronJob : KubeActionKind.ResumeCronJob,
                Resource: new KubeResourceIdentity("context", KubeResourceKind.CronJob, cronJob.Metadata?.NamespaceProperty, cronJob.Metadata?.Name ?? "unknown"),
                Environment: environment,
                Availability: KubeActionAvailability.PreviewAndExecute,
                Confidence: KubeActionPreviewConfidence.High,
                BaseRiskLevel: KubeActionRiskLevel.Low,
                Reasons: reasons,
                DirectTargetCount: 1,
                AffectedNamespaceCount: CountAffectedNamespaces(namespaceName),
                IncludesSystemNamespaces: IncludesSystemNamespaces(namespaceName)));
    }

    private static KubeActionGuardrailDecision BuildNodeSchedulingGuardrails(
        V1Node node,
        KubeActionEnvironmentKind environment,
        bool cordon,
        bool currentlyUnschedulable,
        int scheduledPodCount,
        int affectedNamespaceCount,
        bool includesSystemNamespaces)
    {
        if ((cordon && currentlyUnschedulable) || (!cordon && !currentlyUnschedulable))
        {
            return new KubeActionGuardrailDecision(
                RiskLevel: KubeActionRiskLevel.Informational,
                ConfirmationLevel: KubeActionConfirmationLevel.InlineSummary,
                IsExecutionBlocked: true,
                Summary: cordon
                    ? "The node is already cordoned, so this preview stops at the current scheduling state."
                    : "The node is already schedulable, so this preview stops at the current scheduling state.",
                AcknowledgementHint: null,
                Reasons:
                [
                    cordon
                        ? "The requested cordon state already matches the live node."
                        : "The requested uncordon state already matches the live node."
                ]);
        }

        return KubeActionPolicyEvaluator.Evaluate(
            new KubeActionPolicyContext(
                Action: cordon ? KubeActionKind.CordonNode : KubeActionKind.UncordonNode,
                Resource: new KubeResourceIdentity("context", KubeResourceKind.Node, null, node.Metadata?.Name ?? "unknown"),
                Environment: environment,
                Availability: KubeActionAvailability.PreviewAndExecute,
                Confidence: KubeActionPreviewConfidence.Medium,
                BaseRiskLevel: cordon ? KubeActionRiskLevel.High : KubeActionRiskLevel.Medium,
                Reasons:
                [
                    cordon
                        ? "Cordon changes cluster scheduling behavior for a whole node."
                        : "Uncordon re-opens a node to new scheduling."
                ],
                MultiResource: cordon,
                DirectTargetCount: Math.Max(scheduledPodCount, 1),
                AffectedNamespaceCount: affectedNamespaceCount,
                IncludesSystemNamespaces: includesSystemNamespaces));
    }

    private static KubeActionGuardrailDecision BuildDrainNodeGuardrails(
        V1Node node,
        KubeActionEnvironmentKind environment,
        int scheduledPodCount,
        KubePodDisruptionBudgetImpact disruptionBudgetImpact,
        int affectedNamespaceCount,
        bool includesSystemNamespaces)
    {
        var reasons = new List<string>
        {
            $"{scheduledPodCount} pod(s) are currently scheduled on the node.",
            "Drain requires stronger guardrails than the current execution path provides."
        };

        if (disruptionBudgetImpact.HasMatchedBudgets)
        {
            reasons.Add($"{disruptionBudgetImpact.MatchedBudgetCount} matched PodDisruptionBudget(s) can limit or block eviction during drain.");
        }

        return KubeActionPolicyEvaluator.Evaluate(
            new KubeActionPolicyContext(
                Action: KubeActionKind.DrainNode,
                Resource: new KubeResourceIdentity("context", KubeResourceKind.Node, null, node.Metadata?.Name ?? "unknown"),
                Environment: environment,
                Availability: KubeActionAvailability.PreviewOnly,
                Confidence: KubeActionPreviewConfidence.Medium,
                BaseRiskLevel: KubeActionRiskLevel.Critical,
                Reasons: reasons,
                HasSharedDependencies: disruptionBudgetImpact.HasMatchedBudgets,
                DependencyImpactUnresolved: disruptionBudgetImpact.ZeroDisruptionsAllowedCount > 0 || disruptionBudgetImpact.UnknownAllowanceCount > 0,
                MultiResource: true,
                DirectTargetCount: Math.Max(scheduledPodCount, 1),
                AffectedNamespaceCount: affectedNamespaceCount,
                IncludesSystemNamespaces: includesSystemNamespaces,
                CanExecuteSafelyFromCurrentEvidence: false));
    }

    private static IReadOnlyList<string> BuildDeletePodCoverageLimits(
        KubeRelatedResource? immediateOwner,
        KubeRelatedResource? rolloutOwner,
        bool replacementLikely,
        KubePodDisruptionBudgetImpact disruptionBudgetImpact)
    {
        var limits = new List<string>
        {
            "This preview cannot guarantee which downstream requests, retries, or connection drains will land on the deleted pod before Kubernetes removes it."
        };

        if (rolloutOwner is null && immediateOwner is null)
        {
            limits.Add("No controller owner was confirmed from current metadata, so replacement behavior remains structurally uncertain.");
        }
        else if (!replacementLikely)
        {
            limits.Add("Current evidence does not strongly guarantee that another controller will recreate this pod after deletion.");
        }
        else
        {
            limits.Add("A replacement looks likely from current ownership, but the preview cannot prove when that replacement will become ready.");
        }

        if (disruptionBudgetImpact.HasMatchedBudgets)
        {
            limits.Add("Matched PodDisruptionBudget resources are modeled as impact context, but direct pod deletion can still bypass eviction-style disruption gating.");
        }

        return limits;
    }

    private static string BuildCoverageSummary(KubeActionPreviewConfidence confidence)
    {
        return confidence switch
        {
            KubeActionPreviewConfidence.High => "Current-state coverage is strong for the current target and controller evidence.",
            KubeActionPreviewConfidence.Medium => "Current-state coverage is partial. The controller intent is clear, but later scheduling and policy decisions can still shift the real outcome.",
            _ => "Current-state coverage is limited. This preview cannot fully account for rollout health, policies, autoscalers, or later controller choices."
        };
    }

    private static string BuildRollbackSummary(
        string resourceName,
        int currentReplicas,
        KubeDeploymentRollbackResolution rollbackResolution,
        bool rollbackHistoryCoverageRestricted)
    {
        if (rollbackResolution.PreviousRevision.HasValue)
        {
            return $"Deployment/{resourceName} would restore retained revision {rollbackResolution.PreviousRevision.Value} across {currentReplicas} desired replica(s).";
        }

        return rollbackHistoryCoverageRestricted
            ? $"Deployment/{resourceName} rollback target could not be verified from the current cluster visibility."
            : $"Deployment/{resourceName} has no retained prior revision to undo from the current cluster view.";
    }

    private static IReadOnlyList<KubeActionPreviewAlternative> BuildSaferAlternatives(
        int currentReplicas,
        int targetReplicas,
        int readyReplicas)
    {
        var alternatives = new List<KubeActionPreviewAlternative>();

        if (targetReplicas == 0 && currentReplicas > 1)
        {
            alternatives.Add(new KubeActionPreviewAlternative(
                Label: "Scale to 1 first",
                Reason: "Keep one replica available while you verify whether traffic, probes, and downstream dependencies stay stable."));
        }

        if (targetReplicas < currentReplicas - 1)
        {
            alternatives.Add(new KubeActionPreviewAlternative(
                Label: $"Scale to {currentReplicas - 1}",
                Reason: "A smaller step-down reduces the chance of removing too much capacity before you can observe the result."));
        }

        if (readyReplicas < currentReplicas)
        {
            alternatives.Add(new KubeActionPreviewAlternative(
                Label: "Inspect rollout health first",
                Reason: "Use the existing timeline, signals, and logs to stabilize the deployment before changing its desired scale."));
        }

        if (alternatives.Count is 0)
        {
            alternatives.Add(new KubeActionPreviewAlternative(
                Label: "Keep the current replica count",
                Reason: "If you only need confirmation, the current desired scale is already consistent with the live workload view."));
        }

        return alternatives;
    }

    private static void AppendDeploymentStrategyFacts(List<KubeActionPreviewFact> facts, V1Deployment deployment)
    {
        var strategyType = NormalizeStrategyType(deployment.Spec?.Strategy?.Type, "RollingUpdate");
        facts.Add(new KubeActionPreviewFact("Strategy", strategyType));

        if (deployment.Spec?.Strategy?.RollingUpdate?.MaxUnavailable is { } maxUnavailable)
        {
            facts.Add(new KubeActionPreviewFact("Max unavailable", maxUnavailable.ToString()));
        }

        if (deployment.Spec?.Strategy?.RollingUpdate?.MaxSurge is { } maxSurge)
        {
            facts.Add(new KubeActionPreviewFact("Max surge", maxSurge.ToString()));
        }
    }

    private static IReadOnlyList<string> BuildDeploymentStrategyWarnings(V1Deployment deployment)
    {
        if (!string.Equals(deployment.Spec?.Strategy?.Type, "Recreate", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return
        [
            "This deployment uses Recreate strategy, so a rollout restart can replace all replicas together instead of performing a rolling handoff."
        ];
    }

    private static void AppendDaemonSetStrategyFacts(List<KubeActionPreviewFact> facts, V1DaemonSet daemonSet)
    {
        var strategyType = NormalizeStrategyType(daemonSet.Spec?.UpdateStrategy?.Type, "RollingUpdate");
        facts.Add(new KubeActionPreviewFact("Update strategy", strategyType));

        if (daemonSet.Spec?.UpdateStrategy?.RollingUpdate?.MaxUnavailable is { } maxUnavailable)
        {
            facts.Add(new KubeActionPreviewFact("Max unavailable", maxUnavailable.ToString()));
        }

        if (daemonSet.Spec?.UpdateStrategy?.RollingUpdate?.MaxSurge is { } maxSurge)
        {
            facts.Add(new KubeActionPreviewFact("Max surge", maxSurge.ToString()));
        }
    }

    private static IReadOnlyList<string> BuildDaemonSetStrategyWarnings(V1DaemonSet daemonSet)
    {
        if (!string.Equals(daemonSet.Spec?.UpdateStrategy?.Type, "OnDelete", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        return
        [
            "This DaemonSet uses OnDelete strategy, so a restart only updates the template. Existing pods still need manual deletion before new pods adopt the change."
        ];
    }

    private static string NormalizeStrategyType(string? strategyType, string defaultType)
    {
        return string.IsNullOrWhiteSpace(strategyType)
            ? $"{defaultType} (default)"
            : strategyType.Trim();
    }

    private static IReadOnlyList<KubeActionPreviewFact> BuildBoundaryFacts(
        IEnumerable<KubeActionPreviewFact> facts,
        string contextName,
        string? resourceNamespace,
        IEnumerable<KubeRelatedResource>? affectedResources = null)
    {
        var result = facts.ToList();
        AppendBoundaryFacts(result, contextName, resourceNamespace, affectedResources);
        return result;
    }

    private static void AppendBoundaryFacts(
        List<KubeActionPreviewFact> facts,
        string contextName,
        string? resourceNamespace,
        IEnumerable<KubeRelatedResource>? affectedResources = null)
    {
        facts.Add(new KubeActionPreviewFact("Cluster context", contextName));
        facts.Add(new KubeActionPreviewFact(
            "Scope boundary",
            string.IsNullOrWhiteSpace(resourceNamespace)
                ? "Cluster-scoped"
                : $"Namespace {resourceNamespace.Trim()}"));
        facts.Add(new KubeActionPreviewFact(
            "Affected namespaces",
            FormatAffectedNamespaces(resourceNamespace, affectedResources)));
    }

    private static IReadOnlyList<string> BuildBoundaryWarnings(
        string? resourceNamespace,
        IEnumerable<KubeRelatedResource>? affectedResources = null)
    {
        var namespaces = CollectAffectedNamespaces(resourceNamespace, affectedResources);
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(resourceNamespace))
        {
            if (namespaces.Count > 1)
            {
                warnings.Add($"This cluster-scoped preview currently spans {namespaces.Count} namespaces on the selected context.");
            }

            if (namespaces.Any(static namespaceName => IsSystemNamespace(namespaceName)))
            {
                warnings.Add("System namespaces are included in the directly modeled impact.");
            }
        }
        else if (namespaces.Count > 1)
        {
            warnings.Add("The directly modeled impact crosses namespace boundaries from the original resource scope.");
        }

        return warnings;
    }

    private static IReadOnlyList<string> CollectAffectedNamespaces(
        string? resourceNamespace,
        IEnumerable<KubeRelatedResource>? affectedResources = null)
    {
        var namespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(resourceNamespace))
        {
            namespaces.Add(resourceNamespace.Trim());
        }

        if (affectedResources is not null)
        {
            foreach (var namespaceName in affectedResources
                         .Select(static resource => resource.Namespace)
                         .Where(static namespaceName => !string.IsNullOrWhiteSpace(namespaceName)))
            {
                namespaces.Add(namespaceName!.Trim());
            }
        }

        return namespaces
            .OrderBy(static namespaceName => namespaceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int CountAffectedNamespaces(
        string? resourceNamespace,
        IEnumerable<KubeRelatedResource>? affectedResources = null)
    {
        return CollectAffectedNamespaces(resourceNamespace, affectedResources).Count;
    }

    private static bool IncludesSystemNamespaces(
        string? resourceNamespace,
        IEnumerable<KubeRelatedResource>? affectedResources = null)
    {
        return CollectAffectedNamespaces(resourceNamespace, affectedResources)
            .Any(static namespaceName => IsSystemNamespace(namespaceName));
    }

    private static bool IsSystemNamespace(string namespaceName)
    {
        return namespaceName.StartsWith("kube-", StringComparison.OrdinalIgnoreCase) ||
               namespaceName.EndsWith("-system", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatAffectedNamespaces(
        string? resourceNamespace,
        IEnumerable<KubeRelatedResource>? affectedResources = null)
    {
        var namespaces = CollectAffectedNamespaces(resourceNamespace, affectedResources);

        return namespaces.Count switch
        {
            0 => "None modeled yet",
            1 => $"1 namespace ({namespaces[0]})",
            _ => $"{namespaces.Count} namespaces ({string.Join(", ", namespaces.Take(3))}{(namespaces.Count > 3 ? ", ..." : string.Empty)})"
        };
    }

    private static string? BuildSelectorSummary(IEnumerable<KeyValuePair<string, string>>? matchLabels)
    {
        if (matchLabels is null)
        {
            return null;
        }

        var selectorPairs = matchLabels
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToArray();

        if (selectorPairs.Length is 0)
        {
            return null;
        }

        return string.Join(
            ", ",
            selectorPairs
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => $"{pair.Key}={pair.Value}"));
    }

}
