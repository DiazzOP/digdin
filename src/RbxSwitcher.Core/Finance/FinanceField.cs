namespace RbxSwitcher.Core.Finance;

/// <summary>Keys of the individual reads, used in <see cref="AccountFinance.FailedReads"/>.</summary>
public static class FinanceField
{
    public const string Robux = "saldo";
    public const string Pending = "pendentes";
    public const string Plus = "plus";
    public const string Caps = "tetos";
    public const string Restriction = "restricoes";
    public const string History = "historico";

    public static readonly IReadOnlyList<string> All = [Robux, Pending, Plus, Caps, Restriction, History];

    public static string Describe(string key) => key switch
    {
        Robux => "saldo",
        Pending => "Robux pendentes",
        Plus => "assinatura Plus",
        Caps => "tetos de transferência",
        Restriction => "restrições configuradas",
        History => "histórico de envios",
        _ => key,
    };
}
