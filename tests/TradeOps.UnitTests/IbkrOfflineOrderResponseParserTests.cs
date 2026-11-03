using TradeOps.Domain.Enums;
using TradeOps.Infrastructure.Exchange.Ibkr;
using Xunit;

namespace TradeOps.UnitTests;

public sealed class IbkrOfflineOrderResponseParserTests
{
    private const string Coid = "to-synthetic-001";
    private const string ReplyOne = "07a13a5a-4a48-44a5-bb25-5ab37b79186c";
    private const string ReplyTwo = "a12b34c5-d678-9e01-2345-6a890b12cd3e";

    private const string Ack = """
        [{"order_id":"123456","order_status":"Submitted","encrypt_message":"1"}]
        """;

    private const string Prompt = """
        [{
          "id":"07a13a5a-4a48-44a5-bb25-5ab37b79186c",
          "message":["Synthetic limit-price precaution. Confirm separately."],
          "isSuppressed":false,
          "messageIds":["o163"]
        }]
        """;

    [Theory]
    [InlineData("""{"order_id":"123456","order_status":"Submitted"}""", "Submitted")]
    [InlineData("""[{"order_id":"123456","order_status":"Submitted"}]""", "Submitted")]
    [InlineData("""[{"order_id":"123456","order_status":"PreSubmitted"}]""", "PreSubmitted")]
    [InlineData("""{"order_id":"123456","order_status":"PendingSubmit"}""", "PendingSubmit")]
    public void ParsesDocumentedAcknowledgementShapes(string fixture, string status)
    {
        var outcome = IbkrOfflineOrderResponseParser.Parse(fixture);
        Assert.Equal(IbkrOfflineOrderResponseType.Acknowledged, outcome.Type);
        Assert.Equal("123456", outcome.BrokerOrderId);
        Assert.Equal(status, outcome.BrokerStatus);
        Assert.Null(outcome.ReplyId);
    }

    [Fact]
    public void ParsesWarningWithoutRetainingSensitiveMessageText()
    {
        var outcome = IbkrOfflineOrderResponseParser.Parse(Prompt);
        Assert.Equal(IbkrOfflineOrderResponseType.ReplyRequired, outcome.Type);
        Assert.Equal(ReplyOne, outcome.ReplyId);
        Assert.Equal(1, outcome.WarningCount);
        Assert.Null(outcome.BrokerOrderId);
        Assert.DoesNotContain("Synthetic limit-price", outcome.ToString());
    }

    [Fact]
    public void Http200ErrorBody_IsExplicitBrokerRejectionNotAcknowledgement()
    {
        var outcome = IbkrOfflineOrderResponseParser.Parse(
            """{"error":"Insufficient funds for requested order"}""");
        Assert.Equal(IbkrOfflineOrderResponseType.Rejected, outcome.Type);
        Assert.Null(outcome.BrokerOrderId);
        Assert.Null(outcome.ReplyId);
        Assert.DoesNotContain("Insufficient", outcome.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("2")]
    [InlineData("""{"order_id":"123456"}""")]
    [InlineData("""{"order_status":"Submitted"}""")]
    [InlineData("""{"order_id":123456,"order_status":"Submitted"}""")]
    [InlineData("""{"order_id":"","order_status":"Submitted"}""")]
    [InlineData("""{"order_id":"123456","order_status":"Filled"}""")]
    [InlineData("""{"order_id":"123456","order_status":"Cancelled"}""")]
    [InlineData("""{"order_id":"123456","order_status":"Rejected"}""")]
    [InlineData("""{"order_id":"123456","order_status":"UNKNOWN"}""")]
    [InlineData("""{"order_id":"123456","order_status":"Submitted","encrypt_message":"0"}""")]
    [InlineData("""{"order_id":"123456","order_status":"Submitted","id":"r1"}""")]
    [InlineData("""{"order_id":"123456","order_status":"Submitted","error":"bad"}""")]
    [InlineData("""{"error":null}""")]
    [InlineData("""{"error":"bad","id":"reply"}""")]
    [InlineData("""{"error":"","order_status":"Submitted"}""")]
    [InlineData("""{"id":"r1"}""")]
    [InlineData("""{"message":["warning"]}""")]
    [InlineData("""{"id":"r1","message":[]}""")]
    [InlineData("""{"id":"r1","message":"warning"}""")]
    [InlineData("""{"id":"r1","message":[null]}""")]
    [InlineData("""{"id":"r1","message":[""]}""")]
    [InlineData("""{"id":"../reply","message":["warning"]}""")]
    [InlineData("""{"id":"r1","message":["warning"],"isSuppressed":true}""")]
    [InlineData("""{"id":"r1","message":["warning"],"isSuppressed":"false"}""")]
    [InlineData("""{"id":"r1","message":["warning"],"messageIds":"o163"}""")]
    [InlineData("""{"id":"r1","message":["warning"],"messageIds":["../o163"]}""")]
    [InlineData("""[{},{}]""")]
    [InlineData("""[{"order_id":"1","order_status":"Submitted"},{"id":"r1","message":["warning"]}]""")]
    [InlineData("""{"order_id":"1","order_id":"2","order_status":"Submitted"}""")]
    [InlineData("""{"id":"r1","id":"r2","message":["warning"]}""")]
    [InlineData("""{"error":"first","error":"second"}""")]
    [InlineData("""{"order_id":"1","order_status":"Submitted","order_status":"Cancelled"}""")]
    [InlineData("""{"order_id":"1","order_status":"Submitted","isSuppressed":false}""")]
    public void MalformedConflictingOrUnsupportedResponses_FailClosed(string fixture)
    {
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineOrderResponseParser.Parse(fixture));
    }

    [Fact]
    public void OversizedAndInvalidJson_AreRejectedWithoutReturningPayload()
    {
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineOrderResponseParser.Parse(new string(' ', 33_000)));
        Assert.Throws<InvalidOperationException>(() =>
            IbkrOfflineOrderResponseParser.Parse("{\"order_id\":"));
    }

    [Fact]
    public void CompleteSyntheticReplyChain_MakesNoBrokerRequests()
    {
        var model = Submitted();
        var replay = new IbkrOfflineOrderResponseReplay(model);

        var warning1 = replay.ApplyInitialResponse(Coid, Prompt);
        Assert.Equal(IbkrOfflineOrderState.AwaitingReply, warning1.State);
        Assert.Equal(ReplyOne, warning1.PendingReplyId);
        Assert.Equal(1, warning1.ReplySteps);

        // Parsing a warning does NOT record a confirmation.
        Assert.Throws<InvalidOperationException>(() =>
            replay.ApplyConfirmationResponse(Coid, ReplyOne, Ack));
        model.Apply(new IbkrOfflineOrderEvent(Coid,
            IbkrOfflineOrderEventType.ReplyConfirmed, ReplyId: ReplyOne));

        var warning2 = replay.ApplyConfirmationResponse(
            Coid, ReplyOne,
            """
            {"id":"a12b34c5-d678-9e01-2345-6a890b12cd3e","message":["Second synthetic warning"]}
            """);
        Assert.Equal(IbkrOfflineOrderState.AwaitingReply, warning2.State);
        Assert.Equal(ReplyTwo, warning2.PendingReplyId);

        Assert.Throws<InvalidOperationException>(() =>
            replay.ApplyConfirmationResponse(Coid, ReplyOne, Ack));
        model.Apply(new IbkrOfflineOrderEvent(Coid,
            IbkrOfflineOrderEventType.ReplyConfirmed, ReplyId: ReplyTwo));
        var accepted = replay.ApplyConfirmationResponse(Coid, ReplyTwo, Ack);

        Assert.Equal(IbkrOfflineOrderState.Working, accepted.State);
        Assert.Equal("123456", accepted.BrokerOrderId);
        Assert.Equal(2, accepted.ReplySteps);
        Assert.Equal(IbkrOfflineOrderState.Working, model.Get(Coid).State);
    }

    [Fact]
    public void DirectAcknowledgement_NeedsNoReplyConfirmation()
    {
        var model = Submitted();
        var replay = new IbkrOfflineOrderResponseReplay(model);
        var outcome = replay.ApplyInitialResponse(Coid, Ack);
        Assert.Equal(IbkrOfflineOrderState.Working, outcome.State);
        Assert.Equal("123456", outcome.BrokerOrderId);
        Assert.Throws<InvalidOperationException>(() =>
            replay.ApplyInitialResponse(Coid, Ack));
    }

    [Fact]
    public void Http200BrokerRejection_MapsToTerminalRejection()
    {
        var model = Submitted();
        var replay = new IbkrOfflineOrderResponseReplay(model);
        var rejected = replay.ApplyInitialResponse(
            Coid, """{"error":"Synthetic insufficient funds"}""");
        Assert.Equal(IbkrOfflineOrderState.Rejected, rejected.State);
        Assert.Throws<InvalidOperationException>(() =>
            model.Apply(new IbkrOfflineOrderEvent(Coid,
                IbkrOfflineOrderEventType.SubmissionStarted)));
    }

    [Fact]
    public void MalformedReply_DoesNotChangeOrderOrConfirmWarning()
    {
        var model = Submitted();
        var replay = new IbkrOfflineOrderResponseReplay(model);
        Assert.Throws<InvalidOperationException>(() =>
            replay.ApplyInitialResponse(Coid,
                """{"order_id":"987","order_status":"Filled"}"""));
        Assert.Equal(IbkrOfflineOrderState.AwaitingAcknowledgement, model.Get(Coid).State);

        replay.ApplyInitialResponse(Coid, Prompt);
        Assert.Throws<InvalidOperationException>(() =>
            replay.ApplyConfirmationResponse(Coid, ReplyOne, Ack));
        Assert.Equal(IbkrOfflineOrderState.AwaitingReply, model.Get(Coid).State);
    }

    [Fact]
    public void BrokerResponseAfterUnknownCannotBeAppliedWithoutReconciliation()
    {
        var model = Submitted();
        model.Apply(new IbkrOfflineOrderEvent(Coid, IbkrOfflineOrderEventType.TransportTimedOut));
        var replay = new IbkrOfflineOrderResponseReplay(model);
        Assert.Throws<InvalidOperationException>(() =>
            replay.ApplyInitialResponse(Coid, Ack));
        Assert.Equal(IbkrOfflineOrderState.Unknown, model.Get(Coid).State);
    }

    private static IbkrOfflineOrderLifecycle Submitted()
    {
        var model = new IbkrOfflineOrderLifecycle();
        model.Register(new IbkrOfflineOrderIntent(
            Coid, "offline-fixture", "AAPL", OrderSide.Buy, 1m));
        model.Apply(new IbkrOfflineOrderEvent(
            Coid, IbkrOfflineOrderEventType.PreflightApproved));
        model.Apply(new IbkrOfflineOrderEvent(
            Coid, IbkrOfflineOrderEventType.SubmissionStarted));
        return model;
    }
}
