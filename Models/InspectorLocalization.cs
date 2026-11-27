namespace AvataRunway.Models;

public sealed record InspectorCommandText(
    string Heading,
    string Import,
    string Export,
    string ResetAll,
    string NoAvatar,
    string Empty);

public static class InspectorLocalization
{
    public const string ExpressionsNavigationText = "Expressions";
    public const string PoseNavigationText = "Pose";

    public static bool UsesJapanese(AppLanguage language) => language == AppLanguage.Japanese;

    public static string Select(AppLanguage language, string japanese, string english) =>
        UsesJapanese(language) ? japanese : english;

    public static InspectorCommandText Expressions(AppLanguage language, bool isVrm0)
    {
        var isJapanese = UsesJapanese(language);
        return new InspectorCommandText(
            isJapanese ? (isVrm0 ? "ブレンドシェイプ" : "表情") : (isVrm0 ? "BlendShapes" : "Expressions"),
            isJapanese ? "インポート" : "Import",
            isJapanese ? "エクスポート" : "Export",
            isJapanese ? "すべてリセット" : "Reset All",
            isJapanese ? "VRMが読み込まれていません" : "No avatar loaded",
            isJapanese ? "表情が見つかりません" : "No expressions found");
    }

    public static InspectorCommandText Pose(AppLanguage language)
    {
        var isJapanese = UsesJapanese(language);
        return new InspectorCommandText(
            isJapanese ? "ポーズ" : "Poses",
            isJapanese ? "インポート" : "Import",
            isJapanese ? "エクスポート" : "Export",
            isJapanese ? "すべてリセット" : "Reset All",
            isJapanese ? "VRMが読み込まれていません" : "No avatar loaded",
            isJapanese ? "ポーズが利用できません" : "No pose is available");
    }
}
