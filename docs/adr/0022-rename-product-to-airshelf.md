# 製品名を Radirec から Airshelf に改める

Status: accepted(#256 の grill、2026-10-08。名前空間の改名は #257、製品としての名前の切り替えは #258。[ADR-0006](0006-binary-data-separation-and-path-resolution.md) / [ADR-0013](0013-velopack-per-user-updates.md) と関連)

このツールの名前は "Radirec" / "radirec-tool" と表記が揺れていた("radirec-organizer" と呼ぶこともあった)。
"Radirec" は録音元のアプリ「らじれこ」のローマ字読みで、docs では「らじれこ」(録音元)と「Radirec」(このツール)が
別の意味で並んでいた。radikool の録音も扱うのに、名前はらじれこに寄っている。requirements §14 でも
「`radirec-tool` は内部名、製品名は配布着手時に決める」と先送りになっていた。

## 決定

- **製品名を Airshelf に改め、全層で揃える**。CLI は `airshelf`。
  - 表示名(トレイ、管理画面、ショートカット、設定 → アプリ)は `Airshelf`。
  - Velopack の packId は `Airshelf`(インストール先 `%LOCALAPPDATA%\Airshelf`、`Airshelf-win-Setup.exe`)。
    トレイの exe は `Airshelf.Gui.exe`、アイコンのファイル名は `airshelf.ico`(絵柄は変えない)。
  - データのフォルダは `%LOCALAPPDATA%\AirshelfData\`、設定は `airshelf.json`(sample は `config/airshelf.sample.json`)。
  - 制御チャネルのパイプは `airshelf-control-<SID>`、多重起動を防ぐ Mutex は `Local\Airshelf.Gui.SingleInstance`、
    ログオン自動起動の Run 値は `AirshelfTray`。
  - プロジェクトと名前空間は `Airshelf.*`(#257)。GitHub の repo は `t-seki/airshelf`(更新フィードもここを見る)。
- **「らじれこ」は録音元のアプリの名前としてだけ使う**(CONTEXT.md)。このツールを指すのには使わない。
- **移行は手順書で一度だけ行う**([migration-to-airshelf.md](../migration-to-airshelf.md))。自動で移行するコード
  (旧データのフォルダの検出や移動、旧 Run 値の掃除など)は入れない。

## 検討した案

- **(a) `radirec-organizer` に揃える**:表記の揺れは無くなるが、らじれこのローマ字読みが残り、「らじれこ」と
  「Radirec」が docs で別の意味で並ぶ問題も、radikool を扱うのに名前がらじれこに寄っている問題も残る。
- **(b) `Radirec` のまま、表記だけ揃える**:移行の手間はいちばん小さいが、(a) と同じく、らじれことの紛らわしさと
  らじれこへの偏りが残る。
- **(c) らじれこ由来でない名前に改める(採用)**:上の 3 つ(らじれこと Radirec が別の意味で並ぶ、radikool も扱う、
  表記の揺れ)をまとめて解く。利用者は自分だけなので、移行は入れ直し 1 回で済む。

## 結果

- **packId が変わるので、Velopack 上は別のアプリになる**。Radirec の更新フィードから Airshelf へは更新されず、
  入れ直しが 1 回要る(旧 Radirec をアンインストールし、`Airshelf-win-Setup.exe` を入れる)。
- **データのフォルダは packId と別の名前にする制約を保つ**(`AirshelfData` と `Airshelf`)。Windows はパスの大文字小文字を
  区別しないので、同じ名前だとインストール先と重なり、アンインストールで台帳が消える(`ConfigPaths.cs` のコメント、
  ADR-0013)。
- **自動の移行コードは入れない**(ADR-0006 / ADR-0013 の方針)。データのフォルダのコピーと `airshelf.json` への改名は
  手順書で人が行う。Setup は空の設定を置く(`ConfigSeeder`)ので、データを先に移してから Setup を入れる。
- 過去の ADR の本文、旧環境を片付ける `deploy/uninstall.ps1` の中の旧名は、当時の記録・旧環境を指すものとして変えない。
  `deploy/uninstall.ps1` は共有の `_TrayCommon.ps1` が Airshelf の名前になったので、読み込んだ後に旧名で上書きして、
  旧環境だけを片付ける(現行の Airshelf のトレイと自動起動の登録には触れない)。
- 一度きりで終わった手順書(`migration-to-velopack.md`、`control-channel-verification.md`)は当時の記録として本文を残し、
  冒頭に改名の注記を置く。
