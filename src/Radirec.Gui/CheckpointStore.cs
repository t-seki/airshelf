using System.IO;
using System.Runtime.Versioning;
using Radirec.Gui.Core;

namespace Radirec.Gui;

/// <summary>
/// 購読チェックポイント(不在中サマリの起点。軸A A2)を <c>%LocalAppData%\Radirec\subscription.json</c> に
/// 永続化する。読み込み失敗・破損は null を返し、呼び手は read-from-now へ縮退する(不正確なサマリを出さない)。
/// 書き込み失敗はログのみでトレイを止めない(TrayLog 流儀)。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CheckpointStore
{
    private static readonly string DefaultPath = AppData.File("subscription.json");

    private readonly string _path;

    public CheckpointStore(string? path = null) => _path = path ?? DefaultPath;

    /// <summary>
    /// 保存済みチェックポイントを読む。未記録(未存在/空)は null で無言。**破損(非空だが解析不能)は
    /// warn を残して** null(= read-from-now 縮退)。破損が痕跡なく消えて field で不在中サマリを静かに
    /// 失う穴を塞ぐ(#56)。
    /// </summary>
    public SubscriptionCheckpoint? Load()
    {
        try
        {
            if (!File.Exists(_path))
                return null;

            var parse = SubscriptionCheckpoint.Classify(File.ReadAllText(_path), out var checkpoint);
            if (parse == SubscriptionCheckpoint.CheckpointParse.Corrupt)
                TrayLog.Warn($"購読チェックポイントが破損。read-from-now へ縮退します: {_path}");
            return checkpoint; // Empty/Corrupt は null、Loaded は値。
        }
        catch (IOException ex)
        {
            TrayLog.Warn($"購読チェックポイントの読込に失敗(縮退します): {ex.Message}");
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            TrayLog.Warn($"購読チェックポイントの読込に失敗(縮退します): {ex.Message}");
            return null;
        }
    }

    /// <summary>チェックポイントを保存する。失敗してもトレイは止めない(次回起動で取りこぼす程度)。</summary>
    public void Save(SubscriptionCheckpoint checkpoint)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, checkpoint.Serialize());
        }
        catch (Exception ex)
        {
            TrayLog.Warn($"購読チェックポイントの保存に失敗: {ex.Message}");
        }
    }
}
