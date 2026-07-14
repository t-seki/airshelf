using System.Buffers.Binary;

namespace Radirec.Hosting.Control;

/// <summary>
/// 制御チャネルの 1 メッセージ = 4 バイト長プレフィックス(big-endian Int32)+ UTF-8 JSON のフレーミング。
/// **Byte モードのパイプ**上で動く(named pipe の Message モードは Unix で非対応のため。ADR-0010 の
/// 「round-trip は WSL で検証」を成立させる)。長さで区切るので送信側の write 分割にも依存しない。
/// </summary>
internal static class ControlWire
{
    public static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>1 フレームを読む。相手が何も送らず切断したら null。長さが不正なら例外。</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(stream, 4, cancellationToken).ConfigureAwait(false);
        if (header is null)
            return null;

        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > ControlContract.MaxMessageBytes)
            throw new InvalidDataException($"制御メッセージのフレーム長が不正です: {length}");

        return await ReadExactAsync(stream, length, cancellationToken).ConfigureAwait(false)
               ?? throw new EndOfStreamException("制御メッセージのボディが途中で切れました");
    }

    private static async Task<byte[]?> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        if (count == 0)
            return [];

        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                return offset == 0 ? null : throw new EndOfStreamException("制御メッセージが途中で切れました");
            offset += read;
        }

        return buffer;
    }
}
