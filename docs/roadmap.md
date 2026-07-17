# Radirec.Gui ロードマップ(受け手トレイ / 編集 GUI)

> Status: Map v1 / 最終更新: 2026-07-16

これは **`Radirec.Gui`(取込通知トレイ + 将来の編集 GUI)の将来オプションの地図**であり、着手の約束ではない。
優先順位は付けていない。実際の不満が出た時点で、ある軸を greenlit してから issue を切る(§運用)。

一次資料へのポインタ: 出荷済みの通知・制御レイヤは [requirements.md](requirements.md) §7.1/§7.2、
決定は [ADR-0008](adr/0008-ingest-notification-via-eventlog-signal.md) /
[ADR-0009](adr/0009-toast-receiver-unpackaged-wpf-tray.md) /
[ADR-0010](adr/0010-gui-service-control-via-named-pipe.md)、編集アプリの保留判断は §10.1 と issue #5。

## 前提: 現状の構造(この地図の起点)

M6(通知・制御チャネル)は通知トレイ・制御チャネルとも出荷済み。ただし **`Radirec.Gui` 側で出荷済みなのは
取込通知の受け手トレイのみ**で、制御チャネル(status/rescan/reload)の窓口は今のところ CLI(§軸B が GUI 化の余地)。
トレイの実行時は WinForms(トレイ = NotifyIcon)+ WinRT トースト(`ToastNotificationManagerCompat`)。
`UseWPF=true` は ADR-0009 のフレームワーク選定(WPF)によるもので、現トレイは WPF 型をまだ使っていないが、
同スタックに M5 編集本体を後載せできるよう有効化してある。

アーキテクチャは **2 ツリーの疎結合**:

- **取込・サービス系**(publisher): `Radirec.Hosting → Radirec.Core`、`Radirec.Cli`(= `radirec.exe`)は
  Hosting と Core の**両方を直接参照**する
- **通知受け手系**(subscriber): `Radirec.Gui`(= `Radirec.Gui.exe`)→ `Radirec.Gui.Core`
- 両ツリーは **ProjectReference を共有しない**。繋ぐのは EventLog 上の **バージョン付き JSON 契約
  (EventID 1001)** だけ(型共有を避ける。ADR-0008/0009)。
- 制御チャネル(ADR-0010)のクライアント/サーバ実装は **`Radirec.Hosting/Control/`** にあり、
  Cli のコマンドはそれを薄く使うだけ。

**この地図の各軸は、この疎結合に対して圧力をかける向きが異なる**(§地図から読める構造)。

## 軸A — 通知トレイの深化(サービス→ユーザー / 受信方向)

出荷済みトレイの増分改善。ゲートなし。

| 項目 | 根拠 | 依存・触る面 | 疎結合への影響 |
|---|---|---|---|
| **A1 専用アイコン**(#52 で着手) | `Program.cs` の `TODO(実機)`(現状 `SystemIcons.Application`) | `Radirec.Gui` のみ(アセット差し替え) | なし。最小 |
| **A2 不在中サマリ** | ADR-0009「ブックマークで後載せ」(`readExistingEvents:false` の裏返し) | EventLog の bookmark 機構。**受信側 `Radirec.Gui` で完結**(EventLog が履歴を保持) | 契約変更**不要**。Gui 内で閉じる |
| **A3 失敗の通知(要検討)** | 現状シグナルは新規取込のみ(EventID 1001)。取込失敗は自由文ログのみ | サービス側が失敗シグナルを発行(新 EventID or ペイロード拡張)→ **JSON 契約変更** | 契約を厚くする方向。サービス側とセットのエピック |

## 軸B — 制御チャネルの GUI 化(ユーザー→サービス / 操作方向)

named pipe(ADR-0010)を GUI から叩く。usage が「GUI からの操作は今後追加予定(#5)」と予告済み。

| 項目 | 根拠 | 依存・触る面 | 疎結合への影響 |
|---|---|---|---|
| **B1 トレイメニューに rescan/reload/status**(#50 で着手) | usage.md「GUI からの操作は今後(#5)」 | `Radirec.Gui.Core` に pipe クライアント + 制御 DTO を**自前保持**(4バイト長フレーミング + envelope + 定数)。契約テストでドリフト検出 | **維持**。①`Gui→Hosting` 統合 / ②共有 lib 抽出 の二択は誤り。ADR-0010 が `ControlContract.cs` で既に規定した③=「別 exe の GUI は跨プロセス型を共有せず JSON 契約に自前 DTO で追従」(通知経路と同じ流儀)を採る。named pipe を EventLog と同じ「バージョン付き JSON を運ぶトランスポート」として扱い、共有 ProjectReference を新設しない |
| **B2 状態の可視化** | ADR-0010 の status verb | B1 のクライアント + 定期ポーリング(status をツールチップ/小窓に表示) | B1 に従属。**#50 はメニュー開時問い合わせに留め、常時ポーリングは B2 として後回し** |

## 軸C — M5 編集アプリ本体(キュレーション / 条件付き保留)

issue #5 の本体。**「CLI 運用で不足が判明したら着手」と明示ゲート**(requirements §10.1)。現時点では未着手。

| 項目 | 根拠 | 依存・触る面 | 疎結合への影響 |
|---|---|---|---|
| **C1 一覧グリッド編集** | #5 / §10.1(title/artist/album/albumartist/disc/track の直接編集) | **WPF UI 本体**(現状 WPF 型ゼロ → ここで初めて WPF/xaml が実体化) | — |
| **C2 TagLib# で NAS 直接編集 + リフレッシュ** | #5 / §8(取込後の権威は編集側) | **`Radirec.Gui → Radirec.Core` の直接参照**(タグ書込・Jellyfin 連携を使う) | **疎結合を意図的に崩す**。Gui→Core の初参照で 2 ツリーを統合 |
| **C3 配布フロントエンド** | §14 | C1/C2 + `LocalFolderTarget`(§10.2)+ 非技術者向けインストーラ | 別次元(配布境界の確立) |

## 地図から読める構造(将来判断の要点)

1. **3 軸は疎結合への圧力の向きが違う**:
   - **A2/A3(契約拡張)** = EventLog 上の JSON 契約を厚くする方向。`schemaVersion` + 両側 golden JSON
     テストで吸収でき、2 ツリーは維持される。
   - **B1(制御 GUI 化)** = named pipe を A2/A3 の EventLog と同じ「JSON 契約トランスポート」として扱い、
     `Radirec.Gui.Core` が pipe クライアント + 制御 DTO を**自前保持**する(ADR-0010 が既に規定。#50)。
     共有点を新設しない = 2 ツリーは維持される。※かつて①`Gui→Hosting`統合/②共有 lib 抽出の二択と
     書いていたが、どちらも疎結合を崩す方向で誤り。共有 lib 抽出は「両ツリーは ProjectReference を共有
     しない」不変条件を破る、より大きな決定を B1 に紛れ込ませることになる。
   - **C2(編集)** = `Gui→Core` の直接参照で 2 ツリーを統合する。最も大きな構造変化(#5 本体の賭け)。
2. **ゲート**: 軸 C は CLI 運用の不足が判明するまで着手しない(#5)。A/B は出荷済みトレイの増分でゲートなし。
3. **契約変更を伴う項目(A3)はサービス側とセット**。単独では動かせない(publisher = Hosting も同時に変える)。

## 見送り(検討済み・現時点で不要)

- **トースト深リンク**(タップで Jellyfin アルバムを開く): ADR-0009 が保留にしていた項目。COM activator
  登録とペイロードへの Jellyfin URL 追加(契約変更)が要る割に、現時点で必要性が薄いと判断し**見送り**。
  将来 Jellyfin への即時遷移が欲しくなったら再検討する。

## 運用(この地図の使い方)

- **issue はまだ切らない**。A/B/C の多くはゲート/保留中で「実行可能な単位」に達していない。
- **ある軸を greenlit した時点で**、その軸の項目を issue #5(または新エピック)配下に sub-issue として切る。
- 決定(採用/不採用・設計方針)が出たら、この地図ではなく該当 ADR / requirements を一次資料として更新し、
  この地図は「オプションの俯瞰」に保つ。
