using System.ClientModel;
using IncidentIQ.Api.Features.Embeddings;
using IncidentIQ.Api.Features.Investigation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace IncidentIQ.Api.Configuration;

public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var answering = httpContext.Request.Path.StartsWithSegments("/api/investigation");
        var operation = answering ? "model" : "embedding";
        var investigating = httpContext.Request.Path.StartsWithSegments("/api/investigation/run");
        var artifact = investigating || httpContext.Request.Path.StartsWithSegments("/api/investigation/report")
            ? "report" : "answer";
        ProblemDetails problem;
        if (exception is BadHttpRequestException badRequest)
        {
            logger.LogWarning("Rejected an invalid HTTP request with status {StatusCode}.", badRequest.StatusCode);
            problem = new ProblemDetails
            {
                Status = badRequest.StatusCode,
                Title = "The HTTP request is invalid.",
                Detail = "Provide a JSON object with a string question, an optional topK from 1 to 10, and an optional valid documentId."
            };
        }
        else if (exception is OptionsValidationException configuration
            && configuration.OptionsType == typeof(FoundryOptions))
        {
            logger.LogWarning("Foundry configuration is incomplete or invalid: {Errors}", configuration.Message);
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = answering ? "Foundry model configuration is unavailable." : "Embedding configuration is unavailable.",
                Detail = answering
                    ? "Configure Foundry:Endpoint, Foundry:ApiKey, Foundry:EmbeddingDeployment, and Foundry:ChatDeployment, then restart the app."
                    : "Configure Foundry:Endpoint, Foundry:ApiKey, and Foundry:EmbeddingDeployment, then restart the app."
            };
        }
        else if (exception is ClientResultException provider)
        {
            logger.LogWarning("Foundry {Operation} request failed with upstream HTTP status {UpstreamStatus}.", operation, provider.Status);
            problem = new ProblemDetails
            {
                Status = provider.Status == 429
                    ? StatusCodes.Status503ServiceUnavailable
                    : StatusCodes.Status502BadGateway,
                Title = $"Foundry {operation} request failed.",
                Detail = provider.Status switch
                {
                    401 or 403 => "Foundry rejected the credentials or access. Check the API key and resource permissions.",
                    404 => answering
                        ? "Foundry could not find the model resource. Check the v1 endpoint and both deployment names."
                        : "Foundry could not find the embeddings resource. Check the v1 endpoint and embedding deployment name.",
                    429 => "Foundry is rate-limiting requests or quota is exhausted. Wait and check deployment quota before retrying.",
                    _ => $"The Foundry {operation} request failed. Check the endpoint, deployment, and Azure resource status."
                },
                Extensions = { ["upstreamStatus"] = provider.Status }
            };
        }
        else if (exception is EmbeddingSnapshotException snapshot)
        {
            logger.LogWarning("Semantic search rejected the embedding snapshot: {Reason}", snapshot.Message);
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "The embedding snapshot is unavailable or incompatible.",
                Detail = $"{snapshot.Message} Regenerate it with POST /api/ingestion/embed before searching."
            };
        }
        else if (exception is GroundedAnswerException answer)
        {
            logger.LogWarning("Rejected the generated {Artifact}: {Reason}", artifact, answer.Message);
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status502BadGateway,
                Title = $"The generated {artifact} could not be validated.",
                Detail = $"{answer.Message} No unvalidated {artifact} was returned. Model usage may still have occurred."
            };
        }
        else if (exception is GroundedAnswerContextException or InvestigationContextException)
        {
            logger.LogWarning("Rejected retrieved context above {Limit} characters.", GroundedGenerationService.MaxContextCharacters);
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "The retrieved context is too large.",
                Detail = investigating
                    ? "Reduce topK or narrow the investigation. No report was returned; embedding and chat usage may already have occurred."
                    : "Reduce topK and retry. The question was embedded, but no chat request was made."
            };
        }
        else if (exception is InvestigationTimeoutException)
        {
            logger.LogWarning("Investigation exceeded its {TimeoutSeconds}-second total time limit.", InvestigationService.RequestTimeout.TotalSeconds);
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status504GatewayTimeout,
                Title = "The investigation timed out.",
                Detail = "No report was returned. Model usage may have occurred; check Azure before retrying."
            };
        }
        else if (exception is GroundedAnswerTimeoutException)
        {
            logger.LogWarning("Foundry chat exceeded its {TimeoutSeconds}-second time limit.", FoundryChatClient.RequestTimeout.TotalSeconds);
            problem = new ProblemDetails
            {
                Status = StatusCodes.Status504GatewayTimeout,
                Title = "The Foundry chat request timed out.",
                Detail = $"No {artifact} was returned. Model usage may still have occurred; check Azure before retrying."
            };
        }
        else
        {
            return false;
        }

        httpContext.Response.StatusCode = problem.Status!.Value;
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem
        });
        return true;
    }
}
