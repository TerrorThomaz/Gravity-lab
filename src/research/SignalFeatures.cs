namespace TradingGA;

// The signal panel's feature matrix and labels. Research code: it never feeds a strategy, a GA or a
// live path — it measures whether the signals the strategies are built from carry information.
//
// ── CAUSALITY CONTRACT (same rule as the simulators, enforced by SignalStudyTests) ──
// Feature value at bar t uses bars 0..t only, so it is known at the CLOSE of bar t.
// Every label starts at the OPEN of bar t+1 — the first price a decision taken at t can trade at.
// Truncating the series after bar t therefore cannot change any feature at or before t.
//
// Scale: price-distance features are in units of ATR(14) at t, so a coin's volatility does not
// dominate a pooled regression; forward returns are in the same units, which makes a coefficient
// read as "ATRs of forward move per ATR of signal".
public static class SignalFeatures
{
    public static readonly string[] Names =
    [
        "ret1",          // 0  1-bar return            (short-term reversal / continuation)
        "ret4",          // 1  4-bar return
        "mom24",         // 2  24-bar return
        "dist_ema20",    // 3  close − EMA20
        "dist_ema50",    // 4  close − EMA50           (HybridGrid / AccumGrid anchor)
        "dist_ema200",   // 5  close − EMA200          (RegimeClassifier EMA stack)
        "slope_ema50",   // 6  EMA50 change over 20 bars
        "rsi14",         // 7  (RSI−50)/50             (Dip/Rip RSI bands)
        "adx14",         // 8  ADX/100                 (Grid ranging screen)
        "log_bbw20",     // 9  ln Bollinger width      (Grid compression screen)
        "log_atr_ratio", // 10 ln ATR14/ATR100         (VariantRouter vol bands)
        "range_pos48",   // 11 position in 48-bar high/low range, −1..+1
        "bear_div",      // 12 RSI bearish divergence  (FadeShort)
        "bull_div",      // 13 RSI bullish divergence  (SwingLong / FadeLong)
        "bull_bos",      // 14 close > previous high   (long entry trigger)
        "bear_bos",      // 15 close < previous low    (short entry trigger)
        "funding_bps",   // 16 last settled funding rate, bps per interval (0 when unknown)
        "btc_mom24",     // 17 BTC 24-bar return in BTC ATRs (0 when unaligned)
        "btc_dist_ema50",// 18 BTC close − EMA50 in BTC ATRs
    ];
    public static int Count => Names.Length;

    public const int Warmup = 250;           // EMA200 + ATR100 settle; nothing before is emitted

    // Divergence parameters are fixed conventional values, NOT any trained genotype's — the study
    // asks whether the signal family carries information, not whether one fitted variant does.
    public const int    DivLookback   = 24;
    public const double DivOverbought = 70, DivOversold = 30, DivThreshold = 5;

    // Labels
    public static readonly int[] Horizons = [4, 24, 72];
    public const double BarrierAtr     = 2.0;   // triple barrier at ±2 ATR(t) from the t+1 open
    public const int    BarrierHorizon = 24;    // vertical barrier

    // BTC context at a timestamp: (mom24, distEma50) in BTC ATRs, and BTC's regime.
    public sealed class BtcContext
    {
        private readonly Dictionary<long, (float Mom, float Dist, MarketRegime Regime)> _byHour = new();

        public BtcContext(Candle[] btcH1)
        {
            if (btcH1.Length <= Warmup) return;
            var c   = CandleExt.Closes(btcH1);
            var atr = Volatility.Atr(CandleExt.Highs(btcH1), CandleExt.Lows(btcH1), c, 14);
            var e50 = Trend.Ema(c, 50);
            var reg = RegimeClassifier.ClassifySeriesWithDuration(btcH1);
            for (int t = Warmup; t < c.Length; t++)
            {
                double a = atr[t] > 1e-12 ? atr[t] : c[t] * 0.01;
                _byHour[HourKey(btcH1[t].Time)] = ((float)((c[t] - c[t - 24]) / a),
                                                   (float)((c[t] - e50[t]) / a), reg[t].Regime);
            }
        }

        public bool TryGet(DateTime time, out (float Mom, float Dist, MarketRegime Regime) v)
            => _byHour.TryGetValue(HourKey(time), out v);
    }

    public static long HourKey(DateTime t) => t.Ticks / TimeSpan.TicksPerHour;

    // Feature matrix F[feature][bar] for every bar (values before Warmup are 0 and must not be used).
    public static float[][] Compute(Candle[] h1, FundingRateSession? funding = null, BtcContext? btc = null)
    {
        int n = h1.Length, p = Count;
        var F = new float[p][];
        for (int k = 0; k < p; k++) F[k] = new float[n];
        if (n <= Warmup) return F;

        var c = CandleExt.Closes(h1); var hi = CandleExt.Highs(h1); var lo = CandleExt.Lows(h1);
        var atr    = Volatility.Atr(hi, lo, c, 14);
        var atr100 = Volatility.Atr(hi, lo, c, 100);
        var e20 = Trend.Ema(c, 20); var e50 = Trend.Ema(c, 50); var e200 = Trend.Ema(c, 200);
        var rsi = Momentum.Rsi(c, 14);
        var adx = Trend.Adx(hi, lo, c, 14);
        var bbw = Volatility.BbWidth(c, 20);
        var bearDiv = Signals.BearishDivergence(rsi, c, DivLookback, DivOverbought, DivThreshold);
        var bullDiv = Signals.BullishDivergence(rsi, c, DivLookback, DivOversold, DivThreshold);
        var bullBos = Signals.BullishBoS(c, hi);
        var bearBos = Signals.BearishBoS(c, lo);

        for (int t = Warmup; t < n; t++)
        {
            double a = atr[t] > 1e-12 ? atr[t] : c[t] * 0.01;
            double hh = double.MinValue, ll = double.MaxValue;
            for (int j = t - 47; j <= t; j++) { if (hi[j] > hh) hh = hi[j]; if (lo[j] < ll) ll = lo[j]; }

            F[0][t]  = (float)((c[t] - c[t - 1])  / a);
            F[1][t]  = (float)((c[t] - c[t - 4])  / a);
            F[2][t]  = (float)((c[t] - c[t - 24]) / a);
            F[3][t]  = (float)((c[t] - e20[t])  / a);
            F[4][t]  = (float)((c[t] - e50[t])  / a);
            F[5][t]  = (float)((c[t] - e200[t]) / a);
            F[6][t]  = (float)((e50[t] - e50[t - 20]) / a);
            F[7][t]  = (float)((rsi[t] - 50.0) / 50.0);
            F[8][t]  = (float)(adx[t] / 100.0);
            F[9][t]  = (float)Math.Log(Math.Max(bbw[t], 1e-6));
            F[10][t] = (float)Math.Log(atr100[t] > 1e-12 ? Math.Max(atr[t] / atr100[t], 1e-6) : 1.0);
            F[11][t] = (float)(hh - ll > 1e-12 ? 2.0 * (c[t] - ll) / (hh - ll) - 1.0 : 0.0);
            F[12][t] = bearDiv[t] ? 1f : 0f;
            F[13][t] = bullDiv[t] ? 1f : 0f;
            F[14][t] = bullBos[t] ? 1f : 0f;
            F[15][t] = bearBos[t] ? 1f : 0f;
            // Funding stamped at its settlement time; the last settlement at or before the bar's
            // OPEN is strictly in the past at the bar's close.
            F[16][t] = funding != null && funding.TryGetRate(h1[t].Time, out double fr) ? (float)(fr * 1e4) : 0f;
            if (btc != null && btc.TryGet(h1[t].Time, out var b)) { F[17][t] = b.Mom; F[18][t] = b.Dist; }
        }
        return F;
    }

    // Forward return from the t+1 OPEN to the close of t+h, in ATR(t) units. NaN when out of range.
    public static double ForwardReturn(Candle[] h1, double[] atr, int t, int h)
    {
        if (t + h >= h1.Length || t + 1 >= h1.Length) return double.NaN;
        double a = atr[t] > 1e-12 ? atr[t] : h1[t].Close * 0.01;
        return (h1[t + h].Close - h1[t + 1].Open) / a;
    }

    // Triple barrier from the t+1 open: +1 upper first, −1 lower first, 0 vertical barrier,
    // 2 BOTH touched inside one bar (order unknowable from OHLC — every consumer must treat it as
    // a loss for whichever side it trades, never as a win). TerminalRet is the ATR-unit return at
    // the vertical barrier (or ±BarrierAtr when a barrier is hit).
    public static (sbyte Label, double TerminalRet) TripleBarrier(Candle[] h1, double[] atr, int t)
    {
        if (t + BarrierHorizon >= h1.Length) return (0, double.NaN);
        double a = atr[t] > 1e-12 ? atr[t] : h1[t].Close * 0.01;
        double entry = h1[t + 1].Open, up = entry + BarrierAtr * a, dn = entry - BarrierAtr * a;
        for (int j = t + 1; j <= t + BarrierHorizon; j++)
        {
            bool u = h1[j].High >= up, d = h1[j].Low <= dn;
            if (u && d) return (2, double.NaN);
            if (u) return (1, BarrierAtr);
            if (d) return (-1, -BarrierAtr);
        }
        return (0, (h1[t + BarrierHorizon].Close - entry) / a);
    }
}
