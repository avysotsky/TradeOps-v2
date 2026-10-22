using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Ibkr;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class IbkrOfflineCancelReceiptParserTests
{
    private const string Coid = "offline-cancel-coid";
    private const string BrokerId = "123456789";
    private const string AccountKey = "synthetic-paper-acct";

    [Theory]
    [InlineData("""{"msg":"Request was submitted","order_id":123456789,"account":"synthetic-paper-acct","conid":265598}""", true)]
    [InlineData("""{"msg":"Request was submitted","order_id":"123456789","account":"synthetic-paper-acct"}""", true)]
    [InlineData("""{"msg":"Request was submitted","order_id":123456789,"account":null,"conid":-1}""", false)]
    [InlineData("""{"msg":"Request was submitted","order_id":123456789}""", false)]
    public void RequestAcceptedReceipt_DoesNotMeanCancelled(string json, bool accountVerified)
    {
        var receipt = IbkrOfflineCancelReceiptParser.Parse(json, BrokerId, AccountKey);
        Assert.Equal(BrokerId, receipt.BrokerOrderId);
        Assert.Equal(accountVerified, receipt.AccountVerified);

        var lifecycle = PendingCancel();
        var replay = new IbkrOfflineCancelReceiptReplay(lifecycle);
        var snapshot = replay.Observe(Coid, json);
        Assert.Equal(IbkrOfflineOrderState.CancelPending, snapshot.State);
        Assert.Equal(BrokerId, snapshot.BrokerOrderId);
        Assert.Equal(0m, snapshot.CumulativeFilled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"msg":"Request was submitted","order_id":123456788,"account":"synthetic-paper-acct"}""")]
    [InlineData("""{"msg":"Cancelled","order_id":123456789,"account":"synthetic-paper-acct"}""")]
    [InlineData("""{"msg":"Request was submitted","order_id":123456789,"account":"wrong-acct"}""")]
    [InlineData("""{"msg":"Request was submitted","order_id":123456789,"account":42}""")]
    [InlineData("""{"msg":"Request was submitted","order_id":null,"account":"synthetic-paper-acct"}""")]
    [InlineData("""{"msg":"Request was submitted","order_id":true,"account":"synthetic-paper-acct"}""")]
    [InlineData("""{"msg":"Request was submitted","order_id":1.5,"account":"synthetic-paper-acct"}""")]
    [InlineData("""{"msg":"Request was submitted","order_id":123456789,"error":"cancel failed"}""")]
    [InlineData("""{"msg":"Request was submitted","msg":"Cancelled","order_id":123456789}""")]
    [InlineData("""{"msg":"Request was submitted","order_id":123456789,"order_id":123456788}""")]
    public void RejectsUntrustedOrAmbiguousCancellationReceipt(string json)
    {
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineCancelReceiptParser.Parse(json, BrokerId, AccountKey));
    }

    [Fact]
    public void ParserFailure_DoesNotAdvanceCancelState()
    {
        var model = PendingCancel();
        var replay = new IbkrOfflineCancelReceiptReplay(model);
        Assert.Throws<InvalidOperationException>(() =>
            replay.Observe(Coid,
                """{"msg":"Request was submitted","order_id":999999999,"account":"synthetic-paper-acct"}"""));
        Assert.Equal(IbkrOfflineOrderState.CancelPending, model.Get(Coid).State);
    }

    [Fact]
    public void CancelReceiptBeforeRequest_IsNotAccepted()
    {
        var model = new IbkrOfflineOrderLifecycle();
        model.Register(new IbkrOfflineOrderIntent(
            Coid, AccountKey, "AAPL", OrderSide.Buy, 5m));
        model.Apply(new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.PreflightApproved));
        model.Apply(new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.SubmissionStarted));
        model.Apply(new IbkrOfflineOrderEvent(Coid,
            IbkrOfflineOrderEventType.BrokerAcknowledged, BrokerOrderId: BrokerId));
        var replay = new IbkrOfflineCancelReceiptReplay(model);
        Assert.Throws<InvalidOperationException>(() =>
            replay.Observe(Coid,
                """{"msg":"Request was submitted","order_id":123456789,"account":"synthetic-paper-acct"}"""));
        Assert.Equal(IbkrOfflineOrderState.Working, model.Get(Coid).State);
    }

    private static IbkrOfflineOrderLifecycle PendingCancel()
    {
        var model = new IbkrOfflineOrderLifecycle();
        model.Register(new IbkrOfflineOrderIntent(
            Coid, AccountKey, "AAPL", OrderSide.Buy, 5m));
        model.Apply(new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.PreflightApproved));
        model.Apply(new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.SubmissionStarted));
        model.Apply(new IbkrOfflineOrderEvent(Coid,
            IbkrOfflineOrderEventType.BrokerAcknowledged, BrokerOrderId: BrokerId));
        model.Apply(new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.CancelRequested));
        return model;
    }
}
