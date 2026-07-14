# GUI→サービス制御は named pipe の要求/応答チャネルで行い、CLI クライアントで先行実装する

Status: proposed(#33 GUI→サービス制御チャネル・サービス側先行実装)

[ADR-0008](0008-ingest-notification-via-eventlog-signal.md) で取込通知を**サービス発・一方向**の EventLog シグナルに確定した。その設計中に、将来 GUI が**逆方向**にサービスへ指示を出す需要(手動再スキャン・設定リロード・ライブ状態取得)が判明した。EventLog は一方向のため、この用途には別チャネルが要る。ADR-0008 の非閉塞性(通知は EventLog のまま、制御は別チャネルで足せる)を前提に、本 ADR で GUI→サービスの制御チャネルを設計する。

送出側の本命 GUI(#5)は未着手で、トースト受け手 tracer slice([ADR-0009](0009-toast-receiver-unpackaged-wpf-tray.md))しか無い。ADR-0008 が named pipe を却下した理由の 1 つは「**誰も繋がないパイプサーバを作っても検証できない**」だった — 本 ADR はこの制約に正面から答える必要がある。

## 決定

- **transport = named pipe(固定名 `radirec-control`)。** 単一ユーザー homelab の Windows デスクトップ(ADR-0009)で非目標がリモート制御(#33)である以上、HTTP loopback の唯一の強み(ネットワーク到達性)は非目標そのもの。pipe 名前空間はマシングローバルなので **session 0 越え**(サービス Session 0 ↔ ユーザーセッション GUI)が transport 層で自然に解決し、`PipeSecurity` で同一ユーザー認可を無償で得られる。

- **サービス側 listener + CLI クライアント verb を先行実装し、GUI 配線は #5 へ繰延する。** ADR-0008 が EventLog publisher を Event Viewer で単体検証できたのと同じ構図を作る。`radirec.exe rescan` / `radirec.exe status` / `radirec.exe reload` の CLI クライアント verb が「手動制御」機能そのものであり、かつ **GUI 不在でも listener を同一マシンで E2E 単体検証する手段**になる(別プロセスの `radirec.exe status` を `radirec.exe service` に繋ぐ)。#33 完了条件の「GUI からコマンド送出」は本 ADR では「CLI クライアントから送出」と読み替え、GUI 送出 UI は #5 本体で載せる。

- **listener は `service` verb のホストのみに載せる。** `RadioMonitorWorker` とは別の HostedService として登録し、対話 `ingest` / `refresh` verb には載せない(ADR-0008 の「サービス経路のみ」と一貫)。listener は worker の内部実装ではなく、DI singleton の**狭い仲介 interface**(`IServiceControl` 相当)だけに依存する。worker がそこへ状態を publish しチャネル書込口を expose、listener がそこを参照する。

- **認可 = 自 SID 限定の `PipeSecurity`。** サービスは操作者自身のアカウントで実行される(NAS の UNC 書込のため。`deploy/install.ps1` の `-Credential $ServiceAccount`)ので、サービス = GUI = 同一ユーザーが成立し「ローカル同一ユーザー限定」がほぼ自明に満たされる。`NamedPipeServerStream` を、`WindowsIdentity.GetCurrent().User`(= 自 SID)に FullControl・他は暗黙拒否の ACL で作る。固定 SID(`WellKnownSidType`)ではなく実行中プロセスの SID を動的に使うことで split-privilege 構成でも向きが正しい。サーバ成りすまし(pipe squatting)対策のクライアント側サーバ所有者 SID 照合は単一ユーザー homelab では脅威が低く**任意**。

- **契約 = `schemaVersion` 付き JSON envelope。** ADR-0008/0009 と一貫。request `{ schemaVersion, command, args }` / response `{ schemaVersion, ok, error, result }`。**型共有ポリシーは相手で分ける**: CLI クライアントは `radirec.exe` と同一バイナリ(ADR-0005)なのでサービス側 C# 型をそのまま共有してよい(同一アセンブリ版でドリフト不能)。別 exe の GUI(#5, ADR-0009)は跨プロセス型を共有せず、JSON 契約に対し自前 DTO を持ち `schemaVersion` で互換判定する。サービス側 request/response 型は `Radirec.Hosting`、GUI 側 DTO は通知契約を持つ `Radirec.Gui.Core` に相乗り。両側に golden JSON の契約テストを置く。

- **プロトコル = 1 コマンド 1 接続の単発往復。** message-mode で connect → request 1 通 → response 1 通 → close。常設接続・多重化・バックプレッシャは持たない。数コマンド/操作の頻度に対し接続ごとに状態を持たない方が listener が単純で、途中切断は「次の接続で作り直す」だけで済む。listener は 1 接続処理の例外でその接続を閉じるだけで listen を継続し、1 クライアントの不正が本体取込を巻き込まない。

- **全コマンドを非閉塞にする(在中スキャンをブロックしない)。**
  - **status**: worker が保持する**スレッドセーフな状態スナップショット**(稼働状態・監視ルート・最終スキャン完了時刻とトリガ種別・直近の取込/失敗件数)を直列チャネルを介さず直接読んで即応。worker は現状最終スキャン時刻を保持しないため、この観測可能状態を新設する。
  - **rescan**: 既存の要求チャネル(`BoundedChannel` 容量1・DropWrite で coalesce・ADR-0003)へ 1 本書いて即「受理」を返す。スキャン完了は待たない。既存の単一直列コンシューマにトリガ種別「手動」が 1 つ増えるだけ。
  - **reload**: listener スレッドで**新 config の読込+検証だけ同期実行**(グラフに触れない)し、妥当なら差し替え要求を直列ループへ enqueue して即「受理」、不正なら即エラーで**旧グラフ維持**。

- **reload の反映 = worker グラフのプロセス内再構築(部分 hot-swap はしない)。** `radirec.json` のほとんどの項目(`RadioRoot`/`LedgerPath`/`SkipPatterns` 等)はグラフに焼き込まれ、hot-swap は「どの項目で何を作り直すか」の分岐を worker に増やし ADR-0003 の直列不変条件を自壊させる。よって reload は **worker を丸ごと再起動する**セマンティクスに割り切る。実装は `CompositionRoot.Build` を worker ctor から `ExecuteAsync`(または `RebuildGraph()`)へ移し、グラフ参照を差し替え可能にする。差し替えは**在中スキャン完了後に直列ループ上で**行い、新 config 読込に失敗したら旧グラフを維持して稼働継続する。**プロセス終了→SCM 再起動は採らない**(下記)。

- **フォールバック**: サービス未起動は `Connect(timeout≈2s)` の `TimeoutException`、応答なしは read timeout(≈5s)。CLI は明示メッセージ + 非0 exit。GUI(#5)は制御 UI を無効化し「サービス未接続」表示に degrade し、クラッシュしない。

## なぜ named pipe か(HTTP loopback でない理由)

- **同一ユーザー認可が無償**: `PipeSecurity` で自 SID に限定すれば #33 の「ローカル同一ユーザー限定」が transport 層で成立。HTTP loopback はポート採番・トークンファイル・環境次第のファイアウォール確認を足すだけで、非目標のネットワーク到達性以外に得るものがない。
- **session 0 越えが自然に解決**: pipe 名前空間はマシングローバル。ADR-0008 が「session 0 越えの ACL 設計」を GUI 側へ押し込む懸念としていた点は、pipe では ACL 1 本に縮む。
- **追加依存ゼロ**: pipe クライアントは .NET 標準 API。ADR-0009 の WPF 単一 exe に追加ランタイムを持ち込まない。

## なぜ CLI クライアント先行か

ADR-0008 は「受け手なしのサービス側発行のみ」を EventLog で解いた。本 ADR の等価物が CLI クライアント verb で、**GUI 未着手のまま listener を完全に検証できる**。誰も繋がないパイプサーバという ADR-0008 の却下理由を、CLI クライアントが「常に繋ぐ第一の実装」として無効化する。

## なぜ reload をプロセス内再構築にするか(SCM 再起動でない理由)

- SCM の既定は**正常終了(exit 0)で再起動しない**。プロセス終了で再起動させるには回復オプション「失敗時に再起動」+ 非0 exit が要り、正常な reload が EventLog 上「サービス異常終了」に化ける。
- 不正 config で終了すると **再起動ループ**に陥る。プロセス内再構築なら新 config 読込失敗時に旧グラフを維持して稼働継続でき、reload だけ失敗を返せる。
- プロセスに閉じるためテスト可能(SCM 回復設定という外部状態に依存しない)。

## Considered Options

- **(採用) named pipe 要求/応答 + CLI クライアント先行 + reload=プロセス内再構築** — 同一ユーザー認可が ACL 1 本、GUI 不在で E2E 検証可、直列不変条件を保つ。
- **HTTP loopback(Kestrel/HttpListener)** — 契約は素直だがポート/トークン/ファイアウォールを足す。非目標のリモート到達性以外に利点なし。session 0 越えの追加考慮も。
- **GUI 送出まで本 issue に含める** — #5 未着手で送出側が検証不能。ADR-0008 の「境界を確定し片側だけ実装」と不整合。
- **reload = プロセス終了→SCM 再起動** — 一見単純だが SCM 回復設定依存・正常 reload の異常終了化・不正 config での再起動ループ。
- **reload = 項目別の部分 hot-swap** — 反映が速い項目があるが、worker に再構築分岐が増え ADR-0003 の直列不変条件を自壊させる。

## Consequences

- **要実機検証(Windows / self-hosted runner)**: WSL では named pipe が Unix ソケット実装で Windows ACL 非対応、`PipeSecurity` は `PlatformNotSupported`。ADR-0008 の notify ゲートと同様、**#33 完了条件「認可がローカル同一ユーザーに限定」は Windows 実機検証項目**になる。① `radirec.exe status`/`rescan`/`reload` が稼働サービスに繋がり応答すること、② 別ユーザー/ACL 外からは繋がらないこと、③ reload の新 config が在中スキャン完了後に反映され、不正 config で旧グラフが維持されること、を実機で確認する。
- `RadioMonitorWorker` に観測可能状態(スナップショット)と、グラフをプロセス内で差し替え可能にするリファクタ(`Build` を ctor 外へ)が入る。listener が触れる面は狭い `IServiceControl` 相当の interface に限定する。
- 契約 schema は `schemaVersion` で前方進化させる。GUI(#5)はこの JSON 契約に自前 DTO で追従し、両側 golden テストで守る。
- GUI からの制御送出 UI(#5)・深リンク・リモート制御(非目標)は本 ADR の対象外。
