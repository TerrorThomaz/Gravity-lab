// src/core/VariantRouter.cs
namespace TradingGA;

public record VariantSpec<T>(string Id, double AtrLow, double AtrHigh, T? Genotype)
    where T : class;

public static class VariantRouter
{
    private const int AtrPeriod   = 14;
    private const int AtrBaseline = 100;

    // Fraction of bars in [start, end) where ATR_ratio falls in [atrLow, atrHigh).
    // ATR_ratio[i] = atr[i] / mean(atr[i-AtrBaseline .. i-1]).
    // Bars with insufficient baseline history are excluded from the count.
    public static double VolCoverage(double[] atr, int start, int end, double atrLow, double atrHigh)
    {
        int total = 0, inside = 0;
        for (int i = Math.Max(start, AtrBaseline); i < end; i++)
        {
            double baseline = 0;
            for (int j = i - AtrBaseline; j < i; j++) baseline += atr[j];
            baseline /= AtrBaseline;
            if (baseline < 1e-10) continue;
            double ratio = atr[i] / baseline;
            total++;
            if (ratio >= atrLow && ratio < atrHigh) inside++;
        }
        return total == 0 ? 1.0 : (double)inside / total;
    }

    // Returns the genotype of the best matching variant, or null to abstain.
    // "Best" = tightest AtrHigh - AtrLow among variants whose range contains
    // the ATR ratio at barIndex and whose Genotype is non-null.
    // Requires barIndex >= AtrBaseline.
    public static T? Select<T>(double[] atr, int barIndex, VariantSpec<T>[] variants)
        where T : class
    {
        if (barIndex < AtrBaseline || atr.Length <= barIndex) return null;

        double baseline = 0;
        for (int j = barIndex - AtrBaseline; j < barIndex; j++) baseline += atr[j];
        baseline /= AtrBaseline;
        if (baseline < 1e-10) return null;
        double ratio = atr[barIndex] / baseline;

        VariantSpec<T>? best = null;
        double bestWidth = double.MaxValue;
        foreach (var v in variants)
        {
            if (v.Genotype == null) continue;
            if (ratio < v.AtrLow || ratio >= v.AtrHigh) continue;
            double width = v.AtrHigh - v.AtrLow;
            if (width < bestWidth) { bestWidth = width; best = v; }
        }
        return best?.Genotype;
    }
}
