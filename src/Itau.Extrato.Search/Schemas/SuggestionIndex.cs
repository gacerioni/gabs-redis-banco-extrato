using NRedisStack;
using NRedisStack.RedisStackCommands;
using StackExchange.Redis;

namespace Itau.Extrato.Search.Schemas;

/// <summary>
/// Suggestion dictionary do RediSearch (FT.SUGADD/SUGGET) — typeahead
/// sub-milissegundo com fuzzy nativo. Distinto dos índices FT.CREATE: é uma
/// estrutura de trie compacta otimizada pra prefix matching e score.
///
/// Population: <see cref="AddAsync"/> chama FT.SUGADD pra cada termo (merchant,
/// recipient, categoria). O <c>increment: true</c> aumenta o score quando o
/// termo aparece de novo — termos frequentes sobem no ranking de sugestão.
///
/// Query: <c>FT.SUGGET dict:autocomplete &lt;prefix&gt; FUZZY MAX 8 WITHSCORES</c>
/// </summary>
public static class SuggestionIndex
{
    public const string Key = "dict:autocomplete";

    public static async Task AddAsync(IDatabase db, string term, double score = 1.0)
    {
        var ft = db.FT();
        await ft.SugAddAsync(Key, term, score, increment: true);
    }

    /// <summary>
    /// Bulk inicial — adiciona uma lista de termos com score base. Para o seed.
    /// </summary>
    public static async Task BulkAddAsync(IDatabase db, IEnumerable<string> terms, double baseScore = 1.0)
    {
        var ft = db.FT();
        foreach (var term in terms.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(term)) continue;
            await ft.SugAddAsync(Key, term.Trim(), baseScore, increment: true);
        }
    }

    /// <summary>Limpa o dicionário inteiro — útil pra force re-seed.</summary>
    public static Task ClearAsync(IDatabase db) => db.KeyDeleteAsync(Key);
}
