# バイナリとデータを分離し、設定内の相対パスは設定ファイル基準で解決する

Status: accepted(#16 配布・インストール整備)

移行(#15)後の本番は、実行体・台帳・設定を **`C:\radirec\` に同居**させていた(`radirec.json` / `ledger.db`(+ WAL/SHM)/ `stations.json` / `programs.json` がすべて同一フォルダ、`radirec.json` 内のパスはすべて絶対)。この同居は更新時に台帳や設定をバイナリ差し替えに巻き込む事故リスクを持つ。また設定内の相対パスは実行時 CWD 基準で解決されるため、サービスの既定 CWD(System32)では相対指定が破綻する潜在バグを抱えていた(本番は全パス絶対で回避していた)。

よって以下に分離・変更する:

- **バイナリ** = `C:\Program Files\radirec\radirec.exe`
- **データルート** = `%ProgramData%\radirec\`(`radirec.json` / `ledger.db` / `stations.json` / `programs.json`)
- **設定内の相対パス**(`LedgerPath` / `StationsFile` / `ProgramsFile`)は **設定ファイル自身のディレクトリ基準**で解決する(従来の CWD 基準を廃止)。
- exe は引数省略時、**`%ProgramData%\radirec\radirec.json`** を既定の設定として探す(従来の `AppContext.BaseDirectory` 隣接ではなく)。
- サービス登録は従来どおり設定を**絶対パスで明示注入**する(`radirec.exe service --config "C:\ProgramData\radirec\radirec.json"`)。発見ロジックには依存しない。

更新時はバイナリのみ差し替え、データルートは触らない。台帳のスキーマ移行はサービス起動時に自動実行される([ADR-0004](0004-part-index-dedup-for-timeless-sources.md))。

## なぜ後方互換か

`Path.IsPathRooted` により絶対パスは解決基準の変更を素通りする。本番の `radirec.json` は全パス絶対のため**挙動不変**。CWD→設定ディレクトリ基準の変更は、これまで壊れていた相対指定側だけを修正する。生成する `.sample` は相対パス(裸のファイル名)にでき、データルート配下で自己完結・可搬になる。

## Considered Options

- **(採用) 設定内相対パス = 設定ディレクトリ基準 + 既定発見 = ProgramData** — 絶対パスは無風、相対だけ修正。生成 config が可搬になり install スクリプトが絶対パスを文字列組み立てする責務を持たない。データルートごと移動しても壊れない。
- **生成 config に絶対 ProgramData パスを書き込む(現行流儀の踏襲)** — シンプルだが install スクリプトがパス組み立ての責務を負い、可搬性を失う。
- **CWD 基準のまま維持** — サービスの System32-CWD 破綻が残り、全パス絶対指定の運用制約を強い続ける。

## Consequences

- 本番の `C:\radirec\` レイアウトから新レイアウトへの**一度きり移行**が必要。生きた台帳(`ledger.db` + WAL + SHM)は import-once 冪等の唯一の拠り所であり、新データルートへ**無傷で運ばない限り NAS へ全アーカイブ再コピー**を招く。移行は保守スクリプト化せず、`docs/usage.md` の runbook として正確な手順を残す(Stop-Service → `sc delete` → db/wal/shm + json を 3 点セットで ProgramData へ移動 → config のパスを裸名に書換 → 新 install → 旧 `C:\radirec\` はバックアップ保持)。
- `CompositionRoot` / 設定ローダのパス解決基準を CWD から設定ディレクトリ基準へ変更する。
- Release アセットに**実 `radirec.json` は含めない**(Jellyfin API キーを含む個人設定。`.sample` のみ配布)。
