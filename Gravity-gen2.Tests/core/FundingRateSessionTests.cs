using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// Regression suite for the funding sign rule.
//
// History: FundingPnl existed as six byte-identical private copies across the strategy
// simulators, every one of them accumulating `pnl += rate` regardless of direction. That is
// correct for a short and INVERTED for a long, so DipLong / FadeLong / SwingLong were booking
// funding income for a cost they were paying. These tests pin the rule down:
//
//     POSITIVE rate  → longs PAY, shorts RECEIVE
//     NEGATIVE rate  → longs RECEIVE, shorts PAY
//
// and pin the discrete (never prorated) settlement accounting alongside it.
public class FundingRateSessionTests
{
    private static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0)
        => new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    // A session holding a single constant rate for the whole test window.
    private static FundingRateSession FlatSession(double ratePerInterval)
    {
        var bars = new List<FundingBar>();
        var t = Utc(2023, 12, 1);
        for (int i = 0; i < 400; i++, t = t.AddHours(8))
            bars.Add(new FundingBar(t, ratePerInterval));
        return new FundingRateSession(bars.ToArray());
    }

    // ── Sign rule: positive rate ──────────────────────────────────────────────────────────

    [Fact]
    public void PositiveRate_LongPays()
    {
        var s = FlatSession(0.0005);                       // +0.05% per 8h
        // 00:00 → 24:00 spans the 08:00, 16:00 and 24:00 settlements = 3 ticks.
        double pnl = FundingRateSession.PnlPct(Utc(2024, 3, 1), Utc(2024, 3, 2), s, isLong: true);
        Assert.True(pnl < 0, $"A long must PAY a positive funding rate, got {pnl}");
        Assert.Equal(-0.05 * 3, pnl, 10);                  // percentage points
    }

    [Fact]
    public void PositiveRate_ShortReceives()
    {
        var s = FlatSession(0.0005);
        double pnl = FundingRateSession.PnlPct(Utc(2024, 3, 1), Utc(2024, 3, 2), s, isLong: false);
        Assert.True(pnl > 0, $"A short must RECEIVE a positive funding rate, got {pnl}");
        Assert.Equal(+0.05 * 3, pnl, 10);
    }

    // ── Sign rule: negative rate flips both sides ────────────────────────────────────────

    [Fact]
    public void NegativeRate_LongReceives()
    {
        var s = FlatSession(-0.0005);                      // -0.05% per 8h
        double pnl = FundingRateSession.PnlPct(Utc(2024, 3, 1), Utc(2024, 3, 2), s, isLong: true);
        Assert.True(pnl > 0, $"A long must RECEIVE a negative funding rate, got {pnl}");
        Assert.Equal(+0.05 * 3, pnl, 10);
    }

    [Fact]
    public void NegativeRate_ShortPays()
    {
        var s = FlatSession(-0.0005);
        double pnl = FundingRateSession.PnlPct(Utc(2024, 3, 1), Utc(2024, 3, 2), s, isLong: false);
        Assert.True(pnl < 0, $"A short must PAY a negative funding rate, got {pnl}");
        Assert.Equal(-0.05 * 3, pnl, 10);
    }

    [Fact]
    public void LongAndShort_AreExactMirrorImages()
    {
        var s = FlatSession(0.0003);
        var entry = Utc(2024, 5, 10, 3);
        var exit  = Utc(2024, 5, 14, 11);
        double lng = FundingRateSession.PnlPct(entry, exit, s, isLong: true);
        double sht = FundingRateSession.PnlPct(entry, exit, s, isLong: false);
        Assert.Equal(-lng, sht, 10);
        Assert.NotEqual(0.0, sht);
    }

    // ── Discrete settlement accounting ───────────────────────────────────────────────────

    [Fact]
    public void StraddlingExactlyOneSettlement_ChargesExactlyOneInterval()
    {
        var s = FlatSession(0.0005);
        // 07:00 → 09:00 crosses the 08:00 tick only.
        double pnl = FundingRateSession.PnlPct(Utc(2024, 3, 1, 7), Utc(2024, 3, 1, 9), s, isLong: true);
        Assert.Equal(-0.05, pnl, 10);
        Assert.Equal(1, FundingRateSession.CountSettlements(Utc(2024, 3, 1, 7), Utc(2024, 3, 1, 9)));
    }

    [Fact]
    public void StraddlingNoSettlement_ChargesZero()
    {
        var s = FlatSession(0.0005);
        // 09:00 → 15:00 sits entirely between the 08:00 and 16:00 ticks.
        Assert.Equal(0.0, FundingRateSession.PnlPct(Utc(2024, 3, 1, 9), Utc(2024, 3, 1, 15), s, isLong: true),  10);
        Assert.Equal(0.0, FundingRateSession.PnlPct(Utc(2024, 3, 1, 9), Utc(2024, 3, 1, 15), s, isLong: false), 10);
        Assert.Equal(0, FundingRateSession.CountSettlements(Utc(2024, 3, 1, 9), Utc(2024, 3, 1, 15)));
    }

    [Fact]
    public void ChargeIsDiscrete_NotProrated()
    {
        var s = FlatSession(0.0005);
        // Both holds cross exactly one tick (08:00) despite very different durations:
        // a 2-minute straddle and a near-8h one must cost the same.
        double brief = FundingRateSession.PnlPct(Utc(2024, 3, 1, 7, 59), Utc(2024, 3, 1, 8, 1), s, isLong: true);
        double long_ = FundingRateSession.PnlPct(Utc(2024, 3, 1, 4, 0),  Utc(2024, 3, 1, 11, 0), s, isLong: true);
        Assert.Equal(brief, long_, 10);
        Assert.Equal(-0.05, brief, 10);
    }

    [Fact]
    public void EntryExactlyOnSettlement_IsNotChargedForThatTick()
    {
        // Opened at the 08:00 snapshot → not holding through it; next charge is 16:00.
        Assert.Equal(0, FundingRateSession.CountSettlements(Utc(2024, 3, 1, 8), Utc(2024, 3, 1, 15, 59)));
        Assert.Equal(1, FundingRateSession.CountSettlements(Utc(2024, 3, 1, 8), Utc(2024, 3, 1, 16)));
    }

    [Fact]
    public void NonPositiveDuration_IsZero()
    {
        var s = FlatSession(0.0005);
        var t = Utc(2024, 3, 1, 12);
        Assert.Equal(0.0, FundingRateSession.PnlPct(t, t, s, isLong: true),  10);
        Assert.Equal(0.0, FundingRateSession.PnlPct(t, t.AddHours(-9), s, isLong: false), 10);
        Assert.Equal(0.0, FundingRateSession.PnlPct(t, t, null, isLong: true), 10);
    }

    // ── Fallback branch (no rate series) ─────────────────────────────────────────────────

    [Fact]
    public void Fallback_IsDirectionAware()
    {
        var entry = Utc(2024, 3, 1);
        var exit  = Utc(2024, 3, 2);
        double lng = FundingRateSession.PnlPct(entry, exit, null, isLong: true);
        double sht = FundingRateSession.PnlPct(entry, exit, null, isLong: false);
        // The fallback is deliberately symmetric and pessimistic: it charges BOTH directions the
        // interest floor. It is the real-rate path that is direction-aware. A short's modal case
        // is a credit, but shorts genuinely pay in crowded-short bear regimes — which is exactly
        // where RipShort and FadeShort operate, and both train with funding: null — so booking
        // zero there would improve short selection on an assumption we cannot support.
        Assert.Equal(lng, sht, 10);
        Assert.True(lng < 0, $"Fallback must charge a long for holding, got {lng}");
        Assert.Equal(-FundingRateSession.FallbackIntervalPct * 3, lng, 10);
    }

    [Fact]
    public void Fallback_NeverCreditsALong()
    {
        // The whole point of the fix: no hold length may turn the long fallback into a profit.
        var entry = Utc(2024, 3, 1, 1);
        for (int hours = 1; hours <= 240; hours++)
        {
            double pnl = FundingRateSession.PnlPct(entry, entry.AddHours(hours), null, isLong: true);
            Assert.True(pnl <= 0, $"Long fallback credited {pnl} for a {hours}h hold");
        }
    }

    [Fact]
    public void Fallback_LongCostGrowsWithSettlementsCrossed()
    {
        var entry = Utc(2024, 3, 1);
        double oneDay  = FundingRateSession.PnlPct(entry, entry.AddDays(1), null, isLong: true);
        double fiveDay = FundingRateSession.PnlPct(entry, entry.AddDays(5), null, isLong: true);
        Assert.True(fiveDay < oneDay, $"Longer long hold must cost more: {fiveDay} vs {oneDay}");
        Assert.Equal(-FundingRateSession.FallbackIntervalPct * 15, fiveDay, 10);
    }

    [Fact]
    public void Fallback_ShortIsNeverCredited()
    {
        // Modal case is a credit, but the sign is genuinely uncertain — shorts pay in crowded-short
        // bear regimes — so the fallback charges the floor rather than booking unprovable income.
        // No hold length may turn the short fallback into a profit.
        var entry = Utc(2024, 3, 1, 1);
        foreach (int hours in new[] { 1, 7, 8, 24, 100, 500 })
        {
            double pnl = FundingRateSession.PnlPct(entry, entry.AddHours(hours), null, isLong: false);
            Assert.True(pnl <= 0, $"Short fallback credited {pnl} for a {hours}h hold");
            int intervals = FundingRateSession.CountSettlements(entry, entry.AddHours(hours));
            Assert.Equal(-FundingRateSession.FallbackIntervalPct * intervals, pnl, 10);
        }
    }

    [Fact]
    public void Fallback_IsDiscrete_NotProrated()
    {
        // Crosses no settlement → free, regardless of the hours elapsed.
        Assert.Equal(0.0, FundingRateSession.PnlPct(Utc(2024, 3, 1, 9), Utc(2024, 3, 1, 15), null, isLong: true), 10);
        // Crosses exactly one → exactly one interval.
        Assert.Equal(-FundingRateSession.FallbackIntervalPct,
                     FundingRateSession.PnlPct(Utc(2024, 3, 1, 7), Utc(2024, 3, 1, 9), null, isLong: true), 10);
    }

    // ── Rate lookup uses the rate in force AT each settlement ────────────────────────────

    [Fact]
    public void UsesRateInForceAtEachSettlement()
    {
        var s = new FundingRateSession(new[]
        {
            new FundingBar(Utc(2024, 3, 1,  0), 0.0001),
            new FundingBar(Utc(2024, 3, 1,  8), 0.0002),
            new FundingBar(Utc(2024, 3, 1, 16), 0.0004),
        });
        // 01:00 → 17:00 hits the 08:00 and 16:00 ticks: 0.02% + 0.04% = 0.06% paid by a long.
        double pnl = FundingRateSession.PnlPct(Utc(2024, 3, 1, 1), Utc(2024, 3, 1, 17), s, isLong: true);
        Assert.Equal(-(0.02 + 0.04), pnl, 10);
    }

    [Fact]
    public void RealRateAndFallback_ChargeTheSameNumberOfTicks()
    {
        // Both branches must agree on "how many times charged" for every hold length, so the
        // fallback is a pure rate substitution and never a different accounting rule.
        var s = FlatSession(0.0001);                       // +0.01% per 8h == FallbackIntervalPct
        var entry = Utc(2024, 3, 1, 3);
        for (int hours = 1; hours <= 200; hours++)
        {
            var exit = entry.AddHours(hours);
            int n = FundingRateSession.CountSettlements(entry, exit);
            Assert.Equal(+n * FundingRateSession.FallbackIntervalPct,
                         FundingRateSession.PnlPct(entry, exit, s,    isLong: false), 8);
            Assert.Equal(-n * FundingRateSession.FallbackIntervalPct,
                         FundingRateSession.PnlPct(entry, exit, null, isLong: false), 8);
        }
    }

    // ── Series coverage: the two ends are handled differently on purpose ──────────────────
    //
    // GetRate used to flat-extrapolate BOTH ends, so a settlement predating the funding history
    // was billed the FIRST known print's rate. A trade a year before the series start was
    // charged that fabricated constant at every tick it crossed (-0.30 for a 24h long against a
    // +0.10%/8h first print). The session is built from whatever the funding cache holds and all
    // six strategies price funding through it, so a short cache silently painted a synthetic
    // rate over the leading window of every backtest.
    //
    // CHOSEN BEHAVIOUR before the series start: charge the interest-rate floor, exactly as the
    // `funding == null` branch does — NOT 0.0. A real position did pay something at that
    // settlement; we just don't know how much, and booking zero is booking unprovable funding
    // income relative to the floor. It also makes the cost model coverage-independent: see
    // PreSeriesWindow_PricesIdenticallyToHavingNoSeriesAtAll below.
    //
    // CHOSEN BEHAVIOUR after the series end: keep flat-extrapolating the last known rate.
    // Funding is strongly autocorrelated at the 8h scale and the trailing gap is structurally
    // small (the cache ends at the end of the backtest window, or at "now" in papertrade), so an
    // extrapolated tick sits adjacent to real data rather than an unbounded distance from it.

    [Fact]
    public void SettlementsBeforeSeriesStart_ChargeTheFloor_NotTheFirstKnownRate()
    {
        var s = new FundingRateSession([new FundingBar(Utc(2025, 1, 1), 0.0010)]);   // +0.10%/8h
        // Trade in 2024 — a year before any known funding print. 24h spans 3 settlements.
        double lng = FundingRateSession.PnlPct(Utc(2024, 3, 1), Utc(2024, 3, 2), s, isLong: true);
        double sht = FundingRateSession.PnlPct(Utc(2024, 3, 1), Utc(2024, 3, 2), s, isLong: false);

        Assert.Equal(-FundingRateSession.FallbackIntervalPct * 3, lng, 10);
        Assert.Equal(-FundingRateSession.FallbackIntervalPct * 3, sht, 10);
        // The old back-extrapolated figure, pinned so a regression is unambiguous.
        Assert.NotEqual(-0.30, lng, 10);
    }

    [Fact]
    public void PreSeriesWindow_PricesIdenticallyToHavingNoSeriesAtAll()
    {
        // Coverage independence: partially populating the funding cache must never make a
        // strategy look cheaper (or dearer) to hold than leaving the cache empty.
        var s = new FundingRateSession([new FundingBar(Utc(2025, 1, 1), 0.0010)]);
        var entry = Utc(2024, 3, 1, 1);
        foreach (bool isLong in new[] { true, false })
            for (int hours = 1; hours <= 120; hours++)
            {
                var exit = entry.AddHours(hours);
                Assert.Equal(FundingRateSession.PnlPct(entry, exit, null, isLong),
                             FundingRateSession.PnlPct(entry, exit, s,    isLong), 10);
            }
    }

    [Fact]
    public void EmptySeries_PricesIdenticallyToHavingNoSeriesAtAll()
    {
        var empty = new FundingRateSession(Array.Empty<FundingBar>());
        var entry = Utc(2024, 3, 1);
        var exit  = Utc(2024, 3, 2);
        Assert.Equal(FundingRateSession.PnlPct(entry, exit, null,  isLong: true),
                     FundingRateSession.PnlPct(entry, exit, empty, isLong: true), 10);
        Assert.Equal(-FundingRateSession.FallbackIntervalPct * 3,
                     FundingRateSession.PnlPct(entry, exit, empty, isLong: true), 10);
    }

    [Fact]
    public void WindowStraddlingSeriesStart_ChargesFloorBeforeAndRealRatesAfter()
    {
        var s = new FundingRateSession(new[]
        {
            new FundingBar(Utc(2024, 3, 1,  8), 0.0005),
            new FundingBar(Utc(2024, 3, 1, 16), 0.0005),
        });
        // 2024-02-29 23:00 → 2024-03-01 17:00 crosses 00:00 (pre-series), 08:00 and 16:00.
        double lng = FundingRateSession.PnlPct(Utc(2024, 2, 29, 23), Utc(2024, 3, 1, 17), s, isLong: true);
        Assert.Equal(-FundingRateSession.FallbackIntervalPct - 0.05 - 0.05, lng, 10);
    }

    [Fact]
    public void SettlementsAfterSeriesEnd_FlatExtrapolateTheLastKnownRate()
    {
        var s = new FundingRateSession([new FundingBar(Utc(2024, 3, 1, 0), 0.0005)]);   // +0.05%/8h
        // Trade months after the last print: still priced at the last known rate, deliberately.
        double lng = FundingRateSession.PnlPct(Utc(2024, 6, 1), Utc(2024, 6, 2), s, isLong: true);
        Assert.Equal(-0.05 * 3, lng, 10);
    }

    [Fact]
    public void GetRate_IsZeroBeforeTheSeriesAndCrowdingGatesReadNeutral()
    {
        // A hard binary entry gate must not fire on an invented rate: "unknown" is "not crowded".
        var s = new FundingRateSession([new FundingBar(Utc(2025, 1, 1), 0.0010)]);   // crowded long
        Assert.Equal(0.0, s.GetRate(Utc(2024, 1, 1)), 12);
        Assert.False(s.TryGetRate(Utc(2024, 1, 1), out _));
        Assert.False(s.CoversTime(Utc(2024, 1, 1)));
        Assert.False(s.IsCrowdedLong(Utc(2024, 1, 1)));
        Assert.False(s.IsCrowdedShort(Utc(2024, 1, 1)));

        // The print instant itself IS covered — the boundary is "strictly before".
        Assert.True(s.CoversTime(Utc(2025, 1, 1)));
        Assert.True(s.TryGetRate(Utc(2025, 1, 1), out double r));
        Assert.Equal(0.0010, r, 12);
        Assert.True(s.IsCrowdedLong(Utc(2025, 1, 1)));
    }

    // ── DateTime overflow safety ──────────────────────────────────────────────────────────

    [Fact]
    public void PnlPct_DoesNotThrowOnExtremeExitTime()
    {
        // When exitTime is near DateTime.MaxValue, the settlement loop must guard the increment
        // before re-testing the bound, otherwise AddHours throws ArgumentOutOfRangeException.
        var s = new FundingRateSession(new[]
        {
            new FundingBar(Utc(2024, 3, 1, 0), 0.0001),
        });
        var ex = Record.Exception(() =>
            FundingRateSession.PnlPct(Utc(2024, 3, 1), DateTime.MaxValue, s, isLong: true));
        Assert.Null(ex);
    }

    [Fact]
    public void CountSettlements_DoesNotThrowOnExtremeExitTime()
    {
        // When exitTime is near DateTime.MaxValue, the settlement loop must guard the increment
        // before re-testing the bound, otherwise AddHours throws ArgumentOutOfRangeException.
        var ex = Record.Exception(() =>
            FundingRateSession.CountSettlements(Utc(2024, 3, 1), DateTime.MaxValue));
        Assert.Null(ex);
    }
}
