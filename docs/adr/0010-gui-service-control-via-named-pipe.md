# GUI→サービス制御は named pipe の要求/応答チャネルで行い、CLI クライアントで先行実装する

Status: proposed(#33 GUI→サービス制御チャネル・サービス側先行実装)

[ADR-0008](0008-ingest-notification-via-eventlog-signal.md) で取込通知を**サービス発・一方向**の EventLog シグナルに確定した。その設計中に、将来 GUI が**逆方向**にサービスへ指示を出す需要(手動再スキャン・設定リロード・ライブ状態取得)が判明した。EventLog は一方向のため、この用途には別チャネルが要る。ADR-0008 の非閉塞性(通知は EventLog のまま、制御は別チャネルで足せる)を前提に、本 ADR で GUI→サービスの制御チャネルを設計する。

送出側の本命 GUI(#5)は未着手で、トースト受け手 tracer slice([ADR-0009](0009-toast-receiver-unpackaged-wpf-tray.md))しか無い。ADR-0008 が named pipe を却下した理由の 1 つは「**誰も繋がないパイプサーバを作っても検証できない**」だった — 本 ADR はこの制約に正面から答える必要がある。

## 決定

- **transport = named pipe(固定名 `radirec-control`)。** 単一ユーザー homelab の Windows デスクトップ(ADR-0009)で非目標がリモート制御(#33)である以上、HTTP loopback の唯一の強み(ネットワーク到達性)は非目標そのもの。pipe 名前空間はマシングローバルなので **session 0 越え**(サービス Session 0 ↔ ユーザーセッション GUI)が transport 層で自然に解決し、`PipeSecurity` で同一ユーザー認可を無償で得られる。

- **サービス側 listener + CLI クライアント verb を先行実装し、GUI 配線は #5 へ繰延する。** ADR-0008 が EventLog publisher を Event Viewer で単体検証できたのと同じ構図を作る。`radirec.exe rescan` / `radirec.exe status` / `radirec.exe reload` の CLI クライアント verb が「手動制御」機能そのものであり、かつ **GUI 不在でも listener を同一マシンで E2E 単体検証する手段**になる(別プロセスの `radirec.exe status` を `radirec.exe service` に繋ぐ)。#33 完了条件の「GUI からコマンド送出」は本 ADR では「CLI クライアントから送出」と読み替え、GUI 送出 UI は #5 本体で載せる。

- **listener は `service` verb のホストのみに載せる。** `RadioMonitorWorker` とは別の HostedService として登録し、対話 `ingest` / `refresh` verb には載せない(ADR-0008 の「サービス経路のみ」と一貫)。listener は worker の内部実装ではなく、DI singleton の**狭い仲介 interface**(`IServiceControl` 相当)だけに依存する。worker がそこへ状態を publish しチャネル書込口を expose、listener がそこを参照する。

- **認可 = 操作者 SID を許可する `PipeSecurity`。** 既定インストール(`deploy/install.ps1` の `-ServiceAccount`。既定 `.\$env:USERNAME`、内部で `New-Service -Credential $cred` を呼ぶ)ではサービスも操作者自身のアカウントで走るため、サービス = GUI = 同一ユーザーが成立し「ローカル同一ユーザー限定」がそのまま満たされる。よって既定構成では `NamedPipeServerStream` を `WindowsIdentity.GetCurrent().User`(= 自 SID)に FullControl・他は暗黙拒否の ACL で作れば足りる。
  - **ただし split-privilege 構成に注意**(ADR-0009 の `_TrayCommon.ps1` 注記と同じ落とし穴): `-ServiceAccount` に NAS 書込用の専用アカウントを指定すると **サービス SID ≠ 対話ログオン中の操作者 SID** になり、自 SID 限定 ACL は**操作者本人の GUI/CLI を自分のパイプから締め出す**。認可の対象は「サービス自身」ではなく「**制御を出す操作者ユーザー**」なので、ACL は**操作者ユーザーの SID に接続許可を与える**のが正。既定構成ではこれが自 SID と一致するため自 SID で足りるが、split-privilege では操作者 SID を明示付与する(設定 or インストール時に操作者アカウントを渡す)必要がある。この差異を実装・配布スクリプトで吸収する。
  - サーバ成りすまし(pipe squatting)対策のクライアント側サーバ所有者 SID 照合は単一ユーザー homelab では脅威が低く**任意**。

- **契約 = `schemaVersion` 付き JSON envelope。** ADR-0008/0009 と一貫。request `{ schemaVersion, command, args }` / response `{ schemaVersion, ok, error, result }`。**型共有ポリシーは相手で分ける**: CLI クライアントは `radirec.exe` と同一バイナリ(ADR-0005)なのでサービス側 C# 型をそのまま共有してよい(同一アセンブリ版でドリフト不能)。別 exe の GUI(#5, ADR-0009)は跨プロセス型を共有せず、JSON 契約に対し自前 DTO を持ち `schemaVersion` で互換判定する。サービス側 request/response 型は `Radirec.Hosting`、GUI 側 DTO は通知契約を持つ `Radirec.Gui.Core` に相乗り。両側に golden JSON の契約テストを置く。

- **プロトコル = 1 コマンド 1 接続の単発往復。** connect → request 1 通 → response 1 通 → close。1 通は **4 バイト長プレフィックス(big-endian Int32)+ UTF-8 JSON のフレーミング**で区切る。named pipe の Message モードは Unix(WSL 開発/CI)で非対応のため **Byte モード**上の自前フレーミングにし、round-trip をクロスプラットフォームで検証可能にする(ADR-0008 の EventLog と同じ「今日検証できる形」を優先)。1 接続内でのコマンド多重化・常設接続・バックプレッシャは持たない。数コマンド/操作の頻度に対し接続ごとに状態を持たない方が listener が単純で、途中切断は「次の接続で作り直す」だけで済む。listener は 1 接続処理の例外でその接続を閉じるだけで listen を継続し、1 クライアントの不正が本体取込を巻き込まない。**ただし単一インスタンスの直列 accept は、遅いコマンド(reload の同期 config 読込等)が他の status/rescan を凍結させる(#43)。よって少数(既定 4)の並行 accept ループにする** — `IServiceControl` の各メソッドはスレッドセーフ(rescan=Channel・status=volatile 読み・reload=`Interlocked`)なので並行 dispatch は安全。

- **全コマンドを非閉塞にする(在中スキャンをブロックしない)。**
  - **status**: worker が保持する**スレッドセーフな状態スナップショット**(稼働状態・監視ルート・最終スキャン完了時刻とトリガ種別・直近の取込/失敗件数)を直列チャネルを介さず直接読んで即応。worker は現状最終スキャン時刻を保持しないため、この観測可能状態を新設する。
  - **rescan**: 既存の要求チャネル(`BoundedChannel` 容量1・DropWrite で coalesce・ADR-0003)へ 1 本書いて即「受理」を返す。スキャン完了は待たない。既存の単一直列コンシューマにトリガ種別「手動」が 1 つ増えるだけ。
  - **reload**: listener スレッドで**新 config の読込+検証だけ同期実行**(worker のランタイムに触れない)し、妥当なら**再構築を要求してから**即「受理」、不正なら即エラーで**旧構成を維持**。この reload 要求は **rescan と同じ cap-1・DropWrite の要求チャネルには乗せない**(下記)。

- **reload の反映 = worker の config 由来ランタイムを丸ごとプロセス内で作り直す(部分 hot-swap はしない)。** `radirec.json` の項目はグラフ(`IngestService`)だけでなく **worker が `_config` から直接作る FileSystemWatcher(`RadioRoot`)・定期タイマー(`ScanIntervalSeconds`)・debounce タイマー(`WatchDebounceSeconds`)にも焼き込まれている**。グラフだけ差し替えると watcher/タイマーが旧 config のまま残り**部分反映**になる(新ルートの録音が定期スキャンまで検知されない等)ので、reload は **これら config 由来ランタイム一式(グラフ + watcher + 両タイマー)を破棄→新 config で再構築**する = 本当の「実質再起動相当」にする。項目別の hot-swap は「どの項目で何を作り直すか」の分岐を worker に増やし ADR-0003 の直列不変条件を自壊させるので採らない。
  - **reload 要求の届け方**: rescan は**冪等**(coalesce されても「1 回スキャンする」意図は満たされる)なので cap-1・DropWrite チャネルで正しいが、reload は**冪等でない** — DropWrite チャネルに乗せると在中スキャンがスロットを占有している間に `TryWrite` が false で**黙って捨てられ、しかも「受理」を返す**サイレント config-loss になる。よって reload は**取りこぼさない別経路**で届ける(専用フラグ/別チャネル/`pending reload config` を差す volatile 参照等、確実に単一直列ループへ伝わる手段)。実際の破棄→再構築は**在中スキャン完了後に直列ループ上で**行い(ADR-0003 の直列不変条件を保つ)、新 config 読込・再構築に失敗したら旧構成を維持して稼働継続する。**プロセス終了→SCM 再起動は採らない**(下記)。

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

- **要実機検証(Windows / self-hosted runner)**: WSL では named pipe が Unix ソケット実装で Windows ACL 非対応、`PipeSecurity` は `PlatformNotSupported`。ADR-0008 の notify ゲートと同様、**#33 完了条件「認可がローカル同一ユーザーに限定」は Windows 実機検証項目**になる。① `radirec.exe status`/`rescan`/`reload` が稼働サービスに繋がり応答すること、② 別ユーザー/ACL 外からは繋がらないこと、③ reload で config 由来ランタイム一式(グラフ + watcher + タイマー)が在中スキャン完了後に反映され RadioRoot 変更が即時検知に効くこと・不正 config で旧構成が維持されること、④ split-privilege(`-ServiceAccount` に専用アカウント)構成でも操作者本人の CLI が繋がること(既定は自 SID 限定で足りるが split-privilege ACL は #40 で対応)、⑤ listener がパイプ生成の一過性失敗(dispose→再生成の窓での "all pipe instances busy" 等)で永久停止せず backoff 再試行で listen を継続すること、を実機で確認する。
- `RadioMonitorWorker` に観測可能状態(スナップショット)と、**config 由来ランタイム(グラフ + FileSystemWatcher + 定期/debounce タイマー)を一括で破棄→再構築できるリファクタ**(`CompositionRoot.Build` と `StartWatcher`/タイマー生成を ctor から `ExecuteAsync` 側の再入可能な組立へ移す)が入る。reload 要求は cap-1 DropWrite チャネルとは別の取りこぼさない経路(`PendingReloadSlot`)で届ける。listener が触れる面は狭い `IServiceControl` 相当の interface に限定する。
- **reload の再構築は build-new-first**(新グラフを先に構築し、成功時のみ旧構成を破棄→差し替え)で失敗時に旧構成を無傷で維持する。`SqliteLedger` は `journal_mode=WAL` かつ `Pooling=false` なので、同一 `LedgerPath` の一時的な二重オープン(build-new-first)も Dispose 後のロック解放も既存設計で安全(WAL が並行オープンを許容・pooling 無効で即時解放)。ただしこの点と SQLite 再オープン挙動は Windows 実機で最終確認する。
- **テスト境界の明示**: reload の配送不変条件(`PendingReloadSlot` の原子 take・最新勝ち・起こし)は Hosting 層で単体テスト済み(WSL 可)。だが**再構築の atomicity(build-new-first + watcher/タイマー張り直し + SQLite 再オープン)は `RadioMonitorWorker`(`net10.0-windows`・テストプロジェクトから参照不可)にあり Windows 手動検証項目**。「テスト green」= reload 核まで網羅、と誤読しないこと(listener 永久停止バグを隠したのと同じ盲点)。特に**⑥ 録音ファイルの着信(FileSystemWatcher イベント)が in-flight の最中に reload してもサービスが落ちないこと**(mid-life の watcher/タイマー teardown と threadpool コールバックの競合)を実機で確認する。
- 契約 schema は `schemaVersion` で前方進化させる。GUI(#5)はこの JSON 契約に自前 DTO で追従し、両側 golden テストで守る。
- GUI からの制御送出 UI(#5)・深リンク・リモート制御(非目標)は本 ADR の対象外。
