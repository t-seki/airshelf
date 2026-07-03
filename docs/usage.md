# 利用ガイド(radirec ingest)

らじれこで録音した M4A を、Jellyfin 音楽ライブラリ向けに整理・取り込みする CLI の使い方。

> 現状は **M1**(コア + CLI 取り込み)まで。Jellyfin へのスキャン起動(M2)、Windows サービス
> 常駐(M3)、radikool 移行(M4)、GUI(M5)は未実装です。

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

> 既に取り込み済みの回へ後から alias を効かせる一括再適用は **M4** で提供予定です
> (現時点では新規取り込み分にのみ効きます)。

## 6. 配布用 exe(任意)

非技術者向けに単体 exe を作る場合:

```powershell
dotnet publish src/Radirec.Cli -c Release -r win-x64 --self-contained
# 出力の radirec.exe を使って: radirec.exe ingest --config radirec.json --dry-run
```
