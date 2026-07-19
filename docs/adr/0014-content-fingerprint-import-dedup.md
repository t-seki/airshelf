# 取込の重複判定に内容フィンガープリント(+放送日時)ゲートを足す

Status: accepted(#79。requirements §7 / [ADR-0001](0001-program-identity-anchored-to-folder.md)・[ADR-0002](0002-same-day-tiebreak-by-time.md)・[ADR-0004](0004-part-index-dedup-for-timeless-sources.md)・[ADR-0011](0011-ingest-failure-as-separate-eventlog-signal.md) と関連)

取込の import-once 判定は 2 つの関門だった:同一性キー `Contains(identity, broadcast_time, part_index)` と、フォールバックの `ContainsSource(source_path)`。identity は **番組フォルダ名**由来([ADR-0001](0001-program-identity-anchored-to-folder.md):`RajirekoImporter` は `RawProgram = file.ProgramFolder`)であり、part_index も統合フォルダの並び順由来だ。

このため、**ソースの番組フォルダをリネームすると** identity(と part)が変わって第1関門が外れ、パスも変わって第2関門も外れる。内容が完全に同一でも「未取込」と判定して再取込し、出力先に既存ファイルがあると `LocalFolderTarget` の衝突フォールバックが放送時刻を付けた重複(`… 2024-12-23-1 0830.m4a`)を**無音で**生成した(#79 の実害)。同種の事象は**台帳喪失**(per-user 化 #75-#78 で `ledger.db` が `%LOCALAPPDATA%` へ移設)でも起こりうる — 共有先に残る既取込ファイルを新台帳が知らず、走査で大量に再取込・重複する。

根本原因は「重複判定が**内容**に非依存で、パス/フォルダ名から導く同一性だけに頼っていた」こと。

## 決定

- **第3の関門 `ContainsContent(fingerprint, broadcast_date, broadcast_time)` を足す**。取込元パス/番組フォルダ名に**非依存**で「同一内容が同じ放送スロットに取込済みか」を判定する。3 関門の **OR** で、いずれか一致ならスキップする。
- **フィンガープリント = サイズ + 最終更新(ticks)**。[ADR-0011](0011-ingest-failure-as-separate-eventlog-signal.md) の失敗 de-dup が既に持つ `FileFingerprint`(安定判定で取得済み → **追加 I/O ゼロ**)を流用する。**内容ハッシュは採らない**:毎走査で全ソースファイルを SMB 越しに読む I/O は本ツールが意図的に避けている設計で、主因のフォルダリネームは mtime を変えないため size+mtime で十分捕捉できる。
- **放送日 + 放送時刻で裏取りする**(フィンガープリント単独にしない)。日時はファイル名末尾 `_{YYYYMMDDHHMMSS}` 由来で、フォルダのリネームでは変わらない。裏取りは、異なる放送が偶然同サイズ + 同更新時刻で衝突したときに 2 件目を**誤スキップ(取りこぼし)**する非対称リスク(欠落 > 重複)を大きく下げる。フィンガープリント単独だと、同日・別時刻の 2 放送([ADR-0002](0002-same-day-tiebreak-by-time.md))を誤って畳みうる。
  - **残る非対称リスク(受容)**: identity(局・番組)はキーに含めない — 含めるとフォルダリネームでゲートが外れ目的を果たせないため。よって「別番組が同一サイズ + 同一更新時刻 + 同一放送日時」で衝突すると 2 件目を取りこぼす。実内容が違えばサイズが一致しないので確率は極小。**時刻なしソース(radikool、`broadcast_time=''`)は日付のみの裏取りになり相対的に弱い**が、radikool は本 ADR の主因(らじれこのフォルダリネーム)ではなく、同日別録音は内容(サイズ)が異なる。将来ここを締めるなら、リネームに耐える追加裏取り(ファイル名由来の `[局]` 等)を検討する。
- **part_index はキーに含めない**。part はまさにフォルダ順で変わる値で、含めるとリネーム時にゲートが外れて目的を果たせない。
- **[ADR-0002](0002-same-day-tiebreak-by-time.md) / [ADR-0004](0004-part-index-dedup-for-timeless-sources.md) は不変**。中身の違う同日別放送は内容(サイズ)が異なる → フィンガープリントが一致せず、両方取り込む。回帰テストで固定する。
- **スキーマ**:`imports` に `size_bytes` / `last_write` 列を **ALTER ADD COLUMN** で足す(PK 不変の追加列なので rename→copy 移送は不要)。既存の最新スキーマ(part_index あり・content 列なし)にだけ後付けし、移送された旧行は既定 (0,0)=「内容不明」で、実ファイル(サイズ > 0)の照会には一致しない。索引 `ix_imports_content(size_bytes, last_write)` を張る。読み取り専用(dry-run)で content 列を持たない DB を開いたときは `ContainsContent` を例外にせず false(未取込扱い)にする。

## 結果

- ソースフォルダのリネーム・台帳喪失後でも、内容が同じ回は再取込されずスキップされ、衝突フォールバックの無音重複が出ない。
- 追加コストは列 2 つと索引 1 つ、判定ごとの索引照会 1 回のみ。フィンガープリントは取得済み値の再利用で追加 I/O なし。
- **残作業(#79 に残す)**:`LocalFolderTarget` の衝突フォールバックの堅牢化(内容同一ならスキップ / 内容差異なら区別 + ログ・通知。現状は無音で ` HHmm` を付す)。本 ADR の content ゲートで主因は上流でスキップされるため、フォールバックは真の別内容衝突の最後の砦としてのみ残る。
