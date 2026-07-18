# Windows サービスを廃し、トレイ常駐 per-user モデルへ移行する

Status: accepted(常駐モデルの構造転換。ADR-0003/0005/0006/0007/0008/0009/0010/0011 を supersede/amend。C2〔`Gui→Core` 統合〕をこの移行で先行実施。**決定済み・実装は未着手/複数 PR**)

本番の実行形態を **Windows サービス(Session 0 常駐)** から **トレイ常駐(ユーザーセッション自動起動・非 admin の per-user)** へ移行する。取り込みワーカーを `Radirec.Gui`(トレイ)プロセスに畳み込み、`Radirec.Gui → Radirec.Core` を直接参照する。EventLog シグナル・named pipe 制御チャネル・run-as-user 資格情報という **Session 0 を橋渡しするための機構一式を撤去**する。

## 背景

当初は「録音を常時監視する = OS サービスが本筋」と考え、`radirec.exe service` を Windows サービスとして常駐させた(§3.1)。しかしサービスは **Session 0(非対話)** で動くため、対話デスクトップに直接 UI/トーストを出せず、マップドライブも見えない。この制約が、いま出荷済みのアーキテクチャの相当部分を**派生的に生んで**いた:

- **Session 0 で UI 不可** → 取込通知を EventLog シグナル(機械可読 JSON 契約)で発し、ユーザーセッションの受け手トレイが購読してトースト表示([ADR-0008](0008-ingest-notification-via-eventlog-signal.md) / [ADR-0009](0009-toast-receiver-unpackaged-wpf-tray.md))。**publisher/subscriber の 2 ツリー疎結合**はこの橋のために存在した。
- **受け手はサービスと独立に落ちうる** → 不在中サマリ・catch-up 参加・失敗シグナル de-dup(#54/#55/#58-61)という、**分割が生んだ問題への対処**が積み上がった。
- **サービス→ユーザーの逆方向制御が要る** → named pipe の制御チャネル([ADR-0010](0010-gui-service-control-via-named-pipe.md))。
- **マップドライブ不可視 + サービスアカウント** → NAS を UNC 固定にし、`New-Service -Credential` で自アカウント登録 + `SeServiceLogonRight`([ADR-0007](0007-distribution-via-powershell-scripts.md))。install が admin 必須になった。

**運用実態の確認**(本 ADR の起点): このツールは **単一ユーザーの母艦 1 台**で動く。録音元のらじれこは**ユーザーセッションのアプリ**で、録音後に PC を強制スリープさせる(§3.2)。**ログオフ中(セッション無し)の録音は発生しない**(ユーザー確認済み)。したがってサービスの目玉である「無人・ログオン前でも動く」利点は**この用途では実質的に空**である。空の利点のために、上記の Session 0 橋(通知契約・制御 pipe・資格情報ダンス・admin install)という恒常コストを払い続けていた。

トレイ常駐(ログオン自動起動のユーザーセッションプロセス)なら:

- **UNC/NAS アクセスは無料**(ユーザーとして動く)。資格情報ダンス不要。
- **直接トースト可能**。EventLog シグナル契約・受け手分割・pipe が不要になり in-process 呼び出しへ。
- 電源復帰キャッチアップ(§3.2 の主機構)は、ユーザーセッションプロセスでも `PowerModeChanged` を受けられるので**成立**する。撤去せず移設するだけ。
- **非 admin の per-user install** が可能になり、自己更新技術(Velopack 等)が解錠される([ADR-0013](0013-velopack-per-user-updates.md))。

## Considered Options

- **(採用) トレイ常駐 per-user へ移行し、Session 0 橋を撤去する** — 取り込みワーカーを `Radirec.Gui` プロセスに畳む(下記「ワーカーの居場所」)。通知・制御・履歴はすべて in-process。install/update は非 admin per-user([ADR-0013](0013-velopack-per-user-updates.md))。
  - **ワーカーの居場所 = トレイプロセスに畳む**(別ヘッドレスプロセス + 薄い UI は不採用)。別プロセスにすると、いま撤去する IPC を別形で再導入して逆行するため。1 つのユーザーセッション exe が「取り込み + トースト + 履歴 UI + 制御」を持つ。
- **現状維持(Windows サービス)** — 出荷済み・動作中だが、単一ユーザー用途では上記コストが**空の利点**に対して恒常的にかかる。更新 UX の改善(トレイからの online 更新・per-user パッケージング)を**サービスがブロック**し続ける。
- **段階移行(ホストだけ差し替え、EventLog/pipe は温存)** — 低リスクだが、**カスタム EventLog ソース登録が admin 必須**のため install が admin ゲートのまま残り、per-user パッケージングを解錠しない。目的(非 admin per-user 更新)を 1mm も達成しないため不採用。目的達成には EventLog 撤去(=直接トースト化)まで一括で行う必要がある。

**配布ゲートとの関係**: 本移行は「他人への per-user 配布(C3 / §14)」を**約束しない**。動機は単一ユーザー機での**偶発的複雑性の削減**(サービス/Session 0 分割が生んだ機構の撤去)である。結果として C3 は将来大幅に楽になる(トレイ常駐 + per-user が C3 の目標モデルそのもの)が、C3 のゲートは維持する。

## Consequences

- **C2 を先行実施(かつ C2 より大きい)**: これまでの「2 ツリー・共有 ProjectReference 無し・JSON 契約でのみ接続」という疎結合の不変条件を**意図的に退役**する。ただし C2 は本来「編集グリッドがタグ書込のため Core を呼ぶ」だけの参照だったのに対し、本移行は**監視・キャッチアップ・取り込みワーカー一式をトレイプロセスにホストする**もので、C2 より踏み込んだ統合になる。疎結合の存在理由(Session 0 橋)が消えるため首尾一貫している。
  - **参照の実体は `Radirec.Gui → Radirec.Hosting → Radirec.Core`**(当初「Gui→Core」と記したが不正確だった)。ワーカーは `CompositionRoot`(Hosting)に依存するため、Gui は Hosting を参照する。Hosting は移行後、**dev/batch CLI(`Radirec.Cli`)と常駐トレイ(`Radirec.Gui`)の 2 フロントエンドが共有する合成層**になる(だから Hosting は Cli に畳まず独立を保つ)。
  - **ワーカー `RadioMonitorWorker` は `Radirec.Cli` から `Radirec.Gui` へ移す**(Hosting ではなく)。理由: ワーカーは `SystemEvents.PowerModeChanged`(Windows 専用)に依存するため、Hosting に置くと Hosting が `net10.0-windows` 化し、それを参照する `Radirec.Tests`(net10.0・Linux CI)まで巻き添えになる。移行後ワーカーを host するのは Gui(既に `net10.0-windows`)だけなので、Gui に置けば Hosting/Core はクロスプラットフォームのまま保てる。共有が必要な `ConfigLoading` / `ServiceRuntimeOptions` は Cli → Hosting へ移す。
  - `Radirec.Gui.Core` は分離を維持するが役割が変わる: EventLog subscriber 契約(JSON DTO・購読 parse・制御 DTO)は削除され、**CI で走らせられない WinRT/WPF/WinForms シェルから隔離した「テスト可能な提示ロジック」(トースト文面整形・履歴行整形・版表示)** の置き場になる。
- **supersede/amend される決定**:
  - [ADR-0005](0005-single-exe-cli-service-merge.md)(**amend**): `radirec.exe` の `service` verb を廃止。`radirec.exe` は dev/batch の単発 CLI(`ingest`/`refresh`/`dry-run`)専用に縮小。単一 exe 原則は CLI 側で維持。
  - [ADR-0007](0007-distribution-via-powershell-scripts.md)(**supersede**): 更新は [ADR-0013](0013-velopack-per-user-updates.md) の per-user 自己更新へ。`New-Service -Credential` / `SeServiceLogonRight` / admin install / `deploy/*.ps1` は廃止。
  - [ADR-0008](0008-ingest-notification-via-eventlog-signal.md)(**supersede**): EventLog シグナル(EventID 1001/1002)の JSON 契約を退役。ワーカーが in-process で直接トースト。カスタム EventLog ソース登録(admin 要因)を撤去。
  - [ADR-0009](0009-toast-receiver-unpackaged-wpf-tray.md)(**amend**): トレイは「受け手(subscriber)」ではなく「取り込みワーカーを内包する常駐本体」になる。WPF/WinForms トレイ + WinRT トーストの選定は維持。**不在中サマリの機構は変質**(EventLog bookmark → 起動時 catch-up スキャン結果の集約。CONTEXT.md 参照)。bookmark / RecordId 単調ガード / catch-up 参加は削除。
  - [ADR-0010](0010-gui-service-control-via-named-pipe.md)(**supersede**): named pipe 制御チャネルを退役。rescan/reload/status はトレイメニューからの in-process 呼び出し。CLI の制御 verb(`status`/`rescan`/`reload`)は廃止(`rescan` は `radirec ingest --once` に吸収)。
  - [ADR-0006](0006-binary-data-separation-and-path-resolution.md)(**amend**): データを `%ProgramData%\radirec`(マシン共通)から `%APPDATA%\radirec`(安定 per-user・更新で消えない位置)へ。実行体は Velopack 管理下の versioned ディレクトリ。既存台帳(取込履歴)は一度きり移行する。
  - [ADR-0003](0003-catchup-over-sleep-race.md)(**amend**): 電源イベント購読(`PowerModeChanged`)とキャッチアップ機構は撤去せず、ユーザーセッションのワーカーへ移設。取りこぼしゼロの担保(catch-up + 冪等 + 中断耐性)は不変。
- **障害分離(fault isolation)の喪失 — 意図した代償**: publisher/subscriber 分割は Session 0 を橋渡しするだけでなく、**取り込みワーカーの生存を UI の安定から隔離**していた(いまは WPF/XAML の例外がサービスの取り込みに波及しない)。統合後は **UI バグ 1 つが取り込みも行うプロセスを巻き添えに落とす**。「録音を取りこぼさない」という中核の約束が **GUI スレッドの安定に結合**する。catch-up は**結果整合(eventual correctness)を回復するが、稼働(liveness)は保証しない**——トレイが落ちている間は取り込みも止まる。単一ユーザー機で自動起動 + 起動時 catch-up があれば許容と判断するが、これは非自明な robustness 上のトレードオフである。実装では UI 例外がワーカーを巻き込まないよう最大限分離する(ワーカーは UI スレッド外・未捕捉例外でプロセス即死させない等)。
- **リスク**: 数日前に出荷した M6 系(#54/#55/#58-61)の相当部分を作り直す。退行リスクと投資の破棄を伴う。ただし削る対象の多くは**サービス/Session 0 分割が生んだ偶発的複雑性**であり、正味では複雑性が減る。
- **コード署名/SmartScreen** は引き続き非目標(単一ユーザー機のため)。他人配布(C3)着手時に扱う。
