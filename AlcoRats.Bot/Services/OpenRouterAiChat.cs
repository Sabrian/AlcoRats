using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AlcoRats.Bot.Services;

/// <summary>
/// OpenRouter OpenAI-compatible chat (<c>https://openrouter.ai/api/v1/chat/completions</c>).
/// </summary>
sealed class OpenRouterAiChat : IAIChat, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Configuration _configuration;
    private readonly ILogger<OpenRouterAiChat> _logger;
    private readonly HttpClient _http;

    public OpenRouterAiChat(Configuration configuration, ILogger<OpenRouterAiChat> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://openrouter.ai/api/v1/"),
            Timeout = TimeSpan.FromSeconds(90),
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", "https://github.com/alcoRats/AlcoRats.Bot");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", "AlcoRats.Bot");
    }

    public async Task<string> Query(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Query must not be empty.", nameof(query));

        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "OpenRouter API key is not configured. Set OpenRouterKey in configuration.json or OpenRouterKey / OPENROUTER_API_KEY env.");
        }

        var model = string.IsNullOrWhiteSpace(_configuration.OpenRouterModel)
            ? "openrouter/free"
            : _configuration.OpenRouterModel.Trim();

        _logger.LogDebug("OpenRouter Query model={Model}, length={Length}", model, query.Length);

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                model,
                messages = new[]
                {
                    new { role = "user", content = query.Trim() },
                },
            }, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var apiMessage = TryReadErrorMessage(body);
            _logger.LogWarning(
                "OpenRouter HTTP {Status}: {Body}",
                (int)response.StatusCode,
                Truncate(body, 500));
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(apiMessage)
                    ? $"OpenRouter request failed with HTTP {(int)response.StatusCode}."
                    : $"OpenRouter HTTP {(int)response.StatusCode}: {apiMessage}");
        }

        ChatCompletionResponse parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ChatCompletionResponse>(body, JsonOptions);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("OpenRouter returned invalid JSON.", ex);
        }

        var text = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("OpenRouter returned an empty response.");

        if (!string.IsNullOrWhiteSpace(parsed.Model))
            _logger.LogDebug("OpenRouter used model={Model}", parsed.Model);

        return text.Trim();
    }

    private string ResolveApiKey()
    {
        var raw = _configuration.OpenRouterKey;
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
                ?? Environment.GetEnvironmentVariable("OpenRouterKey");
        }

        return NormalizeApiKey(raw);
    }

    private static string NormalizeApiKey(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var key = raw.Trim().Trim('"').Trim('\'');
        if (key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            key = key["Bearer ".Length..].Trim();
        return key;
    }

    private static string TryReadErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var msg)
                    && msg.ValueKind == JsonValueKind.String)
                    return msg.GetString() ?? "";
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString() ?? "";
            }
        }
        catch
        {
            // ignore parse failures
        }

        return "";
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
            return value ?? "";
        return value[..max] + "…";
    }

    public void Dispose() => _http.Dispose();

    private sealed class ChatCompletionResponse
    {
        public string Model { get; set; }
        public List<Choice> Choices { get; set; }
    }

    private sealed class Choice
    {
        public Message Message { get; set; }
    }

    private sealed class Message
    {
        public string Role { get; set; }
        public string Content { get; set; }
    }
}
