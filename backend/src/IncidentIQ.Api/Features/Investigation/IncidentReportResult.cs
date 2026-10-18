using System.Text.Json.Serialization;
using IncidentIQ.Api.Features.Retrieval;

namespace IncidentIQ.Api.Features.Investigation;

public sealed record IncidentReportResult(
    string Question,
    string Report,
    bool InsufficientEvidence,
    IReadOnlyList<GroundedAnswerStatement> Summary,
    IReadOnlyList<GroundedAnswerStatement> Evidence,
    GroundedAnswerStatement? ProbableRootCause,
    IReadOnlyList<string> Uncertainty,
    IReadOnlyList<RecommendedNextStep> RecommendedNextSteps,
    IReadOnlyList<AnswerCitation> Citations,
    SemanticSearchResult Retrieval,
    AnswerGeneration Generation)
{
    public string RootCauseClassification => ProbableRootCause is null ? "not_established" : "hypothesis";
}

public sealed record RecommendedNextStep(
    [property: JsonRequired] string Action,
    [property: JsonRequired] string Rationale,
    [property: JsonRequired] IReadOnlyList<string> SourceIds)
{
    public string ExecutionStatus => "not_executed";
}

internal sealed record IncidentReportDraft(
    [property: JsonRequired] bool InsufficientEvidence,
    [property: JsonRequired] IReadOnlyList<GroundedAnswerStatement> Summary,
    [property: JsonRequired] IReadOnlyList<GroundedAnswerStatement> Evidence,
    [property: JsonRequired] GroundedAnswerStatement? ProbableRootCause,
    [property: JsonRequired] IReadOnlyList<string> Uncertainty,
    [property: JsonRequired] IReadOnlyList<RecommendedNextStep> RecommendedNextSteps);
