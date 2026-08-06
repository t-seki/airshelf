# 開発ガイド

## 一次資料

- [`docs/requirements.md`](requirements.md) — 要件定義・設計判断(トポロジー、import-once、案A タグ、
  複数ソース、配布境界など)。**設計の根拠はここ**。
- [`CONTEXT.md`](../CONTEXT.md) — ドメイン用語集(放送回 / 録音ファイル / 同一性キー 等)。
- [`docs/usage.md`](usage.md) — 利用者向けの使い方。

## 前提

- **.NET 10 SDK**。`Core` / `Hosting` / `Gui.Core` / `Tests` は `net10.0` でクロスプラットフォーム。
  `Cli`(`net10.0-windows`)と `Gui`(`net10.0-windows10.0.x`)も、両 csproj の `EnableWindowsTargeting`
  により **WSL/Linux でコンパイルできます**。つまり**ソリューション全体のビルドとテストは WSL で完結**します。
  実行については両者で違います — **`Gui` は WPF/WinForms のランタイムが無いので Linux では起動しません**が、
  **`Cli` は Linux でも起動します**(`dotnet radirec.dll --help` 等)。ただし本番の実行環境は Windows 前提で、
  出荷物は win-x64 self-contained、config が指すパス(`I:`, UNC)も Windows のものです。
- テストフィクスチャ(`tests/Radirec.Tests/Fixtures/silent.m4a`)は著作権フリーの無音 M4A で、
  リポジトリに含まれています(再生成には ffmpeg が必要ですが通常は不要)。

## ビルドとテスト

```bash
dotnet build Radirec.slnx        # ソリューション全体(Windows 専用プロジェクト含む)
dotnet test  Radirec.slnx        # 全テスト(xUnit)
dotnet test  Radirec.slnx --filter "FullyQualifiedName~RajirekoImporter"  # 絞り込み
```

> ソリューションは新形式 `Radirec.slnx`(.NET 10 既定)です。NuGet は Central Package Management
> (`Directory.Packages.props`)で版を一元管理、共通ビルド設定は `Directory.Build.props`。

**CI(`.github/workflows/ci.yml`)は PR と main への push で次を走らせます** — 手元で同じコマンドを
打てば CI と同じ検証になります(フラグは不要):

```bash
dotnet build Radirec.slnx -c Release -warnaserror
dotnet test  Radirec.slnx -c Release --no-build
```

CI は **GitHub-hosted の Linux runner** で動きます。`release.yml` が「PR ビルドは self-hosted で
走らせない」(母艦 Windows PC を PR のコードに晒さない)と決めているためで、その判断を維持したまま
PR を検証するための構成です。**`-warnaserror` は CI のみ**で、リリース(`release.yml`)には入れません —
警告で出荷が止まるのを避けるためです。

### 依存の更新・脆弱性(Dependabot)

`.github/dependabot.yml` が **NuGet** と **GitHub Actions** を週次で見て、更新を PR として出します(#106)。
上がってきた PR は上記 CI がそのままビルド + テストします。

- **NuGet**: patch/minor は 1 PR にまとめ、major は個別(破壊的変更を見落とさないため)。
- **GitHub Actions**: `release.yml` / `ci.yml` は action を **commit SHA で pin** していますが、
  Dependabot は SHA とバージョンコメントをまとめて張り替えます(手作業だと更新が途切れるため)。
  > ⚠ **`release.yml` に触れる更新 PR だけは、マージ前に SHA を手で確認してください。**
  > Dependabot はワークフローファイル単位で対象を絞れないため、`ci.yml` と `release.yml` の pin を
  > 同じ PR で書き換えます。ところが **`release.yml` はタグ push でしか起動しない**ので、
  > PR の CI がグリーンでも release.yml 側は何も検証されていません。release.yml は
  > **self-hosted runner + `contents: write`** で動く(= 最高権限)ため、ここだけは
  > 「更新時に人が新 SHA を確かめる」という SHA pin 本来の前提を残します。
  > 確認は `gh api repos/actions/checkout/git/ref/tags/v7.0.1` などで、コメントのタグが指す SHA と
  > diff の SHA が一致するかを見ます。
- **なぜ CI で落とさないか**: `-warnaserror` は MSBuild の全警告を落とすので、NuGetAudit の
  脆弱性警告(NU1901-1904)もエラーになります。それだと**こちらのコードを変えていない PR が、
  依存に新規 CVE が登録された日に落ちる**(直せるのは依存の更新待ち)。そこで
  `Directory.Build.props` の `WarningsNotAsErrors` で NU19xx を除外し、代わりに脆弱性は
  Dependabot が PR として能動的に届けます。**ログに出して誰も読まない状態にしない**のが要点です。

手元で今すぐ確認したいときは:

```bash
dotnet list Radirec.slnx package --vulnerable --include-transitive
```

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
src/Radirec.Core/     ドメインロジック(OS 非依存・テスト可能)
src/Radirec.Hosting/  合成ルート + 環境設定・パス解決 + 制御(Control/)。Cli と Gui が共有する
src/Radirec.Cli/      単一 exe radirec.exe。dev/batch の単発 CLI(ingest / refresh / organize-source)
src/Radirec.Gui/      本番の常駐本体 Radirec.Gui.exe。取り込みワーカー + トレイ + 通知 + 履歴 UI + 自己更新
src/Radirec.Gui.Core/ Gui のテスト可能な提示ロジック(WinRT/WPF/WinForms シェルから隔離)
tests/Radirec.Tests/  xUnit
```

**本番の常駐は `Radirec.Gui`(トレイ)**で、`radirec.exe` は dev/batch の単発 CLI に縮小されている
([ADR-0012](adr/0012-retire-service-for-tray-resident-per-user.md)。`service` verb は廃止)。
参照グラフは 2 経路で Core に届く:

```
Radirec.Gui ──> Radirec.Gui.Core ──> Radirec.Core
     └────────> Radirec.Hosting  ──> Radirec.Core
Radirec.Cli ──> Radirec.Hosting / Radirec.Core
Radirec.Tests ──> Radirec.Core / Radirec.Hosting / Radirec.Gui.Core
```

- `Radirec.Gui` 自体に `Radirec.Core` への直接 ProjectReference は**無い**が、Core の型は推移参照で
  Gui のコンパイル面に乗っている(ADR-0012 の「参照の実体は `Gui → Hosting → Core`」)。
- `Radirec.Hosting` は **CLI と常駐トレイの 2 フロントエンドが共有する合成層**。だから Cli に畳まず
  独立を保つ。制御(rescan/reload/status)の型も `Hosting/Control/` にあり、トレイが in-process で叩く。
- `Radirec.Core` / `Hosting` / `Gui.Core` / `Tests` は `net10.0`(Linux CI でビルド/テスト可能)、
  `Cli` は `net10.0-windows`、`Gui` は `net10.0-windows10.0.x`。ワーカーが Windows 専用 API
  (`SystemEvents.PowerModeChanged`)に依存するため、ワーカーは Hosting ではなく Gui に置いてある。
- 将来の編集 UI(M5)は `Radirec.Gui` へ後載せ(§[roadmap](roadmap.md) 軸C・保留中)。

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

> **EventLog 依存の検証手順は削除しました**([ADR-0012](adr/0012-retire-service-for-tray-resident-per-user.md))。
> かつてここには EventLog bookmark / `subscription.json` / RecordId 単調ガードの確認と、
> EventID 1002 を `Write-EventLog` で注入する手順がありましたが、**その機構はコードに存在しません**(#72 で撤去)。
> 判断の記録は [ADR-0009](adr/0009-toast-receiver-unpackaged-wpf-tray.md) /
> [ADR-0011](adr/0011-ingest-failure-as-separate-eventlog-signal.md) / ADR-0012 が持ちます。
> **ただし検証したかった振る舞いのうち、下記は移行後も生きている**ので現行機構向けに書き直しました。

### 通知の実機検証(トレイ常駐・現行)

`deploy/e2e-tray-toast.ps1` は「単発の取込 → トースト 1 件」の疎通だけを見ます。以下は
**複数走査・複数ファイルにまたがる振る舞い**で、スクリプトの範囲外なので母艦で手動確認します
(純粋ロジック = 要約の集約・文面整形・de-dup ストアは WSL の単体テストで担保済み)。

前提: `e2e-tray-toast.ps1` の隔離設定(本番資産に触れない)。**EventLog への注入は不要** — 実ファイルを
置いてスキャンを起こすだけで再現できます。壊れた録音は「非閏年 2/29」等のパース不能な名前で作れます
(`tests/Radirec.Tests/IngestServiceTests.cs` の `BadFile()` 参照)。

**不在中サマリ**(1 走査 = 最大 1 要約。[CONTEXT.md](../CONTEXT.md) が語彙の権威):
- [ ] **起動時集約**: トレイを終了 → 有効な録音を **2 件**(別番組/同一番組を混ぜる)置く → トレイ起動 →
      **トーストが 1 件だけ**出て、件数が 2 と一致すること。個別トーストが 2 件出ないこと
- [ ] **二重計上しない**: 上記直後にもう一度トレイを再起動 → **トーストが出ない**こと(台帳が取込済みを記録)
- [ ] **ライブ継続**: 常駐したまま録音を 1 件足す → 通常の「新規取込 1件」トーストが出ること

**取込失敗の de-dup**([ADR-0011](adr/0011-ingest-failure-as-separate-eventlog-signal.md) が
「要実機検証」と名指ししている振る舞い。ストアは `IngestFailureStore`(SQLite)で現行も生きている):
- [ ] **初回のみ通知**: 壊れた録音を置いたまま定期スキャンを **2 回**起こす → 失敗トーストは**初回だけ**
      (同じ失敗が毎走査で鳴らない)
- [ ] **再起動を跨いで継続**: 上記の後にトレイを再起動 → 同じ失敗で**再通知されない**こと
- [ ] **成功で解除**: 壊れた録音を正常な名前に直す → 取り込まれ、以後その失敗の抑制が解けること
- [ ] **成功と失敗は別トースト**: 正常な録音と壊れた録音を同時に置いて 1 走査 →
      「新規取込」と「取込失敗」が**別々に**出ること(別シグナル = 別通知)
