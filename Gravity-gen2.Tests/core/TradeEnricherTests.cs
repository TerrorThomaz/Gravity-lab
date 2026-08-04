using System.Reflection;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class TradeEnricherTests
{
    [Fact]
    public void ShortStrategies_ContainsAllShortStrategies()
    {
        var field = typeof(TradeEnricher).GetField("ShortStrategies",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var set = (HashSet<string>)field!.GetValue(null)!;
        Assert.Contains("swing", set);
        Assert.Contains("fade_short", set);
        Assert.Contains("rip_short", set);
        Assert.Contains("gridshort", set);
    }

    [Fact]
    public void ShortStrategies_DoesNotContainLongStrategies()
    {
        var field = typeof(TradeEnricher).GetField("ShortStrategies",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var set = (HashSet<string>)field!.GetValue(null)!;
        Assert.DoesNotContain("diplong", set);
        Assert.DoesNotContain("fade_long", set);
        Assert.DoesNotContain("grid", set);
    }
}
