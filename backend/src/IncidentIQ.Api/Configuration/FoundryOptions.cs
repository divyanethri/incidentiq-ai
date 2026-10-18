namespace IncidentIQ.Api.Configuration;

public sealed class FoundryOptions
{
    public const string SectionName = "Foundry";

    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ChatDeployment { get; set; } = string.Empty;
    public string EmbeddingDeployment { get; set; } = string.Empty;
}
