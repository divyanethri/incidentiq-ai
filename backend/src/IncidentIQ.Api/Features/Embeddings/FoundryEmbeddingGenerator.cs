using System.ClientModel;
using IncidentIQ.Api.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Embeddings;

namespace IncidentIQ.Api.Features.Embeddings;

public sealed class FoundryEmbeddingGenerator(IOptions<FoundryOptions> options) : IDisposable
{
    private readonly Lazy<IEmbeddingGenerator<string, Embedding<float>>> _generator =
        new(() => CreateGenerator(options.Value));

    public void EnsureConfigured() => _ = _generator.Value;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        CancellationToken cancellationToken) =>
        _generator.Value.GenerateAsync(values, cancellationToken: cancellationToken);

    public void Dispose()
    {
        if (_generator.IsValueCreated)
        {
            _generator.Value.Dispose();
        }
    }

    private static IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(FoundryOptions settings)
    {
        var endpoint = FoundryClientConfiguration.Validate(
            settings, nameof(FoundryOptions.EmbeddingDeployment), settings.EmbeddingDeployment);
        var client = new EmbeddingClient(
            settings.EmbeddingDeployment.Trim(),
            new ApiKeyCredential(settings.ApiKey.Trim()),
            new OpenAIClientOptions
            {
                Endpoint = endpoint
            });

        return client.AsIEmbeddingGenerator();
    }
}
