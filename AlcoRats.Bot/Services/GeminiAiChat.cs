using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Logging;

namespace AlcoRats.Bot.Services;

/// <summary>
/// Free-tier Gemini Developer API chat via <c>Google.GenAI</c> (API key from AI Studio).
/// </summary>
sealed class GeminiAiChat : IAIChat
{
    private readonly Configuration _configuration;
    private readonly ILogger<GeminiAiChat> _logger;
    private readonly Lazy<Client> _client;

    public GeminiAiChat(Configuration configuration, ILogger<GeminiAiChat> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _client = new Lazy<Client>(CreateClient);
    }

    public async Task<string> Query(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query must not be empty.", nameof(query));

        var model = string.IsNullOrWhiteSpace(_configuration.GeminiModel)
            ? "gemini-flash-latest"
            : _configuration.GeminiModel.Trim();

        _logger.LogDebug("Gemini Query model={Model}, length={Length}", model, query.Length);

        var response = await _client.Value.Models.GenerateContentAsync(
            model: model,
            contents: query.Trim());

        var text = ExtractText(response);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Gemini returned an empty response.");

        return text.Trim();
    }

    private Client CreateClient()
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "Gemini API key is not configured. Set GeminiApiKey in configuration.json or GEMINI_API_KEY / GeminiApiKey env.");
        }

        return new Client(apiKey: apiKey);
    }

    private string ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_configuration.GeminiApiKey))
            return _configuration.GeminiApiKey.Trim();

        var fromEnv = System.Environment.GetEnvironmentVariable("GEMINI_API_KEY")
            ?? System.Environment.GetEnvironmentVariable("GeminiApiKey");
        return string.IsNullOrWhiteSpace(fromEnv) ? "" : fromEnv.Trim();
    }

    private static string ExtractText(GenerateContentResponse response)
    {
        var parts = response?.Candidates?.FirstOrDefault()?.Content?.Parts;
        if (parts == null || parts.Count == 0)
            return "";

        return string.Concat(parts.Select(p => p?.Text).Where(t => !string.IsNullOrEmpty(t)));
    }
}
