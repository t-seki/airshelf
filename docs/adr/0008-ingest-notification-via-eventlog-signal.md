# 取込通知はサービス発の EventLog シグナルで発し、受け手(GUI)から疎結合する

Status: proposed(#19 取込通知・サービス側先行実装)

常駐サービス(`radirec.exe service`)は**セッション 0(非対話)**で動くため、`ToastNotificationManager` を直接呼んでも対話デスクトップにトーストは出せない([issue #19](https://github.com/t-seki/radirec-tool/issues/19) 背景)。トーストは「ログオン中のユーザーセッション」で出す必要があり、**ユーザーセッション上の受け手**が要る。その受け手は M5 のデスクトップ/GUI アプリ(#5)だが、GUI はまだ着手しておらずフレームワークも未選定(requirements §12)。

よって #19 では、**サービス↔GUI をまたぐ IPC 境界(=最もひっくり返しにくい部分)だけを確定**し、サービス側の発行のみ先行実装する。受け手は後付けする。

## 決定

- **サービスは 1 回の取り込み走査で新規取込(`Imported≥1`)が発生したとき、Windows イベントログ(Application / Source `Radirec`)へ専用 `EventID=1001` の「取込要約シグナル」を 1 件書く。** 取込済スキップは除外し、起動時キャッチアップの大量取込も 1 走査=1 シグナルに集約する(スパム防止)。
- **発行は専用の `EventLog.WriteEntry` 経路**で行い、ILogger/`EventLogLoggerProvider` には相乗りしない。通知は「機能」であって「ログ」ではなく、logging 設定(EventLog 無効化・レベルフィルタ変更)の巻き添えで黙って壊れてはならないため。同じ Source `Radirec` を使うので追加のソース登録は不要。
- **ペイロードは `schemaVersion` を持つ JSON envelope**をイベントメッセージ本文に格納する。番組(アルバム)単位でグルーピングし、ドメインレベルの識別のみを載せる(Jellyfin item ID は載せない):

  ```json
  { "schemaVersion": 1, "totalImported": 7, "truncated": false,
    "albums": [ { "station": "ニッポン放送", "program": "オールナイトニッポン0",
      "episodes": [ { "broadcastDate": "2026-07-09", "partIndex": 0 } ] } ] }
  ```

  `albums` は先頭 20 件・各 `episodes` は先頭 20 件を上限とし、超過時 `truncated=true`(`totalImported` は実数を保持)。イベントメッセージの ~32KB 制約に対しサイズを有界化する。
- **発行ロジックは Core に置く。** `IIngestSummaryPublisher`(transport 非依存の interface)と `IngestSummary`(番組単位グループの domain record)を Core に定義し、`IngestService` に注入。`refresh` の直後・`Imported≥1` のときだけ発行し、**失敗はログのみ・取込は巻き戻さない**([ADR-0003](0003-catchup-over-sleep-race.md) の `ILibraryRefresher` と同方針)。EventLog 実装(`EventLogIngestSummaryPublisher`)は Hosting 側に置き、Core は EventLog を知らない。
- **有効/無効は `radirec.json` の `NotificationsEnabled`(bool・既定 `true`)で切替える。**
- **受け手(GUI)** は Application ログを `Source=Radirec & EventID=1001` でフィルタし `EventLogWatcher` で購読、ペイロードを parse してネイティブトーストを表示する(#5 で実装)。深リンクは GUI 側で番組名から Jellyfin を引いて解決する将来課題(本 ADR のペイロードには含めない)。

## なぜ EventLog か(pipe でない理由)

#19 の今回スコープは**受け手なしのサービス側発行のみ**。この制約が決定的:

- **EventLog は今日ビルドして検証できる唯一の機構**。専用 EventID の発行は Event Viewer で単体確認でき、契約(EventID + JSON schema)を GUI 不在のまま完全に規定できる。
- **Named pipe は重い設計判断を先送りする GUI 側へ押し込む**(誰がサーバを持つか / セッション 0 越えの ACL / 再接続・バックプレッシャ)。誰も繋がないパイプサーバを作っても検証できない。
- **疎結合と永続**: EventLog は発行者が受け手の生死に依存せず、GUI 未起動でもエントリが残りキャッチアップ購読が可能。pipe の唯一の利点(即時 push)は数件/日のトースト要約には無意味。
- **非閉塞性**: 一方向通知を EventLog にしても、将来 GUI→サービスの**制御**チャネル(再スキャン指示・ライブ状態)を別途 pipe/HTTP で足す道は塞がらない(別 issue で扱う)。

## Considered Options

- **(採用) EventLog 専用 EventID シグナル + 専用 WriteEntry 経路** — 追加インフラ最小(既存 Source を流用)、跨セッション購読は `EventLogWatcher` で解決済み、永続・キャッチアップ無料。契約が logging 設定から独立。
- **ILogger に相乗り** — コード最小だが契約が logging 設定に従属し、EventID マップ挙動が Windows 実機依存で WSL 開発では検証できない。
- **Named pipe** — 即時 push・JSON 素直だが、跨セッション ACL とサーバ寿命管理という設計コストが、先送りする GUI 側に落ちる。GUI 未起動時のメッセージ消失も。
- **ファイルスプール(`%ProgramData%` へ JSON drop → FileSystemWatcher)** — 永続・跨セッション自明だが掃除ポリシーが要り、EventLog という既存チャネルがあるのに新経路を増やす。
- **タスクスケジューラ経由の暫定トースト** — #19 非目標。GUI で本実装する方針。

## Consequences

- **要実機検証(Windows / self-hosted runner)**: 非 admin のサービスアカウントから Source `Radirec` へ任意 EventID で `WriteEntry` できること(初回ソース登録は admin 権限。サービス登録側で `Radirec` ソースが事前登録済みである前提を確認する)。Application ログは一般ユーザーが読めるため受け手側 ACL は問題にならない見込み。
- Core に副作用依存が 1 つ増える(`IIngestSummaryPublisher`)。既存の `ILibraryRefresher` と対称なので設計上の異物感はない。
- ペイロード schema は `schemaVersion` で前方進化させる。GUI 実装(#5)はこの EventID と schema を契約として購読する。
- GUI→サービスの制御チャネルは本 ADR の対象外。別 issue で設計する。
