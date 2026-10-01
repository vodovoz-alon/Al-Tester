using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddUserSecrets<Program>(optional: true);
var app = builder.Build();

var cfg = app.Configuration;
string? apiKey        = cfg["ANTHROPIC_API_KEY"];
string  model         = cfg["ANTHROPIC_MODEL"] ?? "claude-haiku-4-5-20251001";
string  appUser       = cfg["APP_USER"] ?? "נוגה";
string  appPass       = cfg["APP_PASS"] ?? "סהר";
string  secret        = string.IsNullOrEmpty(cfg["SESSION_SECRET"])
                            ? Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
                            : cfg["SESSION_SECRET"]!;
string  allowedOrigin = cfg["ALLOWED_ORIGIN"] ?? "";
int     maxTokens     = int.TryParse(cfg["MAX_TOKENS"], out var mt) ? mt : 4096;

// Cookie value: HMAC of the username with a server secret (never leaves the server).
string token = Hmac(secret, appUser);

var http      = new HttpClient();
var apiHits   = new ConcurrentDictionary<string, (long t, int c)>();
var loginHits = new ConcurrentDictionary<string, (long t, int c)>();

bool IsAuthed(HttpRequest req) => req.Cookies.TryGetValue("auth", out var c) && SafeEq(c, token);

// ---------- static site ----------
app.UseDefaultFiles();
app.UseStaticFiles();

// ---------- login (checked here, server-side) ----------
app.MapPost("/api/login", (HttpContext ctx, LoginDto dto) =>
{
    if (Limited(loginHits, IpOf(ctx.Request), 600_000, 15))
        return Results.Json(new { ok = false, error = "too many attempts" }, statusCode: 429);

    if (SafeEq(dto.User, appUser) && SafeEq(dto.Pass, appPass))
    {
        ctx.Response.Cookies.Append("auth", token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = TimeSpan.FromHours(12),
            Secure = ctx.Request.IsHttps
        });
        return Results.Json(new { ok = true });
    }
    return Results.Json(new { ok = false }, statusCode: 401);
});

app.MapGet("/api/me", (HttpContext ctx) => Results.Json(new { authed = IsAuthed(ctx.Request) }));

app.MapPost("/api/logout", (HttpContext ctx) =>
{
    ctx.Response.Cookies.Delete("auth");
    return Results.Json(new { ok = true });
});

// ---------- Claude proxy (requires login) ----------
app.MapPost("/api/claude", async (HttpContext ctx) =>
{
    if (!IsAuthed(ctx.Request))
        return Results.Json(new { error = "login required" }, statusCode: 401);
    if (string.IsNullOrEmpty(apiKey))
        return Results.Json(new { error = "ai_key_invalid", message = "מפתח ה-AI (ANTHROPIC_API_KEY) חסר בהגדרות השרת." }, statusCode: 502);

    var origin = ctx.Request.Headers["Origin"].ToString();
    if (!string.IsNullOrEmpty(allowedOrigin) && !string.IsNullOrEmpty(origin) && origin != allowedOrigin)
        return Results.Json(new { error = "origin not allowed" }, statusCode: 403);
    if (Limited(apiHits, IpOf(ctx.Request), 60_000, 40))
        return Results.Json(new { error = "too many requests, slow down" }, statusCode: 429);

    string raw;
    using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
        raw = await reader.ReadToEndAsync();

    JsonNode? body = string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw);
    if (body?["messages"] is not JsonArray messagesArr)
        return Results.Json(new { error = "messages array required" }, statusCode: 400);

    // model + max_tokens are fixed server-side so clients can't request huge (costly) outputs
    var payload = new JsonObject
    {
        ["model"] = model,
        ["max_tokens"] = maxTokens,
        ["messages"] = messagesArr.DeepClone()
    };
    var system = body?["system"];
    if (system is not null) payload["system"] = system.DeepClone();

    using var upstream = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
    upstream.Headers.Add("x-api-key", apiKey!);
    upstream.Headers.Add("anthropic-version", "2023-06-01");
    upstream.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");

    HttpResponseMessage resp;
    try { resp = await http.SendAsync(upstream); }
    catch { return Results.Json(new { error = "upstream error" }, statusCode: 502); }

    var text = await resp.Content.ReadAsStringAsync();
    if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
        return Results.Json(new { error = "ai_key_invalid", message = "מפתח ה-AI לא תקין או פג תוקף. יש לבדוק את ANTHROPIC_API_KEY בהגדרות השרת." }, statusCode: 502);
    return Results.Content(text, "application/json", Encoding.UTF8, (int)resp.StatusCode);
});

app.MapFallbackToFile("index.html");

app.Run();

// ---------- helpers ----------
static string Hmac(string secret, string data)
{
    using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
    return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
}

static bool SafeEq(string? a, string? b)
    => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a ?? ""), Encoding.UTF8.GetBytes(b ?? ""));

static string IpOf(HttpRequest req)
{
    var xff = req.Headers["X-Forwarded-For"].ToString();
    if (!string.IsNullOrEmpty(xff)) return xff.Split(',')[0].Trim();
    return req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "ip";
}

static bool Limited(ConcurrentDictionary<string, (long t, int c)> map, string ip, long windowMs, int max)
{
    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    var rec = map.AddOrUpdate(ip,
        _ => (now, 1),
        (_, old) => (now - old.t > windowMs) ? (now, 1) : (old.t, old.c + 1));
    return rec.c > max;
}

record LoginDto(string? User, string? Pass);
