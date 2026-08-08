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
}
