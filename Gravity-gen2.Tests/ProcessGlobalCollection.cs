using Xunit;

namespace Gravity_gen2.Tests;

// SERIALISES TESTS THAT MUTATE PROCESS-GLOBAL STATE.
//
// The suite was flaky: a different test failed on each full run and every one of them passed in
// isolation. Two shared resources, both process-wide, both mutated by tests that xUnit was free to
// run concurrently because they live in different collections.
//
// 1. Console.Out, via the `Capture` helpers in GuardedPortfolioTests, FundingSessionSetTests and
//    GaReproducibilityTests. Each does save / SetOut / restore, which interleaves badly:
//
//        A: prev = realConsole      A: SetOut(swA)
//        B: prev = swA              B: SetOut(swB)     <- B saves A's writer as "previous"
//        A: SetOut(realConsole)                        <- A restores, clobbering B mid-test
//        B: asserts on swB -> empty -> FAIL
//
//    The failure lands on whichever test was unlucky, which is why it looked random.
//
// 2. Environment variables, via RegimeRouterHmmTests setting GRAVITY_HMM=0. RegimeRouter.HmmEnabled
//    reads it live, so while that is set ANY concurrently running test that expects HMM routing
//    silently takes the legacy threshold path. Wider blast radius than the console races, and it
//    explains failures in classes that never touch Console at all.
//
// DisableParallelization stops this collection running alongside OTHER collections too, not just
// against itself. That is the property actually needed: for the console races serialising the
// capturers against each other would be enough, but an env-var mutation has to be isolated from
// every reader, and the readers are not enumerable — RegimeRouter.HmmEnabled is consulted deep
// inside routing, so any test that routes is a potential victim.
//
// Cost: these classes run serially. Cheap — they are a handful of fast tests.
//
// If you add a test that calls Console.SetOut, Environment.SetEnvironmentVariable, or mutates any
// other static, put its class in this collection. The alternative is a suite whose red results
// cannot be trusted, which is worse than a slightly slower one.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessGlobalCollection
{
    public const string Name = "process-global state";
}
