using System.Text;
using System.Text.Json;
using BloodPressureBot.Models;

namespace BloodPressureBot.Services;

public class GeminiVisionService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger<GeminiVisionService> _logger;

    private const string Prompt = """
        You read blood pressure monitor displays. Return ONLY a JSON object with this exact shape:
        {"systolic": <int>, "diastolic": <int>, "pulse": <int|null>}
        - systolic is the larger (upper) number (typically 90-200)
        - diastolic is the smaller (lower) number (typically 50-120)
        - pulse is the heart rate if shown, else null
        If you cannot read the values with confidence, return {"error": "<reason>"}.
        Do not include markdown fences, commentary, or any other text.
        """;

    public GeminiVisionService(IHttpClientFactory factory, IConfiguration config, ILogger<GeminiVisionService> logger)
    {
        _http = factory.CreateClient();
        _apiKey = config["Gemini:ApiKey"]
            ?? throw new InvalidOperationException("Gemini:ApiKey is not configured");
        _model = config["Gemini:Model"] ?? "gemini-2.5-flash";
        _logger = logger;
    }

    public async Task<BloodPressureReading?> ReadAsync(byte[] imageBytes, string mediaType)
    {
        var base64 = Convert.ToBase64String(imageBytes);
        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = Prompt },
                        new { inline_data = new { mime_type = mediaType, data = base64 } }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.0,
                maxOutputTokens = 512,
                thinkingConfig = new { thinkingBudget = 0 }
            }
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{_model}:generateContent?key={_apiKey}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("Gemini API error {Status}: {Body}", resp.StatusCode, body);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString() ?? "";

            _logger.LogDebug("Gemini response: {Text}", text);

            // Gemini sometimes wraps JSON in ```json fences
            text = text.Trim();
            if (text.StartsWith("```"))
            {
                var firstNewline = text.IndexOf('\n');
                if (firstNewline > 0) text = text[(firstNewline + 1)..];
                if (text.EndsWith("```")) text = text[..^3];
                text = text.Trim();
            }

            using var parsed = JsonDocument.Parse(text);
            if (parsed.RootElement.TryGetProperty("error", out _)) return null;

            var systolic = parsed.RootElement.GetProperty("systolic").GetInt32();
            var diastolic = parsed.RootElement.GetProperty("diastolic").GetInt32();
            int? pulse = parsed.RootElement.TryGetProperty("pulse", out var p) && p.ValueKind == JsonValueKind.Number
                ? p.GetInt32() : null;

            return new BloodPressureReading(systolic, diastolic, pulse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Gemini response: {Body}", body);
            return null;
        }
    }
}
