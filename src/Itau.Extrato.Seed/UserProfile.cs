namespace Itau.Extrato.Seed;

/// <summary>
/// Define um cliente do banco: dados cadastrais + parâmetros de comportamento
/// (salário, aluguel, recipients comuns de Pix) que o gerador usa pra produzir
/// um histórico de extrato realista.
/// </summary>
public sealed record UserProfile(
    string UserId,
    string DisplayName,
    string Agencia,
    string Conta,
    string CpfMasked,
    decimal SalaryAmount,
    string EmployerName,
    string EmployerDocMasked,
    int SalaryDayOfMonth,
    decimal RentAmount,
    int RentDayOfMonth,
    string LandlordName,
    string LandlordDocMasked,
    decimal StartingBalance,
    (string Name, string DocMasked)[] PixRecipients,
    int LifestyleMultiplier);

public static class DemoProfiles
{
    /// <summary>Personagens do PoV. Gabriel é o "main" com 12 meses ricos.</summary>
    public static readonly UserProfile Gabriel = new(
        UserId: "gabriel_cerioni",
        DisplayName: "Gabriel Cerioni",
        Agencia: "0001",
        Conta: "12345-6",
        CpfMasked: "***.234.567-**",
        SalaryAmount: 18500m,
        EmployerName: "REDIS BRASIL LTDA",
        EmployerDocMasked: "**.345.678/0001-**",
        SalaryDayOfMonth: 5,
        RentAmount: 3200m,
        RentDayOfMonth: 10,
        LandlordName: "IMOBILIARIA LOPES",
        LandlordDocMasked: "**.111.222/0001-**",
        StartingBalance: 38000m,
        PixRecipients: new (string, string)[]
        {
            ("Dua Lipa",        "***.789.012-**"),
            ("Juliana Cerioni", "***.123.456-**"),
            ("Don Ramon",       "***.987.654-**"),
            ("Felipe Lume",     "***.555.444-**"),
            ("Miller Moreno",   "***.333.222-**"),
        },
        LifestyleMultiplier: 3);

    public static readonly UserProfile Miller = new(
        UserId: "miller_moreno",
        DisplayName: "Miller Moreno",
        Agencia: "0001",
        Conta: "23456-7",
        CpfMasked: "***.333.222-**",
        SalaryAmount: 9800m,
        EmployerName: "TECH CONSULTING BR LTDA",
        EmployerDocMasked: "**.876.543/0001-**",
        SalaryDayOfMonth: 1,
        RentAmount: 2400m,
        RentDayOfMonth: 5,
        LandlordName: "RESIDENCIAL VILA NOVA",
        LandlordDocMasked: "**.222.333/0001-**",
        StartingBalance: 14500m,
        PixRecipients: new (string, string)[]
        {
            ("Gabriel Cerioni", "***.234.567-**"),
            ("Camila Andrade",  "***.444.555-**"),
            ("Felipe Lume",     "***.555.444-**"),
        },
        LifestyleMultiplier: 2);

    public static readonly UserProfile Camila = new(
        UserId: "camila_andrade",
        DisplayName: "Camila Andrade",
        Agencia: "0042",
        Conta: "34567-8",
        CpfMasked: "***.444.555-**",
        SalaryAmount: 6500m,
        EmployerName: "AGENCIA CRIATIVA SP",
        EmployerDocMasked: "**.999.888/0001-**",
        SalaryDayOfMonth: 5,
        RentAmount: 1800m,
        RentDayOfMonth: 10,
        LandlordName: "ADM PREDIAL CENTRO",
        LandlordDocMasked: "**.333.444/0001-**",
        StartingBalance: 6200m,
        PixRecipients: new (string, string)[]
        {
            ("Miller Moreno",   "***.333.222-**"),
            ("Pedro Castro",    "***.666.777-**"),
        },
        LifestyleMultiplier: 1);

    public static readonly UserProfile Pedro = new(
        UserId: "pedro_castro",
        DisplayName: "Pedro Castro",
        Agencia: "0001",
        Conta: "45678-9",
        CpfMasked: "***.666.777-**",
        SalaryAmount: 15800m,
        EmployerName: "CONSULTORIA FINANCEIRA PARTNERS",
        EmployerDocMasked: "**.777.888/0001-**",
        SalaryDayOfMonth: 25,
        RentAmount: 4800m,
        RentDayOfMonth: 5,
        LandlordName: "JK HIGIENOPOLIS LTDA",
        LandlordDocMasked: "**.555.666/0001-**",
        StartingBalance: 62000m,
        PixRecipients: new (string, string)[]
        {
            ("Camila Andrade",  "***.444.555-**"),
            ("Gabriel Cerioni", "***.234.567-**"),
        },
        LifestyleMultiplier: 3);
}
