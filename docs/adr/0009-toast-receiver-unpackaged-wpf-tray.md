# 取込通知の受け手は非パッケージ WPF トレイアプリ + 従来 WinRT トーストにする

Status: proposed(#19 の受け手 GUI / #5 の tracer slice)

[ADR-0008](0008-ingest-notification-via-eventlog-signal.md) でサービスは取込要約シグナルを EventLog(EventID 1001)へ発行するようにした。その**受け手**(ユーザーセッションでネイティブトーストを出す常駐アプリ)を、#5(M5 デスクトップ編集アプリ・保留中)の**最初の tracer slice** として実装する。編集グリッド等の本体は保留のまま、まず「1001 を購読しトースト表示するだけ」の最小 GUI を作る。

受け手をどの UI フレームワーク・どのトースト API で作るかは、[ADR-0005](0005-single-exe-cli-service-merge.md) の **単一自己完結 exe(外部ランタイム/ブートストラッパ無し)** 配布方針と衝突しうるため、ひっくり返しにくい。

## 決定

- **フレームワーク = WPF**。単一自己完結 exe の `dotnet publish` が枯れており、トレイ常駐が成熟。既存 `net10.0-windows` スタックと親和。受け手は `radirec.exe`(CLI/サービス)とは別の**独立 exe `Radirec.Gui`**(実行モデルがユーザーセッション GUI で別物。ADR-0005 の CLI+service 統合とは無関係)。
- **トースト = 従来 WinRT(`CommunityToolkit.WinUI.Notifications` の `ToastNotificationManagerCompat` / `ToastContentBuilder`)**。非パッケージ Win32 デスクトップから動く前提の API で、初回 `Show()` 時に **AUMID + スタートメニューショートカット**を自前生成する。
- **WindowsAppSDK `AppNotificationManager` は採らない**。API は素直だが **Singleton MSIX パッケージに依存**し、self-contained アプリでは追加考慮(依存 MSIX の扱い)が要る([MS Learn](https://learn.microsoft.com/en-us/windows/windows-app-sdk/notifications/app-notifications/) 参照)。単一自己完結 exe(ADR-0005)と衝突する。
- **表示専用に割り切る**。トーストのタップで Jellyfin アルバムを開く深リンクは #19 でも任意。**タップ activation を実装しなければ COM activator の登録が不要**になり、AUMID + ショートカットだけでトースト**表示**が成立する。深リンクは #5 本体で後載せ。
- **自動起動 = 自己登録(HKCU Run)**。トレイアプリが初回起動時に `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` へ自分を登録し、以降ログオンで自動常駐。admin/PowerShell 不要でタスクマネージャの「スタートアップ」から可視・ON/OFF 可能。登録ロジックはアプリ内に閉じる(EventLog ソース登録の教訓と同様、機構をコードに寄せる)。`install.ps1` は exe 配置 + 初回起動のみ担う。
- **キャッチアップ = 今から先だけ(`readExistingEvents: false`)**。起動時は新規 1001 のみ購読し、過去分は再生しない。トレイ停止中の取込トーストは取りこぼすが、トーストは「今起きた」揮発シグナルで、取込自体は完了・Event Viewer に記録が残る。ログオン時に過去トーストが大量に出る失敗モードを回避する。
- **ペイロード契約は JSON で疎結合**。受け手は EventLog メッセージの JSON を**自前の DTO** にデシリアライズし、`schemaVersion` で互換を判定する。Core/Hosting の C# 型を跨プロセスで共有しない(型共有よりバージョン付き JSON 契約 + 両側テストの方が壊れにくい)。

## なぜ WPF + 従来トーストか

- **配布制約が一意に決める**: 決定の核は「UI フレームワークの出来」ではなく **非パッケージ単一 exe からトーストを出せるか**。ネイティブトーストはシェル機能で、WPF/WinUI3/Avalonia いずれも同じ WinRT に到達する。差がつくのは**登録と配布**で、そこが ADR-0005 と衝突するのが WindowsAppSDK。
- **非閉塞**: 表示専用・HKCU Run・read-from-now はいずれも最小コストで、後から深リンク(COM activator)・不在中サマリ(ブックマーク)・編集 UI を足す道を塞がない。

## Considered Options

- **(採用) WPF + `ToastNotificationManagerCompat`** — 単一自己完結 exe と親和、非パッケージ Win32 で枯れた経路、追加インフラ最小。
- **WindowsAppSDK / WinUI 3(`AppNotificationManager`)** — API は最新・素直だが Singleton MSIX 依存で単一自己完結 exe が壊れる。packaging(MSIX)前提だと配布方針の全面変更を招く。
- **Avalonia** — 不要な cross-platform。トーストは結局 Windows 専用 WinRT compat か第三者実装になり利点が産まれない。
- **自動起動をタスクスケジューラ / installer 側で** — 可動部増・登録が PowerShell に散る。自己登録(HKCU Run)の方がアプリに閉じてテスト可能。

## Consequences

- 新規プロジェクト `Radirec.Gui`(`net10.0-windows10.0.x`。WinRT トースト API のため Windows SDK バージョン付き TFM が要る)。Release zip に 2 つ目の exe を同梱し、`install.ps1` / `update.ps1` を GUI 配置・初回起動に対応させる。
- **要実機検証(Windows)**: 非パッケージ WPF から `ToastNotificationManagerCompat` が AUMID ショートカットを自動生成しトーストが**対話デスクトップに出る**こと。サービス取込 → 1001 発行 → トレイがトースト、の E2E。HKCU Run 自己登録がログオンで効くこと。
- ペイロード DTO は publisher(Hosting)側の envelope とドリフトしうる。両側に golden JSON の契約テストを置き、`schemaVersion` を上げたら受け手も追従する運用にする。
- 深リンク・不在中サマリ・編集グリッドは本 slice の対象外(#5 本体・別 issue)。
