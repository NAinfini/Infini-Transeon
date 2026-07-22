using InfiniTranseon.Core.Runtime;

namespace InfiniTranseon.Core.Tests.Runtime;

public sealed class WindowsPlatformGuardTests
{
    [Theory]
    [InlineData(10, 0, 22620, false)]
    [InlineData(10, 0, 22621, true)]
    [InlineData(10, 0, 26100, true)]
    [InlineData(6, 3, 9600, false)]
    public void OnlyWindows11Build22621OrNewerIsAccepted(
        int major,
        int minor,
        int build,
        bool expected)
    {
        Assert.Equal(expected,
            WindowsPlatformGuard.IsSupported(new Version(major, minor, build)));
    }
}
