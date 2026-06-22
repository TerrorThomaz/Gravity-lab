namespace TradingGA;

// Capacity-gated portfolio simulation.
//
// Instead of trading every qualifying signal, this sim ranks concurrent signals by quality
// and only opens the top-scoring ones up to a per-strategy capacity limit.  When multiple
// signals fire at the same candle, the highest-scored ones get the available slots; lower-
// scored signals are skipped entirely (the signal is stale by the next candle).
//
// Separate capacity pools for swing and grid prevent the high-frequency grid from crowding
// out the lower-frequency swing signals.  Scores are computed inside each strategy's own
// scale so no cross-strategy normalisation is needed.
//
// Swing score  = (rsiAtPeak − rsiOverbought) × (adx / adxThreshold) × (rallySize / minRally)
//                high = deeply overbought peak, strong trend, big extended rally
// Grid score   = ((adxThreshold − adx) / adxThreshold) × ((bbWidthMax − bbWidth) / bbWidthMax)
//                high = well below trending ADX, very compressed range
public record ScoredTrade(
    string   Coin,
    string   Strategy,   // "swing" | "grid"
    DateTime Entry,
    DateTime Exit,
    double   Return,
    double   Score
);

public static class RankedPortfolioSim
{
    public record SimResult(
        double       EndBalance,
        double       ReturnPct,
        double       MaxDD,
        int          SwingTaken,
        int          SwingSkipped,
        int          GridTaken,
        int          GridSkipped,
        List<double> SwingReturns,
        List<double> GridReturns
    );

    // maxSwing / maxGrid: max concurrent open positions per strategy
    // positionSizePct:    fixed fraction of current equity per position (e.g. 0.05 = 5%)
    public static SimResult Run(
        List<ScoredTrade> candidates,
        int    maxSwing        = 5,
        int    maxGrid         = 5,
        double positionSizePct = 0.05,
        double startBalance    = 100.0)
    {
        if (candidates.Count == 0)
            return new(startBalance, 0, 0, 0, 0, 0, 0, [], []);

        // Group by event time — exits before entries at the same timestamp
        var exitMap  = candidates.GroupBy(t => t.Exit).ToDictionary(g => g.Key, g => g.ToList());
        var swingEntryMap = candidates.Where(t => t.Strategy == "swing")
                                      .GroupBy(t => t.Entry)
                                      .ToDictionary(g => g.Key,
                                                    g => g.OrderByDescending(t => t.Score).ToList());
        var gridEntryMap  = candidates.Where(t => t.Strategy == "grid")
                                      .GroupBy(t => t.Entry)
                                      .ToDictionary(g => g.Key,
                                                    g => g.OrderByDescending(t => t.Score).ToList());

        var allTimes = candidates.SelectMany(t => new[] { t.Entry, t.Exit })
                                  .Distinct().OrderBy(t => t).ToList();

        // Open position pools: trade → balance at time of entry
        var swingOpen = new List<(ScoredTrade Trade, double EntryBal)>();
        var gridOpen  = new List<(ScoredTrade Trade, double EntryBal)>();

        var swingRets = new List<double>();
        var gridRets  = new List<double>();
        double balance = startBalance;
        double peak    = startBalance;
        double maxDD   = 0;
        int sTaken = 0, sSkipped = 0, gTaken = 0, gSkipped = 0;

        foreach (var t in allTimes)
        {
            // 1. Close positions whose exit time is at or before this event
            if (exitMap.TryGetValue(t, out var exiting))
            {
                foreach (var trade in exiting)
                {
                    if (trade.Strategy == "swing")
                    {
                        int idx = swingOpen.FindIndex(p => ReferenceEquals(p.Trade, trade));
                        if (idx < 0) continue;
                        balance += trade.Return / 100.0 * positionSizePct * swingOpen[idx].EntryBal;
                        swingRets.Add(trade.Return);
                        swingOpen.RemoveAt(idx);
                    }
                    else
                    {
                        int idx = gridOpen.FindIndex(p => ReferenceEquals(p.Trade, trade));
                        if (idx < 0) continue;
                        balance += trade.Return / 100.0 * positionSizePct * gridOpen[idx].EntryBal;
                        gridRets.Add(trade.Return);
                        gridOpen.RemoveAt(idx);
                    }
                }
            }

            if (balance > peak) peak = balance;
            maxDD = Math.Max(maxDD, (peak - balance) / peak * 100.0);

            // 2. Open new swing positions (ranked by score, top N within capacity)
            if (swingEntryMap.TryGetValue(t, out var swingEntering))
            {
                int cap = maxSwing - swingOpen.Count;
                foreach (var trade in swingEntering)
                {
                    if (cap <= 0) { sSkipped++; continue; }
                    swingOpen.Add((trade, balance));
                    sTaken++;
                    cap--;
                }
            }

            // 3. Open new grid positions (ranked by score, top N within capacity)
            if (gridEntryMap.TryGetValue(t, out var gridEntering))
            {
                int cap = maxGrid - gridOpen.Count;
                foreach (var trade in gridEntering)
                {
                    if (cap <= 0) { gSkipped++; continue; }
                    gridOpen.Add((trade, balance));
                    gTaken++;
                    cap--;
                }
            }
        }

        // Flush remaining open positions (marked-to-market in the simulator already)
        foreach (var (trade, entryBal) in swingOpen)
        {
            balance += trade.Return / 100.0 * positionSizePct * entryBal;
            swingRets.Add(trade.Return);
        }
        foreach (var (trade, entryBal) in gridOpen)
        {
            balance += trade.Return / 100.0 * positionSizePct * entryBal;
            gridRets.Add(trade.Return);
        }

        double ret = (balance - startBalance) / startBalance * 100.0;
        return new(balance, ret, maxDD, sTaken, sSkipped, gTaken, gSkipped, swingRets, gridRets);
    }
}
