using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Radirec.Core.Configuration;
using Radirec.Core.Normalization;

namespace Radirec.Hosting;

/// <summary><c>programs.json</c> に追記できなかった理由(利用者に見せる文言)。何も書いていない。</summary>
public sealed class ProgramsAppendException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 追記の予定(<see cref="ProgramsFileAppender.Plan"/>)。<see cref="Snippet"/> を利用者に見せて確かめてから
/// <see cref="ProgramsFileAppender.Apply"/> で書く。
/// </summary>
/// <param name="Original">予定を作ったときの programs.json の中身(書く直前に変わっていないか比べる)。</param>
/// <param name="Updated">追記した後の中身。提案が無ければ <see cref="Original"/> と同じ。</param>
/// <param name="Snippet">追記する塊(確認ダイアログに見せる。字下げなし・改行は LF)。提案が無ければ空。</param>
public sealed record ProgramsAppendPlan(
    string Path, byte[] Original, byte[] Updated, string Snippet, IReadOnlyList<SiblingProgramProposal> Proposals);

/// <summary>
/// 統合設定の無い兄弟フォルダの提案を <c>programs.json</c> に**テキストとして追記**する(#144)。追記した後は
/// 利用者がエディタで確かめて直し、保存してから「統合設定の再適用…」を選び直す。
/// </summary>
/// <remarks>
/// <para>
/// 追記する場所は <c>"Programs"</c> の配列の閉じ <c>]</c> の直前(最後の要素の後)。既存の内容・コメント・並びは
/// バイト単位で変えない(挿入するだけ)。最後の要素の後にカンマが無く、エントリを足すときだけ、その要素の直後に
/// カンマを補う。場所はコメント付き JSON を読める <see cref="Utf8JsonReader"/> で決める(文字列を自前で探さない)。
/// </para>
/// <para>
/// 兄弟が既に統合番組に入っている組は、既存のエントリに触れず、足すフォルダをコメントで案内するだけにする。
/// 書く前後の中身は <see cref="ConfigLoader"/> と同じ規則で読めることを確かめ、読めなければ何も書かない。
/// 書く直前に同じフォルダへ <c>programs.json.bak-YYYYMMDD-HHmmss</c> を作り(既にあれば上書きせず別名。
/// <see cref="NewFileWriter"/> で一時ファイル経由)、本体も一時ファイル経由で置き換える(<see cref="ReplacingFileWriter"/>)。
/// programs.json が無いときは呼び出し元(トレイ)が先に見本から作る(起動時と同じ見本)。
/// </para>
/// </remarks>
public static class ProgramsFileAppender
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    // 日本語をそのまま書く(既定のエンコーダーは \uXXXX にしてしまい、エディタで読めない)。
    private static readonly JsonSerializerOptions StringOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// <paramref name="path"/> を読み、<paramref name="groups"/> の提案と、追記した後の中身を作る(まだ書かない)。
    /// </summary>
    /// <exception cref="ProgramsAppendException">ファイルが無い・読めない・壊れている・<c>"Programs"</c> の配列が無い。</exception>
    public static ProgramsAppendPlan Plan(string path, IEnumerable<SiblingFolderGroup> groups, DateOnly today)
    {
        byte[] original;
        try
        {
            original = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException ex)
        {
            throw new ProgramsAppendException($"{Path.GetFileName(path)} がありません", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProgramsAppendException($"{Path.GetFileName(path)} を読めませんでした: {ex.Message}", ex);
        }

        var config = Parse(original, Path.GetFileName(path));
        var proposals = SiblingProgramProposals.Build(groups, new ProgramResolver(config));
        if (proposals.Count == 0)
            return new ProgramsAppendPlan(path, original, original, string.Empty, proposals);

        var updated = Insert(original, proposals, today);
        return new ProgramsAppendPlan(path, original, updated, Render(proposals, today, indent: "", newline: "\n"), proposals);
    }

    /// <summary>
    /// 予定どおりに書く。書く直前に programs.json が予定を作ったときと同じかを確かめ、バックアップを作ってから置き換える。
    /// </summary>
    /// <returns>作ったバックアップのパス。</returns>
    /// <exception cref="ProgramsAppendException">予定を作った後に programs.json が変わった・読めない。何も書いていない。</exception>
    /// <exception cref="Exception">バックアップ・置き換えの失敗はそのまま投げる。programs.json は元のまま残る。</exception>
    public static string Apply(ProgramsAppendPlan plan, DateTime now)
    {
        byte[] current;
        try
        {
            current = File.ReadAllBytes(plan.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProgramsAppendException($"{Path.GetFileName(plan.Path)} を読めませんでした: {ex.Message}", ex);
        }
        if (!current.AsSpan().SequenceEqual(plan.Original))
            throw new ProgramsAppendException(
                $"確認している間に {Path.GetFileName(plan.Path)} が変わったため、追記しませんでした(もう一度選び直してください)");

        // バックアップが作れなければ(投げたら)本体は書き換えない。
        // 中身は書く直前に確かめた元の中身。
        var backup = FileBackup.Create(plan.Path, plan.Original, now);
        ReplacingFileWriter.Replace(plan.Path, s => s.Write(plan.Updated));
        return backup;
    }

    /// <summary>
    /// <paramref name="original"/>(programs.json の中身)の <c>"Programs"</c> 配列の最後に <paramref name="proposals"/> を
    /// 挿入した中身を返す(純粋)。既存のバイトは並びも含めてそのまま残る。
    /// </summary>
    /// <exception cref="ProgramsAppendException">壊れている・<c>"Programs"</c> の配列が無い・追記した結果が読めない。</exception>
    public static byte[] Insert(byte[] original, IReadOnlyList<SiblingProgramProposal> proposals, DateOnly today)
    {
        const string name = "programs.json";
        var before = Parse(original, name);

        var bom = original.AsSpan().StartsWith(Utf8Bom) ? Utf8Bom.Length : 0;
        var json = original.AsSpan(bom);
        var shape = FindProgramsArray(json);
        var newline = json.IndexOf("\r\n"u8) >= 0 ? "\r\n" : "\n";

        var elementIndent = shape.FirstElementStart is { } first ? IndentBefore(json, first) : null;
        elementIndent ??= (IndentBefore(json, shape.PropertyStart) ?? "") + "  ";

        var addsEntries = proposals.Any(p => !p.JoinsExisting);
        var needsComma = addsEntries && shape.LastElementEnd is not null && !shape.HasTrailingComma;

        // ] が行頭(字下げだけの後)にあればその行の頭に行ごと差し込む。] の前に何かある(`[ ]`・`} ]` など)なら、
        // 改行してから差し込み、] は "Programs" の行と同じ字下げで次の行に置く。
        var closeLineStart = LineStartIfOnlyIndent(json, shape.CloseIndex);
        var block = Render(proposals, today, elementIndent, newline);
        var (insertAt, inserted) = closeLineStart is { } lineStart
            ? (lineStart, block)
            : (shape.CloseIndex, newline + block + (IndentBefore(json, shape.PropertyStart) ?? ""));

        var output = new List<byte>(original.Length + Encoding.UTF8.GetByteCount(inserted) + 1);
        output.AddRange(original.AsSpan(0, bom));
        if (needsComma)
        {
            var commaAt = shape.LastElementEnd!.Value;
            output.AddRange(json[..commaAt]);
            output.Add((byte)',');
            output.AddRange(json[commaAt..insertAt]);
        }
        else
        {
            output.AddRange(json[..insertAt]);
        }
        output.AddRange(Encoding.UTF8.GetBytes(inserted));
        output.AddRange(json[insertAt..]);
        var updated = output.ToArray();

        // 追記した結果が同じ規則で読め、足したエントリの数だけ増えていること(読めなければ書かない)。
        var after = Parse(updated, name);
        var added = proposals.Count(p => !p.JoinsExisting);
        if (after.Programs.Count != before.Programs.Count + added)
            throw new ProgramsAppendException($"{name} への追記を組み立てられませんでした(追記後のエントリ数が合いません)");

        return updated;
    }

    // 追記する塊。先頭に見出しのコメント、最後に終わりのコメント(利用者が塊ごと消して元に戻せるように)。
    // 新しいエントリどうしはカンマで区切り、最後のエントリの後にはカンマを付けない(コメントは後に続いてよい)。
    private static string Render(IReadOnlyList<SiblingProgramProposal> proposals, DateOnly today, string indent, string newline)
    {
        var lines = new List<string> { $"// --- 統合設定の提案({today:yyyy-MM-dd} 追記。確認して直してください)---" };
        var lastEntry = proposals.Select((p, i) => (p, i)).LastOrDefault(x => !x.p.JoinsExisting).i;
        for (var i = 0; i < proposals.Count; i++)
        {
            var p = proposals[i];
            if (p.JoinsExisting)
            {
                lines.Add($"// 次の行を、統合番組『{p.Album}』({p.Station})の \"Folders\" の末尾に足してください:");
                lines.AddRange(p.Folders.Select(f => $"//   {Quote(f)}"));
                continue;
            }

            lines.Add("{");
            lines.Add("  \"Match\": {");
            lines.Add($"    \"Station\": {Quote(p.Station)},");
            lines.Add("    \"Folders\": [");
            lines.AddRange(p.Folders.Select((f, j) => $"      {Quote(f)}{(j < p.Folders.Count - 1 ? "," : "")}"));
            lines.Add("    ]");
            lines.Add("  },");
            lines.Add($"  \"Canonical\": {{ \"Album\": {Quote(p.Album)} }}");
            lines.Add(i == lastEntry ? "}" : "},");
        }
        lines.Add("// --- 統合設定の提案ここまで ---");

        var text = new StringBuilder();
        foreach (var line in lines)
            text.Append(indent).Append(line).Append(newline);
        return text.ToString();
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value, StringOptions);

    private static ProgramMasterConfig Parse(byte[] content, string name)
    {
        try
        {
            // ファイルから読むとき(File.ReadAllText)と同じく、BOM は読み飛ばす。
            return ConfigLoader.Parse<ProgramMasterConfig>(new UTF8Encoding(false).GetString(content).TrimStart('﻿'));
        }
        catch (JsonException ex)
        {
            throw new ProgramsAppendException($"{name} を読めません(JSON が壊れています): {ex.Message}", ex);
        }
    }

    /// <summary>"Programs" 配列の形(位置はすべて BOM を除いた先頭からのバイト位置)。</summary>
    /// <param name="PropertyStart">"Programs" のプロパティ名の位置(字下げの手がかり)。</param>
    /// <param name="FirstElementStart">最初の要素の始まり。空の配列なら null。</param>
    /// <param name="LastElementEnd">最後の要素の終わりの直後。空の配列なら null。</param>
    /// <param name="HasTrailingComma">最後の要素と ] の間にカンマがあるか。</param>
    /// <param name="CloseIndex">閉じ ] の位置。</param>
    private sealed record ArrayShape(int PropertyStart, int? FirstElementStart, int? LastElementEnd, bool HasTrailingComma, int CloseIndex);

    private static ArrayShape FindProgramsArray(ReadOnlySpan<byte> json)
    {
        try
        {
            return FindProgramsArrayCore(json);
        }
        catch (JsonException ex)
        {
            throw new ProgramsAppendException($"programs.json を読めません(JSON が壊れています): {ex.Message}", ex);
        }
    }

    private static ArrayShape FindProgramsArrayCore(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Allow, // コメントの位置も知りたい(Skip だと見えない)
            AllowTrailingCommas = true,
        });

        int? propertyStart = null;
        int? arrayDepth = null;
        int? firstElementStart = null;
        int? lastElementEnd = null;
        var commentsAfterLast = new List<(int Start, int End)>();

        while (reader.Read())
        {
            if (arrayDepth is null)
            {
                // ルートのオブジェクト直下の "Programs"(読み込みと同じく大文字小文字を区別しない)。
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1
                    || !string.Equals(reader.GetString(), "Programs", StringComparison.OrdinalIgnoreCase))
                    continue;

                propertyStart = (int)reader.TokenStartIndex;
                do
                {
                    if (!reader.Read())
                        throw new ProgramsAppendException("programs.json の \"Programs\" の値がありません");
                } while (reader.TokenType == JsonTokenType.Comment);

                if (reader.TokenType != JsonTokenType.StartArray)
                    throw new ProgramsAppendException("programs.json の \"Programs\" が配列ではありません");
                arrayDepth = reader.CurrentDepth;
                continue;
            }

            var elementDepth = arrayDepth.Value + 1;
            switch (reader.TokenType)
            {
                case JsonTokenType.EndArray when reader.CurrentDepth == arrayDepth:
                    var close = (int)reader.TokenStartIndex;
                    var end = lastElementEnd ?? close;
                    return new ArrayShape(
                        propertyStart!.Value, firstElementStart, lastElementEnd,
                        lastElementEnd is not null && HasCommaOutside(json, end, close, commentsAfterLast), close);

                case JsonTokenType.Comment:
                    commentsAfterLast.Add(((int)reader.TokenStartIndex, (int)reader.BytesConsumed));
                    break;

                // 要素の始まり(オブジェクト・配列・値)。
                case JsonTokenType.StartObject or JsonTokenType.StartArray when reader.CurrentDepth == elementDepth:
                    firstElementStart ??= (int)reader.TokenStartIndex;
                    break;

                // 要素の終わり。
                case JsonTokenType.EndObject or JsonTokenType.EndArray when reader.CurrentDepth == elementDepth:
                    lastElementEnd = (int)reader.BytesConsumed;
                    commentsAfterLast.Clear();
                    break;

                case JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False
                    or JsonTokenType.Null when reader.CurrentDepth == elementDepth:
                    firstElementStart ??= (int)reader.TokenStartIndex;
                    lastElementEnd = (int)reader.BytesConsumed;
                    commentsAfterLast.Clear();
                    break;
            }
        }

        throw new ProgramsAppendException("programs.json に \"Programs\" の配列が見つかりません");
    }

    // [from, to) のうち、コメントの外にカンマがあるか。トークンの間にはコメント・空白・カンマしか無い。
    private static bool HasCommaOutside(ReadOnlySpan<byte> json, int from, int to, List<(int Start, int End)> comments)
    {
        for (var i = from; i < to; i++)
        {
            if (comments.FirstOrDefault(c => c.Start <= i && i < c.End) is { End: > 0 } comment)
            {
                i = comment.End - 1;
                continue;
            }
            if (json[i] == (byte)',')
                return true;
        }
        return false;
    }

    // index の行の頭から index までが字下げ(空白・タブ)だけなら、その字下げ。そうでなければ null。
    private static string? IndentBefore(ReadOnlySpan<byte> json, int index)
        => LineStartIfOnlyIndent(json, index) is { } start ? Encoding.UTF8.GetString(json[start..index]) : null;

    // index の行の頭から index までが空白・タブだけなら、行の頭の位置。そうでなければ null。
    private static int? LineStartIfOnlyIndent(ReadOnlySpan<byte> json, int index)
    {
        var i = index;
        while (i > 0 && json[i - 1] is (byte)' ' or (byte)'\t')
            i--;
        return i == 0 || json[i - 1] == (byte)'\n' ? i : null;
    }
}
