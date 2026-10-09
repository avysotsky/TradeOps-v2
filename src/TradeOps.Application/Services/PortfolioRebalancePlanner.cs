using TradeOps.Application.Models;
using TradeOps.Domain.Enums;

namespace TradeOps.Application.Services;

public static class PortfolioRebalancePlanner
{
    public static RebalancePlan Plan(
        ResearchDecision decision,
        PortfolioSnapshot portfolio,
        decimal? referencePrice,
        RebalanceConstraints? constraints = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(portfolio);

        constraints ??= new RebalanceConstraints();

        ValidatePortfolio(portfolio);
        ValidateConstraints(constraints);

        var validation =
            ResearchDecisionValidator.Validate(
                decision);

        if (!validation.IsValid ||
            validation.NormalizedDecision is null)
        {
            return BlockedWithoutCalculation(
                decision,
                portfolio,
                new RebalanceConstraintViolation(
                    "InvalidResearchDecision",
                    FormatValidationErrors(
                        validation.Errors)));
        }

        var normalizedDecision =
            validation.NormalizedDecision;

        if (normalizedDecision.Action !=
            ResearchDecisionAction.SetTargetWeight)
        {
            return BlockedWithoutCalculation(
                normalizedDecision,
                portfolio,
                new RebalanceConstraintViolation(
                    "UnsupportedAction",
                    $"WS-03 RebalancePlan v1 supports only {ResearchDecisionAction.SetTargetWeight}."));
        }

        if (normalizedDecision.Instrument.AssetClass !=
            AssetClass.Stock)
        {
            return BlockedWithoutCalculation(
                normalizedDecision,
                portfolio,
                new RebalanceConstraintViolation(
                    "UnsupportedAssetClass",
                    $"WS-03 RebalancePlan v1 supports only {AssetClass.Stock}."));
        }

        var targetWeight =
            normalizedDecision.TargetWeight!.Value;

        var targetViolations =
            EvaluateTargetConstraints(
                normalizedDecision,
                portfolio,
                constraints,
                targetWeight);

        if (targetViolations.Count > 0)
        {
            return BlockedWithoutCalculation(
                normalizedDecision,
                portfolio,
                targetViolations);
        }

        if (!referencePrice.HasValue)
        {
            return BlockedWithoutCalculation(
                normalizedDecision,
                portfolio,
                new RebalanceConstraintViolation(
                    "MissingPrice",
                    "A reference price is required to calculate target and delta quantities."));
        }

        if (referencePrice.Value <= 0m)
        {
            return BlockedWithoutCalculation(
                normalizedDecision,
                portfolio,
                new RebalanceConstraintViolation(
                    "InvalidPrice",
                    "Reference price must be greater than zero."));
        }

        var price =
            referencePrice.Value;
        var targetNotional =
            portfolio.NetAssetValue * targetWeight;
        var targetQuantity =
            targetNotional / price;

        var targetPosition =
            new TargetPosition(
                normalizedDecision.Instrument,
                targetWeight,
                price,
                targetNotional,
                targetQuantity);

        var currentQuantity =
            portfolio.Positions
                .Where(
                    position =>
                        IsSameInstrument(
                            position.Instrument,
                            normalizedDecision.Instrument))
                .Sum(position => position.Quantity);

        var currentNotional =
            currentQuantity * price;
        var deltaQuantity =
            targetQuantity - currentQuantity;
        var deltaNotional =
            targetNotional - currentNotional;

        if (deltaQuantity == 0m)
        {
            return CalculatedPlan(
                normalizedDecision,
                portfolio,
                targetPosition,
                currentQuantity,
                currentNotional,
                deltaQuantity,
                deltaNotional,
                RebalancePlanStatus.NoAction,
                null,
                Array.Empty<RebalanceConstraintViolation>());
        }

        if (Math.Abs(deltaQuantity) <=
            constraints.QuantityTolerance)
        {
            return CalculatedPlan(
                normalizedDecision,
                portfolio,
                targetPosition,
                currentQuantity,
                currentNotional,
                deltaQuantity,
                deltaNotional,
                RebalancePlanStatus.NoAction,
                null,
                new[]
                {
                    new RebalanceConstraintViolation(
                        "QuantityTolerance",
                        $"Absolute quantity delta {Math.Abs(deltaQuantity)} is within tolerance {constraints.QuantityTolerance}.")
                });
        }

        var tradeNotional =
            Math.Abs(deltaNotional);

        if (tradeNotional <
            constraints.MinimumTradeNotional)
        {
            return CalculatedPlan(
                normalizedDecision,
                portfolio,
                targetPosition,
                currentQuantity,
                currentNotional,
                deltaQuantity,
                deltaNotional,
                RebalancePlanStatus.NoAction,
                null,
                new[]
                {
                    new RebalanceConstraintViolation(
                        "MinimumTradeNotional",
                        $"Estimated trade notional {tradeNotional} is below minimum {constraints.MinimumTradeNotional}.")
                });
        }

        if (deltaQuantity > 0m)
        {
            var availableCash =
                portfolio.Cash -
                constraints.MinimumCashReserve;

            if (tradeNotional > availableCash)
            {
                return CalculatedPlan(
                    normalizedDecision,
                    portfolio,
                    targetPosition,
                    currentQuantity,
                    currentNotional,
                    deltaQuantity,
                    deltaNotional,
                    RebalancePlanStatus.Blocked,
                    null,
                    new[]
                    {
                        new RebalanceConstraintViolation(
                            "InsufficientCash",
                            $"Estimated buy notional {tradeNotional} exceeds available cash {availableCash} after reserve.")
                    });
            }
        }

        var side =
            deltaQuantity > 0m
                ? OrderSide.Buy
                : OrderSide.Sell;

        var orderIntent =
            new RebalanceOrderIntent(
                normalizedDecision.DecisionId,
                normalizedDecision.Instrument,
                side,
                Math.Abs(deltaQuantity),
                price,
                tradeNotional);

        return CalculatedPlan(
            normalizedDecision,
            portfolio,
            targetPosition,
            currentQuantity,
            currentNotional,
            deltaQuantity,
            deltaNotional,
            RebalancePlanStatus.Ready,
            orderIntent,
            Array.Empty<RebalanceConstraintViolation>());
    }

    private static IReadOnlyList<RebalanceConstraintViolation>
        EvaluateTargetConstraints(
            ResearchDecision decision,
            PortfolioSnapshot portfolio,
            RebalanceConstraints constraints,
            decimal targetWeight)
    {
        var violations =
            new List<RebalanceConstraintViolation>();

        if (targetWeight < 0m)
        {
            violations.Add(
                new RebalanceConstraintViolation(
                    "LongOnly",
                    "Target weight cannot be negative in WS-03 RebalancePlan v1."));
        }

        if (targetWeight >
            constraints.MaxTargetWeight)
        {
            violations.Add(
                new RebalanceConstraintViolation(
                    "MaxTargetWeight",
                    $"Target weight {targetWeight} exceeds maximum {constraints.MaxTargetWeight}."));
        }

        if (decision.GeneratedAt >
            portfolio.AsOf)
        {
            violations.Add(
                new RebalanceConstraintViolation(
                    "DecisionNotYetAvailable",
                    "Research decision was generated after the portfolio snapshot time."));
        }

        if (decision.ValidUntil.HasValue &&
            decision.ValidUntil.Value <=
            portfolio.AsOf)
        {
            violations.Add(
                new RebalanceConstraintViolation(
                    "DecisionExpired",
                    "Research decision is expired at the portfolio snapshot time."));
        }

        if (constraints.MaximumDecisionAge.HasValue &&
            decision.GeneratedAt <= portfolio.AsOf &&
            portfolio.AsOf - decision.GeneratedAt >
            constraints.MaximumDecisionAge.Value)
        {
            violations.Add(
                new RebalanceConstraintViolation(
                    "DecisionStale",
                    $"Research decision age {portfolio.AsOf - decision.GeneratedAt} exceeds maximum {constraints.MaximumDecisionAge.Value}."));
        }

        if (!string.IsNullOrWhiteSpace(
                decision.Instrument.Currency) &&
            !string.Equals(
                decision.Instrument.Currency,
                portfolio.BaseCurrency.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            violations.Add(
                new RebalanceConstraintViolation(
                    "CurrencyMismatch",
                    "WS-03 RebalancePlan v1 does not perform FX conversion between instrument and portfolio currencies."));
        }

        return violations;
    }

    private static RebalancePlan
        BlockedWithoutCalculation(
            ResearchDecision decision,
            PortfolioSnapshot portfolio,
            params RebalanceConstraintViolation[] violations) =>
        BlockedWithoutCalculation(
            decision,
            portfolio,
            (IReadOnlyList<RebalanceConstraintViolation>)violations);

    private static RebalancePlan
        BlockedWithoutCalculation(
            ResearchDecision decision,
            PortfolioSnapshot portfolio,
            IReadOnlyList<RebalanceConstraintViolation> violations) =>
        new(
            NormalizeIdentifier(
                decision.DecisionId),
            NormalizeIdentifier(
                decision.StrategyId),
            portfolio.AsOf,
            null,
            null,
            null,
            null,
            null,
            RebalancePlanStatus.Blocked,
            null,
            violations);

    private static RebalancePlan
        CalculatedPlan(
            ResearchDecision decision,
            PortfolioSnapshot portfolio,
            TargetPosition targetPosition,
            decimal currentQuantity,
            decimal currentNotional,
            decimal deltaQuantity,
            decimal deltaNotional,
            RebalancePlanStatus status,
            RebalanceOrderIntent? orderIntent,
            IReadOnlyList<RebalanceConstraintViolation> violations) =>
        new(
            decision.DecisionId,
            decision.StrategyId,
            portfolio.AsOf,
            targetPosition,
            currentQuantity,
            currentNotional,
            deltaQuantity,
            deltaNotional,
            status,
            orderIntent,
            violations);

    private static void ValidatePortfolio(
        PortfolioSnapshot portfolio)
    {
        if (string.IsNullOrWhiteSpace(
                portfolio.BaseCurrency))
        {
            throw new ArgumentException(
                "Portfolio base currency is required.",
                nameof(portfolio));
        }

        if (portfolio.NetAssetValue <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(portfolio),
                "Portfolio NAV must be greater than zero.");
        }

        if (portfolio.Cash < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(portfolio),
                "Portfolio cash cannot be negative in the long-only first slice.");
        }

        if (portfolio.AsOf == default)
        {
            throw new ArgumentException(
                "Portfolio AsOf timestamp is required.",
                nameof(portfolio));
        }

        if (portfolio.Positions is null)
        {
            throw new ArgumentException(
                "Portfolio positions are required.",
                nameof(portfolio));
        }

        foreach (var position in portfolio.Positions)
        {
            if (position is null ||
                position.Instrument is null)
            {
                throw new ArgumentException(
                    "Portfolio positions must contain an instrument.",
                    nameof(portfolio));
            }

            if (position.Quantity < 0m)
            {
                throw new ArgumentException(
                    "Short positions are not supported by WS-03 RebalancePlan v1.",
                    nameof(portfolio));
            }
        }
    }

    private static void ValidateConstraints(
        RebalanceConstraints constraints)
    {
        if (constraints.MaxTargetWeight <= 0m ||
            constraints.MaxTargetWeight > 1m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(constraints),
                "MaxTargetWeight must be greater than zero and no greater than one.");
        }

        if (constraints.MinimumTradeNotional < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(constraints),
                "MinimumTradeNotional cannot be negative.");
        }

        if (constraints.MinimumCashReserve < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(constraints),
                "MinimumCashReserve cannot be negative.");
        }

        if (constraints.QuantityTolerance < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(constraints),
                "QuantityTolerance cannot be negative.");
        }

        if (constraints.MaximumDecisionAge.HasValue &&
            constraints.MaximumDecisionAge.Value <=
            TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(constraints),
                "MaximumDecisionAge must be greater than zero when provided.");
        }
    }

    private static bool IsSameInstrument(
        InstrumentReference current,
        InstrumentReference target)
    {
        if (current.AssetClass !=
            target.AssetClass)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(
                current.VenueInstrumentId) &&
            !string.IsNullOrWhiteSpace(
                target.VenueInstrumentId))
        {
            return string.Equals(
                current.VenueInstrumentId.Trim(),
                target.VenueInstrumentId.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        if (!string.Equals(
                current.Symbol.Trim(),
                target.Symbol,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(
                   current.Currency) ||
               string.IsNullOrWhiteSpace(
                   target.Currency) ||
               string.Equals(
                   current.Currency.Trim(),
                   target.Currency,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatValidationErrors(
        IReadOnlyDictionary<string, string[]> errors)
    {
        if (errors.Count == 0)
        {
            return "Research decision validation failed.";
        }

        return string.Join(
            "; ",
            errors.SelectMany(
                item =>
                    item.Value.Select(
                        message =>
                            $"{item.Key}: {message}")));
    }

    private static string NormalizeIdentifier(
        string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim();
}
