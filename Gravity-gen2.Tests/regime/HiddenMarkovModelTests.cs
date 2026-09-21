using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class HiddenMarkovModelTests
{
    [Fact]
    public void SyntheticRecovery_TwoStates_RecoversMeansAndTransitions()
    {
        var rng = new Random(123);
        int T = 3000;
        int D = 9;
        double[] trueMean0 = new double[D];
        double[] trueMean1 = new double[D];
        trueMean1[0] = 3.0;
        double[] trueVar = Enumerable.Repeat(0.25, D).ToArray();
        double[,] trueA = { { 0.98, 0.02 }, { 0.05, 0.95 } };
        double[] truePi = { 0.5, 0.5 };

        var observations = new double[T][];
        var trueStates = new int[T];
        int state = 0;
        for (int t = 0; t < T; t++)
        {
            trueStates[t] = state;
            var mean = state == 0 ? trueMean0 : trueMean1;
            observations[t] = new double[D];
            for (int d = 0; d < D; d++)
                observations[t][d] = mean[d] + rng.NextGaussian() * Math.Sqrt(trueVar[d]);
            state = rng.NextDouble() < trueA[state, state] ? state : 1 - state;
        }

        var trainRng = new Random(99);
        var hmm = HiddenMarkovModel.Train(observations, 2, trainRng, restarts: 3, maxIter: 300);

        int match0 = hmm.Means[0][0] < 1.5 ? 0 : 1;
        int match1 = 1 - match0;

        Assert.True(Math.Abs(hmm.Means[match0][0] - trueMean0[0]) < 0.5,
            $"State0 mean[0] = {hmm.Means[match0][0]:F3}, expected ~{trueMean0[0]:F3}");
        Assert.True(Math.Abs(hmm.Means[match1][0] - trueMean1[0]) < 0.5,
            $"State1 mean[0] = {hmm.Means[match1][0]:F3}, expected ~{trueMean1[0]:F3}");

        Assert.True(Math.Abs(hmm.A[match0][match0] - trueA[0, 0]) < 0.1,
            $"A[0,0] = {hmm.A[match0][match0]:F3}, expected ~{trueA[0, 0]:F3}");
        Assert.True(Math.Abs(hmm.A[match1][match1] - trueA[1, 1]) < 0.1,
            $"A[1,1] = {hmm.A[match1][match1]:F3}, expected ~{trueA[1, 1]:F3}");
    }

    [Fact]
    public void ForwardFilter_RowsSumToOne_AllEntriesValid()
    {
        var rng = new Random(7);
        int T = 200, D = 4, N = 3;
        var obs = new double[T][];
        for (int t = 0; t < T; t++)
        {
            obs[t] = new double[D];
            for (int d = 0; d < D; d++) obs[t][d] = rng.NextGaussian();
        }

        var hmm = HiddenMarkovModel.Train(obs, N, new Random(11), restarts: 2, maxIter: 50);
        var probs = hmm.ForwardFilter(obs);

        Assert.Equal(T, probs.Length);
        for (int t = 0; t < T; t++)
        {
            Assert.Equal(N, probs[t].Length);
            double sum = 0;
            for (int i = 0; i < N; i++)
            {
                Assert.False(double.IsNaN(probs[t][i]));
                Assert.InRange(probs[t][i], -1e-9, 1.0 + 1e-9);
                sum += probs[t][i];
            }
            Assert.InRange(sum, 1.0 - 1e-9, 1.0 + 1e-9);
        }
    }

    [Fact]
    public void NoNaN_ConstantFeatureSeries()
    {
        int T = 100, D = 9;
        var obs = new double[T][];
        for (int t = 0; t < T; t++)
            obs[t] = new double[D];

        var hmm = HiddenMarkovModel.Train(obs, 2, new Random(5), restarts: 2, maxIter: 50);
        var probs = hmm.ForwardFilter(obs);

        for (int t = 0; t < T; t++)
            for (int i = 0; i < hmm.N; i++)
                Assert.False(double.IsNaN(probs[t][i]));
    }

    [Fact]
    public void NoNaN_ShortSeries()
    {
        var obs = new double[5][];
        for (int t = 0; t < 5; t++)
            obs[t] = [1.0, 2.0, 3.0];

        var hmm = HiddenMarkovModel.Train(obs, 2, new Random(3), restarts: 1, maxIter: 20);
        var probs = hmm.ForwardFilter(obs);

        Assert.Equal(5, probs.Length);
        for (int t = 0; t < 5; t++)
            for (int i = 0; i < hmm.N; i++)
                Assert.False(double.IsNaN(probs[t][i]));
    }

    [Fact]
    public void Determinism_SameSeed_IdenticalParameters()
    {
        var rng1 = new Random(42);
        var rng2 = new Random(42);
        int T = 500, D = 4;
        var obs = new double[T][];
        var r = new Random(1);
        for (int t = 0; t < T; t++)
        {
            obs[t] = new double[D];
            for (int d = 0; d < D; d++) obs[t][d] = r.NextGaussian();
        }

        var hmm1 = HiddenMarkovModel.Train(obs, 2, rng1, restarts: 2, maxIter: 50);
        var hmm2 = HiddenMarkovModel.Train(obs, 2, rng2, restarts: 2, maxIter: 50);

        Assert.Equal(hmm1.N, hmm2.N);
        for (int i = 0; i < hmm1.N; i++)
        {
            Assert.Equal(hmm1.Pi[i], hmm2.Pi[i], 12);
            for (int j = 0; j < hmm1.N; j++)
                Assert.Equal(hmm1.A[i][j], hmm2.A[i][j], 12);
            for (int d = 0; d < D; d++)
            {
                Assert.Equal(hmm1.Means[i][d], hmm2.Means[i][d], 12);
                Assert.Equal(hmm1.Vars[i][d], hmm2.Vars[i][d], 12);
            }
        }
    }

    [Fact]
    public void LabelStates_HighVol()
    {
        var means = new double[][]
        {
            [0.0, 0.0, 0.0, 0.0, 0.6, 0.5, 0.0, 0.0, 0.0],
        };
        var labels = HmmGenotype.LabelStates(means);
        Assert.Equal(MarketRegime.HighVol, labels[0]);
    }

    [Fact]
    public void LabelStates_Bull()
    {
        var means = new double[][]
        {
            [0.1, 0.1, 0.1, 0.005, 0.1, 0.5, 0.1, 0.05, 0.05],
        };
        var labels = HmmGenotype.LabelStates(means);
        Assert.Equal(MarketRegime.Bull, labels[0]);
    }

    [Fact]
    public void LabelStates_Bear()
    {
        var means = new double[][]
        {
            [-0.1, -0.1, -0.1, -0.005, 0.1, 0.5, -0.1, -0.05, -0.05],
        };
        var labels = HmmGenotype.LabelStates(means);
        Assert.Equal(MarketRegime.Bear, labels[0]);
    }

    [Fact]
    public void LabelStates_Ranging()
    {
        var means = new double[][]
        {
            [0.0, 0.0, 0.0, 0.0, 0.1, 0.2, 0.0, 0.0, 0.0],
        };
        var labels = HmmGenotype.LabelStates(means);
        Assert.Equal(MarketRegime.Ranging, labels[0]);
    }

    [Fact]
    public void HmmAnnotator_OutputLength_MatchesInput()
    {
        var candles = new Candle[300];
        var t = DateTime.UtcNow;
        for (int i = 0; i < 300; i++)
            candles[i] = new Candle(t.AddHours(i), 100, 101, 99, 100, 1_000_000);

        var geno = MakeDummyGenotype(2);
        var bars = HmmAnnotator.Annotate(candles, geno);
        Assert.Equal(300, bars.Length);
    }

    [Fact]
    public void HmmAnnotator_BarsBelowWarmup_HaveNullProbs()
    {
        int warmup = RegimeClassifier.Warmup;
        var candles = new Candle[warmup + 50];
        var t = DateTime.UtcNow;
        for (int i = 0; i < candles.Length; i++)
            candles[i] = new Candle(t.AddHours(i), 100 + i * 0.1, 101 + i * 0.1, 99 + i * 0.1, 100 + i * 0.1, 1_000_000);

        var geno = MakeDummyGenotype(2);
        var bars = HmmAnnotator.Annotate(candles, geno);

        for (int i = 0; i < warmup; i++)
            Assert.Null(bars[i].HmmProbs);

        for (int i = warmup; i < bars.Length; i++)
            Assert.NotNull(bars[i].HmmProbs);
    }

    [Fact]
    public void HmmAnnotator_TrendingSeries_ProducesSaneLabels()
    {
        int n = 500;
        var candles = new Candle[n];
        var t = DateTime.UtcNow;
        for (int i = 0; i < n; i++)
        {
            double price = 100 + i * 0.5;
            candles[i] = new Candle(t.AddHours(i), price, price + 1, price - 1, price, 1_000_000);
        }

        var geno = MakeDummyGenotype(3);
        var bars = HmmAnnotator.Annotate(candles, geno);

        bool hasNonRanging = false;
        for (int i = RegimeClassifier.Warmup; i < n; i++)
            if (bars[i].Regime != MarketRegime.Ranging) { hasNonRanging = true; break; }
        Assert.True(hasNonRanging);
    }

    static HmmGenotype MakeDummyGenotype(int states)
    {
        int D = 9;
        var pi = new double[states];
        for (int i = 0; i < states; i++) pi[i] = 1.0 / states;
        var A = new double[states][];
        for (int i = 0; i < states; i++)
        {
            A[i] = new double[states];
            for (int j = 0; j < states; j++) A[i][j] = (i == j ? 0.9 : 0.1 / (states - 1));
        }
        var means = new double[states][];
        var vars = new double[states][];
        for (int i = 0; i < states; i++)
        {
            means[i] = new double[D];
            vars[i] = Enumerable.Repeat(0.01, D).ToArray();
        }
        if (states >= 1) { means[0][3] = 0.005; means[0][1] = 0.05; means[0][6] = 0.05; }
        if (states >= 2) { means[1][3] = -0.005; means[1][1] = -0.05; means[1][6] = -0.05; }
        if (states >= 3) { means[2][4] = 0.6; }
        return new HmmGenotype
        {
            StatesN = states,
            Pi = pi,
            A = A,
            Means = means,
            Vars = vars,
            StateLabels = HmmGenotype.LabelStates(means),
            LogLikelihood = -100,
        };
    }
}
