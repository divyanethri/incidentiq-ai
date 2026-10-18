using IncidentIQ.Api.Configuration;
using IncidentIQ.Api.Features.Embeddings;
using IncidentIQ.Api.Features.Ingestion;
using IncidentIQ.Api.Features.Investigation;
using IncidentIQ.Api.Features.Retrieval;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

var configuredOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>();

var allowedOrigins = configuredOrigins
    ?? new[] { "http://localhost:5173", "http://127.0.0.1:5173" };
if (allowedOrigins.Any(origin =>
    !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
    || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
    || !string.IsNullOrEmpty(uri.Fragment) || origin != uri.GetLeftPart(UriPartial.Authority)))
{
    throw new InvalidOperationException("Cors:AllowedOrigins must contain exact HTTP(S) origins without paths, trailing slashes, credentials, queries, or fragments.");
}
builder.Services.AddCors(options => options.AddPolicy("Frontend", policy =>
{
    if (allowedOrigins.Length > 0)
    {
        policy.WithOrigins(allowedOrigins).WithMethods("GET", "POST").WithHeaders("Content-Type");
    }
}));

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<DocumentChunker>();
builder.Services.AddSingleton<SyntheticIngestionService>();
builder.Services.AddSingleton<FoundryEmbeddingGenerator>();
builder.Services.AddSingleton<LocalEmbeddingStore>();
builder.Services.AddSingleton<EmbeddingIngestionService>();
builder.Services.AddSingleton<SemanticSearchService>();
builder.Services.AddSingleton<FoundryChatClient>();
builder.Services.AddSingleton<GroundedGenerationService>();
builder.Services.AddSingleton<GroundedAnswerService>();
builder.Services.AddSingleton<IncidentReportService>();
builder.Services.AddSingleton<InvestigationService>();
//builder.Services.AddOptions<FoundryOptions>()
//    .BindConfiguration(FoundryOptions.SectionName)
//    .Validate(
//        options => string.IsNullOrWhiteSpace(options.Endpoint)
//            || (Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
//                && endpoint.Scheme == Uri.UriSchemeHttps),
//        "Foundry:Endpoint must be an absolute HTTPS URL when supplied.")
//    .ValidateOnStart();

builder.Services.AddOptions<FoundryOptions>()
    .BindConfiguration(FoundryOptions.SectionName);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors("Frontend");

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = (context, report) => context.Response.WriteAsJsonAsync(
        new { service = "IncidentIQ", status = report.Status.ToString() },
        context.RequestAborted)
});

app.MapGet("/api/ingestion/preview", async (
    SyntheticIngestionService ingestion,
    CancellationToken cancellationToken) =>
    Results.Ok(await ingestion.PreviewAsync(cancellationToken)));

app.MapPost("/api/ingestion/embed", async (
    EmbeddingIngestionService ingestion,
    CancellationToken cancellationToken) =>
    Results.Ok(await ingestion.IngestAsync(cancellationToken)));

app.MapPost("/api/retrieval/search", async (
    SemanticSearchRequest request,
    SemanticSearchService search,
    SyntheticIngestionService ingestion,
    ILogger<SemanticSearchService> logger,
    CancellationToken cancellationToken) =>
{
    var errors = ValidateEvidenceRequest(request, ingestion);
    if (errors.Count > 0)
    {
        logger.LogWarning("Semantic search rejected invalid question, topK, or documentId input.");
        return Results.ValidationProblem(errors);
    }

    return Results.Ok(await search.SearchAsync(
        request.Question!.Trim(), request.TopK, request.DocumentId, cancellationToken));
});

app.MapPost("/api/investigation/answer", async (
    SemanticSearchRequest request,
    GroundedAnswerService investigation,
    SyntheticIngestionService ingestion,
    ILogger<GroundedAnswerService> logger,
    CancellationToken cancellationToken) =>
{
    var errors = ValidateEvidenceRequest(request, ingestion);
    if (errors.Count > 0)
    {
        logger.LogWarning("Grounded answer rejected invalid question, topK, or documentId input.");
        return Results.ValidationProblem(errors);
    }

    return Results.Ok(await investigation.AnswerAsync(
        request.Question!.Trim(), request.TopK, request.DocumentId, cancellationToken));
});

app.MapPost("/api/investigation/report", async (
    SemanticSearchRequest request,
    IncidentReportService investigation,
    SyntheticIngestionService ingestion,
    ILogger<IncidentReportService> logger,
    CancellationToken cancellationToken) =>
{
    var errors = ValidateEvidenceRequest(request, ingestion);
    if (errors.Count > 0)
    {
        logger.LogWarning("Incident report rejected invalid question, topK, or documentId input.");
        return Results.ValidationProblem(errors);
    }

    return Results.Ok(await investigation.CreateAsync(
        request.Question!.Trim(), request.TopK, request.DocumentId, cancellationToken));
});

app.MapPost("/api/investigation/run", async (
    SemanticSearchRequest request,
    InvestigationService investigation,
    SyntheticIngestionService ingestion,
    ILogger<InvestigationService> logger,
    CancellationToken cancellationToken) =>
{
    var errors = ValidateEvidenceRequest(request, ingestion);
    if (errors.Count > 0)
    {
        logger.LogWarning("Investigation rejected invalid question, topK, or documentId input.");
        return Results.ValidationProblem(errors);
    }

    return Results.Ok(await investigation.RunAsync(
        request.Question!.Trim(), request.TopK, request.DocumentId, cancellationToken));
});

app.Logger.LogInformation(
    "Starting IncidentIQ in {Environment}. Synthetic preview, opt-in embeddings, search, grounded answers, and incident reports are available.",
    app.Environment.EnvironmentName);

app.Run();

static Dictionary<string, string[]> ValidateEvidenceRequest(
    SemanticSearchRequest request, SyntheticIngestionService ingestion)
{
    var errors = new Dictionary<string, string[]>();
    if (string.IsNullOrWhiteSpace(request.Question)
        || request.Question.Length > SemanticSearchRequest.MaxQuestionLength)
    {
        errors["question"] = [$"Provide a nonblank question of at most {SemanticSearchRequest.MaxQuestionLength} characters."];
    }

    if (request.TopK < 1 || request.TopK > SemanticSearchRequest.MaxTopK)
    {
        errors["topK"] = [$"topK must be between 1 and {SemanticSearchRequest.MaxTopK}."];
    }

    if (request.DocumentId is not null && !ingestion.ContainsDocument(request.DocumentId))
    {
        errors["documentId"] = ["documentId must match a document ID from GET /api/ingestion/preview, or be omitted to search all documents."];
    }

    return errors;
}
