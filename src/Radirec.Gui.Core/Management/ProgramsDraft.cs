using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Radirec.Core.Configuration;
using Radirec.Core.Normalization;

namespace Radirec.Gui.Core.Management;

/// <summary>
/// 番組ページ(#154)の「アルバム名と統合」の 1 行(<c>programs.json</c> の <c>Programs</c> の 1 エントリ)。
/// 編集は行を差し替えて表す(<see cref="Current"/> は書き換えない)。
/// </summary>
public sealed class ProgramEntryRow
{
    /// <summary>W-204。フォルダが 2 つ以上のエントリ(CONTEXT.md「統合番組」)。</summary>
    public const string KindMerged = "統合番組";

    /// <summary>W-204。フォルダが 1 つのエントリ(表示名だけを整える)。</summary>
    public const string KindAlbumOnly = "アルバム名だけ";

    public ProgramEntryRow(ProgramAlias? original, ProgramAlias current)
    {
        Original = original;
        Current = current;
    }

    /// <summary>読み込んだときのエントリ。この画面で追加した行なら null。</summary>
    public ProgramAlias? Original { get; }

    /// <summary>いまのエントリ(保存するとこれを書く)。</summary>
    public ProgramAlias Current { get; }

    public string Station => Current.Match.Station;

    /// <summary>フォルダ(1 行に 1 つ。並びがパート番号の順)。</summary>
    public string Folders => string.Join("\n", Current.Match.Folders);

    public string Album => Current.Canonical.Album;

    /// <summary>W-204。</summary>
    public string Kind => Current.Match.Folders.Count >= 2 ? KindMerged : KindAlbumOnly;

    public string Note => Current.Note ?? string.Empty;

    /// <summary>読み込んだときから変わったか(追加した行も含む)。</summary>
    public bool IsChanged => Original is null || !ProgramsDraft.Same(Original, Current);
}

/// <summary>番組ページの「分けておく兄弟フォルダ」の 1 行(<c>KeepSeparate</c> の 1 組。ADR-0017)。</summary>
public sealed class KeepSeparateRow
{
    public KeepSeparateRow(KeptApartSiblings? original, KeptApartSiblings current)
    {
        Original = original;
        Current = current;
    }

    /// <summary>読み込んだときの組。この画面で追加した行なら null。</summary>
    public KeptApartSiblings? Original { get; }

    public KeptApartSiblings Current { get; }

    public string Station => Current.Match.Station;

    /// <summary>フォルダの組(W-205 の列。1 行に並べる)。</summary>
    public string Folders => string.Join(" / ", Current.Match.Folders);

    public string Note => Current.Note ?? string.Empty;

    public bool IsChanged => Original is null || !ProgramsDraft.Same(Original, Current);
}

/// <summary>
/// 番組ページの「ファイル名の番組名の判断」の 1 行(<c>FilenamePrograms</c> の 1 件。ADR-0021・#235)。
/// 要対応の「この番組の回」「取り込まない」「別の番組…」(W-159)で積んだ判断も、この一覧の保存していない行になる。
/// </summary>
public sealed class FilenameProgramRow
{
    public FilenameProgramRow(FilenameProgramDecision? original, FilenameProgramDecision current)
    {
        Original = original;
        Current = current;
    }

    /// <summary>読み込んだときの判断。この画面で追加した(要対応から積んだ)行なら null。</summary>
    public FilenameProgramDecision? Original { get; }

    public FilenameProgramDecision Current { get; }

    public string Station => Current.Match.Station;

    public string Folder => Current.Match.Folder;

    public string FilenameProgram => Current.Match.FilenameProgram;

    /// <summary>W-231。</summary>
    public string Treat => ProgramsDraft.TreatLabel(Current.Treat);

    /// <summary>「別の番組」のときのアルバム名。ほかの扱いでは空。</summary>
    public string Album => Current.Treat == FilenameProgramTreatment.OtherProgram ? Current.Album ?? string.Empty : string.Empty;

    public string Note => Current.Note ?? string.Empty;

    public bool IsChanged => Original is null || !ProgramsDraft.Same(Original, Current);

    /// <summary>判断する組(局・フォルダ・ファイル名の番組名)の照合キー。取り込みと同じ照合(<see cref="TextNormalizer.ToMatchKey"/>)。</summary>
    public (string Station, string Folder, string FilenameProgram) Key => ProgramsDraft.DecisionKey(Station, Folder, FilenameProgram);
}

/// <summary>判断の編集フォームの「扱い」の選択肢 1 つ(W-231)。</summary>
public sealed record TreatOption(FilenameProgramTreatment Value)
{
    public string Label => ProgramsDraft.TreatLabel(Value);
}

/// <summary>番組ページの編集中の設定を比べる・写す・見せるための部品(純粋)。</summary>
public static class ProgramsDraft
{
    /// <summary>W-241。変更前 / 変更後に何も無いとき。</summary>
    public const string Nothing = "(なし)";

    private static readonly JsonSerializerOptions EntryJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 日本語をそのまま見せる(programs.json の書き出しと同じ)
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static bool Same(ProgramAlias a, ProgramAlias b)
        => a.Match.Station == b.Match.Station
           && a.Match.Folders.SequenceEqual(b.Match.Folders)
           && a.Canonical.Album == b.Canonical.Album
           && a.Note == b.Note;

    public static bool Same(KeptApartSiblings a, KeptApartSiblings b)
        => a.Match.Station == b.Match.Station
           && a.Match.Folders.SequenceEqual(b.Match.Folders)
           && a.Note == b.Note;

    /// <summary>深い写し(画面で編集しても、読み込んだときの設定を書き換えないように)。</summary>
    public static ProgramAlias Copy(ProgramAlias entry) => new()
    {
        Match = new ProgramMatch { Station = entry.Match.Station, Folders = [.. entry.Match.Folders] },
        Canonical = new ProgramCanonical { Album = entry.Canonical.Album },
        Note = entry.Note,
    };

    public static KeptApartSiblings Copy(KeptApartSiblings entry) => new()
    {
        Match = new ProgramMatch { Station = entry.Match.Station, Folders = [.. entry.Match.Folders] },
        Note = entry.Note,
    };

    public static bool Same(FilenameProgramDecision a, FilenameProgramDecision b)
        => a.Match.Station == b.Match.Station
           && a.Match.Folder == b.Match.Folder
           && a.Match.FilenameProgram == b.Match.FilenameProgram
           && a.Treat == b.Treat
           && a.Album == b.Album
           && a.Note == b.Note;

    public static FilenameProgramDecision Copy(FilenameProgramDecision entry) => new()
    {
        Match = new FilenameProgramMatch
        {
            Station = entry.Match.Station,
            Folder = entry.Match.Folder,
            FilenameProgram = entry.Match.FilenameProgram,
        },
        Treat = entry.Treat,
        Album = entry.Album,
        Note = entry.Note,
    };

    /// <summary>W-231。ファイル名の番組名の扱いの呼び名(要対応のボタン W-159 と同じ語)。</summary>
    public static string TreatLabel(FilenameProgramTreatment treat) => treat switch
    {
        FilenameProgramTreatment.SameProgram => "この番組の回",
        FilenameProgramTreatment.Skip => "取り込まない",
        FilenameProgramTreatment.OtherProgram => "別の番組",
        _ => treat.ToString(),
    };

    /// <summary>判断する組の照合キー(取り込みの判定と同じ <see cref="TextNormalizer.ToMatchKey"/>)。</summary>
    public static (string Station, string Folder, string FilenameProgram) DecisionKey(string station, string folder, string filenameProgram)
        => (TextNormalizer.ToMatchKey(station), TextNormalizer.ToMatchKey(folder), TextNormalizer.ToMatchKey(filenameProgram));

    /// <summary>フォルダの欄(1 行に 1 フォルダ。W-222)を読む。前後の空白を落とし、空の行は捨てる。</summary>
    public static List<string> ParseFolders(string? text)
        => (text ?? string.Empty)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

    /// <summary>メモの欄。空なら null(programs.json に書かない)。</summary>
    public static string? ParseNote(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// 要対応の「分けておく」(W-127)で記録する、分けておく組 1 つ。組と重なる分けておく組がすでにあれば(W-125。記録した組に
    /// フォルダが加わった)、それらを 1 つにまとめる:元の組のフォルダの後ろに、加わったフォルダを足す。メモは最初の組のもの。
    /// 重なる組が無ければ、組のフォルダをパートの順に並べた新しい組にする。
    /// </summary>
    /// <returns>記録する組と、それに置き換える(一覧から消す)元の組。</returns>
    public static (KeptApartSiblings Entry, IReadOnlyList<KeptApartSiblings> Replaced) KeepApart(
        IEnumerable<KeptApartSiblings> keepSeparate, SiblingFolderGroup group)
    {
        var stationKey = TextNormalizer.ToMatchKey(group.Station);
        var groupKeys = group.Folders.Select(TextNormalizer.ToMatchKey).ToHashSet();
        var replaced = keepSeparate
            .Where(k => TextNormalizer.ToMatchKey(k.Match.Station) == stationKey
                        && k.Match.Folders.Any(f => groupKeys.Contains(TextNormalizer.ToMatchKey(f))))
            .ToList();

        var folders = new List<string>();
        var keys = new HashSet<string>();
        foreach (var folder in replaced.SelectMany(k => k.Match.Folders).Concat(SiblingFolders.OrderAsParts(group.Folders)))
        {
            if (keys.Add(TextNormalizer.ToMatchKey(folder)))
                folders.Add(folder);
        }

        var entry = new KeptApartSiblings
        {
            Match = new ProgramMatch { Station = replaced.FirstOrDefault()?.Match.Station ?? group.Station, Folders = folders },
            Note = replaced.FirstOrDefault()?.Note,
        };
        return (entry, replaced);
    }

    /// <summary>W-241 の片側。エントリを 1 つずつ JSON で並べる。無ければ <see cref="Nothing"/>。</summary>
    public static string Describe(IEnumerable<object> entries)
    {
        var parts = entries.Select(e => JsonSerializer.Serialize(e, e.GetType(), EntryJson)).ToList();
        return parts.Count == 0 ? Nothing : string.Join("\n\n", parts);
    }
}
