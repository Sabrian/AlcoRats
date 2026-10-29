using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

    /// <summary>
    /// Chat-capable free models used when the free router lands on a content-safety classifier
    /// (e.g. <c>nvidia/nemotron-*-content-safety</c> → <c>User Safety: safe</c>).
    /// OpenRouter allows at most 3 entries in the request <c>models</c> array.
    /// </summary>
    private static readonly string[] ChatFallbackModels =
    [
        "google/gemma-4-31b-it:free",
        "google/gemma-4-26b-a4b-it:free",
        "nvidia/nemotron-3-super-120b-a12b:free",
    ];

    private static readonly Regex SafetyLineRegex = new(
        @"^\s*(User Safety|Response Safety|Safety Categories)\s*:.*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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

        var primary = string.IsNullOrWhiteSpace(_configuration.OpenRouterModel)
            ? "openrouter/free"
            : _configuration.OpenRouterModel.Trim();

        var prompt = query.Trim();
        var (text, usedModel) = await CompleteAsync(apiKey, primary, prompt);

        if (IsSafetyClassifierReply(text))
        {
            _logger.LogWarning(
                "OpenRouter model {Model} returned a content-safety verdict instead of a chat reply ({Preview}); retrying with chat models",
                usedModel ?? primary,
                Truncate(text, 80));

            foreach (var fallback in ChatFallbackModels)
            {
                if (string.Equals(fallback, usedModel, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fallback, primary, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    (text, usedModel) = await CompleteAsync(apiKey, fallback, prompt);
                    if (!IsSafetyClassifierReply(text))
                        break;
                    _logger.LogWarning(
                        "Fallback model {Model} also returned a safety verdict: {Preview}",
                        usedModel ?? fallback,
                        Truncate(text, 80));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "OpenRouter fallback model {Model} failed", fallback);
                }
            }

            if (IsSafetyClassifierReply(text))
            {
                throw new InvalidOperationException(
                    "OpenRouter only returned content-safety verdicts (e.g. 'User Safety: safe'). Try a chat model in OpenRouterModel.");
            }
        }

        if (!string.IsNullOrWhiteSpace(usedModel))
            _logger.LogDebug("OpenRouter used model={Model}", usedModel);

        return text.Trim();
    }

    private async Task<(string Text, string Model)> CompleteAsync(string apiKey, string model, string prompt)
    {
        _logger.LogDebug("OpenRouter Query model={Model}, length={Length}", model, prompt.Length);

        using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        // Prefer chat models if the free router is used — OpenRouter tries these after/alongside routing.
        object payload = string.Equals(model, "openrouter/free", StringComparison.OrdinalIgnoreCase)
            ? new
            {
                model,
                models = ChatFallbackModels,
                messages = new[] { new { role = "user", content = prompt } },
            }
            : new
            {
                model,
                messages = new[] { new { role = "user", content = prompt } },
            };

        request.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
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

        return (text.Trim(), parsed.Model);
    }

    /// <summary>
    /// True when the model is a Nemotron Content Safety (or similar) classifier, not a chat LLM.
    /// </summary>
    internal static bool IsSafetyClassifierReply(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim();
        if (!trimmed.Contains("User Safety:", StringComparison.OrdinalIgnoreCase)
            && !trimmed.Contains("Response Safety:", StringComparison.OrdinalIgnoreCase))
            return false;

        // Strip classifier lines / optional think block — if nothing remains, it's not a chat answer.
        var remainder = Regex.Replace(
            trimmed,
            @"<think>[\s\S]*?</think>",
            " ",
            RegexOptions.IgnoreCase);
        remainder = SafetyLineRegex.Replace(remainder, " ");
        remainder = Regex.Replace(remainder, @"\s+", " ").Trim();
        return remainder.Length < 8;
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
