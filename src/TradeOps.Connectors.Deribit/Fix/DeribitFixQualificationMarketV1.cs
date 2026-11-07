using TradeOps.Application.Oms;
namespace TradeOps.Connectors.Deribit.Fix;

public sealed class DeribitFixQualificationMarketV1(DeribitPrivateCaptureBinding binding,HttpClient http):IDeribitFixQualificationMarketV1
{
    public async Task<DeribitFixQualificationQuoteV1> ReadAsync(CancellationToken ct)
    {
        var rpc=new DeribitRecoveryRpc(http,binding.Scope.Environment);
        var metadata=await rpc.Call("public/get_instrument",new{instrument_name="ETH-PERPETUAL"},ct);
        var book=await rpc.Call("public/get_order_book",new{instrument_name="ETH-PERPETUAL",depth=1},ct);
        if(metadata.GetProperty("instrument_name").GetString()!="ETH-PERPETUAL"||metadata.GetProperty("kind").GetString()!="future"||
            metadata.GetProperty("instrument_type").GetString()!="reversed"||metadata.GetProperty("base_currency").GetString()!="ETH"||
            metadata.GetProperty("settlement_currency").GetString()!="ETH"||metadata.GetProperty("quote_currency").GetString()!="USD"||
            !metadata.GetProperty("is_active").GetBoolean()||metadata.GetProperty("tick_size_steps").GetArrayLength()!=0||
            book.GetProperty("instrument_name").GetString()!="ETH-PERPETUAL"||book.GetProperty("state").GetString()!="open")
            throw new InvalidOperationException("Unsupported qualification market profile.");
        return new(binding.Scope,"ETH-PERPETUAL",DateTimeOffset.UtcNow,DateTimeOffset.FromUnixTimeMilliseconds(book.GetProperty("timestamp").GetInt64()),
            metadata.GetProperty("contract_size").GetDecimal(),metadata.GetProperty("min_trade_amount").GetDecimal(),metadata.GetProperty("tick_size").GetDecimal(),
            book.GetProperty("best_bid_price").GetDecimal(),book.GetProperty("best_ask_price").GetDecimal(),book.GetProperty("min_price").GetDecimal(),book.GetProperty("max_price").GetDecimal());
    }
}
