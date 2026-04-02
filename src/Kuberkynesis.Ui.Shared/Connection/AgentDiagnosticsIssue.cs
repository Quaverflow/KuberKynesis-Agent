namespace Kuberkynesis.Ui.Shared.Connection;

public sealed record AgentDiagnosticsIssue(
    AgentDiagnosticsIssueKind Kind,
    string Summary,
    IReadOnlyList<string> AffectedContexts);
