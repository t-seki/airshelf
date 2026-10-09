# 更新機構は Velopack(GitHub Releases フィード)による per-user 自己更新にする

Status: accepted([ADR-0012](0012-retire-service-for-tray-resident-per-user.md) の per-user 化で解錠された更新機構の選定。**実装済み — #75 per-user 化 / #76 トレイからのアプリ内自己更新**)。**[ADR-0022](0022-rename-product-to-airshelf.md)(2026-10-08)で製品名を Airshelf に改め、Velopack の packId は `Radirec` → `Airshelf`(インストール先 `%LOCALAPPDATA%\Airshelf`)、データのフォルダは `%LOCALAPPDATA%\AirshelfData`、設定ファイルは `airshelf.json` になった(本文の名前は当時のもの。packId とデータのフォルダを別の名前にする決定は維持)**。**#281(2026-10-09)で vpk の版の決め方を改めた — 下の追記を参照**

> **追記(#281、2026-10-09。本文は決定当時の記録なので直さない)**: 下の `release.yml` の記述にある `--version 1.2.0` のような版の固定をやめた。決定そのもの(vpk をパッケージ Velopack と版一致させる)は変わらない。`release.yml` の Install vpk ステップが `Directory.Packages.props` の Velopack の版を読んで `dotnet tool update -g vpk --version <その版>` に渡すので、Renovate が Velopack を上げても vpk の版が食い違わない。Velopack の行が無い、または版が空のときは `throw` してリリースを止める。

トレイ常駐 per-user モデル([ADR-0012](0012-retire-service-for-tray-resident-per-user.md))への移行で install が非 admin になり、デスクトップ自己更新技術が使えるようになった。更新機構に **Velopack**(旧 Squirrel / Clowd.Squirrel の後継)を採用し、**既存の GitHub Releases をフィード源**として使う。トレイが起動時・定期にフィードを照会し、新版があれば delta を取得して**稼働中 exe を差し替え → 再起動**する。

## 背景

サービス時代の更新は `deploy/update.ps1`(admin・手元展開した Release zip から 2 exe を停止/差し替え/再起動)だった。[ADR-0012](0012-retire-service-for-tray-resident-per-user.md) で per-user 化した結果、「トレイから online 更新」「installer / winget」といった当初要望が本命の答えを得られるようになった。決定的な難所は **稼働中の exe を安全に差し替えて再起動する**ことで、これは自作すると `rename-self` 等の細工を要する。

## Considered Options

- **(採用) Velopack** — 稼働中 exe の差し替え・delta・ロールバックという難所を既製で解決。per-user・非 admin。**GitHub Releases をフィード源に直接使える**ため既存の配布(タグ駆動 Release)を流用できる。install/update/データ位置を framework が一貫して面倒見るので、[ADR-0012](0012-retire-service-for-tray-resident-per-user.md) で必要になる install 全面書き直しと重なる。代償は依存追加と、`release.yml` を素の zip 生成から Velopack パッケージ生成(`vpk`)へ書き換えること。
- **自作(GitHub Releases API 直叩き)** — 依存ゼロで既存 Release 資産を流用できるが、「稼働中 exe 差し替え」を自前で背負う。Velopack が解く面倒を再発明するため、本移行の通奏低音「難所は自作しない/偶発的複雑性を増やさない」に反する。
- **winget** — per-user で技術的には可だが、winget-pkgs への manifest 公開は**他人配布(C3)寄り**。単一ユーザー機ではオーバーで、C3 ゲート内。将来 Velopack の上に「発見(`winget install`)」導線として被せられる。
- **ClickOnce** — per-user 自己更新は満たすが、マシンワイドのサービス管理はそもそも不能で、ツールチェーン(Mage)も古い。Velopack の方が本用途に素直。
- **MSIX / App Installer** — 宣言的自動更新を持つが、run-as-user の古典的 Windows サービスと相性が悪かった。サービス撤去後も、per-user デスクトップ自己更新としては Velopack がより軽量で GitHub Releases 親和。

## Consequences

grill(2026-07-19)で S5(#67 配布・install/データ位置)と S6(#68 更新 UX)の具体を決めた。

### 配布・インストール(S5 / #67)

- **`Setup.exe`(per-user・非 admin)が zip + `install.ps1` を置換**。`vpk pack` は Setup.exe / full+delta nupkg / portable / フィード manifest(`releases.<channel>.json`)/ 埋め込み `Update.exe` を生成する。
- **データ位置 = `%LOCALAPPDATA%\RadirecData\`(Local・非 roaming)**。当初 `%APPDATA%`(Roaming)と記したが誤り: **SQLite 台帳はマシンローカルな取込済み記録**で、roaming すると (a) 稼働中 DB の同期で破損、(b) 別マシンで無意味。config も `I:`/NAS UNC などマシン固有パスを指す。`ConfigPaths.DataRoot` を `CommonApplicationData`(ProgramData)→ `LocalApplicationData` の **`RadirecData`** へ変更(ADR-0006 amend)。
  - **フォルダ名を `radirec` ではなく `RadirecData` にする**理由: Velopack は packId `Radirec` を per-user インストール先 `%LOCALAPPDATA%\Radirec` にする。Windows は case-insensitive なので旧案の `%LOCALAPPDATA%\radirec` は `\Radirec` と **同一フォルダ**になり、台帳が Velopack の versioned app ツリー内に入る → **アンインストール/修復で台帳ごと消える**(=全再取込)。そこで `RadirecData`(packId と非一致)= Velopack のインストール先と**別名の兄弟フォルダ**に置き、更新/アンインストールで消えないようにする。gui.log(`AppData.cs`)も同じ `RadirecData` に揃え、トレイの状態を 1 箇所にまとめる。
- **config seeding をアプリへ移設**: `install.ps1` がやっていた「sample から `radirec.json`/`stations.json`/`programs.json` を生成(既存は温存)」を、**トレイ初回起動時**に行う。sample は **EmbeddedResource** でバンドル(loose file を避ける)。
- **既存データ移行 = 一度きり runbook**(手動)。旧トレイ/サービスを停止 → `%ProgramData%\radirec\*`(`ledger.db` + `-wal` + `-shm` + json)を `%LOCALAPPDATA%\RadirecData\` へコピー → Velopack 版を起動。ADR-0006 の先例(「データ移行はスクリプト化せず runbook」)に一致。**新規ユーザー(将来 C3)は ProgramData に何も無く移行不要**なので、恒久の自動移行コードは置かない。台帳を運び損ねると次回スキャンで全件が未取込判定 → NAS 全再コピーになるため、runbook で明示。
- **`deploy/*.ps1` は退役**(install/update は Velopack、uninstall は `Update.exe --uninstall` / Add-Remove Programs)。旧 Windows サービス退役の手順だけ docs の runbook に残す。
- **`VelopackApp.Build().Run()` を `Program.Main` 先頭**に追加(install/update/uninstall フックの処理)。
- **dev CLI `radirec.exe` は Velopack バンドルに同梱**(pack ディレクトリへ一緒に publish。`%LOCALAPPDATA%\Radirec\current\` から dev/batch 実行可)。mainExe はトレイ(`-e Radirec.Gui.exe`)。
- **`release.yml` を vpk 化**: `dotnet publish`(両 exe を 1 フォルダへ・self-contained・非 single-file)→ `dotnet tool update -g vpk --version 1.2.0`(永続 runner で冪等 + パッケージ Velopack と版一致固定)→ `vpk download github`(delta 基準・初回は握る)→ `vpk pack -u Radirec -v <ver> -p <publish> -e Radirec.Gui.exe` → `vpk upload github --tag v<ver>`(prerelease は `VPK_PRE`)。既存の「zip + softprops upload」を置換。CI は既存 `GITHUB_TOKEN`(`contents: write`)を使う。

### private repo フィードのトークン

- 本 repo は **private**。発行(CI)は `GITHUB_TOKEN` で動くが、**アプリの更新チェック/DL は private Release の asset 認証が要る**。`GithubSource(repoUrl, accessToken, prerelease)` の第 2 引数にトークンが必要。
- **トークンは config に置く**(既存 `JellyfinApiKey` と同じ流儀。fine-grained・当 repo の contents:read のみ)。**配布バイナリには埋め込まない**(埋め込むと誰でも抽出して private repo を読める)。単一機の今はこれで十分。
- **C3(他人配布)着手時は「Release だけ別の public repo」**へ切り替え、トークン依存を消す(コードは private のまま)。

### 更新 UX(S6 / #68)

- `UpdateManager(new GithubSource(repoUrl, configToken, updatePrerelease))` → `CheckForUpdatesAsync` → `DownloadUpdatesAsync` → `ApplyUpdatesAndRestart`。
- **チェック時機 = 起動時 + 定期(24h)+ 手動「今すぐ更新を確認」メニュー項目**(常駐は滅多に再起動しないので起動時だけでは気づけない。手動項目は RC 検証にも必須)。
- **提示 = トレイメニュー項目「更新があります(x.y.z)」を主 + 初回検知時のみトースト 1 回**。S1 の版行に「更新あり」を併記。
- **適用 = 確認ダイアログ → DL → 適用 → 再起動**。取り込み中でも import-once + catch-up(ADR-0003)で再起動は安全。
- **`UpdatePrerelease` config トグル(既定 false = 安定版のみ)**。RC(prerelease)の更新フロー E2E を可能にし、prerelease opt-in も兼ねる。
- **RC の E2E**: ① 各 RC の `Setup.exe`(prerelease に添付)を手動導入(admin 不要)で install 検証。② `UpdatePrerelease=true` にして rc1 導入 → rc2 発行 → アプリが検知・適用、で更新機構を検証。本番は false に戻す。

### 共通

- 依存として Velopack ランタイム/ツール(`vpk`)を導入する。
- コード署名 / SmartScreen 対策は引き続き非目標(単一ユーザー機)。C3 着手時に winget 発見導線と署名を被せる。
