using NRedisStack;
using NRedisStack.RedisStackCommands;
using NRedisStack.Search;
using NRedisStack.Search.Literals.Enums;
using StackExchange.Redis;
using Itau.Extrato.Search.Infra;

namespace Itau.Extrato.Search.Schemas;

/// <summary>
/// O índice de busca do extrato — coração técnico do PoV. Em uma chamada de
/// <c>FT.SEARCH</c> você combina:
/// <list type="bullet">
///   <item>TEXT search nas descrições do extrato e nomes de contraparte (FTS)</item>
///   <item>TAG filters por tipo (pix/ted/cartao), direção, categoria, canal, user_id</item>
///   <item>NUMERIC filters por data (range) e valor (range)</item>
///   <item>KNN vetorial nas descrições (VSS semântico)</item>
/// </list>
/// Tudo isso em um único round-trip. Esse é o pitch contra o OpenSearch, que
/// precisa de várias chamadas REST + range shaping pra fazer hybrid.
///
/// Key prefix: <c>txn:</c>
///
/// <para>
/// FT.SUGADD/SUGGET é um índice <i>separado</i> (não FT.CREATE) — vive em
/// <see cref="SuggestionIndex"/>.
/// </para>
/// </summary>
public static class TransactionIndex
{
    public const string Name = "idx:transactions";
    public const string Prefix = "txn:";

    public static async Task EnsureCreatedAsync(IDatabase db, int dim = EmbeddingsClient.DefaultDim)
    {
        var ft = db.FT();
        try { await ft.InfoAsync(Name); return; } catch { /* not created yet */ }

        var schema = new Schema()
            // Tags categóricas — filtros exatos, rápidos
            .AddTagField(new FieldName("$.user_id", "user_id"))
            .AddTagField(new FieldName("$.type", "type"))
            .AddTagField(new FieldName("$.direction", "direction"))
            .AddTagField(new FieldName("$.category", "category"))
            .AddTagField(new FieldName("$.channel", "channel"))
            .AddTagField(new FieldName("$.installment", "installment"))
            // Numéricos — sortable pros SORTBY date DESC do extrato
            .AddNumericField(new FieldName("$.date", "date"), sortable: true)
            .AddNumericField(new FieldName("$.amount_brl", "amount_brl"), sortable: true)
            .AddNumericField(new FieldName("$.balance_after", "balance_after"))
            // Texto — peso maior na descrição, menor na contraparte e na
            // mensagem livre do Pix (memo digitado pelo cliente no app).
            .AddTextField(new FieldName("$.description", "description"), weight: 2.0)
            .AddTextField(new FieldName("$.counterparty_name", "counterparty_name"), weight: 1.5)
            .AddTextField(new FieldName("$.pix_message", "pix_message"), weight: 1.2)
            // Vector — KNN COSINE pra VSS semântico
            .AddVectorField(
                new FieldName("$.embedding", "embedding"),
                Schema.VectorField.VectorAlgo.FLAT,
                new Dictionary<string, object>
                {
                    ["TYPE"] = "FLOAT32",
                    ["DIM"] = dim,
                    ["DISTANCE_METRIC"] = "COSINE",
                });

        await ft.CreateAsync(Name,
            new FTCreateParams().On(IndexDataType.JSON).Prefix(Prefix),
            schema);
    }

    public static async Task DropAsync(IDatabase db, bool keepDocs = false)
    {
        var ft = db.FT();
        try { await ft.DropIndexAsync(Name, dd: !keepDocs); } catch { }
    }
}
