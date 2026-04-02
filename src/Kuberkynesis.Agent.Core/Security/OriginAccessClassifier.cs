using System.Text.RegularExpressions;
using Kuberkynesis.Agent.Core.Configuration;
using Kuberkynesis.Ui.Shared.Connection;

namespace Kuberkynesis.Agent.Core.Security;

public sealed class OriginAccessClassifier
{
    private readonly HashSet<string> interactiveOrigins;
    private readonly Regex? previewRegex;
    private readonly string previewPattern;

    public OriginAccessClassifier(AgentRuntimeOptions options)
    {
        interactiveOrigins = new HashSet<string>(options.Origins.Interactive, StringComparer.OrdinalIgnoreCase);
        previewPattern = options.Origins.PreviewPattern;
        previewRegex = string.IsNullOrWhiteSpace(previewPattern)
            ? null
            : new Regex(previewPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }

    public IReadOnlyList<string> InteractiveOrigins => interactiveOrigins.ToArray();

    public string PreviewPattern => previewPattern;

    public OriginAccessDecision Evaluate(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return OriginAccessDecision.Denied();
        }

        if (interactiveOrigins.Contains(origin))
        {
            return OriginAccessDecision.Allow(OriginAccessClass.Interactive);
        }

        if (previewRegex?.IsMatch(origin) == true)
        {
            return OriginAccessDecision.Allow(OriginAccessClass.ReadonlyPreview);
        }

        return OriginAccessDecision.Denied();
    }
}
