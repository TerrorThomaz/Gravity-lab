namespace GravityGen2.Strategies.AccumulationGrid;

public sealed record AccumulationGridGenotype
{
    public int EmaPeriod { get; init; } = 50;
    public double GridStepAtrMult { get; init; } = 1.5;
    public int MaxLevels { get; init; } = 5;
    public double TakeProfitAtrMult { get; init; } = 3.0;
    public double StopLossAtrMult { get; init; } = 2.0;
    public int MaxHoldBars { get; init; } = 100;
    public double PositionSizePct { get; init; } = 0.05;
    public int RegimeSustainBars { get; init; } = 24;
}
