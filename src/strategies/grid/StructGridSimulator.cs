namespace TradingGA;

// STRUCTURAL-DEFAULT GRID — the always-on rung rule found by research/rulepath.py and FROZEN
// 2026-10-04 from discovery data only (2020-2023, BacktestCoins). See
// docs/EDGE_DISCOVERY_SCOPE_2026-10.md, "STRUCT" and "Freeze".
//
// No setup gate: at every 1h close with no open position, ONE limit order rests for the next bar
// only, at close ∓ K·ATR14 (K = 3). It fills if that bar trades 5bp THROUGH the level — never on the
// arming bar itself (the same-bar fill defect). Fill price = the level, or the open if the bar gapped
// through it.
//   LONG  exit: the close of the bar `LongHold` (24) bars after the fill bar. Taker.
//   SHORT exit: trailing stop TrailAtr (3) × arming ATR from the extreme CLOSE since the fill bar's
//               close. Triggered by a close beyond the stop; exits at the next open (taker, stop gap
//               charged). Otherwise the close ShortMaxHold (168) bars after the fill.
// Exit monitoring starts the bar after the fill; a new order is armed only once the position is
// closed. Maker entry, taker exit, via TradeCosts. Funding via FundingRateSession.PnlPct (null = floor).
//
// Discovery evidence: long +0.28%/trade, short +0.10%/trade. Validation 2024-25H1 (rulepath book):
// Sharpe +0.50 BT / +0.72 OOS, vs −1.51 / −2.83 for a static 1-ATR TP/SL grid. Validation has been
// viewed repeatedly, so only forward data is clean evidence.
//
// The shorter-exit idea (from the continuation-model study) is NOT the default: on discovery data
// every short time exit loses, and longs are best at 24h. `longHold` / `shortMaxHold` are exposed so
// it can be measured, but changing them is a new trial.
public static class StructGridSimulator
{
    public const double K = 3.0, Through = 0.0005, TrailAtr = 3.0;
    public const int AtrPeriod = 14, LongHold = 24, ShortMaxHold = 168;
    private const double StopGapAtrK = 0.18;                    // same gap premium as GridSimulator

    public static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)>
        GetReturns(ReadOnlySpan<Candle> h1, bool isLong, FundingRateSession? funding = null,
                   int longHold = LongHold, int shortMaxHold = ShortMaxHold)
    {
        var res = new List<(DateTime, double, string, DateTime, double)>();
        int n = h1.Length;
        if (n < AtrPeriod + 10) return res;
        var o = CandleExt.Opens(h1); var hi = CandleExt.Highs(h1); var lo = CandleExt.Lows(h1);
        var c = CandleExt.Closes(h1); var tm = CandleExt.Times(h1);
        var atr = Volatility.Atr(hi, lo, c, AtrPeriod);
        double s = isLong ? 1.0 : -1.0;

        int i = AtrPeriod + 1;                                   // arming bar (decision at its close)
        while (i < n - 1)
        {
            double a = atr[i];
            if (!(a > 0) || !(c[i] > 0)) { i++; continue; }
            double lvl = c[i] - s * K * a;
            int f = i + 1;                                       // the only bar the order lives in
            bool filled = isLong ? lvl > 0 && lo[f] <= lvl * (1 - Through) : hi[f] >= lvl * (1 + Through);
            if (!filled) { i++; continue; }
            double px = isLong ? Math.Min(o[f], lvl) : Math.Max(o[f], lvl);

            int xb = -1; double xpx = double.NaN; bool isStop = false;
            if (isLong)
            {
                if (f + longHold < n) { xb = f + longHold; xpx = c[xb]; }
            }
            else
            {
                double ext = c[f], stop = ext + TrailAtr * a;
                for (int h = 1; h <= shortMaxHold && f + h < n; h++)
                {
                    int b = f + h;
                    if (c[b] >= stop)
                    {
                        if (b + 1 < n) { xb = b + 1; xpx = o[b + 1]; isStop = true; }
                        break;
                    }
                    ext = Math.Min(ext, c[b]);
                    stop = Math.Min(stop, ext + TrailAtr * a);
                    if (h == shortMaxHold) { xb = b; xpx = c[b]; }
                }
            }
            if (xb < 0) break;                                   // exit falls past the data: unlabelled, stop

            double gross = s * (xpx / px - 1.0) * 100.0;
            double cost = TradeCosts.RoundTripPct(TradeCosts.AtrPct(a, px), isStop, StopGapAtrK,
                                                  entryMaker: true, exitMaker: false);
            double fund = FundingRateSession.PnlPct(tm[f], tm[xb], funding, isLong);
            res.Add((tm[xb], gross - cost + fund, isLong ? "struct_long" : "struct_short", tm[f], px));
            i = xb;                                              // next order armed at the exit bar's close
        }
        return res;
    }
}
