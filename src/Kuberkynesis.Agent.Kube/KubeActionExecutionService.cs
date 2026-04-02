using System.Text.Json;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubeActionExecutionService(
    IKubeConfigLoader kubeConfigLoader,
    KubeActionPreviewService previewService) : IKubeActionExecutionService
{
    public async Task<KubeActionExecuteResponse> ExecuteAsync(KubeActionExecuteRequest request, CancellationToken cancellationToken)
    {
        return await ExecuteAsync(request, reportProgress: null, cancellationToken);
    }

    public async Task<KubeActionExecuteResponse> ExecuteAsync(
        KubeActionExecuteRequest request,
        Action<KubeActionExecutionProgressUpdate>? reportProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ContextName))
        {
            throw new ArgumentException("A kube context name is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("A resource name is required.", nameof(request));
        }

        ReportProgress(
            reportProgress,
            "Validating preview",
            "Rechecking the guarded preview and execution confirmation before submitting the mutation.");

        return request.Action switch
        {
            KubeActionKind.ScaleDeployment when request.Kind is KubeResourceKind.Deployment => await ExecuteScaleDeploymentAsync(request, reportProgress, cancellationToken),
            KubeActionKind.RestartDeploymentRollout when request.Kind is KubeResourceKind.Deployment => await ExecuteRestartDeploymentRolloutAsync(request, reportProgress, cancellationToken),
            KubeActionKind.RollbackDeploymentRollout when request.Kind is KubeResourceKind.Deployment => await ExecuteRollbackDeploymentRolloutAsync(request, reportProgress, cancellationToken),
            KubeActionKind.DeletePod when request.Kind is KubeResourceKind.Pod => await ExecuteDeletePodAsync(request, reportProgress, cancellationToken),
            KubeActionKind.ScaleStatefulSet when request.Kind is KubeResourceKind.StatefulSet => await ExecuteScaleStatefulSetAsync(request, reportProgress, cancellationToken),
            KubeActionKind.RestartDaemonSetRollout when request.Kind is KubeResourceKind.DaemonSet => await ExecuteRestartDaemonSetRolloutAsync(request, reportProgress, cancellationToken),
            KubeActionKind.DeleteJob when request.Kind is KubeResourceKind.Job => await ExecuteDeleteJobAsync(request, reportProgress, cancellationToken),
            KubeActionKind.SuspendCronJob when request.Kind is KubeResourceKind.CronJob => await ExecuteCronJobSuspendAsync(request, suspend: true, reportProgress, cancellationToken),
            KubeActionKind.ResumeCronJob when request.Kind is KubeResourceKind.CronJob => await ExecuteCronJobSuspendAsync(request, suspend: false, reportProgress, cancellationToken),
            KubeActionKind.CordonNode when request.Kind is KubeResourceKind.Node => await ExecuteNodeSchedulingAsync(request, cordon: true, reportProgress, cancellationToken),
            KubeActionKind.UncordonNode when request.Kind is KubeResourceKind.Node => await ExecuteNodeSchedulingAsync(request, cordon: false, reportProgress, cancellationToken),
            _ => throw new ArgumentException("The selected action is not supported for the target resource type in the current slice.", nameof(request))
        };
    }

    private async Task<KubeActionExecuteResponse> ExecuteScaleDeploymentAsync(
        KubeActionExecuteRequest request,
        Action<KubeActionExecutionProgressUpdate>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            throw new ArgumentException("A namespace is required for deployment scale execution.", nameof(request));
        }

        if (request.TargetReplicas is null or < 0 or > 500)
        {
            throw new ArgumentException("Deployment scale execution requires a target replica count between 0 and 500.", nameof(request));
        }

        var (preview, context, client, name, namespaceName) = await PrepareExecutionAsync(request, reportProgress, cancellationToken);
        using (client)
        {
            ReportProgress(reportProgress, "Submitting mutation", $"Submitting the scale request for Deployment/{name}.");
            var deployment = await client.ReadNamespacedDeploymentAsync(name, namespaceName, cancellationToken: cancellationToken);
            var beforeReplicas = deployment.Spec?.Replicas ?? 1;

            var scale = await client.ReadNamespacedDeploymentScaleAsync(name, namespaceName, cancellationToken: cancellationToken);
            scale.Spec ??= new V1ScaleSpec();
            scale.Spec.Replicas = request.TargetReplicas.Value;

            var updatedScale = await client.ReplaceNamespacedDeploymentScaleAsync(
                scale,
                name,
                namespaceName,
                cancellationToken: cancellationToken);

            var appliedReplicas = updatedScale.Spec?.Replicas ?? request.TargetReplicas.Value;
            ReportProgress(reportProgress, "Finalizing result", $"The scale request for Deployment/{name} was accepted.", canCancel: false);

            return new KubeActionExecuteResponse(
                Action: request.Action,
                Resource: new KubeResourceIdentity(
                    ContextName: context.Name,
                    Kind: KubeResourceKind.Deployment,
                    Namespace: namespaceName,
                    Name: name),
                Summary: $"Deployment/{name} scale request submitted: {beforeReplicas} -> {appliedReplicas} desired replicas.",
                CompletedAtUtc: DateTimeOffset.UtcNow,
                Facts:
                [
                    new KubeActionPreviewFact("Previous desired replicas", beforeReplicas.ToString()),
                    new KubeActionPreviewFact("Requested replicas", request.TargetReplicas.Value.ToString()),
                    new KubeActionPreviewFact("Applied desired replicas", appliedReplicas.ToString())
                ],
                Notes:
                [
                    "This mutation updates the deployment scale subresource only.",
                    "Pod replacement and rollout timing still depend on the controller and cluster conditions after submission."
                ],
                TransparencyCommands:
                [
                    CreateExecutedMutationCommand(
                        label: "Executed scale",
                        command: $"kubectl --context {context.Name} -n {namespaceName} scale deployment/{name} --replicas={request.TargetReplicas.Value}",
                        contextName: context.Name,
                        kind: KubeResourceKind.Deployment,
                        namespaceName: namespaceName,
                        resourceName: name,
                        requestSummary: $"replicas={request.TargetReplicas.Value}")
                ])
            {
                TargetResults =
                [
                    CreateSucceededTargetResult(context.Name, KubeResourceKind.Deployment, namespaceName, name)
                ]
            };
        }
    }

    private async Task<KubeActionExecuteResponse> ExecuteRestartDeploymentRolloutAsync(
        KubeActionExecuteRequest request,
        Action<KubeActionExecutionProgressUpdate>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            throw new ArgumentException("A namespace is required for deployment rollout restart execution.", nameof(request));
        }

        var (_, context, client, name, namespaceName) = await PrepareExecutionAsync(request, reportProgress, cancellationToken);
        using (client)
        {
            ReportProgress(reportProgress, "Submitting mutation", $"Submitting the rollout restart for Deployment/{name}.");
            var deployment = await client.ReadNamespacedDeploymentAsync(name, namespaceName, cancellationToken: cancellationToken);
            var desiredReplicas = deployment.Spec?.Replicas ?? 1;
            var strategy = deployment.Spec?.Strategy?.Type ?? "RollingUpdate";
            var restartedAt = DateTimeOffset.UtcNow;
            var patchContent = $"{{\"spec\":{{\"template\":{{\"metadata\":{{\"annotations\":{{\"kubectl.kubernetes.io/restartedAt\":\"{restartedAt:O}\"}}}}}}}}}}";
            var patch = new V1Patch(
                patchContent,
                V1Patch.PatchType.StrategicMergePatch);

            await client.PatchNamespacedDeploymentAsync(
                patch,
                name,
                namespaceName,
                cancellationToken: cancellationToken);
            ReportProgress(reportProgress, "Finalizing result", $"The rollout restart for Deployment/{name} was accepted.", canCancel: false);

            return new KubeActionExecuteResponse(
                Action: request.Action,
                Resource: new KubeResourceIdentity(
                    ContextName: context.Name,
                    Kind: KubeResourceKind.Deployment,
                    Namespace: namespaceName,
                    Name: name),
                Summary: $"Deployment/{name} rollout restart requested for {desiredReplicas} desired replica(s).",
                CompletedAtUtc: restartedAt,
                Facts:
                [
                    new KubeActionPreviewFact("Desired replicas", desiredReplicas.ToString()),
                    new KubeActionPreviewFact("Strategy", strategy),
                    new KubeActionPreviewFact("Restart requested", restartedAt.ToString("u"))
                ],
                Notes:
                [
                    "This mutation patches spec.template.metadata.annotations.kubectl.kubernetes.io/restartedAt.",
                    "The deployment controller will create a fresh ReplicaSet and replace pods over time."
                ],
                TransparencyCommands:
                [
                    CreateExecutedMutationCommand(
                        label: "Executed rollout restart",
                        command: $"kubectl --context {context.Name} -n {namespaceName} rollout restart deployment/{name}",
                        contextName: context.Name,
                        kind: KubeResourceKind.Deployment,
                        namespaceName: namespaceName,
                        resourceName: name,
                        requestSummary: $"spec.template.metadata.annotations.kubectl.kubernetes.io/restartedAt={restartedAt:O}")
                ])
            {
                TargetResults =
                [
                    CreateSucceededTargetResult(context.Name, KubeResourceKind.Deployment, namespaceName, name)
                ]
            };
        }
    }

    private async Task<KubeActionExecuteResponse> ExecuteRollbackDeploymentRolloutAsync(
        KubeActionExecuteRequest request,
        Action<KubeActionExecutionProgressUpdate>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            throw new ArgumentException("A namespace is required for deployment rollout undo execution.", nameof(request));
        }

        var (_, context, client, name, namespaceName) = await PrepareExecutionAsync(request, reportProgress, cancellationToken);
        using (client)
        {
            ReportProgress(reportProgress, "Resolving retained revision", $"Resolving the retained rollout target for Deployment/{name}.");
            var deployment = await client.ReadNamespacedDeploymentAsync(name, namespaceName, cancellationToken: cancellationToken);
            var desiredReplicas = deployment.Spec?.Replicas ?? 1;
            var (rollbackResolution, _) = await KubeDeploymentRollbackPlanner.ResolveAsync(client, deployment, cancellationToken);

            if (!rollbackResolution.CanRollback ||
                rollbackResolution.PreviousReplicaSet?.Spec?.Template is null ||
                !rollbackResolution.PreviousRevision.HasValue)
            {
                throw new InvalidOperationException("No verified retained deployment revision is available for direct rollout undo from the current cluster state.");
            }

            ReportProgress(reportProgress, "Submitting mutation", $"Submitting rollout undo for Deployment/{name} to retained revision {rollbackResolution.PreviousRevision.Value}.");
            var targetTemplateJson = JsonSerializer.Serialize(rollbackResolution.PreviousReplicaSet.Spec.Template);
            var patchContent = $"[{{\"op\":\"replace\",\"path\":\"/spec/template\",\"value\":{targetTemplateJson}}}]";
            var patch = new V1Patch(patchContent, V1Patch.PatchType.JsonPatch);

            await client.PatchNamespacedDeploymentAsync(
                patch,
                name,
                namespaceName,
                cancellationToken: cancellationToken);

            var completedAt = DateTimeOffset.UtcNow;
            var targetReplicaSetName = rollbackResolution.PreviousReplicaSet.Metadata?.Name ?? "unknown";
            ReportProgress(reportProgress, "Finalizing result", $"Rollout undo for Deployment/{name} to revision {rollbackResolution.PreviousRevision.Value} was accepted.", canCancel: false);

            return new KubeActionExecuteResponse(
                Action: request.Action,
                Resource: new KubeResourceIdentity(
                    ContextName: context.Name,
                    Kind: KubeResourceKind.Deployment,
                    Namespace: namespaceName,
                    Name: name),
                Summary: $"Deployment/{name} rollout undo requested to retained revision {rollbackResolution.PreviousRevision.Value}.",
                CompletedAtUtc: completedAt,
                Facts:
                [
                    new KubeActionPreviewFact("Desired replicas", desiredReplicas.ToString()),
                    new KubeActionPreviewFact("Current revision", rollbackResolution.CurrentRevision?.ToString() ?? "Unknown"),
                    new KubeActionPreviewFact("Rollback target revision", rollbackResolution.PreviousRevision.Value.ToString()),
                    new KubeActionPreviewFact("Rollback target", $"ReplicaSet/{targetReplicaSetName}")
                ],
                Notes:
                [
                    "This mutation patches spec.template to the retained ReplicaSet pod template that was selected during preview.",
                    "The deployment controller can scale the older ReplicaSet back up over time instead of creating a fresh restarted revision."
                ],
                TransparencyCommands:
                [
                    CreateExecutedMutationCommand(
                        label: "Executed rollout undo",
                        command: $"kubectl --context {context.Name} -n {namespaceName} rollout undo deployment/{name} --to-revision={rollbackResolution.PreviousRevision.Value}",
                        contextName: context.Name,
                        kind: KubeResourceKind.Deployment,
                        namespaceName: namespaceName,
                        resourceName: name,
                        requestSummary: $"rollbackToRevision={rollbackResolution.PreviousRevision.Value}")
                ])
            {
                TargetResults =
                [
                    CreateSucceededTargetResult(context.Name, KubeResourceKind.Deployment, namespaceName, name)
                ]
            };
        }
    }

    private async Task<KubeActionExecuteResponse> ExecuteDeletePodAsync(
        KubeActionExecuteRequest request,
        Action<KubeActionExecutionProgressUpdate>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            throw new ArgumentException("A namespace is required for pod delete execution.", nameof(request));
        }

        var (preview, context, client, name, namespaceName) = await PrepareExecutionAsync(request, reportProgress, cancellationToken);
        using (client)
        {
            ReportProgress(reportProgress, "Submitting mutation", $"Submitting the delete request for Pod/{name}.");
            var pod = await client.ReadNamespacedPodAsync(name, namespaceName, cancellationToken: cancellationToken);
            var phase = pod.Status?.Phase ?? "Unknown";
            var nodeName = pod.Spec?.NodeName ?? "Unknown";
            var restartCount = pod.Status?.ContainerStatuses?.Sum(static status => status.RestartCount) ?? 0;

            await client.DeleteNamespacedPodAsync(
                name,
                namespaceName,
                cancellationToken: cancellationToken);
            ReportProgress(reportProgress, "Finalizing result", $"The delete request for Pod/{name} was accepted.", canCancel: false);

            return new KubeActionExecuteResponse(
                Action: request.Action,
                Resource: new KubeResourceIdentity(
                    ContextName: context.Name,
                    Kind: KubeResourceKind.Pod,
                    Namespace: namespaceName,
                    Name: name),
                Summary: $"Pod/{name} delete request submitted.",
                CompletedAtUtc: DateTimeOffset.UtcNow,
                Facts:
                [
                    new KubeActionPreviewFact("Phase before delete", phase),
                    new KubeActionPreviewFact("Node", nodeName),
                    new KubeActionPreviewFact("Restarts", restartCount.ToString())
                ],
                Notes:
                [
                    preview.Notes.FirstOrDefault() ?? "Deleting one pod acts on a single live instance, not the whole workload definition.",
                    "Controller replacement timing still depends on the owning workload and cluster conditions after submission."
                ],
                TransparencyCommands:
                [
                    CreateExecutedMutationCommand(
                        label: "Executed pod delete",
                        command: $"kubectl --context {context.Name} -n {namespaceName} delete pod/{name}",
                        contextName: context.Name,
                        kind: KubeResourceKind.Pod,
                        namespaceName: namespaceName,
                        resourceName: name)
                ])
            {
                TargetResults =
                [
                    CreateSucceededTargetResult(context.Name, KubeResourceKind.Pod, namespaceName, name)
                ]
            };
        }
    }

    private async Task<KubeActionExecuteResponse> ExecuteScaleStatefulSetAsync(
        KubeActionExecuteRequest request,
        Action<KubeActionExecutionProgressUpdate>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            throw new ArgumentException("A namespace is required for statefulset scale execution.", nameof(request));
        }

        if (request.TargetReplicas is null or < 0 or > 500)
        {
            throw new ArgumentException("StatefulSet scale execution requires a target replica count between 0 and 500.", nameof(request));
        }

        var (_, context, client, name, namespaceName) = await PrepareExecutionAsync(request, reportProgress, cancellationToken);
        using (client)
        {
            ReportProgress(reportProgress, "Submitting mutation", $"Submitting the scale request for StatefulSet/{name}.");
            var statefulSet = await client.ReadNamespacedStatefulSetAsync(name, namespaceName, cancellationToken: cancellationToken);
            var beforeReplicas = statefulSet.Spec?.Replicas ?? 1;

            var scale = await client.ReadNamespacedStatefulSetScaleAsync(name, namespaceName, cancellationToken: cancellationToken);
            scale.Spec ??= new V1ScaleSpec();
            scale.Spec.Replicas = request.TargetReplicas.Value;

            var updatedScale = await client.ReplaceNamespacedStatefulSetScaleAsync(
                scale,
                name,
                namespaceName,
                cancellationToken: cancellationToken);

            var appliedReplicas = updatedScale.Spec?.Replicas ?? request.TargetReplicas.Value;
            ReportProgress(reportProgress, "Finalizing result", $"The scale request for StatefulSet/{name} was accepted.", canCancel: false);

            return new KubeActionExecuteResponse(
                Action: request.Action,
                Resource: new KubeResourceIdentity(context.Name, KubeResourceKind.StatefulSet, namespaceName, name),
                Summary: $"StatefulSet/{name} scale request submitted: {beforeReplicas} -> {appliedReplicas} desired replicas.",
                CompletedAtUtc: DateTimeOffset.UtcNow,
                Facts:
                [
                    new KubeActionPreviewFact("Previous desired replicas", beforeReplicas.ToString()),
                    new KubeActionPreviewFact("Requested replicas", request.TargetReplicas.Value.ToString()),
                    new KubeActionPreviewFact("Applied desired replicas", appliedReplicas.ToString())
                ],
                Notes:
                [
                    "This mutation updates the statefulset scale subresource only.",
                    "Stable pod ordinals and persistent storage behavior still depend on the controller and cluster after submission."
                ],
                TransparencyCommands:
                [
                    CreateExecutedMutationCommand(
                        label: "Executed scale",
                        command: $"kubectl --context {context.Name} -n {namespaceName} scale statefulset/{name} --replicas={request.TargetReplicas.Value}",
                        contextName: context.Name,
                        kind: KubeResourceKind.StatefulSet,
                        namespaceName: namespaceName,
                        resourceName: name,
                        requestSummary: $"replicas={request.TargetReplicas.Value}")
                ])
            {
                TargetResults =
                [
                    CreateSucceededTargetResult(context.Name, KubeResourceKind.StatefulSet, namespaceName, name)
                ]
            };
        }
    }

    private async Task<KubeActionExecuteResponse> ExecuteRestartDaemonSetRolloutAsync(
        KubeActionExecuteRequest request,
        Action<KubeActionExecutionProgressUpdate>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            throw new ArgumentException("A namespace is required for daemonset rollout restart execution.", nameof(request));
        }

        var (_, context, client, name, namespaceName) = await PrepareExecutionAsync(request, reportProgress, cancellationToken);
        using (client)
        {
            ReportProgress(reportProgress, "Submitting mutation", $"Submitting the rollout restart for DaemonSet/{name}.");
            var daemonSet = await client.ReadNamespacedDaemonSetAsync(name, namespaceName, cancellationToken: cancellationToken);
            var desiredScheduled = daemonSet.Status?.DesiredNumberScheduled ?? 0;
            var restartedAt = DateTimeOffset.UtcNow;
            var patchContent = $"{{\"spec\":{{\"template\":{{\"metadata\":{{\"annotations\":{{\"kubectl.kubernetes.io/restartedAt\":\"{restartedAt:O}\"}}}}}}}}}}";
            var patch = new V1Patch(patchContent, V1Patch.PatchType.StrategicMergePatch);

            await client.PatchNamespacedDaemonSetAsync(
                patch,
                name,
                namespaceName,
                cancellationToken: cancellationToken);
            ReportProgress(reportProgress, "Finalizing result", $"The rollout restart for DaemonSet/{name} was accepted.", canCancel: false);

            return new KubeActionExecuteResponse(
                Action: request.Action,
                Resource: new KubeResourceIdentity(context.Name, KubeResourceKind.DaemonSet, namespaceName, name),
                Summary: $"DaemonSet/{name} rollout restart requested across {desiredScheduled} scheduled pod(s).",
                CompletedAtUtc: restartedAt,
                Facts:
                [
                    new KubeActionPreviewFact("Desired scheduled", desiredScheduled.ToString()),
                    new KubeActionPreviewFact("Restart requested", restartedAt.ToString("u"))
                ],
                Notes:
                [
                    "This mutation patches spec.template.metadata.annotations.kubectl.kubernetes.io/restartedAt.",
                    "The daemonset controller will rotate pods over time across eligible nodes."
                ],
                TransparencyCommands:
                [
                    CreateExecutedMutationCommand(
                        label: "Executed rollout restart",
                        command: $"kubectl --context {context.Name} -n {namespaceName} rollout restart daemonset/{name}",
                        contextName: context.Name,
                        kind: KubeResourceKind.DaemonSet,
                        namespaceName: namespaceName,
                        resourceName: name,
                        requestSummary: $"spec.template.metadata.annotations.kubectl.kubernetes.io/restartedAt={restartedAt:O}")
                ])
            {
                TargetResults =
                [
                    CreateSucceededTargetResult(context.Name, KubeResourceKind.DaemonSet, namespaceName, name)
                ]
            };
        }
    }

    private async Task<KubeActionExecuteResponse> ExecuteDeleteJobAsync(
        KubeActionExecuteRequest request,
        Action<KubeActionExecutionProgressUpdate>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            throw new ArgumentException("A namespace is required for job delete execution.", nameof(request));
        }

        var (_, context, client, name, namespaceName) = await PrepareExecutionAsync(request, reportProgress, cancellationToken);
        using (client)
        {
            ReportProgress(reportProgress, "Submitting mutation", $"Submitting the delete request for Job/{name}.");
            var job = await client.ReadNamespacedJobAsync(name, namespaceName, cancellationToken: cancellationToken);
            var active = job.Status?.Active ?? 0;

            await client.DeleteNamespacedJobAsync(name, namespaceName, cancellationToken: cancellationToken);
            ReportProgress(reportProgress, "Finalizing result", $"The delete request for Job/{name} was accepted.", canCancel: false);

            return new KubeActionExecuteResponse(
                Action: request.Action,
                Resource: new KubeResourceIdentity(context.Name, KubeResourceKind.Job, namespaceName, name),
                Summary: $"Job/{name} delete request submitted.",
                CompletedAtUtc: DateTimeOffset.UtcNow,
                Facts:
                [
                    new KubeActionPreviewFact("Active pods before delete", active.ToString())
                ],
                Notes:
                [
                    "This mutation removes the job resource from the namespace.",
                    "Any follow-on recreation still depends on external automation after submission."
                ],
                TransparencyCommands:
                [
                    CreateExecutedMutationCommand(
                        label: "Executed job delete",
                        command: $"kubectl --context {context.Name} -n {namespaceName} delete job/{name}",
                        contextName: context.Name,
                        kind: KubeResourceKind.Job,
                        namespaceName: namespaceName,
                        resourceName: name)
                ])
            {
                TargetResults =
                [
                    CreateSucceededTargetResult(context.Name, KubeResourceKind.Job, namespaceName, name)
                ]
            };
        }
    }

    private async Task<KubeActionExecuteResponse> ExecuteCronJobSuspendAsync(
        KubeActionExecuteRequest request,
        bool suspend,
        Action<KubeActionExecutionProgressUpdate>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Namespace))
        {
            throw new ArgumentException("A namespace is required for CronJob execution.", nameof(request));
        }

        var (_, context, client, name, namespaceName) = await PrepareExecutionAsync(request, reportProgress, cancellationToken);
        using (client)
        {
            ReportProgress(
                reportProgress,
                "Submitting mutation",
                $"{(suspend ? "Suspending" : "Resuming")} CronJob/{name} through the local agent.");
            var patch = new V1Patch(
                $"{{\"spec\":{{\"suspend\":{suspend.ToString().ToLowerInvariant()}}}}}",
                V1Patch.PatchType.MergePatch);

            await client.PatchNamespacedCronJobAsync(
                patch,
                name,
                namespaceName,
                cancellationToken: cancellationToken);
            ReportProgress(reportProgress, "Finalizing result", $"{(suspend ? "Suspend" : "Resume")} was accepted for CronJob/{name}.", canCancel: false);

            return new KubeActionExecuteResponse(
                Action: request.Action,
                Resource: new KubeResourceIdentity(context.Name, KubeResourceKind.CronJob, namespaceName, name),
                Summary: suspend
                    ? $"CronJob/{name} suspend request submitted."
                    : $"CronJob/{name} resume request submitted.",
                CompletedAtUtc: DateTimeOffset.UtcNow,
                Facts:
                [
                    new KubeActionPreviewFact("Requested state", suspend ? "Suspended" : "Active")
                ],
                Notes:
                [
                    "This mutation patches spec.suspend on the CronJob.",
                    "Existing jobs remain separate resources after submission."
                ],
                TransparencyCommands:
                [
                    CreateExecutedMutationCommand(
                        label: suspend ? "Executed suspend" : "Executed resume",
                        command: $"kubectl --context {context.Name} -n {namespaceName} patch cronjob/{name} --type merge -p '{{\"spec\":{{\"suspend\":{suspend.ToString().ToLowerInvariant()}}}}}'",
                        contextName: context.Name,
                        kind: KubeResourceKind.CronJob,
                        namespaceName: namespaceName,
                        resourceName: name,
                        requestSummary: $"spec.suspend={suspend.ToString().ToLowerInvariant()}")
                ])
            {
                TargetResults =
                [
                    CreateSucceededTargetResult(context.Name, KubeResourceKind.CronJob, namespaceName, name)
                ]
            };
        }
    }

    private async Task<KubeActionExecuteResponse> ExecuteNodeSchedulingAsync(
        KubeActionExecuteRequest request,
        bool cordon,
        Action<KubeActionExecutionProgressUpdate>? reportProgress,
        CancellationToken cancellationToken)
    {
        var (_, context, client, name, _) = await PrepareExecutionAsync(request, reportProgress, cancellationToken);
        using (client)
        {
            ReportProgress(
                reportProgress,
                "Submitting mutation",
                $"{(cordon ? "Cordoning" : "Uncordoning")} Node/{name} through the local agent.");
            var patch = new V1Patch(
                $"{{\"spec\":{{\"unschedulable\":{cordon.ToString().ToLowerInvariant()}}}}}",
                V1Patch.PatchType.MergePatch);

            await client.PatchNodeAsync(
                patch,
                name,
                cancellationToken: cancellationToken);
            ReportProgress(reportProgress, "Finalizing result", $"{(cordon ? "Cordon" : "Uncordon")} was accepted for Node/{name}.", canCancel: false);

            return new KubeActionExecuteResponse(
                Action: request.Action,
                Resource: new KubeResourceIdentity(context.Name, KubeResourceKind.Node, null, name),
                Summary: cordon
                    ? $"Node/{name} cordon request submitted."
                    : $"Node/{name} uncordon request submitted.",
                CompletedAtUtc: DateTimeOffset.UtcNow,
                Facts:
                [
                    new KubeActionPreviewFact("Requested schedulability", cordon ? "Unschedulable" : "Schedulable")
                ],
                Notes:
                [
                    "This mutation changes future scheduling only.",
                    "Pods already on the node remain until some other controller or operator moves them."
                ],
                TransparencyCommands:
                [
                    CreateExecutedMutationCommand(
                        label: cordon ? "Executed cordon" : "Executed uncordon",
                        command: cordon
                            ? $"kubectl --context {context.Name} cordon {name}"
                            : $"kubectl --context {context.Name} uncordon {name}",
                        contextName: context.Name,
                        kind: KubeResourceKind.Node,
                        namespaceName: null,
                        resourceName: name,
                        requestSummary: cordon ? "spec.unschedulable=true" : "spec.unschedulable=false")
                ])
            {
                TargetResults =
                [
                    CreateSucceededTargetResult(context.Name, KubeResourceKind.Node, null, name)
                ]
            };
        }
    }

    private async Task<(KubeActionPreviewResponse Preview, DiscoveredKubeContext Context, Kubernetes Client, string Name, string? NamespaceName)> PrepareExecutionAsync(
        KubeActionExecuteRequest request,
        Action<KubeActionExecutionProgressUpdate>? reportProgress,
        CancellationToken cancellationToken)
    {
        var preview = await previewService.GetPreviewAsync(
            new KubeActionPreviewRequest(
                ContextName: request.ContextName,
                Kind: request.Kind,
                Namespace: request.Namespace,
                Name: request.Name,
                Action: request.Action,
                TargetReplicas: request.TargetReplicas,
                GuardrailProfile: request.GuardrailProfile,
                LocalEnvironmentRules: request.LocalEnvironmentRules),
            cancellationToken);

        if (preview.Guardrails.IsExecutionBlocked)
        {
            throw new InvalidOperationException("This action is currently blocked by the active guardrails. Choose a safer alternative or use a stronger review path first.");
        }

        var requiredTypedConfirmation = GetRequiredTypedConfirmationText(preview);

        if (!string.IsNullOrWhiteSpace(requiredTypedConfirmation) &&
            !string.Equals(requiredTypedConfirmation, request.ConfirmationText?.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException($"Typed confirmation is required. Type '{requiredTypedConfirmation}' to continue.", nameof(request));
        }

        ReportProgress(
            reportProgress,
            "Preparing client",
            $"Resolving kube context {request.ContextName.Trim()} and preparing the cluster client.");

        var loadResult = kubeConfigLoader.Load();
        var context = KubeResourceQueryService.ResolveTargetContexts([request.ContextName], loadResult).Single();

        if (context.Status is KubeContextStatus.ConfigurationError)
        {
            throw new ArgumentException(context.StatusMessage ?? $"The kube context '{context.Name}' is invalid.", nameof(request));
        }

        return (
            Preview: preview,
            Context: context,
            Client: kubeConfigLoader.CreateClient(loadResult, context.Name),
            Name: request.Name.Trim(),
            NamespaceName: string.IsNullOrWhiteSpace(request.Namespace) ? null : request.Namespace.Trim());
    }

    private static KubectlCommandPreview CreateExecutedMutationCommand(
        string label,
        string command,
        string contextName,
        KubeResourceKind kind,
        string? namespaceName,
        string resourceName,
        string? requestSummary = null)
    {
        return new KubectlCommandPreview(
            Label: label,
            Command: command,
            Notes: "This is the executed mutation path for the current slice.",
            TransparencyKind: KubectlTransparencyKind.Equivalent,
            TargetSummary: $"{kind}/{resourceName}",
            ScopeSummary: string.IsNullOrWhiteSpace(namespaceName)
                ? $"{contextName} / cluster-scoped"
                : $"{contextName} / {namespaceName}",
            RequestSummary: requestSummary);
    }

    private static string? GetRequiredTypedConfirmationText(KubeActionPreviewResponse preview)
    {
        if (preview.Guardrails.ConfirmationLevel is not (
            KubeActionConfirmationLevel.TypedConfirmation or
            KubeActionConfirmationLevel.TypedConfirmationWithScope))
        {
            return null;
        }

        return $"{GetKubectlResourceType(preview.Resource.Kind)}/{preview.Resource.Name}";
    }

    private static string GetKubectlResourceType(KubeResourceKind? kind)
    {
        return kind switch
        {
            KubeResourceKind.Deployment => "deployment",
            KubeResourceKind.StatefulSet => "statefulset",
            KubeResourceKind.DaemonSet => "daemonset",
            KubeResourceKind.Pod => "pod",
            KubeResourceKind.Job => "job",
            KubeResourceKind.CronJob => "cronjob",
            KubeResourceKind.Node => "node",
            null => "resource",
            _ => kind.Value.ToString().ToLowerInvariant()
        };
    }

    private static KubeActionExecutionTargetResult CreateSucceededTargetResult(
        string contextName,
        KubeResourceKind kind,
        string? namespaceName,
        string name)
    {
        return new KubeActionExecutionTargetResult(
            new KubeResourceIdentity(contextName, kind, namespaceName, name),
            KubeActionExecutionStatus.Succeeded,
            null);
    }

    private static void ReportProgress(
        Action<KubeActionExecutionProgressUpdate>? reportProgress,
        string statusText,
        string summary,
        bool canCancel = true)
    {
        reportProgress?.Invoke(new KubeActionExecutionProgressUpdate(statusText, summary)
        {
            CanCancel = canCancel
        });
    }
}
