namespace TradingGA;

public static class Simulator
{
    // MaxDcaLevels is now a gene (g.MaxDcaLevels)

    public static double Simulate(Genotype g, Candle[] segment, bool useAtr = true) =>
        SharpeRatio(GetReturns(g, segment, useAtr));

    public static List<double> GetReturns(Genotype g, Candle[] segment, bool useAtr = true)
    {
        int startIdx = Math.Max(g.RsiPeriod, g.EmaPeriod);
        if (segment.Length <= startIdx + 2) return [];

        var closes  = segment.Select(c => c.Close).ToArray();
        var highs   = segment.Select(c => c.High).ToArray();
        var lows    = segment.Select(c => c.Low).ToArray();
        var volumes = segment.Select(c => c.Volume).ToArray();
        var rsi5m   = ComputeRsi(closes, g.RsiPeriod);
        var ema5m   = ComputeEma(closes, g.EmaPeriod);
        double avgVolume = volumes.Average();

        // 15m blending: aggregate segment to 15m and compute higher-TF indicators
        double w = g.TimeframeBlend;
        var seg15m   = AggregateSegment(segment, 3);
        var closes15 = seg15m.Select(c => c.Close).ToArray();
        var rsi15m   = closes15.Length > g.RsiPeriod ? ComputeRsi(closes15, g.RsiPeriod) : rsi5m;
        var ema15m   = closes15.Length > g.EmaPeriod ? ComputeEma(closes15, g.EmaPeriod) : ema5m;

        // At 5m index i, corresponding 15m index is i/3
        double BlendedRsi(int i) => (1 - w) * rsi5m[i] + w * rsi15m[Math.Min(i / 3, rsi15m.Length - 1)];
        double BlendedEma(int i) => (1 - w) * ema5m[i] + w * ema15m[Math.Min(i / 3, ema15m.Length - 1)];

        double segAtrPct          = useAtr ? SegmentAtrPct(highs, lows, closes, startIdx) : 1.0;
        double effectiveGrid      = g.GridStepAtrMult    * segAtrPct;  // trailing stop width
        double effectiveDca       = g.DcaTriggerAtrMult  * segAtrPct;
        double effectiveBreakEven = g.BreakEvenAtrMult   * segAtrPct;

        var tradeReturns = new List<double>();

        bool   inTrade          = false;
        double avgEntry         = 0;
        double totalUnits       = 0;
        int    dcaLevel         = 0;
        double tradeHigh        = 0;
        double lowestSinceEntry = 0;
        bool   breakEvenArmed   = false;

        bool   inRecovery    = false;
        int    recoveryLeg   = 0;
        double legEntry      = 0;
        double lowestSinceLeg = 0;

        bool   bosDetected      = false;
        int    bosCandlesWaited = 0;
        double lastHigh         = 0;
        bool   rsiWasOverbought = false;

        for (int i = startIdx; i < segment.Length; i++)
        {
            double price = closes[i];

            // ── Layer 0: look for entry ───────────────────────────────────────
            if (!inTrade)
            {
                double rsiBlended = BlendedRsi(i);
                double emaBlended = BlendedEma(i);

                if (rsiBlended >= g.RsiOverbought) rsiWasOverbought = true;
                if (segment[i].High < lastHigh * g.BosThreshold && !bosDetected) bosDetected = true;

                if (bosDetected)
                {
                    bosCandlesWaited++;
                    bool volumeSpike     = volumes[i] > avgVolume * g.VolumeMultiplier;
                    bool rsiFading       = rsiWasOverbought && rsiBlended < g.RsiOverbought;
                    bool pumpWasExtended = lastHigh > emaBlended;

                    if (bosCandlesWaited >= g.BosCandlesWait && rsiFading && pumpWasExtended && volumeSpike)
                    {
                        inTrade          = true;
                        avgEntry         = price;
                        totalUnits       = 1.0;
                        dcaLevel         = 0;
                        tradeHigh        = price;
                        lowestSinceEntry = price;
                        breakEvenArmed   = false;
                        inRecovery       = false;
                        recoveryLeg      = 0;
                        bosDetected      = false;
                        bosCandlesWaited = 0;
                        rsiWasOverbought = false;
                    }
                }
            }

            // ── Layer 1: normal trade ─────────────────────────────────────────
            else if (!inRecovery)
            {
                if (price < lowestSinceEntry) lowestSinceEntry = price;
                if (price > tradeHigh)        tradeHigh        = price;

                double bestDrop = (avgEntry - lowestSinceEntry) / avgEntry * 100.0;
                if (!breakEvenArmed && effectiveBreakEven > 0 && bestDrop >= effectiveBreakEven)
                    breakEvenArmed = true;

                // Trailing stop: exit when price bounces effectiveGrid% from its lowest
                bool   trailActive = lowestSinceEntry < avgEntry;
                double trailStopPx = lowestSinceEntry * (1.0 + effectiveGrid / 100.0);

                if (trailActive && price >= trailStopPx)
                {
                    tradeReturns.Add((avgEntry - price) / avgEntry * 100.0);
                    inTrade = false;
                }
                // Break-even stop: once armed, exit if price returns to entry
                else if (breakEvenArmed && price >= avgEntry)
                {
                    tradeReturns.Add((avgEntry - price) / avgEntry * 100.0);
                    inTrade = false;
                }
                else
                {
                    bool dcaBos = tradeHigh > avgEntry
                                  && (tradeHigh - price) / tradeHigh * 100.0 >= effectiveDca;
                    if (dcaBos)
                    {
                        if (dcaLevel < g.MaxDcaLevels)
                        {
                            avgEntry   = (avgEntry * totalUnits + price) / (totalUnits + 1.0);
                            totalUnits += 1.0;
                            dcaLevel++;
                            tradeHigh  = price;
                        }
                        else
                        {
                            inRecovery    = true;
                            recoveryLeg   = 0;
                            legEntry      = price;
                            lowestSinceLeg = price;
                        }
                    }
                }
            }

            // ── Layer 2: recovery legs with trailing stop per leg ─────────────
            else
            {
                if (price < lowestSinceLeg) lowestSinceLeg = price;

                bool   legTrailActive = lowestSinceLeg < legEntry;
                double legTrailPx     = lowestSinceLeg * (1.0 + effectiveGrid / 100.0);
                double legRise        = (price - legEntry) / legEntry * 100.0;

                if (legTrailActive && price >= legTrailPx)
                {
                    recoveryLeg++;
                    if (recoveryLeg >= 4)
                    { tradeReturns.Add((avgEntry - price) / avgEntry * 100.0); inTrade = false; }
                    else { legEntry = price; lowestSinceLeg = price; }
                }
                else if (legRise >= effectiveDca * 2)
                {
                    tradeReturns.Add((avgEntry - price) / avgEntry * 100.0);
                    inTrade = false;
                }
            }

            if (segment[i].High > lastHigh) lastHigh = segment[i].High;
        }

        if (inTrade)
            tradeReturns.Add((avgEntry - closes[^1]) / avgEntry * 100.0);

        return tradeReturns;
    }

    public static void Diagnose(Genotype g, IEnumerable<Candle[]> segments, bool useAtr = true)
    {
        int totalSegs = 0, segsWithTrades = 0;
        int noBos = 0, noRsiPeak = 0, noRsiFade = 0, noEma = 0, noVol = 0, noWait = 0;

        foreach (var segment in segments)
        {
            totalSegs++;
            int startIdx = Math.Max(g.RsiPeriod, g.EmaPeriod);
            if (segment.Length <= startIdx + 2) continue;

            var closes  = segment.Select(c => c.Close).ToArray();
            var volumes = segment.Select(c => c.Volume).ToArray();
            var rsi     = ComputeRsi(closes, g.RsiPeriod);
            var ema     = ComputeEma(closes, g.EmaPeriod);
            double avgVolume = volumes.Average();

            bool hadTrade = false;
            bool bosEver = false, rsiPeakEver = false, rsiFadeEver = false;
            bool emaEver = false, volEver = false, waitEver = false;

            bool inTrade = false, bosDetected = false, rsiWasOverbought = false;
            int bosCandlesWaited = 0;
            double lastHigh = 0;

            for (int i = startIdx; i < segment.Length; i++)
            {
                double price = closes[i];
                if (!inTrade)
                {
                    if (rsi[i] >= g.RsiOverbought) { rsiWasOverbought = true; rsiPeakEver = true; }
                    if (segment[i].High < lastHigh * g.BosThreshold && !bosDetected)
                    { bosDetected = true; bosEver = true; }

                    if (bosDetected)
                    {
                        bosCandlesWaited++;
                        if (rsiWasOverbought && rsi[i] < g.RsiOverbought) rsiFadeEver = true;
                        if (lastHigh > ema[i]) emaEver = true;
                        if (volumes[i] > avgVolume * g.VolumeMultiplier) volEver = true;
                        if (bosCandlesWaited >= g.BosCandlesWait) waitEver = true;

                        bool rsiFading       = rsiWasOverbought && rsi[i] < g.RsiOverbought;
                        bool pumpWasExtended = lastHigh > ema[i];
                        bool volumeSpike     = volumes[i] > avgVolume * g.VolumeMultiplier;

                        if (bosCandlesWaited >= g.BosCandlesWait && rsiFading && pumpWasExtended && volumeSpike)
                        { hadTrade = true; inTrade = true; bosDetected = false; bosCandlesWaited = 0; rsiWasOverbought = false; }
                    }
                }
                else
                {
                    double priceDrop = (price < closes[i > 0 ? i - 1 : 0]) ? 1 : 0; // simplified exit
                    inTrade = false; // just reset for diagnosis purposes
                }
                if (segment[i].High > lastHigh) lastHigh = segment[i].High;
            }

            if (hadTrade) segsWithTrades++;
            else
            {
                if (!bosEver)  noBos++;
                else if (!rsiPeakEver) noRsiPeak++;
                else if (!rsiFadeEver) noRsiFade++;
                else if (!waitEver)    noWait++;
                else if (!emaEver)     noEma++;
                else if (!volEver)     noVol++;
            }
        }

        int totalTrades = 0;
        foreach (var seg in segments)
        {
            var closes2  = seg.Select(c => c.Close).ToArray();
            var highs2   = seg.Select(c => c.High).ToArray();
            var lows2    = seg.Select(c => c.Low).ToArray();
            var volumes2 = seg.Select(c => c.Volume).ToArray();
            var rsi2     = ComputeRsi(closes2, g.RsiPeriod);
            var ema2     = ComputeEma(closes2, g.EmaPeriod);
            double avgVol2 = volumes2.Average();
            int startIdx2 = Math.Max(g.RsiPeriod, g.EmaPeriod);
            if (seg.Length <= startIdx2 + 2) continue;
            double segAtrPct2  = useAtr ? SegmentAtrPct(highs2, lows2, closes2, startIdx2) : 1.0;
            double effGrid2    = g.GridStepAtrMult   * segAtrPct2;
            double effDca2     = g.DcaTriggerAtrMult * segAtrPct2;
            bool inT = false, bosD = false, rsiWas = false;
            int bosW = 0; double lH = 0, avgE = 0, totU = 0; int dcaL = 0;
            bool inRec = false; int recLeg = 0; double legE = 0;
            for (int i = startIdx2; i < seg.Length; i++)
            {
                double pr = closes2[i];
                if (!inT)
                {
                    if (rsi2[i] >= g.RsiOverbought) rsiWas = true;
                    if (seg[i].High < lH * g.BosThreshold && !bosD) bosD = true;
                    if (bosD)
                    {
                        bosW++;
                        if (bosW >= g.BosCandlesWait && rsiWas && rsi2[i] < g.RsiOverbought
                            && lH > ema2[i] && volumes2[i] > avgVol2 * g.VolumeMultiplier)
                        { inT = true; avgE = pr; totU = 1; dcaL = 0; inRec = false; recLeg = 0; bosD = false; bosW = 0; rsiWas = false; totalTrades++; }
                    }
                }
                else if (!inRec)
                {
                    double drop = (avgE - pr) / avgE * 100.0, rise = (pr - avgE) / avgE * 100.0;
                    if (drop >= effGrid2) inT = false;
                    else if (rise >= effDca2)
                    { if (dcaL < g.MaxDcaLevels) { avgE = (avgE * totU + pr) / (totU + 1); totU++; dcaL++; } else { inRec = true; recLeg = 0; legE = pr; } }
                }
                else
                {
                    double lDrop = (legE - pr) / legE * 100.0, lRise = (pr - legE) / legE * 100.0;
                    if (lDrop >= effGrid2) { recLeg++; if (recLeg >= 4) inT = false; else legE = pr; }
                    else if (lRise >= effDca2 * 2) inT = false;
                }
                if (seg[i].High > lH) lH = seg[i].High;
            }
        }

        Console.WriteLine($"  Segments: {totalSegs} total, {segsWithTrades} with trades, {totalTrades} total trades");
        Console.WriteLine($"  Blocked: no BoS={noBos}, no RSI peak={noRsiPeak}, no RSI fade={noRsiFade}, " +
                          $"wait not met={noWait}, no EMA ext={noEma}, no vol spike={noVol}");
    }

    // Returns the open-position state at the END of the segment (for paper trading).
    public record TradeState(bool InTrade, double AvgEntry, int DcaLevel, bool InRecovery, int RecoveryLeg);

    public static TradeState GetTradeState(Genotype g, Candle[] segment, bool useAtr = true)
    {
        int startIdx = Math.Max(g.RsiPeriod, g.EmaPeriod);
        if (segment.Length <= startIdx + 2) return new(false, 0, 0, false, 0);

        var closes  = segment.Select(c => c.Close).ToArray();
        var highs   = segment.Select(c => c.High).ToArray();
        var lows    = segment.Select(c => c.Low).ToArray();
        var volumes = segment.Select(c => c.Volume).ToArray();
        var rsi5m   = ComputeRsi(closes, g.RsiPeriod);
        var ema5m   = ComputeEma(closes, g.EmaPeriod);
        double avgVolume = volumes.Average();

        double w       = g.TimeframeBlend;
        var seg15m     = AggregateSegment(segment, 3);
        var closes15   = seg15m.Select(c => c.Close).ToArray();
        var rsi15m     = closes15.Length > g.RsiPeriod ? ComputeRsi(closes15, g.RsiPeriod) : rsi5m;
        var ema15m     = closes15.Length > g.EmaPeriod ? ComputeEma(closes15, g.EmaPeriod) : ema5m;

        double BlendedRsi(int i) => (1 - w) * rsi5m[i] + w * rsi15m[Math.Min(i / 3, rsi15m.Length - 1)];
        double BlendedEma(int i) => (1 - w) * ema5m[i] + w * ema15m[Math.Min(i / 3, ema15m.Length - 1)];

        double segAtrPct          = useAtr ? SegmentAtrPct(highs, lows, closes, startIdx) : 1.0;
        double effectiveGrid      = g.GridStepAtrMult   * segAtrPct;
        double effectiveDca       = g.DcaTriggerAtrMult * segAtrPct;
        double effectiveBreakEven = g.BreakEvenAtrMult  * segAtrPct;

        bool   inTrade          = false;
        double avgEntry         = 0; double totalUnits = 0; double tradeHigh = 0;
        int    dcaLevel         = 0;
        double lowestSinceEntry = 0; bool breakEvenArmed = false;
        bool   inRecovery       = false; int recoveryLeg = 0;
        double legEntry         = 0; double lowestSinceLeg = 0;
        bool   bosDetected      = false; int bosCandlesWaited = 0;
        double lastHigh         = 0; bool rsiWasOverbought = false;

        for (int i = startIdx; i < segment.Length; i++)
        {
            double price = closes[i];
            if (!inTrade)
            {
                double rsiB = BlendedRsi(i), emaB = BlendedEma(i);
                if (rsiB >= g.RsiOverbought) rsiWasOverbought = true;
                if (segment[i].High < lastHigh * g.BosThreshold && !bosDetected) bosDetected = true;
                if (bosDetected)
                {
                    bosCandlesWaited++;
                    bool volSpike = volumes[i] > avgVolume * g.VolumeMultiplier;
                    bool rsiFade  = rsiWasOverbought && rsiB < g.RsiOverbought;
                    bool extended = lastHigh > emaB;
                    if (bosCandlesWaited >= g.BosCandlesWait && rsiFade && extended && volSpike)
                    {
                        inTrade = true; avgEntry = price; totalUnits = 1; dcaLevel = 0;
                        tradeHigh = price; lowestSinceEntry = price; breakEvenArmed = false;
                        inRecovery = false; recoveryLeg = 0;
                        bosDetected = false; bosCandlesWaited = 0; rsiWasOverbought = false;
                    }
                }
            }
            else if (!inRecovery)
            {
                if (price < lowestSinceEntry) lowestSinceEntry = price;
                if (price > tradeHigh)        tradeHigh        = price;

                double bestDrop = (avgEntry - lowestSinceEntry) / avgEntry * 100.0;
                if (!breakEvenArmed && effectiveBreakEven > 0 && bestDrop >= effectiveBreakEven)
                    breakEvenArmed = true;

                bool   trailActive = lowestSinceEntry < avgEntry;
                double trailStopPx = lowestSinceEntry * (1.0 + effectiveGrid / 100.0);

                if (trailActive && price >= trailStopPx)
                    inTrade = false;
                else if (breakEvenArmed && price >= avgEntry)
                    inTrade = false;
                else
                {
                    bool dcaBos = tradeHigh > avgEntry && (tradeHigh - price) / tradeHigh * 100.0 >= effectiveDca;
                    if (dcaBos)
                    {
                        if (dcaLevel < g.MaxDcaLevels)
                        { avgEntry = (avgEntry * totalUnits + price) / (totalUnits + 1); totalUnits++; dcaLevel++; tradeHigh = price; }
                        else { inRecovery = true; recoveryLeg = 0; legEntry = price; lowestSinceLeg = price; }
                    }
                }
            }
            else
            {
                if (price < lowestSinceLeg) lowestSinceLeg = price;
                bool   legTrailActive = lowestSinceLeg < legEntry;
                double legTrailPx     = lowestSinceLeg * (1.0 + effectiveGrid / 100.0);
                double legRise        = (price - legEntry) / legEntry * 100.0;

                if (legTrailActive && price >= legTrailPx)
                {
                    recoveryLeg++;
                    if (recoveryLeg >= 4) inTrade = false;
                    else { legEntry = price; lowestSinceLeg = price; }
                }
                else if (legRise >= effectiveDca * 2)
                    inTrade = false;
            }
            if (segment[i].High > lastHigh) lastHigh = segment[i].High;
        }

        return new TradeState(inTrade, avgEntry, dcaLevel, inRecovery, recoveryLeg);
    }

    // Returns the same results as GetReturns but paired with the exit candle's timestamp.
    public static List<(DateTime Time, double Return)> GetTimedReturns(Genotype g, Candle[] segment, bool useAtr = true)
    {
        int startIdx = Math.Max(g.RsiPeriod, g.EmaPeriod);
        if (segment.Length <= startIdx + 2) return [];

        var closes  = segment.Select(c => c.Close).ToArray();
        var highs   = segment.Select(c => c.High).ToArray();
        var lows    = segment.Select(c => c.Low).ToArray();
        var volumes = segment.Select(c => c.Volume).ToArray();
        var rsi5m   = ComputeRsi(closes, g.RsiPeriod);
        var ema5m   = ComputeEma(closes, g.EmaPeriod);
        double avgVolume = volumes.Average();

        double w = g.TimeframeBlend;
        var seg15m   = AggregateSegment(segment, 3);
        var closes15 = seg15m.Select(c => c.Close).ToArray();
        var rsi15m   = closes15.Length > g.RsiPeriod ? ComputeRsi(closes15, g.RsiPeriod) : rsi5m;
        var ema15m   = closes15.Length > g.EmaPeriod ? ComputeEma(closes15, g.EmaPeriod) : ema5m;

        double BlendedRsi(int i) => (1 - w) * rsi5m[i] + w * rsi15m[Math.Min(i / 3, rsi15m.Length - 1)];
        double BlendedEma(int i) => (1 - w) * ema5m[i] + w * ema15m[Math.Min(i / 3, ema15m.Length - 1)];

        double segAtrPct          = useAtr ? SegmentAtrPct(highs, lows, closes, startIdx) : 1.0;
        double effectiveGrid      = g.GridStepAtrMult    * segAtrPct;
        double effectiveDca       = g.DcaTriggerAtrMult  * segAtrPct;
        double effectiveBreakEven = g.BreakEvenAtrMult   * segAtrPct;

        var result = new List<(DateTime, double)>();

        bool   inTrade          = false;
        double avgEntry         = 0; double totalUnits = 0; double tradeHigh = 0;
        int    dcaLevel         = 0;
        double lowestSinceEntry = 0; bool breakEvenArmed = false;
        bool   inRecovery       = false; int recoveryLeg = 0;
        double legEntry         = 0; double lowestSinceLeg = 0;
        bool   bosDetected      = false; int bosCandlesWaited = 0;
        double lastHigh         = 0; bool rsiWasOverbought = false;

        for (int i = startIdx; i < segment.Length; i++)
        {
            double price = closes[i];
            if (!inTrade)
            {
                double rsiB = BlendedRsi(i), emaB = BlendedEma(i);
                if (rsiB >= g.RsiOverbought) rsiWasOverbought = true;
                if (segment[i].High < lastHigh * g.BosThreshold && !bosDetected) bosDetected = true;
                if (bosDetected)
                {
                    bosCandlesWaited++;
                    bool volSpike = volumes[i] > avgVolume * g.VolumeMultiplier;
                    bool rsiFade  = rsiWasOverbought && rsiB < g.RsiOverbought;
                    bool extended = lastHigh > emaB;
                    if (bosCandlesWaited >= g.BosCandlesWait && rsiFade && extended && volSpike)
                    {
                        inTrade = true; avgEntry = price; totalUnits = 1; dcaLevel = 0;
                        tradeHigh = price; lowestSinceEntry = price; breakEvenArmed = false;
                        inRecovery = false; recoveryLeg = 0;
                        bosDetected = false; bosCandlesWaited = 0; rsiWasOverbought = false;
                    }
                }
            }
            else if (!inRecovery)
            {
                if (price < lowestSinceEntry) lowestSinceEntry = price;
                if (price > tradeHigh)        tradeHigh        = price;

                double bestDrop = (avgEntry - lowestSinceEntry) / avgEntry * 100.0;
                if (!breakEvenArmed && effectiveBreakEven > 0 && bestDrop >= effectiveBreakEven)
                    breakEvenArmed = true;

                bool   trailActive = lowestSinceEntry < avgEntry;
                double trailStopPx = lowestSinceEntry * (1.0 + effectiveGrid / 100.0);

                if (trailActive && price >= trailStopPx)
                { result.Add((segment[i].Time, (avgEntry - price) / avgEntry * 100.0)); inTrade = false; }
                else if (breakEvenArmed && price >= avgEntry)
                { result.Add((segment[i].Time, (avgEntry - price) / avgEntry * 100.0)); inTrade = false; }
                else
                {
                    bool dcaBos = tradeHigh > avgEntry && (tradeHigh - price) / tradeHigh * 100.0 >= effectiveDca;
                    if (dcaBos)
                    {
                        if (dcaLevel < g.MaxDcaLevels)
                        { avgEntry = (avgEntry * totalUnits + price) / (totalUnits + 1); totalUnits++; dcaLevel++; tradeHigh = price; }
                        else { inRecovery = true; recoveryLeg = 0; legEntry = price; lowestSinceLeg = price; }
                    }
                }
            }
            else
            {
                if (price < lowestSinceLeg) lowestSinceLeg = price;
                bool   legTrailActive = lowestSinceLeg < legEntry;
                double legTrailPx     = lowestSinceLeg * (1.0 + effectiveGrid / 100.0);
                double legRise        = (price - legEntry) / legEntry * 100.0;

                if (legTrailActive && price >= legTrailPx)
                {
                    recoveryLeg++;
                    if (recoveryLeg >= 4)
                    { result.Add((segment[i].Time, (avgEntry - price) / avgEntry * 100.0)); inTrade = false; }
                    else { legEntry = price; lowestSinceLeg = price; }
                }
                else if (legRise >= effectiveDca * 2)
                { result.Add((segment[i].Time, (avgEntry - price) / avgEntry * 100.0)); inTrade = false; }
            }
            if (segment[i].High > lastHigh) lastHigh = segment[i].High;
        }

        if (inTrade) result.Add((segment[^1].Time, (avgEntry - closes[^1]) / avgEntry * 100.0));
        return result;
    }

    // ── Grid trading (long-only buy-the-dip on non-pump candles) ─────────────
    // Entry:  price drops DcaTriggerAtrMult*ATR% from a local high.
    // Exit:   trailing take-profit — price drops GridStepAtrMult*ATR% from trade high.
    // DCA:    each further drop of DcaTriggerAtrMult*ATR% adds a long unit (max MaxDcaLevels).
    // BE:     once the trade rises BreakEvenAtrMult*ATR%, arm break-even; exit if falls to entry.
    // Same params as the pump short but mirrored to the long side.

    public static List<double> GetGridReturns(Genotype g, Candle[] candles, bool useAtr = true)
        => RunGrid(g, candles, useAtr).Trades.Select(t => t.Return).ToList();

    public static List<(DateTime Time, double Return)> GetTimedGridReturns(Genotype g, Candle[] candles, bool useAtr = true)
        => RunGrid(g, candles, useAtr).Trades;

    public static TradeState GetGridTradeState(Genotype g, Candle[] candles, bool useAtr = true)
        => RunGrid(g, candles, useAtr).FinalState;

    private static (List<(DateTime Time, double Return)> Trades, TradeState FinalState) RunGrid(
        Genotype g, Candle[] candles, bool useAtr)
    {
        int startIdx = Math.Max(g.EmaPeriod, 14);
        if (candles.Length <= startIdx + 10)
            return ([], new TradeState(false, 0, 0, false, 0));

        var closes = candles.Select(c => c.Close).ToArray();
        var highs  = candles.Select(c => c.High).ToArray();
        var lows   = candles.Select(c => c.Low).ToArray();

        double segAtrPct     = useAtr ? SegmentAtrPct(highs, lows, closes, startIdx) : 0.5;
        double gridStep      = g.GridStepAtrMult    * segAtrPct;  // trailing drop from high to exit
        double dcaTrigger    = g.DcaTriggerAtrMult  * segAtrPct;  // drop% that triggers entry / DCA
        double breakEvenDist = g.BreakEvenAtrMult   * segAtrPct;  // gain% needed to arm break-even

        var result = new List<(DateTime, double)>();

        bool   inTrade    = false;
        double avgEntry   = 0;
        double totalUnits = 0;
        int    dcaLevel   = 0;
        double dcaRef     = 0;   // price at last DCA anchor (or entry)
        double tradeHigh  = 0;   // highest close seen since entry
        bool   beArmed    = false;
        double localHigh  = closes[startIdx];

        for (int i = startIdx; i < candles.Length; i++)
        {
            double price = closes[i];

            if (!inTrade)
            {
                if (price > localHigh) localHigh = price;
                if ((localHigh - price) / localHigh * 100.0 >= dcaTrigger)
                {
                    inTrade = true; avgEntry = price; totalUnits = 1;
                    dcaLevel = 0; dcaRef = price; tradeHigh = price; beArmed = false;
                }
            }
            else
            {
                if (price > tradeHigh) tradeHigh = price;

                // Arm break-even once the trade reached breakEvenDist% profit
                double bestGain = (tradeHigh - avgEntry) / avgEntry * 100.0;
                if (!beArmed && breakEvenDist > 0 && bestGain >= breakEvenDist) beArmed = true;

                // Trailing take-profit: price dropped gridStep% from the trade high
                bool   trailActive = tradeHigh > avgEntry;
                double trailStop   = tradeHigh * (1.0 - gridStep / 100.0);

                if (trailActive && price <= trailStop)
                {
                    result.Add((candles[i].Time, (price - avgEntry) / avgEntry * 100.0));
                    inTrade = false; localHigh = price;
                }
                else if (beArmed && price <= avgEntry)
                {
                    result.Add((candles[i].Time, (price - avgEntry) / avgEntry * 100.0));
                    inTrade = false; localHigh = price;
                }
                else
                {
                    // DCA: price dropped another dcaTrigger% below the last DCA anchor
                    if ((dcaRef - price) / dcaRef * 100.0 >= dcaTrigger && dcaLevel < g.MaxDcaLevels)
                    {
                        avgEntry    = (avgEntry * totalUnits + price) / (totalUnits + 1);
                        totalUnits += 1;
                        dcaLevel++;
                        dcaRef      = price;
                    }
                }
            }
        }

        if (inTrade)
            result.Add((candles[^1].Time, (closes[^1] - avgEntry) / avgEntry * 100.0));

        return (result, new TradeState(inTrade, avgEntry, dcaLevel, false, 0));
    }

    // ── Portfolio simulation ──────────────────────────────────────────────────

    public record PortfolioResult(
        double StartBalance,
        double EndBalance,
        double RealizedProfit,
        double TotalValue,
        double MaxDrawdownPct,
        double Confidence,
        double AvgPositionEur,
        int    TradesCount,
        int    TradesToTenPct   // -1 if never reached
    );

    // Confidence via half-Kelly: derived from training statistics, applied to val trades.
    public static double ComputeConfidence(List<double> returns)
    {
        if (returns.Count < 5) return 0;
        var wins   = returns.Where(r => r > 0).ToList();
        var losses = returns.Where(r => r <= 0).ToList();
        if (wins.Count == 0) return 0;
        double p      = (double)wins.Count / returns.Count;
        double avgWin = wins.Average();
        double avgLoss = losses.Count > 0 ? Math.Abs(losses.Average()) : avgWin;
        double b      = avgWin / avgLoss;
        double kelly  = (p * b - (1 - p)) / b;     // full Kelly
        double halfKelly = kelly / 2.0;             // half-Kelly for safety
        return Math.Clamp(halfKelly, 0.0, 1.0);
    }

    public static PortfolioResult SimulatePortfolio(
        List<double> valReturns,
        double       confidence,
        double       startBalance     = 100.0,
        double       maxPositionPct   = 0.05,
        double       profitReinvest   = 0.10)
    {
        double balance        = startBalance;
        double realizedProfit = 0;
        double peak           = startBalance;
        double maxDd          = 0;
        double totalPosSizeEur = 0;
        int    tradesToTenPct = -1;

        for (int t = 0; t < valReturns.Count; t++)
        {
            double posEur = confidence * maxPositionPct * balance;
            totalPosSizeEur += posEur;
            double pnlEur = valReturns[t] / 100.0 * posEur;

            if (pnlEur >= 0)
            {
                balance        += profitReinvest * pnlEur;   // 10% of profit compounds
                realizedProfit += (1 - profitReinvest) * pnlEur; // 90% taken out
            }
            else
            {
                balance += pnlEur;                            // full loss hits balance
            }

            double totalNow = balance + realizedProfit;
            if (tradesToTenPct < 0 && totalNow >= startBalance * 1.10)
                tradesToTenPct = t + 1;

            if (balance > peak) peak = balance;
            double dd = peak > 0 ? (peak - balance) / peak * 100.0 : 0;
            if (dd > maxDd) maxDd = dd;
        }

        return new PortfolioResult(
            StartBalance:    startBalance,
            EndBalance:      balance,
            RealizedProfit:  realizedProfit,
            TotalValue:      balance + realizedProfit,
            MaxDrawdownPct:  maxDd,
            Confidence:      confidence,
            AvgPositionEur:  valReturns.Count > 0 ? totalPosSizeEur / valReturns.Count : 0,
            TradesCount:     valReturns.Count,
            TradesToTenPct:  tradesToTenPct
        );
    }

    // Sharpe ratio as fitness: mean / stddev * sqrt(n).
    // Returns 0 if < 5 trades or profit factor < 1.3 (not enough edge to survive fees).
    public static double SharpeRatio(List<double> returns)
    {
        if (returns.Count < 5) return 0;
        double grossProfit = returns.Where(r => r > 0).Sum();
        double grossLoss   = Math.Abs(returns.Where(r => r <= 0).Sum());
        if (grossLoss < 1e-10 || grossProfit / grossLoss < 1.3) return 0;
        double mean = returns.Average();
        double std  = Math.Sqrt(returns.Select(r => Math.Pow(r - mean, 2)).Average());
        return std < 1e-10 ? 0 : mean / std * Math.Sqrt(returns.Count);
    }

    // ── Additional KPI metrics ────────────────────────────────────────────────

    public static double SortinoRatio(List<double> returns)
    {
        if (returns.Count < 5) return 0;
        double mean       = returns.Average();
        var    negReturns = returns.Where(r => r < 0).ToList();
        if (negReturns.Count == 0) return mean > 0 ? double.MaxValue : 0;
        double downStd = Math.Sqrt(negReturns.Select(r => r * r).Average());
        return downStd < 1e-10 ? 0 : mean / downStd * Math.Sqrt(returns.Count);
    }

    public static double ProfitFactor(List<double> returns)
    {
        double gross = returns.Where(r => r > 0).Sum();
        double loss  = Math.Abs(returns.Where(r => r <= 0).Sum());
        return loss < 1e-10 ? (gross > 0 ? 99.99 : 0) : Math.Min(gross / loss, 99.99);
    }

    // Calmar: annualised simple return divided by max drawdown (both in %).
    // Assumes returns are per-trade return%s (not portfolio); uses cumulative sum as proxy.
    public static double CalmarRatio(List<double> returns)
    {
        if (returns.Count < 5) return 0;
        double cumulative = 0, peak = 0, maxDd = 0;
        foreach (var r in returns)
        {
            cumulative += r;
            if (cumulative > peak) peak = cumulative;
            double dd = peak - cumulative;
            if (dd > maxDd) maxDd = dd;
        }
        if (maxDd < 1e-10) return cumulative > 0 ? double.MaxValue : 0;
        double annualised = cumulative * (252.0 / returns.Count); // rough daily scaling
        return annualised / maxDd;
    }

    public static int MaxConsecLosses(List<double> returns)
    {
        int max = 0, cur = 0;
        foreach (var r in returns)
        {
            if (r <= 0) { cur++; if (cur > max) max = cur; }
            else cur = 0;
        }
        return max;
    }

    // ── Unified regime-aware simulator ────────────────────────────────────────
    // Runs on the FULL candle series. Regime at each candle decides which strategy fires:
    //   TrendingUp  (ADX ≥ threshold AND price > EMA) → pump short
    //   Ranging     (ADX < threshold)                 → grid long
    //   TrendingDown                                   → flat
    // One trade open at a time (pump XOR grid).

    public static List<(DateTime Time, double Return, string Kind)>
        GetUnifiedReturns(Genotype g, Candle[] candles, bool useAtr = true)
    {
        var (trades, _) = RunUnified(g, candles, useAtr);
        return trades;
    }

    public record UnifiedTradeState(
        bool   PumpOpen, double PumpEntry, int PumpDca, bool PumpInRecovery, int PumpRecoveryLeg,
        string Regime);

    public static UnifiedTradeState GetUnifiedTradeState(Genotype g, Candle[] candles, bool useAtr = true)
    {
        var (_, state) = RunUnified(g, candles, useAtr);
        return state;
    }

    private static (List<(DateTime, double, string)> Trades, UnifiedTradeState FinalState)
        RunUnified(Genotype g, Candle[] candles, bool useAtr)
    {
        int adxWarmup = g.RegimeAdxPeriod * 2 + 1;
        int startIdx  = Math.Max(Math.Max(g.RsiPeriod, g.EmaPeriod), adxWarmup);
        if (candles.Length <= startIdx + 10)
            return ([], new UnifiedTradeState(false, 0, 0, false, 0, "Ranging"));

        var closes  = candles.Select(c => c.Close).ToArray();
        var highs   = candles.Select(c => c.High).ToArray();
        var lows    = candles.Select(c => c.Low).ToArray();
        var volumes = candles.Select(c => c.Volume).ToArray();

        var rsi5m      = ComputeRsi(closes, g.RsiPeriod);
        var ema5m      = ComputeEma(closes, g.EmaPeriod);
        var adx        = ComputeAdx(highs, lows, closes, g.RegimeAdxPeriod);
        double avgVol  = volumes.Average();

        double w       = g.TimeframeBlend;
        var seg15m     = AggregateSegment(candles, 3);
        var closes15   = seg15m.Select(c => c.Close).ToArray();
        var rsi15m     = closes15.Length > g.RsiPeriod ? ComputeRsi(closes15, g.RsiPeriod) : rsi5m;
        var ema15m     = closes15.Length > g.EmaPeriod ? ComputeEma(closes15, g.EmaPeriod) : ema5m;

        double BlendRsi(int i) => (1 - w) * rsi5m[i] + w * rsi15m[Math.Min(i / 3, rsi15m.Length - 1)];
        double BlendEma(int i) => (1 - w) * ema5m[i] + w * ema15m[Math.Min(i / 3, ema15m.Length - 1)];

        double atrPct    = useAtr ? SegmentAtrPct(highs, lows, closes, startIdx) : 1.0;
        double gridStep  = g.GridStepAtrMult    * atrPct;
        double dcaTrig   = g.DcaTriggerAtrMult  * atrPct;
        double beAtr     = g.BreakEvenAtrMult   * atrPct;

        var result = new List<(DateTime, double, string)>();

        // ── Pump short state ──────────────────────────────────────────────────
        bool   pOpen = false,  pBosFound = false, pRsiOB = false;
        bool   pInRec = false, pBeArmed  = false;
        int    pBosWait = 0,   pDcaLvl  = 0,  pRecLeg = 0;
        double pEntry  = 0,    pUnits   = 0,   pHigh   = 0, pLow = 0;
        double pLegE   = 0,    pLegLow  = 0,   pLastHi = 0;

        string lastRegime = "Ranging";

        for (int i = startIdx; i < candles.Length; i++)
        {
            double price = closes[i];
            double rsi   = BlendRsi(i);
            double ema   = BlendEma(i);

            bool trendUp  = adx[i] >= g.RegimeAdxThreshold && price > ema;
            bool ranging  = adx[i] <  g.RegimeAdxThreshold;
            lastRegime    = trendUp ? "TrendingUp" : (ranging ? "Ranging" : "TrendingDown");

            // ── PUMP SHORT ────────────────────────────────────────────────────
            if (!pOpen)
            {
                if (trendUp)
                {
                    if (rsi >= g.RsiOverbought) pRsiOB = true;
                    if (candles[i].High < pLastHi * g.BosThreshold && !pBosFound)
                        pBosFound = true;

                    if (pBosFound)
                    {
                        pBosWait++;
                        bool volOk   = volumes[i] > avgVol * g.VolumeMultiplier;
                        bool rsiFade = pRsiOB && rsi < g.RsiOverbought;
                        bool extd    = pLastHi > ema;

                        if (pBosWait >= g.BosCandlesWait && rsiFade && extd && volOk)
                        {
                            pOpen = true;  pEntry = price; pUnits = 1;
                            pDcaLvl = 0;   pHigh  = price; pLow   = price;
                            pBeArmed = false; pInRec = false; pRecLeg = 0;
                            pBosFound = false; pBosWait = 0; pRsiOB = false;
                        }
                    }
                }
                else
                {
                    pBosFound = false; pBosWait = 0; pRsiOB = false;
                }
            }
            else if (!pInRec)
            {
                if (price < pLow) pLow = price;
                if (price > pHigh) pHigh = price;

                double bestDrop = (pEntry - pLow) / pEntry * 100.0;
                if (!pBeArmed && beAtr > 0 && bestDrop >= beAtr) pBeArmed = true;

                bool   trailActive = pLow < pEntry;
                double trailStop   = pLow * (1.0 + gridStep / 100.0);

                if (trailActive && price >= trailStop)
                {
                    result.Add((candles[i].Time, (pEntry - price) / pEntry * 100.0, "pump"));
                    pOpen = false;
                }
                else if (pBeArmed && price >= pEntry)
                {
                    result.Add((candles[i].Time, (pEntry - price) / pEntry * 100.0, "pump"));
                    pOpen = false;
                }
                else
                {
                    bool dcaBos = pHigh > pEntry && (pHigh - price) / pHigh * 100.0 >= dcaTrig;
                    if (dcaBos)
                    {
                        if (pDcaLvl < g.MaxDcaLevels)
                        { pEntry = (pEntry * pUnits + price) / (pUnits + 1); pUnits++; pDcaLvl++; pHigh = price; }
                        else
                        { pInRec = true; pRecLeg = 0; pLegE = price; pLegLow = price; }
                    }
                }
            }
            else  // pump recovery legs
            {
                if (price < pLegLow) pLegLow = price;
                bool   legActive = pLegLow < pLegE;
                double legStop   = pLegLow * (1.0 + gridStep / 100.0);

                if (legActive && price >= legStop)
                {
                    pRecLeg++;
                    if (pRecLeg >= 4)
                    { result.Add((candles[i].Time, (pEntry - price) / pEntry * 100.0, "pump")); pOpen = false; }
                    else { pLegE = price; pLegLow = price; }
                }
                else if ((price - pLegE) / pLegE * 100.0 >= dcaTrig * 2)
                {
                    result.Add((candles[i].Time, (pEntry - price) / pEntry * 100.0, "pump"));
                    pOpen = false;
                }
            }

            if (candles[i].High > pLastHi) pLastHi = candles[i].High;
        }

        if (pOpen) result.Add((candles[^1].Time, (pEntry - closes[^1]) / pEntry * 100.0, "pump"));

        var finalState = new UnifiedTradeState(
            PumpOpen: pOpen, PumpEntry: pEntry, PumpDca: pDcaLvl,
            PumpInRecovery: pInRec, PumpRecoveryLeg: pRecLeg,
            Regime: lastRegime);

        return (result, finalState);
    }

    private static Candle[] AggregateSegment(Candle[] segment, int factor)
    {
        var result = new List<Candle>(segment.Length / factor + 1);
        for (int i = 0; i + factor <= segment.Length; i += factor)
        {
            double high = segment[i].High, low = segment[i].Low, vol = 0;
            for (int j = i; j < i + factor; j++)
            {
                if (segment[j].High > high) high = segment[j].High;
                if (segment[j].Low  < low)  low  = segment[j].Low;
                vol += segment[j].Volume;
            }
            result.Add(new Candle(segment[i].Time, segment[i].Open, high, low, segment[i + factor - 1].Close, vol));
        }
        return result.ToArray();
    }

    private static double SegmentAtrPct(double[] highs, double[] lows, double[] closes, int startIdx, int atrPeriod = 14)
    {
        var atr = ComputeAtr(highs, lows, closes, atrPeriod);
        double sum = 0; int count = 0;
        for (int i = startIdx; i < closes.Length; i++)
            if (atr[i] > 0 && closes[i] > 0) { sum += atr[i] / closes[i] * 100.0; count++; }
        return count > 0 ? sum / count : 0.2;
    }

    private static double[] ComputeAtr(double[] highs, double[] lows, double[] closes, int period)
    {
        var tr  = new double[closes.Length];
        var atr = new double[closes.Length];
        tr[0] = highs[0] - lows[0];
        for (int i = 1; i < closes.Length; i++)
            tr[i] = Math.Max(highs[i] - lows[i],
                    Math.Max(Math.Abs(highs[i] - closes[i - 1]),
                             Math.Abs(lows[i]  - closes[i - 1])));
        double sum = 0;
        int init = Math.Min(period, closes.Length);
        for (int i = 0; i < init; i++) sum += tr[i];
        atr[init - 1] = sum / init;
        for (int i = init; i < closes.Length; i++)
            atr[i] = (atr[i - 1] * (period - 1) + tr[i]) / period;
        return atr;
    }

    private static double[] ComputeRsi(double[] closes, int period)
    {
        var rsi = new double[closes.Length];
        double avgGain = 0, avgLoss = 0;

        for (int i = 1; i <= period; i++)
        {
            double diff = closes[i] - closes[i - 1];
            if (diff > 0) avgGain += diff; else avgLoss -= diff;
        }
        avgGain /= period;
        avgLoss /= period;

        for (int i = period; i < closes.Length; i++)
        {
            if (i > period)
            {
                double diff = closes[i] - closes[i - 1];
                avgGain = (avgGain * (period - 1) + Math.Max(diff, 0)) / period;
                avgLoss = (avgLoss * (period - 1) + Math.Max(-diff, 0)) / period;
            }
            rsi[i] = avgLoss == 0 ? 100 : 100 - 100 / (1 + avgGain / avgLoss);
        }
        return rsi;
    }

    private static double[] ComputeEma(double[] closes, int period)
    {
        var ema = new double[closes.Length];
        double k = 2.0 / (period + 1);
        ema[0] = closes[0];
        for (int i = 1; i < closes.Length; i++)
            ema[i] = closes[i] * k + ema[i - 1] * (1 - k);
        return ema;
    }

    // Wilder's smoothed ADX.
    private static double[] ComputeAdx(double[] highs, double[] lows, double[] closes, int period)
    {
        int n = closes.Length;
        var tr    = new double[n];
        var pDm   = new double[n];
        var mDm   = new double[n];

        for (int i = 1; i < n; i++)
        {
            double hDiff = highs[i] - highs[i - 1];
            double lDiff = lows[i - 1] - lows[i];
            tr[i]  = Math.Max(highs[i] - lows[i],
                     Math.Max(Math.Abs(highs[i] - closes[i - 1]),
                              Math.Abs(lows[i]  - closes[i - 1])));
            pDm[i] = hDiff > lDiff && hDiff > 0 ? hDiff : 0;
            mDm[i] = lDiff > hDiff && lDiff > 0 ? lDiff : 0;
        }

        var sTr  = new double[n];
        var sPDm = new double[n];
        var sMDm = new double[n];
        var dx   = new double[n];
        var adx  = new double[n];

        if (period >= n) return adx;

        // Seed first Wilder sum
        for (int i = 1; i <= period; i++) { sTr[period] += tr[i]; sPDm[period] += pDm[i]; sMDm[period] += mDm[i]; }

        for (int i = period + 1; i < n; i++)
        {
            sTr[i]  = sTr[i-1]  - sTr[i-1]  / period + tr[i];
            sPDm[i] = sPDm[i-1] - sPDm[i-1] / period + pDm[i];
            sMDm[i] = sMDm[i-1] - sMDm[i-1] / period + mDm[i];

            if (sTr[i] < 1e-10) continue;
            double pDi   = 100.0 * sPDm[i] / sTr[i];
            double mDi   = 100.0 * sMDm[i] / sTr[i];
            double diSum = pDi + mDi;
            dx[i] = diSum > 1e-10 ? 100.0 * Math.Abs(pDi - mDi) / diSum : 0;
        }

        // Seed ADX from DX
        int adxStart = period * 2;
        if (adxStart >= n) return adx;
        double sumDx = 0;
        for (int i = period + 1; i <= adxStart && i < n; i++) sumDx += dx[i];
        adx[adxStart] = sumDx / period;

        for (int i = adxStart + 1; i < n; i++)
            adx[i] = (adx[i - 1] * (period - 1) + dx[i]) / period;

        return adx;
    }
}
