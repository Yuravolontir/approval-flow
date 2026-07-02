using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ApprovalFlow.Shared.Utils;

/// <summary>
/// Computes a deterministic deduplication key for an invoice:
/// SHA256 over "vendor|invoiceNumber|total" (lowercased, invariant-culture F2), first 16 hex chars.
/// </summary>
public static class DedupKey
{
    public static string Compute(string vendor, string invoiceNumber, decimal total)
    {
        var input = $"{vendor.ToLowerInvariant()}|{invoiceNumber.ToLowerInvariant()}|{total.ToString("F2", CultureInfo.InvariantCulture)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16];
    }
}
