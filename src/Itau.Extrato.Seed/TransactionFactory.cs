using Itau.Extrato.Search.Models;

namespace Itau.Extrato.Seed;

/// <summary>
/// Gera transações realistas no padrão Itaú a partir de um UserProfile,
/// distribuídas ao longo de um período (mês a mês). Determinístico por seed.
///
/// Cada mês gera:
///   • 1 salário (crédito, dia fixo do profile)
///   • 1 aluguel (boleto, dia fixo do profile)
///   • 4 utilities: AES Eletropaulo, Sabesp, Vivo Fibra, TIM Celular (DA)
///   • 3-4 streaming (Netflix/Spotify/Prime/Disney+ no cartão de crédito)
///   • 4-5 supermercado (semanal, débito)
///   • 8-15 delivery (iFood/Rappi, débito/crédito)
///   • 6-12 transporte (Uber/99/posto)
///   • 3-6 cartão crédito médio (compras online, restaurantes)
///   • 0-2 parcelado (Magalu/Amazon, "PARC N/12")
///   • 4-8 Pix (mix outbound/inbound entre os recipients do profile)
///   • 1-2 investimento (CDB Itaú aplicação/resgate)
///   • 0-1 estorno
///   • 0-1 saque ATM
///   • 1 tarifa de manutenção
///
/// Total típico: ~70-130 lançamentos/mês. Em 12 meses pra perfil "premium",
/// chega em ~1500. Multiplica/divide por LifestyleMultiplier do profile.
/// </summary>
public static class TransactionFactory
{
    public static List<Transaction> Generate(
        UserProfile profile,
        DateOnly startMonth,
        int months,
        int seed)
    {
        var rng = new Random(seed);
        var all = new List<Transaction>();
        var balance = profile.StartingBalance;
        var lifeMul = profile.LifestyleMultiplier;
        int seq = 0;

        for (int m = 0; m < months; m++)
        {
            var monthStart = startMonth.AddMonths(m);
            var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);

            // ---- Salário (fixo, dia X do mês) ------------------------------
            balance = AddTxn(all, ref seq, profile, rng,
                date: SafeDate(monthStart.Year, monthStart.Month, profile.SalaryDayOfMonth, 9),
                amount: profile.SalaryAmount,
                type: TransactionType.Salario,
                direction: TransactionDirection.Inbound,
                description: $"CREDITO SALARIO {profile.EmployerName}",
                counterpartyName: ToTitleCase(profile.EmployerName),
                counterpartyDoc: profile.EmployerDocMasked,
                category: TransactionCategory.Salario,
                channel: TransactionChannel.DebitoAutomatico,
                installment: null,
                currentBalance: balance, affectsBalance: true);

            // ---- Aluguel (boleto, dia Y) -----------------------------------
            balance = AddTxn(all, ref seq, profile, rng,
                date: SafeDate(monthStart.Year, monthStart.Month, profile.RentDayOfMonth, 12),
                amount: -profile.RentAmount,
                type: TransactionType.Boleto,
                direction: TransactionDirection.Outbound,
                description: $"BOLETO PAGO {profile.LandlordName} ALUGUEL",
                counterpartyName: ToTitleCase(profile.LandlordName),
                counterpartyDoc: profile.LandlordDocMasked,
                category: TransactionCategory.Moradia,
                channel: TransactionChannel.AppMobile,
                installment: null,
                currentBalance: balance, affectsBalance: true);

            // ---- Utilities (4 contas mensais via DA) -----------------------
            foreach (var u in Utilities(profile, m))
            {
                balance = AddTxn(all, ref seq, profile, rng,
                    date: SafeDate(monthStart.Year, monthStart.Month, u.Day, 10),
                    amount: -u.Amount,
                    type: TransactionType.DebitoAutomatico,
                    direction: TransactionDirection.Outbound,
                    description: $"DEBITO AUTOMATICO {u.Description}",
                    counterpartyName: ToTitleCase(u.Merchant),
                    counterpartyDoc: u.Doc,
                    category: u.Category,
                    channel: TransactionChannel.DebitoAutomatico,
                    installment: null,
                    currentBalance: balance, affectsBalance: true);
            }

            // ---- Streaming (cartão de crédito) -----------------------------
            foreach (var s in StreamingSubs(rng, lifeMul))
            {
                balance = AddTxn(all, ref seq, profile, rng,
                    date: SafeDate(monthStart.Year, monthStart.Month, s.Day, 14),
                    amount: -s.Amount,
                    type: TransactionType.CartaoCredito,
                    direction: TransactionDirection.Outbound,
                    description: $"COMPRA NO CREDITO {s.Merchant}",
                    counterpartyName: ToTitleCase(s.Merchant),
                    counterpartyDoc: null,
                    category: TransactionCategory.Lazer,
                    channel: TransactionChannel.AppWeb,
                    installment: null,
                    currentBalance: balance, affectsBalance: false);
            }

            // ---- Supermercado (semanal, débito) ----------------------------
            for (int week = 0; week < 4; week++)
            {
                if (rng.NextDouble() < 0.85)
                {
                    var day = Math.Min(daysInMonth, 5 + week * 7 + rng.Next(0, 3));
                    var market = PickRandom(rng, Markets);
                    var amount = (decimal)(180 + rng.NextDouble() * 320) * lifeMul / 2;
                    balance = AddTxn(all, ref seq, profile, rng,
                        date: SafeDate(monthStart.Year, monthStart.Month, day, 19),
                        amount: -Round2(amount),
                        type: TransactionType.CartaoDebito,
                        direction: TransactionDirection.Outbound,
                        description: $"COMPRA NO DEBITO {market}",
                        counterpartyName: ToTitleCase(market),
                        counterpartyDoc: null,
                        category: TransactionCategory.Alimentacao,
                        channel: TransactionChannel.Pos,
                        installment: null,
                        currentBalance: balance, affectsBalance: true);
                }
            }

            // ---- Delivery (iFood/Rappi) -----------------------------------
            int deliveryCount = 12 + rng.Next(0, 10) + (lifeMul - 1) * 2;
            for (int i = 0; i < deliveryCount; i++)
            {
                var day = rng.Next(1, daysInMonth + 1);
                var merchant = PickRandom(rng, DeliveryMerchants);
                var amount = Round2((decimal)(28 + rng.NextDouble() * 90));
                var isCredit = rng.NextDouble() < 0.6;
                balance = AddTxn(all, ref seq, profile, rng,
                    date: SafeDate(monthStart.Year, monthStart.Month, day, rng.Next(11, 22)),
                    amount: -amount,
                    type: isCredit ? TransactionType.CartaoCredito : TransactionType.CartaoDebito,
                    direction: TransactionDirection.Outbound,
                    description: $"COMPRA NO {(isCredit ? "CREDITO" : "DEBITO")} {merchant}",
                    counterpartyName: ToTitleCase(merchant.Replace("IFD*", "").Replace("RPP*", "")),
                    counterpartyDoc: null,
                    category: TransactionCategory.Alimentacao,
                    channel: TransactionChannel.Pos,
                    installment: null,
                    currentBalance: balance, affectsBalance: !isCredit);
            }

            // ---- Transporte (Uber/99/posto) -------------------------------
            int transportCount = 10 + rng.Next(0, 10);
            for (int i = 0; i < transportCount; i++)
            {
                var day = rng.Next(1, daysInMonth + 1);
                var isFuel = rng.NextDouble() < 0.25;
                var merchant = isFuel ? PickRandom(rng, GasStations) : PickRandom(rng, RideShareMerchants);
                var amount = isFuel
                    ? Round2((decimal)(150 + rng.NextDouble() * 250))
                    : Round2((decimal)(12 + rng.NextDouble() * 55));
                balance = AddTxn(all, ref seq, profile, rng,
                    date: SafeDate(monthStart.Year, monthStart.Month, day, rng.Next(7, 23)),
                    amount: -amount,
                    type: TransactionType.CartaoCredito,
                    direction: TransactionDirection.Outbound,
                    description: $"COMPRA NO CREDITO {merchant}",
                    counterpartyName: ToTitleCase(merchant),
                    counterpartyDoc: null,
                    category: isFuel ? TransactionCategory.Transporte : TransactionCategory.Transporte,
                    channel: TransactionChannel.Pos,
                    installment: null,
                    currentBalance: balance, affectsBalance: false);
            }

            // ---- Compras online médias ------------------------------------
            int shoppingCount = 5 + rng.Next(0, 6);
            for (int i = 0; i < shoppingCount; i++)
            {
                var day = rng.Next(1, daysInMonth + 1);
                var merchant = PickRandom(rng, OnlineShops);
                var amount = Round2((decimal)(80 + rng.NextDouble() * 450) * lifeMul / 2);
                balance = AddTxn(all, ref seq, profile, rng,
                    date: SafeDate(monthStart.Year, monthStart.Month, day, rng.Next(10, 22)),
                    amount: -amount,
                    type: TransactionType.CartaoCredito,
                    direction: TransactionDirection.Outbound,
                    description: $"COMPRA NO CREDITO {merchant}",
                    counterpartyName: ToTitleCase(merchant),
                    counterpartyDoc: null,
                    category: TransactionCategory.Compras,
                    channel: TransactionChannel.AppWeb,
                    installment: null,
                    currentBalance: balance, affectsBalance: false);
            }

            // ---- Parcelado (1-4 por mês — bump pra deixar a query "compras parceladas" rica) -
            int parceladoCount = 1 + rng.Next(0, 4);
            for (int i = 0; i < parceladoCount; i++)
            {
                var day = rng.Next(1, daysInMonth + 1);
                var merchant = PickRandom(rng, BigShops);
                var totalParcelas = PickRandom(rng, new[] { 6, 10, 12 });
                var currentParcela = rng.Next(1, totalParcelas + 1);
                var amount = Round2((decimal)(80 + rng.NextDouble() * 250));
                balance = AddTxn(all, ref seq, profile, rng,
                    date: SafeDate(monthStart.Year, monthStart.Month, day, 15),
                    amount: -amount,
                    type: TransactionType.CartaoCredito,
                    direction: TransactionDirection.Outbound,
                    description: $"COMPRA NO CREDITO {merchant} PARC {currentParcela:D2}/{totalParcelas:D2}",
                    counterpartyName: ToTitleCase(merchant),
                    counterpartyDoc: null,
                    category: TransactionCategory.Compras,
                    channel: TransactionChannel.AppWeb,
                    installment: $"{currentParcela:D2}/{totalParcelas:D2}",
                    currentBalance: balance, affectsBalance: false);
            }

            // ---- Pix (mix in/out) -----------------------------------------
            // GARANTIA: 2 Pix outbound + 1 inbound pra recipient[0] (esposa)
            // em todo mês. Random não cooperava — alguns meses ficavam sem
            // nenhum Pix pra esposa, quebrando demos como "piques pra Dua".
            for (int g = 0; g < 3; g++)
            {
                var day = rng.Next(1, daysInMonth + 1);
                var (recNameG, recDocG) = profile.PixRecipients[0];
                var outboundG = g < 2;  // 2 outbound, 1 inbound
                var amountG = Round2((decimal)(50 + rng.NextDouble() * 500));
                balance = AddTxn(all, ref seq, profile, rng,
                    date: SafeDate(monthStart.Year, monthStart.Month, day, rng.Next(8, 22)),
                    amount: outboundG ? -amountG : amountG,
                    type: TransactionType.Pix,
                    direction: outboundG ? TransactionDirection.Outbound : TransactionDirection.Inbound,
                    description: outboundG ? $"PIX ENVIADO {recNameG.ToUpperInvariant()}" : $"PIX RECEBIDO {recNameG.ToUpperInvariant()}",
                    counterpartyName: recNameG,
                    counterpartyDoc: recDocG,
                    category: TransactionCategory.Transferencias,
                    channel: TransactionChannel.AppMobile,
                    installment: null,
                    currentBalance: balance, affectsBalance: true,
                    pixMessage: MaybePixMemo(rng, 0.4));  // 40% nos Pix da esposa — relação próxima, memos comuns
            }

            // Resto dos Pix: weighted random como antes.
            int pixCount = 6 + rng.Next(0, 8);
            for (int i = 0; i < pixCount; i++)
            {
                var day = rng.Next(1, daysInMonth + 1);
                var weights = Enumerable.Range(0, profile.PixRecipients.Length)
                                        .Select(idx => idx == 0 ? 2 : 1).ToArray();
                var totalWeight = weights.Sum();
                var pick = rng.Next(totalWeight);
                int chosen = 0; int acc = 0;
                for (int k = 0; k < weights.Length; k++)
                {
                    acc += weights[k];
                    if (pick < acc) { chosen = k; break; }
                }
                var (recName, recDoc) = profile.PixRecipients[chosen];
                var outbound = rng.NextDouble() < 0.65;
                var amount = Round2((decimal)(30 + rng.NextDouble() * 400));
                balance = AddTxn(all, ref seq, profile, rng,
                    date: SafeDate(monthStart.Year, monthStart.Month, day, rng.Next(8, 22)),
                    amount: outbound ? -amount : amount,
                    type: TransactionType.Pix,
                    direction: outbound ? TransactionDirection.Outbound : TransactionDirection.Inbound,
                    description: outbound
                        ? $"PIX ENVIADO {recName.ToUpperInvariant()}"
                        : $"PIX RECEBIDO {recName.ToUpperInvariant()}",
                    counterpartyName: recName,
                    counterpartyDoc: recDoc,
                    category: TransactionCategory.Transferencias,
                    channel: TransactionChannel.AppMobile,
                    installment: null,
                    currentBalance: balance, affectsBalance: true,
                    pixMessage: MaybePixMemo(rng, 0.30));  // 30% dos Pix variáveis têm memo
            }

            // ---- Saúde (farmácia, eventual) -------------------------------
            if (rng.NextDouble() < 0.7)
            {
                var day = rng.Next(1, daysInMonth + 1);
                var pharmacy = PickRandom(rng, Pharmacies);
                var amount = Round2((decimal)(35 + rng.NextDouble() * 180));
                balance = AddTxn(all, ref seq, profile, rng,
                    date: SafeDate(monthStart.Year, monthStart.Month, day, 16),
                    amount: -amount,
                    type: TransactionType.CartaoDebito,
                    direction: TransactionDirection.Outbound,
                    description: $"COMPRA NO DEBITO {pharmacy}",
                    counterpartyName: ToTitleCase(pharmacy),
                    counterpartyDoc: null,
                    category: TransactionCategory.Saude,
                    channel: TransactionChannel.Pos,
                    installment: null,
                    currentBalance: balance, affectsBalance: true);
            }

            // ---- Investimento (CDB ou Tesouro) ----------------------------
            if (rng.NextDouble() < 0.8)
            {
                var day = profile.SalaryDayOfMonth + rng.Next(1, 4);
                var product = PickRandom(rng, new[] { "CDB ITAU", "TESOURO SELIC", "FUNDO RENDA FIXA", "TESOURO IPCA+" });
                var amount = Round2((decimal)(500 + rng.NextDouble() * 2000) * lifeMul);
                balance = AddTxn(all, ref seq, profile, rng,
                    date: SafeDate(monthStart.Year, monthStart.Month, Math.Min(day, daysInMonth), 11),
                    amount: -amount,
                    type: TransactionType.Investimento,
                    direction: TransactionDirection.Outbound,
                    description: $"APLICACAO {product}",
                    counterpartyName: "Itaú Investimentos",
                    counterpartyDoc: null,
                    category: TransactionCategory.Investimentos,
                    channel: TransactionChannel.AppMobile,
                    installment: null,
                    currentBalance: balance, affectsBalance: true);
            }

            // ---- Saque ATM (raro) -----------------------------------------
            if (rng.NextDouble() < 0.4)
            {
                var day = rng.Next(1, daysInMonth + 1);
                var amount = PickRandom(rng, new[] { 100m, 200m, 300m, 500m });
                balance = AddTxn(all, ref seq, profile, rng,
                    date: SafeDate(monthStart.Year, monthStart.Month, day, 13),
                    amount: -amount,
                    type: TransactionType.Saque,
                    direction: TransactionDirection.Outbound,
                    description: "SAQUE 24HORAS",
                    counterpartyName: "Itaú 24h",
                    counterpartyDoc: null,
                    category: TransactionCategory.Outros,
                    channel: TransactionChannel.Atm,
                    installment: null,
                    currentBalance: balance, affectsBalance: true);
            }

            // ---- Estorno (eventual) ---------------------------------------
            if (rng.NextDouble() < 0.15)
            {
                var day = rng.Next(1, daysInMonth + 1);
                var merchant = PickRandom(rng, OnlineShops);
                var amount = Round2((decimal)(40 + rng.NextDouble() * 200));
                balance = AddTxn(all, ref seq, profile, rng,
                    date: SafeDate(monthStart.Year, monthStart.Month, day, 14),
                    amount: amount,
                    type: TransactionType.Estorno,
                    direction: TransactionDirection.Inbound,
                    description: $"ESTORNO COMPRA {merchant}",
                    counterpartyName: ToTitleCase(merchant),
                    counterpartyDoc: null,
                    category: TransactionCategory.Compras,
                    channel: TransactionChannel.AppWeb,
                    installment: null,
                    currentBalance: balance, affectsBalance: true);
            }

            // ---- Tarifa mensal --------------------------------------------
            balance = AddTxn(all, ref seq, profile, rng,
                date: SafeDate(monthStart.Year, monthStart.Month, Math.Min(daysInMonth, 28), 8),
                amount: -29.90m,
                type: TransactionType.TarifaServico,
                direction: TransactionDirection.Outbound,
                description: "TARIFA MENSAL PACOTE SERVICOS",
                counterpartyName: "Itaú Unibanco",
                counterpartyDoc: null,
                category: TransactionCategory.Outros,
                channel: TransactionChannel.DebitoAutomatico,
                installment: null,
                currentBalance: balance, affectsBalance: true);
        }

        // ---- Easter egg: Pix Gabriel → "Morenão da Redis" (Miller Moreno) -
        // 6 Pix engraçados no mês atual com mensagens livres, pra demonstrar
        // FTS no campo pix_message (memo escrito pelo usuário no app do banco).
        // Só roda no perfil do Gabriel — fica fora dos outros users.
        if (profile.UserId == "gabriel_cerioni")
        {
            AddEasterEggMoreno(all, ref seq, profile, rng);
        }

        // Ordenar por data
        return all.OrderBy(t => t.DateUnix).ToList();
    }

    /// <summary>
    /// Conjunto fixo (com pequena variação aleatória) de Pix Gabriel → Miller
    /// Moreno no mês corrente, cada um com uma mensagem livre. Demonstra:
    ///   • FTS sobre o pix_message (busca "café", "racha", "cerveja" deve achar)
    ///   • LLM rewrite reconhecendo "Morenão da Redis" como counterparty
    ///   • Que o sistema modela o campo opcional de mensagem do Pix BR
    /// O easter egg fica "agulha no palheiro" — só busca específica acha.
    /// </summary>
    private static void AddEasterEggMoreno(List<Transaction> list, ref int seq, UserProfile p, Random rng)
    {
        var miller = p.PixRecipients.FirstOrDefault(r => r.Name.Contains("Miller", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrEmpty(miller.Name)) return;

        // Mês atual em BRT
        var nowBrt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-3));
        var year = nowBrt.Year;
        var month = nowBrt.Month;
        var daysInMonth = DateTime.DaysInMonth(year, month);
        var todayDay = Math.Min(nowBrt.Day, daysInMonth);

        // Set de Pix Gabriel → Morenão, todos OUTBOUND, com memo escrito.
        // Datas espalhadas nos últimos 20 dias do mês corrente (limitando ao
        // dia de hoje pra não criar tx no futuro).
        var memos = new[]
        {
            ("racha do uber ontem rapa, valeu",                                        28.50m),
            ("cerveja do happy hour de quinta",                                        54.00m),
            ("café e pão de queijo do food truck que você gostou",                     18.40m),
            ("aposta do brasileirão — paguei como combinamos 🤝",                      50.00m),
            ("almoço de terça, tu deixou a carteira em casa kkkk",                     67.80m),
            ("vai render — bonus do PoV da Redis, te garanto",                        100.00m),
        };

        // Distribuir nos últimos 18 dias (ou até hoje, o que vier primeiro).
        var spreadDays = Math.Min(18, todayDay - 1);
        if (spreadDays < memos.Length) spreadDays = memos.Length;
        var dayStep = Math.Max(1, spreadDays / memos.Length);

        for (int i = 0; i < memos.Length; i++)
        {
            var day = Math.Min(todayDay, 1 + i * dayStep + rng.Next(0, 2));
            var date = new DateTimeOffset(year, month, day, 12 + rng.Next(0, 8), rng.Next(0, 60), 0, TimeSpan.FromHours(-3));
            var (memo, amount) = memos[i];
            seq++;
            list.Add(new Transaction(
                Id: $"txn_{p.UserId}_{date:yyyyMMdd}_{seq:D5}_morenao",
                UserId: p.UserId,
                DateUnix: date.ToUnixTimeSeconds(),
                AmountBrl: -amount,
                Type: TransactionType.Pix,
                Direction: TransactionDirection.Outbound,
                Description: $"PIX ENVIADO {miller.Name.ToUpperInvariant()}",
                CounterpartyName: miller.Name,
                CounterpartyDocMasked: miller.DocMasked,
                PixMessage: memo,
                Category: TransactionCategory.Transferencias,
                Channel: TransactionChannel.AppMobile,
                Installment: null,
                BalanceAfter: null,  // BalanceAfter é setado depois — ordering por data; aqui só agulha no palheiro
                Embedding: null));
        }
    }

    // ---------------- Helpers ----------------

    /// <summary>
    /// Adiciona uma transação na lista e retorna o novo saldo corrente.
    /// <para>
    /// <c>affectsBalance</c>: <b>true</b> pra ops de conta corrente (Pix,
    /// salário, boleto, débito, débito automático, saque, investimento). A
    /// transação atualiza <c>currentBalance</c> e grava <c>BalanceAfter</c>.
    /// </para>
    /// <para>
    /// <c>false</c> pra cartão de crédito (não afeta saldo da conta corrente
    /// no momento da compra; só na hora de pagar a fatura). Grava
    /// <c>BalanceAfter=null</c> e retorna <c>currentBalance</c> inalterado.
    /// </para>
    /// <para>
    /// Antes esse método tinha um bug: <c>balance: null</c> retornava
    /// <c>0m</c>, zerando o running balance ao longo da geração. Por isso o
    /// saldo no extrato vinha em R\$ 420 em vez dos ~100k esperados.
    /// </para>
    /// </summary>
    private static decimal AddTxn(
        List<Transaction> list, ref int seq, UserProfile p, Random rng,
        DateTimeOffset date, decimal amount, string type, string direction,
        string description, string? counterpartyName, string? counterpartyDoc,
        string category, string channel, string? installment,
        decimal currentBalance, bool affectsBalance,
        string? pixMessage = null)
    {
        seq++;
        var newBalance = affectsBalance ? currentBalance + amount : currentBalance;
        list.Add(new Transaction(
            Id: $"txn_{p.UserId}_{date:yyyyMMdd}_{seq:D5}",
            UserId: p.UserId,
            DateUnix: date.ToUnixTimeSeconds(),
            AmountBrl: amount,
            Type: type,
            Direction: direction,
            Description: description,
            CounterpartyName: counterpartyName,
            CounterpartyDocMasked: counterpartyDoc,
            PixMessage: pixMessage,
            Category: category,
            Channel: channel,
            Installment: installment,
            BalanceAfter: affectsBalance ? Round2(newBalance) : null,
            Embedding: null));
        return newBalance;
    }

    private static DateTimeOffset SafeDate(int y, int m, int d, int hour)
    {
        var maxDay = DateTime.DaysInMonth(y, m);
        return new DateTimeOffset(y, m, Math.Min(d, maxDay), hour, 0, 0, TimeSpan.FromHours(-3));
    }

    private static T PickRandom<T>(Random rng, IList<T> list) => list[rng.Next(list.Count)];

    private static decimal Round2(decimal x) => Math.Round(x, 2, MidpointRounding.AwayFromZero);

    // Memos genéricos pra Pix — populados aleatoriamente em ~30% dos Pix
    // pra demonstrar que o campo pix_message é indexado e pesquisável via FT.SEARCH
    // (não só os do easter egg do Morenão).
    private static readonly string[] PixMemosCommon =
    {
        "racha do almoço",
        "uber compartilhado",
        "rateio da pizza",
        "cota do churrasco",
        "café e pão de queijo",
        "presente de aniversário",
        "fatura do clube",
        "vaquinha aniversário galera",
        "valeu pelo café",
        "babá da semana",
        "fiquei devendo",
        "obrigado!",
        "almoço de domingo",
        "rateio happy hour",
        "passagem combinada",
        "doação amiga",
        "ingresso show",
        "fica devendo agora você",
    };

    private static string? MaybePixMemo(Random rng, double probability = 0.30)
        => rng.NextDouble() < probability ? PixMemosCommon[rng.Next(PixMemosCommon.Length)] : null;

    private static string ToTitleCase(string upper)
    {
        var parts = upper.ToLowerInvariant().Split(' ');
        return string.Join(" ", parts.Select(p => p.Length == 0 ? p : char.ToUpper(p[0]) + p[1..]));
    }

    // ---------------- Catalogs ----------------

    private static (int Day, decimal Amount, string Description, string Merchant, string Doc, string Category)[] Utilities(UserProfile p, int monthSeed)
    {
        // Variação leve mês a mês pra parecer real (consumo flutua)
        var rng = new Random(p.UserId.GetHashCode() + monthSeed);
        return new[]
        {
            (12, Round2(180 + (decimal)rng.NextDouble() * 220), "AES ELETROPAULO CONTA DE LUZ", "AES Eletropaulo", "**.555.666/0001-**", TransactionCategory.ServicosUtilidades),
            (15, Round2(60 + (decimal)rng.NextDouble() * 80),   "SABESP CONTA DE AGUA",         "Sabesp",          "**.444.777/0001-**", TransactionCategory.ServicosUtilidades),
            (18, 119.90m,                                       "VIVO FIBRA INTERNET",          "Vivo Fibra",      "**.222.111/0001-**", TransactionCategory.ServicosUtilidades),
            (22, 89.90m,                                        "TIM CELULAR CONTROLE",          "TIM Celular",     "**.333.444/0001-**", TransactionCategory.ServicosUtilidades),
        };
    }

    private static (int Day, decimal Amount, string Merchant)[] StreamingSubs(Random rng, int lifeMul)
    {
        var list = new List<(int, decimal, string)>
        {
            (3, 55.90m, "NETFLIX BR"),
            (8, 21.90m, "SPOTIFY PREMIUM"),
            (14, 19.90m, "AMAZON PRIME VIDEO"),
        };
        if (lifeMul >= 2) list.Add((20, 34.90m, "DISNEY PLUS"));
        if (lifeMul >= 3) list.Add((25, 29.90m, "HBO MAX"));
        return list.ToArray();
    }

    private static readonly string[] DeliveryMerchants =
    {
        "IFD*IFOOD", "IFD*IFOOD", "IFD*IFOOD",   // peso maior, é o mais comum
        "RPP*RAPPI", "UBEREATS",
        "MCDONALDS", "BURGER KING", "SUBWAY", "HABIB'S", "OUTBACK STEAKHOUSE",
        "MADERO STEAKHOUSE", "STARBUCKS COFFEE", "COCO BAMBU", "PIZZA HUT", "DOMINOS PIZZA"
    };

    private static readonly string[] Markets =
    {
        "CARREFOUR HIPER", "CARREFOUR EXPRESS", "PAO DE ACUCAR", "ATACADAO",
        "ASSAI ATACADISTA", "EXTRA HIPER", "SAMS CLUB", "HORTIFRUTI", "ST MARCHE"
    };

    private static readonly string[] RideShareMerchants =
    {
        "UBER DO BRASIL", "UBER DO BRASIL", "UBER DO BRASIL",  // peso maior
        "99 TECNOLOGIA", "99 TECNOLOGIA",
        "CABIFY BRASIL"
    };

    private static readonly string[] GasStations =
    {
        "POSTO SHELL", "POSTO IPIRANGA", "POSTO PETROBRAS", "POSTO BR MANIA", "POSTO ALE"
    };

    private static readonly string[] OnlineShops =
    {
        "AMAZON.COM.BR", "MERCADO LIVRE", "MAGAZINE LUIZA", "MAGALU",
        "AMERICANAS", "SUBMARINO", "SHOPEE", "ALIEXPRESS",
        "RENNER", "RIACHUELO", "C&A", "ZARA", "ADIDAS", "NIKE BR"
    };

    private static readonly string[] BigShops =
    {
        "MAGAZINE LUIZA", "MAGALU", "AMAZON.COM.BR", "CASAS BAHIA",
        "AMERICANAS", "FAST SHOP", "PONTO FRIO"
    };

    private static readonly string[] Pharmacies =
    {
        "DROGASIL", "DROGARIA SAO PAULO", "DROGA RAIA", "PAGUE MENOS", "ONOFRE"
    };
}
