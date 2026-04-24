using BloodPressureBot.Services;

var builder = WebApplication.CreateBuilder(args);

// Render / Railway / Fly provide $PORT — listen on it if present
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

// Override config from env vars (for hosting platforms that only expose env)
// Convention: Line__ChannelSecret, Anthropic__ApiKey, Database__ConnectionString, etc.
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddHttpClient();
builder.Services.AddSingleton<LineService>();
builder.Services.AddSingleton<GeminiVisionService>();
builder.Services.AddSingleton<RecordRepository>();

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? new[] { "http://localhost:5173" };
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod());
});

var app = builder.Build();

app.UseCors();

// Initialise DB schema at startup (idempotent)
await app.Services.GetRequiredService<RecordRepository>().EnsureSchemaAsync();

app.MapGet("/", () => Results.Ok(new { status = "ok", service = "blood-pressure-bot" }));

// LINE webhook — must respond 200 quickly, so process events in the background
app.MapPost("/webhook", async (
    HttpRequest req,
    LineService line,
    GeminiVisionService vision,
    RecordRepository repo,
    ILogger<Program> logger) =>
{
    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    var signature = req.Headers["x-line-signature"].ToString();

    if (!line.VerifySignature(body, signature))
    {
        logger.LogWarning("Invalid LINE signature");
        return Results.Unauthorized();
    }

    _ = Task.Run(async () =>
    {
        try { await line.ProcessEventsAsync(body, vision, repo); }
        catch (Exception ex) { logger.LogError(ex, "Background event processing failed"); }
    });

    return Results.Ok();
});

app.MapGet("/api/records", async (RecordRepository repo, string? userId) =>
    Results.Ok(await repo.GetRecordsAsync(userId)));

app.MapGet("/api/stats", async (RecordRepository repo, string? userId, int? days) =>
    Results.Ok(await repo.GetStatsAsync(userId, days ?? 30)));

app.Run();
