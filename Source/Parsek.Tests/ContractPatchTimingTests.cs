using Xunit;

namespace Parsek.Tests
{
    /// <summary>
    /// CONTRACT-RESTORED-AT-SCENE-LOAD-PAYS-TWICE: the contract patch must not restore into
    /// stock's list before stock's ContractSystem has loaded the save, because stock's load
    /// routine clears that list without unregistering what Parsek put there, leaving a
    /// GameEvents-subscribed copy that completes and pays a second (third) time.
    /// </summary>
    public class ContractPatchTimingTests
    {
        [Fact]
        public void NoContractSystem_Skips()
        {
            Assert.Equal(KspStatePatcher.ContractPatchTiming.Skip,
                KspStatePatcher.DecideContractPatchTiming(
                    contractSystemPresent: false, stockContractsLoaded: false));
            Assert.Equal(KspStatePatcher.ContractPatchTiming.Skip,
                KspStatePatcher.DecideContractPatchTiming(
                    contractSystemPresent: false, stockContractsLoaded: true));
        }

        [Fact]
        public void SceneLoadBeforeStockLoaded_Defers()
        {
            // ParsekScenario.OnLoad runs before stock's one-frame-deferred OnLoadRoutine.
            Assert.Equal(KspStatePatcher.ContractPatchTiming.DeferUntilStockLoaded,
                KspStatePatcher.DecideContractPatchTiming(
                    contractSystemPresent: true, stockContractsLoaded: false));
        }

        [Fact]
        public void InSessionRecalcAfterStockLoaded_RunsNow()
        {
            Assert.Equal(KspStatePatcher.ContractPatchTiming.RunNow,
                KspStatePatcher.DecideContractPatchTiming(
                    contractSystemPresent: true, stockContractsLoaded: true));
        }
    }
}
