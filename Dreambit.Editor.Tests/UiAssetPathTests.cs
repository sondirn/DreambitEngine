using Dreambit.UI;

namespace Dreambit.Editor.Tests;

public sealed class UiAssetPathTests
{
    [Theory]
    [InlineData("Ui/main.uxml", "Ui/main.xmlb")]
    [InlineData("Ui/main.xml", "Ui/main.xmlb")]
    [InlineData("Ui/main.xmlb", "Ui/main.xmlb")]
    [InlineData("Ui/main", "Ui/main.xmlb")]
    public void UiSourcePathsMapToXmlb(string sourcePath, string expected)
    {
        Assert.Equal(expected, UiAssetPath.ToBakedXml(sourcePath));
    }

    [Theory]
    [InlineData("Ui/main.ucss", "Ui/main.cssb")]
    [InlineData("Ui/main.css", "Ui/main.cssb")]
    [InlineData("Ui/main.cssb", "Ui/main.cssb")]
    [InlineData("Ui/main", "Ui/main.cssb")]
    public void StylesheetSourcePathsMapToCssb(string sourcePath, string expected)
    {
        Assert.Equal(expected, UiAssetPath.ToBakedStylesheet(sourcePath));
    }

    [Theory]
    [InlineData("Ui/main.uxml", "Ui/main.ucss")]
    [InlineData("Ui/main.xmlb", "Ui/main.ucss")]
    [InlineData("Ui/main", "Ui/main.ucss")]
    [InlineData("Ui/main.xml", "Ui/main.css")]
    public void SiblingStylesheetsUseTheMatchingAuthoredConvention(
        string documentPath,
        string expected)
    {
        Assert.Equal(expected, UiAssetPath.GetSiblingStylesheet(documentPath));
    }
}
