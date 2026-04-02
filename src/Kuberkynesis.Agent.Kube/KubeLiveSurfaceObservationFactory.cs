using Kuberkynesis.LiveSurface;
using Kuberkynesis.Ui.Shared.Kubernetes;

namespace Kuberkynesis.Agent.Kube;

internal static class KubeLiveSurfaceObservationFactory
{
    private const string ObservationSchemaVersion = "kuberkynesis.observation.v1";
    private const string ObservationStream = "kuberkynesis.observations";

    public static IReadOnlyList<LiveSurfaceEnvelope> CreateDerivedEnvelopes(
        KubeResourceSummary resource,
        IReadOnlyList<LiveSurfaceEnvelope> rawEvents,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(rawEvents);

        var derived = new List<LiveSurfaceEnvelope>();
        var statusEnvelope = CreateStatusEnvelope(resource, rawEvents, observedAtUtc);

        if (statusEnvelope is not null)
        {
            derived.Add(statusEnvelope);
        }

        var activityEnvelope = CreateActivityEnvelope(resource, rawEvents, observedAtUtc);

        if (activityEnvelope is not null)
        {
            derived.Add(activityEnvelope);
        }

        var timelineEvents = rawEvents
            .Select(CreateTimelineEvent)
            .Where(static item => item is not null)
            .Cast<KubeResourceTimelineEvent>()
            .ToArray();

        foreach (var cause in KubeResourceCauseInference.InferLikelyCauses(timelineEvents))
        {
            derived.Add(CreateCauseEnvelope(resource, cause));
        }

        return derived;
    }

    private static LiveSurfaceEnvelope? CreateStatusEnvelope(
        KubeResourceSummary resource,
        IReadOnlyList<LiveSurfaceEnvelope> rawEvents,
        DateTimeOffset observedAtUtc)
    {
        var statusText = Normalize(resource.Status);
        var summaryText = Normalize(resource.Summary);
        var readinessText = CreateReadinessText(resource);

        if (statusText is null && summaryText is null && readinessText is null)
        {
            return null;
        }

        var segments = new[]
        {
            statusText,
            summaryText,
            readinessText
        }
        .Where(static segment => !string.IsNullOrWhiteSpace(segment))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

        return new LiveSurfaceEnvelope(
            SchemaVersion: ObservationSchemaVersion,
            Stream: ObservationStream,
            EventType: "Status summary",
            TimestampUtc: ResolveObservationTimestamp(rawEvents, observedAtUtc),
            Severity: ResolveStatusSeverity(resource, statusText, summaryText),
            Summary: string.Join(". ", segments),
            Namespace: resource.Namespace,
            ResourceKind: GetResourceKindLabel(resource),
            ResourceName: resource.Name,
            Component: null,
            Tags: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["resourceKind"] = GetResourceKindLabel(resource),
                ["resourceName"] = resource.Name,
                ["relationship"] = "Selected resource"
            },
            Fields: CreateStatusFields(resource, statusText, summaryText, readinessText),
            Category: "status");
    }

    private static LiveSurfaceEnvelope? CreateActivityEnvelope(
        KubeResourceSummary resource,
        IReadOnlyList<LiveSurfaceEnvelope> rawEvents,
        DateTimeOffset observedAtUtc)
    {
        if (rawEvents.Count is 0)
        {
            return null;
        }

        var latestEnvelope = rawEvents
            .OrderByDescending(static envelope => envelope.TimestampUtc)
            .First();
        var warningCount = rawEvents.Count(static envelope => string.Equals(envelope.Severity, "warning", StringComparison.OrdinalIgnoreCase));
        var relatedCount = rawEvents.Count(static envelope => !string.Equals(GetRelationship(envelope), "Selected resource", StringComparison.OrdinalIgnoreCase));
        var normalCount = rawEvents.Count - warningCount;
        var segments = new List<string>();

        if (warningCount > 0)
        {
            segments.Add(warningCount == 1
                ? "1 warning envelope is active across the current scope."
                : $"{warningCount} warning envelopes are active across the current scope.");
        }

        if (normalCount > 0)
        {
            segments.Add(normalCount == 1
                ? "1 additional non-warning envelope is present."
                : $"{normalCount} additional non-warning envelopes are present.");
        }

        if (relatedCount > 0)
        {
            segments.Add(relatedCount == 1
                ? "1 envelope came from a nearby modeled relation."
                : $"{relatedCount} envelopes came from nearby modeled relations.");
        }

        if (!string.IsNullOrWhiteSpace(latestEnvelope.Summary))
        {
            segments.Add($"Latest: {latestEnvelope.Summary}");
        }

        return new LiveSurfaceEnvelope(
            SchemaVersion: ObservationSchemaVersion,
            Stream: ObservationStream,
            EventType: warningCount > 0 ? "Warning activity" : "Recent activity",
            TimestampUtc: ResolveObservationTimestamp(rawEvents, observedAtUtc),
            Severity: warningCount > 0 ? "warning" : "normal",
            Summary: string.Join(" ", segments),
            Namespace: resource.Namespace,
            ResourceKind: GetResourceKindLabel(resource),
            ResourceName: resource.Name,
            Component: null,
            Tags: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["resourceKind"] = GetResourceKindLabel(resource),
                ["resourceName"] = resource.Name,
                ["relationship"] = "Selected resource"
            },
            Fields: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["relationship"] = "Selected resource",
                ["warningEnvelopeCount"] = warningCount.ToString(),
                ["normalEnvelopeCount"] = normalCount.ToString(),
                ["relatedEnvelopeCount"] = relatedCount.ToString()
            },
            Category: "activity");
    }

    private static LiveSurfaceEnvelope CreateCauseEnvelope(KubeResourceSummary resource, KubeResourceLikelyCause cause)
    {
        var resourceKind = cause.SourceKind?.ToString() ?? GetResourceKindLabel(resource);
        var resourceName = string.IsNullOrWhiteSpace(cause.SourceName)
            ? resource.Name
            : cause.SourceName;

        return new LiveSurfaceEnvelope(
            SchemaVersion: ObservationSchemaVersion,
            Stream: ObservationStream,
            EventType: cause.EventType,
            TimestampUtc: cause.OccurredAtUtc,
            Severity: cause.Severity,
            Summary: cause.Summary,
            Namespace: cause.SourceNamespace ?? resource.Namespace,
            ResourceKind: resourceKind,
            ResourceName: resourceName,
            Component: null,
            Tags: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["resourceKind"] = resourceKind,
                ["resourceName"] = resourceName,
                ["relationship"] = cause.SourceRelationship
            },
            Fields: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["relationship"] = cause.SourceRelationship,
                ["evidenceReason"] = cause.EvidenceReason
            },
            Category: "cause");
    }

    private static KubeResourceTimelineEvent? CreateTimelineEvent(LiveSurfaceEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.ResourceName))
        {
            return null;
        }

        return new KubeResourceTimelineEvent(
            OccurredAtUtc: envelope.TimestampUtc,
            SourceRelationship: GetRelationship(envelope),
            SourceKind: TryParseResourceKind(envelope.ResourceKind),
            SourceName: envelope.ResourceName,
            SourceNamespace: envelope.Namespace,
            Type: envelope.Fields.TryGetValue("type", out var type) && !string.IsNullOrWhiteSpace(type)
                ? type
                : string.Equals(envelope.Severity, "warning", StringComparison.OrdinalIgnoreCase) ? "Warning" : "Normal",
            Reason: envelope.Fields.TryGetValue("reason", out var reason) && !string.IsNullOrWhiteSpace(reason)
                ? reason
                : envelope.EventType,
            Message: envelope.Fields.TryGetValue("message", out var message) && !string.IsNullOrWhiteSpace(message)
                ? message
                : envelope.Summary ?? envelope.EventType,
            Count: envelope.Fields.TryGetValue("count", out var countText) && int.TryParse(countText, out var count)
                ? count
                : 1,
            IsRootResource: string.Equals(GetRelationship(envelope), "Selected resource", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyDictionary<string, string> CreateStatusFields(
        KubeResourceSummary resource,
        string? statusText,
        string? summaryText,
        string? readinessText)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["relationship"] = "Selected resource"
        };

        if (!string.IsNullOrWhiteSpace(statusText))
        {
            fields["status"] = statusText;
        }

        if (!string.IsNullOrWhiteSpace(summaryText))
        {
            fields["resourceSummary"] = summaryText;
        }

        if (!string.IsNullOrWhiteSpace(readinessText))
        {
            fields["readiness"] = readinessText;
        }

        if (resource.ReadyReplicas.HasValue)
        {
            fields["readyReplicas"] = resource.ReadyReplicas.Value.ToString();
        }

        if (resource.DesiredReplicas.HasValue)
        {
            fields["desiredReplicas"] = resource.DesiredReplicas.Value.ToString();
        }

        return fields;
    }

    private static DateTimeOffset ResolveObservationTimestamp(
        IReadOnlyList<LiveSurfaceEnvelope> rawEvents,
        DateTimeOffset observedAtUtc)
    {
        return rawEvents.Count is 0
            ? observedAtUtc
            : rawEvents.Max(static envelope => envelope.TimestampUtc);
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static string? CreateReadinessText(KubeResourceSummary resource)
    {
        if (!resource.ReadyReplicas.HasValue && !resource.DesiredReplicas.HasValue)
        {
            return null;
        }

        var readyReplicas = resource.ReadyReplicas ?? 0;

        return resource.DesiredReplicas.HasValue
            ? $"{readyReplicas}/{resource.DesiredReplicas.Value} ready"
            : $"{readyReplicas} ready";
    }

    private static string ResolveStatusSeverity(
        KubeResourceSummary resource,
        string? statusText,
        string? summaryText)
    {
        if (resource.DesiredReplicas.HasValue &&
            resource.ReadyReplicas.GetValueOrDefault() < resource.DesiredReplicas.Value)
        {
            return "warning";
        }

        var combined = $"{statusText} {summaryText}";

        return ContainsAny(
                combined,
                "pending",
                "failed",
                "error",
                "crash",
                "back-off",
                "unhealthy",
                "unschedulable",
                "unknown",
                "degraded",
                "notready",
                "unavailable")
            ? "warning"
            : "normal";
    }

    private static bool ContainsAny(string? value, params string[] patterns)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return patterns.Any(pattern => value.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetRelationship(LiveSurfaceEnvelope envelope)
    {
        return envelope.Fields.TryGetValue("relationship", out var relationship) && !string.IsNullOrWhiteSpace(relationship)
            ? relationship
            : "Selected resource";
    }

    private static KubeResourceKind? TryParseResourceKind(string? value)
    {
        return Enum.TryParse<KubeResourceKind>(value, ignoreCase: true, out var kind)
            ? kind
            : null;
    }

    private static string GetResourceKindLabel(KubeResourceSummary resource)
    {
        return resource.CustomResourceType?.Kind ?? resource.Kind.ToString();
    }
}
