using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BloodPressureBot.Models;

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

    // userId/groupId → pending manual-edit state. Set when user taps "修正" on the confirm card;
    // the next text message that looks like numbers gets saved with the original measuredAt.
    private readonly ConcurrentDictionary<string, PendingEdit> _pendingEdits = new();
    private static readonly TimeSpan EditTtl = TimeSpan.FromMinutes(5);

    // Postback dedupe: prevent double-tap from inserting twice.
    private readonly ConcurrentDictionary<string, DateTime> _processedPostbacks = new();

    private enum EditStep { Systolic, Diastolic, Pulse }

    private record PendingEdit(
        DateTime MeasuredAt,
        DateTime ExpiresAt,
        EditStep Step,
        int? Systolic,
        int? Diastolic);

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

        // Source identity (groupId for group events, userId for 1:1)
        var source = evt.GetProperty("source");
        var groupId = source.TryGetProperty("groupId", out var gid) ? gid.GetString() : null;
        var isGroup = groupId is not null;
        var userKey = groupId
            ?? (source.TryGetProperty("userId", out var uid) ? uid.GetString()! : "unknown");

        if (type == "postback")
        {
            await HandlePostbackAsync(evt, repo, userKey, groupId);
            return;
        }

        if (type != "message") return;

        var msg = evt.GetProperty("message");
        var msgType = msg.GetProperty("type").GetString();
        var replyToken = evt.GetProperty("replyToken").GetString()!;

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
                await ReplyTextAsync(replyToken, "⚠️ 無法辨識血壓數值,請傳清晰一點的照片再試");
                return;
            }

            var timestampMs = evt.GetProperty("timestamp").GetInt64();
            var flex = BuildConfirmFlex(reading, timestampMs);
            await ReplyAsync(replyToken, flex);
        }
        else if (msgType == "text")
        {
            var text = msg.GetProperty("text").GetString() ?? "";

            // Edit mode: if user tapped "修正" recently, walk through systolic → diastolic → pulse.
            if (_pendingEdits.TryGetValue(userKey, out var pending)
                && DateTime.UtcNow < pending.ExpiresAt)
            {
                await HandleEditTextAsync(userKey, pending, text, replyToken, repo);
                return;
            }

            // Group: @mention activates a 10-minute window for image processing.
            if (isGroup && IsBotMentioned(msg))
            {
                _groupSessions[groupId!] = DateTime.UtcNow;
                await ReplyTextAsync(replyToken, "📸 請在 10 分鐘內傳血壓計照片");
                return;
            }

            // Help command works in 1:1 only — group chats stay quiet unless mentioned.
            if (!isGroup && text.Trim() is "help" or "說明")
            {
                await ReplyTextAsync(replyToken,
                    "📸 直接傳血壓計的照片給我,我會自動辨識並請你確認後記錄。");
            }
        }
    }

    private async Task HandlePostbackAsync(
        JsonElement evt, RecordRepository repo, string userKey, string? groupId)
    {
        var replyToken = evt.GetProperty("replyToken").GetString()!;
        var data = evt.GetProperty("postback").GetProperty("data").GetString() ?? "";
        var p = ParseQueryString(data);
        var action = p.GetValueOrDefault("a");

        // Dedupe: same postback (identified by timestamp) tapped multiple times → only first counts.
        if (p.TryGetValue("t", out var t))
        {
            var dedupeKey = $"{userKey}:{t}:{action}";
            if (!_processedPostbacks.TryAdd(dedupeKey, DateTime.UtcNow))
            {
                await ReplyTextAsync(replyToken, "已處理過了 ✓");
                return;
            }
        }

        switch (action)
        {
            case "c":  // confirm
            {
                var s = int.Parse(p["s"]);
                var d = int.Parse(p["d"]);
                int? pulse = p.TryGetValue("p", out var pp) ? int.Parse(pp) : null;
                var measuredAt = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(p["t"])).UtcDateTime;

                await repo.InsertAsync(userKey, new BloodPressureReading(s, d, pulse), measuredAt);
                await ReplyTextAsync(replyToken, FormatSavedMessage(s, d, pulse, "已記錄"));
                break;
            }
            case "e":  // edit
            {
                var measuredAt = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(p["t"])).UtcDateTime;
                _pendingEdits[userKey] = new PendingEdit(
                    measuredAt, DateTime.UtcNow.Add(EditTtl), EditStep.Systolic, null, null);
                await ReplyTextAsync(replyToken, "✏️ 開始修正\n\n收縮壓多少?(輸入數字)");
                break;
            }
            case "r":  // retry
            {
                if (groupId is not null) _groupSessions[groupId] = DateTime.UtcNow;
                await ReplyTextAsync(replyToken, "📷 請重傳清晰一點的血壓計照片");
                break;
            }
            default:
                _logger.LogWarning("Unknown postback action: {Data}", data);
                break;
        }
    }

    private bool HasActiveSession(string groupId) =>
        _groupSessions.TryGetValue(groupId, out var ts) && DateTime.UtcNow - ts < SessionTtl;

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

    private async Task HandleEditTextAsync(
        string userKey, PendingEdit pending, string text, string replyToken, RecordRepository repo)
    {
        var num = ExtractNumber(text);

        switch (pending.Step)
        {
            case EditStep.Systolic:
                if (num is null || num < 50 || num > 300)
                {
                    await ReplyTextAsync(replyToken, "請輸入收縮壓數字 (50-300),例如 128");
                    return;
                }
                _pendingEdits[userKey] = pending with { Step = EditStep.Diastolic, Systolic = num };
                await ReplyTextAsync(replyToken, $"收縮壓: {num} ✓\n\n舒張壓多少?(輸入數字)");
                break;

            case EditStep.Diastolic:
                var sys = pending.Systolic!.Value;
                if (num is null || num < 30 || num > 200 || num >= sys)
                {
                    await ReplyTextAsync(replyToken,
                        $"請輸入舒張壓數字 (30-{sys - 1}),例如 82");
                    return;
                }
                _pendingEdits[userKey] = pending with { Step = EditStep.Pulse, Diastolic = num };
                await ReplyTextAsync(replyToken, $"舒張壓: {num} ✓\n\n脈搏多少?(沒有就傳 - )");
                break;

            case EditStep.Pulse:
                int? pulse = null;
                var trimmed = text.Trim();
                var skip = trimmed is "-" or "無" or "沒有" or "skip" or "no";
                if (!skip)
                {
                    if (num is null || num < 30 || num > 250)
                    {
                        await ReplyTextAsync(replyToken,
                            "請輸入脈搏數字 (30-250),沒有就傳 -");
                        return;
                    }
                    pulse = num;
                }

                _pendingEdits.TryRemove(userKey, out _);
                var s = pending.Systolic!.Value;
                var d = pending.Diastolic!.Value;
                await repo.InsertAsync(userKey, new BloodPressureReading(s, d, pulse), pending.MeasuredAt);
                await ReplyTextAsync(replyToken, FormatSavedMessage(s, d, pulse, "已記錄(手動修正)"));
                break;
        }
    }

    private static int? ExtractNumber(string text)
    {
        var m = Regex.Match(text, @"\d{2,3}");
        return m.Success && int.TryParse(m.Value, out var n) ? n : null;
    }

    private static Dictionary<string, string> ParseQueryString(string data)
    {
        var result = new Dictionary<string, string>();
        foreach (var pair in data.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2) result[parts[0]] = parts[1];
        }
        return result;
    }

    private static string FormatSavedMessage(int s, int d, int? pulse, string verb)
    {
        var pulseText = pulse.HasValue ? $"\n❤️ 脈搏: {pulse} bpm" : "";
        return $"✅ {verb}\n🩺 收縮壓: {s}\n🩺 舒張壓: {d}{pulseText}";
    }

    private static object BuildConfirmFlex(BloodPressureReading r, long timestampMs)
    {
        var status = (r.Systolic >= 140 || r.Diastolic >= 90) ? ("偏高", "#dc2626")
                   : (r.Systolic >= 130 || r.Diastolic >= 85) ? ("正常高值", "#f59e0b")
                   : ("正常", "#10b981");

        var pulseStr = r.Pulse?.ToString() ?? "—";
        var pulsePart = r.Pulse.HasValue ? $"&p={r.Pulse}" : "";
        var dataConfirm = $"a=c&s={r.Systolic}&d={r.Diastolic}{pulsePart}&t={timestampMs}";
        var dataEdit = $"a=e&s={r.Systolic}&d={r.Diastolic}{pulsePart}&t={timestampMs}";

        object Row(string label, string value, string? color = null) => new
        {
            type = "box",
            layout = "horizontal",
            contents = new object[]
            {
                new { type = "text", text = label, color = "#888888", flex = 2, size = "sm" },
                new
                {
                    type = "text",
                    text = value,
                    weight = "bold",
                    flex = 3,
                    size = "lg",
                    color = color ?? "#111111"
                }
            }
        };

        return new
        {
            type = "flex",
            altText = $"血壓辨識: {r.Systolic}/{r.Diastolic} 請確認",
            contents = new
            {
                type = "bubble",
                size = "kilo",
                header = new
                {
                    type = "box",
                    layout = "vertical",
                    contents = new object[]
                    {
                        new { type = "text", text = "🩺 血壓辨識結果", weight = "bold", size = "lg" },
                        new { type = "text", text = "請確認數值是否正確", size = "xs", color = "#888888" }
                    }
                },
                body = new
                {
                    type = "box",
                    layout = "vertical",
                    spacing = "md",
                    contents = new object[]
                    {
                        Row("收縮壓", r.Systolic.ToString()),
                        Row("舒張壓", r.Diastolic.ToString()),
                        Row("脈搏", pulseStr),
                        new { type = "separator", margin = "sm" },
                        Row("狀態", status.Item1, status.Item2)
                    }
                },
                footer = new
                {
                    type = "box",
                    layout = "vertical",
                    spacing = "sm",
                    contents = new object[]
                    {
                        new
                        {
                            type = "button",
                            style = "primary",
                            color = "#10b981",
                            action = new { type = "postback", label = "✅ 確認", data = dataConfirm, displayText = "確認" }
                        },
                        new
                        {
                            type = "button",
                            style = "secondary",
                            action = new { type = "postback", label = "✏️ 修正", data = dataEdit, displayText = "修正" }
                        },
                        new
                        {
                            type = "button",
                            style = "link",
                            action = new { type = "postback", label = "📷 重傳", data = "a=r", displayText = "重傳" }
                        }
                    }
                }
            }
        };
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

    private Task ReplyTextAsync(string replyToken, string text) =>
        ReplyAsync(replyToken, new { type = "text", text });

    private async Task ReplyAsync(string replyToken, object message)
    {
        var payload = new { replyToken, messages = new[] { message } };

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
