namespace TradingGA;

// Coin classification by median 1h ATR%: Liquid <1.5%, Mid 1.5-3.5%, HighVol ≥3.5%.
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

    // Name-based fallback for papertrade (no h1 candles available).
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


    private static double MedianH1AtrPct(Candle[] h1)
    {
        if (h1.Length < 15) return 2.5;   // default mid if insufficient data

        var highs  = h1.Select(c => c.High).ToArray();
        var lows   = h1.Select(c => c.Low).ToArray();
        var closes = h1.Select(c => c.Close).ToArray();
        int n      = closes.Length;


        var tr = new double[n];
        tr[0] = highs[0] - lows[0];
        for (int i = 1; i < n; i++)
            tr[i] = Math.Max(highs[i] - lows[i],
                    Math.Max(Math.Abs(highs[i] - closes[i - 1]),
                             Math.Abs(lows[i]  - closes[i - 1])));


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
