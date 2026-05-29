using System.Text.RegularExpressions;

namespace Itau.Extrato.Search;

/// <summary>
/// Triage rápido (regex, 0-1ms, zero rede) pra decidir qual caminho a query
/// deve seguir:
///
///   <see cref="QueryClass.Keyword"/> — termos simples, sem data/range/direção.
///     Resolução: FT.SEARCH puro com synonyms + TAG. Latência alvo: 1-10ms.
///     Cobre ~95% das queries de extrato real.
///
///   <see cref="QueryClass.NaturalLanguage"/> — frase com data relativa,
///     direção (enviei/recebi), valor com range, ou interrogação. Resolução:
///     cache check → LLM rewrite (se miss) → FT.SEARCH com filtros estruturados.
///
///   <see cref="QueryClass.Empty"/> — string vazia/branca.
///
/// O classifier nunca consulta Redis nem OpenAI; é puro CPU.
/// </summary>
public static class QueryClassifier
{
    // Indicadores de natural language (PT-BR). Match ANY → NL path.
    private static readonly Regex NaturalLanguagePattern = new(
        @"\b(" +
            // Interrogativos
            @"quais|quanto|qual|quando|onde|como|por\s*que|pra\s*onde|" +
            // Datas relativas
            @"hoje|ontem|amanh[ãa]|esta\s+semana|essa\s+semana|" +
            @"este\s+m[êe]s|esse\s+m[êe]s|m[êe]s\s+passado|m[êe]s\s+atual|" +
            @"este\s+ano|esse\s+ano|ano\s+passado|ano\s+atual|" +
            @"[uúú]ltimos?\s+\d+\s+(dias?|semanas?|meses?|anos?)|" +
            // Pronomes possessivos (indicam contexto pessoal)
            @"meu\s|minha\s|meus\s|minhas\s|" +
            // Verbos em 1ª pessoa (intent: outbound/inbound)
            @"eu\s+(fiz|paguei|recebi|mandei|enviei|gastei|comprei|tirei|saquei|investi|apliquei)|" +
            // Ações sem pronome (também direção)
            @"(enviei|paguei|recebi|gastei|comprei|investi|apliquei|saquei|mandei)\b|" +
            // Ranges de valor
            @"mais\s+de\s+r?\$?\s*\d|menos\s+de\s+r?\$?\s*\d|acima\s+de\s+r?\$?\s*\d|abaixo\s+de\s+r?\$?\s*\d|entre\s+r?\$?\s*\d.*?e\s+r?\$?\s*\d|" +
            // Tipos compostos
            @"fatura\s+(do|de)|cart[ãa]o\s+(de\s+cr[ée]dito|de\s+d[ée]bito)|conta\s+de\s+(luz|[áa]gua|internet|celular|telefone|g[áa]s)|" +
            // Compras com qualificador — \w* nos sufixos pra match "parceladas" inteiro
            // (o \b final do pattern exterior exigia boundary depois de "parcelad",
            // que falhava com "parceladas" porque "a" é word char).
            @"compras?\s+(parcelad\w*|no\s+cr[ée]dito|no\s+d[ée]bito|do\s+m[êe]s)|" +
            // Composições explícitas (queries que claramente envolvem mais que
            // 1 palavra-chave). Nomes simples como "salário", "investimento"
            // sozinhos vão pelo keyword path (FT.SEARCH + synonyms basta).
            @"vencimento|vencendo|aplicac[ãa]o" +
        @")\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static QueryClass Classify(string? query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return QueryClass.Empty;

        // Atalho: 1 palavra curta sem nada → quase certo keyword
        var wordCount = q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        if (NaturalLanguagePattern.IsMatch(q)) return QueryClass.NaturalLanguage;

        // 4+ palavras sem nenhum marker — provavelmente ainda é frase. Vai pra NL
        // pra dar uma chance ao LLM tentar entender. Caso comum: "compras parceladas
        // no Magalu" (4 palavras, "parcelad" tá no regex, então já pega — esse if
        // cobre só os edge cases que escaparam).
        if (wordCount >= 5) return QueryClass.NaturalLanguage;

        return QueryClass.Keyword;
    }
}

public enum QueryClass
{
    Empty,
    Keyword,
    NaturalLanguage,
}
