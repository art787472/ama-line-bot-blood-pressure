using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BloodPressureBot.Services;

public class LineService
{
    private readonly HttpClient _http;
    private readonly string _channelSecret;
    private readonly string _accessToken;
    private readonly ILogger<LineService> _logger;

    // Group → time when the bot was @mentioned. Image messages from a group are only
    // processed if there's a recent mention (so we don't OCR every photo in the family chat).
    private readonly ConcurrentDictionary<string, DateTime> _groupSessions = new();
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(10);

    public LineService(IHttpClientFactory factory, IConfiguration config, ILogger<LineService> logger)
    {
        _http = factory.CreateClient();
        _channelSecret = config["Line:ChannelSecret"]
            ?? throw new InvalidOperationException("Line:ChannelSecret is not configured");
        _accessToken = config["Line:ChannelAccessToken"]
            ?? throw new InvalidOperationException("Line:ChannelAccessToken is not configured");
        _logger = logger;
    }

    public bool VerifySignature(string body, string signature)
    {
        if (string.IsNullOrEmpty(signature)) return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_channelSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return Convert.ToBase64String(hash) == signature;
    }

    public async Task ProcessEventsAsync(
        string body,
        GeminiVisionService vision,
        RecordRepository repo)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("events", out var events)) return;

        foreach (var evt in events.EnumerateArray())
        {
            try
            {
                await HandleEventAsync(evt, vision, repo);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to handle event");
            }
        }
    }

    private async Task HandleEventAsync(JsonElement evt, GeminiVisionService vision, RecordRepository repo)
    {
        var type = evt.GetProperty("type").GetString();
        if (type != "message") return;

        var msg = evt.GetProperty("message");
        var msgType = msg.GetProperty("type").GetString();
        var replyToken = evt.GetProperty("replyToken").GetString()!;

        // Group chats use groupId; 1:1 chats use userId.
        // All records in a family group belong to the same person (grandma), so key by
        // groupId so any family member can log a reading.
        var source = evt.GetProperty("source");
        var groupId = source.TryGetProperty("groupId", out var gid) ? gid.GetString() : null;
        var isGroup = groupId is not null;
        var userId = groupId
            ?? (source.TryGetProperty("userId", out var uid) ? uid.GetString()! : "unknown");

        if (msgType == "image")
        {
            // In a group, only process if the bot was @mentioned within the last 10 min.
            if (isGroup && !HasActiveSession(groupId!))
            {
                _logger.LogDebug("Image in group {GroupId} ignored (no active session)", groupId);
                return;
            }

            var messageId = msg.GetProperty("id").GetString()!;
            var (bytes, mediaType) = await DownloadImageAsync(messageId);
            var reading = await vision.ReadAsync(bytes, mediaType);

            if (reading is null)
            {
                await ReplyTextAsync(replyToken, "⚠️ 無法辨識血壓數值,請再試一次(請確保數字清晰可見)");
                return;
            }

            var measuredAt = DateTimeOffset.FromUnixTimeMilliseconds(
                evt.GetProperty("timestamp").GetInt64()).UtcDateTime;

            await repo.InsertAsync(userId, reading, measuredAt);

            var pulseText = reading.Pulse.HasValue ? $"\n❤️ 脈搏: {reading.Pulse} bpm" : "";
            await ReplyTextAsync(replyToken,
                $"✅ 已記錄\n🩺 收縮壓: {reading.Systolic}\n🩺 舒張壓: {reading.Diastolic}{pulseText}");
        }
        else if (msgType == "text")
        {
            // In a group, @mention activates a 10-minute window for image processing.
            if (isGroup && IsBotMentioned(msg))
            {
                _groupSessions[groupId!] = DateTime.UtcNow;
                await ReplyTextAsync(replyToken, "📸 請在 10 分鐘內傳血壓計照片");
                return;
            }

            // Help command works in 1:1 only — group chats stay quiet unless mentioned.
            if (!isGroup)
            {
                var text = msg.GetProperty("text").GetString() ?? "";
                if (text.Trim() is "help" or "說明")
                {
                    await ReplyTextAsync(replyToken,
                        "📸 直接傳血壓計的照片給我,我會自動辨識並記錄數值。");
                }
            }
        }
    }

    private bool HasActiveSession(string groupId)
    {
        return _groupSessions.TryGetValue(groupId, out var ts)
               && DateTime.UtcNow - ts < SessionTtl;
    }

    private static bool IsBotMentioned(JsonElement msg)
    {
        if (!msg.TryGetProperty("mention", out var mention)) return false;
        if (!mention.TryGetProperty("mentionees", out var mentionees)) return false;
        foreach (var m in mentionees.EnumerateArray())
        {
            if (m.TryGetProperty("isSelf", out var isSelf) && isSelf.GetBoolean())
                return true;
        }
        return false;
    }

    private async Task<(byte[] bytes, string mediaType)> DownloadImageAsync(string messageId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://api-data.line.me/v2/bot/message/{messageId}/content");
        req.Headers.Add("Authorization", $"Bearer {_accessToken}");

        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();

        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return (bytes, mediaType);
    }

    private async Task ReplyTextAsync(string replyToken, string text)
    {
        var payload = new
        {
            replyToken,
            messages = new[] { new { type = "text", text } }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post,
            "https://api.line.me/v2/bot/message/reply");
        req.Headers.Add("Authorization", $"Bearer {_accessToken}");
        req.Content = new StringContent(JsonSerializer.Serialize(payload),
            Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            _logger.LogError("LINE reply failed {Status}: {Body}", resp.StatusCode, body);
        }
    }
}
