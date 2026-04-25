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
        You read digits from a home blood pressure monitor's LCD display.

        Return ONLY a JSON object with this exact shape (no markdown, no commentary):
        {"systolic": <int>, "diastolic": <int>, "pulse": <int|null>}

        Reading the display:
        - The display has three stacked numeric fields. From top to bottom they are usually:
          1. SYS / 收縮壓 / SYSTOLIC — the largest, topmost number (typically 80-220)
          2. DIA / 舒張壓 / DIASTOLIC — the middle number (typically 40-130)
          3. PULSE / 脈搏 / 心跳 / ♥ — the bottom number (typically 40-160)
        - If labels (SYS/DIA/PULSE, mmHg, bpm, ♥) are visible, trust the labels over position.
        - Systolic is ALWAYS greater than diastolic. If your reading violates this, re-examine
          the image — you likely swapped the rows or misread a digit (e.g. 1 vs 7, 0 vs 8, 5 vs 6, 3 vs 8).
        - Pulse is often shown smaller, near a heart icon, or alternates on the screen. If only
          two numbers are visible, set pulse to null.

        Handling photo issues:
        - The photo may be taken from an angle, rotated, tilted, or with glare/reflection on the LCD.
          Mentally de-skew the display and read the seven-segment digits as if viewed straight-on.
        - Ignore date/time, memory index (e.g. "M-01"), user icons, battery icons, irregular-heartbeat
          symbols, and unit labels. Only the three vital-sign numbers matter.
        - If the screen is blurry, cropped, glared out, or the digits are ambiguous, do NOT guess —
          return {"error": "<short reason in English>"} instead.

        If the image is not a blood pressure monitor at all, return {"error": "not a BP monitor"}.
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
