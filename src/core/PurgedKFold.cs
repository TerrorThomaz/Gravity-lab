namespace TradingGA;

// Purged K-fold with embargo (Lopez de Prado, AFML ch. 7).
// Purge: drop training trades whose holding period overlaps the test window.
// Embargo: also drop training trades starting shortly after the test window (serial correlation).
// Used for reporting only (gap between purged held-out score and in-sample score = overfit estimate).
// FoldScoreHelper's folds are NOT cross-validation — all k folds feed GA selection.
public static class PurgedKFold
{
    public readonly record struct Split(
        int TestStart, int TestEnd,          // half-open bar range of the held-out fold
        int EmbargoEnd);                     // test trades AND the embargo tail end here

    // Bar ranges for fold `testFold` of `k`. embargoPct is fraction of total length (matches FitnessConfig units).
    // Embargo extends past test window only; purging handles the leading edge.
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

    // Is a training trade admissible? Rejects if holding period overlaps test window (purge)
    // or starts in embargo tail after test window.
    public static bool IsTrainAdmissible(int entryBar, int exitBar, in Split s)
    {
        // Purge: any overlap with test window disqualifies.
        bool overlapsTest = entryBar < s.TestEnd && exitBar >= s.TestStart;
        if (overlapsTest) return false;

        // Embargo: starts in tail after test window.
        if (entryBar >= s.TestEnd && entryBar < s.EmbargoEnd) return false;

        return true;
    }

    public static bool IsTestTrade(int entryBar, in Split s)
        => entryBar >= s.TestStart && entryBar < s.TestEnd;

    // Purge report: if purging drops a large share, holds are long relative to fold length (contamination was high).
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
