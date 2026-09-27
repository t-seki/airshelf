# radirec-tool

「らじれこ」で録音したラジオ番組(M4A)を整理し、Jellyfin 音楽ライブラリ(NAS)へ
統合・配信する運用支援ツール。旧録音アプリ **radikool**(MP3)の過去分も取り込み、
単一ライブラリに束ねる。

- **整理**: ファイル名・メタデータを Jellyfin(Music)向けに正規化(1 番組 = 1 アルバム)。
- **統合**: らじれこ / radikool など複数ソースを import-once で NAS へ非破壊取り込み。
- **配信**: Jellyfin 経由でスマホから聴取。

## ステータス

**M1(コア + CLI 取り込み)/ M2(Jellyfin リフレッシュ)/ M4-A(radikool 移行)実装済み**(M4-A の移行用の `ingest --root` は [ADR-0020](docs/adr/0020-cli-as-agent-front-via-control-channel.md) で CLI から削除済み)。
常駐は **トレイ常駐 per-user モデル**(`Radirec.Gui`)で、取り込みワーカーをトレイプロセスが内包し、
取込通知は in-process トースト、制御はトレイメニュー、状態と取込履歴・番組の設定(編集と再適用)・設定ファイル・更新の確認は管理画面(トレイの「Radirec を開く」)、更新は Velopack のアプリ内自己更新で行う
([ADR-0012](docs/adr/0012-retire-service-for-tray-resident-per-user.md) /
[ADR-0013](docs/adr/0013-velopack-per-user-updates.md)。旧 Windows サービス/EventLog は退役。named pipe は、CLI の `radirec status` / `reload` がトレイへ頼む経路として形を変えて戻した:[ADR-0020](docs/adr/0020-cli-as-agent-front-via-control-channel.md))。
配布は **Velopack の per-user インストーラ(非 admin)+ タグ駆動 Release**
([最新リリース](https://github.com/t-seki/radirec-tool/releases))。
ソース跨ぎのアルバム統合(M4-C)/ 編集 GUI(M5)は未実装。

## インストール・更新(Windows・per-user・非 admin)

[Releases](https://github.com/t-seki/radirec-tool/releases) の **`Radirec-win-Setup.exe`** をダウンロードして実行
するだけ(**管理者不要**)。`%LOCALAPPDATA%\Radirec` へ導入し、ログオン自動起動を登録して起動します。
データ(設定・台帳・マスタ・ログ)は `%LOCALAPPDATA%\RadirecData\` に置かれ、初回起動時に設定を sample から
生成します(`RadioRoot` / `OutputRoot` / Jellyfin を編集)。

- **更新**: 管理画面のバージョン情報ページ「今すぐ更新を確認」→「更新を適用して再起動」(保留中はトレイからも適用できる。
  起動時・定期にも自動チェック)。
  private repo のため、更新フィード読取用トークンを `radirec.json` の `UpdateToken` に設定する。
- **アンインストール**: 設定 → アプリ →「Radirec」、または `%LOCALAPPDATA%\Radirec\Update.exe --uninstall`。
- **取込通知**: 新規取込時にトレイが in-process でトースト表示(管理画面の設定ページで止められる。取込失敗のトーストは常に出る)。

新版は `git tag -a vX.Y.Z -m "…" && git push origin vX.Y.Z` で CI(`vpk`)が Setup.exe/更新フィードを Release へ
発行します。導入・運用・**旧環境からの移行**の詳細は
[利用ガイド §6](docs/usage.md) と [移行 runbook](docs/migration-to-velopack.md)。

## 開発時の実行(SDK から)

```bash
# 設定は常に %LOCALAPPDATA%\RadirecData\ に置く(CLI も常駐トレイもここを読む。別の場所を指すオプションは無い)。
# トレイを一度起動すると見本から作られる。手で置くなら config/*.sample.json を
# radirec.json / stations.json / programs.json としてそこへコピーする
# (相対パスは radirec.json のあるディレクトリ基準で解決される)。

# まず dry-run(副作用なしで取り込み予定を確認)→ 実行
dotnet run --project src/Radirec.Cli -- ingest --dry-run
dotnet run --project src/Radirec.Cli -- ingest   # 常駐トレイに走査を頼み、終わるまで待つ(トレイが動いていなければ終了コード 2)
```

詳しくは [利用ガイド](docs/usage.md)。

## ドキュメント

| ドキュメント | 対象 |
|------|------|
| [利用ガイド](docs/usage.md) | 使い方(セットアップ・実行・設定・alias) |
| [開発ガイド](docs/development.md) | ビルド・テスト・構成・拡張・規約 |
| [要件定義](docs/requirements.md) | 設計判断の一次資料 |
| [用語集](CONTEXT.md) | ドメイン用語(グロッサリ) |

## 技術スタック

- C# / .NET 10(実行は Windows ネイティブ)
- タグ操作: TagLib# / 台帳: SQLite / CLI: Spectre.Console.Cli / 配布・自己更新: Velopack
- ファイル監視: FileSystemWatcher(トレイ常駐のワーカー)/ Jellyfin 連携: REST API / 通知: WinRT トースト

## 構成

| プロジェクト | 役割 | 状態 |
|------|------|------|
| `Radirec.Core` | ドメインロジック(パース/正規化/import-once/タグ/出力ターゲット) | 実装済み |
| `Radirec.Hosting` | 合成ルート + 環境設定・パス解決(Core と入口の橋渡し。CLI と常駐トレイが共有) | 実装済み |
| `Radirec.Cli` | 単一 exe `radirec.exe`。普段の運用と AI エージェントの窓口(`ingest`/`reapply`/`refresh`/`history`/`attention`/`status`/`reload`。ADR-0020)。`ingest`・`reapply`・`status`・`reload` は常駐トレイに頼む | 実装済み |
| `Radirec.Cli.Core` | `Radirec.Cli` のテスト可能な核(コマンド・`--json` の形・終了コード) | 実装済み |
| `Radirec.Gui` | **常駐トレイ**(独立 exe)。取り込みワーカーを内包し、通知/制御/管理画面(取込履歴・番組の設定を含む)/自己更新を担う(ADR-0012) | 実装済み(管理画面は 5 ページとも実装済み) |
| `Radirec.Gui.Core` | `Radirec.Gui` のテスト可能な提示ロジック(トースト整形・履歴行整形・版表示・状態の文言)と管理画面の ViewModel | 実装済み |
| `Radirec.Tests` | 単体・統合テスト | 実装済み |
