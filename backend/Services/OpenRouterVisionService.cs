using System.Text;
using System.Text.Json;
using BloodPressureBot.Models;

namespace BloodPressureBot.Services;

// OpenRouter aggregates many LLMs behind one OpenAI-compatible endpoint.
// Key advantages over hitting Gemini/Claude directly:
//   - Single billing portal that takes payment methods Anthropic's billing rejects
//   - Picks region-friendly upstreams, sidestepping the geo-block we hit on Gemini
//   - Free tier on several vision models (good enough for BP-monitor OCR)
public class OpenRouterVisionService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger<OpenRouterVisionService> _logger;

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

    public OpenRouterVisionService(IHttpClientFactory factory, IConfiguration config, ILogger<OpenRouterVisionService> logger)
    {
        _http = factory.CreateClient();
        _apiKey = config["OpenRouter:ApiKey"]
            ?? throw new InvalidOperationException("OpenRouter:ApiKey is not configured");
        // Default to Google's free Gemini 2.0 Flash variant — solid OCR, no cost, accessible from TW via OpenRouter.
        _model = config["OpenRouter:Model"] ?? "google/gemini-2.0-flash-exp:free";
        _logger = logger;
    }

    public async Task<BloodPressureReading?> ReadAsync(byte[] imageBytes, string mediaType)
    {
        var base64 = Convert.ToBase64String(imageBytes);
        var dataUrl = $"data:{mediaType};base64,{base64}";

        var payload = new
        {
            model = _model,
            max_tokens = 256,
            temperature = 0.0,
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = Prompt },
                        new { type = "image_url", image_url = new { url = dataUrl } }
                    }
                }
            }
        };

        var url = "https://openrouter.ai/api/v1/chat/completions";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Authorization", $"Bearer {_apiKey}");
        // OpenRouter recommends these headers — used for site-level analytics on their dashboard.
        req.Headers.Add("HTTP-Referer", "https://github.com/blood-pressure-bot");
        req.Headers.Add("X-Title", "Blood Pressure Bot");

        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("OpenRouter API error {Status}: {Body}", resp.StatusCode, body);
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            _logger.LogDebug("OpenRouter response: {Text}", text);

            // LLMs sometimes wrap JSON in ```json fences
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
            _logger.LogError(ex, "Failed to parse OpenRouter response: {Body}", body);
            return null;
        }
    }
}
