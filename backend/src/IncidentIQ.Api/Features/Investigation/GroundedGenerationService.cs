using System.Text.Json;
using IncidentIQ.Api.Configuration;
using IncidentIQ.Api.Features.Retrieval;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace IncidentIQ.Api.Features.Investigation;

public sealed class GroundedGenerationService(
    SemanticSearchService search,
    FoundryChatClient chat,
    IOptions<FoundryOptions> options,
    ILogger<GroundedGenerationService> logger)
{
    public const int MaxContextCharacters = 32000;

    internal async Task<GroundedGeneration> GenerateAsync(
        string question,
        int topK,
        string? documentId,
        string instructions,
        string schemaName,
        string schema,
        CancellationToken cancellationToken)
    {
        chat.EnsureConfigured();
        var retrieval = await search.SearchAsync(question, topK, documentId, cancellationToken);
        var sources = retrieval.Matches
            .Select((match, index) => new AnswerCitation($"S{index + 1}", match.Evidence))
            .ToArray();
        return await GenerateFromEvidenceAsync(
            question, retrieval, sources, instructions, schemaName, schema, null, cancellationToken);
    }

    internal async Task<GroundedGeneration> GenerateFromEvidenceAsync(
        string question,
        SemanticSearchResult retrieval,
        IReadOnlyList<AnswerCitation> sources,
        string instructions,
        string schemaName,
        string schema,
        object? investigation,
        CancellationToken cancellationToken)
    {
        var context = investigation is null
            ? JsonSerializer.Serialize(new { question, sources }, GroundedResponse.JsonOptions)
            : JsonSerializer.Serialize(new { question, sources, investigation }, GroundedResponse.JsonOptions);
        if (context.Length > MaxContextCharacters)
        {
            throw new GroundedAnswerContextException();
        }

        var response = await chat.GenerateAsync(
            [
                new Microsoft.Extensions.AI.ChatMessage(ChatRole.System, instructions),
                new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, context)
            ],
            schemaName, schema, cancellationToken);
        var metadata = RecordGeneration(response);

        ValidateCompletion(response, allowToolCalls: false);
        return new GroundedGeneration(response.Text, sources, retrieval, metadata);
    }

    internal AnswerGeneration RecordGeneration(ChatResponse response)
    {
        var usage = new ChatUsage(
            response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount,
            response.Usage?.TotalTokenCount, response.Usage?.ReasoningTokenCount);
        logger.LogInformation(
            "Foundry chat used {InputTokens} input tokens, {OutputTokens} output tokens, and {TotalTokens} total tokens; {ReasoningTokens} reasoning tokens are included in output.",
            usage.InputTokens, usage.OutputTokens, usage.TotalTokens, usage.ReasoningTokens);

        return new AnswerGeneration(options.Value.ChatDeployment.Trim(), response.ModelId, usage, null);
    }

    internal static void ValidateCompletion(ChatResponse response, bool allowToolCalls)
    {
        if ((response.FinishReason != Microsoft.Extensions.AI.ChatFinishReason.Stop
                && (!allowToolCalls || response.FinishReason != Microsoft.Extensions.AI.ChatFinishReason.ToolCalls))
            || response.Messages.SelectMany(message => message.Contents).Any(content => content is ErrorContent)
            || response.RawRepresentation is ChatCompletion { Refusal: { Length: > 0 } })
        {
            throw new GroundedAnswerException("The chat model refused, filtered, or did not complete the response.");
        }
    }
}

internal sealed record GroundedGeneration(
    string Text,
    IReadOnlyList<AnswerCitation> Sources,
    SemanticSearchResult Retrieval,
    AnswerGeneration Generation);
