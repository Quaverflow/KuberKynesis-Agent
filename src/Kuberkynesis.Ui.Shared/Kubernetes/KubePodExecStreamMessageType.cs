namespace Kuberkynesis.Ui.Shared.Kubernetes;

public enum KubePodExecStreamMessageType
{
    Snapshot,
    Output,
    Completed,
    Cancelled,
    Error
}
