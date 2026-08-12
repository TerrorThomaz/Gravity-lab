namespace TradingGA;

// Correlated-shock stress test — Layer 2 of the risk model.
//
// WHAT LAYER 0 LEFT OPEN
// MarkToMarket showed gross exposure scaling 3.4x across the cap sweep while drawdown stayed flat,
// and with a metric that provably responds to concurrency that is a real diversification effect:
// twenty staggered 5% positions ARE smoother than six. But "uncorrelated" is a property of the
// window, not of the strategy. Crypto's actual failure mode is everything moving together, and the
// validation window contains no such day.
//
// So this asks the question the sweep cannot: at a given exposure cap, what simultaneous adverse
// move does the book survive?
//
// METHOD
// Walk the same time grid MarkToMarket uses. At each sampled instant, take the book AS IT ACTUALLY
// STOOD — the real open positions with their real allocated sizes — and apply a synthetic shock of
// -X% to every one of them at once. Record the resulting equity hit. The worst instant across the
// whole history is the answer for that X.
//
// This is a STRESS HARNESS, not a probability model. It says "if this happened, here is the damage",
// and deliberately says nothing about likelihood. That is the honest division: the damage is
// computable from the book, the likelihood is not computable from 3 years of one asset class.
//
// WHY SHOCK EVERY POSITION EQUALLY
// It is the conservative reading and it matches how crypto actually breaks — in a liquidation
// cascade, alt perps move together with BTC and beta is roughly 1 or worse. Longs and shorts are
// NOT netted here, because that netting is precisely what fails in a cascade: shorts gap through
// their stops on the squeeze while longs gap through theirs on the dump. A book that looks hedged
// on paper is not hedged when both sides gap. Direction-aware netting would produce a flattering
// number that the one event it exists to model would not honour.
public static class CorrelatedShock
{
    public record Row(
        double ShockPct,        // the simultaneous adverse move applied to every open position
        double WorstEquityHit,  // % of equity lost at the worst instant
        DateTime WorstAt,       // when that instant was
        double GrossAtWorst);   // gross exposure (% of equity) at that instant

    // positions: the same shape MarkToMarket consumes — entry, return, hold, allocated EUR.
    public static IReadOnlyList<Row> Run(
        IReadOnlyList<(DateTime EntryTime, double ReturnPct, TimeSpan Hold, double PosEur)> trades,
        double startBalance,
        IReadOnlyList<double> shocks,
        TimeSpan step)
    {
        var rows = new List<Row>();
        if (trades.Count == 0) return rows;

        var ordered = trades.OrderBy(t => t.EntryTime).ToList();
        DateTime t0 = ordered[0].EntryTime;
        DateTime t1 = ordered.Max(t => t.EntryTime + t.Hold);
        if (t1 <= t0) return rows;

        foreach (double shock in shocks)
        {
            double realized = startBalance, worstHit = 0, grossAtWorst = 0;
            DateTime worstAt = t0;
            int next = 0;
            var open = new List<(DateTime Entry, DateTime Close, double Ret, double Eur)>();

            for (DateTime now = t0; now <= t1; now += step)
            {
                while (next < ordered.Count && ordered[next].EntryTime <= now)
                {
                    var tr = ordered[next++];
                    open.Add((tr.EntryTime, tr.EntryTime + tr.Hold, tr.ReturnPct, tr.PosEur));
                }
                for (int i = open.Count - 1; i >= 0; i--)
                {
                    if (open[i].Close > now) continue;
                    realized += open[i].Ret / 100.0 * open[i].Eur;
                    open.RemoveAt(i);
                }

                double unrealized = 0, gross = 0;
                foreach (var p in open)
                {
                    double span = (p.Close - p.Entry).TotalSeconds;
                    double frac = span > 1e-9 ? Math.Clamp((now - p.Entry).TotalSeconds / span, 0, 1) : 1.0;
                    unrealized += p.Ret / 100.0 * p.Eur * frac;
                    gross      += p.Eur;
                }

                double equity = realized + unrealized;
                if (equity <= 1e-9 || gross <= 0) continue;

                // Every open position takes the shock simultaneously.
                double hitPct = gross * (shock / 100.0) / equity * 100.0;
                if (hitPct > worstHit)
                {
                    worstHit     = hitPct;
                    worstAt      = now;
                    grossAtWorst = gross / equity * 100.0;
                }
            }
            rows.Add(new Row(shock, worstHit, worstAt, grossAtWorst));
        }
        return rows;
    }

    public static void Print(IReadOnlyList<Row> rows, double capPct)
    {
        Console.WriteLine($"\n── Correlated-shock stress (cap {capPct:P0}) ──────────────────────────────");
        Console.WriteLine("  Every open position takes the same adverse move at once. Longs and shorts are");
        Console.WriteLine("  NOT netted: in a liquidation cascade both sides gap through their stops.");
        Console.WriteLine($"  {"shock",7}  {"equity hit",11}  {"gross then",11}  {"worst instant",20}  verdict");
        Console.WriteLine($"  {new string('-', 68)}");
        foreach (var r in rows)
        {
            string verdict = r.WorstEquityHit >= 100 ? "ACCOUNT GONE"
                           : r.WorstEquityHit >= 50  ? "unrecoverable"
                           : r.WorstEquityHit >= 25  ? "severe"
                           : r.WorstEquityHit >= 10  ? "painful"
                                                     : "survivable";
            Console.WriteLine($"  {r.ShockPct,6:F0}%  {r.WorstEquityHit,10:F1}%  {r.GrossAtWorst,10:F1}%  "
                            + $"{r.WorstAt,20:yyyy-MM-dd HH:mm}  {verdict}");
        }
    }
}
