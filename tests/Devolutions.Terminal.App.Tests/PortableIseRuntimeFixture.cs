using Devolutions.Terminal.App.Connections;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class PortableIseRuntimeFixture : IAsyncLifetime
{
    public ValueTask InitializeAsync() =>
        new(PowerShellIseRuntime.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(60)));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
