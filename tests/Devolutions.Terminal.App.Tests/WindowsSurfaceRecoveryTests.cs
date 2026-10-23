using System.Runtime.InteropServices;
using Devolutions.Terminal.App.Platform;
using Xunit;

namespace Devolutions.Terminal.App.Tests;

public sealed class WindowsSurfaceRecoveryTests
{
    [Fact]
    public void RepaintsWhenTheDisplayConfigurationChanges()
    {
        Assert.True(WindowsSurfaceRecovery.ShouldRepaint(WindowsSurfaceRecovery.WmDisplayChange, 32, 0));
    }

    [Theory]
    [InlineData(0x8, true)] // WTS_SESSION_UNLOCK
    [InlineData(0x1, true)] // WTS_CONSOLE_CONNECT
    [InlineData(0x3, true)] // WTS_REMOTE_CONNECT
    [InlineData(0x7, false)] // WTS_SESSION_LOCK
    [InlineData(0x2, false)] // WTS_CONSOLE_DISCONNECT
    public void RepaintsWhenTheSessionBecomesVisibleAgain(long reason, bool expected)
    {
        Assert.Equal(
            expected,
            WindowsSurfaceRecovery.ShouldRepaint(WindowsSurfaceRecovery.WmWtsSessionChange, (nint)reason, 0));
    }

    [Theory]
    [InlineData(0x7, true)] // PBT_APMRESUMESUSPEND
    [InlineData(0x12, true)] // PBT_APMRESUMEAUTOMATIC
    [InlineData(0x4, false)] // PBT_APMSUSPEND
    [InlineData(0xA, false)] // PBT_APMPOWERSTATUSCHANGE
    public void RepaintsAfterResume(long eventType, bool expected)
    {
        Assert.Equal(
            expected,
            WindowsSurfaceRecovery.ShouldRepaint(WindowsSurfaceRecovery.WmPowerBroadcast, (nint)eventType, 0));
    }

    [Theory]
    [InlineData(0u, false)] // off
    [InlineData(1u, true)] // on
    [InlineData(2u, true)] // dimmed
    public void RepaintsWhenTheDisplayTurnsBackOn(uint state, bool expected)
    {
        Assert.Equal(
            expected,
            ShouldRepaintForPowerSetting(WindowsSurfaceRecovery.ConsoleDisplayState, state));
    }

    [Fact]
    public void IgnoresOtherPowerSettings()
    {
        Assert.False(ShouldRepaintForPowerSetting(Guid.NewGuid(), 1));
        Assert.False(WindowsSurfaceRecovery.ShouldRepaint(
            WindowsSurfaceRecovery.WmPowerBroadcast,
            WindowsSurfaceRecovery.PbtPowerSettingChange,
            0));
    }

    [Theory]
    [InlineData(0x000Fu)] // WM_PAINT
    [InlineData(0x0005u)] // WM_SIZE
    [InlineData(0x0002u)] // WM_DESTROY
    public void IgnoresUnrelatedMessages(uint message)
    {
        Assert.False(WindowsSurfaceRecovery.ShouldRepaint(message, 0x8, 0));
    }

    private static bool ShouldRepaintForPowerSetting(Guid powerSetting, uint data)
    {
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<WindowsSurfaceRecovery.PowerBroadcastSetting>());
        try
        {
            Marshal.StructureToPtr(
                new WindowsSurfaceRecovery.PowerBroadcastSetting
                {
                    PowerSetting = powerSetting,
                    DataLength = sizeof(uint),
                    Data = data,
                },
                buffer,
                fDeleteOld: false);
            return WindowsSurfaceRecovery.ShouldRepaint(
                WindowsSurfaceRecovery.WmPowerBroadcast,
                WindowsSurfaceRecovery.PbtPowerSettingChange,
                buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
