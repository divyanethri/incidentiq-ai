using IncidentIQ.Api.Features.Ingestion;

namespace IncidentIQ.Api.Features.Embeddings;

public sealed record EmbeddingSnapshot(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    string Endpoint,
    string Deployment,
    string? ModelId,
    int Dimensions,
    int DocumentCount,
    IReadOnlyList<EmbeddedEvidenceChunk> Chunks,
    EmbeddingUsage Usage);

public sealed record EmbeddedEvidenceChunk(EvidenceChunk Evidence, float[] Vector);

public sealed record EmbeddingUsage(long? InputTokens, long? TotalTokens);

public sealed record EmbeddingIngestionResult(
    int DocumentCount,
    int ChunkCount,
    int Dimensions,
    string Deployment,
    string? ModelId,
    EmbeddingUsage Usage,
    decimal? EstimatedCostUsd,
    string StoreFile,
    DateTimeOffset CreatedAt);
