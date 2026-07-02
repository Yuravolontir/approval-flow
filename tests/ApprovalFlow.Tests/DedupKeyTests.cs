using ApprovalFlow.Shared.Utils;
using Xunit;

namespace ApprovalFlow.Tests;

public class DedupKeyTests
{
    [Fact]
    public void Compute_SameInputs_ReturnsSameKey()
    {
        var key1 = DedupKey.Compute("Noodle Works", "NW-INV-7781", 42.50m);
        var key2 = DedupKey.Compute("Noodle Works", "NW-INV-7781", 42.50m);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void Compute_Returns16UppercaseHexChars()
    {
        var key = DedupKey.Compute("Noodle Works", "NW-INV-7781", 42.50m);

        Assert.Equal(16, key.Length);
        Assert.Matches("^[0-9A-F]{16}$", key);
    }

    [Theory]
    [InlineData("ACME Corp", "acme corp")]
    [InlineData("Noodle Works", "NOODLE WORKS")]
    public void Compute_VendorCaseInsensitive(string vendor1, string vendor2)
    {
        var key1 = DedupKey.Compute(vendor1, "INV-001", 100m);
        var key2 = DedupKey.Compute(vendor2, "INV-001", 100m);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void Compute_InvoiceNumberCaseInsensitive()
    {
        var key1 = DedupKey.Compute("Acme", "inv-001", 100m);
        var key2 = DedupKey.Compute("Acme", "INV-001", 100m);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void Compute_AmountNormalizedToTwoDecimals()
    {
        var key1 = DedupKey.Compute("Acme", "INV-001", 42.5m);
        var key2 = DedupKey.Compute("Acme", "INV-001", 42.50m);
        var key3 = DedupKey.Compute("Acme", "INV-001", 42.500m);

        Assert.Equal(key1, key2);
        Assert.Equal(key1, key3);
    }

    [Fact]
    public void Compute_DifferentAmounts_ReturnDifferentKeys()
    {
        var key1 = DedupKey.Compute("Acme", "INV-001", 42.50m);
        var key2 = DedupKey.Compute("Acme", "INV-001", 42.51m);

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void Compute_DifferentVendors_ReturnDifferentKeys()
    {
        var key1 = DedupKey.Compute("Acme", "INV-001", 100m);
        var key2 = DedupKey.Compute("Globex", "INV-001", 100m);

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void Compute_DifferentInvoiceNumbers_ReturnDifferentKeys()
    {
        var key1 = DedupKey.Compute("Acme", "INV-001", 100m);
        var key2 = DedupKey.Compute("Acme", "INV-002", 100m);

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void Compute_PipeInFields_CollidesAcrossBoundary()
    {
        // Documents current (known) behavior: "a|b" + "c" and "a" + "b|c"
        // concatenate to the same "a|b|c" input and therefore collide.
        // Acceptable for MVP: real vendor names / invoice numbers don't contain '|'.
        var key1 = DedupKey.Compute("a|b", "c", 100m);
        var key2 = DedupKey.Compute("a", "b|c", 100m);

        Assert.Equal(key1, key2);
    }
}
