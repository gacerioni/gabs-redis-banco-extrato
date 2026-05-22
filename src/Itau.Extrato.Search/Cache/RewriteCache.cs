using System.Security.Cryptography;
using System.Text;
using Itau.Extrato.Search.Infra;
using StackExchange.Redis;

namespace Itau.Extrato.Search.Cache;

/// <summary>
/// Cache Redis-backed pra os filtros estruturados que o LLM rewriter gera.
///
/// Por quê: cada chamada do LLM custa ~1.5-2s e ~$0.00008. Se a mesma query
/// natural é feita N vezes (mesma demo, mesmo cliente, sessão recorrente), só
/// a 1ª paga o custo. As subsequentes batem em Redis em ~5-10ms.
///
/// Chave: <c>rewrite:{sha256(normalized_query)}</c> · TTL 24h
/// Valor: o JSON exato emitido pelo OpenAI (json_schema strict) — pode ser
/// rehidratado em <see cref="RewrittenFilter"/> direto.
///
/// Normalização da query antes do hash: lowercase + trim + colapsa espaços
/// múltiplos. "Piques  pra Dua" e "piques pra Dua" batem na mesma chave.
///
/// Próximo passo (não nesse PoV): cache <i>semântico</i> via KNN num índice
/// FT.CREATE adicional — "piques pra Dua esse mês" e "pix pra Dua nesse mês
/// atual" bateriam na mesma entrada por similaridade. Mostra outro power-up
/// do Redis na evolução.
/// </summary>
public sealed class RewriteCache
{
    public const string KeyPrefix = "rewrite:";

    private readonly RedisConnection _redis;
    private readonly AppSettings _settings;

    public RewriteCache(RedisConnection redis, AppSettings settings)
    {
        _redis = redis;
        _settings = settings;
    }

    public string KeyFor(string query) => KeyPrefix + Hash(Normalize(query));

    public async Task<string?> GetAsync(string query, CancellationToken ct = default)
    {
        var v = await _redis.Db.StringGetAsync(KeyFor(query));
        return v.IsNullOrEmpty ? null : (string)v!;
    }

    public Task SetAsync(string query, string filterJson, CancellationToken ct = default)
        => _redis.Db.StringSetAsync(KeyFor(query), filterJson, _settings.RewriteTtl);

    public Task ClearAsync(string query) => _redis.Db.KeyDeleteAsync(KeyFor(query));

    /// <summary>Diagnóstico/admin: quantos rewrites estão em cache agora.</summary>
    public async Task<long> CountAsync()
    {
        var server = _redis.Db.Multiplexer.GetServers().FirstOrDefault(s => s.IsConnected);
        if (server is null) return 0;
        long n = 0;
        await foreach (var _ in server.KeysAsync(pattern: KeyPrefix + "*", pageSize: 500)) n++;
        return n;
    }

    private static string Normalize(string q)
    {
        var s = (q ?? "").Trim().ToLowerInvariant();
        // Colapsa espaços
        var sb = new StringBuilder(s.Length);
        bool prevSpace = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace) sb.Append(' ');
                prevSpace = true;
            }
            else
            {
                sb.Append(ch);
                prevSpace = false;
            }
        }
        return sb.ToString();
    }

    private static string Hash(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        // Compacto: hex curto de 16 bytes basta pra ser único em escala de PoV
        var sb = new StringBuilder(32);
        for (int i = 0; i < 16; i++) sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }
}
