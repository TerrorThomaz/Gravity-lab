using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingGA;

// Trained HMM parameters + post-hoc state labels. The states themselves are latent and
// unordered; StateLabels maps each state index to a MarketRegime by inspecting the mean
// feature vector (see LabelStates for the rules).
public class HmmGenotype
{
    public int StatesN { get; set; }
    public double[] Pi { get; set; } = [];
    public double[][] A { get; set; } = [];
    public double[][] Means { get; set; } = [];
    public double[][] Vars { get; set; } = [];
    public MarketRegime[] StateLabels { get; set; } = [];
    public double LogLikelihood { get; set; }
    public double Fitness { get; set; }

    public HiddenMarkovModel ToHmm()
    {
        var m = new HiddenMarkovModel(StatesN, Means[0].Length);
        Array.Copy(Pi, m.Pi, StatesN);
        for (int i = 0; i < StatesN; i++)
        {
            Array.Copy(A[i], m.A[i], StatesN);
            Array.Copy(Means[i], m.Means[i], Means[i].Length);
            Array.Copy(Vars[i], m.Vars[i], Vars[i].Length);
        }
        m.LogLikelihood = LogLikelihood;
        return m;
    }

    // Post-hoc labeling of latent states. Uses the mean feature vector per state.
    // Feature indices match RegimeClassifier.Features:
    //   0=PriceVsEma20 (±0.5), 1=PriceVsEma50, 2=PriceVsEma200,
    //   3=Slope50 (±0.10), 4=AtrRatio/5 (0..1 → atrRatio = val*5),
    //   5=Adx/60, 6=Momentum20 (±0.5), 7=Ema20VsEma50, 8=Ema50VsEma200
    public static MarketRegime[] LabelStates(double[][] means)
    {
        var labels = new MarketRegime[means.Length];
        for (int i = 0; i < means.Length; i++)
            labels[i] = LabelOne(means[i]);
        return labels;
    }

    static MarketRegime LabelOne(double[] m)
    {
        double atrRatio = m[4] * 5.0;
        double slope50 = m[3];
        double priceVsEma50 = m[1];
        double momentum20 = m[6];

        if (atrRatio > 2.5) return MarketRegime.HighVol;
        if (slope50 > 0.002 && (priceVsEma50 > 0 || momentum20 > 0)) return MarketRegime.Bull;
        if (slope50 < -0.002 && (priceVsEma50 < 0 || momentum20 < 0)) return MarketRegime.Bear;
        return MarketRegime.Ranging;
    }
}

public record HmmGenotypeDto(
    int StatesN,
    double[] Pi,
    double[][] A,
    double[][] Means,
    double[][] Vars,
    int[] StateLabelsInt,
    double LogLikelihood,
    double Fitness = 0.0)
{
    public HmmGenotype ToGenotype()
    {
        var labels = new MarketRegime[StateLabelsInt.Length];
        for (int i = 0; i < StateLabelsInt.Length; i++)
            labels[i] = (MarketRegime)StateLabelsInt[i];
        return new()
        {
            StatesN = StatesN,
            Pi = Pi,
            A = A,
            Means = Means,
            Vars = Vars,
            StateLabels = labels,
            LogLikelihood = LogLikelihood,
            Fitness = Fitness,
        };
    }

    public static HmmGenotypeDto From(HmmGenotype g)
    {
        var labelsInt = new int[g.StateLabels.Length];
        for (int i = 0; i < g.StateLabels.Length; i++)
            labelsInt[i] = (int)g.StateLabels[i];
        return new(g.StatesN, g.Pi, g.A, g.Means, g.Vars, labelsInt, g.LogLikelihood, g.Fitness);
    }
}
