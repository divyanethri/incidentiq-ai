using IncidentIQ.Api.Configuration;
using IncidentIQ.Api.Features.Ingestion;
using Microsoft.Extensions.Options;

namespace IncidentIQ.Api.Features.Embeddings;

public sealed class EmbeddingIngestionService(
    SyntheticIngestionService ingestion,
    FoundryEmbeddingGenerator generator,
    IOptions<FoundryOptions> options,
    LocalEmbeddingStore store,
    ILogger<EmbeddingIngestionService> logger) : IDisposable
{
    private readonly SemaphoreSlim _ingestionLock = new(1, 1);

    public async Task<EmbeddingIngestionResult> IngestAsync(CancellationToken cancellationToken)
    {
        await _ingestionLock.WaitAsync(cancellationToken);
        try
        {
            var preview = await ingestion.PreviewAsync(cancellationToken);
            var generated = await generator.GenerateAsync(
                preview.Chunks.Select(chunk => chunk.Content), cancellationToken);
            var usage = new EmbeddingUsage(
                generated.Usage?.InputTokenCount, generated.Usage?.TotalTokenCount);

            logger.LogInformation(
                "Foundry returned {EmbeddingCount} embeddings using {InputTokens} input tokens and {TotalTokens} total tokens.",
                generated.Count, usage.InputTokens, usage.TotalTokens);

            if (generated.Count == 0 || generated.Count != preview.ChunkCount)
            {
                throw new InvalidDataException("Foundry did not return one embedding for every evidence chunk.");
            }

            var dimensions = generated[0].Vector.Length;
            var modelId = generated[0].ModelId;
            var chunks = new List<EmbeddedEvidenceChunk>(preview.ChunkCount);
            for (var index = 0; index < generated.Count; index++)
            {
                var embedding = generated[index];
                var vector = embedding.Vector.ToArray();
                if (dimensions == 0
                    || vector.Length != dimensions
                    || embedding.ModelId != modelId)
                {
                    throw new InvalidDataException("Foundry returned inconsistent or invalid embedding vectors.");
                }

                _ = VectorMath.Norm(vector);
                chunks.Add(new EmbeddedEvidenceChunk(preview.Chunks[index], vector));
            }

            var settings = options.Value;
            var snapshot = new EmbeddingSnapshot(
                LocalEmbeddingStore.SchemaVersion,
                DateTimeOffset.UtcNow,
                settings.Endpoint.Trim().TrimEnd('/') + "/",
                settings.EmbeddingDeployment.Trim(),
                modelId,
                dimensions,
                preview.DocumentCount,
                chunks,
                usage);

            await store.SaveAsync(snapshot, cancellationToken);

            logger.LogInformation(
                "Saved {ChunkCount} evidence embeddings with {Dimensions} dimensions to {StoreFile}.",
                chunks.Count, dimensions, LocalEmbeddingStore.StoreFile);

            return new EmbeddingIngestionResult(
                preview.DocumentCount, chunks.Count, dimensions, snapshot.Deployment,
                modelId, usage, null, LocalEmbeddingStore.StoreFile, snapshot.CreatedAt);
        }
        finally
        {
            _ingestionLock.Release();
        }
    }

    public void Dispose() => _ingestionLock.Dispose();
}
