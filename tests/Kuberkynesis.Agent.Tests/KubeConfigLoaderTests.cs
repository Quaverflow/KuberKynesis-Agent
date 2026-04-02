using Kuberkynesis.Agent.Kube;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Tests;

public sealed class KubeConfigLoaderTests
{
    [Fact]
    public void ResolveKubeConfigPaths_ReturnsExistingConfiguredFiles()
    {
        var tempRoot = Directory.CreateTempSubdirectory("kuberkynesis-kubeconfig");
        var firstConfig = Path.Combine(tempRoot.FullName, "first.yaml");
        var secondConfig = Path.Combine(tempRoot.FullName, "second.yaml");
        File.WriteAllText(firstConfig, "apiVersion: v1");
        File.WriteAllText(secondConfig, "apiVersion: v1");

        var original = Environment.GetEnvironmentVariable("KUBECONFIG");

        try
        {
            Environment.SetEnvironmentVariable(
                "KUBECONFIG",
                string.Join(Path.PathSeparator, [firstConfig, secondConfig, firstConfig, Path.Combine(tempRoot.FullName, "missing.yaml")]));

            var resolved = KubeConfigLoader.ResolveKubeConfigPaths();

            Assert.Collection(
                resolved,
                item => Assert.Equal(firstConfig, item.FullName),
                item => Assert.Equal(secondConfig, item.FullName));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KUBECONFIG", original);
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_UsesConfiguredKubeConfigPath()
    {
        var tempRoot = Directory.CreateTempSubdirectory("kuberkynesis-kubeconfig-load");
        var kubeConfigPath = Path.Combine(tempRoot.FullName, "config.yaml");
        File.WriteAllText(kubeConfigPath, CreateKubeConfig("demo-context", "demo-cluster", "demo-user"));

        var original = Environment.GetEnvironmentVariable("KUBECONFIG");

        try
        {
            Environment.SetEnvironmentVariable("KUBECONFIG", kubeConfigPath);

            var loader = new KubeConfigLoader();
            var result = loader.Load();

            Assert.Equal(kubeConfigPath, Assert.Single(result.SourcePaths).FullName);
            Assert.Empty(result.Warnings);

            var context = Assert.Single(result.Contexts);
            Assert.Equal("demo-context", context.Name);
            Assert.Equal("demo-cluster", context.ClusterName);
            Assert.Equal("demo-user", context.UserName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KUBECONFIG", original);
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_MergesMultipleConfiguredKubeConfigFiles()
    {
        var tempRoot = Directory.CreateTempSubdirectory("kuberkynesis-kubeconfig-merge");
        var firstPath = Path.Combine(tempRoot.FullName, "first.yaml");
        var secondPath = Path.Combine(tempRoot.FullName, "second.yaml");
        File.WriteAllText(firstPath, CreateKubeConfig("alpha-context", "alpha-cluster", "alpha-user"));
        File.WriteAllText(secondPath, CreateKubeConfig("beta-context", "beta-cluster", "beta-user"));

        var original = Environment.GetEnvironmentVariable("KUBECONFIG");

        try
        {
            Environment.SetEnvironmentVariable("KUBECONFIG", string.Join(Path.PathSeparator, [firstPath, secondPath]));

            var loader = new KubeConfigLoader();
            var result = loader.Load();

            Assert.Empty(result.Warnings);
            Assert.Equal(2, result.SourcePaths.Count);
            Assert.Contains(result.Contexts, context => string.Equals(context.Name, "alpha-context", StringComparison.Ordinal));
            Assert.Contains(result.Contexts, context => string.Equals(context.Name, "beta-context", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KUBECONFIG", original);
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void Load_AndCreateClient_SupportsExecAuthUsers()
    {
        var tempRoot = Directory.CreateTempSubdirectory("kuberkynesis-kubeconfig-exec-auth");
        var kubeConfigPath = Path.Combine(tempRoot.FullName, "exec-auth.yaml");
        var execCredentialPath = Path.Combine(tempRoot.FullName, "exec-credential.json");
        File.WriteAllText(execCredentialPath, CreateExecCredentialJson());

        var (execCommand, execArguments) = CreateExecCredentialCommand(execCredentialPath);
        File.WriteAllText(kubeConfigPath, CreateExecAuthKubeConfig("exec-context", "exec-cluster", "exec-user", execCommand, execArguments));

        var original = Environment.GetEnvironmentVariable("KUBECONFIG");

        try
        {
            Environment.SetEnvironmentVariable("KUBECONFIG", kubeConfigPath);

            var loader = new KubeConfigLoader();
            var result = loader.Load();

            Assert.Empty(result.Warnings);

            var context = Assert.Single(result.Contexts);
            Assert.Equal("exec-context", context.Name);
            Assert.Equal(KubeContextStatus.Configured, context.Status);
            Assert.Null(context.StatusMessage);

            using var client = loader.CreateClient(result, "exec-context");
            Assert.NotNull(client);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KUBECONFIG", original);
            tempRoot.Delete(recursive: true);
        }
    }

    private static string CreateKubeConfig(string contextName, string clusterName, string userName)
    {
        return $$"""
            apiVersion: v1
            kind: Config
            clusters:
            - cluster:
                server: https://{{clusterName}}.example.invalid
              name: {{clusterName}}
            contexts:
            - context:
                cluster: {{clusterName}}
                namespace: default
                user: {{userName}}
              name: {{contextName}}
            current-context: {{contextName}}
            users:
            - name: {{userName}}
              user:
                token: fake-token
            """;
    }

    private static string CreateExecAuthKubeConfig(
        string contextName,
        string clusterName,
        string userName,
        string execCommand,
        IReadOnlyList<string> execArguments)
    {
        var argsBlock = string.Join(
            Environment.NewLine,
            execArguments.Select(argument => $"                  - {argument}"));

        return $$"""
            apiVersion: v1
            kind: Config
            clusters:
            - cluster:
                server: https://{{clusterName}}.example.invalid
              name: {{clusterName}}
            contexts:
            - context:
                cluster: {{clusterName}}
                namespace: default
                user: {{userName}}
              name: {{contextName}}
            current-context: {{contextName}}
            users:
            - name: {{userName}}
              user:
                exec:
                  apiVersion: client.authentication.k8s.io/v1beta1
                  command: {{execCommand}}
                  args:
            {{argsBlock}}
            """;
    }

    private static string CreateExecCredentialJson()
    {
        return """
            {
              "apiVersion": "client.authentication.k8s.io/v1beta1",
              "kind": "ExecCredential",
              "status": {
                "token": "fake-token"
              }
            }
            """;
    }

    private static (string Command, IReadOnlyList<string> Arguments) CreateExecCredentialCommand(string execCredentialPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return ("cmd", ["/c", "type", execCredentialPath]);
        }

        return ("/bin/sh", ["-c", $"cat '{execCredentialPath.Replace("'", "'\\''", StringComparison.Ordinal)}'"]);
    }
}
