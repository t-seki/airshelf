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
  **`Cli` は Debug ビルドなら Linux でも起動します**
  (`dotnet src/Airshelf.Cli/bin/Debug/net10.0-windows/airshelf.dll --help` 等)。**Release ビルドは起動しません** —
  `Airshelf.Cli.csproj` が Release のときだけ `RuntimeIdentifier=win-x64` / `SelfContained=true` にするので、
  `bin/Release/net10.0-windows/win-x64/airshelf.dll` を `dotnet` で起動すると `libhostpolicy.so` が見つからずに終了します。
  WSL で CLI を動かして確かめるときは Debug ビルドを使ってください。ただし本番の実行環境は Windows 前提で、
  出荷物は win-x64 self-contained、config が指すパス(`I:`, UNC)も Windows のものです。
- テストフィクスチャ(`tests/Airshelf.Tests/Fixtures/silent.m4a`)は著作権フリーの無音 M4A で、
  リポジトリに含まれています(再生成には ffmpeg が必要ですが通常は不要)。

## ビルドとテスト

```bash
dotnet build Airshelf.slnx        # ソリューション全体(Windows 専用プロジェクト含む)
dotnet test  Airshelf.slnx        # 全テスト(xUnit)
dotnet test  Airshelf.slnx --filter "FullyQualifiedName~RajirekoImporter"  # 絞り込み
```

> ソリューションは新形式 `Airshelf.slnx`(.NET 10 既定)です。NuGet は Central Package Management
> (`Directory.Packages.props`)で版を一元管理、共通ビルド設定は `Directory.Build.props`。

**CI(`.github/workflows/ci.yml`)は PR と main への push で次を走らせます** — 手元で同じコマンドを
打てば CI と同じ検証になります(フラグは不要):

```bash
dotnet build Airshelf.slnx -c Release -warnaserror
dotnet test  Airshelf.slnx -c Release --no-build
```

CI は **GitHub-hosted の Linux runner** で動きます。`release.yml` が「PR ビルドは self-hosted で
走らせない」(母艦 Windows PC を PR のコードに晒さない)と決めているためで、その判断を維持したまま
PR を検証するための構成です。**`-warnaserror` は CI のみ**で、リリース(`release.yml`)には入れません —
警告で出荷が止まるのを避けるためです。

### 依存の更新・脆弱性(Renovate)

依存の更新は **Renovate** が PR として出します(#252 で Dependabot から移行。以前の `.github/dependabot.yml` は無い)。
設定は `renovate.json` で、共通プリセット `t-seki/renovate-config` を継承します。上がってきた PR は上記 CI が
そのままビルド + テストします。

プリセット(`default.json`。`gh api repos/t-seki/renovate-config/contents/default.json` で読める)の内容:

- ベースは `config:best-practices`(GitHub Actions の digest pin を含む)。
- 実行は週次(月曜 9 時前, Asia/Tokyo)。PR には `dependencies` ラベル、Dependency Dashboard も有効。
- **minor/patch**: 1 つのグループ(`minor and patch updates`)にまとめ、**CI が通れば自動マージ**(`platformAutomerge`)。
- **pin / pinDigest / digest**: 同じく CI が通れば自動マージ。
- **major**: 自動マージせず、人がレビューする(破壊的変更を見落とさないため)。

この repo の `renovate.json` が上乗せしているもの:

- `pin.enabled: true`。
- **GitHub Actions**(`matchManagers: github-actions`)は `automerge: false`、`pinDigests: true`。
  プリセットの自動マージ(minor/patch、digest)を上書きして、**Actions の更新は全部人がマージ**します。
  `release.yml` / `ci.yml` は action を **commit SHA で pin** していて、Renovate が SHA とバージョンコメントを
  まとめて張り替えます(手作業だと更新が途切れるため)。
  > ⚠ **`release.yml` に触れる Actions の更新 PR は、マージ前に SHA を手で確認してください。**
  > `ci.yml` と `release.yml` の pin は同じ PR で書き換わりますが、**`release.yml` はタグ push でしか
  > 起動しない**ので、PR の CI がグリーンでも release.yml 側は何も検証されていません。release.yml は
  > **self-hosted runner + `contents: write`** で動く(= 最高権限)ため、自動マージを外して
  > 「更新時に人が新 SHA を確かめる」という SHA pin 本来の前提を残しています。
  > 確認は `gh api repos/actions/checkout/git/ref/tags/v7.0.1` などで、コメントのタグが指す SHA と
  > diff の SHA が一致するかを見ます。
- NuGet は上乗せなし(プリセットのとおり)。

未確認: Renovate の GitHub App がこの repo で実際に動いているか、`platformAutomerge` が効く前提(repo の
auto-merge 設定、branch protection の必須チェック)が整っているかは、ここからは読めていません。
実際の挙動は Dependency Dashboard と過去の Renovate PR で確かめてください。

- **なぜ CI で落とさないか**: `-warnaserror` は MSBuild の全警告を落とすので、NuGetAudit の
  脆弱性警告(NU1901-1904)もエラーになります。それだと**こちらのコードを変えていない PR が、
  依存に新規 CVE が登録された日に落ちる**(直せるのは依存の更新待ち)。そこで
  `Directory.Build.props` の `WarningsNotAsErrors` で NU19xx を除外し、代わりに脆弱性は
  Renovate が PR として能動的に届けます。**ログに出して誰も読まない状態にしない**のが要点です。

手元で今すぐ確認したいときは:

```bash
dotnet list Airshelf.slnx package --vulnerable --include-transitive
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
行う(利用手順は [usage.md §6](usage.md)、移行は [migration-to-airshelf.md](migration-to-airshelf.md)。旧 admin 配置からは [migration-to-velopack.md](migration-to-velopack.md))。

**ランナー**: `runs-on: [self-hosted, windows, x64, homelab]`(母艦 Windows PC の self-hosted runner)。
トリガはタグ push のみ(PR ビルドは self-hosted で走らせない)。ランナーの導入手順は **別リポジトリ
`t-seki/homelab`** の runbook にある(このリポジトリには無い):
<https://github.com/t-seki/homelab/blob/main/docs/runbooks/github-actions-runner.md>。
要点は repo ごとに `C:\actions-runner\<repo>\` へ runner を展開し(この repo の runner は改名前に登録した
`C:\actions-runner\radirec-tool\` のまま。repo-level の登録なので、`t-seki/airshelf` への改名後もそのまま動く。#256)
`--labels "self-hosted,windows,x64,homelab" --runasservice` で登録、`actions/checkout` のため
Git for Windows を PATH に置く、こと。

## プロジェクト構成

```
src/Airshelf.Core/     ドメインロジック(OS 非依存・テスト可能)
src/Airshelf.Hosting/  合成ルート + 環境設定・パス解決 + 制御(Control/)。Cli と Gui が共有する
src/Airshelf.Cli/      単一 exe airshelf.exe。普段の運用とエージェントの窓口(ingest / reapply / refresh / history / attention / status / reload)。Program.cs だけの薄い入口
src/Airshelf.Cli.Core/ Cli のテスト可能な核(コマンド・--json の形・終了コード・CommandApp の設定。net10.0)
src/Airshelf.Gui/      本番の常駐本体 Airshelf.Gui.exe。取り込みワーカー + トレイ + 通知 + 管理画面(XAML)+ 自己更新
src/Airshelf.Gui.Core/ Gui のテスト可能な提示ロジックと管理画面の ViewModel(WinRT/WPF/WinForms シェルから隔離)
tests/Airshelf.Tests/  xUnit
```

**本番の常駐は `Airshelf.Gui`(トレイ)**で([ADR-0012](adr/0012-retire-service-for-tray-resident-per-user.md)。`service` verb は廃止)、
`airshelf.exe` は普段の運用と AI エージェントの窓口([ADR-0020](adr/0020-cli-as-agent-front-via-control-channel.md))。
台帳を書き換える `ingest` / `reapply` と、`status` / `reload` はトレイの制御チャネルへ頼む(トレイが動いていなければ終了コード 2)。
そのため、開発機でトレイを動かさずに CLI だけで**取り込み**を試せるのは `ingest --dry-run` まで(ADR-0020 の帰結。
`refresh` / `history` / `attention` はトレイなしでも動く。`reapply` は確認も含めてトレイに頼む。#181)。
参照グラフは 2 経路で Core に届く:

```
Airshelf.Gui ──> Airshelf.Gui.Core ──> Airshelf.Core
     │                 └───────────> Airshelf.Hosting
     └────────> Airshelf.Hosting  ──> Airshelf.Core
Airshelf.Cli ──> Airshelf.Cli.Core ──> Airshelf.Hosting / Airshelf.Core
Airshelf.Tests ──> Airshelf.Core / Airshelf.Hosting / Airshelf.Gui.Core / Airshelf.Cli.Core
```

`Airshelf.Cli`(exe)は `net10.0-windows` で、Release では win-x64 の自己完結 exe になるので、Linux の CI から
テストで参照できない。コマンドと終了コード・JSON の形は `Airshelf.Cli.Core`(`net10.0`)に置き、テストはそちらを
参照する(#178。`Airshelf.Gui` / `Airshelf.Gui.Core` と同じ分け方)。Spectre.Console.Cli を参照するのは `Airshelf.Cli.Core`
だけで、トレイと共有する `Airshelf.Hosting` には入れない。

- `Airshelf.Gui` 自体に `Airshelf.Core` への直接 ProjectReference は**無い**が、Core の型は推移参照で
  Gui のコンパイル面に乗っている(ADR-0012 の「参照の実体は `Gui → Hosting → Core`」)。
- `Gui.Core → Hosting` は、管理画面の ViewModel が制御面(`Hosting/Control/` の `IServiceControl` /
  `ServiceStatusSnapshot`)をそのまま受け取るための辺(#151)。Gui.Core 側にインタフェースを切らないのは、
  写しとアダプタを制御面の変更のたびに二重に直すことになるため。
- 管理画面は WPF + XAML + MVVM(`CommunityToolkit.Mvvm`)。ViewModel は `Gui.Core/Management/` に置いて
  WSL の CI でテストし、`Gui` の XAML とコードビハインドはバインドとマウス・キー操作・タイマーだけの薄いシェルにする。
  XAML(`ManagementWindow.xaml`)も `EnableWindowsTargeting` により Linux でコンパイルされる(起動はしない)。
- `Airshelf.Hosting` は **CLI と常駐トレイの 2 フロントエンドが共有する合成層**。だから Cli に畳まず
  独立を保つ。制御(rescan/reload/status)の型も `Hosting/Control/` にあり、トレイが in-process で叩く。CLI の
  `status` / `reload` は、同じ `Hosting/Control/` の制御チャネル(named pipe。`ControlPipeServer` / `ControlPipeClient`)で
  トレイへ頼む(#179・[ADR-0020](adr/0020-cli-as-agent-front-via-control-channel.md))。
- `Airshelf.Core` / `Hosting` / `Gui.Core` / `Tests` は `net10.0`(Linux CI でビルド/テスト可能)、
  `Cli` は `net10.0-windows`、`Gui` は `net10.0-windows10.0.x`。ワーカーが Windows 専用 API
  (`SystemEvents.PowerModeChanged`)に依存するため、ワーカーは Hosting ではなく Gui に置いてある。
- 編集 UI(M5)は作らないと決めた(#5、2026-09-28。§[roadmap](roadmap.md) 軸C)。

### Airshelf.Core の主な型

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
  → 冪等判定(台帳にあればスキップ。同一性キー / ソースパス / 内容ゲート ADR-0014)
  → 差し替え放送の判定(ファイル名の番組名。正規化解決で決めた扱いを見る。ADR-0021)
       判断待ち → 保留(`Held`。取り込まない)
       取り込まない → `SkippedByDecision`
       別の番組として取り込む → アルバムは判断のアルバム名(正規化解決の段で決まっている)
  → 配置先の算出(同名衝突は音声部分で同じ内容か判定。ADR-0015)
       同じ内容 → 書かずに取込済み(`済`)+ 台帳記録(出力パスは既存ファイル。dry-run は記録しない)
       違う内容 → ` HHmm` / ` 2` を付けた別名(結果に警告)
  → [dry-run ならここで停止・副作用ゼロ]
  → アトミック非破壊コピー(.partial → タグ → move) → 台帳記録(最後)

全ファイルの後(dry-run はしない):
  → Jellyfin リフレッシュ → 取込要約シグナル → 取込失敗のストアの片付け・取込失敗シグナル
  → 保留のストアの片付け(保留でなくなった・取込元から消えた記録を消す)→ 初めて保留した録音を記録 → 保留シグナル
```

要点(壊さないための不変条件):
- **台帳記録は必ず最後**。半端な状態で「取り込み済み」にしない。
- **出力先の同名衝突は、同じ内容と確かめられたときだけ書かずに済ませる**。判定できなければ
  違う内容として別名で書く(欠落 > 重複。[ADR-0015](adr/0015-output-collision-same-audio-check.md))。
- **`.partial` は拡張子を保つ**(`foo.partial.m4a`)。TagLib# は拡張子で形式判定するため。
- **temp は最終と同一ディレクトリ**に置く(クロスボリューム move は非アトミック)。
- **disc/track は `ushort`**。16bit 制約を型で守り `track=YYYYMMDD` 統合を不可能化。
- **date はフル ISO を `©day` に直書き**(`Tag.Year` は使わない。年で上書きされるため)。
- **1 ファイルの失敗は走査全体を止めない**(`Failed` 扱いで継続)。
- **差し替え放送の判定は冪等判定の後**。取り込み済みの回は判断待ちでも `済` にする(導入した直後に過去の回をまとめて保留に
  しない。取り込み済みの判断待ちは要対応が台帳から見つける。[ADR-0021](adr/0021-substitute-broadcast-held-by-filename-program-name.md))。
- **保留のストアは記録してから保留シグナルを発行する**(取込失敗のストアは発行してから記録する)。保留のストアは要対応の元でも
  あるので、発行に失敗しても記録は残す。片付けは、列挙が 0 件の走査と、安定待ち(`SkippedUnstable`)の録音ではしない。
- **保留は失敗ではない**。`airshelf ingest` の終了コードに数えない。

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

## Supervisor

`/supervise`(dotfiles の共通 skill)が読む repo 固有の手順。

### worktree の準備

なし。git 管理外の個人設定(`config/programs.json` / `config/stations.json`)は、ビルドにもテストにも使われない。

### マージ前の検証

なし。CI(`.github/workflows/ci.yml`、GitHub-hosted Linux)が PR ごとに
`dotnet build Airshelf.slnx -c Release -warnaserror` と `dotnet test` を走らせ、手元で同じコマンドを
打つのと同じ検証になる([ビルドとテスト](#ビルドとテスト))。`Airshelf.Gui` は WSL/Linux で起動できないので、
GUI を触る PR は、**実機で見てほしい点を PR 本文に書かせる**。

### マージの承認

自動。ただし**次のパスを触る PR は承認制**:

- `src/Airshelf.Gui/` — WSL で起動できず、CI では画面を確かめられない。実機で見てから入れる
- `src/Airshelf.Core/Ledger/` — 台帳スキーマとマイグレーション。壊れると既存の台帳に影響する
- `.github/workflows/` — 中の 2 本はどちらも自動では入れられない:
  - `ci.yml` は自動マージの条件にしている CI そのもの。変えると検証が緩みうる
  - `release.yml` は母艦の self-hosted runner + `contents: write` で動き、PR の CI では検証されない
    ([依存の更新・脆弱性](#依存の更新脆弱性renovate)を参照)

### マージ後の確認(デプロイ)

なし。配布はタグ駆動(`release.yml`、[リリース](#リリースタグ--github-release))で、main へのマージだけでは
本番(トレイ常駐の Windows 環境)に入らない。**リリースのタグ付けは Supervisor の手順に含めず、人が判断する**。

## ドキュメント保守 — 当時の記録に事実誤りが見つかったとき(#113 で確立)

ADR や requirements の「決定当時の記録」に、後から見て事実として誤っている記述が見つかることがある。
**書き換えるか残すかは、その記述が置かれている場所で決める**:

| 場所 | 扱い | 根拠 |
|---|---|---|
| **ADR の Status 行** | **訂正する** | 「その決定が今も有効か」を示す**メタデータ**であって決定の記録ではない(#90 / #95) |
| **ADR の本文**(背景 / Considered Options / Consequences) | **触らない。冒頭(Status 行の直後)に警告ブロックを置く** | 決定当時の記録。後知恵で書き換えると「当時どう考えたか」が失われる |
| **requirements の覆った決定・マイルストーン** | **原文を残し `→` で追記** | §12 / §13 で確立した書き方(#96)。問いが一度決着したこと自体が記録に値する |
| **「現在地」として書かれた記述** | **無条件に直す** | 読者に「今こうです」と伝える文なので、古びるのではなく**偽になる** |

**誤りの質も見分ける**(#113):

- **存在しないものの名指し**(例: `airshelf ingest --once` — このフラグは一度も実装されていない)
  → 読者が打っても失敗するだけ。害は小さいが、一次資料としては直したい
- **実在するが役割が違う**(例:「`rescan` は `airshelf ingest` に吸収」— 実際の移設先はトレイメニュー)
  → 読者は**それらしく動くもの**に辿り着いてしまい、誤解が定着する。**こちらの方が危険**

いずれも**発見時に必ず現在地の記述を直す**。当時の記録側をどうするかは上の表に従う。

## E2E 手動確認(参考)

無音フィクスチャを らじれこ風フォルダに置いて CLI を通す手順:
1. `{root}/{局}/{番組}/[{局}]{番組}(TimeFree)_{YYYYMMDDHHMMSS}.m4a` にフィクスチャをコピー。
2. `airshelf.json` の `RadioRoot`/`OutputRoot`/`LedgerPath` を一時ディレクトリに向ける。
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
(`tests/Airshelf.Tests/IngestServiceTests.cs` の `BadFile()` 参照)。

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
