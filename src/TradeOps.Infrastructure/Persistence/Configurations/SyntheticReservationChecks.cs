namespace TradeOps.Infrastructure.Persistence.Configurations;

/// <summary>
/// Fixed SQL fragments used by TO-V2-21's model and its initial migration.
/// Treat the values as an immutable historical migration contract.
/// </summary>
internal static class SyntheticReservationChecks
{
    internal const string OrderScope =
        "(\"ProviderKey\" = 'Mock' " +
        "AND left(\"InstrumentKey\", 5) = 'MOCK:' AND length(\"InstrumentKey\") > 5) " +
        "OR (\"ProviderKey\" = 'IbkrOffline' " +
        "AND left(\"InstrumentKey\", 22) = 'SYNTHETIC-IBKR-SYMBOL:' " +
        "AND length(\"InstrumentKey\") > 22)";

    internal const string OrderText =
        "length(btrim(\"AccountKey\")) > 0 AND \"AccountKey\" = btrim(\"AccountKey\") " +
        "AND length(btrim(\"ClientOrderId\")) > 0 AND \"ClientOrderId\" = btrim(\"ClientOrderId\") " +
        "AND length(btrim(\"BrokerOrderId\")) > 0 AND \"BrokerOrderId\" = btrim(\"BrokerOrderId\") " +
        "AND length(btrim(\"InstrumentKey\")) > 0 AND \"InstrumentKey\" = btrim(\"InstrumentKey\") " +
        "AND \"LocalOrderId\" <> '00000000-0000-0000-0000-000000000000'::uuid";

    internal const string OrderTerms =
        "\"Side\" IN ('Buy','Sell') " +
        "AND \"OrderType\" IN ('Market','Limit','Stop') " +
        "AND \"RequestedQuantity\" > 0 AND \"RequestedQuantity\" < 10000000000000000 " +
        "AND (\"Price\" IS NULL OR (\"Price\" > 0 AND \"Price\" < 10000000000000000))";

    internal const string ExecutionScope =
        "\"ProviderKey\" IN ('Mock','IbkrOffline') " +
        "AND length(btrim(\"AccountKey\")) > 0 " +
        "AND \"AccountKey\" = btrim(\"AccountKey\") " +
        "AND length(btrim(\"ClientOrderId\")) > 0 " +
        "AND \"ClientOrderId\" = btrim(\"ClientOrderId\") " +
        "AND length(btrim(\"ExecutionId\")) > 0 " +
        "AND \"ExecutionId\" = btrim(\"ExecutionId\") " +
        "AND length(btrim(\"BrokerOrderId\")) > 0 " +
        "AND \"BrokerOrderId\" = btrim(\"BrokerOrderId\") " +
        "AND \"LocalOrderId\" <> '00000000-0000-0000-0000-000000000000'::uuid";

    internal const string ExecutionTerms =
        "\"Quantity\" > 0 AND \"Quantity\" < 10000000000000000 " +
        "AND \"Price\" > 0 AND \"Price\" < 10000000000000000 " +
        "AND (\"Fee\" IS NULL OR (\"Fee\" >= 0 AND \"Fee\" < 10000000000000000))";
}
