# 利用ガイド(radirec ingest)

らじれこで録音した M4A を、Jellyfin 音楽ライブラリ向けに整理・取り込みする CLI の使い方。

> 実装済み: **M1**(コア + CLI 取り込み)/ **M2**(Jellyfin 自動リフレッシュ)/ **M3**(常駐サービス)/
> **M4-A**(radikool MP3 の一括移行)/ **M6**(取込通知・制御チャネル)。配布・編集 GUI(M5)、
> ソース跨ぎのアルバム統合(M4-C)は未実装です。

## 1. 前提

- **.NET 10 SDK**(実行のみなら .NET 10 ランタイム、または後述の自己完結 exe)。
- 実行ホストは **Windows** を想定(らじれこ出力ドライブと配信先 NAS の両方がネイティブに見えるため)。
- 配信先が NAS の場合は **UNC パス**(`\\host\share\...`)で指定します。Windows のドライブレター
  (`Z:` 等)は将来のサービス実行で不可視になるため使いません。

## 2. セットアップ

サンプル設定をコピーして、自分の環境に合わせて編集します。

```
config/radirec.sample.json   → radirec.json       (環境設定)
config/stations.sample.json  → config/stations.json (放送局マスタ)
config/programs.sample.json  → config/programs.json (番組 alias)
```

`radirec.json` の主な項目:

| キー | 意味 | 例 |
|------|------|----|
| `RadioRoot` | らじれこ出力ルート | `I:\\radio\\tmp` |
| `OutputRoot` | 配信先(ローカル or UNC) | `\\\\192.168.11.121\\share\\media\\radio` |
| `LedgerPath` | 取り込み台帳 DB(ローカルディスク) | `ledger.db`(相対) |
| `StabilitySeconds` | 書き込み中判定の待機秒数 | `15` |
| `SkipPatterns` | 除外するファイル名部分一致(ジャンク録音) | `["この時間はＮＨＫネットラジオでは"]` |
| `KnownRecordingKinds` | 剥がしてよい録音種別マーカー | `["TimeFree"]` |
| `Genre` | 既定ジャンル | `ラジオ` |
| `JellyfinUrl` | Jellyfin ルート URL(未設定なら更新しない) | `https://jellyfin.example.net` |
| `JellyfinApiKey` | Jellyfin API キー(ダッシュボード → API キー) | `xxxxxxxx…` |
| `ScanIntervalSeconds` | 常駐サービスの定期(安全網)スキャン間隔・秒 | `120` |
| `WatchDebounceSeconds` | 検知後スキャンまでのデバウンス秒 | `10` |
| `NotificationsEnabled` | 新規取込時に取込要約シグナル(EventLog EventID 1001)を発行(受け手 GUI が通知表示。ADR-0008) | `true` |

> 設定ファイルは JSON ですが、`//` コメントと末尾カンマを許容します。
>
> **パスの解決基準(ADR-0006)**: `LedgerPath` / `StationsFile` / `ProgramsFile`(および `RadioRoot` /
> `OutputRoot`)の**相対パスは `radirec.json` のあるディレクトリ基準**で解決されます。デプロイ時は
> これらが `%ProgramData%\radirec\` に同居するため、裸のファイル名(`ledger.db` 等)で足ります。
> 絶対パス(`C:\...`、UNC `\\host\share\...`)はそのまま使われます。

## 3. 実行

まず必ず **`--dry-run`** で、何がどこへ取り込まれるかを副作用なしに確認します。

```powershell
# 開発時(SDK から)
dotnet run --project src/Radirec.Cli -- ingest --config radirec.json --dry-run

# 問題なければ実行(コピー + タグ付与)
dotnet run --project src/Radirec.Cli -- ingest --config radirec.json
```

`--config` を省略すると **`%ProgramData%\radirec\radirec.json`**(デプロイ時のデータルート)を使います
(ADR-0006)。開発時に別の場所の設定を使うなら `--config <path>` で明示します。

> ⚠️ 以前はカレントディレクトリの `radirec.json` が既定でした。**`radirec ingest` を `--config` 無しで
> 呼ぶ既存のスケジュールタスク / バッチがあれば、`--config <path>` を明示**してください(さもないと
> 別のデータルート・別台帳を参照し、取りこぼしや再スキャンを招きます)。

### 結果テーブルの見方

| 表示 | 意味 |
|------|------|
| 取込 | 実際に取り込んだ |
| 予定 | dry-run で取り込み予定 |
| 済 | 取り込み済み(冪等スキップ) |
| 未安定 | 書き込み中(まだ取り込まない) |
| ジャンク | スキップパターン一致で除外 |
| 対象外 | 当該 importer が扱えない |
| 失敗 | パース/コピー等に失敗 |

`(fallback)` は、放送局または番組名が alias 未登録で生名を採用したことを示します。

### Jellyfin ライブラリ更新

`JellyfinUrl` / `JellyfinApiKey` を設定しておくと、**取込が1件以上あった実行の最後に自動で
Jellyfin の全ライブラリスキャンを起動**します(`POST /Library/Refresh`。dry-run では起動しません)。
更新の失敗は取り込み成功を巻き戻さず、警告として表示します。

手動で更新だけ起動したいときは `refresh` コマンド(疎通確認にも便利):

```powershell
radirec refresh --config radirec.json
```

### radikool(旧 MP3 録音)の一括移行(M4-A)

旧録音アプリ **radikool** の MP3 アーカイブ(`{局コード}/{番組名}_{YYYYMMDD}.mp3`)を、`--root` で
一度きり取り込みます。同じ台帳・出力先・Jellyfin ライブラリへ束ねられ(**一度きりの移行なので常駐
サービスの監視対象にはしない**)、`.m4a`/`.mp3` はファイルごとに自動で振り分けられます。

```powershell
# まず dry-run(radikool の records フォルダを指す)
radirec ingest --config radirec.json --root "I:\d\radikool\records" --dry-run
# 問題なければ実行
radirec ingest --config radirec.json --root "I:\d\radikool\records"
```

radikool のバージョンごとに `records` フォルダが分かれている場合(例 `radikool4.9.3` /
`radikool4.9.1` / `radikool`)は、**同じ台帳・出力先のまま `--root` を替えて順に実行**すれば、
版をまたぐ同一回は同一性キーで**自動的に重複排除**される(先に取り込んだ版が残り、後は「済」でスキップ)。
版フォルダの構造は `{局コード}/file.mp3` の 1 階層と `{局コード}/{番組}/file.mp3` の 2 階層が混在するが、
番組名・放送日は**ファイル名から**採るためどちらも取り込める。

radikool 固有の扱い:

- **放送局は ID3 の `genre` タグが権威**(局コードフォルダは誤分類がありうるため信用しない。
  `genre` の表記ゆれは `stations.json` の alias で吸収)。番組名はファイル名 stem。時刻は持たない。
- **同日重複マーカー `(N)`**(例 `…_20200223(2).mp3`)は、中身の違う同日別録音として
  **両方**取り込み、`-N` / `track` / `title (N)` で区別します([ADR-0004](adr/0004-part-index-dedup-for-timeless-sources.md))。
- radikool 元タグ(`comment` の和文日付など)は案A で上書き/除去し、`artist`(出演者)は維持します。
- **番組名の表記ゆれ統合・らじれことのアルバム統合は M4-C(未実装)**。現状は radikool の番組名ごとに
  別アルバムとして入ります(例 `欅坂46`/`櫻坂46` は別アルバム)。局の正規化(配置)は今スライスで行います。

## 4. 取り込みの挙動

- **非破壊**: らじれこ原本(`RadioRoot`)は読むだけで変更しません。整理済みは `OutputRoot` にコピーされます。
- **配置**: `{放送局}/{番組}/{番組} YYYY-MM-DD.m4a`(Jellyfin はタグで束ねますが人間可読性のため)。
  複数フォルダを統合した番組の同日パートは `… YYYY-MM-DD-1.m4a` / `-2` とパート番号で区別されます。
- **タグ(案A)**: `albumartist=放送局 / album=番組 / title=放送日 / disc=放送年 / track=MMDD /
  genre / comment=録音種別 / date=放送日(フル ISO)`。出演者は元タグを維持。
  統合番組の同日パートは `title` に `(N)`、`track` に `MMDD*10+N` を付けてアルバム内の並び順を確定します。
- **冪等(import-once)**: アルバムのグルーピングは `(放送局, 番組, 放送日)` 単位ですが、**取り込みの
  重複判定は放送時刻も見ます**([ADR-0002](adr/0002-same-day-tiebreak-by-time.md))。同日でも時刻が
  異なる別放送(時間帯分割番組のパート等)は両方取り込まれ、同時刻の再取り込みだけがスキップされます。
  時刻を持たないソース(radikool)の同日重複はパート序数で区別します
  ([ADR-0004](adr/0004-part-index-dedup-for-timeless-sources.md))。
  台帳(`LedgerPath`)に記録し、再実行しても重複しません。

## 5. アルバムの束ね方・表示名の整え方

**番組(アルバム)の同一性はフォルダ名で決まります**([ADR-0001](adr/0001-program-identity-anchored-to-folder.md))。
らじれこはファイル名の頭に週替わりゲスト名を入れますが、フォルダは番組ごとに安定しているため、
**同じフォルダの回は自動で 1 アルバムに集約**され、ゲストは各回の `artist` に残ります。例:
`オールナイトニッポン0(ZERO)` フォルダの全回は、ゲストが違っても 1 アルバムに束ねられます(**設定不要**)。

設定が要るのは次の 2 つだけです:

- **放送局の表記ゆれ**(`NHKラジオ第1(radiko)` → `NHKラジオ第1`、radikool の局コード等)を
  `config/stations.json` の別名で正規名へ寄せる。
- **表示名の整形 / 複数フォルダの統合**を `config/programs.json` で行う(任意)。

```jsonc
// config/stations.json
{ "Stations": [ { "Canonical": "ニッポン放送", "Aliases": ["LFR", "ニッポン放送"] } ] }
```
```jsonc
// config/programs.json
{
  "Programs": [
    // ラテン名の空白除去フォルダを綺麗な表示名に
    { "Match": { "Station": "J-WAVE", "Folders": ["LOGISTEEDRADIONOMICS"] },
      "Canonical": { "Album": "LOGISTEED RADIONOMICS" } },
    // 複数フォルダを 1 アルバムに統合
    { "Match": { "Station": "TBSラジオ", "Folders": ["パンサー向井のふらっと(1)", "パンサー向井のふらっと(2)"] },
      "Canonical": { "Album": "パンサー向井のふらっと" } }
  ]
}
```
`Match.Folders` は番組**フォルダ名**で照合します(表記ゆれは大文字化・空白除去で吸収)。

> 既に取り込み済みの回へ後から alias を効かせる一括再適用は **M4-C** で提供予定です
> (現時点では新規取り込み分にのみ効きます)。

### 既に取り込み済みアルバムを手作業で統合する(M4-C 未実装時の暫定手順)

`config/programs.json` の統合設定は**新規取り込み分にのみ**効きます(既取込への一括再適用 = **M4-C** は未実装)。
既に別アルバムとして入ってしまった回を今すぐ 1 つに束ねたいときは、以下を手作業で行います。

**前提となる Jellyfin の挙動(重要)**: Jellyfin(本ライブラリ構成)は**アルバムを「フォルダ単位」でグルーピング**します。
`album` タグの書換だけではアルバムは移動・統合されません(メタデータの FullRefresh でタグ値は更新されても
アルバム所属は変わらない)。**ファイルを統合先フォルダへ物理移動して初めて統合されます**(表示名は `album` タグ由来)。

| 操作 | 結果 |
| --- | --- |
| `album` タグ書換のみ + 通常スキャン | 統合されない |
| `album` タグ書換のみ + メタデータ FullRefresh | タグ値は更新されるがアルバム所属は不変 |
| **タグ書換 + 統合先フォルダへ移動 + 通常スキャン** | **統合される** |

手順(NAS 書込のため Windows で実施):

1. 統合先の `album` タグ値は**手打ちせず**、統合先フォルダの既存ファイルから読み取って使う
   (全角 `＆` や `D.C.` の有無など、表記が 1 文字でも違うと別アルバムになるため)。
2. 移動元の各ファイルの `album` タグを 1 の値へ書き換える(`TagLibSharp.dll` でその場編集。他タグは保持)。
   **時間帯分割番組(同日パート)を `(1)`/`(2)` 別アルバムから 1 アルバムのパート形式へ統合**する場合は、
   統合番組の規約に合わせて `title` を `YYYY-MM-DD (N)`、`track` を `MMDD*10+N`(N=フォルダ順の 1 始まり)にも
   書き換える(単体取込では `title=YYYY-MM-DD` / `track=MMDD` になっているため)。
3. 統合先フォルダへ移動し、ファイル名を `{album} {YYYY-MM-DD}.{ext}` 規約に合わせてリネームする
   (放送日が重複しないことを事前確認)。
4. 空になった移動元フォルダを削除する。
5. `radirec refresh`(または Jellyfin の通常ライブラリスキャン)を実行する。これで**アルバムの統合(所属)**は反映される。
   ただし**書き換えたタグ(`title`/`track`/`disc` 等)が各トラックに反映されない**場合は、統合先アルバムに対して
   **メタデータの FullRefresh**(`POST /Items/{albumId}/Refresh?metadataRefreshMode=FullRefresh&replaceAllMetadata=true`)を
   実行する。移動でファイルは Jellyfin から「新規」として拾われるが、既存メタデータが残ってタグ由来値(パート番号 `(N)` や
   `track` 等)が出ないことがあるため。

> **台帳は触らない**: import-once は `source_path` で再取込をガードするため、出力側フォルダを動かしても
> 元ソースの再取込は起きません(台帳の `output_path` は陳腐化しますが無害)。
> なお統合先が**既存フォルダ名と一致する**場合は `programs.json` を変更しないのが安全です。後から
> `programs.json` を足すと Canonical 変更で `identity_key` が変わり、アーカイブの再スキャン時に**重複取込**を招くことがあります。

## 6. 常駐サービス(自動取り込み・M3)

`radirec.exe service`(Windows サービス)を常駐させると、録音を検知して自動で取り込み + Jellyfin 更新まで
行います。らじれこは録音後 ~30 秒で強制スリープするため、**取りこぼしゼロはスリープ復帰時のキャッチアップ
+ import-once 冪等で担保**します([ADR-0003](adr/0003-catchup-over-sleep-race.md))。トリガは①録音検知
(FileSystemWatcher)②スリープ復帰 ③サービス起動時 ④定期(安全網)⑤手動(制御チャネル・下記)。

### 制御チャネル(手動再スキャン・状態取得)

常駐サービスへローカルから指示を出せます([ADR-0010](adr/0010-gui-service-control-via-named-pipe.md)。
named pipe 経由・**同一ユーザーのみ**・リモート制御は非対応)。

```powershell
radirec status     # サービスのライブ状態(監視ルート・最終スキャン時刻とトリガ・直近の取込/失敗件数)
radirec rescan     # 自動トリガを待たず今すぐ 1 回スキャンさせる(受理まで。完了は待たない)
radirec reload     # radirec.json を再読込させる(config 由来ランタイムを丸ごと再構築)
```

`reload` はサービスを止めずに設定変更を反映します。**受理 = 構文検証通過 + 再構築キュー投入まで**で、
実際の反映は在中スキャンの完了後です(部分反映はせず、グラフ + 監視 + タイマーを一括で作り直します)。
不正な `radirec.json`(構文エラー・未知キー等)は受理時に弾かれ、再構築に失敗しても**旧構成のまま稼働継続**します。

サービスが未起動/未応答なら、いずれもエラーメッセージを表示して終了コード 1 を返します(破綻しません)。
GUI からの操作は今後追加予定です(#5)。

### 導入(Release + スクリプト)

配布は **GitHub Release** から取得します。CLI もサービスも **単一 exe `radirec.exe`**(自己完結・ランタイム
不要)に統合されており([ADR-0005](adr/0005-single-exe-cli-service-merge.md))、実行体とデータは分離配置します
([ADR-0006](adr/0006-binary-data-separation-and-path-resolution.md)):

- 実行体: `C:\Program Files\radirec\radirec.exe`
- データ: `%ProgramData%\radirec\`(`radirec.json` / `ledger.db` / `stations.json` / `programs.json`)

1. [Releases](https://github.com/t-seki/radirec-tool/releases) から `radirec-<version>-win-x64.zip` を落として展開。
2. **管理者 PowerShell** で `deploy\install.ps1` を実行:

```powershell
cd <展開先>\deploy
# 実行アカウントは既定で .\<現在のユーザー>。NAS(UNC)へ書くため自分のアカウントで登録する
.\install.ps1
```

`install.ps1` は EventLog ソース登録 → 設定を sample から初回生成(既存は温存)→ サービス登録
(`-Credential` で自分のアカウント、パスワード対話)まで行います。**再実行しても壊れません**
(既存サービスがあればスキップ。再登録は `-Force`)。

3. 生成された `%ProgramData%\radirec\radirec.json` を編集(`RadioRoot` / `OutputRoot` / Jellyfin)。
4. サービス開始:

```powershell
Start-Service Radirec
Get-Service Radirec
Get-EventLog -LogName Application -Source Radirec -Newest 20   # 動作ログ
```

### 更新(バイナリ差し替え)

新しい Release を展開し、管理者 PowerShell で `update.ps1` を実行します。データ(設定・台帳)は触りません
(台帳スキーマは起動時に自動移行)。**認証情報は不要**(登録済みアカウントを継承):

```powershell
cd <新しい展開先>\deploy
.\update.ps1   # 停止 → radirec.exe 差し替え → 再開
```

### アンインストール

```powershell
.\uninstall.ps1              # サービス削除・EventLog 解除・実行体削除。データは残す
.\uninstall.ps1 -PurgeData   # データ(台帳・設定含む)も削除
```

## 7. 開発時の実行

SDK から直接動かす場合(`radirec.exe` を publish せずに):

```powershell
dotnet run --project src/Radirec.Cli -- ingest --config <path>\radirec.json --dry-run
```

単一 exe を手元で作るには `dotnet publish src/Radirec.Cli -c Release`(win-x64 自己完結・単一ファイルが
既定で出力)。CI がタグ push で同じ成果物を Release へ添付します。非技術者向けの GUI インストーラ
(Inno Setup)は M5(#5)で本 publish/Release を土台に被せる予定です([ADR-0007](adr/0007-distribution-via-powershell-scripts.md))。

## 8. 旧レイアウトからの一度きり移行(`C:\radirec\` → 分離配置)

M4-A まで運用していた「実行体・台帳・設定を `C:\radirec\` に同居」から、新レイアウト(実行体 =
`C:\Program Files\radirec\`、データ = `%ProgramData%\radirec\`)へ移す**一度きりの手順**です
([ADR-0006](adr/0006-binary-data-separation-and-path-resolution.md))。

> ⚠️ **台帳が生命線**: `ledger.db` は取り込み冪等(import-once)の唯一の記録です。新データルートへ
> **無傷で運ばない**と、次回スキャンで全放送回が「未取込」判定になり、NAS へ**再コピーが爆発**します。
> 稼働中の DB を WAL ごとコピーすると破損しうるため、**必ずサービスを停止してから**扱います。
> 以下は **`Copy-Item`(移動ではなくコピー)** を使い、旧 `C:\radirec\` を検証完了まで**無傷のバックアップ
> として残します**。ロールバックが必要になっても台帳を失いません。

**このスクリプトは新 Release を展開して `install.ps1` を実行する「前」に、管理者 PowerShell で実行**します
(空のデータルートへコピーするため衝突しません)。台帳コピーは失敗を握り潰さず、成否を検証します。

```powershell
$old = 'C:\radirec'
$data = "$env:ProgramData\radirec"

# 1. 旧サービスを停止・削除(WAL 生存中のコピーを避ける)
Stop-Service Radirec -ErrorAction SilentlyContinue
sc.exe delete Radirec
Start-Sleep -Seconds 2   # ファイルハンドル解放を待つ

# 2. データルートを作成
New-Item -ItemType Directory -Path $data -Force | Out-Null

# 3. 台帳をコピー(必須成功)。sidecar(-wal/-shm)は無いこともあるので任意。
Copy-Item "$old\ledger.db" $data          # 失敗すれば例外で停止(握り潰さない)
Copy-Item "$old\ledger.db-wal","$old\ledger.db-shm" $data -ErrorAction SilentlyContinue

# 4. マスタ・設定をコピー
Copy-Item "$old\stations.json","$old\programs.json","$old\radirec.json" $data

# 5. 移した radirec.json のパスを裸のファイル名へ決定的に書き換える(config ディレクトリ基準で解決。
#    RadioRoot/OutputRoot の絶対パスはそのまま)。手編集ミスを避けるため置換で行う。
$cfg = Join-Path $data 'radirec.json'
(Get-Content $cfg -Raw) `
  -replace '"C:\\\\radirec\\\\ledger\.db"', '"ledger.db"' `
  -replace '"C:\\\\radirec\\\\stations\.json"', '"stations.json"' `
  -replace '"C:\\\\radirec\\\\programs\.json"', '"programs.json"' |
  Set-Content $cfg -Encoding UTF8

# 6. 検証(install.ps1 を実行する前の門番):
#    (a) 台帳が新ルートに非空で存在する
$ledger = Join-Path $data 'ledger.db'
if (-not (Test-Path $ledger) -or (Get-Item $ledger).Length -eq 0) {
    throw "台帳の移行に失敗しています($ledger)。install.ps1 を実行せず原因を調べてください。"
}
#    (b) パス書換が効いたこと。Copy(移動でなく)なので旧 C:\radirec\ledger.db は残存し、書換が
#        漏れても LedgerPath が旧絶対パスを指したまま起動が通ってしまう(旧 C:\radirec を後で消した
#        瞬間に空 DB → 再コピー爆発)。正規の値は I:\... / UNC \\... / 裸名のみなので、C:\radirec 参照が
#        残っていれば書換ミス。
if ((Get-Content $cfg -Raw) -match 'C:\\+radirec') {
    throw "radirec.json に旧 C:\radirec のパスが残っています(書換が未反映)。install.ps1 を実行しないでください。"
}
Write-Host "台帳を移行しました: $ledger ($((Get-Item $ledger).Length) bytes)"
```

その後、新 Release を展開して `deploy\install.ps1` を実行します。設定は既存を**温存**する(生成しない)ため、
コピーした `radirec.json` がそのまま使われます。

```powershell
cd <展開先>\deploy
.\install.ps1        # 実行体を Program Files へ、サービスを run-as 自分のアカウントで再登録
Start-Service Radirec
Get-EventLog -LogName Application -Source Radirec -Newest 20   # 取り込みが再開し、再コピーが起きていないことを確認
```

移行が正しく効いていれば、既存の全放送回は台帳にあるため**再取り込みは起きません**(新規録音のみ処理)。
**再コピーが起きていないことを EventLog で確認してから**、旧 `C:\radirec\`(および `ledger.db.bak`)を削除します。
