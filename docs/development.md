# 開発ガイド

## 一次資料

- [`docs/requirements.md`](requirements.md) — 要件定義・設計判断(トポロジー、import-once、案A タグ、
  複数ソース、配布境界など)。**設計の根拠はここ**。
- [`CONTEXT.md`](../CONTEXT.md) — ドメイン用語集(放送回 / 録音ファイル / 同一性キー 等)。
- [`docs/usage.md`](usage.md) — 利用者向けの使い方。

## 前提

- **.NET 10 SDK**。`Core` / `Cli` / `Tests` はクロスプラットフォームなので、開発・ビルド・テストは
  **WSL/Linux でも可能**です(Windows 固有 API を使わない)。実行時のパス(`I:`, UNC)だけが Windows 前提。
- テストフィクスチャ(`tests/Radirec.Tests/Fixtures/silent.m4a`)は著作権フリーの無音 M4A で、
  リポジトリに含まれています(再生成には ffmpeg が必要ですが通常は不要)。

## ビルドとテスト

```bash
dotnet build Radirec.slnx        # ソリューション全体
dotnet test  Radirec.slnx        # 全テスト(xUnit)
dotnet test  Radirec.slnx --filter "FullyQualifiedName~RajirekoImporter"  # 絞り込み
```

> ソリューションは新形式 `Radirec.slnx`(.NET 10 既定)です。NuGet は Central Package Management
> (`Directory.Packages.props`)で版を一元管理、共通ビルド設定は `Directory.Build.props`。

## プロジェクト構成

```
src/Radirec.Core/    ドメインロジック(OS 非依存・テスト可能)
src/Radirec.Cli/     Spectre.Console.Cli の薄い入口(合成ルート + コマンド)
tests/Radirec.Tests/ xUnit
```

将来 `Radirec.Service`(Windows サービス, M3)/ `Radirec.Gui`(配布フロントエンド, M5)を
Core のみ依存で追加します。参照は単方向(`Cli → Core` / `Tests → Core`)。

### Radirec.Core の主な型

| 名前空間 | 型 | 責務 |
|----------|----|----|
| `Model` | `RecordingFile` / `Episode` / `IdentityKey` / `ParsedSource` / `NormalizedTags` | ドメインの値・エンティティ |
| `Sources` | `ISourceImporter` / `RajirekoImporter` | ファイル名・フォルダから生情報を抽出 |
| `Normalization` | `TextNormalizer` / `StationResolver` / `ProgramResolver` / `EpisodeAssembler` / `TagMapper` | 正規化・同一性キー構築・案A タグ算出 |
| `Output` | `IOutputTarget` / `LocalFolderTarget` / `ITagWriter` / `TagLibTagWriter` / `ITagReader` | 非破壊コピー・タグ読み書き |
| `Ledger` | `ILedger` / `SqliteLedger` / `NullLedger` / `LedgerEntry` | 冪等性の記録(SQLite/WAL) |
| `Ingestion` | `IRecordingSource` / `StabilityChecker` / `RecordingSkipPolicy` / `IngestService` | 走査・安定判定・オーケストレーション |

### 取り込みの制御フロー(`IngestService.RunOnceAsync`)

```
列挙 → スキップ判定(ジャンク) → 対応判定(CanHandle) → 安定判定
  → パース → 正規化解決 → 同一性キー
  → 冪等判定(台帳にあればスキップ)
  → [dry-run ならここで停止・副作用ゼロ]
  → アトミック非破壊コピー(.partial → タグ → move) → 台帳記録(最後)
```

要点(壊さないための不変条件):
- **台帳記録は必ず最後**。半端な状態で「取り込み済み」にしない。
- **`.partial` は拡張子を保つ**(`foo.partial.m4a`)。TagLib# は拡張子で形式判定するため。
- **temp は最終と同一ディレクトリ**に置く(クロスボリューム move は非アトミック)。
- **disc/track は `ushort`**。16bit 制約を型で守り `track=YYYYMMDD` 統合を不可能化。
- **date はフル ISO を `©day` に直書き**(`Tag.Year` は使わない。年で上書きされるため)。
- **1 ファイルの失敗は走査全体を止めない**(`Failed` 扱いで継続)。

## 拡張ポイント

- **新しい録音ソース**(例 radikool, M4): `ISourceImporter` を実装し、`(放送局, 番組, 放送日)` を
  抽出できるようにする。出力・台帳・正規化はそのまま再利用できる。
- **新しい出力先**(例 Jellyfin リフレッシュ付き, M2): `IOutputTarget` を実装(`LocalFolderTarget` を
  ラップしてスキャン起動を足す形)。

## コーディング/コミット規約

- **Conventional Commits**(`feat:` / `fix:` / `docs:` / `chore:` …)。
- **Issue 駆動**: コミット/PR に `Refs #N` / `Closes #N`。設計判断の経緯は docs に残す。
- **ワークフロー**: main からブランチ → 実装 → コミット → PR → コードレビュー(`/code-review`)→ 指摘対応 → マージ。
- 個人固有値(パス・NAS・局マスタ)は **config のみ**。コードにハードコードしない(将来の配布のため)。

## E2E 手動確認(参考)

無音フィクスチャを らじれこ風フォルダに置いて CLI を通す手順:
1. `{root}/{局}/{番組}/[{局}]{番組}(TimeFree)_{YYYYMMDDHHMMSS}.m4a` にフィクスチャをコピー。
2. `radirec.json` の `RadioRoot`/`OutputRoot`/`LedgerPath` を一時ディレクトリに向ける。
3. `ingest --dry-run`(副作用ゼロ)→ `ingest`(生成)→ 再実行(全て「済」= 冪等)を確認。
