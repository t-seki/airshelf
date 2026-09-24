using System.Collections;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Radirec.Core.Configuration;

namespace Radirec.Hosting;

/// <summary><c>programs.json</c> を読めない・書けなかった理由(利用者に見せる文言)。</summary>
public class ProgramsFileException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 読み込んだ後に <c>programs.json</c> が変わっていた(その間にエディタで直された等)ので、書かなかった。
/// 呼び出し側は読み直してから、もう一度やり直す。
/// </summary>
public sealed class ProgramsFileChangedException(string message) : ProgramsFileException(message);

/// <summary>
/// 読み込んだときの <c>programs.json</c>(<see cref="ProgramsFileWriter.Read"/>)。書くときに、この中身から
/// 変わっていないかを確かめる。
/// </summary>
/// <param name="Original">読み込んだときの中身(バイト列そのまま)。ファイルが無かったなら null。</param>
/// <param name="Config">読み込んだ設定。ファイルが無かったなら空の設定。</param>
/// <param name="CommentCount">
/// ファイルにあるコメントの件数(<c>//</c> 1 行で 1 件、<c>/* */</c> 1 つで 1 件)。書き直すと消えるので、
/// 初めて保存するときの確認に使う(ADR-0017)。
/// </param>
public sealed record ProgramsFileSnapshot(string Path, byte[]? Original, ProgramMasterConfig Config, int CommentCount);

/// <summary>
/// <c>programs.json</c> の書き手(ADR-0017)。書くときはファイル全体を書き直す(コメントは残らない)。
/// </summary>
/// <remarks>
/// <para>
/// 書く前に同じフォルダへ <c>programs.json.bak-YYYYMMDD-HHmmss</c> を作り、本体は一時ファイルに書き切ってから置き換える
/// (<see cref="ProgramsFileAppender"/> と同じ仕組み)。読み込んだときの中身と、書く直前の中身が違っていたら書かない。
/// </para>
/// <para>
/// 書き出しは字下げあり・非 ASCII(日本語)をエスケープしない・UTF-8(BOM なし)。<see cref="ProgramAlias.Note"/> が null の
/// エントリ、空の <see cref="ProgramMasterConfig.KeepSeparate"/> は、キーごと書かない。これらを使わない限り、
/// 未知のプロパティを拒む古い版(<see cref="ConfigLoader"/>)でも読める。
/// </para>
/// </remarks>
public static class ProgramsFileWriter
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // 日本語をそのまま書く(既定のエンコーダーは \uXXXX にしてしまい、エディタで読めない)。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Note が null ならキーごと書かない。
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { OmitEmptyKeepSeparate } },
    };

    /// <summary><paramref name="path"/> を読む。ファイルが無ければ空の設定を返す(書くときに新しく作る)。</summary>
    /// <exception cref="ProgramsFileException">読めない・JSON が壊れている・未知のプロパティがある。</exception>
    public static ProgramsFileSnapshot Read(string path)
    {
        byte[] original;
        try
        {
            original = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new ProgramsFileSnapshot(path, null, new ProgramMasterConfig(), 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProgramsFileException($"{Path.GetFileName(path)} を読めませんでした: {ex.Message}", ex);
        }

        try
        {
            var json = WithoutBom(original);
            // ファイルから読むとき(File.ReadAllText)と同じく、BOM は読み飛ばす。
            var config = ConfigLoader.Parse<ProgramMasterConfig>(Encoding.UTF8.GetString(json));
            return new ProgramsFileSnapshot(path, original, config, CountComments(json));
        }
        catch (JsonException ex)
        {
            throw new ProgramsFileException($"{Path.GetFileName(path)} を読めません(JSON が壊れています): {ex.Message}", ex);
        }
    }

    /// <summary>
    /// <paramref name="config"/> でファイル全体を書き直す。書く直前に、<paramref name="snapshot"/> を読み込んだときから
    /// 中身が変わっていないかを確かめ、バックアップを作ってから置き換える。
    /// </summary>
    /// <returns>作ったバックアップのパス。ファイルが無くて新しく作ったなら null。</returns>
    /// <exception cref="ProgramsFileChangedException">読み込んだ後にファイルが変わっていた。何も書いていない。</exception>
    /// <exception cref="ProgramsFileException">書く直前に読めなかった。何も書いていない。</exception>
    /// <exception cref="Exception">バックアップ・置き換えの失敗はそのまま投げる。programs.json は元のまま残る。</exception>
    public static string? Write(ProgramsFileSnapshot snapshot, ProgramMasterConfig config, DateTime now)
    {
        var path = snapshot.Path;
        var name = Path.GetFileName(path);
        var content = Serialize(config);

        byte[]? current;
        try
        {
            current = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            current = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProgramsFileException($"{name} を読めませんでした: {ex.Message}", ex);
        }

        var unchanged = (current, snapshot.Original) switch
        {
            (null, null) => true,
            ({ } c, { } o) => c.AsSpan().SequenceEqual(o),
            _ => false,
        };
        if (!unchanged)
            throw Changed(name);

        if (current is null)
        {
            // 置く直前に誰かが作っていたら、上書きしない。
            if (!NewFileWriter.CreateIfAbsent(path, s => s.Write(content)))
                throw Changed(name);
            return null;
        }

        // バックアップが作れなければ(投げたら)本体は書き換えない。
        var backup = FileBackup.Create(path, current, now);
        ReplacingFileWriter.Replace(path, s => s.Write(content));
        return backup;
    }

    /// <summary>書き出す中身(純粋)。<see cref="ConfigLoader"/> と同じ規則で読めることを確かめてから返す。</summary>
    /// <exception cref="ProgramsFileException">書き出した中身が読めない(起きないはず。起きたら書かない)。</exception>
    public static byte[] Serialize(ProgramMasterConfig config)
    {
        var json = JsonSerializer.Serialize(config, WriteOptions) + WriteOptions.NewLine;
        try
        {
            ConfigLoader.Parse<ProgramMasterConfig>(json);
        }
        catch (JsonException ex)
        {
            throw new ProgramsFileException($"programs.json の書き出しを組み立てられませんでした: {ex.Message}", ex);
        }
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
    }

    /// <summary>
    /// コメントの件数を数える(<c>//</c> 1 行で 1 件、<c>/* */</c> 1 つで 1 件)。文字列の中の <c>//</c> は数えない。
    /// 先頭の BOM は読み飛ばす。
    /// </summary>
    /// <exception cref="JsonException">JSON として読めない。</exception>
    public static int CountComments(ReadOnlySpan<byte> content)
    {
        var reader = new Utf8JsonReader(WithoutBom(content), new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Allow,
            AllowTrailingCommas = true, // ConfigLoader と同じく許す(許さないと、読める設定で数える側だけが落ちる)
        });

        var count = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.Comment)
                count++;
        }
        return count;
    }

    private static ReadOnlySpan<byte> WithoutBom(ReadOnlySpan<byte> content)
        => content.StartsWith(Utf8Bom) ? content[Utf8Bom.Length..] : content;

    private static ProgramsFileChangedException Changed(string name)
        => new($"読み込んだ後に {name} が変わったため、保存しませんでした(読み直してからやり直してください)");

    // 空の KeepSeparate はキーごと書かない(使わない限り古い版でも読めるように)。
    private static void OmitEmptyKeepSeparate(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Type != typeof(ProgramMasterConfig))
            return;

        foreach (var property in typeInfo.Properties)
        {
            if (property.Name == nameof(ProgramMasterConfig.KeepSeparate))
                property.ShouldSerialize = (_, value) => value is ICollection { Count: > 0 };
        }
    }
}
