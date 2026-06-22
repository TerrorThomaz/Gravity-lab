// Gravity-gen2.Tests/core/VariantRouterTests.cs
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class VariantRouterTests
{
    // Flat ATR array: all ratios = 1.0, so [0, 9999] covers 100% of bars
    private static double[] FlatAtr(int n, double val = 1.0)
        => Enumerable.Repeat(val, n).ToArray();

    [Fact]
    public void VolCoverage_DefaultRange_ReturnsOne()
    {
        var atr = FlatAtr(200);
        double cov = VariantRouter.VolCoverage(atr, 100, 200, 0.0, 9999.0);
        Assert.Equal(1.0, cov, precision: 6);
    }

    [Fact]
    public void VolCoverage_NoBarInRange_ReturnsZero()
    {
        // All ATR values equal baseline → ratio = 1.0; range is [2.0, 9999]
        var atr = FlatAtr(200);
        double cov = VariantRouter.VolCoverage(atr, 100, 200, 2.0, 9999.0);
        Assert.Equal(0.0, cov, precision: 6);
    }

    [Fact]
    public void Select_NoVariantsWithGenotype_ReturnsNull()
    {
        var variants = new[]
        {
            new VariantSpec<string>("hival", 1.5, 9999.0, null)
        };
        // flat ATR → ratio 1.0 → below 1.5 → no match
        var atr = FlatAtr(200);
        string? result = VariantRouter.Select(atr, 150, variants);
        Assert.Null(result);
    }

    [Fact]
    public void Select_DefaultVariant_MatchesWhenNothingElseDoes()
    {
        var variants = new[]
        {
            new VariantSpec<string>("hival",   1.5, 9999.0, null),
            new VariantSpec<string>("default", 0.0, 9999.0, "myGenotype"),
        };
        var atr = FlatAtr(200);
        string? result = VariantRouter.Select(atr, 150, variants);
        Assert.Equal("myGenotype", result);
    }

    [Fact]
    public void Select_TightestRangeWins()
    {
        var variants = new[]
        {
            new VariantSpec<string>("default", 0.0, 9999.0, "broad"),
            new VariantSpec<string>("hival",   0.9, 1.1,    "tight"),  // ratio=1.0 is inside
        };
        var atr = FlatAtr(200);
        string? result = VariantRouter.Select(atr, 150, variants);
        Assert.Equal("tight", result);
    }
}
