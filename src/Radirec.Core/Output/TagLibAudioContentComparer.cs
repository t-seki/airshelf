namespace Radirec.Core.Output;

/// <summary>
/// TagLib# による <see cref="IAudioContentComparer"/> 実装。<c>File.InvariantStartPosition</c> /
/// <c>InvariantEndPosition</c>(タグ編集で変わらない区間)のバイト列を比べる。MP4 は <c>mdat</c> ボックス
/// (<c>mdat</c> が 1 つのファイルだけ。複数 <c>mdat</c> / <c>moof</c> は判定できない = 違う内容扱い)、
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

        // TagLib の MP4 パーサは mdat を見つけるたびに区間を上書きするので、不変区間は「最後の mdat」しか
        // 指さない。mdat が複数ある・fragmented(moof)な MP4 では、最後の断片(番組末尾の無音等)が一致した
        // だけで別放送を同じ内容と判定してしまう(= 恒久的な欠落)。mdat が 1 つの MP4 だけを比べる。
        if (file is TagLib.Mpeg4.File && !HasSingleMediaData(path))
            return null;

        var start = file.InvariantStartPosition;
        var end = file.InvariantEndPosition;
        // TagLib は読み取り後にストリームを閉じ、File.Length が 0 になるので、実ファイルの長さと照らす。
        if (start < 0 || end <= start || end > new FileInfo(path).Length)
            return null;

        return (start, end - start);
    }

    // MP4 のトップレベルのボックスを先頭から辿り(size/type ヘッダだけ読む)、mdat がちょうど 1 つで moof が
    // 無いときだけ true。ヘッダが壊れている・読めないときも false(判定できない = 違う内容扱い)。
    private static bool HasSingleMediaData(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = stream.Length;
        var header = new byte[16];
        var mdatCount = 0;
        long position = 0;

        while (position < length)
        {
            if (length - position < 8)
                return false;

            stream.Seek(position, SeekOrigin.Begin);
            stream.ReadExactly(header, 0, 8);
            long size = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            var headerSize = 8L;

            if (size == 1)
            {
                // 64bit の largesize がヘッダ直後に続く。
                if (length - position < 16)
                    return false;
                stream.ReadExactly(header, 8, 8);
                var largeSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                if (largeSize > long.MaxValue)
                    return false;
                size = (long)largeSize;
                headerSize = 16;
            }
            else if (size == 0)
            {
                // ファイル末尾まで続く最後のボックス。
                size = length - position;
            }

            if (size < headerSize || size > length - position)
                return false;

            if (type == "moof")
                return false;
            if (type == "mdat" && ++mdatCount > 1)
                return false;

            position += size;
        }

        return mdatCount == 1;
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
