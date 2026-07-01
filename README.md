# radirec-tool

「らじれこ」で録音したラジオ番組(M4A)を整理し、Jellyfin 音楽ライブラリ(NAS)へ
統合・配信する運用支援ツール。旧録音アプリ **radikool**(MP3)の過去分も取り込み、
単一ライブラリに束ねる。

- **整理**: ファイル名・メタデータを Jellyfin(Music)向けに正規化(1 番組 = 1 アルバム)。
- **統合**: らじれこ / radikool など複数ソースを import-once で NAS へ非破壊取り込み。
- **配信**: Jellyfin 経由でスマホから聴取。

## ステータス

要件定義フェーズ。設計の一次資料は [`docs/requirements.md`](docs/requirements.md)。

## 技術スタック

- C# / .NET(Windows ネイティブ実行)
- タグ操作: TagLib# / 監視: FileSystemWatcher(Windows サービス)
- 台帳: SQLite / Jellyfin 連携: REST API

## 構成(予定)

| プロジェクト | 役割 |
|------|------|
| `Radirec.Core` | ドメインロジック(パース/正規化/import-once/タグ/出力ターゲット) |
| `Radirec.Cli` | 単発実行・開発・バッチ入口 |
| `Radirec.Service` | Windows サービス(I: 常時監視・本番) |
| `Radirec.Gui` | (将来)配布フロントエンド兼編集 UI |
| `Radirec.Tests` | 単体テスト |

詳細は要件定義書を参照。
