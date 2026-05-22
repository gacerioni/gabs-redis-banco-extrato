using System.Diagnostics;
using System.Text.Json;
using Itau.Extrato.Search.Infra;
using Itau.Extrato.Search.Schemas;
using NRedisStack;
using NRedisStack.RedisStackCommands;
using StackExchange.Redis;
// Disambiguar do NRedisStack.Transaction (que é a transação Redis MULTI/EXEC, não a nossa).
using Transaction = Itau.Extrato.Search.Models.Transaction;

namespace Itau.Extrato.Seed;

/// <summary>
/// Orquestrador do seed do Itaú Extrato:
///   1. Gera transações realistas pra cada UserProfile via TransactionFactory
///   2. Concatena tudo, ordena por data
///   3. Embeda descrições em batches via OpenAI (compartilha uma chamada por ~200 docs)
///   4. JSON.SET em chunks de 50 (mesma estratégia do PoV Inter, evita timeout)
///   5. Popula dict:autocomplete via FT.SUGADD
///   6. Configura grupos de sinônimos via FT.SYNUPDATE
///
/// Idempotente: se idx:transactions já tem docs e force=false, skipa tudo.
/// </summary>
public sealed class Seeder
{
    private const int ChunkSize = 50;
    private const int EmbedBatchSize = 200;

    private readonly RedisConnection _redis;
    private readonly EmbeddingsClient _embeddings;
    private readonly string _seedsDir;

    public Seeder(RedisConnection redis, EmbeddingsClient embeddings, string seedsDir)
    {
        _redis = redis;
        _embeddings = embeddings;
        _seedsDir = seedsDir;
    }

    public async Task<SeedReport> SeedAllAsync(bool force = false, CancellationToken ct = default)
    {
        var db = _redis.Db;

        // Quando force=true, dropar o índice tb — schema pode ter mudado
        // (ex: adição do TEXT field pix_message). Recriação garante que o
        // novo schema entre em efeito sem precisar de FT.ALTER manual.
        if (force)
        {
            await TransactionIndex.DropAsync(db, keepDocs: false);
            await SuggestionIndex.ClearAsync(db);
        }

        await TransactionIndex.EnsureCreatedAsync(db, _embeddings.Dim);

        var existing = await CountDocsAsync(db, TransactionIndex.Name);
        if (existing > 0 && !force)
            return new SeedReport(existing, 0, "skipped (already populated)", 0, 0);

        if (force)
        {
            await DeletePrefixAsync(db, TransactionIndex.Prefix);
        }

        // 1. Generate transactions for all profiles.
        // Window: vai de hoje-(N-1) meses até HOJE inclusive (mês corrente
        // sempre presente — caso contrário queries como "esse mês" voltam
        // vazio enquanto a UI mostra "hoje").
        var sw = Stopwatch.StartNew();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var all = new List<Transaction>();
        all.AddRange(TransactionFactory.Generate(DemoProfiles.Gabriel, today.AddMonths(-11), months: 12, seed: 42));
        all.AddRange(TransactionFactory.Generate(DemoProfiles.Miller,  today.AddMonths(-5),  months: 6,  seed: 84));
        all.AddRange(TransactionFactory.Generate(DemoProfiles.Camila,  today.AddMonths(-2),  months: 3,  seed: 168));
        all.AddRange(TransactionFactory.Generate(DemoProfiles.Pedro,   today.AddMonths(-2),  months: 3,  seed: 336));
        var generateMs = sw.Elapsed.TotalMilliseconds;

        // 2. Build text to embed (descrição + counterparty + categoria pra ter contexto semântico amplo)
        sw.Restart();
        var embedTexts = all.Select(t => $"{t.Description} | {t.CounterpartyName ?? ""} | {t.Category} | {t.Type}").ToList();
        var embeddings = new List<float[]>(all.Count);
        for (int i = 0; i < embedTexts.Count; i += EmbedBatchSize)
        {
            var chunk = embedTexts.Skip(i).Take(EmbedBatchSize).ToList();
            var vecs = await _embeddings.EmbedManyAsync(chunk, ct);
            embeddings.AddRange(vecs);
        }
        var embedMs = sw.Elapsed.TotalMilliseconds;

        // Combine embedding into transactions
        var withEmb = new List<Transaction>(all.Count);
        for (int i = 0; i < all.Count; i++)
            withEmb.Add(all[i] with { Embedding = embeddings[i] });

        // 3. JSON.SET em chunks
        sw.Restart();
        var json = db.JSON();
        for (int start = 0; start < withEmb.Count; start += ChunkSize)
        {
            var end = Math.Min(start + ChunkSize, withEmb.Count);
            var batch = new Task[end - start];
            for (int i = start; i < end; i++)
            {
                var t = withEmb[i];
                batch[i - start] = json.SetAsync($"{TransactionIndex.Prefix}{t.Id}", "$", t);
            }
            await Task.WhenAll(batch);
        }
        var writeMs = sw.Elapsed.TotalMilliseconds;

        // 4. Autocomplete corpus (merchants + recipients + categorias + frases)
        sw.Restart();
        await PopulateAutocompleteAsync(db, withEmb, ct);
        var sugMs = sw.Elapsed.TotalMilliseconds;

        // 5. Synonyms (FT.SYNUPDATE)
        sw.Restart();
        await ApplySynonymsAsync(db, ct);
        var synMs = sw.Elapsed.TotalMilliseconds;

        return new SeedReport(
            DocsBefore: existing,
            DocsAdded: withEmb.Count,
            Note: $"generate={generateMs:F0}ms · embed={embedMs:F0}ms · write={writeMs:F0}ms · sugadd={sugMs:F0}ms · synupdate={synMs:F0}ms",
            AutocompleteTerms: 0,
            SynonymGroups: 0);
    }

    // ----------------------------------------------------------------------
    // Autocomplete: alimenta dict:autocomplete com merchants + recipients +
    // categorias + frases (do arquivo JSON), com incremento por contagem.
    // ----------------------------------------------------------------------
    private async Task PopulateAutocompleteAsync(IDatabase db, List<Transaction> txns, CancellationToken ct)
    {
        var ft = db.FT();

        // Frequência de cada termo na base — termos que aparecem mais ranqueiam
        // mais alto no FT.SUGGET.
        var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Bump(string? term, int weight = 1)
        {
            if (string.IsNullOrWhiteSpace(term)) return;
            var k = term.Trim();
            if (k.Length < 2) return;
            freq[k] = freq.TryGetValue(k, out var v) ? v + weight : weight;
        }

        // Da própria base
        foreach (var t in txns)
        {
            Bump(t.CounterpartyName, 2);
            // Extrai o "merchant name" da description (parte depois do COMPRA/PIX/etc.)
            var desc = t.Description;
            if (desc.StartsWith("COMPRA NO ", StringComparison.OrdinalIgnoreCase))
            {
                var afterCompra = desc.Substring("COMPRA NO ".Length);
                var spaceIdx = afterCompra.IndexOf(' ');
                if (spaceIdx > 0)
                {
                    var merchant = afterCompra.Substring(spaceIdx + 1);
                    var parcIdx = merchant.IndexOf(" PARC ");
                    if (parcIdx > 0) merchant = merchant.Substring(0, parcIdx);
                    Bump(merchant);
                }
            }
            else if (desc.StartsWith("PIX ENVIADO ", StringComparison.OrdinalIgnoreCase))
                Bump(desc.Substring("PIX ENVIADO ".Length));
            else if (desc.StartsWith("PIX RECEBIDO ", StringComparison.OrdinalIgnoreCase))
                Bump(desc.Substring("PIX RECEBIDO ".Length));
        }

        // Do arquivo de corpus curado (categorias, frases comuns)
        var corpusPath = Path.Combine(_seedsDir, "autocomplete_corpus.json");
        if (File.Exists(corpusPath))
        {
            try
            {
                using var fs = File.OpenRead(corpusPath);
                using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct);
                foreach (var section in new[] { "categorias_e_tipos", "frases_busca_comuns" })
                {
                    if (doc.RootElement.TryGetProperty(section, out var el) && el.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var it in el.EnumerateArray())
                            Bump(it.GetString(), weight: 3);
                    }
                }
                if (doc.RootElement.TryGetProperty("merchants", out var merchantsEl))
                {
                    foreach (var group in merchantsEl.EnumerateObject())
                        foreach (var m in group.Value.EnumerateArray())
                            Bump(m.GetString(), weight: 2);
                }
                if (doc.RootElement.TryGetProperty("recipients_pix_padrao", out var recsEl))
                {
                    foreach (var r in recsEl.EnumerateArray())
                        Bump(r.GetString(), weight: 4);
                }
            }
            catch { /* corpus opcional */ }
        }

        // Push em FT.SUGADD com score = frequência
        foreach (var (term, count) in freq)
        {
            try { await ft.SugAddAsync(SuggestionIndex.Key, term, count, increment: false); }
            catch { /* skip erros individuais */ }
        }
    }

    // ----------------------------------------------------------------------
    // Synonyms: lê synonyms.json e aplica FT.SYNUPDATE em idx:transactions.
    // Grupo de sinônimos faz queries tipo "luz" também matcharem "energia",
    // "eletropaulo", "cpfl", "enel"... DENTRO do FT.SEARCH text query.
    // ----------------------------------------------------------------------
    private async Task ApplySynonymsAsync(IDatabase db, CancellationToken ct)
    {
        var path = Path.Combine(_seedsDir, "synonyms.json");
        if (!File.Exists(path)) return;

        using var fs = File.OpenRead(path);
        using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("groups", out var groupsEl)) return;

        // FT.SYNUPDATE <index> <group_id> <SKIPINITIALSCAN> <term1> <term2> ...
        // Cada grupo recebe um ID estável (o nome do grupo no JSON).
        foreach (var group in groupsEl.EnumerateObject())
        {
            var groupId = group.Name;
            var terms = group.Value.EnumerateArray()
                                   .Select(t => t.GetString())
                                   .Where(s => !string.IsNullOrWhiteSpace(s))
                                   .Cast<string>()
                                   .ToArray();
            if (terms.Length < 2) continue;

            // NRedisStack tem SynUpdateAsync, ou via Execute direto:
            var args = new List<object> { TransactionIndex.Name, groupId };
            foreach (var t in terms) args.Add(t);
            try
            {
                await db.ExecuteAsync("FT.SYNUPDATE", args.ToArray());
            }
            catch { /* skip grupo com erro */ }
        }
    }

    private static async Task<long> CountDocsAsync(IDatabase db, string indexName)
    {
        try
        {
            var info = await db.FT().InfoAsync(indexName);
            return info?.NumDocs ?? 0;
        }
        catch { return 0; }
    }

    private static async Task DeletePrefixAsync(IDatabase db, string prefix)
    {
        var server = db.Multiplexer.GetServers().FirstOrDefault(s => s.IsConnected);
        if (server is null) return;
        var batch = new List<RedisKey>();
        await foreach (var k in server.KeysAsync(pattern: prefix + "*", pageSize: 200))
        {
            batch.Add(k);
            if (batch.Count >= 500)
            {
                await db.KeyDeleteAsync(batch.ToArray());
                batch.Clear();
            }
        }
        if (batch.Count > 0) await db.KeyDeleteAsync(batch.ToArray());
    }
}

public sealed record SeedReport(
    long DocsBefore,
    long DocsAdded,
    string Note,
    int AutocompleteTerms,
    int SynonymGroups);
