namespace ApprovalFlow.Shared.Models;

public class FxRates
{
    public Dictionary<string, decimal> Rates { get; set; } = new()
    {
        ["EUR"] = 1.08m,
        ["GBP"] = 1.27m,
        ["USD"] = 1.00m
    };

    public decimal ConvertToUsd(decimal amount, string currency)
    {
        if (currency == "USD") return amount;
        if (Rates.TryGetValue(currency, out var rate))
            return amount * rate;
        throw new ArgumentException($"Unknown currency: {currency}");
    }
}
