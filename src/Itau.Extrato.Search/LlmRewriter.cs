using System.Text.Json;
using System.Text.Json.Serialization;
using Itau.Extrato.Search.Telemetry;
using OpenAI;
using OpenAI.Chat;

namespace Itau.Extrato.Search;

/// <summary>
/// Pega uma query em linguagem natural PT-BR (long-tail), tipo "quais foram
/// os piques que eu fiz pra Dua esse mês?", e devolve um objeto estruturado
/// com filtros que o SearchService converte em FT.SEARCH determinístico.
///
/// Usa OpenAI Chat Completions com Structured Outputs (response_format
/// json_schema), garantindo que o output sempre obedeça ao schema —
/// dispensa parser defensivo.
///
/// Custo típico: ~250 tokens in + 80 tokens out = ~$0.00008 / chamada com
/// gpt-4o-mini. Demo bem barata.
/// </summary>
public sealed class LlmRewriter
{
    public const string DefaultModel = "gpt-4o-mini";

    private readonly ChatClient _client;
    public string Model { get; }

    public LlmRewriter(string apiKey, string model = DefaultModel)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("OPENAI_API_KEY obrigatório.", nameof(apiKey));
        Model = model;
        _client = new OpenAIClient(apiKey).GetChatClient(model);
    }

    private const string SystemPrompt = """
        Você converte queries em PT-BR sobre o extrato bancário do Itaú em filtros estruturados.

        A pessoa pode pedir coisas como:
          • "quais foram os piques que eu fiz pra Dua esse mês?"     → type=pix, direction=outbound, counterparty=Dua, date_from=início_mês_atual
          • "uber"                                                    → free_text=uber
          • "minhas contas de luz esse ano"                           → free_text=luz, date_from=início_ano_atual
          • "compras parceladas no Magalu"                            → counterparty=Magalu, installment_only=true
          • "pix recebido de mais de 100 reais"                       → type=pix, direction=inbound, amount_min=100
          • "fatura do cartão de outubro"                             → type=cartao_credito, date_from=2025-10-01, date_to=2025-10-31
          • "salário"                                                 → type=salario
          • "estornos"                                                → type=estorno

        REGRAS:
        • "piques" = "pix" (typo comum, PT-BR informal). "boleto" pode ser "boletinho".
        • Datas relativas: "esse mês" / "este mês" → mês corrente; "mês passado" → mês anterior; "essa semana" → últimos 7 dias; "esse ano" → ano corrente; "ano passado" → ano anterior.
        • Use a data atual fornecida no prompt do usuário pra calcular as datas absolutas.
        • Se o usuário citar um nome próprio ("Dua", "Juliana", "Felipe", "Don Ramon", "Miller", "Morenão", "Moreno", "Camila", "Pedro"), preencha `counterparty_match` com a raiz do nome (não use o nome completo, deixe o match parcial). Apelidos PT-BR: "Morenão"/"Moreno" = "Moreno", "Lipa" = "Dua", "Mãe"/"mainha" = "Juliana" (pra esse user), "Pai" = "Don Ramon". Sempre prefira a parte mais distintiva pra free-text matching.
        • Apelidos profissionais: "da Redis", "do trabalho", "do escritório" geralmente indicam Miller Moreno (colega de Redis); ignorar o sufixo no counterparty_match, mas isso é uma dica forte que o nome citado é Miller.
        • Direction: "enviei", "fiz pra", "mandei", "paguei" → outbound. "recebi", "caiu", "entrou" → inbound. Não inferir se não disser.
        • Se a query for muito vaga (só palavra), use `free_text` e nada mais.
        • Quando a query é sobre o memo/texto do Pix ("pix que falava sobre café", "pix do racha do uber"), preencha `free_text` com a palavra-chave — o FTS cobre o campo pix_message (texto livre que o cliente digita no app do banco).

        TIPOS específicos do Itaú (atenção!):
        • "fatura do cartão", "compras no crédito", "compras parceladas" → type=cartao_credito
        • "conta de luz", "conta de água", "internet", "celular" → NÃO preencha `type` (deixe null). Use `free_text` com a palavra-chave. Essas contas são débito automático mas têm texto descritivo único (AES Eletropaulo, Sabesp, Vivo Fibra…) que o text search captura via sinônimos.
        • "boleto" sozinho ou "boletos" → type=boleto (aluguel etc.)
        • "salário" → type=salario
        • "investi", "aplicação", "tesouro", "cdb" → type=investimento
        • "saque" → type=saque
        • "estorno", "estornei", "devolução" → type=estorno
        • "ifood", "rappi", "uber eats", "comida", "delivery" → não preencha `type`; use `free_text` com a palavra (sinônimos cobrem)
        • "uber", "99", "corrida" → não preencha `type`; use `free_text` com a palavra
        • Sempre preencha `interpretation` com uma frase curta em PT-BR explicando o que entendeu.
        """;

    private const string FilterSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "type":              { "type": ["string", "null"], "enum": ["pix", "ted", "doc", "boleto", "cartao_credito", "cartao_debito", "saque", "deposito", "estorno", "salario", "investimento", "tarifa", "iof", "debito_automatico", null] },
            "direction":         { "type": ["string", "null"], "enum": ["outbound", "inbound", null] },
            "category":          { "type": ["string", "null"], "enum": ["transferencias", "alimentacao", "transporte", "moradia", "saude", "lazer", "servicos", "investimentos", "salario", "compras", "educacao", "impostos", "outros", null] },
            "counterparty_match":{ "type": ["string", "null"] },
            "free_text":         { "type": ["string", "null"] },
            "date_from":         { "type": ["string", "null"], "description": "ISO date YYYY-MM-DD or null" },
            "date_to":           { "type": ["string", "null"], "description": "ISO date YYYY-MM-DD or null" },
            "amount_min":        { "type": ["number", "null"] },
            "amount_max":        { "type": ["number", "null"] },
            "installment_only":  { "type": ["boolean", "null"] },
            "interpretation":    { "type": "string" }
          },
          "required": ["type", "direction", "category", "counterparty_match", "free_text", "date_from", "date_to", "amount_min", "amount_max", "installment_only", "interpretation"]
        }
        """;

    public async Task<(RewrittenFilter Filter, string RawJson, int InTokens, int OutTokens)> RewriteAsync(
        string query,
        StageTimer? timer = null,
        CancellationToken ct = default)
    {
        var todayBrt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3));
        var userPrompt = $"""
            Data de hoje (BRT): {todayBrt:yyyy-MM-dd} ({todayBrt:dddd})
            Query do cliente: {query}

            Devolva o JSON estruturado com os filtros.
            """;

        var options = new ChatCompletionOptions
        {
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "extrato_filter",
                jsonSchema: BinaryData.FromString(FilterSchema),
                jsonSchemaIsStrict: true),
            Temperature = 0,
        };
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(SystemPrompt),
            new UserChatMessage(userPrompt),
        };

        using var _ = timer?.Stage("llm.rewrite");
        var resp = await _client.CompleteChatAsync(messages, options, ct);
        var completion = resp.Value;
        var rawJson = completion.Content.Count > 0 ? completion.Content[0].Text : "{}";
        var filter = JsonSerializer.Deserialize<RewrittenFilter>(rawJson) ?? new RewrittenFilter();
        return (filter, rawJson,
            completion.Usage?.InputTokenCount ?? 0,
            completion.Usage?.OutputTokenCount ?? 0);
    }
}

/// <summary>
/// Filtros estruturados emitidos pelo LLM (Structured Outputs do OpenAI).
/// Cada campo opcional (null = sem filtro nessa dimensão). O SearchService
/// junta esses em uma única expressão FT.SEARCH.
/// </summary>
public sealed class RewrittenFilter
{
    [JsonPropertyName("type")]               public string? Type { get; set; }
    [JsonPropertyName("direction")]          public string? Direction { get; set; }
    [JsonPropertyName("category")]           public string? Category { get; set; }
    [JsonPropertyName("counterparty_match")] public string? CounterpartyMatch { get; set; }
    [JsonPropertyName("free_text")]          public string? FreeText { get; set; }
    [JsonPropertyName("date_from")]          public string? DateFromIso { get; set; }
    [JsonPropertyName("date_to")]            public string? DateToIso { get; set; }
    [JsonPropertyName("amount_min")]         public decimal? AmountMin { get; set; }
    [JsonPropertyName("amount_max")]         public decimal? AmountMax { get; set; }
    [JsonPropertyName("installment_only")]   public bool? InstallmentOnly { get; set; }
    [JsonPropertyName("interpretation")]     public string? Interpretation { get; set; }

    public long? DateFromUnix => ParseDate(DateFromIso, endOfDay: false);
    public long? DateToUnix   => ParseDate(DateToIso,   endOfDay: true);

    private static long? ParseDate(string? iso, bool endOfDay)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        if (!DateOnly.TryParse(iso, out var d)) return null;
        var t = endOfDay ? new TimeOnly(23, 59, 59) : new TimeOnly(0, 0, 0);
        var dto = new DateTimeOffset(d, t, TimeSpan.FromHours(-3));
        return dto.ToUnixTimeSeconds();
    }
}
