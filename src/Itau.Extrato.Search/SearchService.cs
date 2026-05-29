using System.Text;
using System.Text.Json;
using Itau.Extrato.Search.Cache;
using Itau.Extrato.Search.Infra;
using Itau.Extrato.Search.Models;
using Itau.Extrato.Search.Schemas;
using Itau.Extrato.Search.Telemetry;
using NRedisStack;
using NRedisStack.RedisStackCommands;
using NRedisStack.Search;
using StackExchange.Redis;

namespace Itau.Extrato.Search;

/// <summary>
/// Orquestrador da busca. <b>Redis é a estrela</b>: 95% das queries resolvem
/// em &lt;10ms sem nem chamar OpenAI. Só o long-tail conversacional usa LLM
/// — e mesmo esse é cacheado em Redis, então a 2ª vez é instantânea.
///
/// Três caminhos, em ordem de custo:
///
/// <list type="number">
/// <item>
///   <b>Keyword (Redis-only):</b> default pra ~95% das queries. Usa
///   <see cref="QueryClassifier"/> regex pra detectar termos simples ("uber",
///   "luz", "salário"). Resolve via <c>FT.SEARCH</c> com synonyms nativos
///   (FT.SYNUPDATE), TAG filter por user, sort por date DESC. <b>Zero
///   chamada externa.</b> Latência alvo: 1-10ms.
/// </item>
/// <item>
///   <b>Natural language (LLM rewrite + cache):</b> queries com data relativa,
///   range de valor, direção. 1ª vez: chama LLM (~1.5s) → grava rewrite no
///   Redis. Subsequentes: <c>JSON.GET rewrite:&lt;hash&gt;</c> → pula LLM.
///   Latência: 1ª vez ~1.7s; cacheada ~10ms.
/// </item>
/// <item>
///   <b>Semantic (VSS, opt-in):</b> embed da query + KNN contra o vector
///   field. Usado APENAS quando o frontend manda <c>mode=semantic</c>. Custa
///   200-400ms (embed na OpenAI) + 2-5ms (KNN). Vale a pena pra similaridade
///   sem palavras em comum.
/// </item>
/// </list>
/// </summary>
public sealed class SearchService
{
    private readonly RedisConnection _redis;
    private readonly EmbeddingsClient _embeddings;
    private readonly LlmRewriter _rewriter;
    private readonly RewriteCache _rewriteCache;

    public SearchService(
        RedisConnection redis,
        EmbeddingsClient embeddings,
        LlmRewriter rewriter,
        RewriteCache rewriteCache)
    {
        _redis = redis;
        _embeddings = embeddings;
        _rewriter = rewriter;
        _rewriteCache = rewriteCache;
    }

    // ----------------------------------------------------------------------
    // Baseline PING — uma chamada Redis trivial pra medir o RTT atômico
    // antes do FT.SEARCH. Vira o "qual é a latência mais pura do Redis agora"
    // no cockpit, independente da complexidade do search subsequente.
    // Custo: <1ms em Redis local.
    // ----------------------------------------------------------------------
    private async Task MeasurePingBaselineAsync(StageTimer timer)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await _redis.Db.PingAsync();
        }
        finally
        {
            sw.Stop();
            timer.Record("redis.ping_baseline", sw.Elapsed.TotalMilliseconds, isRedis: true);
        }
    }

    // ----------------------------------------------------------------------
    // SUGGEST — FT.SUGGET com FUZZY (sub-ms)
    // ----------------------------------------------------------------------
    public async Task<List<string>> SuggestAsync(string prefix, int max = 8, bool fuzzy = true, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(prefix)) return new List<string>();
        try
        {
            var sugs = await _redis.Db.FT().SugGetAsync(SuggestionIndex.Key, prefix.Trim(), fuzzy: fuzzy, max: max);
            return sugs?.ToList() ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    // ----------------------------------------------------------------------
    // KEYWORD — Redis-only. Sem embed, sem KNN. Synonyms via FT.SYNUPDATE
    // resolvem expansão (luz→eletropaulo etc.) sem custo extra.
    // ----------------------------------------------------------------------
    public async Task<SearchResult> KeywordSearchAsync(
        string query,
        string userId,
        int limit,
        StageTimer timer,
        CancellationToken ct = default)
    {
        await MeasurePingBaselineAsync(timer);
        var safeQuery = EscapeText(query);
        var tokens = safeQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var textClause = tokens.Length switch
        {
            0 => "*",
            1 => tokens[0],
            _ => "(" + string.Join(" | ", tokens) + ")",
        };
        var qstr = $"@user_id:{{{EscapeTag(userId)}}} {textClause}";

        var q = new Query(qstr)
            .ReturnFields("id", "user_id", "date", "amount_brl", "type", "direction", "description",
                          "counterparty_name", "pix_message", "category", "channel", "installment", "balance_after")
            .SetSortBy("date", ascending: false)
            .Limit(0, limit)
            .Dialect(2);

        NRedisStack.Search.SearchResult sr;
        using (timer.RedisStage("ft.search"))
            sr = await _redis.Db.FT().SearchAsync(TransactionIndex.Name, q);

        return new SearchResult(
            Mode: "keyword_redis_only",
            ModeLabel: "🚀 Redis-only",
            TotalResults: (int)sr.TotalResults,
            Items: sr.Documents.Select(MapDoc).ToList(),
            FromRewriteCache: false,
            LlmRewriteJson: null,
            Filter: null);
    }

    // ----------------------------------------------------------------------
    // NATURAL LANGUAGE — LLM rewrite com cache em Redis
    // ----------------------------------------------------------------------
    public async Task<SearchResult> NaturalLanguageSearchAsync(
        string query,
        string userId,
        int limit,
        StageTimer timer,
        CancellationToken ct = default)
    {
        await MeasurePingBaselineAsync(timer);

        // 1) Cache check — barato (1 GET, <1ms)
        string? cachedJson;
        using (timer.RedisStage("rewrite.cache_get"))
            cachedJson = await _rewriteCache.GetAsync(query, ct);

        RewrittenFilter filter;
        string rawJson;
        bool fromCache;

        if (cachedJson is not null)
        {
            filter = JsonSerializer.Deserialize<RewrittenFilter>(cachedJson) ?? new RewrittenFilter();
            rawJson = cachedJson;
            fromCache = true;
            // Stage virtual pra UI mostrar "evitou chamar OpenAI"
            timer.Record("llm.rewrite_skipped_cache_hit", 0, isRedis: false);
        }
        else
        {
            (filter, rawJson, _, _) = await _rewriter.RewriteAsync(query, timer, ct);
            using (timer.RedisStage("rewrite.cache_set"))
                await _rewriteCache.SetAsync(query, rawJson, ct);
            fromCache = false;
        }

        // 2) Monta FT.SEARCH a partir dos filtros estruturados
        var clauses = new List<string> { $"@user_id:{{{EscapeTag(userId)}}}" };
        if (!string.IsNullOrWhiteSpace(filter.Type))      clauses.Add($"@type:{{{EscapeTag(filter.Type)}}}");
        if (!string.IsNullOrWhiteSpace(filter.Direction)) clauses.Add($"@direction:{{{EscapeTag(filter.Direction)}}}");
        if (!string.IsNullOrWhiteSpace(filter.Category))  clauses.Add($"@category:{{{EscapeTag(filter.Category)}}}");

        if (filter.DateFromUnix.HasValue || filter.DateToUnix.HasValue)
        {
            var from = filter.DateFromUnix.HasValue ? filter.DateFromUnix.Value.ToString() : "-inf";
            var to   = filter.DateToUnix.HasValue   ? filter.DateToUnix.Value.ToString()   : "+inf";
            clauses.Add($"@date:[{from} {to}]");
        }
        if (filter.AmountMin.HasValue || filter.AmountMax.HasValue)
        {
            var from = filter.AmountMin.HasValue ? filter.AmountMin.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "-inf";
            var to   = filter.AmountMax.HasValue ? filter.AmountMax.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "+inf";
            clauses.Add($"@amount_brl:[{from} {to}]");
        }

        // Text parts (counterparty + free_text + PARC pra parcelado) — multi-word vira OR
        var textParts = new List<string>();
        void AddTextPart(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var clean = EscapeText(raw);
            var toks = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length == 0) return;
            textParts.Add(toks.Length == 1 ? toks[0] : "(" + string.Join(" | ", toks) + ")");
        }
        AddTextPart(filter.CounterpartyMatch);
        AddTextPart(filter.FreeText);
        if (filter.InstallmentOnly == true) textParts.Add("PARC");

        if (textParts.Count > 0)
            clauses.Add("(" + string.Join(" | ", textParts) + ")");

        var qstr = string.Join(" ", clauses);

        var q = new Query(qstr)
            .ReturnFields("id", "user_id", "date", "amount_brl", "type", "direction", "description",
                          "counterparty_name", "pix_message", "category", "channel", "installment", "balance_after")
            .SetSortBy("date", ascending: false)
            .Limit(0, limit)
            .Dialect(2);

        NRedisStack.Search.SearchResult sr;
        using (timer.RedisStage("ft.search"))
            sr = await _redis.Db.FT().SearchAsync(TransactionIndex.Name, q);

        var mode = fromCache ? "nl_cached_rewrite" : "nl_llm_rewrite";
        var label = fromCache ? "✨ Cached rewrite + Redis" : "🧠 LLM rewrite + Redis";

        return new SearchResult(
            Mode: mode,
            ModeLabel: label,
            TotalResults: (int)sr.TotalResults,
            Items: sr.Documents.Select(MapDoc).ToList(),
            FromRewriteCache: fromCache,
            LlmRewriteJson: rawJson,
            Filter: new FilterSummary(
                Interpretation: filter.Interpretation,
                Type: filter.Type,
                Direction: filter.Direction,
                Category: filter.Category,
                CounterpartyMatch: filter.CounterpartyMatch,
                FreeText: filter.FreeText,
                DateFrom: filter.DateFromIso,
                DateTo: filter.DateToIso,
                AmountMin: filter.AmountMin,
                AmountMax: filter.AmountMax,
                FtQuery: qstr));
    }

    // ----------------------------------------------------------------------
    // SEMANTIC — opt-in: embed da query + KNN. Usar quando o cliente quer
    // "busca por significado" (sinônimos não cobririam).
    // ----------------------------------------------------------------------
    public async Task<SearchResult> SemanticSearchAsync(
        string query,
        string userId,
        int limit,
        StageTimer timer,
        CancellationToken ct = default)
    {
        await MeasurePingBaselineAsync(timer);

        float[] vec;
        using (timer.Stage("embed"))
            vec = await _embeddings.EmbedAsync(query, ct);

        var qstr = $"(@user_id:{{{EscapeTag(userId)}}})=>[KNN {limit} @embedding $vec AS dist]";
        var q = new Query(qstr)
            .AddParam("vec", EmbeddingsClient.ToBytes(vec))
            .ReturnFields("id", "user_id", "date", "amount_brl", "type", "direction", "description",
                          "counterparty_name", "pix_message", "category", "channel", "installment", "balance_after", "dist")
            .SetSortBy("dist", ascending: true)
            .Limit(0, limit)
            .Dialect(2);

        NRedisStack.Search.SearchResult sr;
        using (timer.RedisStage("ft.search.knn"))
            sr = await _redis.Db.FT().SearchAsync(TransactionIndex.Name, q);

        return new SearchResult(
            Mode: "semantic_vss",
            ModeLabel: "🔍 Semantic VSS",
            TotalResults: (int)sr.TotalResults,
            Items: sr.Documents.Select(MapDoc).ToList(),
            FromRewriteCache: false,
            LlmRewriteJson: null,
            Filter: null);
    }

    // ----------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------

    private static SearchHit MapDoc(NRedisStack.Search.Document d)
    {
        return new SearchHit(
            Id: (string)d["id"]!,
            UserId: (string)d["user_id"]!,
            DateUnix: (long)d["date"],
            AmountBrl: (decimal)(double)d["amount_brl"],
            Type: (string)d["type"]!,
            Direction: (string)d["direction"]!,
            Description: (string)d["description"]!,
            CounterpartyName: d["counterparty_name"].IsNullOrEmpty ? null : (string)d["counterparty_name"]!,
            PixMessage: d["pix_message"].IsNullOrEmpty ? null : (string)d["pix_message"]!,
            Category: (string)d["category"]!,
            Channel: (string)d["channel"]!,
            Installment: d["installment"].IsNullOrEmpty ? null : (string)d["installment"]!,
            BalanceAfter: d["balance_after"].IsNullOrEmpty ? (decimal?)null : (decimal)(double)d["balance_after"]);
    }

    private static string EscapeTag(string s) =>
        s.Replace("\\", "\\\\")
         .Replace("-", "\\-").Replace(":", "\\:").Replace(".", "\\.")
         .Replace("@", "\\@").Replace(" ", "\\ ");

    private static string EscapeText(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if ("()|{}[]\"~*-+=<>!?,;:'`".IndexOf(ch) >= 0) sb.Append(' ');
            else sb.Append(ch);
        }
        return sb.ToString().Trim();
    }
}

public sealed record SearchResult(
    string Mode,                       // keyword_redis_only | nl_llm_rewrite | nl_cached_rewrite | semantic_vss
    string ModeLabel,                  // human readable pra UI
    int TotalResults,
    IReadOnlyList<SearchHit> Items,
    bool FromRewriteCache,
    string? LlmRewriteJson,
    FilterSummary? Filter);

public sealed record SearchHit(
    string Id,
    string UserId,
    long DateUnix,
    decimal AmountBrl,
    string Type,
    string Direction,
    string Description,
    string? CounterpartyName,
    string? PixMessage,
    string Category,
    string Channel,
    string? Installment,
    decimal? BalanceAfter);

public sealed record FilterSummary(
    string? Interpretation,
    string? Type,
    string? Direction,
    string? Category,
    string? CounterpartyMatch,
    string? FreeText,
    string? DateFrom,
    string? DateTo,
    decimal? AmountMin,
    decimal? AmountMax,
    string FtQuery);
