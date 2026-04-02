using k8s;
using k8s.Models;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

public sealed class KubeCustomResourceDefinitionService(IKubeConfigLoader kubeConfigLoader)
{
    public async Task<KubeCustomResourceDefinitionResponse> GetDefinitionsAsync(
        IReadOnlyList<string> requestedContexts,
        CancellationToken cancellationToken)
    {
        var loadResult = kubeConfigLoader.Load();
        var warnings = loadResult.Warnings.Select(static warning => new KubeQueryWarning(null, warning)).ToList();

        if (loadResult.Contexts.Count is 0)
        {
            return new KubeCustomResourceDefinitionResponse([], [], warnings, []);
        }

        var targetContexts = KubeResourceQueryService.ResolveTargetContexts(requestedContexts, loadResult).ToArray();
        var definitions = new Dictionary<string, KubeCustomResourceType>(StringComparer.Ordinal);

        foreach (var context in targetContexts)
        {
            if (context.Status is not KubeContextStatus.Configured)
            {
                warnings.Add(new KubeQueryWarning(context.Name, context.StatusMessage ?? "The kube context is not currently queryable."));
                continue;
            }

            try
            {
                using var client = kubeConfigLoader.CreateClient(loadResult, context.Name);
                var operations = (IApiextensionsV1Operations)client;
                var list = await k8s.ApiextensionsV1OperationsExtensions.ListCustomResourceDefinitionAsync(
                    operations,
                    cancellationToken: cancellationToken);

                foreach (var definition in list.Items)
                {
                    foreach (var customResourceType in ExpandDefinition(definition))
                    {
                        definitions.TryAdd(customResourceType.DefinitionId, customResourceType);
                    }
                }
            }
            catch (Exception exception)
            {
                warnings.Add(new KubeQueryWarning(context.Name, exception.Message));
            }
        }

        return new KubeCustomResourceDefinitionResponse(
            targetContexts.Select(static context => context.Name).ToArray(),
            definitions.Values
                .OrderBy(static definition => definition.Kind, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static definition => definition.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static definition => definition.Version, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static definition => definition.Plural, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            warnings,
            CreateTransparencyCommands(targetContexts.Select(static context => context.Name).ToArray()));
    }

    private static IReadOnlyList<KubeCustomResourceType> ExpandDefinition(V1CustomResourceDefinition definition)
    {
        var spec = definition.Spec;
        var names = spec?.Names;
        var group = spec?.Group;
        var plural = names?.Plural;
        var kind = names?.Kind;

        if (string.IsNullOrWhiteSpace(group) ||
            string.IsNullOrWhiteSpace(plural) ||
            string.IsNullOrWhiteSpace(kind))
        {
            return [];
        }

        var namespaced = string.Equals(spec?.Scope, "Namespaced", StringComparison.OrdinalIgnoreCase);

        return (spec?.Versions ?? [])
            .Where(static version => version.Served)
            .Select(version => new KubeCustomResourceType(
                Group: group,
                Version: version.Name,
                Kind: kind,
                Plural: plural,
                Namespaced: namespaced,
                Singular: names?.Singular,
                ListKind: names?.ListKind))
            .ToArray();
    }

    private static IReadOnlyList<KubectlCommandPreview> CreateTransparencyCommands(IReadOnlyList<string> contexts)
    {
        return contexts
            .Select(contextName => new KubectlCommandPreview(
                Label: contexts.Count > 1 ? $"CRDs in {contextName}" : "Custom resource definitions",
                Command: $"kubectl --context {contextName} get crd -o wide",
                Notes: "Each served CRD version becomes an available custom-resource explorer type.",
                TransparencyKind: KubectlTransparencyKind.Equivalent,
                TargetSummary: "Custom resource definitions",
                ScopeSummary: $"{contextName} / cluster-scoped"))
            .ToArray();
    }
}
