using System.Text.Json.Serialization;
using DotNetEnv;
using Itau.Extrato.Search;
using Itau.Extrato.Search.Cache;
using Itau.Extrato.Search.Infra;
using Itau.Extrato.Search.Schemas;
using Itau.Extrato.Search.Telemetry;
using Itau.Extrato.Seed;
using NRedisStack;
using NRedisStack.RedisStackCommands;
// Aliases pra desambiguar do NRedisStack
using SearchResult = Itau.Extrato.Search.SearchResult;
using Query = NRedisStack.Search.Query;

if (File.Exists(".env")) Env.Load();

string Require(string name) =>
    Environment.GetEnvironmentVariable(name)
    ?? throw new InvalidOperationException(
        $"Missing env var: {name}. Set it in .env or via docker run -e {name}=...");

string Optional(string name, string fallback) =>
    Environment.GetEnvironmentVariable(name) ?? fallback;

var redisUrl = Require("REDIS_URL");
var openAiKey = Require("OPENAI_API_KEY");
var embedModel = Optional("EMBED_MODEL", EmbeddingsClient.DefaultModel);
var chatModel = Optional("CHAT_MODEL", LlmRewriter.DefaultModel);
var seedsDir = Optional("SEEDS_DIR", Path.Combine(AppContext.BaseDirectory, "seeds"));
if (!Directory.Exists(seedsDir))
{
    var repoSeeds = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "seeds"));
    if (Directory.Exists(repoSeeds)) seedsDir = repoSeeds;
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

builder.Services.AddSingleton<RedisConnection>(_ => new RedisConnection(redisUrl));
builder.Services.AddSingleton(_ => new EmbeddingsClient(openAiKey, embedModel));
builder.Services.AddSingleton(_ => new LlmRewriter(openAiKey, chatModel));
// Singleton mutável — admin panel atualiza via PUT /api/admin/settings.
builder.Services.AddSingleton(_ => AppSettings.FromEnvironment());
builder.Services.AddSingleton(sp => new RewriteCache(
    sp.GetRequiredService<RedisConnection>(),
    sp.GetRequiredService<AppSettings>()));
builder.Services.AddSingleton<Seeder>(sp => new Seeder(
    sp.GetRequiredService<RedisConnection>(),
    sp.GetRequiredService<EmbeddingsClient>(),
    seedsDir));
builder.Services.AddSingleton(sp => new SearchService(
    sp.GetRequiredService<RedisConnection>(),
    sp.GetRequiredService<EmbeddingsClient>(),
    sp.GetRequiredService<LlmRewriter>(),
    sp.GetRequiredService<RewriteCache>()));

var allowedOrigins = Optional("CORS_ORIGINS", "https://extrato.platformengineer.io")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(allowedOrigins)
     .AllowAnyMethod()
     .AllowAnyHeader()));

var app = builder.Build();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();

// ---- Boot: aguarda Redis sair do LOADING, depois seeda (idempotente) ----
using (var scope = app.Services.CreateScope())
{
    var log = app.Logger;
    var redis = scope.ServiceProvider.GetRequiredService<RedisConnection>();
    var ready = false;
    for (int i = 0; i < 60; i++)
    {
        try
        {
            var t = redis.Ping();
            log.LogInformation("Redis ping OK ({ping}ms) após {tries} tentativa(s)", t.TotalMilliseconds, i + 1);
            ready = true;
            break;
        }
        catch (StackExchange.Redis.RedisServerException ex) when (ex.Message.Contains("LOADING"))
        {
            if (i % 5 == 0) log.LogInformation("Redis ainda carregando snapshot… ({s}s)", i);
            await Task.Delay(1000);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Redis indisponível — tentando de novo em 1s ({s}s)", i);
            await Task.Delay(1000);
        }
    }

    if (!ready)
    {
        app.Logger.LogError("Redis não respondeu em 60s — busca vai falhar até que reseje boot.");
    }
    else
    {
        try
        {
            var seeder = scope.ServiceProvider.GetRequiredService<Seeder>();
            var force = Environment.GetEnvironmentVariable("SEED_FORCE") == "1";
            var report = await seeder.SeedAllAsync(force: force);
            log.LogInformation("Seed: {note} (before={before}, added={added})",
                report.Note, report.DocsBefore, report.DocsAdded);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Seed falhou. Verifique OPENAI_API_KEY e seeds dir.");
        }
    }
}

// ---- Admin auth helper ----
var adminKey = Environment.GetEnvironmentVariable("ADMIN_API_KEY");
static bool IsAdmin(HttpContext ctx, string? adminKey)
{
    if (string.IsNullOrEmpty(adminKey)) return true; // not configured → open (dev)
    return ctx.Request.Headers.TryGetValue("X-Admin-Key", out var val)
        && val.FirstOrDefault() == adminKey;
}

// ---- Endpoints ----

app.MapGet("/api/health", (RedisConnection redis) =>
{
    try
    {
        var t = redis.Ping();
        return Results.Ok(new { status = "ok", redis_ping_ms = Math.Round(t.TotalMilliseconds, 2), service = "itau-extrato" });
    }
    catch { return Results.Json(new { status = "error", error = "Redis unavailable" }, statusCode: 503); }
});

// Perfil + saldo atual (computado da última tx com balance_after preenchido).
app.MapGet("/api/account", async (string? user_id, RedisConnection redis) =>
{
    var uid = user_id ?? DemoProfiles.Gabriel.UserId;
    var profile = uid switch
    {
        "gabriel_cerioni" => DemoProfiles.Gabriel,
        "miller_moreno"   => DemoProfiles.Miller,
        "camila_andrade"  => DemoProfiles.Camila,
        "pedro_castro"    => DemoProfiles.Pedro,
        _ => DemoProfiles.Gabriel,
    };

    // Saldo: pega últimas 10 transações por data DESC, encontra primeira com
    // balance_after preenchido (algumas são cartão de crédito que não afetam
    // conta corrente direto).
    decimal? balance = null;
    long? balanceAtUnix = null;
    try
    {
        var q = new Query($"@user_id:{{{profile.UserId.Replace("-", "\\-").Replace("_", "\\_")}}}")
            .ReturnFields("balance_after", "date")
            .SetSortBy("date", ascending: false)
            .Limit(0, 10)
            .Dialect(2);
        var sr = await redis.Db.FT().SearchAsync(TransactionIndex.Name, q);
        foreach (var d in sr.Documents)
        {
            if (!d["balance_after"].IsNullOrEmpty)
            {
                balance = (decimal)(double)d["balance_after"];
                balanceAtUnix = (long)d["date"];
                break;
            }
        }
    }
    catch { /* sem saldo no boot inicial */ }

    return Results.Ok(new
    {
        user_id = profile.UserId,
        display_name = profile.DisplayName,
        agencia = profile.Agencia,
        conta = profile.Conta,
        cpf_masked = profile.CpfMasked,
        balance_brl = balance,
        balance_at_unix = balanceAtUnix,
    });
});

// Autocomplete: FT.SUGGET FUZZY, sub-ms.
app.MapGet("/api/extrato/suggest", async (string? q, int? max, SearchService search) =>
{
    if (string.IsNullOrWhiteSpace(q)) return Results.Ok(new { items = Array.Empty<string>(), latency_ms = 0.0 });
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var items = await search.SuggestAsync(q.Trim(), max ?? 8, fuzzy: true);
    sw.Stop();
    return Results.Ok(new { items, latency_ms = Math.Round(sw.Elapsed.TotalMilliseconds, 2) });
});

// Busca principal: triage automática via QueryClassifier; pode forçar com `mode`.
app.MapPost("/api/extrato/search", async (SearchRequest req, SearchService search, AppSettings settings, ILogger<Program> log, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Query))
        return Results.BadRequest(new { error = "query is required" });

    if (req.Query.Length > 500)
        return Results.BadRequest(new { error = "query too long (max 500 chars)" });

    var userId = string.IsNullOrWhiteSpace(req.UserId) ? DemoProfiles.Gabriel.UserId : req.UserId.Trim();
    var query = req.Query.Trim();
    var limit = req.Limit is > 0 and <= 100 ? req.Limit.Value : 30;

    // Precedência do modo:
    //   1. Header `mode` no request explícito (sempre vence)
    //   2. AppSettings.DefaultMode != "auto" (override pelo admin)
    //   3. QueryClassifier regex
    string mode;
    if (!string.IsNullOrWhiteSpace(req.Mode) && req.Mode.ToLowerInvariant() is "keyword" or "natural" or "semantic")
        mode = req.Mode!.ToLowerInvariant();
    else if (settings.DefaultMode is "keyword" or "natural" or "semantic")
        mode = settings.DefaultMode;
    else
        mode = QueryClassifier.Classify(query) == QueryClass.NaturalLanguage ? "natural" : "keyword";

    var timer = new StageTimer();
    try
    {
        SearchResult result = mode switch
        {
            "natural"  => await search.NaturalLanguageSearchAsync(query, userId, limit, timer, ct),
            "semantic" => await search.SemanticSearchAsync(query, userId, limit, timer, ct),
            _          => await search.KeywordSearchAsync(query, userId, limit, timer, ct),
        };

        var metrics = timer.Build(
            mode: result.Mode,
            llmRewriteJson: result.LlmRewriteJson,
            totalResults: result.TotalResults);

        return Results.Ok(new
        {
            query, user_id = userId,
            mode = result.Mode,
            mode_label = result.ModeLabel,
            from_rewrite_cache = result.FromRewriteCache,
            total_results = result.TotalResults,
            items = result.Items,
            filter = result.Filter,
            metrics,
            error = (string?)null,
        });
    }
    catch (Exception ex)
    {
        log.LogError(ex, "Search failed for query=\"{q}\"", query);
        return Results.Ok(new
        {
            query, user_id = userId,
            mode = "error",
            mode_label = "❌ Erro",
            from_rewrite_cache = false,
            total_results = 0,
            items = Array.Empty<object>(),
            filter = (object?)null,
            metrics = timer.Build(mode: "error"),
            error = "An internal error occurred while processing your search.",
        });
    }
});

// ============================================================
// Admin + Redis info endpoints
// ============================================================

// Detalhes do Redis conectado — exibe na UI ("Redis Cloud sa-east-1" / "Local 8.6.2").
// Gated behind admin auth — exposes infrastructure details.
app.MapGet("/api/redis/info", async (HttpContext ctx, RedisConnection redis) =>
{
    if (!IsAdmin(ctx, adminKey))
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);

    string host = "unknown", version = "?", deployment = "local";
    int port = 0;
    string? region = null;
    try
    {
        var url = redisUrl;
        if (url.StartsWith("redis://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(url);
            host = uri.Host;
            port = uri.IsDefaultPort ? 6379 : uri.Port;
            var hostLower = host.ToLowerInvariant();
            if (hostLower.Contains("redis-cloud.com") || hostLower.Contains("redislabs.com") || hostLower.Contains(".cloud.rlrcp.com"))
                deployment = "redis_cloud";
            // Extrai região: redis-NNNNN-sa-east-1-N.redis-cloud.com → sa-east-1
            var head = hostLower.Split('.')[0];
            var parts = head.Split('-');
            for (int i = 0; i < parts.Length - 2; i++)
            {
                if (parts[i].Length == 2 && (parts[i + 1] is "east" or "west" or "central" or "north" or "south" or "northeast" or "southeast"))
                {
                    region = $"{parts[i]}-{parts[i + 1]}-{parts[i + 2]}";
                    break;
                }
            }
        }
        // INFO server retorna texto cru; parse line-by-line procurando "redis_version:".
        // (server.Info() retorna IGrouping<string,KVP[]> mas o agrupamento por section pode
        // não pegar "server" no Redis 8 — parsing manual é mais confiável.)
        var raw = await redis.Db.ExecuteAsync("INFO", "server");
        if (!raw.IsNull)
        {
            var text = (string)raw!;
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("redis_version:", StringComparison.OrdinalIgnoreCase))
                {
                    version = trimmed["redis_version:".Length..].Trim();
                    break;
                }
            }
        }
    }
    catch { }
    return Results.Ok(new
    {
        host, port, version,
        deployment,
        region,
        modules = new[] { "search", "json", "vectorset", "bloom", "timeseries" },
    });
});

// Estatísticas pra UI/admin: contagens dos índices, cache, sinônimos.
app.MapGet("/api/admin/stats", async (HttpContext ctx, RewriteCache cache, RedisConnection redis) =>
{
    if (!IsAdmin(ctx, adminKey))
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);

    long txCount = 0, suggCount = 0;
    long cacheCount = await cache.CountAsync();
    try
    {
        var info = await redis.Db.FT().InfoAsync(TransactionIndex.Name);
        txCount = info?.NumDocs ?? 0;
    }
    catch { }
    try
    {
        suggCount = await redis.Db.FT().SugLenAsync(SuggestionIndex.Key);
    }
    catch { }
    return Results.Ok(new
    {
        idx_transactions = txCount,
        dict_autocomplete = suggCount,
        rewrite_cache_entries = cacheCount,
    });
});

// Settings em memória — admin altera live, sem rebuild.
app.MapGet("/api/admin/settings", (HttpContext ctx, AppSettings settings) =>
{
    if (!IsAdmin(ctx, adminKey))
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);

    return Results.Ok(new
    {
        rewrite_ttl_sec = (int)settings.RewriteTtl.TotalSeconds,
        default_mode = settings.DefaultMode,
    });
});

app.MapPut("/api/admin/settings", (HttpContext ctx, UpdateSettingsRequest req, AppSettings settings) =>
{
    if (!IsAdmin(ctx, adminKey))
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);

    if (req.RewriteTtlSec is > 0 and <= 86400)
        settings.RewriteTtl = TimeSpan.FromSeconds(req.RewriteTtlSec.Value);
    if (!string.IsNullOrWhiteSpace(req.DefaultMode))
        settings.DefaultMode = req.DefaultMode.ToLowerInvariant();
    return Results.Ok(new
    {
        rewrite_ttl_sec = (int)settings.RewriteTtl.TotalSeconds,
        default_mode = settings.DefaultMode,
    });
});

// Wipe do cache de rewrite — útil pra demo "limpar cache → mostrar 1ª chamada lenta de novo".
app.MapPost("/api/admin/clear-cache", async (HttpContext ctx, RedisConnection redis) =>
{
    if (!IsAdmin(ctx, adminKey))
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);

    var server = redis.Db.Multiplexer.GetServers().FirstOrDefault(s => s.IsConnected);
    if (server is null) return Results.Ok(new { deleted = 0 });
    long deleted = 0;
    var batch = new List<StackExchange.Redis.RedisKey>();
    await foreach (var k in server.KeysAsync(pattern: RewriteCache.KeyPrefix + "*", pageSize: 200))
    {
        batch.Add(k);
        if (batch.Count >= 500)
        {
            deleted += await redis.Db.KeyDeleteAsync(batch.ToArray());
            batch.Clear();
        }
    }
    if (batch.Count > 0) deleted += await redis.Db.KeyDeleteAsync(batch.ToArray());
    return Results.Ok(new { deleted });
});

// Forçar re-seed.
app.MapPost("/api/seed", async (HttpContext ctx, Seeder seeder) =>
{
    if (!IsAdmin(ctx, adminKey))
        return Results.Json(new { error = "unauthorized" }, statusCode: 401);

    var report = await seeder.SeedAllAsync(force: true);
    return Results.Ok(report);
});

app.MapGet("/", () => Results.Redirect("/index.html"));

app.Run();

public sealed record SearchRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("user_id")] string? UserId,
    [property: JsonPropertyName("mode")] string? Mode,
    [property: JsonPropertyName("limit")] int? Limit);

public sealed record UpdateSettingsRequest(
    [property: JsonPropertyName("rewrite_ttl_sec")] int? RewriteTtlSec,
    [property: JsonPropertyName("default_mode")] string? DefaultMode);
