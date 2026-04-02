namespace Kuberkynesis.Ui.Shared.Kubernetes;

public enum KubeActionKind
{
    ScaleDeployment,
    RestartDeploymentRollout,
    RollbackDeploymentRollout,
    DeletePod,
    ScaleStatefulSet,
    RestartDaemonSetRollout,
    DeleteJob,
    SuspendCronJob,
    ResumeCronJob,
    CordonNode,
    UncordonNode,
    DrainNode,
    ExecPodShell
}
