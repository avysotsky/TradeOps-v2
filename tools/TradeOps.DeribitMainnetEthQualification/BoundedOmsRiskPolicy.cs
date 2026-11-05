using System.Text.Json;

namespace TradeOps.DeribitMainnetEthQualification;

internal static class BoundedOmsRiskPolicy
{
    internal static decimal BuyPrice(JsonElement metadata, JsonElement book, DateTimeOffset now)
    {
        if (metadata.GetProperty("instrument_name").GetString() != "ETH-PERPETUAL" ||
            metadata.GetProperty("kind").GetString() != "future" || metadata.GetProperty("instrument_type").GetString() != "reversed" ||
            metadata.GetProperty("base_currency").GetString() != "ETH" || metadata.GetProperty("quote_currency").GetString() != "USD" ||
            metadata.GetProperty("settlement_currency").GetString() != "ETH" || !metadata.GetProperty("is_active").GetBoolean() ||
            book.GetProperty("instrument_name").GetString() != "ETH-PERPETUAL" || book.GetProperty("state").GetString() != "open" ||
            metadata.GetProperty("tick_size_steps").GetArrayLength() != 0)
            throw new InvalidOperationException("Unsupported bounded instrument metadata.");
        var amountStep = metadata.GetProperty("min_trade_amount").GetDecimal();
        var contract = metadata.GetProperty("contract_size").GetDecimal();
        var tick = metadata.GetProperty("tick_size").GetDecimal();
        var bid = book.GetProperty("best_bid_price").GetDecimal(); var ask = book.GetProperty("best_ask_price").GetDecimal();
        var minimumSell = book.GetProperty("min_price").GetDecimal(); var maximumBuy = book.GetProperty("max_price").GetDecimal();
        var age = now - DateTimeOffset.FromUnixTimeMilliseconds(book.GetProperty("timestamp").GetInt64());
        if (amountStep <= 0 || amountStep > 1 || 1m % amountStep != 0 || contract <= 0 || 1m % contract != 0 ||
            tick <= 0 || bid <= 0 || ask <= bid || minimumSell <= 0 || maximumBuy < ask ||
            age < TimeSpan.FromSeconds(-2) || age > TimeSpan.FromSeconds(5))
            throw new InvalidOperationException("Unsupported amount/quote limits.");
        var price = decimal.Floor(bid * 0.9m / tick) * tick;
        // Venue min_price is a SELL floor, max_price a BUY ceiling; never clamp a buy upwards.
        if (price <= 0 || price > bid * 0.9m || price >= ask || price > maximumBuy)
            throw new InvalidOperationException("No qualified distant passive buy price.");
        return price;
    }
}
