using System.Text.Json.Serialization;
using IncidentIQ.Api.Features.Ingestion;
using IncidentIQ.Api.Features.Retrieval;

namespace IncidentIQ.Api.Features.Investigation;

public sealed record GroundedAnswerResult(
    string Question,
    string Answer,
    bool InsufficientEvidence,
    IReadOnlyList<GroundedAnswerStatement> Statements,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<AnswerCitation> Citations,
    SemanticSearchResult Retrieval,
    AnswerGeneration Generation);

public sealed record GroundedAnswerStatement(
    [property: JsonRequired] string Text,
    [property: JsonRequired] IReadOnlyList<string> SourceIds);

public sealed record AnswerCitation(string SourceId, EvidenceChunk Evidence);

public sealed record AnswerGeneration(
    string Deployment,
    string? ModelId,
    ChatUsage Usage,
    decimal? EstimatedCostUsd);

public sealed record ChatUsage(
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    long? ReasoningTokens);

internal sealed record GroundedAnswerDraft(
    [property: JsonRequired] bool InsufficientEvidence,
    [property: JsonRequired] IReadOnlyList<GroundedAnswerStatement> Statements,
    [property: JsonRequired] IReadOnlyList<string> Limitations);

public sealed class GroundedAnswerException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class GroundedAnswerContextException()
    : Exception("The retrieved context exceeds the grounded-answer size limit.");
