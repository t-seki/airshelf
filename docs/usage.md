# 利用ガイド(radirec ingest)

らじれこで録音した M4A を、Jellyfin 音楽ライブラリ向けに整理・取り込みする CLI の使い方。

> 実装済み: **M1**(コア + CLI 取り込み)/ **M2**(Jellyfin 自動リフレッシュ)/ **M3**(常駐サービス)/
> **M4-A**(radikool MP3 の一括移行)。GUI(M5)、ソース跨ぎのアルバム統合(M4-C)は未実装です。

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
| `LedgerPath` | 取り込み台帳 DB(ローカルディスク) | `C:\\ProgramData\\radirec\\ledger.db` |
| `StabilitySeconds` | 書き込み中判定の待機秒数 | `15` |
| `SkipPatterns` | 除外するファイル名部分一致(ジャンク録音) | `["この時間はＮＨＫネットラジオでは"]` |
| `KnownRecordingKinds` | 剥がしてよい録音種別マーカー | `["TimeFree"]` |
| `Genre` | 既定ジャンル | `ラジオ` |
| `JellyfinUrl` | Jellyfin ルート URL(未設定なら更新しない) | `https://jellyfin.example.net` |
| `JellyfinApiKey` | Jellyfin API キー(ダッシュボード → API キー) | `xxxxxxxx…` |
| `ScanIntervalSeconds` | 常駐サービスの定期(安全網)スキャン間隔・秒 | `120` |
| `WatchDebounceSeconds` | 検知後スキャンまでのデバウンス秒 | `10` |

> 設定ファイルは JSON ですが、`//` コメントと末尾カンマを許容します。

## 3. 実行

まず必ず **`--dry-run`** で、何がどこへ取り込まれるかを副作用なしに確認します。

```powershell
# 開発時(SDK から)
dotnet run --project src/Radirec.Cli -- ingest --config radirec.json --dry-run

# 問題なければ実行(コピー + タグ付与)
dotnet run --project src/Radirec.Cli -- ingest --config radirec.json
```

`--config` を省略するとカレントディレクトリの `radirec.json` を使います。

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
3. 統合先フォルダへ移動し、ファイル名を `{album} {YYYY-MM-DD}.{ext}` 規約に合わせてリネームする
   (放送日が重複しないことを事前確認)。
4. 空になった移動元フォルダを削除する。
5. `radirec refresh`(または Jellyfin の通常ライブラリスキャン)を実行する。

> **台帳は触らない**: import-once は `source_path` で再取込をガードするため、出力側フォルダを動かしても
> 元ソースの再取込は起きません(台帳の `output_path` は陳腐化しますが無害)。
> なお統合先が**既存フォルダ名と一致する**場合は `programs.json` を変更しないのが安全です。後から
> `programs.json` を足すと Canonical 変更で `identity_key` が変わり、アーカイブの再スキャン時に**重複取込**を招くことがあります。

## 6. 常駐サービス(自動取り込み・M3)

`Radirec.Service`(Windows サービス)を常駐させると、録音を検知して自動で取り込み + Jellyfin 更新まで
行います。らじれこは録音後 ~30 秒で強制スリープするため、**取りこぼしゼロはスリープ復帰時のキャッチアップ
+ import-once 冪等で担保**します([ADR-0003](adr/0003-catchup-over-sleep-race.md))。トリガは①録音検知
(FileSystemWatcher)②スリープ復帰 ③サービス起動時 ④定期(安全網)。

### ビルド

```powershell
dotnet publish src\Radirec.Service -c Release -r win-x64 --self-contained -o C:\radirec\service
```
`radirec.json`(および `stations.json` / `programs.json`)を exe と同じ場所か、絶対パスで参照できる場所に置きます。

### インストール(PowerShell 管理者)

NAS(UNC)へ書くため、**サービスは自分の Windows アカウントで実行**します(マップ済み Z: はサービスから
不可視なので UNC 直参照)。

```powershell
# イベントログのソースを一度だけ登録(任意だが推奨)
New-EventLog -LogName Application -Source Radirec

# サービス登録(Log On As = 自分のアカウント。NAS アクセスを継承)
New-Service -Name Radirec `
  -BinaryPathName '"C:\radirec\service\Radirec.Service.exe" "C:\radirec\radirec.json"' `
  -StartupType Automatic -Credential (Get-Credential)

Start-Service Radirec
```

### 確認・アンインストール

```powershell
Get-Service Radirec
Get-EventLog -LogName Application -Source Radirec -Newest 20   # 動作ログ

Stop-Service Radirec
sc.exe delete Radirec
```

## 7. 配布用 exe(任意)

非技術者向けに単体 exe を作る場合:

```powershell
dotnet publish src/Radirec.Cli -c Release -r win-x64 --self-contained
# 出力の radirec.exe を使って: radirec.exe ingest --config radirec.json --dry-run
```
