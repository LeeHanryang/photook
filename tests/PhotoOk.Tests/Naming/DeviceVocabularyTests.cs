using System.IO;
using PhotoOk.Core.Metadata;
using PhotoOk.Core.Naming;
using PhotoOk.Core.Presets;

namespace PhotoOk.Tests.Naming;

public class DeviceVocabularyTests
{
    [Fact]
    public void Empty_ContainsNothing()
    {
        Assert.False(DeviceVocabulary.Empty.Contains("Zf"));
        Assert.False(DeviceVocabulary.Empty.Contains(""));
    }

    [Theory]
    [InlineData("NIKON")]
    [InlineData("Z f")]
    [InlineData("zf")]
    [InlineData("Z-F")]
    [InlineData("NIKON Z f")]
    [InlineData("nikon_z_f")]
    public void From_ContainsMakeModelAndBoth_IgnoringCaseSpaceHyphenUnderscore(string folder)
    {
        var vocabulary = DeviceVocabulary.From([Photo("NIKON", "Z f")], []);

        Assert.True(vocabulary.Contains(folder));
    }

    [Fact]
    public void From_ModelWithMakePrefix_ContainsModelWithoutPrefix()
    {
        var vocabulary = DeviceVocabulary.From([Photo("NIKON CORPORATION", "NIKON Z f")], []);

        Assert.True(vocabulary.Contains("Z f"));
        Assert.True(vocabulary.Contains("NIKON Z f"));
        Assert.True(vocabulary.Contains("NIKON CORPORATION"));
    }

    [Fact]
    public void From_ContainsAliasNameAndModel()
    {
        var vocabulary = DeviceVocabulary.From([], [new DeviceAlias("서브카메라", "ILCE-7M4")]);

        Assert.True(vocabulary.Contains("서브카메라"));
        Assert.True(vocabulary.Contains("ilce7m4"));
    }

    [Fact]
    public void From_UsesEveryFile_AndSkipsMissingValues()
    {
        var vocabulary = DeviceVocabulary.From(
            [Photo(null, null), Photo("Canon", null), Photo(null, "EOS R6"), Photo("  ", "")], []);

        Assert.True(vocabulary.Contains("Canon"));
        Assert.True(vocabulary.Contains("EOS R6"));
        Assert.False(vocabulary.Contains(""));
    }

    [Fact]
    public void From_IgnoresValuesShorterThanTwoCharacters()
    {
        var vocabulary = DeviceVocabulary.From([Photo("X", "5")], [new DeviceAlias("a", "b")]);

        Assert.False(vocabulary.Contains("X"));
        Assert.False(vocabulary.Contains("5"));
        Assert.False(vocabulary.Contains("a"));
        Assert.False(vocabulary.Contains("b"));
    }

    [Fact]
    public void From_ManyPhotosWithSameDevice_StillContainsTheDevice()
    {
        var photos = Enumerable.Range(0, 2000).Select(_ => Photo("NIKON", "Z f")).Append(Photo("Canon", "EOS R6")).ToList();

        var vocabulary = DeviceVocabulary.From(photos, []);

        Assert.True(vocabulary.Contains("Zf"));
        Assert.True(vocabulary.Contains("EOS R6"));
    }

    [Fact]
    public void Contains_UnrelatedName_IsFalse()
    {
        var vocabulary = DeviceVocabulary.From([Photo("NIKON", "Z f")], []);

        Assert.False(vocabulary.Contains("가족여행"));
        Assert.False(vocabulary.Contains("Z f 촬영"));
    }

    private static PhotoInfo Photo(string? make, string? model) =>
        new(@"C:\a.jpg", 1, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), MediaKind.Image, null, make, model, null);
}
