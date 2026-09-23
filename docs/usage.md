# 利用ガイド(radirec ingest)

らじれこで録音した M4A を、Jellyfin 音楽ライブラリ向けに整理・取り込みする CLI の使い方。

> 実装済み: **M1**(コア + CLI 取り込み)/ **M2**(Jellyfin 自動リフレッシュ)/ **M4-A**(radikool MP3 の
> 一括移行)。常駐・通知・制御・取込履歴・自己更新は **トレイ常駐 per-user モデル**(§6。ADR-0012/0013)。
> ソース跨ぎのアルバム統合(M4-C)/ 編集 GUI(M5)は未実装です。
>
> この §1–§5 は主に **dev/batch の CLI(`radirec.exe`)** の使い方。常駐運用は §6 を参照。

## 1. 前提

- **.NET 10 SDK**(実行のみなら .NET 10 ランタイム、または後述の自己完結 exe)。
- 実行ホストは **Windows** を想定(らじれこ出力ドライブと配信先 NAS の両方がネイティブに見えるため)。
- 配信先が NAS の場合は **UNC パス**(`\\host\share\...`)で指定します。Windows のドライブレター
  (`Z:` 等)は別プロセス/セッションから不可視になりうるため使いません(UNC が確実)。

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
| `OutputRoot` | 配信先(ローカル or UNC) | `\\\\NAS\\share\\media\\radio` |
| `LedgerPath` | 取り込み台帳 DB(ローカルディスク) | `ledger.db`(相対) |
| `StabilitySeconds` | 書き込み中判定の待機秒数 | `15` |
| `SkipPatterns` | 除外するファイル名部分一致(ジャンク録音) | `["この時間はＮＨＫネットラジオでは"]` |
| `KnownRecordingKinds` | 剥がしてよい録音種別マーカー | `["TimeFree"]` |
| `Genre` | 既定ジャンル | `ラジオ` |
| `JellyfinUrl` | Jellyfin ルート URL(未設定なら更新しない) | `https://jellyfin.example.net` |
| `JellyfinApiKey` | Jellyfin API キー(ダッシュボード → API キー) | `xxxxxxxx…` |
| `ScanIntervalSeconds` | 常駐トレイの定期(安全網)スキャン間隔・秒 | `120` |
| `WatchDebounceSeconds` | 検知後スキャンまでのデバウンス秒 | `10` |
| `NotificationsEnabled` | 新規取込時にトレイが in-process でトースト表示(ADR-0012) | `true` |
| `UpdatePrerelease` | アプリ内更新で prerelease(RC)も対象にするか(§6・ADR-0013) | `false` |
| `UpdateToken` | 更新フィード(GitHub Releases)読取用トークン。**private repo では必須** | `""` |

> 設定ファイルは JSON ですが、`//` コメントと末尾カンマを許容します。
>
> **パスの解決基準(ADR-0006)**: `LedgerPath` / `StationsFile` / `ProgramsFile`(および `RadioRoot` /
> `OutputRoot`)の**相対パスは `radirec.json` のあるディレクトリ基準**で解決されます。常駐トレイのデータルートは
> `%LOCALAPPDATA%\RadirecData\` で、これらが同居するため裸のファイル名(`ledger.db` 等)で足ります(ADR-0013)。
> 絶対パス(`C:\...`、UNC `\\host\share\...`)はそのまま使われます。

## 3. 実行

まず必ず **`--dry-run`** で、何がどこへ取り込まれるかを副作用なしに確認します。

```powershell
# 開発時(SDK から)
dotnet run --project src/Radirec.Cli -- ingest --config radirec.json --dry-run

# 問題なければ実行(コピー + タグ付与)
dotnet run --project src/Radirec.Cli -- ingest --config radirec.json
```

`--config` を省略すると **`%LOCALAPPDATA%\RadirecData\radirec.json`**(常駐トレイのデータルート)を使います
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

### 散在ソースを {放送局}/{番組}/ 構造へ仕分ける(`organize-source`。#81)

取込(ingest)とは別に、**ローカルのソース音源そのものを整理**するコマンド。散在したソース
(`I:\radio\old\…` の らじれこ M4A + radikool MP3)を、らじれこ録音ルート `RADIO\{放送局}\{番組}\`
構造へ**物理的に移動**して 1 つの整理済みアーカイブに集約します。タグ書換や Jellyfin 出力はせず、
**ファイル名も変えません**(末尾 `_{timestamp}` を温存 → 取込互換 + 出所保持)。

```powershell
# まず dry-run(何がどこへ移動するか副作用なしに確認)
radirec organize-source --from "I:\radio\old" --to "I:\radio\radirec\RADIO" --dry-run
# 問題なければ実行(既定は move。移動元を残すなら --copy)
radirec organize-source --from "I:\radio\old" --to "I:\radio\radirec\RADIO"
```

- **移動先** = `{to}\{正規放送局}\{番組}\{元ファイル名}`。放送局は `stations.json` の正規名(radikool の
  局コード `LFR` もここで正規化)。番組フォルダは**ソースの生の名前を維持**(らじれこの
  `パンサー向井のふらっと(1)`/`(2)` を保ち、再取込時の統合・パート `-N` を壊さない)。radikool は
  ファイル名 stem を番組フォルダにする(局コードフォルダには惑わされない)。
- **移動先が既存なら決して上書きしない**:同一内容(サイズ+最終更新)はスキップ、別内容の同名は
  **衝突**として報告し移動元に残す。ジャンク(`SkipPatterns` 一致)・parse 失敗も移動せず残す。
  → **移動元に残ったもの = 要確認の山**(要約行に「要確認: N 件」を表示)。
- **`--to` の既存フォルダを尊重**:移動先に「同じ正規名へ解決される別名フォルダ」(例 らじれこ が使う
  `NHK-FM（東京）`)があれば、canonical `NHK-FM` を新設せず**その既存フォルダを再利用**して分裂を防ぐ。
- `--from` の途中階層(`d_radio` 等)は無視される(各ファイルの親=番組・祖父=放送局で解決)。

## 4. 取り込みの挙動

- **非破壊**: らじれこ原本(`RadioRoot`)は読むだけで変更しません。整理済みは `OutputRoot` にコピーされます。
- **配置**: `{放送局}/{番組}/{番組} YYYY-MM-DD.m4a`(Jellyfin はタグで束ねますが人間可読性のため)。
  統合番組(`programs.json` で複数フォルダを 1 番組に束ねたもの)の回は、`… YYYY-MM-DD-1.m4a` / `-2` と
  **パート**番号(由来フォルダの `Folders` 順・1 始まり)で区別されます。パート番号は放送日の重なりと関係なく付き、
  片方のフォルダしか録れていない日の回も `-1` などになります。
- **タグ(案A)**: `albumartist=放送局 / album=番組 / title=放送日 / disc=放送年 / track=MMDD /
  genre / comment=録音種別 / date=放送日(フル ISO)`。出演者は元タグを維持。
  統合番組の回は `title` に `(N)`、`track` に `MMDD*10+N`(N = パート番号)を付けてアルバム内の並び順を確定します。
- **冪等(import-once)**: アルバムのグルーピングは `(放送局, 番組, 放送日)` 単位ですが、**取り込みの
  重複判定は放送時刻も見ます**([ADR-0002](adr/0002-same-day-tiebreak-by-time.md))。同日でも時刻が
  異なる別放送(時間帯分割番組のパート等)は両方取り込まれ、同時刻の再取り込みだけがスキップされます。
  時刻を持たないソース(radikool)の同日重複は、ファイル名末尾の**重複マーカー** `(N)` で区別します
  (出力はパートと同じ `-N` / `(N)` / `MMDD*10+N` の形になります)
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
   `title` / `track` とファイル名(手順 3)は、**新規取り込みされた回と同じ形**になるよう、統合先が
   **統合番組**(`programs.json` の 1 エントリの `Folders` に異なるフォルダが 2 つ以上ある番組)かどうかで決める:

   | 統合先 | ファイル名 | `title` | `track` |
   | --- | --- | --- | --- |
   | 統合番組(パート番号あり) | `{album} {YYYY-MM-DD}-{N}.{ext}` | `YYYY-MM-DD (N)` | `MMDD*10+N` |
   | それ以外(表記ゆれ・フォルダ名の変更など) | `{album} {YYYY-MM-DD}.{ext}` | `YYYY-MM-DD` | `MMDD` |

   - N は**パート**番号 = 移動元フォルダがその `Folders` の何番目か(1 始まり)。手順 2 と 3 で同じ N を使う。
   - パート番号は放送日の重なりと関係なく付ける。片方のフォルダしか録れていない日の回も `-1` / `(1)` にする
     (単体取込では `title=YYYY-MM-DD` / `track=MMDD` になっているので書き換えが要る)。
   - **例外(重複マーカー由来の `-N`)**: radikool の `(N)` 付き録音は、統合番組でなくても `-N` / `(N)` /
     `MMDD*10+N` で取り込まれている。統合先が統合番組**でない**ときは、その N をファイル名・`title`・`track` に
     そのまま残す。統合先が統合番組で、移動元に重複マーカー由来の `-N` があるときは、パートと番号がぶつかるので
     手作業統合しない([#86](https://github.com/t-seki/radirec-tool/issues/86) の解決待ち)。
3. 統合先フォルダへ移動し、ファイル名を手順 2 の表の形にリネームする。パート番号の無いケースでは、
   統合先に同じ放送日のファイルが無いことを事前に確認する。
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

## 6. 常駐トレイ(自動取り込み)

`Radirec.Gui`(トレイ常駐)を起動すると、録音を検知して自動で取り込み + Jellyfin 更新まで行います。
**取り込みワーカーをトレイプロセスが内包**し、ユーザーセッションで動きます
([ADR-0012](adr/0012-retire-service-for-tray-resident-per-user.md)。旧 Windows サービスは退役)。
らじれこは録音後 ~30 秒で強制スリープするため、**取りこぼしゼロはスリープ復帰時のキャッチアップ +
import-once 冪等で担保**します([ADR-0003](adr/0003-catchup-over-sleep-race.md))。トリガは①録音検知
(FileSystemWatcher)②スリープ復帰 ③起動時 ④定期(安全網)⑤手動(トレイメニュー)。新規取込時は
トレイが **in-process でトースト表示**します。

### 制御・履歴・更新(トレイメニュー)

トレイアイコンの右クリックから(かつての `radirec status/rescan/reload`〔named pipe〕は in-process 化で
**メニューに統合**。ADR-0012 で pipe は退役):

- **今すぐ再スキャン**: 自動トリガを待たず今すぐ 1 回スキャン(受理まで)。
- **設定リロード…**: `radirec.json` を再読込(config 由来ランタイムを丸ごと再構築。部分反映しない)。
  不正な設定は弾かれ、再構築に失敗しても**旧構成のまま稼働継続**。
- **状態 / 最終スキャン**: 稼働状態・監視ルート・最終スキャン時刻/件数。
- **取込履歴…**: 台帳の全取込を一覧表示(読み取り専用。番組/局/放送日/取込日時/ファイル)。
  出力先パスは長いため列にはファイル名だけを出し、フルパスは ToolTip と窓下部の詳細ペイン
  (出力先 / 取込元。選択してコピーできる)で見せます。行を**ダブルクリック**(または Enter)で
  出力先をエクスプローラーで開き、**右クリック**から取込元を開く / それぞれのパスをコピーもできます。
  ファイルが既に無いとき(取込元を消した / `organize-source` で移動した等)は、たどれる一番近い
  親フォルダを開いてその旨を表示します。
- **今すぐ更新を確認 / 更新を適用して再起動**: アプリ内更新(下記)。

### 導入(Velopack Setup.exe・非 admin)

[Releases](https://github.com/t-seki/radirec-tool/releases) の **`Radirec-win-Setup.exe`** を実行するだけ
(**管理者不要**。[ADR-0013](adr/0013-velopack-per-user-updates.md)):

- 実行体: `%LOCALAPPDATA%\Radirec\`(Velopack 管理・更新で差し替わる)
- データ: `%LOCALAPPDATA%\RadirecData\`(`radirec.json` / `ledger.db` / `stations.json` / `programs.json` / `gui.log`)

初回起動時に設定を sample から生成します。`%LOCALAPPDATA%\RadirecData\radirec.json` を編集
(`RadioRoot` / `OutputRoot` / Jellyfin / 更新用 `UpdateToken`)。トレイはログオン自動起動に自己登録されます。
動作ログは `%LOCALAPPDATA%\RadirecData\gui.log`。

### 更新(アプリ内・Velopack)

トレイメニュー「**今すぐ更新を確認**」→ 新版があれば「**更新を適用して再起動**」。起動時・24h ごとに自動チェック
もします。適用は DL → ワーカーを graceful 停止 → 差し替え → 自動再起動。
**private repo のため**、更新フィード読取用の fine-grained PAT(contents:read)を `radirec.json` の `UpdateToken`
に設定します。RC(prerelease)を試すときは `"UpdatePrerelease": true`。

### アンインストール

設定 → アプリ →「Radirec」→ アンインストール、または `%LOCALAPPDATA%\Radirec\Update.exe --uninstall`。
ログオン自動起動も解除されます。データ(`%LOCALAPPDATA%\RadirecData`)は残ります(消すと import-once 履歴が
失われる)。不要なら手動削除。

> 旧 admin 配置(`%ProgramData%\radirec` + Windows サービス)からの移行は
> [移行 runbook](migration-to-velopack.md) を参照。

## 7. 開発時の実行

SDK から直接動かす場合(`radirec.exe` を publish せずに):

```powershell
dotnet run --project src/Radirec.Cli -- ingest --config <path>\radirec.json --dry-run
```

単一 exe を手元で作るには `dotnet publish src/Radirec.Cli -c Release`(win-x64 自己完結)。配布(常駐トレイの
Setup.exe・更新フィード)は CI がタグ push で **`vpk`(Velopack)** により Release へ発行します(§6・ADR-0013)。

## 8. 旧環境からの移行

旧 admin 配置(`%ProgramData%\radirec` + Windows サービス)や、さらに古い `C:\radirec\` 同居レイアウトからの
移行は、いずれも **台帳(`ledger.db`)を無傷で新データルート `%LOCALAPPDATA%\RadirecData\` へ運ぶ**のが要点です
(運び損ねると全放送回が「未取込」判定になり NAS へ再コピーが爆発)。現行の手順は
[移行 runbook](migration-to-velopack.md) にまとめています。
