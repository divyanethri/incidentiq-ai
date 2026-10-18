using System.Text.Json;

namespace IncidentIQ.Api.Features.Embeddings;

public sealed class LocalEmbeddingStore(IHostEnvironment environment)
{
    public const int SchemaVersion = 1;
    public static string StoreFile => Path.Combine(".local", "embeddings", "synthetic.json");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _storePath = Path.Combine(environment.ContentRootPath, StoreFile);

    public async Task<EmbeddingSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                _storePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                bufferSize: 65536, useAsync: true);
            return await JsonSerializer.DeserializeAsync<EmbeddingSnapshot>(
                stream, JsonOptions, cancellationToken)
                ?? throw new EmbeddingSnapshotException("The local embedding snapshot is empty.");
        }
        catch (FileNotFoundException exception)
        {
            throw new EmbeddingSnapshotException("The local embedding snapshot does not exist.", exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new EmbeddingSnapshotException("The local embedding snapshot does not exist.", exception);
        }
        catch (JsonException exception)
        {
            throw new EmbeddingSnapshotException("The local embedding snapshot is not valid JSON.", exception);
        }
    }

    public async Task SaveAsync(EmbeddingSnapshot snapshot, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(environment.ContentRootPath, ".local", "embeddings");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"synthetic.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 65536, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _storePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}

public sealed class EmbeddingSnapshotException(string message, Exception? innerException = null)
    : Exception(message, innerException);
