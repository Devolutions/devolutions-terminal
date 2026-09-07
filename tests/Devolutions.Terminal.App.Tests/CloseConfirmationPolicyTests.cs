using Devolutions.Terminal.App.Actions;
using Devolutions.Terminal.Settings;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class CloseConfirmationPolicyTests
{
    [Theory]
    [InlineData(ConfirmOnClose.Never, 5, false)]
    [InlineData(ConfirmOnClose.Automatic, 0, false)]
    [InlineData(ConfirmOnClose.Automatic, 1, false)]
    [InlineData(ConfirmOnClose.Automatic, 2, true)]
    [InlineData(ConfirmOnClose.Always, 0, false)]
    [InlineData(ConfirmOnClose.Always, 1, true)]
    public void PolicyCountsOnlyRunningSessions(ConfirmOnClose policy, int running, bool expected) =>
        Assert.Equal(expected, CloseConfirmationPolicy.RequiresConfirmation(policy, running));

    [Theory]
    [InlineData(ConfirmOnClose.Never)]
    [InlineData(ConfirmOnClose.Automatic)]
    [InlineData(ConfirmOnClose.Always)]
    public void AutomaticProcessExitNeverPrompts(ConfirmOnClose policy) =>
        Assert.False(CloseConfirmationPolicy.RequiresConfirmation(policy, 3, automaticExit: true));
}
