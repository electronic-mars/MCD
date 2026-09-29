using Mcd.Interop.Machine;
using Shouldly;

namespace Mcd.Tests.Machine;

/// <summary>
/// The two letters a layout is written as, and the hands-off promises the
/// keep-awake switch makes.
/// </summary>
public sealed class KeyboardLayoutsTests
{
    [Theory]
    [InlineData(0x0409, "EN")]
    [InlineData(0x0809, "EN")]
    [InlineData(0x0419, "RU")]
    [InlineData(0x0407, "DE")]
    public void ALanguageIsWrittenAsItsTwoLetters(int language, string code)
    {
        KeyboardLayouts.Of((ushort)language).Code.ShouldBe(code);
    }

    [Fact]
    public void ALayoutSaysItsNameForTheTooltip()
    {
        KeyboardLayouts.Of(0x0419).Name.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TheLayoutOfTheWindowInFrontIsAlwaysSomething()
    {
        // Whatever holds the foreground on the machine running the tests, the
        // widget must have two letters to draw.
        KeyboardLayouts.Current().Code.Length.ShouldBe(2);
    }
}
