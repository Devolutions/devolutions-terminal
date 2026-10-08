using Devolutions.Terminal.App.Connections;
using Xunit;

namespace Devolutions.Terminal.UI.Tests;

public sealed class PortableIseEditorRuntimeFixture : IAsyncLifetime
{
    public ValueTask InitializeAsync() =>
        new(PowerShellIseRuntime.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(60)));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
