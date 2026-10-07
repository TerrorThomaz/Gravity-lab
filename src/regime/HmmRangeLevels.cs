namespace TradingGA;

// HMM-derived support/resistance for range trading (PROTOTYPE).
//
// The HMM cannot see price levels — its 9 features are scale-free ratios — but it CAN say when
// the market is ranging. This class turns that into levels: while P(ranging) holds, support and
// resistance are simply the running low/high of the current ranging segment. The HMM does the
// timing, plain price arithmetic does the levels.
//
// CAUSALITY: everything at index i uses bars [0, i] only — the HMM forward filter is causal and
// the running extremes include bar i itself. A consumer that acts on bar i+1 must therefore read
// index i (HmmRangeGridSimulator does exactly that); reading index i and filling on bar i would
// let the bar's own low define the support it then "bounces" off.
public sealed record RangeLevels(
    double[] PRange,      // P(ranging) = summed probability of every HMM state labelled Ranging
    bool[]   InRange,     // hysteresis-gated segment membership
    int[]    SegBars,     // bars since the current segment opened (0 when not in a segment)
    double[] Support,     // running low of the segment (NaN when not in a segment)
    double[] Resistance); // running high of the segment (NaN when not in a segment)

public static class HmmRangeLevels
{
    // Enter a segment at P(ranging) >= enterProb, leave below exitProb. The gap is hysteresis:
    // a single threshold flickers at the boundary and every flicker would reset the levels.
    public const double DefaultEnterProb = 0.60;
    public const double DefaultExitProb  = 0.40;

    public static RangeLevels Compute(Candle[] h1, HmmGenotype geno,
        double enterProb = DefaultEnterProb, double exitProb = DefaultExitProb)
    {
        var bars = HmmAnnotator.Annotate(h1, geno);
        var pRange = new double[h1.Length];
        for (int i = 0; i < h1.Length; i++)
        {
            var p = bars[i].HmmProbs;
            if (p == null) continue;
            for (int s = 0; s < p.Length && s < geno.StateLabels.Length; s++)
                if (geno.StateLabels[s] == MarketRegime.Ranging) pRange[i] += p[s];
        }
        return FromProbabilities(h1, pRange, enterProb, exitProb);
    }

    // Sub-window view for train/val splits. Compute on the FULL series first so the forward filter
    // and segment counters are warm at the slice start — still causal, since index i never looked
    // past bar i. SegBars may exceed the slice length when a segment opened before it.
    public static RangeLevels Slice(RangeLevels lv, int from, int to) => new(
        lv.PRange[from..to], lv.InRange[from..to], lv.SegBars[from..to],
        lv.Support[from..to], lv.Resistance[from..to]);

    // Segmentation split from annotation so it can be tested without a trained HMM.
    public static RangeLevels FromProbabilities(Candle[] h1, double[] pRange,
        double enterProb = DefaultEnterProb, double exitProb = DefaultExitProb)
    {
        if (exitProb > enterProb)
            throw new ArgumentException($"exitProb ({exitProb}) must not exceed enterProb ({enterProb})");

        int n = h1.Length;
        var inRange = new bool[n];
        var segBars = new int[n];
        var support = new double[n];
        var resist  = new double[n];

        bool   active = false;
        int    len    = 0;
        double lo = double.NaN, hi = double.NaN;

        for (int i = 0; i < n; i++)
        {
            if (!active && pRange[i] >= enterProb)
            {
                active = true; len = 0; lo = double.MaxValue; hi = double.MinValue;
            }
            else if (active && pRange[i] < exitProb)
            {
                active = false;
            }

            if (active)
            {
                len++;
                lo = Math.Min(lo, h1[i].Low);
                hi = Math.Max(hi, h1[i].High);
                inRange[i] = true;
                segBars[i] = len;
                support[i] = lo;
                resist[i]  = hi;
            }
            else
            {
                support[i] = double.NaN;
                resist[i]  = double.NaN;
            }
        }

        return new RangeLevels(pRange, inRange, segBars, support, resist);
    }
}
