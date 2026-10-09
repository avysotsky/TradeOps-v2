namespace TradeOps.Api.Security;

internal static class SignalIngressAuditContext
{
    private const string AuditIdItemKey =
        "TradeOps.SignalIngress.AuditId";

    public static void SetAuditId(HttpContext context, Guid auditId)
    {
        context.Items[AuditIdItemKey] = auditId;
        context.Response.Headers["X-TradeOps-Ingress-Audit-Id"] =
            auditId.ToString("D");
    }

    public static bool TryGetAuditId(
        HttpContext context,
        out Guid auditId)
    {
        if (context.Items.TryGetValue(
                AuditIdItemKey,
                out var value)
            && value is Guid storedAuditId)
        {
            auditId = storedAuditId;
            return true;
        }

        auditId = Guid.Empty;
        return false;
    }
}
