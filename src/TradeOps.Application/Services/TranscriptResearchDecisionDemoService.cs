using TradeOps.Application.Models;

namespace TradeOps.Application.Services;

public sealed record TranscriptResearchArtifactBundle(
    string NormalizedDocumentJson,
    string StructuredExtractionJson,
    EarningsTranscriptResearchContext Context);

public sealed record TranscriptResearchDecisionDemoRequest(
    TranscriptResearchArtifactBundle Prior,
    TranscriptResearchArtifactBundle Current,
    EarningsResearchPolicyDefinition Policy);

public sealed record TranscriptResearchDecisionDemoResult(
    EarningsTranscriptResearchInput PriorInput,
    EarningsTranscriptFactSet PriorFactSet,
    EarningsEvent PriorEvent,
    EarningsTranscriptResearchInput CurrentInput,
    EarningsTranscriptFactSet CurrentFactSet,
    EarningsEvent CurrentEvent,
    EarningsAssessmentResult Assessment,
    ResearchDecision Decision,
    string PolicyFingerprint);

public sealed class TranscriptResearchDecisionDemoService
{
    public TranscriptResearchDecisionDemoResult Run(
        TranscriptResearchDecisionDemoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Prior);
        ArgumentNullException.ThrowIfNull(request.Current);
        ArgumentNullException.ThrowIfNull(request.Policy);

        var prior =
            BuildEvent(
                request.Prior);
        var current =
            BuildEvent(
                request.Current);

        if (!Equals(
                prior.Event.Instrument,
                current.Event.Instrument))
        {
            throw new ArgumentException(
                "Prior and current transcript artifacts must reference the same instrument.",
                nameof(request));
        }

        var priorPublishedAt =
            prior.Event.PublishedAt
                .ToUniversalTime();
        var currentPublishedAt =
            current.Event.PublishedAt
                .ToUniversalTime();

        if (priorPublishedAt >=
            currentPublishedAt)
        {
            throw new ArgumentException(
                "Prior transcript event must be published strictly before the current transcript event.",
                nameof(request));
        }

        var settings =
            EarningsResearchPolicyConfiguration
                .ToDecisionRuleSettings(
                    request.Policy);
        var targetWeightPolicy =
            EarningsResearchPolicyConfiguration
                .ToTargetWeightPolicy(
                    request.Policy);
        var policyFingerprint =
            EarningsResearchPolicyConfiguration
                .ComputeFingerprint(
                    request.Policy);

        var assessment =
            DeterministicEarningsDecisionRule
                .Assess(
                    current.Event,
                    prior.Event,
                    settings);

        var currentRetrievedAt =
            current.Event.Provenance
                .RetrievedAt
                .ToUniversalTime();
        var generatedAt =
            currentRetrievedAt >
            currentPublishedAt
                ? currentRetrievedAt
                : currentPublishedAt;

        var decision =
            DeterministicEarningsDecisionRule
                .Evaluate(
                    current.Event,
                    prior.Event,
                    generatedAt,
                    targetWeightPolicy,
                    request.Policy.StrategyId,
                    settings);

        return new TranscriptResearchDecisionDemoResult(
            prior.Input,
            prior.FactSet,
            prior.Event,
            current.Input,
            current.FactSet,
            current.Event,
            assessment,
            decision,
            policyFingerprint);
    }

    private static (
        EarningsTranscriptResearchInput Input,
        EarningsTranscriptFactSet FactSet,
        EarningsEvent Event)
        BuildEvent(
            TranscriptResearchArtifactBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(bundle.Context);

        var input =
            DocFlowEarningsResearchAdapter
                .Adapt(
                    bundle.NormalizedDocumentJson,
                    bundle.Context);

        var factSet =
            DocFlowStructuredEarningsFactsAdapter
                .Adapt(
                    bundle.StructuredExtractionJson,
                    input);

        var earningsEvent =
            EarningsTranscriptEventFactory
                .Create(
                    input,
                    factSet);

        return (
            input,
            factSet,
            earningsEvent);
    }
}
