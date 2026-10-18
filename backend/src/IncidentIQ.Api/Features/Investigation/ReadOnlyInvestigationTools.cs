using System.Globalization;
using System.Text.Json;
using IncidentIQ.Api.Features.Ingestion;
using Microsoft.Extensions.AI;

namespace IncidentIQ.Api.Features.Investigation;

internal sealed class ReadOnlyInvestigationTools(
    IngestionPreview preview,
    string? documentScope,
    List<AnswerCitation> sources,
    ILogger logger)
{
    internal const int MaxReturnedItems = 12;
    private static readonly HashSet<string> ConfigurationEvents = new(StringComparer.Ordinal)
    {
        "service.configuration", "operator.configuration.review", "deployment.started",
        "deployment.completed", "operator.rollback.started"
    };

    internal static IList<AITool> Declarations { get; } =
    [
        new EvidenceToolDeclaration("GetIncidentLogs",
            "Read up to 12 original synthetic JSON log events, oldest first, for an allowlisted application-log document. "
            + "Optional inclusive UTC start/end filters use ISO 8601 timestamps ending in Z. Null means unbounded."),
        new EvidenceToolDeclaration("GetConfigurationChanges",
            "Read up to 12 recorded service.configuration, operator.configuration.review, deployment.started, "
            + "deployment.completed and operator.rollback.started events from an allowlisted synthetic application-log "
            + "document. Optional inclusive UTC start/end filters use ISO 8601 ending in Z. "
            + "This reads recorded events, not live configuration or deployment manifests."),
        new EvidenceToolDeclaration("GetRunbook",
            "Read the bundled troubleshooting guidance for an allowlisted runbook document. "
            + "Guidance is not an incident observation or evidence that an action was executed.")
    ];

    internal InvestigationToolResult Execute(FunctionCallContent call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (call.Name is not ("GetIncidentLogs" or "GetConfigurationChanges" or "GetRunbook"))
        {
            return Reject("unknown_tool", "Only the three declared read-only tools are permitted.");
        }

        InvestigationToolArguments arguments;
        try
        {
            if (call.Exception is not null || call.Arguments is null)
            {
                return Reject("invalid_arguments", "Provide the declared JSON arguments.");
            }

            var json = JsonSerializer.Serialize(call.Arguments, GroundedResponse.JsonOptions);
            arguments = GroundedResponse.Parse<InvestigationToolArguments>(json);
        }
        catch (GroundedAnswerException)
        {
            return Reject("invalid_arguments", "Arguments must match the declared fields and types.");
        }

        if (string.IsNullOrWhiteSpace(arguments.DocumentId) || arguments.DocumentId.Length > 100)
        {
            return Reject("invalid_document", "Use an exact document ID from the supplied catalog.");
        }

        if (documentScope is not null && arguments.DocumentId != documentScope)
        {
            return Reject("out_of_scope", "The requested document is outside the caller's explicit document scope.", arguments);
        }

        var document = preview.Documents.SingleOrDefault(item => item.DocumentId == arguments.DocumentId);
        if (document is null)
        {
            return Reject("unknown_document", "The document is not in the bundled synthetic catalog.");
        }

        var expectedType = call.Name == "GetRunbook" ? "runbook" : "application-log";
        if (document.DocumentType != expectedType)
        {
            return Reject("wrong_document_type", "Choose a catalog document of the tool's supported type.", arguments);
        }

        if (!TryTime(arguments.StartTimeUtc, out var start) || !TryTime(arguments.EndTimeUtc, out var end)
            || (start is not null && end is not null && start > end)
            || (call.Name == "GetRunbook" && (start is not null || end is not null)))
        {
            return Reject("invalid_time_window", "Use inclusive UTC timestamps ending in Z, with start <= end. Runbooks do not accept a time window.", arguments);
        }

        var chunks = preview.Chunks.Where(chunk => chunk.DocumentId == document.DocumentId).ToArray();
        var evidence = new List<EvidenceChunk>();
        if (call.Name == "GetRunbook")
        {
            evidence.AddRange(chunks);
        }
        else
        {
            var lines = new SortedDictionary<int, string>();
            foreach (var chunk in chunks)
            {
                var contentLines = chunk.Content.Split('\n');
                for (var index = 0; index < contentLines.Length; index++)
                {
                    lines[chunk.StartLine + index] = contentLines[index];
                }
            }

            foreach (var (lineNumber, line) in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var json = JsonDocument.Parse(line);
                var entry = json.RootElement;
                if (!entry.GetProperty("synthetic").GetBoolean()
                    || !TryTime(entry.GetProperty("timestamp").GetString(), out var timestamp)
                    || timestamp is null)
                {
                    throw new InvalidDataException("A bundled synthetic log event has invalid metadata.");
                }

                if ((start is not null && timestamp < start) || (end is not null && timestamp > end)
                    || (call.Name == "GetConfigurationChanges"
                        && !ConfigurationEvents.Contains(entry.GetProperty("event").GetString()!)))
                {
                    continue;
                }

                evidence.Add(DocumentChunker.CreateEvidence(
                    document.DocumentId, document.SourceFile, document.DocumentType, lineNumber, [line]));
            }
        }

        var selected = evidence.Take(MaxReturnedItems).Select(Register).ToArray();
        var trace = new InvestigationToolCall(
            call.CallId, call.Name, arguments, "completed",
            evidence.Count == 0 ? "No matching synthetic evidence was found." : "Read bundled synthetic evidence only.",
            evidence.Count, evidence.Count > MaxReturnedItems,
            selected.Select(source => source.SourceId).ToArray());
        logger.LogInformation(
            "Read-only investigation tool {ToolName} returned {Count} of {MatchingCount} evidence items. Truncated: {Truncated}.",
            call.Name, selected.Length, evidence.Count, trace.Truncated);
        return new InvestigationToolResult(trace, selected);

        InvestigationToolResult Reject(string code, string message, InvestigationToolArguments? args = null)
        {
            logger.LogWarning("Rejected a read-only investigation tool request: {Code}.", code);
            return new InvestigationToolResult(
                new InvestigationToolCall(call.CallId, call.Name, args, "rejected",
                    $"{code}: {message}", 0, false, []), []);
        }
    }

    private AnswerCitation Register(EvidenceChunk evidence)
    {
        var existing = sources.FirstOrDefault(source => source.Evidence.Id == evidence.Id);
        if (existing is not null)
        {
            return existing;
        }

        var citation = new AnswerCitation($"S{sources.Count + 1}", evidence);
        sources.Add(citation);
        return citation;
    }

    private static bool TryTime(string? text, out DateTimeOffset? value)
    {
        value = null;
        if (text is null)
        {
            return true;
        }

        if (text.Length > 40
            || !DateTimeOffset.TryParseExact(
                text, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private sealed class EvidenceToolDeclaration(string name, string description) : AIFunctionDeclaration
    {
        public override string Name => name;
        public override string Description => description;
        public override JsonElement JsonSchema { get; } = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                documentId = new { type = "string" },
                startTimeUtc = new { type = new[] { "string", "null" } },
                endTimeUtc = new { type = new[] { "string", "null" } }
            },
            required = new[] { "documentId", "startTimeUtc", "endTimeUtc" },
            additionalProperties = false
        });
    }
}
