using Iseberg.Core;
using Xunit;

namespace Devolutions.Terminal.UI.Tests;

public sealed class PortableIseEditorRuntimeFixture : IAsyncLifetime
{
    public PowerShellSession Analysis { get; } = new();

    public async ValueTask InitializeAsync()
    {
        await Analysis.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    public ValueTask DisposeAsync() => Analysis.DisposeAsync();
}
