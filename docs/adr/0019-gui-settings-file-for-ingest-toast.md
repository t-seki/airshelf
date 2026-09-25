# 新規取込のトーストの切り替えは管理画面用の設定ファイルに持つ

Status: accepted(#152 の grill、2026-09-25。requirements §10.3、[ADR-0011](0011-ingest-failure-as-separate-eventlog-signal.md) の失敗の de-dup、[ADR-0017](0017-management-window-owns-programs-json.md)、[ADR-0018](0018-attention-items-derived-from-state.md) の要対応と関連)

管理画面の設定ページ(requirements §10.3)で、新規取込のトーストを出すかを切り替えられるようにする。
いまは `radirec.json` の `NotificationsEnabled`(既定 `true`)がこれを決めているが、`false` にすると新規取込のトーストだけでなく、
取込失敗のトーストも止まり、失敗のストア(ADR-0011 の de-dup)も使われなくなる。§10.3 の決定は「失敗のトーストは常に出し、
止められるのは新規取込だけ」。

一方で、設定ページでは `radirec.json` を**読み取り専用**で見せ、GUI で編集しない(#152 の非目標)。`radirec.json` は
`//` コメント付きで書けるファイルで、利用者がエディタで直す。チェックボックスで切り替える値を、どこに保存するかを決める。

## 決定

- **管理画面用の設定ファイル `gui-settings.json` を、`radirec.json` と同じフォルダ(既定は `%LOCALAPPDATA%\RadirecData\`)に置く**。
  書くのは管理画面だけで、いまのキーは `IngestToastsEnabled`(新規取込のトーストを出すか)の 1 つ。
  書くときは一時ファイルに書き切ってから置き換える(途中で切れた壊れた JSON を残さない)。読めない・壊れているときは
  `gui.log` に書いて、値が無いものとして続ける。
- **`radirec.json` には一切書かない**。
- **後方互換**:`gui-settings.json` にまだ値が無いときだけ、`radirec.json` の `NotificationsEnabled` を初期値として読み、
  `gui-settings.json` に書く(`false` なら新規取込のトーストを止めた状態で始める)。以後は `gui-settings.json` の値だけを使う。
  `radirec.json` を読めない起動では、書かずに「出す」で始める(旧設定が分かる起動まで値を固めない)。
  `NotificationsEnabled` のキーは「旧設定」として残す(`radirec.json` は未知のキーを読み込みで失敗させるので、消すと既存の
  設定が読めなくなる)。
- **止めるのは提示側で、成功要約だけ**。取り込みの合成(`CompositionRoot.SelectPublishers`)は、この設定に関係なく
  成功要約と失敗の両方を発行し、失敗のストアも使う。トレイは、新規取込(成功要約)のトーストを出す瞬間に設定を読み、
  止めていれば出さない。切り替えは設定リロード無しですぐ効く。

## 検討した案

- **`radirec.json` のキーを GUI で書き換える**:保存先は 1 つで済むが、「`radirec.json` は GUI で編集しない」に反する。
  1 つのキーを変えるには、全体を書き直す(利用者のコメントが消える)か、テキストを部分的に書き換える(壊れやすい)しかない。
  ADR-0017 は `programs.json` についてコメントを失う代償を引き受けたが、それは番組の設定を GUI の主な書き手に移すからで、
  トーストの 1 つの切り替えのために `radirec.json` の書き手を増やすのは見合わない。
- **チェックボックスをやめ、`radirec.json` の新しいキー(例 `IngestToastsEnabled`)で切り替える**:いちばん単純だが、
  「画面から切り替える」という §10.3 の決定を変えることになる。

## 結果

- 利用者が直す設定(`radirec.json`)と、管理画面が持つ設定(`gui-settings.json`)が別のファイルになる。設定の置き場所が 1 つ増え、
  `gui-settings.json` はバックアップや移行のときに `radirec.json` と一緒に扱う(同じフォルダにあるので、フォルダごと扱えば足りる)。
- `NotificationsEnabled` は初期値の出どころとしてだけ残る。`gui-settings.json` に値が入った後に `radirec.json` の
  `NotificationsEnabled` を変えても効かない(設定ページで切り替える)。
- 新規取込のトーストを止めても、取込失敗のトーストは出て、失敗のストアにも記録される。ADR-0018 の結果の節が残した宿題
  (失敗のストアは取込通知が無効だと使われないので、通知の有無から切り離す)は、この決定で片付いた。兄弟フォルダの知らせ(新規取込の
  トーストの 1 行)は、止めると一緒に出なくなる(`gui.log` には残る。要対応へ移すのは #155)。
- 管理画面が今後ほかの設定(画面の状態など)を持つときも、`gui-settings.json` に足す。
