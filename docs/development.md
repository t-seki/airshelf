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

## リリース(タグ → GitHub Release)

配布はタグ駆動の CI(`.github/workflows/release.yml`)で行う([ADR-0005](adr/0005-single-exe-cli-service-merge.md) /
[ADR-0007](adr/0007-distribution-via-powershell-scripts.md))。**版はタグが唯一の源**で、CI が
`-p:Version=<tag>` を注入する(タグ無しのローカルビルドは `Directory.Build.props` の `0.0.0-dev`)。

```bash
git tag v1.2.0 && git push origin v1.2.0   # → workflow が発火
```

ワークフローは `dotnet test` → `dotnet publish`(win-x64 自己完結・両 exe を 1 フォルダ)→
**`vpk`(Velopack)で pack + GitHub Release へ upload**(Setup.exe / full+delta nupkg / portable /
更新フィード)、まで自動化する(ADR-0013)。以後の配布・更新は Velopack の Setup.exe とアプリ内更新で
行う(利用手順は [usage.md §6](usage.md)、移行は [migration-to-velopack.md](migration-to-velopack.md))。

**ランナー**: `runs-on: [self-hosted, windows, x64, homelab]`(母艦 Windows PC の self-hosted runner)。
トリガはタグ push のみ(PR ビルドは self-hosted で走らせない)。ランナーの導入手順は **別リポジトリ
`t-seki/homelab`** の runbook にある(このリポジトリには無い):
<https://github.com/t-seki/homelab/blob/main/docs/runbooks/github-actions-runner.md>。
要点は repo ごとに `C:\actions-runner\radirec-tool\` へ runner を展開し
`--labels "self-hosted,windows,x64,homelab" --runasservice` で登録、`actions/checkout` のため
Git for Windows を PATH に置く、こと。

## プロジェクト構成

```
src/Radirec.Core/    ドメインロジック(OS 非依存・テスト可能)
src/Radirec.Hosting/ 合成ルート + 環境設定・パス解決(Core 依存)
src/Radirec.Cli/     単一 exe radirec.exe。Spectre.Console.Cli の入口 + service verb(常駐)
tests/Radirec.Tests/ xUnit
```

CLI と常駐サービスは単一 exe `radirec.exe` に統合済み(`ingest`/`refresh`/`service`。[ADR-0005](adr/0005-single-exe-cli-service-merge.md))。
参照は単方向(`Cli → Hosting → Core` / `Tests → Core, Hosting` / `Gui → Gui.Core`)。`Radirec.Gui`(独立 exe)は
取込通知トレイとして実装済み([ADR-0009](adr/0009-toast-receiver-unpackaged-wpf-tray.md))。将来の配布フロントエンド兼編集 UI(M5)は同プロジェクトへ後載せ。

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

**トレイ常駐(現行)の E2E**: 合成無音 M4A を隔離設定で流してトースト経路を検証する
`deploy/e2e-tray-toast.ps1`(本番資産に触れない)。旧環境からの移行検証は
[migration-to-velopack.md](migration-to-velopack.md)。

> **⚠ 以下の「不在中サマリ / 取込失敗の EventLog 検証」は退役機構の手順**
> ([ADR-0012](adr/0012-retire-service-for-tray-resident-per-user.md))。EventLog シグナル(1001/1002)・
> `Write-EventLog` 注入・bookmark・不在中サマリ集約はいずれも in-process 化で撤去済み。歴史的記録として残す。

### 不在中サマリの実機検証(軸A / A2 / #54)

EventLog の bookmark/RecordId 挙動は Windows 実機依存で WSL では回せない。核となるフェーズ境界は
必ず母艦で確認する(純粋ロジック=マージ/整形/単調ガードは WSL 単体テストで担保済み)。

前提: サービス稼働中・トレイ配置済み(`install.ps1`)。ログは `%LocalAppData%\Radirec\gui.log`、
チェックポイントは `%LocalAppData%\Radirec\subscription.json`。

- [ ] **通常回収**: トレイを終了(タスクトレイから終了 or プロセス kill)→ 取込を **2 回**発生させる
      (別番組/同一番組を混ぜる)→ トレイを再起動 → **不在中サマリのトーストが 1 件だけ**出て、
      表題「不在中の取込 N件」の N が **2 回分の合計と一致**すること。個別トーストが 2 件出ないこと
- [ ] **境界の非二重計上**: 上記直後にもう一度トレイを再起動 → **不在中サマリが出ない**
      (提示済み分が RecordId ガードで再提示されない)こと
- [ ] **ライブ継続**: 再起動後にトレイ常駐のまま取込を 1 回発生 → 通常の「新規取込 N件」トーストが出て、
      `subscription.json` の `lastRecordId` が進むこと
- [ ] **初回起動の縮退**: `subscription.json` を削除 → トレイ再起動 → 過去分の一括再生が**出ない**
      (read-from-now で開始)こと
- [ ] **破損 bookmark の縮退**: `subscription.json` を不正 JSON に書き換え → トレイ再起動 → クラッシュせず、
      `gui.log` に縮退の warn が残り、read-from-now で開始すること
- [ ] **ログクリア後もライブ通知が死なない**(回帰の要): **有効な** `subscription.json` がある状態で
      Event Viewer から Application ログをクリア(RecordId が 1 に戻る)→ トレイ再起動 → 取込を 1 回発生
      → **通常の「新規取込 N件」トーストが出る**こと(旧 RecordId baseline がライブ通知を潰さない)

すべて満たしたら #54 の実機検証を完了とし #54 を close してよい。

### 取込失敗通知の実機検証(軸A / A3 / #58・#59)

EventLog の 1002 経路は Windows 実機依存(WSL 不可)。純粋ロジック(発行側 de-dup・受け手のマージ/整形/
EventID 振り分け)は WSL 単体テスト済み。母艦で振る舞いを確認する。

**合成注入**(実取込失敗を待たずに検証): 1001 と同様、`Write-EventLog` で **EventID 1002** の失敗シグナルを
注入できる。JSON は発行側 `EventLogIngestFailurePublisher.BuildPayload` と同形:

```powershell
function Send-RadirecFailure {
  param([int]$Count=1,[string]$Name='[X局]番組(TimeFree)_20250229123000.m4a',[string]$Reason='パースに失敗しました')
  $json = '{{"schemaVersion":1,"failedCount":{0},"truncated":false,"files":[{{"name":"{1}","reason":"{2}"}}]}}' -f $Count,$Name,$Reason
  Write-EventLog -LogName Application -Source Radirec -EntryType Warning -EventId 1002 -Message $json
}
```

- [ ] **ライブ失敗**: トレイ稼働中に 1002 注入(or 壊れた録音で実失敗)→ **「取込失敗 N件」トースト**が出て、
      ファイル名 + 理由の行が見えること(成功トーストとは別に出る)
- [ ] **成功/失敗の分岐**: 1001 と 1002 を続けて注入 → 「新規取込」トーストと「取込失敗」トーストが**別々に**出る
- [ ] **不在中の失敗**: トレイ停止 → 1002 を 2 件注入 → トレイ再起動 → **「不在中の失敗 N件」トースト 1 本**に集約
- [ ] **成功と失敗の混在(不在中)**: 停止中に 1001 と 1002 を両方注入 → 再起動 → **成功 1 本 + 失敗 1 本の 2 トースト**
      (別シグナル = 別通知)
- [ ] **de-dup**(実失敗経由): 壊れた録音を置いたまま periodic スキャンを 2 回起こす → 失敗トーストは**初回のみ**
      (同じ失敗が毎走査で鳴らない)。`gui.log` / サービスログで確認

すべて満たしたら #58・#59 の実機検証を完了とし両 issue を close してよい。**リリースは #58 + #59 が
両方マージされてから 1 本**(publisher 単体配布は 1002 を表示するトレイが無く失敗が無言で消えるため不可)。
