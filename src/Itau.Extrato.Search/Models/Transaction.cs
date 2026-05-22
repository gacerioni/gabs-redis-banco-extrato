using System.Text.Json.Serialization;

namespace Itau.Extrato.Search.Models;

/// <summary>
/// Uma linha do extrato. Mapeia 1-pra-1 com o que o cliente vê no app/web do
/// Itaú — descrição, valor, contraparte, tipo, canal.
///
/// JSON em Redis (key = <c>txn:{id}</c>) usa snake_case (System.Text.Json
/// usa o JsonPropertyName de cada campo) pra que FT.SEARCH com paths tipo
/// <c>$.user_id</c> e <c>$.amount_brl</c> bata exatamente.
///
/// Convenções de sinal:
///   AmountBrl &lt; 0  → débito (saída) — Direction=outbound
///   AmountBrl &gt; 0  → crédito (entrada) — Direction=inbound
/// </summary>
public sealed record Transaction(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("date")] long DateUnix,
    [property: JsonPropertyName("amount_brl")] decimal AmountBrl,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("counterparty_name")] string? CounterpartyName,
    [property: JsonPropertyName("counterparty_doc_masked")] string? CounterpartyDocMasked,
    // Mensagem de texto livre que vem com Pix (campo "Mensagem ao destinatário"
    // do app do Itaú/qualquer banco BR). Indexado como TEXT em idx:transactions
    // pra ficar pesquisável via FT.SEARCH — busca "racha", "uber", "almoço"
    // deve encontrar Pix cujo memo contém essas palavras, não só a description.
    [property: JsonPropertyName("pix_message")] string? PixMessage,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("installment")] string? Installment,
    [property: JsonPropertyName("balance_after")] decimal? BalanceAfter,
    [property: JsonPropertyName("embedding")] float[]? Embedding);

/// <summary>Constantes/dicionários enumerados, evita typo.</summary>
public static class TransactionType
{
    public const string Pix = "pix";
    public const string Ted = "ted";
    public const string Doc = "doc";
    public const string Boleto = "boleto";
    public const string CartaoCredito = "cartao_credito";
    public const string CartaoDebito = "cartao_debito";
    public const string Saque = "saque";
    public const string Deposito = "deposito";
    public const string Estorno = "estorno";
    public const string Salario = "salario";
    public const string Investimento = "investimento";
    public const string TarifaServico = "tarifa";
    public const string Iof = "iof";
    public const string DebitoAutomatico = "debito_automatico";
}

public static class TransactionDirection
{
    public const string Outbound = "outbound";  // saiu da conta
    public const string Inbound = "inbound";    // entrou na conta
}

public static class TransactionCategory
{
    public const string Transferencias = "transferencias";
    public const string Alimentacao = "alimentacao";
    public const string Transporte = "transporte";
    public const string Moradia = "moradia";
    public const string Saude = "saude";
    public const string Lazer = "lazer";
    public const string ServicosUtilidades = "servicos";
    public const string Investimentos = "investimentos";
    public const string Salario = "salario";
    public const string Compras = "compras";
    public const string Educacao = "educacao";
    public const string Impostos = "impostos";
    public const string Outros = "outros";
}

public static class TransactionChannel
{
    public const string AppMobile = "app_mobile";
    public const string AppWeb = "app_web";
    public const string Atm = "atm";
    public const string Agencia = "agencia";
    public const string DebitoAutomatico = "debito_automatico";
    public const string CaixaEletronico = "caixa_eletronico";
    public const string Pos = "pos";  // maquininha
}
