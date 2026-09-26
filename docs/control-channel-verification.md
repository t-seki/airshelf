# 制御チャネル Windows 実機検証ランブック(ADR-0010 / #33・#50)

> **⚠ OBSOLETE([ADR-0012](adr/0012-retire-service-for-tray-resident-per-user.md) / #64)**: トレイ常駐移行で
> named pipe 制御チャネルは退役し、制御(rescan/reload/status)はトレイメニューからの in-process 呼び出しに
> なった。本ランブックと `deploy/verify-control-channel.ps1`(削除済み)が検証する pipe/ACL/`radirec.exe status`
> verb は存在しない。歴史的記録として残す。
> → **#179 で形を変えて戻った**([ADR-0020](adr/0020-cli-as-agent-front-via-control-channel.md)):CLI の `radirec status` / `reload` が
> トレイの受け口へ頼む。パイプはユーザーごとの名前(`radirec-control-<SID>`)で、同じユーザーへは `PipeOptions.CurrentUserOnly` で絞る
> (本ランブックの固定名・`PipeSecurity` とは違う)。今の実機検証の手順は #163。
> → #180 で `radirec ingest`(走査の実行)も同じ受け口へ頼むようになった。
> → **#214 で、Windows では `CurrentUserOnly` をやめた**:.NET の `CurrentUserOnly` はユーザーの SID ではなくトークンの所有者で
> ACL を作り・比べるので、UAC で昇格した端末(所有者が `BUILTIN\Administrators`)の CLI が非昇格のトレイに繋がらなかった。
> 今は `ControlPipeSecurity`(`Hosting/Control/`)が、昇格の有無に関係なく許可と所有者を**ユーザーの SID** で作り、CLI は
> 繋いだ後にパイプの所有者がユーザーの SID かを確かめる(なりすまし対策)。Windows 以外は今も `CurrentUserOnly`。

制御チャネル([ADR-0010](adr/0010-gui-service-control-via-named-pipe.md))は、WSL では named pipe の
ACL(`PipeSecurity` は `PlatformNotSupported`)・実サービスの listener+worker 疎通・SQLite 再オープンが
**構造的に検証できない**。よって単体テスト(WSL・golden/round-trip/配送不変条件)を通過しても、Windows 実機で
以下を確認するまで #33 の完了条件は満たされない。本書はその手順書。

## 前提

- `deploy/install.ps1` でインストール済み(実行体 `C:\Program Files\radirec\radirec.exe`、データ
  `%ProgramData%\radirec\`、サービス名 `Radirec`)。
- サービスが Running(`Get-Service Radirec`)。
- **操作者本人のセッションで実行**する。既定インストールではサービス実行アカウント = 操作者なので、
  自 SID 限定 ACL で疎通する(split-privilege 構成は下記 ④ / #40)。

## 自動検証(`verify-control-channel.ps1`)

大半は同梱スクリプトで自動チェックできる。管理者権限は不要(config 変更を伴う ③ は
`%ProgramData%\radirec\radirec.json` への書込権が要る = 通常は操作者=サービスアカウントで可)。

```powershell
# 既定パスで全項目
.\deploy\verify-control-channel.ps1

# config 変更/ファイル投入を伴う ③⑥ を省略(読み取り専用の確認だけ)
.\deploy\verify-control-channel.ps1 -SkipDestructive

# 非既定インストール
.\deploy\verify-control-channel.ps1 -ExeTarget 'D:\radirec\radirec.exe' -DataDir 'D:\radirec-data'
```

各行が `[PASS]/[FAIL]/[SKIP] <id> <名前>` で出力され、末尾に集計。FAIL が 1 つでもあれば exit 1。
**③ は `radirec.json` を生バイトでバックアップし、必ず原状復帰してから元 config を reload する**(非破壊)。

### 自動チェックが担う項目(ADR-0010 Consequences)

| # | 項目 | スクリプトの検査 |
|---|---|---|
| ① | status/rescan/reload が稼働サービスに繋がり応答 | 各 verb の exit 0 + 期待出力 |
| ② | 認可(別ユーザー拒否) | **自動判定しない**(named pipe の DACL は PowerShell から確実に読めず、`Get-Acl` は pipe パス非対応・成功しても SID/日本語グループ名の取りこぼしで誤 PASS になりうる)。SKIP を出し、下記**手動手順 ②**へ誘導する |
| ③a | reload で RadioRoot 変更が反映 | 一時ルートへ変更→reload→status が新ルートを反映するまでポーリング(表の折返しに強い正規化照合) |
| ③b | 不正 config は拒否・サービス生存・旧構成維持 | 壊れた JSON を書いて reload→exit 1+「拒否」・サービス Running・status が旧ルートのまま |
| ⑤ | listener がパイプ churn で永久停止しない | status を 30 連打し全応答(dispose→再生成の窓を通す) |
| ⑥ | ファイル着信 in-flight 中の reload で落ちない | 監視ルートへ junk ファイルを高速生成し続けながら reload 連打→サービス Running + status 応答 |

## 手動検証(自動化不可)

### ② 別ユーザーからの接続拒否(認可の本体)

ACL の代理検査(②自動)に加え、**別のローカルユーザーからは繋がらない**ことを実地で確認する:

1. 第 2 のローカルユーザー(例 `radirec-guest`)を作成し、そのユーザーでログオン(or `runas /user:`)。
2. そのセッションで `radirec status` を実行 → **接続が拒否される**(繋がって状態が返ってはいけない)。
   - 期待: `ConnectAsync` が ACL で弾かれ、`サービスが起動していません(制御チャネルに接続できません)` 相当で exit 1。
3. 元の操作者セッションでは同じコマンドが成功することを対比確認。

### ④ split-privilege 構成(対象外・単一ユーザー前提)

`install.ps1 -ServiceAccount` に**専用アカウント**(操作者と別)を指定した構成では、自 SID 限定 ACL が
操作者本人を締め出す。ただしこれは**単一ユーザー homelab の前提**(ADR-0009: 「操作者 = 通知ユーザー」)では
発生せず、[#40](https://github.com/t-seki/radirec-tool/issues/40) は won't-fix でクローズ済み:

- 既定構成では `install.ps1 -ServiceAccount` は `.\$USERNAME`(インストールした本人)= 操作者。サービス SID =
  操作者 SID なので自 SID ACL で繋がる。
- **昇格(管理者権限)は無関係**: UAC 昇格でもユーザー SID(`WindowsIdentity.GetCurrent().User`)は不変。
  → これはサービスの時代の ACL(ユーザーの SID で作っていた)についての記述。#179 の `PipeOptions.CurrentUserOnly` は
  ユーザーの SID ではなくトークンの所有者(昇格すると `BUILTIN\Administrators`)で比べるため、**昇格が効いていた**
  (管理者の端末の CLI が非昇格のトレイに繋がらない)。#214 でユーザーの SID で作り・比べるように直し、再び無関係になった。

将来 split-privilege(専用サービスアカウント)を採る場合のみ #40 を再オープンし、操作者 SID の ACL 付与を
実装したうえで「専用アカウント構成でも操作者本人の CLI が繋がる」を本節に追記して検証する。

## #33 完了チェックリスト

- [ ] `verify-control-channel.ps1` が **FAIL=0**(① ②代理 ③ ⑤ ⑥)
- [ ] 手動 ②: 別ユーザーからの `radirec status` が拒否される
- [x] ④: 既定構成(サービス=操作者)で運用 → split-privilege は対象外(#40 won't-fix)

すべて満たしたら #33 を close してよい。

## トレイ GUI からの制御(軸B / #50)

#33 は**サービス側 listener + CLI クライアント**を検証する。#50 は同じ制御チャネル(ADR-0010)を
**トレイメニュー(`Radirec.Gui`)**から叩く**提示層**を足すもので、pipe プロトコル自体は #33 で検証済み。
新規の未検証面は**トレイ UI の挙動だけ**で、これは対話 GUI ゆえ `verify-control-channel.ps1` では
自動化できない(下記は手動)。Gui.Core の自前クライアント↔実 listener の疎通は WSL の往復契約テスト
(`GuiControlChannelTests`)で担保済みなので、ここで見るのは**UI の配線・表示・縮退**に限る。

### 前提(#33 前提に加えて)

- `deploy/install.ps1` 実行後、取込通知トレイ(`Radirec.Gui.exe`)が配置・起動され HKCU Run へ
  自己登録済み(タスクトレイに Radirec アイコンが常駐している)。
- サービスが Running(トレイの status 表示の前提)。

### 手動チェックリスト

- [ ] **メニュー表示**: トレイアイコン右クリックで「今すぐ再スキャン / 設定リロード… / 状態 / 最終スキャン / 終了」が出る。
- [ ] **status 反映**: メニューを開くと状態行が「稼働中(監視中)」or「稼働中(定期スキャンのみ)」に、最終スキャン行が
      時刻(ローカル時刻)+ トリガ + 取込件数に埋まる(開いた瞬間に問い合わせる。開くまでは「未確認」)。
- [ ] **rescan**: 「今すぐ再スキャン」→ 「手動再スキャンを要求しました」balloon。サービスが実際にスキャンを
      走らせる(EventLog / サービスログ、または直後に status を開き最終スキャンが更新されることで確認)。
- [ ] **reload(受理)**: 「設定リロード…」→ 「サービスが実質再起動されます」確認ダイアログ → **OK** で
      「設定リロードを受理しました」balloon。**キャンセル**では何も起きない(送信されない)。
- [ ] **reload(拒否)**: `radirec.json` を一時的に壊して(非破壊で退避 → 復元)reload → 「拒否されました」balloon が
      理由付きで出る。※`verify-control-channel.ps1 -SkipDestructive` 相当の注意で、必ず原状復帰する。
- [ ] **サービス停止時の縮退**: `Stop-Service Radirec` した状態でメニューを開く → 状態行が「サービス停止中」、
      **rescan/reload がグレーアウト(無効)**。**エラーダイアログが出ない・トレイが落ちない**。停止中に rescan/reload を
      押しても(グレーアウト前提だが)控えめな balloon のみで破綻しない。`Start-Service Radirec` 後に開き直すと復帰する。
- [ ] **UI 非フリーズ**: status 問い合わせ中(遅い場合含む)もメニュー操作・アイコン操作が固まらない。
- [ ] **多重発火なし**: rescan/reload を素早く再操作しても、in-flight 中は二重送信されない。

すべて満たしたら #50 の実機検証を完了とし #50 を close してよい(WSL の単体テスト緑は前提)。
