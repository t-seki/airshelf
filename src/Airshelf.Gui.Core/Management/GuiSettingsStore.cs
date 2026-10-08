using System.Text.Json;
using Airshelf.Hosting;

namespace Airshelf.Gui.Core.Management;

/// <summary>
/// 管理画面用の設定ファイル <c>gui-settings.json</c> の中身(#152・ADR-0019)。<c>airshelf.json</c> と同じフォルダに置き、
/// 管理画面だけが読み書きする(<c>airshelf.json</c> は GUI で編集しない)。値が無いキーは null(既定の決め方は読み手が持つ)。
/// </summary>
public sealed record GuiSettings
{
    /// <summary>新規取込のトーストを出すか(W-302)。取込失敗のトーストはこの設定に関係なく出る。</summary>
    public bool? IngestToastsEnabled { get; init; }
}

/// <summary>
/// <see cref="GuiSettings"/> を <c>gui-settings.json</c> に読み書きする(#152・ADR-0019)。書くときは同じフォルダの一時ファイルに
/// 書き切ってから置き換える(<see cref="ReplacingFileWriter"/>。途中で切れた壊れた JSON を残さない)。
/// </summary>
public sealed class GuiSettingsStore
{
    /// <summary>設定ファイルの名前(<see cref="ConfigPaths.DataRoot"/> に置く)。</summary>
    public const string FileName = "gui-settings.json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly Action<string> _logWarn;

    /// <param name="path">設定ファイルの絶対パス。</param>
    /// <param name="logWarn">gui.log への記録(読めなかったとき)。</param>
    public GuiSettingsStore(string path, Action<string> logWarn)
    {
        Path = path;
        _logWarn = logWarn;
    }

    /// <summary>設定ファイルの絶対パス。</summary>
    public string Path { get; }

    /// <summary>
    /// 読む。ファイルが無ければ空の設定を返す。壊れていて読めないときも、gui.log に書いて空の設定を返す
    /// (トースト 1 つの設定のために常駐を止めない。次に保存したときに書き直される)。
    /// </summary>
    public GuiSettings Load()
    {
        try
        {
            if (!File.Exists(Path))
                return new GuiSettings();
            return JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(Path), ReadOptions) ?? new GuiSettings();
        }
        catch (Exception ex)
        {
            _logWarn($"{FileName} を読めませんでした(既定の値で続けます。{Path}): {ex.Message}");
            return new GuiSettings();
        }
    }

    /// <summary>書く。失敗はそのまま投げる(ファイルは元のまま)。</summary>
    public void Save(GuiSettings settings)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (dir is not null)
            Directory.CreateDirectory(dir);
        ReplacingFileWriter.Replace(Path, stream => JsonSerializer.Serialize(stream, settings, WriteOptions));
    }
}
