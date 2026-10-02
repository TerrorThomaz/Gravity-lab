using TradingGA;

namespace GravityGen2.Strategies.HybridGrid;

// HybridGrid simulator: Grid + AccumulationGrid merged, LONG or SHORT by EMA side. 1h candles.
//
// From AccumulationGrid: the ladder is anchored on the LIVE EMA (it follows price), positions are
// protected by a trailing stop off the best close, and the session is trend-bound.
// From Grid: the deferred-fill resting-order engine (orders placed at a bar's close fill from the
// NEXT bar), session-level returns for fitness, per-session MAE for the path-aware drawdown term.
// New: the side. Above a rising EMA it buys pullbacks; below a falling EMA it sells rallies.
//
// ── CAUSALITY, which is the whole reason this is a new file rather than a flag on AccumulationGrid ──
// Every price level consulted DURING bar i — rung prices, the trailing stop, take-profits — is built
// only from information that existed at the close of bar i-1: ema[i-1], atr[i-1], the best close
// through i-1, and the ATR frozen when the rung filled. Bar i's own close is used only for decisions
// that execute AT that close (slope-flip exit, max-hold exit, arming orders that rest from i+1).
// AccumulationGridSimulator violates this in three places (rungs off ema[i], TP off atr[i], trailing
// stop off close[i] then tested against low[i]); RandomWalkNullTests holds this file to the rule.
//
// Intrabar ORDER is unknowable from OHLC, so every ambiguity is resolved against the strategy:
//   - a rung that fills on bar i is exposed to bar i's stop, but cannot take profit until bar i+1;
//   - if a bar touches both the stop and a take-profit, the stop wins;
//   - a rung that took profit on bar i cannot refill until bar i+1.
public static class HybridGridSimulator
{
    private const int    AtrPeriod   = 14;
    private const int    AdxPeriod   = 14;
    private const double StopGapAtrK = 0.18;  // same gap premium as the rest of the grid family

    public const string LongKind  = "hybrid_long";
    public const string ShortKind = "hybrid_short";

    internal static double TradeCost(double atrAtFill, double entryPx, bool isStop)
        => TradeCosts.RoundTripPct(TradeCosts.AtrPct(atrAtFill, entryPx), isStop, StopGapAtrK);

    // Per-rung returns, for display and portfolio replay. null funding = floor fallback (not zero).
    public static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)> GetHybridReturns(
        HybridGridGenotype g, ReadOnlySpan<Candle> h1, HybridGridSides sides = HybridGridSides.Both,
        FundingRateSession? funding = null)
        => Run(g, h1, sides, sessionLevel: false, funding, maeOut: null, leakSameBar: false);

    // Per-session returns (mean of the session's rung returns) for GA fitness.
    // maeOut: opt-in worst adverse excursion per session, index-aligned with the result.
    public static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)> GetHybridSessionReturns(
        HybridGridGenotype g, ReadOnlySpan<Candle> h1, HybridGridSides sides = HybridGridSides.Both,
        FundingRateSession? funding = null, List<double>? maeOut = null)
        => Run(g, h1, sides, sessionLevel: true, funding, maeOut, leakSameBar: false);

    // DIAGNOSTIC ONLY — reintroduces AccumulationGrid's defect (levels for bar i built from bar i's
    // own ema/atr/close) so RandomWalkNullTests can prove the gate still catches it on THIS engine.
    // Never call from a reported book.
    internal static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)> GetHybridSessionReturnsSameBarLeak(
        HybridGridGenotype g, ReadOnlySpan<Candle> h1, HybridGridSides sides = HybridGridSides.Both)
        => Run(g, h1, sides, sessionLevel: true, funding: null, maeOut: null, leakSameBar: true);

    private static List<(DateTime, double, string, DateTime, double)> Run(
        HybridGridGenotype g, ReadOnlySpan<Candle> candles, HybridGridSides sides, bool sessionLevel,
        FundingRateSession? funding, List<double>? maeOut, bool leakSameBar)
    {
        var result = new List<(DateTime, double, string, DateTime, double)>();
        int slopeLb = Math.Max(1, g.SlopeLookback);
        int warmup  = Math.Max(Math.Max(g.EmaPeriod, AtrPeriod), AdxPeriod * 2 + 1) + slopeLb + 2;
        if (candles.Length <= warmup + 5) return result;

        var opens  = CandleExt.Opens(candles);
        var closes = CandleExt.Closes(candles);
        var highs  = CandleExt.Highs(candles);
        var lows   = CandleExt.Lows(candles);
        var times  = CandleExt.Times(candles);
        var ema    = Trend.Ema(closes, g.EmaPeriod);
        var atr    = Volatility.Atr(highs, lows, closes, AtrPeriod);
        var adx    = Trend.Adx(highs, lows, closes, AdxPeriod);

        double Atr(int k) => atr[k] > 1e-10 ? atr[k] : closes[k] * 0.02;
        double Slope(int k)
        {
            int r = k - slopeLb;
            return ema[r] > 1e-10 ? (ema[k] - ema[r]) / ema[r] : 0.0;
        }

        int levels = Math.Clamp(g.GridLevels, 1, HybridGridGenotype.MaxLevels);
        var filled     = new bool[levels];
        var freshFill  = new bool[levels];   // filled on the current bar → no TP until next bar
        var entryPx    = new double[levels];
        var fillAtr    = new double[levels];
        var fillTime   = new DateTime[levels];

        bool     active = false;
        int      side = 0;            // +1 long, -1 short
        double   extreme = 0;         // best close since arming, through the previous bar
        int      holdCount = 0;
        bool     everFilled = false;
        DateTime firstFillTime = default;
        double   sessionMae = 0;
        double   entrySum = 0; int entryN = 0;
        var      sessionFills = new List<double>();

        string Kind() => side > 0 ? LongKind : ShortKind;

        void Book(int i, int n, double exitPx, bool isStop)
        {
            double ret = side * (exitPx - entryPx[n]) / entryPx[n] * 100.0
                       - TradeCost(fillAtr[n], entryPx[n], isStop)
                       + FundingRateSession.PnlPct(fillTime[n], times[i], funding, isLong: side > 0);
            if (sessionLevel) sessionFills.Add(ret);
            else              result.Add((times[i], ret, Kind(), fillTime[n], entryPx[n]));
            filled[n] = false;
        }

        void CloseAll(int i, double exitPx, bool isStop)
        {
            for (int n = 0; n < levels; n++) if (filled[n]) Book(i, n, exitPx, isStop);
        }

        void EndSession(int i)
        {
            if (sessionLevel && sessionFills.Count > 0)
            {
                result.Add((times[i], sessionFills.Average(), Kind(), firstFillTime,
                            entryN > 0 ? entrySum / entryN : 0.0));
                maeOut?.Add(sessionMae);
            }
            sessionFills.Clear();
            active = false;
        }

        for (int i = warmup; i < candles.Length; i++)
        {
            if (active)
            {
                holdCount++;

                // Everything below is fixed by the close of bar i-1 (bar i under the diagnostic leak).
                int    r       = leakSameBar ? i : i - 1;
                double atrRef  = Atr(r);
                double anchor  = ema[r];
                double best    = leakSameBar
                               ? (side > 0 ? Math.Max(extreme, closes[i]) : Math.Min(extreme, closes[i]))
                               : extreme;
                double stop    = best - side * g.TrailStopAtrMult * atrRef;

                // 1. Resting rungs fill. A rung beyond the stop is never placed — it would be a
                //    position opened only to be stopped.
                Array.Clear(freshFill);
                for (int n = 0; n < levels; n++)
                {
                    if (filled[n]) continue;
                    double lvl = anchor - side * (n + 1) * g.GridStepAtrMult * atrRef;
                    if (side * (lvl - stop) <= 0) continue;
                    bool touched = side > 0 ? lows[i] <= lvl : highs[i] >= lvl;
                    if (!touched) continue;
                    filled[n] = true; freshFill[n] = true;
                    entryPx[n] = lvl; fillAtr[n] = atrRef; fillTime[n] = times[i];
                    entrySum += lvl; entryN++;
                    if (!everFilled) { everFilled = true; firstFillTime = times[i]; }
                }

                // 2. Path: worst unrealised point of the open rungs on this bar.
                {
                    double sum = 0; int cnt = 0;
                    for (int n = 0; n < levels; n++) if (filled[n]) { sum += entryPx[n]; cnt++; }
                    if (cnt > 0)
                    {
                        double mean = sum / cnt;
                        double adverse = side > 0 ? lows[i] : highs[i];
                        double unreal = side * (adverse - mean) / mean * 100.0;
                        if (unreal < sessionMae) sessionMae = unreal;
                    }
                }

                // 3. Trailing stop — beats any take-profit on the same bar.
                // A bar that OPENS beyond the stop gapped through it: the order executes at the open,
                // not at a stop price that never traded. Booking the stop price there is a free
                // option on every gap — RandomWalkNullTests measured it at t≈3-4 on pure noise.
                bool stopHit = side > 0 ? lows[i] <= stop : highs[i] >= stop;
                if (stopHit)
                {
                    double exitPx = side > 0 ? Math.Min(stop, opens[i]) : Math.Max(stop, opens[i]);
                    CloseAll(i, exitPx, isStop: true);
                    EndSession(i);
                    continue;
                }

                // 4. Per-rung take-profit, ATR frozen at fill. Not on the bar the rung filled.
                for (int n = 0; n < levels; n++)
                {
                    if (!filled[n] || freshFill[n]) continue;
                    double tp = entryPx[n] + side * g.TakeProfitAtrMult * fillAtr[n];
                    bool hit = side > 0 ? highs[i] >= tp : lows[i] <= tp;
                    if (hit) Book(i, n, tp, isStop: false);
                }

                // 5. Decisions taken AT the close of bar i (bar i's own data is legitimately known).
                if (side * Slope(i) < 0 || holdCount >= g.MaxHoldBars)
                {
                    CloseAll(i, closes[i], isStop: false);
                    EndSession(i);
                    continue;
                }
                if (everFilled && !filled.Any(f => f))
                {
                    EndSession(i);
                    continue;
                }
                extreme = side > 0 ? Math.Max(extreme, closes[i]) : Math.Min(extreme, closes[i]);
            }
            else
            {
                // Arm at the close of bar i; the ladder rests from bar i+1. No fill on this bar.
                double atrNow = Atr(i);
                double slope  = Slope(i);
                if (adx[i] < g.AdxMin) continue;

                int want = 0;
                if (sides != HybridGridSides.ShortOnly
                    && closes[i] > ema[i] + g.BiasBandAtr * atrNow && slope > g.SlopeMinPct)
                    want = +1;
                else if (sides != HybridGridSides.LongOnly
                    && closes[i] < ema[i] - g.BiasBandAtr * atrNow && slope < -g.SlopeMinPct)
                    want = -1;
                if (want == 0) continue;

                active = true; side = want; extreme = closes[i];
                holdCount = 0; everFilled = false; firstFillTime = default;
                sessionMae = 0; entrySum = 0; entryN = 0;
                Array.Clear(filled);
                sessionFills.Clear();
            }
        }

        if (active)
        {
            int last = candles.Length - 1;
            CloseAll(last, closes[last], isStop: false);
            EndSession(last);
        }
        return result;
    }
}
