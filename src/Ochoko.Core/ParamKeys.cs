namespace Ochoko.Core;

/// <summary>
/// 受け付ける A.I.VOICE のパラメータ名。公式ドキュメント(https://aivoice.jp/manual/editor/api.html)に基づく。
/// 実機のプリセット JSON と一致するかは list_presets で確認すること。
/// </summary>
public static class ParamKeys
{
    public static readonly string[] PresetNumeric = { "Volume", "Speed", "Pitch", "PitchRange", "MiddlePause", "LongPause" };

    public const string Styles = "Styles";
    public const string StyleName = "Name";
    public const string StyleValue = "Value";

    public static readonly string[] MasterNumeric =
    {
        "Volume", "Speed", "Pitch", "PitchRange", "MiddlePause", "LongPause", "SentencePause",
    };

    /// <summary>エンジンが作る作業用プリセットの接頭辞。ユーザーのプリセットとは別に扱う。</summary>
    public const string WorkingPresetPrefix = "ochoko:";
}
