using TradingGA;
using Bybit.Net.Clients;

var client = new BybitRestClient();

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "";
switch (mode)
{
    case "train":            await TrainCommands.RunFadeShortTrain(client, args);                      break;
    case "backtest":         await BacktestCommands.RunBacktest(client);                               break;
    case "papertrade":       await PapertradeCommands.RunPaperTrade(client);                           break;
    case "gridtrain":        await GridCommands.RunGridTrain(client, args);                            break;
    case "gridbacktest":     await GridCommands.RunGridBacktest(client);                               break;
    case "combinedbacktest": await CombinedBacktest.RunCombinedBacktest(client);                       break;
    case "rankedbacktest":   await GridCommands.RunRankedBacktest(client);                             break;
    case "test":             await BacktestCommands.RunTest(client);                                   break;
    case "yearlybreakdown":  await BacktestCommands.RunYearlyBreakdown(client);                        break;
    case "fadelongtrain":    await LongTrainCommands.RunFadeLongTrain(client, args);                   break;
    case "diplongtrain":     await LongTrainCommands.RunDipLongTrain(client, args);                    break;
    case "swinglongtrain":   await LongTrainCommands.RunSwingLongTrain(client, args);                  break;
    case "routertrain":      await LongTrainCommands.RunRegimeRouterTrain(client, args);               break;
    case "coevolvetrain":    await LongTrainCommands.RunCoevolve(client, args);                        break;
    case "retrain":          await TrainCommands.RunFadeShortTrain(client, args, invertScreen: true);  break;
    case "oosbacktest":      await OosBacktest.RunOosBacktest(client);                           break;
    case "allcoinsbacktest": await OosBacktest.RunAllCoinsBacktest(client);                      break;
    case "fulltest":         await FullTest.RunFullTest(client);                                break;
    case "dynamicguardtrain":   await DynamicGuardTrainCommands.RunDynamicGuardTrain(client);  break;
    default:
        Console.WriteLine("Gravity-gen2 — usage:");
        Console.WriteLine("  dotnet run -- train              Train FadeShort GA (~10 min)");
        Console.WriteLine("  dotnet run -- backtest           FadeShort + grid backtest: 93 coins, val 20%");
        Console.WriteLine("  dotnet run -- papertrade         Live signals, refreshes every 4h");
        Console.WriteLine("  dotnet run -- gridtrain          Grid GA: ranging-market long grid");
        Console.WriteLine("  dotnet run -- gridbacktest       Grid backtest: 93 coins, val 20%");
        Console.WriteLine("  dotnet run -- combinedbacktest   FadeShort + grid, shared capital");
        Console.WriteLine("  dotnet run -- rankedbacktest     Ranked portfolio: top-N signals by quality");
        Console.WriteLine("  dotnet run -- test               Statistical edge validation");
        Console.WriteLine("  dotnet run -- yearlybreakdown    Per-year portfolio returns (full history)");
        Console.WriteLine("  dotnet run -- fadelongtrain      FadeLong GA: oversold bounce, 53 coins, ~3yr");
        Console.WriteLine("  dotnet run -- diplongtrain       DipLong GA: bull pullback, regime-gated, 53 coins, ~3yr");
        Console.WriteLine("  dotnet run -- swingLongtrain      SwingLong GA: bull divergence+BoS long, 93 coins, ~3yr");
        Console.WriteLine("  dotnet run -- routertrain        RegimeRouter GA: train routing thresholds + duration gates");
        Console.WriteLine("  dotnet run -- coevolvetrain      Red-Queen coevolve: Router (profit) ↔ Guard (risk), 8 rounds parallel — strategies frozen");
        Console.WriteLine("  dotnet run -- retrain            Retrain FadeShort on unknown coins (inverted screen, fixes overfit)");
        Console.WriteLine("  dotnet run -- oosbacktest        OOS backtest: 28 never-seen coins, full history, all strategies");
        Console.WriteLine("  dotnet run -- allcoinsbacktest   Portfolio sim: BacktestCoins (val 20%) + OOS coins (full history), concurrency analysis");
        Console.WriteLine("  dotnet run -- fulltest           Condensed master report: val+OOS, 7 sections, single candle fetch");
        Console.WriteLine("  dotnet run -- dynamicguardtrain  Train BTC 4H ATR/momentum dynamic guard (proactive, corrects router)");
        break;
}
