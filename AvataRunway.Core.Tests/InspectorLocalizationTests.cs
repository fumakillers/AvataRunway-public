using AvataRunway.Models;
using Xunit;

namespace AvataRunway.Core.Tests;

public sealed class InspectorLocalizationTests
{
    [Fact]
    public void NavigationLabelsAreAlwaysEnglish()
    {
        Assert.Equal("Expressions", InspectorLocalization.ExpressionsNavigationText);
        Assert.Equal("Pose", InspectorLocalization.PoseNavigationText);
    }

    [Fact]
    public void JapaneseUsesJapaneseInspectorCommands()
    {
        var expressions = InspectorLocalization.Expressions(AppLanguage.Japanese, isVrm0: false);
        var pose = InspectorLocalization.Pose(AppLanguage.Japanese);

        Assert.Equal(("表情", "インポート", "エクスポート", "すべてリセット"),
            (expressions.Heading, expressions.Import, expressions.Export, expressions.ResetAll));
        Assert.Equal(("ポーズ", "インポート", "エクスポート", "すべてリセット"),
            (pose.Heading, pose.Import, pose.Export, pose.ResetAll));
    }

    [Theory]
    [InlineData(AppLanguage.English)]
    [InlineData(AppLanguage.Chinese)]
    [InlineData(AppLanguage.Korean)]
    public void NonJapaneseLanguagesFallBackToEnglish(AppLanguage language)
    {
        var expressions = InspectorLocalization.Expressions(language, isVrm0: false);
        var pose = InspectorLocalization.Pose(language);

        Assert.Equal(("Expressions", "Import", "Export", "Reset All"),
            (expressions.Heading, expressions.Import, expressions.Export, expressions.ResetAll));
        Assert.Equal(("Poses", "Import", "Export", "Reset All"),
            (pose.Heading, pose.Import, pose.Export, pose.ResetAll));
    }

    [Fact]
    public void Vrm0HeadingKeepsTechnicalBlendShapesName()
    {
        Assert.Equal("ブレンドシェイプ",
            InspectorLocalization.Expressions(AppLanguage.Japanese, isVrm0: true).Heading);
        Assert.Equal("BlendShapes",
            InspectorLocalization.Expressions(AppLanguage.English, isVrm0: true).Heading);
    }
}
