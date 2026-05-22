namespace Itau.Extrato.Search;

/// <summary>
/// Configuração runtime — pode ser mutada via <c>PUT /api/admin/settings</c>
/// pra o time do banco brincar com knobs sem rebuild. Singleton em memória;
/// resetar = restart do container (intencional — settings são pra demo).
///
/// Defaults vêm de env vars ou hardcoded sensible. TTL default do rewrite
/// cache é 5min: o cliente brasileiro espera ver transação cair no extrato
/// quase imediato, e o cache de rewrite é independente disso (o FT.SEARCH
/// sempre vai no índice atual), mas 5min reforça o feel "demo viva".
/// </summary>
public sealed class AppSettings
{
    public TimeSpan RewriteTtl { get; set; } = TimeSpan.FromMinutes(5);
    public string DefaultMode { get; set; } = "auto";

    public static AppSettings FromEnvironment()
    {
        var s = new AppSettings();
        if (int.TryParse(Environment.GetEnvironmentVariable("ITAU_REWRITE_TTL_SEC"), out var ttl) && ttl > 0)
            s.RewriteTtl = TimeSpan.FromSeconds(ttl);
        var defMode = Environment.GetEnvironmentVariable("ITAU_DEFAULT_MODE");
        if (!string.IsNullOrWhiteSpace(defMode)) s.DefaultMode = defMode.ToLowerInvariant();
        return s;
    }
}
