using System.IO.Abstractions;
using Radirec.Core.Attention;
using Radirec.Core.Configuration;
using Radirec.Core.Ledger;
using Radirec.Core.Normalization;
using Radirec.Core.Reapply;
using Radirec.Core.Sources;

namespace Radirec.Hosting;

/// <summary>
/// 管理画面の要対応(ADR-0018)を、台帳(失敗のストアを含む)とディスクの設定ファイル(<c>programs.json</c> / <c>stations.json</c>)から計算する。
/// 設定は取り込みが読み込んだものではなく、ディスクのファイルを読む(「統合する」「分けておく」が書く先と同じで、
/// 取り込みが止まっていても計算できる。#155 の決定 4)。読むのは台帳と設定ファイルだけで、取込元・出力先には触れない。
/// </summary>
public static class AttentionLoader
{
    /// <param name="ledgerPath">
    /// 台帳の絶対パス。まだ無ければ(取込ゼロ)空の台帳として計算する。親フォルダに届かなければ空とせず失敗にする(#216)。
    /// </param>
    /// <param name="stationsFile">stations.json の場所。使わない設定(空)なら既定の放送局の設定。</param>
    /// <param name="programsFile">programs.json の場所。使わない設定(<c>ProgramsFile</c> が空)なら null か空。</param>
    /// <param name="knownRecordingKinds">らじれこの録音種別(<c>radirec.json</c> の <c>KnownRecordingKinds</c>)。</param>
    /// <exception cref="Exception">台帳か設定ファイルを読めなかった(呼び出し側で理由を出す)。</exception>
    /// <exception cref="DirectoryNotFoundException">台帳の親フォルダに届かない(<see cref="LedgerFile.Exists"/>)。</exception>
    public static AttentionItems Load(
        string ledgerPath, string? stationsFile, string? programsFile, IEnumerable<string> knownRecordingKinds)
    {
        var stations = string.IsNullOrWhiteSpace(stationsFile)
            ? new StationMasterConfig()
            : ConfigLoader.LoadOptional<StationMasterConfig>(stationsFile);
        var programs = string.IsNullOrWhiteSpace(programsFile)
            ? null
            : ConfigLoader.LoadOptional<ProgramMasterConfig>(programsFile);

        IReadOnlyList<LedgerEntry> entries = [];
        IReadOnlyList<IngestFailureRecord> failures = [];
        if (LedgerFile.Exists(ledgerPath))
        {
            // 取り込みワーカーの書込接続とは別の読み取り専用の接続(取込履歴と同じ。SQLite の WAL は並行に読める)。
            // 失敗のストアも同じ台帳の別テーブル(ADR-0011)。
            using var ledger = new SqliteLedger(ledgerPath, readOnly: true);
            entries = ledger.ListAll();
            failures = ledger.List();
        }

        var resolver = new LedgerEpisodeResolver(
            new FileSystem().Path,
            new RajirekoImporter(knownRecordingKinds),
            new StationResolver(stations),
            new ProgramResolver(programs ?? new ProgramMasterConfig()));
        return AttentionCalculator.Compute(entries, resolver, new AttentionPrograms(programs), failures);
    }
}
