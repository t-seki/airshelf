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

## 4. 取り込みの挙動

- **非破壊**: らじれこ原本(`RadioRoot`)は読むだけで変更しません。整理済みは `OutputRoot` にコピーされます。
- **配置**: `{放送局}/{番組}/{番組} YYYY-MM-DD.m4a`(Jellyfin はタグで束ねますが人間可読性のため)。
- **タグ(案A)**: `albumartist=放送局 / album=番組 / title=放送日 / disc=放送年 / track=MMDD /
  genre / comment=録音種別 / date=放送日(フル ISO)`。出演者は元タグを維持。
- **冪等(import-once)**: 同一放送回 = `(放送局, 番組, 放送日)` は 1 回だけ取り込み。台帳(`LedgerPath`)に
  記録し、再実行しても重複しません。何度でも安全に実行できます。

## 5. 表記ゆれ・未知番組の整え方

- 新しい番組は alias 不要で、ファイル名/フォルダ由来の生名で取り込まれます(`(fallback)` 表示 + 警告)。
- 放送局の表記ゆれ(`NHKラジオ第1(radiko)` → `NHKラジオ第1`、radikool の局コード等)は
  `config/stations.json` に別名を追加して正規名へ寄せます。
- 番組名の表記ゆれ・複数表記の統合は `config/programs.json` に追加します。

```jsonc
// config/stations.json
{ "Stations": [ { "Canonical": "ニッポン放送", "Aliases": ["LFR", "ニッポン放送"] } ] }
```
```jsonc
// config/programs.json
{
  "Programs": [
    { "Match": { "Station": "J-WAVE", "Keys": ["SONYSONPO...", "SONY SONPO ..."] },
      "Canonical": { "Album": "SONY SONPO QUEST FOR THE FUTURE" } }
  ]
}
```

> 既に取り込み済みの回のアルバム名を後から alias で直す一括再適用は **M4** で提供予定です
> (M1 時点では新規取り込み分にのみ効きます)。

## 6. 配布用 exe(任意)

非技術者向けに単体 exe を作る場合:

```powershell
dotnet publish src/Radirec.Cli -c Release -r win-x64 --self-contained
# 出力の radirec.exe を使って: radirec.exe ingest --config radirec.json --dry-run
```
