namespace TradingGA;

// Mark-to-market drawdown — Layer 0 of the risk model.
//
// WHY THIS EXISTS
// SimulatePortfolioExposureCapped books a trade's entire P&L the moment it opens and measures
// drawdown only at those instants. Two consequences, both of which make its MaxDrawdownPct unable
// to answer "how much risk is this book carrying":
//
//   1. Positions that are open and underwater contribute NOTHING until they are booked. Ten
//      simultaneously losing positions read as zero drawdown.
//   2. Every term in that simulator is proportional to `balance` — posEur = frac*balance, and when
//      the cap binds, headroom = cap*balance - open. So percentage drawdown is scale-invariant BY
//      CONSTRUCTION: it literally cannot respond to a change in the exposure cap. Measured, it sat
//      at exactly 2.3% across caps 0.30/0.50/0.80 while average position size scaled 3.5x.
//
// That is why the cap sweep showed 3.7x return at flat drawdown. Not free leverage — a risk metric
// structurally incapable of moving.
//
// WHAT THIS COMPUTES
// A time-grid walk that values open positions continuously instead of only at entry. Each position
// accrues its P&L linearly across its holding period, so N overlapping trades are all simultaneously
// visible in equity. Peak-to-trough is then taken on that curve.
//
// WHAT IT DELIBERATELY DOES NOT DO
// This is NOT a true MAE model. Linear accrual understates intra-trade excursion: a trade that dives
// -15% before recovering to +2% is carried as a smooth ride to +2%. Capturing that needs the worst
// excursion per trade, which the simulators track for the FAVOURABLE direction only (trailLow /
// trailHigh drive the trailing stop) and never record for the adverse one. Adding it means touching
// every simulator's return type.
//
// So treat this as a LOWER BOUND on true drawdown. It is still strictly more informative than the
// realized-at-close number, because it is the term that responds to concurrency — which is exactly
// the axis the exposure cap moves along. If drawdown scales with the cap here, it scales for real.
public static class MarkToMarket
{
    public record Result(
        double MaxDrawdownPct,      // peak-to-trough on the mark-to-market curve
        double PeakGrossExposurePct,// max concurrent notional as a fraction of equity
        double AvgGrossExposurePct, // time-weighted average of the same
        int    Steps,
        // The curve itself, one point per step. A portfolio Sharpe has to come from here: a
        // per-trade mean/std is not one, however it is scaled.
        IReadOnlyList<(DateTime Time, double Equity)> Curve);

    // One open position. `UnrealisedPct` is the OPT-IN path: given a time inside the hold it
    // returns the position's unrealised return in percent, marked from the real price series.
    // Supply it and the interior of the hold is real; leave it null and the position accrues
    // linearly, exactly as before (see the LOWER BOUND note above — that is what null means).
    public readonly record struct Position(
        DateTime EntryTime,
        double   ReturnPct,
        TimeSpan Hold,
        double   PosEur,
        Func<DateTime, double>? UnrealisedPct = null);

    // Backward-compatible overload: no price paths, linear accrual, bit-identical to the original.
    public static Result Compute(
        IReadOnlyList<(DateTime EntryTime, double ReturnPct, TimeSpan Hold, double PosEur)> trades,
        double startBalance,
        TimeSpan step)
        => Compute(trades.Select(t => new Position(t.EntryTime, t.ReturnPct, t.Hold, t.PosEur)).ToList(),
                   startBalance, step);

    // trades: position size as ALLOCATED by the exposure-capped sim, so this reflects the cap
    // actually under test rather than a nominal size.
    public static Result Compute(
        IReadOnlyList<Position> trades,
        double startBalance,
        TimeSpan step)
    {
        if (trades.Count == 0) return new Result(0, 0, 0, 0, Array.Empty<(DateTime, double)>());

        var ordered = trades.OrderBy(t => t.EntryTime).ToList();
        DateTime t0 = ordered[0].EntryTime;
        DateTime t1 = ordered.Max(t => t.EntryTime + t.Hold);
        if (t1 <= t0) return new Result(0, 0, 0, 0, Array.Empty<(DateTime, double)>());

        // Realized equity accrues as trades CLOSE; open positions are marked continuously. Keeping
        // the two separate is what stops a position being counted twice at the moment it closes.
        double realized = startBalance;
        double peak = startBalance, maxDd = 0, peakGross = 0, grossTimeSum = 0;
        int steps = 0, next = 0;
        var curve = new List<(DateTime, double)>();
        var open = new List<(DateTime Entry, DateTime Close, double Ret, double Eur, Func<DateTime, double>? Path)>();

        for (DateTime now = t0; now <= t1; now += step)
        {
            while (next < ordered.Count && ordered[next].EntryTime <= now)
            {
                var tr = ordered[next++];
                open.Add((tr.EntryTime, tr.EntryTime + tr.Hold, tr.ReturnPct, tr.PosEur, tr.UnrealisedPct));
            }

            for (int i = open.Count - 1; i >= 0; i--)
            {
                if (open[i].Close > now) continue;
                realized += open[i].Ret / 100.0 * open[i].Eur;   // book it once, at close
                open.RemoveAt(i);
            }

            // Unrealized P&L of everything still open, accrued linearly over its hold.
            double unrealized = 0, gross = 0;
            foreach (var p in open)
            {
                if (p.Path is { } markAt)
                {
                    // Real path: the position is worth what the price says right now. It is still
                    // SETTLED at p.Ret when it closes (above), so costs and stop/target execution
                    // land at the close rather than being smeared across the hold.
                    unrealized += markAt(now) / 100.0 * p.Eur;
                }
                else
                {
                    double span = (p.Close - p.Entry).TotalSeconds;
                    double frac = span > 1e-9 ? Math.Clamp((now - p.Entry).TotalSeconds / span, 0, 1) : 1.0;
                    unrealized += p.Ret / 100.0 * p.Eur * frac;
                }
                gross      += p.Eur;
            }

            double equity = realized + unrealized;
            curve.Add((now, equity));
            if (equity > peak) peak = equity;
            if (peak > 0)
            {
                double dd = (peak - equity) / peak * 100.0;
                if (dd > maxDd) maxDd = dd;
                double grossFrac = gross / peak * 100.0;
                if (grossFrac > peakGross) peakGross = grossFrac;
                grossTimeSum += grossFrac;
            }
            steps++;
        }

        return new Result(maxDd, peakGross, steps > 0 ? grossTimeSum / steps : 0, steps, curve);
    }
}
