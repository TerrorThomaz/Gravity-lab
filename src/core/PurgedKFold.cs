namespace TradingGA;

// Purged K-fold with embargo (Lopez de Prado, Advances in Financial Machine Learning ch. 7).
//
// THE PROBLEM THIS FIXES
// FoldScoreHelper's existing scheme is NOT cross-validation and CLAUDE.md says so plainly: k folds
// are cut per coin, ALL k are scored, and every one feeds the fitness the GA selects on. There is
// no held-out test fold, so the process produces no out-of-sample estimate at all. It is a
// dispersion penalty across time slices — useful, but it cannot answer "does this genotype
// generalise", and the GA is free to fit all k folds simultaneously.
//
// FitnessConfig.EmbargoPct trims the leading 5% of each fold, which sounds like the right idea but
// separates two windows that are BOTH in-sample. It prevents no leakage into any held-out set,
// because there isn't one.
//
// WHAT PURGING AND EMBARGO ACTUALLY DO
// A trade is not a point observation: it spans [entry, exit]. If a training trade is still open
// when the test window begins, its outcome is partly determined by test-window prices — the label
// leaks. Two corrections, and they are different:
//
//   PURGE   — drop training trades whose HOLDING PERIOD overlaps the test window. Removes leakage
//             backwards and forwards through overlapping labels.
//   EMBARGO — additionally drop training trades starting shortly AFTER the test window ends.
//             Serial correlation means bars just after the test window still carry information
//             about it; purging alone does not remove that.
//
// Without purging, a strategy with MaxHoldCandles = 120 has trades spanning five days, so a naive
// fold boundary leaks up to five days of test information into training. This repo's holds run to
// 250 h1 bars, so the leak is over ten days.
//
// HOW THIS IS USED HERE
// The GA does not "fit" in the supervised sense — it scores a genotype. So the analogue is: score
// the genotype on k-1 folds (selection), and score it SEPARATELY on the held-out fold with the
// training trades purged and embargoed (reporting). The gap between the two is the overfit
// estimate that this repo currently gets only from oosbacktest on never-trained coins.
//
// Reporting first, selection later. Making the held-out fold drive selection would change every
// genotype's objective at once; measuring the gap first tells us whether that is worth doing.
public static class PurgedKFold
{
    public readonly record struct Split(
        int TestStart, int TestEnd,          // half-open bar range of the held-out fold
        int EmbargoEnd);                     // test trades AND the embargo tail end here

    // Bar ranges for fold `testFold` of `k`, over an array of `length` bars.
    //
    // embargoPct is a fraction of TOTAL length, matching FitnessConfig.EmbargoPct's units so the
    // two cannot be confused. The embargo extends past the test window only — purging handles the
    // leading edge, because a trade that starts before the test window and runs into it is caught
    // by the overlap test, not by a fixed offset.
    public static Split MakeSplit(int length, int k, int testFold, double embargoPct)
    {
        if (k < 2) throw new ArgumentOutOfRangeException(nameof(k), k, "purged k-fold needs k >= 2");
        if ((uint)testFold >= (uint)k) throw new ArgumentOutOfRangeException(nameof(testFold));

        int foldLen  = length / k;
        int start    = testFold * foldLen;
        int end      = testFold == k - 1 ? length : start + foldLen;   // last fold absorbs remainder
        int embargo  = (int)Math.Round(length * Math.Max(0.0, embargoPct));
        return new Split(start, end, Math.Min(length, end + embargo));
    }

    // Is a training trade admissible given the held-out window?
    //
    // Rejects on two grounds:
    //   · its holding period OVERLAPS the test window at all (purge), or
    //   · it STARTS inside the embargo tail immediately after the test window.
    //
    // Indices are bar positions in the same array MakeSplit was given.
    public static bool IsTrainAdmissible(int entryBar, int exitBar, in Split s)
    {
        // Purge: any overlap with [TestStart, TestEnd) disqualifies, in either direction.
        bool overlapsTest = entryBar < s.TestEnd && exitBar >= s.TestStart;
        if (overlapsTest) return false;

        // Embargo: starts in the tail just after the test window.
        if (entryBar >= s.TestEnd && entryBar < s.EmbargoEnd) return false;

        return true;
    }

    public static bool IsTestTrade(int entryBar, in Split s)
        => entryBar >= s.TestStart && entryBar < s.TestEnd;

    // How much of the training set purging actually removes. Worth reporting: if purging drops a
    // large share, the strategy's holds are long relative to the fold length and the ORIGINAL
    // unpurged number was badly contaminated.
    public record PurgeReport(int Total, int Kept, int PurgedOverlap, int PurgedEmbargo, int Test)
    {
        public double PurgedPct => Total > 0 ? (double)(PurgedOverlap + PurgedEmbargo) / Total * 100.0 : 0.0;
    }

    public static PurgeReport Partition(
        IReadOnlyList<(int EntryBar, int ExitBar)> trades, in Split s,
        List<int>? trainIdx = null, List<int>? testIdx = null)
    {
        int overlap = 0, embargoed = 0, kept = 0, test = 0;
        for (int i = 0; i < trades.Count; i++)
        {
            var (e, x) = trades[i];
            if (IsTestTrade(e, s)) { test++; testIdx?.Add(i); continue; }

            if (e < s.TestEnd && x >= s.TestStart)      { overlap++;   continue; }
            if (e >= s.TestEnd && e < s.EmbargoEnd)      { embargoed++; continue; }

            kept++; trainIdx?.Add(i);
        }
        return new PurgeReport(trades.Count, kept, overlap, embargoed, test);
    }
}
