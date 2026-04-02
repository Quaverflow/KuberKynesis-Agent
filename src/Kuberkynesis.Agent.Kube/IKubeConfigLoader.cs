namespace Kuberkynesis.Agent.Kube;

public interface IKubeConfigLoader
{
    KubeConfigLoadResult Load();

    k8s.Kubernetes CreateClient(KubeConfigLoadResult loadResult, string contextName);
}
