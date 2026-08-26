using Mcd.Interop.Shell;
using Shouldly;

namespace Mcd.Tests.Shell;

/// <summary>
/// Getting an icon out of the shell.
/// </summary>
/// <remarks>
/// Asked of the real shell, against a file every Windows machine has. There is
/// nothing here to mock that would be worth mocking: this code is a sequence of
/// calls that each return success while producing nothing, which is precisely
/// how it first shipped - every launcher button drew a letter instead of an
/// icon, with no error anywhere.
/// </remarks>
public sealed class ShellIconTests
{
    [Fact]
    public void AProgramHasAnIcon()
    {
        string notepad = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");

        IconPixels pixels = ShellIcon.For(notepad).ShouldNotBeNull();

        pixels.Width.ShouldBeGreaterThan(0);
        pixels.Height.ShouldBeGreaterThan(0);
        pixels.Bgra.Length.ShouldBe(pixels.Width * pixels.Height * 4);

        // An icon that is transparent everywhere is the failure this test is
        // for: every call succeeded and the picture is empty.
        pixels.Bgra.Where((_, i) => i % 4 == 3).ShouldContain(a => a != 0);
    }

    [Fact]
    public async Task AnIconIsStillFoundFromAPoolThread()
    {
        string notepad = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");

        IconPixels? pixels = await Task.Run(() => ShellIcon.For(notepad));

        pixels.ShouldNotBeNull();
    }

    [Fact]
    public void SomethingThatIsNotAFileGivesNothing()
    {
        ShellIcon.For("https://example.com").ShouldBeNull();
    }
}
