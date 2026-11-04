namespace TradeOps.Connectors.Deribit;

/// <summary>Valid only with this explicit request; unrequested permissions can inherit API-key rights.</summary>
internal static class DeribitPrivateReadOnlyScope
{
    internal const string Requested = "connection trade:read account:read wallet:none block_trade:none block_rfq:none";

    internal static bool IsValidGrant(string scope)
    {
        if (scope.Length is < 1 or > 2048 || scope.Any(char.IsControl)) return false;
        var tokens = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Distinct(StringComparer.Ordinal).Count() != tokens.Length) return false;
        foreach (var required in new[] { "connection", "trade:read", "account:read" })
            if (tokens.Count(x => x == required) != 1) return false;
        if (tokens.Count(x => x is "mainaccount" or "subaccount") > 1) return false;
        if (tokens.Count(x => x.StartsWith("name:", StringComparison.Ordinal)) > 1) return false;
        // Deribit normalizes denied categories away in live grants. Optional explicit :none is also valid.
        // Only name: is accepted as opaque server metadata, never as account identity or permission.
        return tokens.All(x => x is "connection" or "trade:read" or "account:read" or "mainaccount" or "subaccount" or
            "wallet:none" or "block_trade:none" or "block_rfq:none" ||
            (x.StartsWith("name:", StringComparison.Ordinal) && x.Length is > 5 and <= 133));
    }
}
