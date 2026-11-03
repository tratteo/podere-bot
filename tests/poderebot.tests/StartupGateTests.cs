using PodereBot.Services;
using Xunit;

namespace PodereBot.Tests;

public sealed class StartupGateTests
{
    [Fact]
    public void MarkReadyRaisesTheFlag()
    {
        var gate = new StartupGate();

        Assert.False(gate.Ready);
        gate.MarkReady();
        Assert.True(gate.Ready);
    }
}
