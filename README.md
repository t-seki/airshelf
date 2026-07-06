# radirec-tool

「らじれこ」で録音したラジオ番組(M4A)を整理し、Jellyfin 音楽ライブラリ(NAS)へ
統合・配信する運用支援ツール。旧録音アプリ **radikool**(MP3)の過去分も取り込み、
単一ライブラリに束ねる。

- **整理**: ファイル名・メタデータを Jellyfin(Music)向けに正規化(1 番組 = 1 アルバム)。
- **統合**: らじれこ / radikool など複数ソースを import-once で NAS へ非破壊取り込み。
- **配信**: Jellyfin 経由でスマホから聴取。

## ステータス

**M1(コア + CLI 取り込み)/ M2(Jellyfin リフレッシュ)/ M3(Windows サービス常駐)/ M4-A(radikool 移行)実装済み**。
単一 exe + install スクリプト + タグ駆動 Release を提供([最新リリース](https://github.com/t-seki/radirec-tool/releases))。
ソース跨ぎのアルバム統合(M4-C)/ GUI 配布(M5)は未実装。

## インストール・更新(Windows・運用)

常駐運用は [Releases](https://github.com/t-seki/radirec-tool/releases) の zip を使います(ランタイム不要の単一 exe。
実行体 = `C:\Program Files\radirec\`、データ = `%ProgramData%\radirec\`)。zip を展開し、**管理者 PowerShell** で:

```powershell
# 新規: EventLog 登録 → 設定生成 → サービス登録(自分のアカウント。冪等)
.\deploy\install.ps1
# → %ProgramData%\radirec\radirec.json を編集して Start-Service Radirec

# 更新: 停止 → radirec.exe 差し替え → 再開(認証不要・設定/台帳はそのまま)
.\deploy\update.ps1

# 削除: サービス/EventLog/exe を削除(データは残す。-PurgeData で全消し)
.\deploy\uninstall.ps1
```

新版は `git tag -a vX.Y.Z -m "…" && git push origin vX.Y.Z` で CI が Release に zip を自動添付します。
導入・更新・移行の詳細は [利用ガイド §6–8](docs/usage.md)。

## 開発時の実行(SDK から)

```bash
# サンプル設定をコピー(相対パスは radirec.json のあるディレクトリ基準で解決される)
cp config/radirec.sample.json  radirec.json
cp config/stations.sample.json stations.json
cp config/programs.sample.json programs.json

# まず dry-run(副作用なしで取り込み予定を確認)→ 実行
dotnet run --project src/Radirec.Cli -- ingest --config radirec.json --dry-run
dotnet run --project src/Radirec.Cli -- ingest --config radirec.json
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
- タグ操作: TagLib# / 台帳: SQLite / CLI: Spectre.Console.Cli
- ファイル監視: FileSystemWatcher(Windows サービス, M3)/ Jellyfin 連携: REST API(M2)

## 構成

| プロジェクト | 役割 | 状態 |
|------|------|------|
| `Radirec.Core` | ドメインロジック(パース/正規化/import-once/タグ/出力ターゲット) | 実装済み |
| `Radirec.Hosting` | 合成ルート + 環境設定・パス解決(Core と入口の橋渡し) | 実装済み |
| `Radirec.Cli` | 単一 exe `radirec.exe`(CLI + 常駐サービス。`ingest`/`refresh`/`service`) | 実装済み |
| `Radirec.Gui` | 配布フロントエンド兼編集 UI | 予定(M5) |
| `Radirec.Tests` | 単体・統合テスト | 実装済み |
