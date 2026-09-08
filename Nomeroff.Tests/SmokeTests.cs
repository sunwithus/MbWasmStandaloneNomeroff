using Nomeroff.Shared;
using Xunit;

namespace Nomeroff.Tests;

/// <summary>Базовые инварианты алфавита — страховка от регрессий в нормализации.</summary>
public class PlateAlphabetTests
{
    [Theory]
    [InlineData("A636AA06", "А636АА06")]
    [InlineData("t314xc125", "Т314ХС125")]
    [InlineData("В 713 ВВ 125", "В713ВВ125")]
    public void Normalize_MapsLatinToCyrillicAndStripsSeparators(string raw, string expected)
        => Assert.Equal(expected, PlateAlphabet.Normalize(raw));

    [Theory]
    [InlineData("А636АА06", true)]
    [InlineData("Х034ХА125", true)]
    [InlineData("9036СС45", true)]
    [InlineData("АБВ123", false)]
    [InlineData("А636ЯЯ06", false)]
    public void LooksLikeRuPlate_AcceptsCivilianAndMilitary(string plate, bool expected)
        => Assert.Equal(expected, PlateAlphabet.LooksLikeRuPlate(plate));

    [Fact]
    public void TryFixMilitaryFromCivilianLookalike_UsesGlyphShapeNotHardcodedNine()
    {
        Assert.Equal("9036СС45", PlateAlphabet.TryFixMilitaryFromCivilianLookalike("У036СС45"));
        Assert.Equal("0036СС45", PlateAlphabet.TryFixMilitaryFromCivilianLookalike("О036СС45"));
        Assert.Null(PlateAlphabet.TryFixMilitaryFromCivilianLookalike("К036СС45"));
    }
}
