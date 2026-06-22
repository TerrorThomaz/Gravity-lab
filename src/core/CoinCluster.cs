namespace TradingGA;

// Classifies coins by volatility profile so each cluster gets its own GA-evolved genotype.
//
// Cluster boundaries (median 1h ATR%):
//   Liquid   < 1.5%  — BTC, ETH, BNB, SOL-class; slow, deep order books
//   Mid    1.5–3.5%  — LINK, AVAX, OP, ARB-class; moderate volatility
//   HighVol  ≥ 3.5%  — memes and new-issue tokens; high ATR, thin books
//
// The same FadeShortGA is reused per cluster; only the coin subset differs.
// Genotypes are saved to separate files so train/backtest/papertrade can load the
// appropriate one per coin without touching the universal swing_best_genotype.json.
public enum CoinCluster { Liquid, Mid, HighVol }

public static class CoinClusterHelper
{
    private const double LiquidCeiling  = 1.5;  // median h1 ATR% < 1.5 → Liquid
    private const double MidCeiling     = 3.5;  // 1.5–3.5 → Mid, ≥ 3.5 → HighVol

    public static CoinCluster Classify(Candle[] h1)
    {
        double atrPct = MedianH1AtrPct(h1);
        return atrPct < LiquidCeiling ? CoinCluster.Liquid
             : atrPct < MidCeiling    ? CoinCluster.Mid
             : CoinCluster.HighVol;
    }

    // File name for a cluster's swing genotype.
    public static string GenoFile(CoinCluster cluster) => cluster switch
    {
        CoinCluster.Liquid  => "genotypes/swing_best_genotype_liquid.json",
        CoinCluster.Mid     => "genotypes/swing_best_genotype_mid.json",
        CoinCluster.HighVol => "genotypes/swing_best_genotype_highvol.json",
        _ => throw new ArgumentOutOfRangeException(nameof(cluster))
    };

    public static string Label(CoinCluster cluster) => cluster switch
    {
        CoinCluster.Liquid  => "liquid",
        CoinCluster.Mid     => "mid",
        CoinCluster.HighVol => "highvol",
        _ => "?"
    };

    // Name-based fallback for contexts where only 4h candles are available (papertrade).
    // Matches the expected cluster assignment from training on h1 candles.
    public static CoinCluster ClassifyByName(string symbol) => symbol switch
    {
        "BTCUSDT" or "ETHUSDT" or "BNBUSDT" or "XRPUSDT" or "ADAUSDT" or
        "DOGEUSDT" or "DOTUSDT" or "LTCUSDT" or "BCHUSDT" => CoinCluster.Liquid,

        "SOLUSDT" or "AVAXUSDT" or "LINKUSDT" or "ATOMUSDT" or "NEARUSDT" or
        "INJUSDT" or "OPUSDT" or "ARBUSDT" or "UNIUSDT" or "AAVEUSDT" or
        "RUNEUSDT" or "STXUSDT" or "SUIUSDT" or "APTUSDT" or "LDOUSDT" or
        "GMXUSDT" or "DYDXUSDT" or "SANDUSDT" or "MANAUSDT" or "GALAUSDT" or
        "APEUSDT" or "FILUSDT" or "MATICUSDT" => CoinCluster.Mid,

        _ => CoinCluster.HighVol   // memes, new-issue tokens
    };

    // Compute median (ATR / close × 100) across all h1 bars with a 14-period simple ATR.
    private static double MedianH1AtrPct(Candle[] h1)
    {
        if (h1.Length < 15) return 2.5;   // default mid if insufficient data

        var highs  = h1.Select(c => c.High).ToArray();
        var lows   = h1.Select(c => c.Low).ToArray();
        var closes = h1.Select(c => c.Close).ToArray();
        int n      = closes.Length;

        // True range array
        var tr = new double[n];
        tr[0] = highs[0] - lows[0];
        for (int i = 1; i < n; i++)
            tr[i] = Math.Max(highs[i] - lows[i],
                    Math.Max(Math.Abs(highs[i] - closes[i - 1]),
                             Math.Abs(lows[i]  - closes[i - 1])));

        // Rolling 14-bar simple average of TR as % of close
        const int period = 14;
        var pcts = new List<double>(n - period);
        for (int i = period; i < n; i++)
        {
            double atr = 0;
            for (int j = i - period + 1; j <= i; j++) atr += tr[j];
            atr /= period;
            if (closes[i] > 0)
                pcts.Add(atr / closes[i] * 100.0);
        }

        if (pcts.Count == 0) return 2.5;
        pcts.Sort();
        return pcts[pcts.Count / 2];
    }
}
