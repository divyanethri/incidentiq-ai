using Microsoft.Extensions.Options;

namespace IncidentIQ.Api.Configuration;

internal static class FoundryClientConfiguration
{
    public static Uri Validate(FoundryOptions settings, string deploymentSetting, string deployment)
    {
        var errors = new List<string>();
        var endpointText = settings.Endpoint?.Trim() ?? string.Empty;
        if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps
            || !(endpoint.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase)
                || endpoint.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(endpoint.UserInfo)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || endpoint.AbsolutePath.TrimEnd('/') != "/openai/v1")
        {
            errors.Add("Foundry:Endpoint must be an Azure OpenAI HTTPS endpoint ending in /openai/v1/.");
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            errors.Add("Foundry:ApiKey is required to call a model.");
        }

        if (string.IsNullOrWhiteSpace(deployment))
        {
            errors.Add($"Foundry:{deploymentSetting} is required to call this model.");
        }

        if (errors.Count > 0)
        {
            throw new OptionsValidationException(Options.DefaultName, typeof(FoundryOptions), errors);
        }

        return new Uri(endpointText.TrimEnd('/') + "/");
    }
}
