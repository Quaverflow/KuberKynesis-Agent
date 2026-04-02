using Kuberkynesis.Ui.Shared.Connection;

namespace Kuberkynesis.Agent.Core.Security;

public sealed record OriginAccessDecision(bool IsAllowed, OriginAccessClass? AccessClass)
{
    public static OriginAccessDecision Denied() => new(false, null);

    public static OriginAccessDecision Allow(OriginAccessClass accessClass) => new(true, accessClass);
}
