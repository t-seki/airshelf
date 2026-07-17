namespace Radirec.Gui.Core;

/// <summary>
/// 取込失敗シグナル(EventLog EventID 1002)の JSON envelope に対応する受け手側 DTO(ADR-0011)。
/// 発行側(Hosting)の C# 型は共有せず、バージョン付き JSON 契約として受ける。ドリフトは契約テストで検出する。
/// </summary>
public sealed record IngestFailurePayload
{
    public int SchemaVersion { get; init; }

    /// <summary>新しく現れた失敗の総数(発行側で de-dup 済み。キャップ前の実数)。</summary>
    public int FailedCount { get; init; }

    /// <summary>表示上限により files を間引いたか。</summary>
    public bool Truncated { get; init; }

    public IReadOnlyList<IngestFailureFilePayload> Files { get; init; } = [];
}

/// <summary>失敗した 1 ファイル(ファイル名 + 理由)。フルパスは運ばれない。</summary>
public sealed record IngestFailureFilePayload
{
    public string Name { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}
