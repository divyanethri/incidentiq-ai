namespace IncidentIQ.Api.Features.Ingestion;

public sealed record EvidenceChunk(
    string Id,
    string DocumentId,
    string SourceFile,
    string DocumentType,
    int StartLine,
    int EndLine,
    string Content,
    string ContentHash);
