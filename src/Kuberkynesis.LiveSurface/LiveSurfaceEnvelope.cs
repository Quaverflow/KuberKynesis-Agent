namespace Kuberkynesis.LiveSurface;

public sealed record LiveSurfaceEnvelope(
    string SchemaVersion,
    string Stream,
    string EventType,
    DateTimeOffset TimestampUtc,
    string? Severity,
    string? Summary,
    string? Namespace,
    string? ResourceKind,
    string? ResourceName,
    string? Component,
    IReadOnlyDictionary<string, string> Tags,
    IReadOnlyDictionary<string, string> Fields,
    string? Category = null);
