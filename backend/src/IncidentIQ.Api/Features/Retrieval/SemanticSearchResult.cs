using IncidentIQ.Api.Features.Embeddings;
using IncidentIQ.Api.Features.Ingestion;

namespace IncidentIQ.Api.Features.Retrieval;

public sealed record SemanticSearchRequest(string? Question, int TopK = 3, string? DocumentId = null)
{
    public const int MaxQuestionLength = 2000;
    public const int MaxTopK = 10;
}

public sealed record SemanticSearchResult(
    string Question,
    int RequestedTopK,
    string? DocumentId,
    int IndexedChunkCount,
    int SearchedChunkCount,
    string Deployment,
    string? ModelId,
    int Dimensions,
    DateTimeOffset SnapshotCreatedAt,
    EmbeddingUsage Usage,
    decimal? EstimatedCostUsd,
    IReadOnlyList<SemanticSearchMatch> Matches)
{
    public string ScoreMetric => "cosineSimilarity";
    public int MatchCount => Matches.Count;
}

public sealed record SemanticSearchMatch(int Rank, double Score, EvidenceChunk Evidence);
