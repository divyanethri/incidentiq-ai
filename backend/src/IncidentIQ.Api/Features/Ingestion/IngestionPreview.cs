namespace IncidentIQ.Api.Features.Ingestion;

public sealed record IngestionPreview(
    IReadOnlyList<IngestedDocument> Documents,
    IReadOnlyList<EvidenceChunk> Chunks,
    int MaxLinesPerChunk,
    int OverlapLines)
{
    public int DocumentCount => Documents.Count;
    public int ChunkCount => Chunks.Count;
}

public sealed record IngestedDocument(
    string DocumentId,
    string SourceFile,
    string DocumentType,
    int LineCount,
    int ChunkCount);
