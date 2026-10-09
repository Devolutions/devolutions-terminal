using Devolutions.Terminal.App.Connections;
using Iseberg.Core;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class PortableIseRuntimeFixture : IAsyncLifetime
{
    public PowerShellSession Analysis { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await PowerShellIseRuntime.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(60));
        await Analysis.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    public ValueTask DisposeAsync() => Analysis.DisposeAsync();
}
