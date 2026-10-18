using System.Text.Json.Serialization;

namespace IncidentIQ.Api.Features.Investigation;

public sealed record InvestigationResult(
    IncidentReportResult Report,
    IReadOnlyList<InvestigationToolCall> ToolCalls,
    IReadOnlyList<AnswerCitation> ToolEvidence,
    string StopReason,
    IReadOnlyList<InvestigationModelCall> ModelCalls)
{
    public int MaxToolCalls => InvestigationService.MaxToolCalls;
    public int MaxChatCalls => InvestigationService.MaxToolCalls + 1;
    public IReadOnlyList<string> EvidenceCollectionLimitations
    {
        get
        {
            var limits = new List<string>
            {
                "Tools read only bundled synthetic data, not live systems or deployment audits.",
                "Tool time filters do not restrict the initial semantic retrieval; this is not an as-of investigation."
            };
            if (StopReason == "tool_limit_reached")
            {
                limits.Add("The tool-call budget was reached; additional evidence may remain unread.");
            }

            if (ToolCalls.Any(call => call.Truncated))
            {
                limits.Add("At least one tool result was truncated; inspect its time window and matching item count.");
            }

            if (ToolCalls.Any(call => call.Status == "rejected"))
            {
                limits.Add("At least one tool request was rejected and supplied no evidence.");
            }

            return limits;
        }
    }
    public ChatUsage TotalChatUsage => new(
        Sum(call => call.Generation.Usage.InputTokens),
        Sum(call => call.Generation.Usage.OutputTokens),
        Sum(call => call.Generation.Usage.TotalTokens),
        Sum(call => call.Generation.Usage.ReasoningTokens));
    public decimal? EstimatedCostUsd => null;

    private long? Sum(Func<InvestigationModelCall, long?> selector)
    {
        var values = ModelCalls.Select(selector).ToArray();
        return values.Any(value => value is null) ? null : values.Sum(value => value!.Value);
    }
}

public sealed record InvestigationToolCall(
    string CallId,
    string Name,
    InvestigationToolArguments? Arguments,
    string Status,
    string Message,
    int MatchingItemCount,
    bool Truncated,
    IReadOnlyList<string> SourceIds);

public sealed record InvestigationToolArguments(
    [property: JsonRequired] string DocumentId,
    string? StartTimeUtc = null,
    string? EndTimeUtc = null);

public sealed record InvestigationModelCall(string Stage, AnswerGeneration Generation);

internal sealed record InvestigationToolResult(
    InvestigationToolCall Call,
    IReadOnlyList<AnswerCitation> Sources);

public sealed class InvestigationContextException()
    : Exception("The bounded investigation context exceeds its character limit.");

public sealed class InvestigationTimeoutException(Exception innerException)
    : Exception("The investigation exceeded its total time limit.", innerException);
