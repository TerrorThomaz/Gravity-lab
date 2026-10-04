namespace TradingGA;

// STRUCTURAL-DEFAULT RUNG ENGINE — the deep (k=3) one-bar rung found by research/rulepath.py. It has
// two uses:
//   GetReturns         the frozen standalone default (2026-10-04, discovery data only): always on,
//                      long → TIME24, short → TRAIL30, no hard stop. NOT a replacement for the GA
//                      grid: in edgetest it matched its CAGR at maxDD 40% vs 3.3%.
//   GetOverlayReturns  the DYNAMIC-GRID OVERLAY (option 1): the GA grid stays as is, and a classifier
//                      (decisions exported by research/overlay_decisions.py) decides per arming bar
//                      whether to add a deep rung, and with which exit. A hard stop from the GA
//                      genotype caps the tails.
//
// Shared mechanics: at the close of arming bar i, ONE limit rests for bar i+1 only, at
// close ∓ K·ATR14 (K = 3). It fills if that bar trades 5bp THROUGH the level, never on the arming bar
// itself (the same-bar fill defect). Fill price = the level, or the open if the bar gapped through
// it. Exit monitoring starts the bar after the fill. A new order is armed only once the position is
// closed. Maker entry; taker exits except a take-profit (maker). Costs via TradeCosts; funding via
// FundingRateSession.PnlPct (null = floor).
//
// Exits (a = the arming bar's ATR):
//   TIME24   the close 24 bars after the fill bar
//   TPSL1    TP +1a (maker, 5bp through), SL −1a on a CLOSE → next open, max 24
//   TPSL2    TP +2a, SL −1a, max 72
//   TRAIL15  trailing 1.5a from the extreme close since the fill bar's close, on a close → next open, max 168
//   TRAIL30  the same at 3.0a
// A close-triggered exit fills at the next bar's actual open, so no stop-gap premium is charged: the
// open already holds the gap. The HARD STOP (overlay only) triggers INTRABAR at fill ∓ hardStopAtr·a.
// It exits at that level (or the open if gapped) and is taker WITH the stop-gap premium, checked
// first each bar (pessimistic). If a bar both closes beyond the SL and touches the TP, the SL is
// assumed.
public static class StructGridSimulator
{
    public const double K = 3.0, Through = 0.0005;
    public const int AtrPeriod = 14;
    private const double StopGapAtrK = 0.18;                    // GridSimulator's gap premium, for the intrabar hard stop

    public static readonly Dictionary<string, (string Kind, int MaxHold, double Tp, double SlOrTrail)> Exits = new()
    {
        ["TIME24"]  = ("time",  24,  0,   0),
        ["TPSL1"]   = ("tpsl",  24,  1.0, 1.0),
        ["TPSL2"]   = ("tpsl",  72,  2.0, 1.0),
        ["TRAIL15"] = ("trail", 168, 0,   1.5),
        ["TRAIL30"] = ("trail", 168, 0,   3.0),
    };

    // Frozen standalone default (unchanged behaviour): long TIME24, short TRAIL30, no hard stop.
    public static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)>
        GetReturns(ReadOnlySpan<Candle> h1, bool isLong, FundingRateSession? funding = null)
        => Run(h1, isLong, _ => isLong ? "TIME24" : "TRAIL30", hardStopAtr: double.PositiveInfinity, funding);

    // Overlay: `decide(armingBarTime)` returns the exit name to arm, or null for "no rung this bar".
    public static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)>
        GetOverlayReturns(ReadOnlySpan<Candle> h1, bool isLong, Func<DateTime, string?> decide,
                          double hardStopAtr, FundingRateSession? funding = null)
        => Run(h1, isLong, decide, hardStopAtr, funding);

    private static List<(DateTime, double, string, DateTime, double)> Run(
        ReadOnlySpan<Candle> h1, bool isLong, Func<DateTime, string?> decide, double hardStopAtr, FundingRateSession? funding)
    {
        var res = new List<(DateTime, double, string, DateTime, double)>();
        int n = h1.Length;
        if (n < AtrPeriod + 10) return res;
        var o = CandleExt.Opens(h1); var hi = CandleExt.Highs(h1); var lo = CandleExt.Lows(h1);
        var c = CandleExt.Closes(h1); var tm = CandleExt.Times(h1);
        var atr = Volatility.Atr(hi, lo, c, AtrPeriod);
        double s = isLong ? 1.0 : -1.0;

        int i = AtrPeriod + 1;
        while (i < n - 1)
        {
            double a = atr[i];
            if (!(a > 0) || !(c[i] > 0) || decide(tm[i]) is not { } exitName || !Exits.TryGetValue(exitName, out var ex))
            { i++; continue; }
            double lvl = c[i] - s * K * a;
            int f = i + 1;
            bool filled = isLong ? lvl > 0 && lo[f] <= lvl * (1 - Through) : hi[f] >= lvl * (1 + Through);
            if (!filled) { i++; continue; }
            double px = isLong ? Math.Min(o[f], lvl) : Math.Max(o[f], lvl);

            double hard = px - s * hardStopAtr * a;
            double tgt = px + s * ex.Tp * a;
            double ext = c[f];
            double stop = ex.Kind == "trail" ? ext - s * ex.SlOrTrail * a : px - s * ex.SlOrTrail * a;
            int xb = -1; double xpx = double.NaN; bool intrabarStop = false, maker = false;
            for (int h = 1; h <= ex.MaxHold && f + h < n; h++)
            {
                int b = f + h;
                if (!double.IsInfinity(hardStopAtr) && (isLong ? lo[b] <= hard : hi[b] >= hard))
                {
                    xb = b; xpx = isLong ? Math.Min(o[b], hard) : Math.Max(o[b], hard); intrabarStop = true; break;
                }
                if (ex.Kind != "time" && (isLong ? c[b] <= stop : c[b] >= stop))
                {
                    if (b + 1 < n) { xb = b + 1; xpx = o[b + 1]; }
                    break;
                }
                if (ex.Kind == "tpsl" && (isLong ? hi[b] >= tgt * (1 + Through) : lo[b] <= tgt * (1 - Through)))
                {
                    xb = b; xpx = tgt; maker = true; break;
                }
                if (ex.Kind == "trail")
                {
                    ext = isLong ? Math.Max(ext, c[b]) : Math.Min(ext, c[b]);
                    stop = isLong ? Math.Max(stop, ext - ex.SlOrTrail * a) : Math.Min(stop, ext + ex.SlOrTrail * a);
                }
                if (h == ex.MaxHold) { xb = b; xpx = c[b]; }
            }
            if (xb < 0) break;                                   // exit falls past the data: unlabelled, stop

            double gross = s * (xpx / px - 1.0) * 100.0;
            double cost = TradeCosts.RoundTripPct(TradeCosts.AtrPct(a, px), isStop: intrabarStop, StopGapAtrK,
                                                  entryMaker: true, exitMaker: maker);
            double fund = FundingRateSession.PnlPct(tm[f], tm[xb], funding, isLong);
            res.Add((tm[xb], gross - cost + fund, $"{(isLong ? "struct_long" : "struct_short")}_{exitName}", tm[f], px));
            i = xb;
        }
        return res;
    }
}
