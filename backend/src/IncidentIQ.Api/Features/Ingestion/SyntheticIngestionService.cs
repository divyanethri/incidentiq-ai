namespace IncidentIQ.Api.Features.Ingestion;

public sealed class SyntheticIngestionService(
    DocumentChunker chunker,
    ILogger<SyntheticIngestionService> logger)
{
    private static readonly (string Id, string FileName, string Type)[] Sources =
    [
        ("checkout-timeouts-runbook", "checkout-timeouts-runbook.md", "runbook"),
        ("inc-syn-001-report", "inc-syn-001-report.md", "incident-report"),
        ("inc-syn-002-logs", "inc-syn-002-checkout.jsonl", "application-log")
    ];

    public bool ContainsDocument(string documentId) =>
        Sources.Any(source => source.Id == documentId);

    public async Task<IngestionPreview> PreviewAsync(CancellationToken cancellationToken)
    {
        var documents = new List<IngestedDocument>();
        var chunks = new List<EvidenceChunk>();

        foreach (var source in Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceFile = Path.Combine("data", "synthetic", source.FileName);
            var path = Path.Combine(AppContext.BaseDirectory, sourceFile);
            var lines = await File.ReadAllLinesAsync(path, cancellationToken);
            var documentChunks = chunker.Chunk(source.Id, sourceFile, source.Type, lines);

            documents.Add(new IngestedDocument(
                source.Id, sourceFile, source.Type, lines.Length, documentChunks.Count));
            chunks.AddRange(documentChunks);
        }

        logger.LogInformation(
            "Previewed {DocumentCount} synthetic documents as {ChunkCount} evidence chunks.",
            documents.Count,
            chunks.Count);

        return new IngestionPreview(
            documents, chunks, DocumentChunker.MaxLinesPerChunk, DocumentChunker.OverlapLines);
    }
}
