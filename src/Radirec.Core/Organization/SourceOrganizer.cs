using System.IO.Abstractions;
using Radirec.Core.Ingestion;
using Radirec.Core.Model;
using Radirec.Core.Normalization;
using Radirec.Core.Output;
using Radirec.Core.Sources;

namespace Radirec.Core.Organization;

/// <summary>
/// 散在するソース録音を <c>{targetRoot}/{正規放送局}/{番組}/{元ファイル名}</c> 構造へ仕分ける(#81)。
/// <list type="bullet">
///   <item>放送局 = stations.json の正規名。移動先に「canonical の**表記変種**へ解決される既存フォルダ」
///     (らじれこ の alias 慣習。例 <c>NHK-FM(東京)</c>)があれば再利用して分裂を防ぐ。ただし
///     局 ID コード(<c>LFR</c> 等の不透明な別名)は変種とみなさず reuse しない。</item>
///   <item>番組 = ソースの生の名前(<see cref="ParsedSource.RawProgram"/>)。統合フォルダの
///     <c>(1)/(2)</c> を保ち、再取込時の統合・パート <c>-N</c> を壊さない(ADR-0001/0002)。</item>
///   <item>ファイル名は不変(<c>_{timestamp}</c> 温存で取込互換・出所保持)。</item>
/// </list>
/// 未安定(書込中/0 バイト)は動かさない。移動先が既存なら**決して上書きしない**(内容一致=スキップ /
/// 別内容=衝突報告)。1 ファイルの失敗でバッチを止めず、失敗時は途中書きの移動先を残さない。移動元は削除しない。
/// </summary>
public sealed class SourceOrganizer(
    IFileSystem fileSystem,
    IRecordingSource source,
    ISourceImporter importer,
    IStationResolver stationResolver,
    IStabilityChecker stability,
    RecordingSkipPolicy skipPolicy,
    string targetRoot)
{
    public OrganizeReport Run(bool dryRun, bool copy)
    {
        // 列挙を先に確定させる(移動でツリーを変えながらの遅延列挙で、動かした先のファイルを
        // 再列挙してしまう事故を防ぐ)。
        var files = source.Enumerate().ToList();

        // 局フォルダ解決(既存フォルダの再利用判定)は移動先ルートを 1 回だけ列挙して map 化する
        // (ファイル数 × フォルダ数の再走査を避ける。新規に確定した局は map に足して一貫させる)。
        var stationFolders = BuildStationFolderMap();

        var items = new List<OrganizeItemResult>(files.Count);
        foreach (var file in files)
            items.Add(Process(file, dryRun, copy, stationFolders));

        return new OrganizeReport { Items = items, DryRun = dryRun, Copy = copy };
    }

    private OrganizeItemResult Process(
        RecordingFile file, bool dryRun, bool copy, Dictionary<string, string> stationFolders)
    {
        // ジャンク(NHK 非提供アナウンス等)は綺麗アーカイブに混ぜない。移動元に残して人手処分に委ねる。
        if (skipPolicy.ShouldSkip(file))
            return Result(OrganizeOutcome.SkippedJunk, file.FilePath);

        if (!importer.CanHandle(file))
            return Result(OrganizeOutcome.Unsupported, file.FilePath);

        // 書込中/0 バイトの録音は動かさない(move は移動元を消すため、欠けた録音を運んでしまう)。ingest と同じガード。
        if (!stability.IsStable(file))
            return Result(OrganizeOutcome.SkippedUnstable, file.FilePath);

        ParsedSource parsed;
        try
        {
            parsed = importer.Parse(file);
        }
        catch (Exception ex)
        {
            // parse は SourceParseException 以外(radikool の part 序数 int.Parse Overflow 等)も投げうる。
            // バッチ全体を止めないため広く捕捉して当該ファイルのみ Failed 扱いにする。
            return Result(OrganizeOutcome.Failed, file.FilePath, message: ex.Message);
        }

        var stationFolder = ResolveStationFolder(parsed.RawStation, stationFolders);
        var dir = fileSystem.Path.Combine(targetRoot, stationFolder, FileNameSanitizer.Sanitize(parsed.RawProgram));
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
            // 途中書きの移動先を残さない(後の ingest が truncated を本物として取り込むのを防ぐ)。
            // 移動元が無事に残っている限り、移動先の削除は安全(source が真)。
            TryCleanupFailedTarget(file.FilePath, target);
            return Result(OrganizeOutcome.Failed, file.FilePath, target, ex.Message);
        }
    }

    // 移動先ルート直下の既存フォルダを 1 回列挙し、canonical → 再利用フォルダ名の map を作る。
    // 「canonical の表記変種」のフォルダだけを候補にする(局 ID コードは弾く)。決定的にするため
    // 名前の昇順で最初の 1 つを採る。
    private Dictionary<string, string> BuildStationFolderMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!fileSystem.Directory.Exists(targetRoot))
            return map;

        foreach (var d in fileSystem.Directory.EnumerateDirectories(targetRoot)
                     .Select(fileSystem.Path.GetFileName)
                     .Where(n => !string.IsNullOrEmpty(n))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            var name = d!;
            var canonical = stationResolver.Resolve(name).Canonical;
            if (IsCanonicalVariant(name, canonical) && !map.ContainsKey(canonical))
                map[canonical] = name;
        }

        return map;
    }

    private string ResolveStationFolder(string rawStation, Dictionary<string, string> stationFolders)
    {
        var canonical = stationResolver.Resolve(rawStation).Canonical;
        if (stationFolders.TryGetValue(canonical, out var folder))
            return folder;

        // 新規局。canonical でフォルダを作る方針を map に固定し、同一局の後続ファイルも同じ場所へ。
        folder = FileNameSanitizer.Sanitize(canonical);
        stationFolders[canonical] = folder;
        return folder;
    }

    // 既存フォルダ名が canonical の「表記変種」か。照合キーが互いに包含関係にあるものだけ変種とみなし、
    // 局 ID コード(LFR 等、canonical と共通部分を持たない不透明な別名)は reuse 対象から外す。
    private static bool IsCanonicalVariant(string folderName, string canonical)
    {
        var f = TextNormalizer.ToMatchKey(folderName);
        var c = TextNormalizer.ToMatchKey(canonical);
        return f.Length > 0 && c.Length > 0 && (f.Contains(c) || c.Contains(f));
    }

    // 移動先に同一「内容」があるか(サイズ一致時のみ中身を突き合わせる)。mtime は使わない
    // (タイムスタンプがずれた同一内容を誤って衝突にしないため)。同名衝突は稀なので読取 I/O は限定的。
    private bool SameContent(RecordingFile file, string target)
    {
        var info = fileSystem.FileInfo.New(target);
        if (info.Length != file.SizeBytes)
            return false;

        using var a = fileSystem.File.OpenRead(file.FilePath);
        using var b = fileSystem.File.OpenRead(target);
        return StreamsEqual(a, b);
    }

    private static bool StreamsEqual(Stream a, Stream b)
    {
        const int ChunkSize = 64 * 1024;
        var bufA = new byte[ChunkSize];
        var bufB = new byte[ChunkSize];
        while (true)
        {
            var ra = ReadFully(a, bufA);
            var rb = ReadFully(b, bufB);
            if (ra != rb)
                return false;
            if (ra == 0)
                return true;
            if (!bufA.AsSpan(0, ra).SequenceEqual(bufB.AsSpan(0, rb)))
                return false;
        }
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0)
                break;
            total += n;
        }
        return total;
    }

    private void TryCleanupFailedTarget(string sourcePath, string target)
    {
        try
        {
            if (fileSystem.File.Exists(sourcePath) && fileSystem.File.Exists(target))
                fileSystem.File.Delete(target);
        }
        catch
        {
            // 掃除失敗は致命的でない(移動元は残っているので手動で再実行できる)。
        }
    }

    private bool PathEquals(string a, string b) =>
        string.Equals(
            fileSystem.Path.GetFullPath(a),
            fileSystem.Path.GetFullPath(b),
            StringComparison.OrdinalIgnoreCase);

    private static OrganizeItemResult Result(
        OrganizeOutcome outcome, string sourcePath, string? targetPath = null, string? message = null)
        => new() { Outcome = outcome, SourcePath = sourcePath, TargetPath = targetPath, Message = message };
}
