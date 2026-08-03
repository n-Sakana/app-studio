# App Studio 仕様

## 1. 目的

App Studio は、インストール権限の限られた Windows 端末へテキストファイルだけを持ち込み、既存アプリケーションの UI 部品を調査するためのツールである。対象を最前面のまま、Win32 と UI Automation (UIA) の情報、切抜き画像、locator候補、操作試験、イベント、診断を一つのセッションへまとめる。

本書が仕様の正本である。実装済み初版の利用仕様を説明する。

## 2. 配布・起動構成

- 持ち込む実行形式はない。`launch.vbs` が Windows PowerShell 5.1 を非表示起動する。
- `app-studio.ps1` は `src/*.cs` を名前順で読み、.NET Framework の `Add-Type` でメモリ内コンパイルする。
- UI は WPF、overlay はclick-through/non-activateのWPF窓である。
- WP-S実測で採用した構成Cとして、UIA取得とUIA操作はOS標準の `powershell.exe` 子processへ隔離する。active workerとwarm spareを各1つ持ち、1,500msのhover期限超過時はactiveを終了してspareへ切り替える。
- workerとのIPCはUTF-8 JSON Linesのstdin/stdoutであり、生のUIA要素をprocess境界へ渡さない。
- 外部通信、追加Runtime、NuGet package、同梱DLLは使わない。

## 3. 主な画面動線

画面は「1. 調べる画面を選ぶ → 2. 何をするか選ぶ → 実行中の表示」の3段で、段ごとに必要なものだけを出す。実行中でない段の部品は画面にもaccessibility treeにも出さない。

1. **対象を選ぶ**: 可視のトップレベルwindowを、アプリ名・window名・大きさ・pidで一覧する。［カーソルで指して選ぶ］でドラッグ照準からも選べる。
2. **目的を選ぶ**: ［自動でひととおり洗い出す］［自分で操作しながら記録する］［部品をひとつ試しに操作する］［AIに操作を考えてもらう］の4つを、1行の説明つきで並べる。
3. **自動調査**: 対象processの可視トップレベルwindow（最大12）について、操作せずに現在表示中の要素を集める。件数と進捗を前面に出し、［中止する］でいつでも止められる。終了後は要約を先、部品一覧と「取得できなかったもの」を折り畳みで後に置く。
4. **手動観察（操作レコーダー）**: hover取得を動かしたまま、指した部品・滞在・clickを調査logへ自動追記する。［いったん止める］［表示を止める］［この部品を残す］［終わる］だけを出す。［終わる］で**記録した操作を起きた順に確認する段**へ進む（3.3.2）。
5. **操作試験**: 自動調査の一覧から選んだ部品、またはカーソル下の部品を対象にする。既定は読取専用で、対象を変えるkindは明示toggle後だけ実行できる。
6. overlayは対象矩形の枠と4行の要約を表示する。修飾キー中は要約を隠す。ツール本体とoverlay自身は結果側で判別して捨てる。hoverは止めない。
7. ［調査パックを書き出す］で選択folder配下へ新しい調査packを作る。同名folderがあれば `_2`, `_3` とし、既存packを上書きしない。
8. **案件（AIに操作を考えてもらう）**: 3.5 の4段。自動調査の結果画面の［この結果をAIに渡す］からも入る。
9. **履歴**: 対象選択・目的選択・案件結果の各画面から［これまでの記録を見る］で入る。日時・対象アプリ・やりたいこと・成功／失敗を新しい順に一覧し、選ぶと `case.md` をそのまま読める。［この案件の続きをする］で回答取り込みから再開する。

## 3.1 自動保存

セッション開始時に `runtime/live-session/<sessionId>/` を作り、**明示的な保存操作なしで**次を逐次追記する。1行書くたびにflushし、2秒ごとにOSへ確定させるため、強制終了しても書けた分は残る。

| file | 内容 |
|---|---|
| `events.jsonl` | session開始、対象選択、調査開始・要約、操作試験結果、値方針変更、worker再起動、緊急停止 |
| `elements.jsonl` | 自動調査で見つけた要素1件1行、および明示的に残した部品。`screenId` と `componentId` を持つ |
| `screens.jsonl` | 調査した画面1件1行。screenId・HWND・矩形・componentIds・撮れた写真のpathとhash、撮れなかった理由 |
| `shots/screen-<scanId>-<screenId>.png` | 調査直後に撮った画面ごとのscreenshot |
| `observations.jsonl` | 手動観察の要素遷移・滞在・click・click前後差分・アプリ側の変化 |
| `pointer-raw.jsonl` | 座標の生trail（読みやすい記録とは分離する）|
| `summary-<scanId>.md` / `observation-summary.md` | 人向け要約 |
| `environment.json` | 起動時診断 |

書けない場合は画面に「自動保存できません」と理由を出し、記録件数を0のまま進めない。

## 3.2 自動調査で記録する事実

要素ごとに、その要素が属する画面(`screenId`)と `componentId`(`E<n>`)、取得元(`sources`: uia / msaa / win32 / hit-test)、process、top-level HWND、HWND（無い場合は `hasHwnd:false`）、class/realClass、control type（および localizedControlType、MSAA role）、name、AutomationId、`RuntimeId`、矩形、visible/enabled/offscreen/keyboardFocusable/isPassword、対応操作pattern、親子path、frame部品かどうか、ctrlId、style/exStyle を残す。値本文はscanでは読まない(`valueKind: not-read`)。

取得経路は UIA(raw view walk) → MSAA(`AccessibleChildren` walk) → Win32 child window列挙 → 座標grid hit-test で、hit-testは前3者が薄いときだけ動く。**patternは可否propertyを読むだけで実行しない。** provider別に state / 件数 / 所要 / 打切り理由(`SCAN-MAXNODES` `SCAN-TIMEOUT` `SCAN-MAXDEPTH` `UIA-EMPTYTREE` `MSAA-NOELEMENT` `HITTEST-GRID` など)を必ず残し、要約の末尾に「これで全部だという保証はない」と明示する。

## 3.3 手動観察で記録する事実

対象processに限定し、指した部品が変わったときだけ `observe.enter` / `observe.leave`（時刻・座標・識別情報・滞在ms・その要素内での移動回数）を書く。clickは `GetAsyncKeyState` によるmouse buttonの遷移だけを見る。click直後に同じ座標を取り直し、`observe.click.result` として前後の identity / name / state / enabled / 矩形 / windowタイトルの差分と、その間に届いたアプリ側eventを残す。差分が無い場合は「この位置では変化を観測できなかった」と明記する。

**keyboardは一切読まない。** `SetWindowsHookEx`、`WH_KEYBOARD`、`GetKeyboardState`、`ToUnicode`、`GetClipboardData` は製品sourceに存在せず、`test-observe` がその不在を固定する。対象外processのclickは「対象外でclickがあった」事実だけを残し、要素は書かない。

### 3.3.1 対象の範囲はprocess1つではない

電卓のようなpackaged applicationは、window一覧に出るのは `ApplicationFrameHost` だが、中身は別processが描く。選んだwindowのprocess idだけで絞ると**一件も記録できない**。そこで対象選択時に `WindowTools.ContentProcessIds` で子windowを辿り、その窓を実際に描いているprocessを受入集合へ加える。ライブ表示の「対象外」判定も同じ集合を使う。受入集合は `observe.start` の `targetProcessIds` に残す。

clickの検出は `GetAsyncKeyState` を50ms間隔で見る。「今押されているか」だけでは、polling間隔の間に始まって終わったclick（trackpadのtap等）を落とすため、「前回呼び出し以降に押されたか」も併せて見る。実測では押下30ms以上なら前者だけでも拾えるが、0msのclickは後者でしか拾えない。**低levelの入力hookは使わない。**

### 3.3.2 記録した操作の確認

［終わる］で、いま記録した内容を**保存済みのfileから読み戻して**起きた順に出す。1行につき順番・指した/clickした・部品の種類と名前とAutomationId・座標を出し、clickには「アプリ側に変化があった」「ここでは変化を観測できなかった」を添える。保存先(`runtime/live-session/<sessionId>/`)を画面に出し、案件が開いていれば同じ内容を `case.md` の「記録した操作」節へ自動で綴じる。**別途の保存操作は要求しない。**

画面に出す一覧は記録した件数からではなくfileから作る。読み戻せなかった場合は空欄にせず、理由を一覧の位置に出す。（session logは書込みhandleを開いたままなので、読み戻しは書き手と共存する共有指定で開く必要がある。`SessionLog.ReadAllLines` がそれを担う。）

## 3.4 収集しないもの

キー入力の内容、資格情報、clipboard、対象アプリ以外の画面内容は収集しない。値本文は既定(`maskedOnly`)では長さだけを記録し、`IsPassword` は全modeより優先して取得・表示・記録をしない。自動調査は値本文を読まない。

## 3.5 案件（調査 → AIへ依頼 → 回答取り込み → 操作試験）

対象アプリの調査結果を材料に、実現したい操作をAIへ依頼し、返ってきた手順を取り込んで実際に試し、結果を1つのfolderへ残す。API直結はせず、依頼文のclipboard copyとfileの手動添付で運ぶ。

段は4つで、1段につき必要なものだけを出す。

1. **依頼をつくる**: 集まっているもの（screenshot / 部品件数 / 観察したclick数）を1行で出し、**やりたいことは自由入力の1欄だけ**にする。回収内容の選択肢・操作ひな形・categoryは置かない。段へ入ると対象windowを前面に出して screenshot を撮り、撮り直しもできる。［依頼文をコピーする］で `investigation.md` / `request.txt` / `elements.json` / `screens.json` / `handoff.json` と、**添付する2fileだけを入れた `handoff/` folder**（`handoff.txt` と `screens.pdf`）を書き、依頼文をclipboardへ入れる。［添付するファイルの場所を開く］は `handoff/` を開く。添付が1つでも作れなかった場合は理由を出し、**［回答を取り込む］へ進ませない**。
2. **回答を取り込む**: 貼り付け欄と［貼った内容を読み取る］だけ。読めたら手順を日本語の一覧で出し、読めなければ**理由を全部出して1つも実行しない**。
3. **実行**: 進捗と1手順ごとの結果、［中止する］だけを出す。
4. **結果**: 成功／失敗／変化を観測できず／未実行の件数と、手順ごとの経路・反応・使えた識別情報を出す。書き出しは済んでいる。

### 3.5.1 AI回答の形式

回答はJSONオブジェクト1つである。前後に説明文があってもよく、` ``` ` fenceに入っていてもよい。取り込み側は最初の均衡した `{...}` を取り出すだけで、**壊れた回答を推測で直さない**。

```
{ "format": "pui-plan", "version": 1, "title": "...", "notes": "...",
  "steps": [ { "id": 1, "action": "setValue", "target": { "element": "E12" },
               "value": "...", "expect": "...", "why": "..." } ] }
```

- `action` は操作試験と同じ10種（`read / focus / invoke / toggle / select / expand / setValue / scroll / click / keys`）だけで、**AI用に別の操作一覧を持たない**。
- **key と `action` の値は英語ASCIIの固定語だけである。** 対象アプリ固有の表示名を原文で書いてよいのは `title` / `notes` / `expect` / `why` の中身に限る。最上位keyは `format / version / title / notes / steps`、step keyは `id / action / target / value / expect / why` で、それ以外は「使わなかった項目」として出す。
- 部品の指し方は `{"element":"E12"}` か `{"point":{"x":..,"y":..}}` の2つだけ。`E<n>` は自動調査が振った nodeId で、`handoff.txt` の部品台帳に載せた Component ID である。解決先は「矩形の中心 + HWND」であり、結果一覧から部品を選んで操作するのと同じ経路を通る。
- `setValue` と `keys` は `value` が要る。
- 検証は全部か無かである。1手順でも不正なら**その手順だけ捨てず、plan全体を拒否して理由を全部出す**。このツールが使わない項目は捨てるのではなく「使わなかった項目」として画面と記録の両方に出す。
- 対象を変える操作を含むplanは、`write` toggleを入れるまで［この内容で実行する］を押せない。
- 実行はProbeRunnerをそのまま使う。読取専用の既定、覆われた点の拒否、passwordへの `setValue`/`keys` 拒否、5値のoutcome、`method` の記録はすべて操作試験と同一である。手順間は0.5秒あける。［成功しなかった時点で残りを実行しない］が既定でONである。

### 3.5.2 案件folder

`runtime/cases/<caseId>/` が1案件1folderで、`case.md` が正本である。

| file | 内容 |
|---|---|
| `case.md` | 中心となる記録。screenshot・調査・やりたいこと・依頼・回答・操作試験結果を順に綴じる |
| `shots/*.png` | 対象windowのscreenshot |
| `handoff/handoff.txt` | **AIへ添付する統合テキスト（1本）**。画面台帳・部品台帳・取得できなかったもの・観察結果を1fileへまとめる |
| `handoff/screens.pdf` | **AIへ添付する画面のPDF（1本）**。1画面1ページで、各ページ冒頭に Screen ID とページ番号を書く |
| `screens.json` | Screen台帳の機械可読正本（screenId・HWND・矩形・componentIds・写真のhashとPDFページ） |
| `handoff.json` | bundleId・premiseHash・添付2fileのsha256とbytes・ページ数 |
| `investigation.md` | 調査logの単独版（`handoff.txt` に同じ本文が入っている）|
| `request.txt` | AIへ貼る依頼文。文面は `assets/messages/request-template.txt` |
| `elements.json` | `E<n>` の解決表。再起動後に案件を開き直しても回答を実行できる |
| `answer-NN.txt` / `plan-NN.json` / `run-NN.jsonl` | 取り込んだ回答の原本、解釈したplan、手順ごとの結果 |
| `case.json` | 履歴一覧が読む索引 |

`case.md` の操作試験節には手順ごとに**使えた識別情報**（AutomationId / Name / ControlType / class / ctrlId / HWND / 座標）と**観測した反応**を残す。これが後で本格的な自動化を書くときの元資料になる。

部品表は250行を上限とし、超えた分・窓枠部品・今見えていないもの・位置が取れないものは件数を明記して外す。**黙って切り捨てない。**

### 3.5.3 Screen台帳とComponent ID

自動調査は歩いたトップレベルwindowを1つの**画面**として数え、走査順に `S1`, `S2`, ... を振る。その画面で見つけた要素は全て同じ `screenId` を持ち、`elements.jsonl` の各行に `screenId` と `componentId`（`E<n>`）が入る。

調査終了直後に、画面ごとに1枚ずつscreenshotを撮る。写真に写るのは対象なので、画面を1つずつ前面へ出してから撮り、終わったらApp Studioへ戻す。撮れた／撮れない は `screens.jsonl` と結果画面へ必ず出す。撮れなかった画面も台帳の行を持ち、理由（`SHOT-NORECT` など）を書く。**行ごと消さない。**

添付は2fileだけである。

- `handoff.txt` … Screen台帳（Screen ID / PDFのページ番号 / window名 / class / HWND / 位置 / 部品数 / 写真の有無）、部品台帳（Component ID と screen 列を持つ）、取得できなかったもの、観察結果。
- `screens.pdf` … 1画面1ページ。ページ冒頭に `Screen S1   page 1 of N` と HWND・寸法・部品数を書く。画像はFlateDecodeで**無劣化**に格納する。同梱fontを持たないためcaptionはASCIIのみで、非ASCIIのwindow名は `?` になる旨をcaption自身に書く。原文は `handoff.txt` にある。

自動調査をせずに案件へ入った場合（3.5 の1段目は調査が無くても通る）、調査した画面は0件である。このとき**段へ入ったときに撮った対象の写真**を1ページの画面として使い、「この画面は写真だけで自動調査をしていない。画面座標で指すこと」を台帳に書く。添付は2fileのまま保たれる。写真も撮れなかった場合はPDFを作らずテキスト1fileで送れるようにし、**依頼文の添付一覧は実際に書けたfileだけを並べる**。無いfileの名前を依頼文へ書かない。

### 3.5.4 回答は自分が答えた前提にだけ属する

依頼を作るたびに `bundleId` と `premiseHash` を発行する。`premiseHash` は scanId、各画面（screenId・HWND・矩形・部品数）、表に載せた各部品（Component ID・screenId・Name・AutomationId・ControlType・矩形）から作る。

回答を取り込むとき、いま使っている台帳から `premiseHash` を作り直して照合する。**一致しなければ読むだけで実行させない**。一致しない典型は「依頼を作ったあとで対象をもう一度調べた」場合で、そのとき `E<n>` は当時と別の部品を指す。画面には次にすること（依頼を作り直して聞き直す）を出し、記録には両方のhashを残す。

取り込んだ回答の原文はそのまま `answer-NN.txt` へ書き、`case.md` に sha256 と文字数と `bundleId` を残す。**取り込み側は回答を書き換えない。**

## 4. ホットキー

| 操作 | 既定 |
|---|---|
| 手動観察の開始・終了 | F8 |
| フリーズ・解除 | F9 |
| この部品を残す | F10 |
| 全景撮影 | F11 |
| メモへ移動 | F6 |
| 緊急停止 | Shift+F12 |

登録済みの組合せと衝突した場合は `HOTKEY-TAKEN` を診断へ出し、ShiftまたはCtrlを足した代替を試す。実際の割当は `runtime/settings/hotkeys.txt` に保存する。設定を書けない場合もボタン操作で継続できる。緊急停止は調査とoverlayを止め、書込みtoggleを解除して読取専用へ戻す。

## 5. 取得層

### Win32

`WindowFromPoint`、class/real class、`GetDlgCtrlID`、style/exStyle、物理矩形、親、z順、PID/TID、monitorId、DPIを取得する。captionは `SendMessageTimeout(SMTO_ABORTIFHUNG)` 150msで読み、応答しなければ `partial / WIN32-HUNG` とする。UIAが失敗してもWin32結果は残る。

### UI Automation

hoverでは基本properties、固定時はsupported patterns、tree path、最大50 childrenを取得する。Custom/Pane/Windowが子を一つも公開しない場合は `partial / UIA-EMPTYTREE` とする。期限超過時はworkerを終了し、`unavailable / UIA-TIMEOUT` と `ACQ-RESTART` を残す。

### 画像

固定時の切抜きは `BitBlt`、黒画像なら `PrintWindow(PW_RENDERFULLCONTENT)` を試す。`IsPassword` 矩形は黒塗りする。全景は明示操作と警告の後だけ撮影し、自動maskできない限界をreportへ残す。

## 6. Locator

実際に取得できた材料だけから候補を作る。

- 非空かつ数字だけでない AutomationId + ControlType
- Name + ControlType
- UIA tree path
- 0/-1でないctrlId + 親class
- Win32 class path + index
- 対象client rect基準の相対座標

HWND、UIA RuntimeId、Value本文はlocator式へ入れない。生成直後と再起動後に候補を実解決し、matchCount、sameElement、durationMs、targetRunIdを追記する。0件はlow、安定材料で一意かつ同一ならhigh、複数一致はhighにしない。相対座標は常にlow以下である。WinFormsのprocess生成class/ctrlIdのように再起動後に変わった候補も削除せず、失敗とlow確度を残す。

## 7. 操作試験

kindは `read / focus / invoke / toggle / select / expand / setValue / scroll / click / keys`。結果は `success / failed / blocked / notSupported / unknown` の5値で、実際に使った `method` を必ず記録する。

読取以外は画面上の点に対して実行するため、実行直前にその点が対象要素のprocessのものか確認し、別のwindowが覆っていれば `policy.covered` でblockedにする。**覆われた点へ入力を出さない。**

ただし packaged application は frame windowを `ApplicationFrameHost` が持ち、中身を別processが描く（3.3.1）。この場合、点のprocessが要素のprocessと違うのは「覆われた」ではなく「対象が自分を描いている」である。そこで受入集合に、**その対象window自身の子windowを描いているprocess**（`WindowTools.ContentProcessIds`）を加える。別アプリのwindowは対象windowの子ではないので、覆いの拒否はそのまま働く。この区別が無いと、packaged applicationのwindowを指した非read操作は必ずblockedになる。

経路は UIA pattern、Win32 message、SendInput の順である。`focus` のWin32経路は対象windowの前面化（`SetForegroundWindow`）である。window全体のような入れ物は点の要素としてfocusを受け取れずUIA経路が失敗するので、この経路が無いと「対象を前面に出してから操作する」という当たり前の手順が失敗になる。**経路の繰り上がりは、UIA経路が notSupported を返した場合と、`focus` が失敗した場合だけである。** UIAが拒否した `setValue` を window message で押し通すことはしない。

patternを公開していても前後変化を観測できなければ `unknown` であり、successへ丸めない。同一要素・同一kindは1秒間隔に制限する。`setValue` だけ元値を保持してundoを提供する。`IsPassword` に対する `setValue` と `keys` は常にblockedである。

## 8. 値とmask

- `maskedOnly` が既定。値本文を永続化せず、length、kind、masked、maskRuleだけを記録する。
- `full` はセッション単位の明示選択で、`mode.change`、画面の常時表示、report結論帯へ残す。
- `none` はlengthも記録しない。
- `IsPassword` は全modeより優先し、取得・表示・記録をしない。
- `nameRegex / controlType / manual` maskを追加でき、ライブ表示にも同じ規則を適用する。
- 許可されたライブ値は画面だけに「表示のみ・未記録」と表示し、hover変更で置換、調査停止で消去する。
- Edit/DocumentのWin32 captionも値として扱い、RecordedValue policyを迂回してJSON/reportへ出ない。

## 9. 調査pack

出力folderには次を作る。

- `report.html`: 外部参照と編集要素のない自己完結report。PNGをdata URIで内包する。
- `session.json`: schemaVersion 1の機械可読正本。
- `shots/*.png`: 画像原本。
- `diagnostics.log`: `[時刻][LEVEL][CODE]` 形式の診断。
- `MANIFEST.json`: 各fileのbytesとSHA-256、session hash。
- `README.txt`: 開き方と、manifestは暗号化ではない旨。

書けない場所では `PACK-WRITE` を返し、別の場所へ勝手に出力しない。`environment.writeTargets` はstartup diagnostics、temporary shots、hotkey settings、各pack出力先を申告する。

## 10. 既知の限界

- 全景画像は自動maskできない。
- Java AWT/Swing、SAP GUI、製品固有APIは検出後に別経路が必要である。
- `SysListView32 / SysTreeView32` の行dataは初版では取得せず `NEEDS-B3` を出す。実対象で自動化に必要と確認された場合だけWP-B3を判断する。
- OCRと低level入力hookは初版対象外である。
- 画像、UIA、PrintWindow、SendInputの品質は対象実装・権限・desktop状態に依存する。
- 高DPI・複数画面の構成では物理矩形の一致を確認すること。RDPと対象現場のDPI混在は現地確認事項である。
