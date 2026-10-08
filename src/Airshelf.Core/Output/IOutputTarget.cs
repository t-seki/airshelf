using Airshelf.Core.Model;

namespace Airshelf.Core.Output;

/// <summary>
/// 正規化済み録音の配置先。M1 は <see cref="LocalFolderTarget"/> のみ(Jellyfin リフレッシュは M2)。
/// </summary>
public interface IOutputTarget
{
    /// <summary>
    /// 副作用なしで配置先を算出する(dry-run と書き込みの両方で使う)。出力先に同じ内容のファイルが
    /// 既にあれば <see cref="OutputPlan.AlreadyPresent"/> を立てる(ADR-0015)。
    /// </summary>
    OutputPlan Plan(RecordingFile source, Episode episode, NormalizedTags tags);

    /// <summary>原本を <paramref name="plan"/> のパスへ非破壊コピーしてタグを書き、確定パスを返す。</summary>
    Task<string> WriteAsync(
        RecordingFile source, OutputPlan plan, NormalizedTags tags, CancellationToken cancellationToken = default);

    /// <summary>中断で残った孤児テンポラリを掃除し、削除数を返す(常駐の起動時。ADR-0003)。</summary>
    int CleanupOrphans();
}
