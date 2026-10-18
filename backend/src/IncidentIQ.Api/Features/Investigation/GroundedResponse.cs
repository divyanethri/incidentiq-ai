using System.Text.Json;
using System.Text.Json.Serialization;

namespace IncidentIQ.Api.Features.Investigation;

internal static class GroundedResponse
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static T Parse<T>(string text) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(text, JsonOptions)
                ?? throw new GroundedAnswerException("The chat model returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new GroundedAnswerException("The chat model returned an invalid response structure.", exception);
        }
    }

    public static void ValidateCitedText(
        string? text,
        IReadOnlyList<string>? sourceIds,
        IReadOnlyList<AnswerCitation> sources,
        int maxLength = 1500)
    {
        var allowedIds = sources.Select(source => source.SourceId).ToHashSet(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength
            || text.Contains("[S", StringComparison.Ordinal)
            || sourceIds is null || sourceIds.Count == 0 || sourceIds.Count > sources.Count
            || sourceIds.Any(id => id is null || !allowedIds.Contains(id))
            || sourceIds.Distinct(StringComparer.Ordinal).Count() != sourceIds.Count)
        {
            throw new GroundedAnswerException("The chat model returned invalid cited text or source references.");
        }
    }

    public static void ValidateStatements(
        IReadOnlyList<GroundedAnswerStatement>? statements,
        IReadOnlyList<AnswerCitation> sources,
        int maxCount)
    {
        if (statements is null || statements.Count > maxCount)
        {
            throw new GroundedAnswerException("The chat model returned missing or oversized statement fields.");
        }

        foreach (var statement in statements)
        {
            if (statement is null)
            {
                throw new GroundedAnswerException("The chat model returned an empty statement.");
            }

            ValidateCitedText(statement.Text, statement.SourceIds, sources);
        }
    }

    public static string FormatStatement(GroundedAnswerStatement statement) =>
        $"{statement.Text} {string.Join(" ", statement.SourceIds.Select(id => $"[{id}]"))}";
}
