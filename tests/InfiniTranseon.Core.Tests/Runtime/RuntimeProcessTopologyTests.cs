using InfiniTranseon.Contracts.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class RuntimeProcessTopologyTests
{
    [Fact]
    public void NormalRuntimeAllowsOnlyAppAndEngineHost()
    {
        RuntimeProcessRole[] processes =
        [
            RuntimeProcessRole.App,
            RuntimeProcessRole.EngineHost,
        ];

        Assert.True(RuntimeProcessTopology.IsAllowedResidentSet(processes, localModelEnabled: false));
    }

    [Fact]
    public void ModelWorkerRequiresAnEnabledLocalModel()
    {
        RuntimeProcessRole[] processes =
        [
            RuntimeProcessRole.App,
            RuntimeProcessRole.EngineHost,
            RuntimeProcessRole.ModelWorker,
        ];

        Assert.False(RuntimeProcessTopology.IsAllowedResidentSet(processes, localModelEnabled: false));
        Assert.True(RuntimeProcessTopology.IsAllowedResidentSet(processes, localModelEnabled: true));
    }

    [Theory]
    [InlineData(RuntimeProcessRole.ProviderWorker)]
    [InlineData(RuntimeProcessRole.UpdaterWorker)]
    public void ExtraResidentWorkersAreRejected(RuntimeProcessRole extraProcess)
    {
        RuntimeProcessRole[] processes =
        [
            RuntimeProcessRole.App,
            RuntimeProcessRole.EngineHost,
            extraProcess,
        ];

        Assert.False(RuntimeProcessTopology.IsAllowedResidentSet(processes, localModelEnabled: true));
    }
}
