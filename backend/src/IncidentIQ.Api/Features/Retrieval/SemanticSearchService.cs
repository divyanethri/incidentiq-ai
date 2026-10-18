using IncidentIQ.Api.Configuration;
using IncidentIQ.Api.Features.Embeddings;
using IncidentIQ.Api.Features.Ingestion;
using Microsoft.Extensions.Options;

namespace IncidentIQ.Api.Features.Retrieval;

public sealed class SemanticSearchService(
    LocalEmbeddingStore store,
    SyntheticIngestionService ingestion,
    FoundryEmbeddingGenerator generator,
    IOptions<FoundryOptions> options,
    ILogger<SemanticSearchService> logger)
{
    public async Task<SemanticSearchResult> SearchAsync(
        string question,
        int topK,
        string? documentId,
        CancellationToken cancellationToken)
    {
        generator.EnsureConfigured();
        var snapshot = await store.ReadAsync(cancellationToken);
        var currentEvidence = await ingestion.PreviewAsync(cancellationToken);
        ValidateSnapshot(snapshot, currentEvidence);

        var candidates = snapshot.Chunks
            .Where(chunk => documentId is null || chunk.Evidence.DocumentId == documentId)
            .ToArray();
        var generated = await generator.GenerateAsync([question], cancellationToken);
        var usage = new EmbeddingUsage(
            generated.Usage?.InputTokenCount, generated.Usage?.TotalTokenCount);
        logger.LogInformation(
            "Generated a search embedding using {InputTokens} input tokens and {TotalTokens} total tokens.",
            usage.InputTokens, usage.TotalTokens);

        if (generated.Count != 1)
        {
            throw new InvalidDataException("Foundry did not return exactly one question embedding.");
        }

        var query = generated[0];
        if (query.Vector.Length != snapshot.Dimensions || query.ModelId != snapshot.ModelId)
        {
            throw new EmbeddingSnapshotException("The question embedding no longer matches the stored model or dimensions.");
        }

        var queryVector = query.Vector.ToArray();
        _ = VectorMath.Norm(queryVector);
        var matches = candidates
            .Select(chunk =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new { chunk.Evidence, Score = VectorMath.CosineSimilarity(queryVector, chunk.Vector) };
            })
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Evidence.Id, StringComparer.Ordinal)
            .Take(topK)
            .Select((match, index) => new SemanticSearchMatch(index + 1, match.Score, match.Evidence))
            .ToArray();

        logger.LogInformation(
            "Semantic search returned {MatchCount} matches from {ChunkCount} evidence chunks.",
            matches.Length, candidates.Length);

        return new SemanticSearchResult(
            question, topK, documentId, snapshot.Chunks.Count, candidates.Length, snapshot.Deployment, query.ModelId,
            snapshot.Dimensions, snapshot.CreatedAt, usage, null, matches);
    }

    private void ValidateSnapshot(EmbeddingSnapshot snapshot, IngestionPreview currentEvidence)
    {
        var settings = options.Value;
        var configuredEndpoint = new Uri(settings.Endpoint.Trim().TrimEnd('/') + "/");
        if (snapshot.SchemaVersion != LocalEmbeddingStore.SchemaVersion
            || !Uri.TryCreate(snapshot.Endpoint?.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var storedEndpoint)
            || storedEndpoint != configuredEndpoint
            || snapshot.Deployment != settings.EmbeddingDeployment.Trim()
            || string.IsNullOrWhiteSpace(snapshot.ModelId)
            || snapshot.Dimensions <= 0)
        {
            throw new EmbeddingSnapshotException("The snapshot schema or embedding configuration does not match this application.");
        }

        if (snapshot.Chunks is null
            || snapshot.Chunks.Count != currentEvidence.ChunkCount
            || snapshot.DocumentCount != currentEvidence.DocumentCount)
        {
            throw new EmbeddingSnapshotException("The stored evidence no longer matches the current synthetic documents.");
        }

        var currentChunks = currentEvidence.Chunks.ToDictionary(chunk => chunk.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in snapshot.Chunks)
        {
            if (chunk?.Evidence is not { } evidence
                || string.IsNullOrWhiteSpace(evidence.Id)
                || !seen.Add(evidence.Id)
                || !currentChunks.TryGetValue(evidence.Id, out var currentChunk)
                || evidence != currentChunk)
            {
                throw new EmbeddingSnapshotException("Stored evidence is stale, duplicated, or has invalid citation metadata.");
            }

            if (chunk.Vector is null || chunk.Vector.Length != snapshot.Dimensions)
            {
                throw new EmbeddingSnapshotException("Stored vector dimensions are inconsistent.");
            }

            try
            {
                _ = VectorMath.Norm(chunk.Vector);
            }
            catch (InvalidDataException exception)
            {
                throw new EmbeddingSnapshotException("A stored vector contains invalid values.", exception);
            }
        }
    }
}
