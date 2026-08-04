using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class FitnessConfigTests
{
    [Fact]
    public void Defaults_AllWeightsCorrect()
    {
        var cfg = new FitnessConfig();
        Assert.Equal("default", cfg.VariantId);
        Assert.Equal(1.0,   cfg.GainW);
        Assert.Equal(0.9,   cfg.WrW);
        Assert.Equal(0.8,   cfg.QualityW);
        Assert.Equal(0.6,   cfg.FreqW);
        Assert.Equal(1.2,   cfg.DdPenalty);
        Assert.Equal(1.0,   cfg.RetentionW);
        Assert.Equal(0.5,   cfg.SharpeW);
        Assert.Equal(0.0,   cfg.CalmarW);
        Assert.Equal(0.0,   cfg.PfW);
        Assert.Equal(0.3,   cfg.SortinoW);
        Assert.Equal(0.0,   cfg.AtrLow);
        Assert.Equal(9999.0,cfg.AtrHigh);
        Assert.Equal(0.3,   cfg.CVaRW);
        Assert.Equal(0.2,   cfg.TailRatioW);
        Assert.Equal(0.2,   cfg.RegimeDiversityW);
        Assert.Equal(0.05,  cfg.EmbargoPct);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var cfg = FitnessConfig.Load("nonexistent_file_xyz.json");
        Assert.Equal(new FitnessConfig(), cfg);
    }

    [Fact]
    public void Load_ValidJson_OverridesFields()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """{"SharpeW":0.7,"CalmarW":0.9,"AtrLow":1.5,"AtrHigh":9999}""");
        var cfg = FitnessConfig.Load(path);
        File.Delete(path);
        Assert.Equal(0.7,  cfg.SharpeW);
        Assert.Equal(0.9,  cfg.CalmarW);
        Assert.Equal(1.5,  cfg.AtrLow);
        Assert.Equal(1.0,  cfg.GainW);   // unchanged default
    }
}
