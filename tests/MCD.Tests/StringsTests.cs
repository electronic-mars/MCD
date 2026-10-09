using System.Text.RegularExpressions;
using System.Xml.Linq;
using Shouldly;

namespace Mcd.Tests;

/// <summary>
/// Every language says everything English says, with the same blanks to fill.
/// </summary>
/// <remarks>
/// A language that drifts does not fail anywhere else: a missing key quietly
/// shows the English fallback, and a lost "{0}" throws only on the day that
/// string is formatted. Master Audio Switcher's previous life ended up with
/// fifteen languages three keys behind and nothing to say so.
/// </remarks>
public sealed partial class StringsTests
{
    private static Dictionary<string, string> Read(string language) =>
        XDocument.Load(Path.Combine("strings", language, "Resources.resw"))
            .Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);

    public static TheoryData<string> Languages()
    {
        var data = new TheoryData<string>();
        foreach (string dir in Directory.GetDirectories("strings"))
        {
            if (Path.GetFileName(dir) != "en-US")
            {
                data.Add(Path.GetFileName(dir));
            }
        }

        return data;
    }

    [GeneratedRegex(@"\{\d+(?::[^}]*)?\}")]
    private static partial Regex Placeholder();

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryLanguageHasEveryStringWithTheSamePlaceholders(string language)
    {
        Dictionary<string, string> english = Read("en-US");
        Dictionary<string, string> other = Read(language);

        other.Keys.Order().ShouldBe(english.Keys.Order());

        foreach ((string key, string source) in english)
        {
            other[key].ShouldNotBeNullOrWhiteSpace(key);
            Placeholder().Matches(other[key]).Select(m => m.Value).Order()
                .ShouldBe(Placeholder().Matches(source).Select(m => m.Value).Order(), key);
        }
    }

    [Fact]
    public void ThereAreFifteenLanguagesInAll() =>
        Directory.GetDirectories("strings").Length.ShouldBe(15);
}
