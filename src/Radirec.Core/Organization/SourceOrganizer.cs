using System.IO.Abstractions;
using Radirec.Core.Ingestion;
using Radirec.Core.Model;
using Radirec.Core.Normalization;
using Radirec.Core.Sources;

namespace Radirec.Core.Organization;

/// <summary>
/// 散在するソース録音を <c>{targetRoot}/{正規放送局}/{番組}/{元ファイル名}</c> 構造へ仕分ける(#81)。
/// <list type="bullet">
///   <item>放送局 = stations.json の正規名。ただし移動先に「同じ正規名へ解決される既存フォルダ」
///     (らじれこ の別名慣習。例 alias <c>NHK-FM(東京)</c>)があれば**それを再利用**し、canonical と
///     alias でフォルダが分裂するのを防ぐ。</item>
///   <item>番組 = ソースの生の名前(<see cref="ParsedSource.RawProgram"/>)。統合フォルダの
///     <c>(1)/(2)</c> を保ち、再取込時の統合・パート <c>-N</c> を壊さない(ADR-0001/0002)。</item>
///   <item>ファイル名は不変(<c>_{timestamp}</c> 温存で取込互換・出所保持)。</item>
/// </list>
/// 移動先が既存なら**決して上書きしない**(同一内容=スキップ / 別内容=衝突報告)。1 ファイルの失敗で
/// バッチ全体を止めない。移動元は常に残す(削除しない)。
/// </summary>
public sealed class SourceOrganizer(
    IFileSystem fileSystem,
    IRecordingSource source,
    ISourceImporter importer,
    IStationResolver stationResolver,
    RecordingSkipPolicy skipPolicy,
    string targetRoot)
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    public OrganizeReport Run(bool dryRun, bool copy)
    {
        // 列挙を先に確定させる(移動でツリーを変えながらの遅延列挙で、動かした先のファイルを
        // 再列挙してしまう事故を防ぐ)。
        var files = source.Enumerate().ToList();
        var items = new List<OrganizeItemResult>(files.Count);
        foreach (var file in files)
            items.Add(Process(file, dryRun, copy));

        return new OrganizeReport { Items = items, DryRun = dryRun, Copy = copy };
    }

    private OrganizeItemResult Process(RecordingFile file, bool dryRun, bool copy)
    {
        // ジャンク(NHK 非提供アナウンス等)は綺麗アーカイブに混ぜない。移動元に残して人手処分に委ねる。
        if (skipPolicy.ShouldSkip(file))
            return Result(OrganizeOutcome.SkippedJunk, file.FilePath);

        if (!importer.CanHandle(file))
            return Result(OrganizeOutcome.Unsupported, file.FilePath);

        ParsedSource parsed;
        try
        {
            parsed = importer.Parse(file);
        }
        catch (SourceParseException ex)
        {
            return Result(OrganizeOutcome.Failed, file.FilePath, message: ex.Message);
        }

        var stationFolder = ResolveStationFolder(parsed.RawStation);
        var dir = fileSystem.Path.Combine(targetRoot, stationFolder, Sanitize(parsed.RawProgram));
        var target = fileSystem.Path.Combine(dir, file.FileName);

        if (PathEquals(file.FilePath, target))
            return Result(OrganizeOutcome.AlreadyInPlace, file.FilePath, target);

        if (fileSystem.File.Exists(target))
            return SameContent(file, target)
                ? Result(OrganizeOutcome.DuplicateSkipped, file.FilePath, target)
                : Result(OrganizeOutcome.Conflict, file.FilePath, target);

        if (dryRun)
            return Result(copy ? OrganizeOutcome.Copied : OrganizeOutcome.Moved, file.FilePath, target);

        try
        {
            fileSystem.Directory.CreateDirectory(dir);
            if (copy)
                fileSystem.File.Copy(file.FilePath, target, overwrite: false);
            else
                fileSystem.File.Move(file.FilePath, target);

            return Result(copy ? OrganizeOutcome.Copied : OrganizeOutcome.Moved, file.FilePath, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 1 ファイルの移動失敗(ロック/権限/クロスボリューム等)で走査全体を止めない。
            // overwrite:false なので失敗時に移動先を壊さず、移動元も無傷で残る。
            return Result(OrganizeOutcome.Failed, file.FilePath, target, ex.Message);
        }
    }

    // 局フォルダ名を決める。正規名を基本とするが、移動先に「同じ正規名へ解決される既存フォルダ」
    // (らじれこ が使う alias 表記など)があればそれを再利用し、canonical/alias でフォルダが割れるのを防ぐ。
    private string ResolveStationFolder(string rawStation)
    {
        var canonical = stationResolver.Resolve(rawStation).Canonical;

        if (fileSystem.Directory.Exists(targetRoot))
        {
            var existing = fileSystem.Directory.EnumerateDirectories(targetRoot)
                .Select(d => fileSystem.Path.GetFileName(d))
                .Where(name => !string.IsNullOrEmpty(name)
                    && string.Equals(stationResolver.Resolve(name!).Canonical, canonical, StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)
                .FirstOrDefault();
            if (existing is not null)
                return existing;
        }

        return Sanitize(canonical);
    }

    // 移動先に同一内容(サイズ + 最終更新時刻)が既にあるか。同名衝突が真の重複か別物かの判別に使う。
    private bool SameContent(RecordingFile file, string target)
    {
        var info = fileSystem.FileInfo.New(target);
        // FileInfo.LastWriteTimeUtc は UTC の DateTime(.Ticks = UTC tick)、RecordingFile 側は
        // DateTimeOffset(.UtcTicks)。同一インスタントなら両者の tick は一致する。
        return info.Length == file.SizeBytes
            && info.LastWriteTimeUtc.Ticks == file.LastWriteTimeUtc.UtcTicks;
    }

    private bool PathEquals(string a, string b) =>
        string.Equals(
            fileSystem.Path.GetFullPath(a),
            fileSystem.Path.GetFullPath(b),
            StringComparison.OrdinalIgnoreCase);

    // 不正なパス文字を "_" に畳む(LocalFolderTarget.Sanitize と同一。ディレクトリ生成の防御)。
    private static string Sanitize(string segment)
    {
        var cleaned = string.Join('_', segment.Split(InvalidChars)).Trim();
        return cleaned.Length == 0 ? "_" : cleaned;
    }

    private static OrganizeItemResult Result(
        OrganizeOutcome outcome, string sourcePath, string? targetPath = null, string? message = null)
        => new() { Outcome = outcome, SourcePath = sourcePath, TargetPath = targetPath, Message = message };
}
