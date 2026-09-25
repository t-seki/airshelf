# Radirec.Gui ロードマップ(常駐トレイ / 編集 GUI)

> Status: Map v3 / 最終更新: 2026-09-25

これは **`Radirec.Gui`(常駐トレイ + 将来の編集 GUI)の将来オプションの地図**であり、着手の約束ではない。
優先順位は付けていない。実際の不満が出た時点で、ある軸を greenlit してから issue を切る(§運用)。

v1(2026-07-18)が並べていた軸A(通知トレイの深化)・軸B(制御チャネルの GUI 化)は**すべて決着済み**で、
生きた軸は **軸C(M5 編集アプリ本体)だけ**になった。経緯は §経緯 に畳んである。
2026-09-25 に、軸C とは別の **軸D(管理画面)** を greenlit した(§軸D)。

一次資料へのポインタ:

- **現行アーキテクチャ**: [ADR-0012](adr/0012-retire-service-for-tray-resident-per-user.md)(トレイ常駐 per-user)/
  [ADR-0013](adr/0013-velopack-per-user-updates.md)(Velopack 自己更新)/ 運用手順は [usage.md](usage.md) §6
- **編集アプリの保留判断**: [requirements.md](requirements.md) §10.1 と issue #5
- **管理画面の設計**: [requirements.md](requirements.md) §10.3 / [ADR-0017](adr/0017-management-window-owns-programs-json.md) /
  [ADR-0018](adr/0018-attention-items-derived-from-state.md) と issue #5 のコメント(2026-09-25)
- **移行前の設計(歴史)**: [ADR-0008](adr/0008-ingest-notification-via-eventlog-signal.md) /
  [ADR-0009](adr/0009-toast-receiver-unpackaged-wpf-tray.md) /
  [ADR-0010](adr/0010-gui-service-control-via-named-pipe.md) /
  [ADR-0011](adr/0011-ingest-failure-as-separate-eventlog-signal.md) と requirements §7.1/§7.2(バナー下の本文)

## 経緯: v1 以降に決着したこと

**軸A(通知トレイの深化)— 出荷済み**

- **A1 専用アイコン**(#52): 汎用 `SystemIcons.Application` を専用 ico に差し替え。
- **A2 不在中サマリ**(#54): 常駐が動いていなかった間の取込を起動時に 1 トーストへ集約。
- **A3 失敗の通知**(#58 / #59): 成功要約とは別種の失敗通知([ADR-0011](adr/0011-ingest-failure-as-separate-eventlog-signal.md))。

A2 / A3 は当初 EventLog シグナルの購読機構として実装したが、[ADR-0012](adr/0012-retire-service-for-tray-resident-per-user.md)
の in-process 化(#72)で再構成された。**ユーザー体験は同一**で、bookmark / RecordId 単調ガード /
catch-up 参加といった「分割が生んだ機構」が消えた([CONTEXT.md](../CONTEXT.md) が語彙の権威)。

**軸B(制御チャネルの GUI 化)— 決着**

- **B1 トレイメニューに rescan/reload/status**(#50): 出荷済み。named pipe クライアントとして実装したが、
  ADR-0012 で in-process 呼び出しに置き換わり、`Radirec.Gui.Core` の制御 DTO は削除された。
- **B2 状態の可視化**: **見送り**(§見送り)。

さらに、v1 の地図に無かった項目が #62(トレイ常駐移行エピック)でトレイメニューに載った —
**版情報**(#63)・**取込履歴**(#66・台帳の読み取り専用ビュー)・**アプリ内自己更新**(#68)。
制御チャネルの GUI 化という論点は、ここで実質的に終わっている。

## 前提: 現状の構造(この地図の起点)

`Radirec.Gui` は「取込通知の受け手」ではなく、**取り込みワーカーを内包する常駐本体**である(ADR-0012)。
1 つのユーザーセッション exe が「取り込み + トースト + 管理画面(取込履歴を含む)+ 制御 + 自己更新」を持つ。

v1 が不変条件として掲げていた「**2 ツリーは ProjectReference を共有しない**」は**退役した**。
実際の参照グラフは 2 経路で Core に届いている:

```
Radirec.Gui ──> Radirec.Gui.Core ──> Radirec.Core
     │                 └───────────> Radirec.Hosting
     └────────> Radirec.Hosting  ──> Radirec.Core
Radirec.Cli ──> Radirec.Cli.Core ──> Radirec.Hosting / Radirec.Core
```

- `Radirec.Cli.Core` は #178 で足した。CLI のコマンド・終了コード・JSON の形を、Linux の CI でテストできる `net10.0` に
  置く(exe の `Radirec.Cli` は Program.cs だけ)。`Gui` / `Gui.Core` と同じ分け方。

- `Gui.Core → Hosting` は管理画面(#151)で足した辺。ViewModel が制御面(`IServiceControl` /
  `ServiceStatusSnapshot`)をそのまま受け取る。Gui.Core 側にインタフェースを切ると、`IServiceControl` の写しと
  アダプタができ、制御面を足すたびに両方を直すことになるので採らなかった。Hosting は `net10.0`(依存は Core だけ)
  なので、Gui.Core は引き続き WSL の CI でビルド・テストできる。

- `Radirec.Gui` 自体に `Radirec.Core` への直接 ProjectReference は**無い**が、**Core の型は既に Gui の
  コンパイル面に乗っている**(`Gui/RadioMonitorWorker.cs` が `Radirec.Core.Ingestion`、
  `Gui/IngestHistory.cs` が `Radirec.Core.Ledger` を直接使う)。ADR-0012 が「参照の実体は
  `Gui → Hosting → Core`」と自己訂正したとおり、`Gui → Core` という辺そのものは存在しない。
- `Radirec.Hosting` は **CLI(`radirec.exe`)と常駐トレイの 2 フロントエンドが共有する合成層**。
  だから Hosting は Cli に畳まず独立を保つ。
- 両ツリーを繋いでいた EventLog の JSON 契約(EventID 1001/1002)と named pipe は**どちらも撤去済み**。
  ただし named pipe は #179([ADR-0020](adr/0020-cli-as-agent-front-via-control-channel.md))で、CLI → トレイの制御チャネルとして
  形を変えて戻った(ユーザーごとの名前・`PipeOptions.CurrentUserOnly`。受け口・型・接続は `Hosting/Control/`)。
  `IngestSummaryPayload` / `IngestFailurePayload` という**型は `Gui.Core` に残っているが、跨プロセスの
  バージョン付き JSON 契約としてではなく、ワーカーから文面整形へ渡す in-process の受け渡し型**として
  生き残っている(シリアライズは無い)。
- **制御(rescan/reload/status)は `Radirec.Hosting/Control/` に残り**(`IServiceControl` /
  `ServiceStatusSnapshot` 等)、トレイが in-process で叩く。CLI の `status` / `reload` は、トレイの受け口
  (`ControlPipeServer`)を通って同じ `IServiceControl` に届く(#179)。制御の実装は Hosting 側にある — ここでも
  Hosting が 2 フロントエンドの共有合成層として効いている。

**`Radirec.Gui.Core` は分離を維持しているが、役割が変わった**: EventLog subscriber 契約の置き場ではなく、
**CI で走らせられない WinRT/WPF/WinForms シェルから隔離した「テスト可能な提示ロジック」**
(トースト文面整形・履歴行整形・版表示・状態の文言)と、管理画面の ViewModel(#151)の置き場である(ADR-0012)。
**軸C で UI を厚くするときも、ロジックは Gui.Core 側に置いてシェルを薄く保つ**のがこの分離の使い方になる。

トレイの実行時は WinForms(トレイ = NotifyIcon)+ WinRT トースト。**WPF は既に実体化済み**で、
管理画面(#151)が WPF + XAML + MVVM(CommunityToolkit.Mvvm)で動いている。取込履歴は管理画面のページになり、
かつての取込履歴ウィンドウ(#66。コードビハインド・xaml なし)は退役した。

## 軸C — M5 編集アプリ本体(キュレーション / 条件付き保留)

issue #5 の本体。**「CLI 運用で不足が判明したら着手」と明示ゲート**(requirements §10.1)。現時点では未着手。

**移行(#62)が軸C の構造前提を先に支払った**。v1 は C2 を「最も大きな構造変化 / #5 本体の賭け」と
値付けしていたが、その値付けは既に無効である。ただし **「構造コストが下がった」と「着手可」は別**で、
§10.1 のゲートは維持する(移行の動機は単一ユーザー機の複雑性削減であって、編集本体の greenlight ではない)。

| 項目 | 根拠 | v1 の値付け | 現在の残差 |
|---|---|---|---|
| **C1 一覧グリッド編集** | #5 / §10.1(title/artist/album/albumartist/disc/track の直接編集) | 「現状 WPF 型ゼロ → ここで初めて WPF/xaml が実体化」 | **WPF スタックは #66 で実体化済み**。読み取り専用グリッド(取込履歴)も動いている。残差は「**編集可能化 + 書き戻し**」で、UI 基盤の新規立ち上げではない |
| **C2 TagLib# で NAS 直接編集 + リフレッシュ** | #5 / §8(取込後の権威は編集側) | 「`Gui→Core` の初参照で 2 ツリーを統合。**疎結合を意図的に崩す**」 | **統合は #62 で完了**。`TagLibTagWriter` / `JellyfinLibraryRefresher` はどちらも Core にあり、後者は取り込みパイプライン(`IngestService`)経由で**既にトレイプロセス内で走っている**。残差は「**取込後のファイルへタグを書き戻す**」経路と、編集と取り込みが同じファイルに触る際の権限分離(§8) |
| **C3 配布フロントエンド** | §14 | 「別次元(配布境界の確立)」 | **per-user インストーラ + 自己更新は ADR-0013 で出荷済み** = C3 の目標モデルそのもの。残差は `LocalFolderTarget` 既定化(§10.2)・非技術者向けの初期設定・コード署名/SmartScreen。**ただし ADR-0012 は「per-user 化は C3 を約束しない」と明記**しており、配布ゲートは維持 |

## 軸D — 管理画面(greenlit 2026-09-25 / エピック #159)

軸C の編集本体(回のタグを 1 つずつ直す)ではなく、**番組の設定の編集と取り込みの状況**を 1 つのウィンドウに集める
(requirements §10.3)。greenlit の根拠は実際に出た不満 — `programs.json` をエディタで直すのがつらい、
兄弟フォルダの知らせをトーストで見逃す、トレイのメニューと通知が増えた — である。**軸C のゲートは外していない**。

- 1 つのウィンドウに 5 ページ(ホーム / 取込履歴 / 番組 / 設定 / バージョン情報)。トレイは「開く・今すぐ動かす・止める」に絞る
- `programs.json` の書き手は管理画面になる(全体を書き直す・`Note`・`KeepSeparate`。ADR-0017)
- 要対応はいまの状態から毎回導く(ADR-0018)
- WPF + XAML + MVVM。ロジックは `Gui.Core` / `Hosting` に置く(§前提 の分離の使い方どおり)
- 作る順:HTML モック → 骨組み+ホーム → 設定ページ・通知の分割 → `programs.json` の書き手 → 番組ページ →
  要対応(兄弟フォルダ)→ 要対応(失敗・食い違った回)→ バージョン情報

§地図から読める構造 の 1.(UI を厚くすると障害分離の圧力が増える)は、軸D にもそのまま当てはまる。

## 地図から読める構造(将来判断の要点)

1. **疎結合への圧力は消え、代わりに障害分離への圧力が来る**。v1 の 3 軸は「2 ツリーの疎結合をどれだけ
   崩すか」で値付けされていたが、その不変条件は #62 で退役した。代わりに ADR-0012 が**意図した代償**として
   引き受けたのが**障害分離の喪失**である — **UI バグ 1 つが取り込みも行うプロセスを巻き添えに落とす**。
   catch-up は結果整合を回復するが稼働は保証しない(トレイが落ちている間は取り込みも止まる)。
   **軸C は UI を厚くする方向の変更なので、この圧力を正面から増やす**。C1 に着手するなら、
   「UI 例外がワーカーを巻き込まない」分離(ワーカーは UI スレッド外・未捕捉例外でプロセス即死させない)を
   設計の前提に置くこと。§前提 の「ロジックは `Gui.Core`・シェルは薄く」はこの圧力への対処でもある。
2. **構造コストは前払い済み。残差は機能そのもの**。C1/C2/C3 いずれも「参照を通す」「基盤を立てる」段階は
   終わっており、残っているのは編集機能・書き戻し経路・配布整備という**中身**である。地図としては
   「大きな構造変化の賭け」という値付けを取り下げる。
3. **ゲートは維持**。軸C は CLI 運用の不足が判明するまで着手しない(#5 / §10.1)。C3 は配布ゲート(§14)。
   **コストが下がったことはゲートを緩める理由にならない** — 移行はどちらの greenlight でもない。

## 見送り(検討済み・現時点で不要)

- **B2 状態の可視化**(常時ポーリング → ツールチップ表示): 「定期ポーリングで status をキャッシュする」という
  設計は**跨プロセス pipe の往復コストを前提**にしていた。in-process 化でその前提が消え、いまはメニューを
  開いた瞬間(`ToolStripDropDown.Opening`)にスナップショットを読むのが最も安い。稼働状態・監視有無・
  最終スキャン時刻と結果は既にメニュー内に出ている。**「状態の可視化」という目的は B1 で概ね達成**され、
  未達なのは「メニューを開かずに見える」という手段だけなので**見送り**。トレイを一目見て状態を知りたいという
  不満が出たら再検討する。
- **トースト深リンク**(タップで Jellyfin アルバムを開く): [ADR-0009](adr/0009-toast-receiver-unpackaged-wpf-tray.md)
  が保留にしていた項目。**v1 が挙げていた「ペイロードへの Jellyfin URL 追加 = 契約変更」というコストは、
  EventLog の JSON 契約が退役したため消滅**した(通知は in-process で組み立てるので単なるフィールド追加)。
  残るコストは COM activator 登録のみ。それでも Jellyfin への即時遷移の必要性が現時点で薄いため
  **見送りを継続**する。ここでも「安くなった」と「やる」は別。

## 運用(この地図の使い方)

- **生きた軸は C と D**。C はゲート下にあるため issue は切らない。D は greenlit 済みで、エピック #159 の sub-issue に切った。
  軸A / 軸B は決着済み(§経緯)。
- **軸C を greenlit した時点で**、その項目を issue #5(または新エピック)配下に sub-issue として切る。
- 決定(採用/不採用・設計方針)が出たら、この地図ではなく該当 ADR / requirements を一次資料として更新し、
  この地図は「オプションの俯瞰」に保つ。
- **構造や実行モデルを変える ADR を切ったら、この地図の整合も同じ PR か直後の docs PR で確認する**。
  v1 が 2 週間古びたのは、#78 の docs 整合が
  README/usage/development/requirements を拾いながら地図だけ飛ばしたためで(#88)、地図は仕様でも運用手順でも
  ないぶん整合対象として思い出されにくい。
