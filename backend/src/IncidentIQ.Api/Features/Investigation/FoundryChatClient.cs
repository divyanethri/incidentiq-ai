using System.ClientModel;
using System.ClientModel.Primitives;
using IncidentIQ.Api.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace IncidentIQ.Api.Features.Investigation;

public sealed class FoundryChatClient(IOptions<FoundryOptions> options) : IDisposable
{
    public const int MaxOutputTokens = 4096;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);

    private readonly Lazy<IChatClient> _client = new(() => CreateClient(options.Value));

    public void EnsureConfigured() => _ = _client.Value;

    public async Task<ChatResponse> GenerateAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
        string schemaName,
        string schema,
        CancellationToken cancellationToken)
        => await SendAsync(messages, new ChatOptions
        {
            MaxOutputTokens = MaxOutputTokens,
            RawRepresentationFactory = _ => new ChatCompletionOptions
            {
                ResponseFormat = OpenAI.Chat.ChatResponseFormat.CreateJsonSchemaFormat(
                    schemaName, BinaryData.FromString(schema), jsonSchemaIsStrict: true)
            }
        }, cancellationToken);

    internal Task<ChatResponse> SelectToolsAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
        IList<AITool> tools,
        CancellationToken cancellationToken) =>
        SendAsync(messages, new ChatOptions
        {
            MaxOutputTokens = MaxOutputTokens,
            Tools = tools,
            ToolMode = ChatToolMode.Auto,
            AllowMultipleToolCalls = false
        }, cancellationToken);

    private async Task<ChatResponse> SendAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            return await _client.Value.GetResponseAsync(messages, chatOptions, timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GroundedAnswerTimeoutException(exception);
        }
    }

    public void Dispose()
    {
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    private static IChatClient CreateClient(FoundryOptions settings)
    {
        var endpoint = FoundryClientConfiguration.Validate(
            settings, nameof(FoundryOptions.ChatDeployment), settings.ChatDeployment);
        return new ChatClient(
            settings.ChatDeployment.Trim(),
            new ApiKeyCredential(settings.ApiKey.Trim()),
            new OpenAIClientOptions
            {
                Endpoint = endpoint,
                RetryPolicy = new ClientRetryPolicy(maxRetries: 0)
            }).AsIChatClient();
    }
}

public sealed class GroundedAnswerTimeoutException(Exception innerException)
    : Exception("The Foundry chat request exceeded its time limit.", innerException);
