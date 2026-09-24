namespace Radirec.Core.Output;

/// <summary>
/// TagLib# による <see cref="IAudioContentComparer"/> 実装。<c>File.InvariantStartPosition</c> /
/// <c>InvariantEndPosition</c>(タグ編集で変わらない区間)のバイト列を比べる。MP4 は <c>mdat</c> ボックス、
/// MP3 は ID3v2 と末尾タグ(ID3v1 / APE)の間。出力ファイルは <see cref="TagLibTagWriter"/> でタグを
/// 書き換えているので、ファイル全体やサイズではなくこの区間で比べる(ADR-0015)。
/// </summary>
/// <remarks>
/// ファイル全体を読むので重い。呼ぶのは出力先で同名衝突が起きたときだけにする(衝突は ADR-0014 の
/// 内容ゲートを通り抜けた後にしか起きず稀)。
/// </remarks>
public sealed class TagLibAudioContentComparer : IAudioContentComparer
{
    private const int BufferSize = 64 * 1024;

    public bool HasSameAudio(string pathA, string pathB)
    {
        try
        {
            if (InvariantRange(pathA) is not { } a || InvariantRange(pathB) is not { } b)
                return false;

            // 音声区間の長さが違えば中身を読むまでもなく別内容。
            if (a.Length != b.Length)
                return false;

            return SameBytes(pathA, a.Start, pathB, b.Start, a.Length);
        }
        catch
        {
            // 読めない・TagLib が解釈できない等は「違う内容」扱い(欠落 > 重複)。
            return false;
        }
    }

    // タグ以外の音声区間(開始位置, 長さ)。取れない・空・ファイル長と矛盾するときは null。
    private static (long Start, long Length)? InvariantRange(string path)
    {
        using var file = TagLib.File.Create(path);
        var start = file.InvariantStartPosition;
        var end = file.InvariantEndPosition;
        // TagLib は読み取り後にストリームを閉じ、File.Length が 0 になるので、実ファイルの長さと照らす。
        if (start < 0 || end <= start || end > new FileInfo(path).Length)
            return null;

        return (start, end - start);
    }

    private static bool SameBytes(string pathA, long startA, string pathB, long startB, long length)
    {
        using var a = new FileStream(pathA, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        using var b = new FileStream(pathB, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        a.Seek(startA, SeekOrigin.Begin);
        b.Seek(startB, SeekOrigin.Begin);

        var bufA = new byte[BufferSize];
        var bufB = new byte[BufferSize];
        var remaining = length;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(BufferSize, remaining);
            a.ReadExactly(bufA, 0, chunk);
            b.ReadExactly(bufB, 0, chunk);
            if (!bufA.AsSpan(0, chunk).SequenceEqual(bufB.AsSpan(0, chunk)))
                return false;
            remaining -= chunk;
        }

        return true;
    }
}
