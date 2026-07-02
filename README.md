# radirec-tool

「らじれこ」で録音したラジオ番組(M4A)を整理し、Jellyfin 音楽ライブラリ(NAS)へ
統合・配信する運用支援ツール。旧録音アプリ **radikool**(MP3)の過去分も取り込み、
単一ライブラリに束ねる。

- **整理**: ファイル名・メタデータを Jellyfin(Music)向けに正規化(1 番組 = 1 アルバム)。
- **統合**: らじれこ / radikool など複数ソースを import-once で NAS へ非破壊取り込み。
- **配信**: Jellyfin 経由でスマホから聴取。

## ステータス

**M1(コア + CLI 取り込み)実装済み**。`radirec ingest --once / --dry-run` を提供。
Jellyfin リフレッシュ(M2)/ Windows サービス常駐(M3)/ radikool 移行(M4)/ GUI(M5)は未実装。

## クイックスタート

```bash
# サンプル設定をコピーして自分の環境に合わせて編集
cp config/radirec.sample.json  radirec.json
cp config/stations.sample.json config/stations.json
cp config/programs.sample.json config/programs.json

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
- ファイル監視: FileSystemWatcher(Windows サービス, M3 予定)/ Jellyfin 連携: REST API(M2 予定)

## 構成

| プロジェクト | 役割 | 状態 |
|------|------|------|
| `Radirec.Core` | ドメインロジック(パース/正規化/import-once/タグ/出力ターゲット) | 実装済み |
| `Radirec.Cli` | 単発実行・開発・バッチ入口 | 実装済み |
| `Radirec.Service` | Windows サービス(I: 常時監視・本番) | 予定(M3) |
| `Radirec.Gui` | 配布フロントエンド兼編集 UI | 予定(M5) |
| `Radirec.Tests` | 単体・統合テスト | 実装済み |
