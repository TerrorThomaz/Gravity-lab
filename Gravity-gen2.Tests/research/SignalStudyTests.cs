using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// The signal study cannot be run in CI (it needs exchange data), so these tests are what stand
// between it and a wrong conclusion. Three properties, in the order the null-control doctrine asks:
//   1. CAUSAL   — no feature at bar t changes when bars after t are removed; labels start at t+1.
//   2. NULL     — on a driftless random walk nothing is significant after FDR, no model forecasts
//                 out of sample, and no cost-aware P&L is significantly profitable.
//   3. POWER    — a planted mean-reversion is found, with the right sign, by the IC table, the
//                 variance ratio and the walk-forward model. A null that cannot fail is not a test.
public class SignalStudyTests
{
    private static readonly DateTime T0 = new(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Proper OHLC: open = previous close. kappa > 0 plants reversion of log price toward its own
    // EMA(50) — the "distance from EMA predicts the next move" structure the fade/grid strategies
    // are paid by. kappa = 0 is a driftless random walk.
    private static Candle[] Series(int n, int seed, double kappa = 0.0, double sigma = 0.01)
    {
        var rng = new Random(seed);
        var a = new Candle[n];
        double logP = Math.Log(100), ema = logP, alpha = 2.0 / 51.0, prevClose = 100;
        for (int i = 0; i < n; i++)
        {
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            logP += -kappa * (logP - ema) + sigma * z - 0.5 * sigma * sigma;
            ema = alpha * logP + (1 - alpha) * ema;
            double close = Math.Exp(logP), open = prevClose;
            double wick = close * sigma * (0.2 + 0.6 * rng.NextDouble());
            a[i] = new Candle(T0.AddHours(i), open, Math.Max(open, close) + wick, Math.Min(open, close) - wick, close, 1_000_000);
            prevClose = close;
        }
        return a;
    }

    private static SignalPanel Panel(int coins, int bars, double kappa, int stride, int seed0 = 500)
    {
        var btc = new SignalFeatures.BtcContext(Series(bars, seed0 - 1, kappa));
        var p = new SignalPanel();
        for (int c = 0; c < coins; c++) p.AddCoin($"C{c}", Series(bars, seed0 + c, kappa), null, btc, stride);
        return p;
    }

    // ── 1. causality ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Features_AtBarT_DoNotDependOnLaterBars()
    {
        var full = Series(2000, 11, kappa: 0.03);
        var btc  = new SignalFeatures.BtcContext(Series(2000, 12));
        var F = SignalFeatures.Compute(full, null, btc);
        foreach (int cut in new[] { 400, 1100, 1700 })
        {
            var P = SignalFeatures.Compute(full[..cut], null, btc);
            for (int k = 0; k < SignalFeatures.Count; k++)
                for (int t = SignalFeatures.Warmup; t < cut; t++)
                    Assert.True(F[k][t] == P[k][t], $"{SignalFeatures.Names[k]} at bar {t} changed when truncated at {cut}");
        }
    }

    [Fact]
    public void Labels_StartAtTheNextBarsOpen()
    {
        var s = Series(600, 3);
        var atr = Volatility.Atr(CandleExt.Highs(s), CandleExt.Lows(s), CandleExt.Closes(s), 14);
        int t = 300;
        Assert.Equal((s[t + 4].Close - s[t + 1].Open) / atr[t], SignalFeatures.ForwardReturn(s, atr, t, 4), 12);

        // Moving bar t's own high/low anywhere cannot change the barrier label at t: it scans t+1...
        var mod = (Candle[])s.Clone();
        mod[t] = mod[t] with { High = mod[t].High * 1.5, Low = mod[t].Low * 0.5 };
        Assert.Equal(SignalFeatures.TripleBarrier(s, atr, t), SignalFeatures.TripleBarrier(mod, atr, t));
    }

    [Fact]
    public void TripleBarrier_BothTouchedInOneBar_IsATieNeverAWin()
    {
        var s = Series(400, 5);
        var atr = Volatility.Atr(CandleExt.Highs(s), CandleExt.Lows(s), CandleExt.Closes(s), 14);
        int t = 300;
        s[t + 1] = s[t + 1] with { High = s[t + 1].Open + 10 * atr[t], Low = s[t + 1].Open - 10 * atr[t] };
        Assert.Equal((sbyte)2, SignalFeatures.TripleBarrier(s, atr, t).Label);
    }

    [Fact]
    public void Attribution_UsesTheLastClosedBar()
    {
        var s = Series(600, 9);
        var F = SignalFeatures.Compute(s);
        var atr = Volatility.Atr(CandleExt.Highs(s), CandleExt.Lows(s), CandleExt.Closes(s), 14);
        // Entry at the open of bar 400 → bar 399 closed exactly then; bar 400 has not.
        var a = SignalStudyCommand.Attribute("x", s, F, atr, s[400].Time, 1.0, +1)!;
        Assert.Equal(F[0][399], a.X[0]);
        // Entry 59 minutes into bar 400 → still bar 399.
        var b = SignalStudyCommand.Attribute("x", s, F, atr, s[400].Time.AddMinutes(59), 1.0, +1)!;
        Assert.Equal(F[0][399], b.X[0]);
    }

    // ── 2. null ──────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void OnARandomWalk_NothingIsSignificant_AndNoModelProfits()
    {
        const int stride = 2;
        var panel = Panel(coins: 24, bars: 6000, kappa: 0.0, stride: stride);
        var r = SignalStudy.Analyze(panel, stride);

        int sig = r.Ic.Count(x => x.Q < 0.05);
        Assert.True(sig <= 2, $"{sig} of {r.Ic.Count} IC tests significant at q<.05 on a random walk: " +
            string.Join("; ", r.Ic.Where(x => x.Q < 0.05).Select(x => $"{x.Feature} h={x.Horizon} {x.Regime} t={x.Ic.T:F1}")));

        foreach (var m in r.Ridge)
        {
            Assert.True(Math.Abs(m.OosIcT) < 3.0, $"h={m.Horizon}: walk-forward OOS IC t={m.OosIcT:F2} on a random walk");
            // t, not the sign of the mean: a few hundred ±2-ATR trades put the mean ±0.1 ATR from
            // its expectation by chance (the first draft asserted mean < 0 and failed on exactly
            // that — t=0.86). "Not significantly profitable" is the property; same rule as the
            // simulator gate in RandomWalkNullTests.
            Assert.True(m.Pnl.Trades == 0 || !(m.Pnl.T > 2.0),
                $"h={m.Horizon}: ridge P&L {m.Pnl.MeanAtr:F3} ATR/trade (t={m.Pnl.T:F2}) over {m.Pnl.Trades} on a random walk");
        }
        Assert.NotNull(r.Logit);
        Assert.InRange(r.Logit!.Auc, 0.47, 0.53);
        Assert.True(r.Logit.Pnl.Trades == 0 || !(r.Logit.Pnl.T > 2.0),
            $"barrier P&L {r.Logit.Pnl.MeanAtr:F3} ATR/trade (t={r.Logit.Pnl.T:F2}) over {r.Logit.Pnl.Trades} on a random walk");

        foreach (var v in r.VarianceRatios.Where(v => v.Regime == VarianceRatioAccumulator.AllRegimes))
            Assert.InRange(v.PooledVr, 0.9, 1.1);
    }

    // ── 3. power ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void APlantedMeanReversion_IsFound_WithTheRightSign()
    {
        const int stride = 2;
        var panel = Panel(coins: 24, bars: 6000, kappa: 0.03, stride: stride);
        var r = SignalStudy.Analyze(panel, stride);

        // Above the EMA → the next move is DOWN: negative IC, significant after FDR.
        var ic = r.Ic.First(x => x.Feature == "dist_ema50" && x.Horizon == 24 && x.Regime == "All");
        Assert.True(ic.Ic.Ic < 0 && ic.Q < 0.01, $"dist_ema50 IC {ic.Ic.Ic:F4} q={ic.Q:G3}");

        // Reversion compresses multi-bar variance.
        var vr = r.VarianceRatios.First(v => v.Regime == VarianceRatioAccumulator.AllRegimes && v.Q == 24);
        Assert.True(vr.PooledVr < 0.9, $"VR(24)={vr.PooledVr:F3} under planted reversion");

        // The single fixed model finds it out of sample.
        var m = r.Ridge.First(x => x.Horizon == 24);
        Assert.True(m.OosIc > 0 && m.OosIcT > 3, $"OOS IC {m.OosIc:F4} t={m.OosIcT:F2}");
        Assert.True(r.Logit!.Auc > 0.55, $"barrier AUC {r.Logit.Auc:F3}");
    }

    // The placebo must (a) rank a planted effect above EVERY shifted-target replay, and (b) on a
    // random walk, put the actual statistic inside its own distribution rather than in the tail.
    [Fact]
    public void Placebo_RanksAPlantedSignalFirst_AndARandomWalkInTheBulk()
    {
        const int stride = 2, N = 9;   // minimum attainable p = 1/(N+1) = 0.1
        var planted = SignalStudy.Analyze(Panel(16, 6000, 0.03, stride, seed0: 900), stride, placebos: N);
        var m = planted.Ridge.First(x => x.Horizon == 24);
        Assert.Equal(1.0 / (N + 1), m.CorrPlacebo.P, 9);
        Assert.Equal(1.0 / (N + 1), planted.Logit!.AucPlacebo.P, 9);
        Assert.True(m.OosIc > m.CorrPlacebo.Mean + 5 * m.CorrPlacebo.Sd,
            $"planted OOS corr {m.OosIc:F4} vs placebo {m.CorrPlacebo.Mean:F4}±{m.CorrPlacebo.Sd:F4}");

        var nul = SignalStudy.Analyze(Panel(16, 6000, 0.0, stride, seed0: 900), stride, placebos: N);
        foreach (var x in nul.Ridge)
        {
            double z = (x.OosIc - x.CorrPlacebo.Mean) / x.CorrPlacebo.Sd;
            Assert.True(Math.Abs(z) < 3, $"h={x.Horizon}: random-walk OOS corr is {z:F1} placebo-sd from the placebo mean");
        }
        double za = (nul.Logit!.Auc - nul.Logit.AucPlacebo.Mean) / nul.Logit.AucPlacebo.Sd;
        Assert.True(Math.Abs(za) < 3, $"random-walk AUC is {za:F1} placebo-sd from the placebo mean");
    }

    // ── statistics units ─────────────────────────────────────────────────────────────────────
    [Fact]
    public void Ranks_AverageTies() =>
        Assert.Equal(new[] { 1.0, 2.5, 2.5, 4.0 }, SignalStats.Ranks(new[] { 1.0, 5.0, 5.0, 9.0 }));

    [Fact]
    public void BenjaminiHochberg_MatchesHandComputation()
    {
        // p sorted: .01 .02 .03 .5 ; m=4 → .04 .04 .04 .5
        var q = SignalStats.BenjaminiHochberg(new[] { 0.03, 0.01, 0.5, 0.02 });
        Assert.Equal(new[] { 0.04, 0.04, 0.5, 0.04 }, q.Select(v => Math.Round(v, 10)).ToArray());
    }

    [Fact]
    public void NeweyWest_PositiveAutocorrelation_WidensTheInterval()
    {
        var rng = new Random(1); var s = new List<double>(); double e = 0;
        for (int i = 0; i < 2000; i++) { e = 0.8 * e + rng.NextDouble() - 0.5; s.Add(e + 0.05); }
        Assert.True(Math.Abs(SignalStats.NeweyWestT(s, 10).T) < Math.Abs(SignalStats.NeweyWestT(s, 0).T));
    }

    [Fact]
    public void Ridge_RecoversAPlantedLinearModel()
    {
        var rng = new Random(2); var g = new SignalStats.Agg(3);
        for (int i = 0; i < 20000; i++)
        {
            var x = new[] { rng.NextDouble(), rng.NextDouble() * 4, rng.NextDouble() };
            g.Add(x, 2.0 * x[0] - 0.5 * x[1] + 0.1 * (rng.NextDouble() - 0.5));
        }
        var m = SignalStats.FitRidge(g, lambda: 1e-6)!;
        double[] v1 = { 0.3, 1.0, 0.7 }, v2 = { 0.8, 3.0, 0.1 };
        // Differences cancel the intercept: Δpred = 2·Δx0 − 0.5·Δx1, and x2 carries nothing.
        Assert.Equal(2.0 * (0.3 - 0.8) - 0.5 * (1.0 - 3.0), m.Predict(k => v1[k]) - m.Predict(k => v2[k]), 2);
    }

    [Fact]
    public void Logit_FindsTheSignOfAPlantedEffect_AndAucIsOneWhenPerfect()
    {
        var rng = new Random(3); var X = new List<double[]>(); var y = new List<int>();
        for (int i = 0; i < 5000; i++) { double a = rng.NextDouble() - 0.5, b = rng.NextDouble() - 0.5; X.Add(new[] { a, b }); y.Add(rng.NextDouble() < 1 / (1 + Math.Exp(-6 * a)) ? 1 : 0); }
        var m = SignalStats.FitLogit(X, y)!;
        Assert.True(m.B[0] > 0.5 && Math.Abs(m.B[1]) < 0.2, $"b={m.B[0]:F3},{m.B[1]:F3}");
        Assert.Equal(1.0, SignalStats.Auc(new[] { 0.1, 0.2, 0.8, 0.9 }, new[] { 0, 0, 1, 1 }));
    }

    [Fact]
    public void WalkForward_TrainsOnlyOnThePast_WithAPurgeGap()
    {
        var days = Enumerable.Range(100, 600).ToArray();
        foreach (var (tr, te) in SignalStats.WalkForwardFolds(days, 6, purgeDays: 5))
            Assert.True(tr.Max() + 5 < te.Min(), $"train ends {tr.Max()}, test starts {te.Min()}");
    }

    [Theory]
    [InlineData(4, 1)] [InlineData(24, 3)] [InlineData(72, 2)] [InlineData(24, 5)]
    public void NonOverlapping_SpacesRowsAtLeastOneHorizonApart(int h, int stride)
    {
        var kept = Enumerable.Range(0, 2000).Select(k => SignalFeatures.Warmup + k * stride)
                             .Where(b => SignalStats.NonOverlapping(b, h, stride)).ToList();
        Assert.True(kept.Count > 5);
        Assert.All(kept.Zip(kept.Skip(1)), z => Assert.True(z.Second - z.First >= h));
    }
}
