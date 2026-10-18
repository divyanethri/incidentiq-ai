using System.Security.Cryptography;
using System.Text;

namespace IncidentIQ.Api.Features.Ingestion;

public sealed class DocumentChunker
{
    public const int MaxLinesPerChunk = 12;
    public const int OverlapLines = 2;

    public IReadOnlyList<EvidenceChunk> Chunk(
        string documentId,
        string sourceFile,
        string documentType,
        string[] lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentNullException.ThrowIfNull(lines);

        if (lines.Length == 0 || lines.All(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException($"Synthetic document '{sourceFile}' contains no evidence.");
        }

        var chunks = new List<EvidenceChunk>();

        for (var start = 0; start < lines.Length; start += MaxLinesPerChunk - OverlapLines)
        {
            var end = Math.Min(start + MaxLinesPerChunk, lines.Length);
            // Canonical newlines keep content hashes stable across Windows and Linux.
            chunks.Add(CreateEvidence(documentId, sourceFile, documentType, start + 1, lines[start..end]));

            if (end == lines.Length)
            {
                break;
            }
        }

        return chunks;
    }

    internal static EvidenceChunk CreateEvidence(
        string documentId, string sourceFile, string documentType, int startLine, string[] lines)
    {
        var content = string.Join("\n", lines);
        var contentHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var endLine = startLine + lines.Length - 1;
        return new EvidenceChunk(
            $"{documentId}:{startLine}-{endLine}:{contentHash}",
            documentId, sourceFile, documentType, startLine, endLine, content, contentHash);
    }
}
