namespace TradeOps.Domain.Enums;

public enum SignalIngressRequestOutcome
{
    Received = 0,
    TimestampRejected = 1,
    SignatureRejected = 2,
    PayloadRejected = 3,
    ReplayRejected = 4,
    RequestRejected = 5,
    SignalConflict = 6,
    RiskRejected = 7,
    Accepted = 8,
    Failed = 9
}
