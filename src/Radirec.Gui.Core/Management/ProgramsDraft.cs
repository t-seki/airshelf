using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Radirec.Core.Configuration;

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

    /// <summary>W-241 の片側。エントリを 1 つずつ JSON で並べる。無ければ <see cref="Nothing"/>。</summary>
    public static string Describe(IEnumerable<object> entries)
    {
        var parts = entries.Select(e => JsonSerializer.Serialize(e, e.GetType(), EntryJson)).ToList();
        return parts.Count == 0 ? Nothing : string.Join("\n\n", parts);
    }
}
