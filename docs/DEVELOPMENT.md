# App Studio 開発手順

## 前提

- Windows PowerShell 5.1
- .NET Framework 4.x と `csc.exe`
- WPF、Windows Forms、UIAutomationClient/UIAutomationTypes
- 開発試験用 fixture を表示できる interactive desktop

外部 package restore や network access は不要である。fixture の EXE は `tests/.build` に開発時だけ生成し、持込み配布物には含めない。

## 起動

利用者向けの通常起動:

```powershell
wscript.exe .\launch.vbs
```

console を見ながら起動:

```powershell
.\launch.bat
```

compile だけを確認:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File .\app-studio.ps1 -CompileOnly
```

headless 環境診断:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File .\app-studio.ps1 -Headless -DiagnosticsPath .\runtime\headless-diagnostics.json
```

## 実装規約

- `src/*.cs` は C# 5.0 構文かつ ASCII だけを使う。文字列補間、`nameof`、pattern matching、tuple、null 条件演算子などを使わない。
- 日本語 UI 文言は `assets/messages/*.txt` に置く。記号（`●` など）も文言である。
- `launch.vbs` は ASCII を保つ。
- PowerShell は 5.1 互換にし、三項演算子や PowerShell 7 専用構文を使わない。
- UI thread は対象へ直接問い合わせない。UIA 取得・UIA 操作は worker へ値渡しする。
- **WPF のオブジェクトは、それを作った thread からしか触らない。** 録画は専用 thread で走るので、枠と録画コントロールへの操作は必ず dispatcher 経由にする。撮影前の「消す」は待ち合わせる（`Invoke`）、時計や枠の追従は待たない（`BeginInvoke`）。
- WinEvent callback は queue へ積むだけにし、UI timer が drain する。
- 値は `Privacy` を通してしか step に入らない。他の場所で「これは秘密か」を判断しない。
- timeout、partial、fallback、worker restart、request drop を握り潰さない。
- 既存の期待値を緩める、assertion を削る、skip を追加することで試験を緑にしない。

## Source map

| file | 責務 |
|---|---|
| `00_Theme` | 共通 design system 準拠の color token・spacing・型スケールと ControlTemplate。light/dark の切替と `runtime/settings/theme.txt` への保存 |
| `01_App` | 起動、DPI、fatal error |
| `02_Studio` | 小さなランチャと結果画面の2形態、カウントダウン、秘密入力と再生許可の問い合わせ |
| `04_Hotkeys` | RegisterHotKey（stop / emergency）、代替、設定保存 |
| `05_Native` | Win32 宣言、DPI/monitor/input/window helper、重なり順の列挙、検証つき前面化 |
| `06_Win32Probe` | bounded Win32 取得 |
| `07_UiaProbe` | UIA snapshot と pattern 操作 |
| `08_Snapshot` | layer contract と worker 入口 |
| `09_InputWatch` | ポインタの低レベル hook（押下/解放/double click/drag/wheel）、入力イベントの語彙、打鍵検出キー表 |
| `10_Locator` | ScanNode からのロケータ生成と確度 |
| `11_Resolver` | 取り直した一覧に対する解決。何が identification で何が description かを決める |
| `12_Probe` | 操作の実行、guard、経路の繰り上がり、試行列、undo |
| `13_Capture` | BitBlt/PrintWindow、mask、hash |
| `14_Privacy` | 何を書き残してよいかの唯一の規則 |
| `15_Store` | session model と JSONL 保存・読み戻し |
| `16_Report` | 自己完結 HTML |
| `17_Diagnostics` | environment 採取 |
| `18_Json` | 順序安定 JSON writer |
| `19_Worker` | active+warm spare process、JSONL、watchdog、scan streaming |
| `20_SessionLog` | 書き手と同居したまま記録を読み戻す |
| `21_Scan` | 取得の model と Win32 child 列挙、provider 統合 |
| `22_ScanProviders` | UIA/MSAA tree の walk と座標 sampling（worker 側）|
| `23_ScanRunner` | 専用 worker、進捗、打切り理由、人向け要約。録画用に worker を使い回す持続 mode を持つ |
| `24_Messages` | `assets/messages` の日本語文言読み出し |
| `25_Recorder` | アプリ横断の記録。入力監視スレッドと記述スレッドの分離、生タイムライン、前面/フォーカス追跡、入力欄の値読み戻し |
| `26_JsonReader` | JSON 読み取り |
| `27_Picker` | 全画面の選択オーバーレイ |
| `28_RecordHud` | 録画中の枠と停止コントロール。撮影前に画面から消えたことを自分で確かめる |
| `29_Replay` | 再生。ウィンドウ確認、フォーカス復帰、記録間隔の再現、解決、実行、試行列 |
| `30_SessionMd` | AI へ渡す `session.md` |
| `31_Screens` | 画面台帳と撮影 |
| `32_Pdf` | 依存なしの PDF writer（無劣化 Flate と写真 DCT） |
| `33_ScreensPdf` | AI へ渡す `screens.pdf` と容量予算 |
| `34_Acquire` | 取得と撮影の共通処理、秘密欄の黒塗り、出力の書き出し口 |
| `36_ScriptModel` | 録画 step を両言語共通の操作列へ落とす唯一の場所。PowerShell と VBA が同じ録画について食い違わない根拠 |
| `37_PowerShellGen` | PowerShell の初期コード生成。UIA でウィンドウと要素を解決し、位置は要素矩形に対する割合で扱う |
| `38_VbaGen` | VBA の初期コード生成。Win32 だけで届く範囲を書き、届かない step は理由付きで停止させる |
| `39_CodeProject` | 生成版・現行版・取り込み直前版の3版保持と `runtime/sessions/<id>/code/` への保存・読み戻し |
| `40_Handoff` | AI へ渡すテキスト1個の組み立てと、長い場合の番号付き分割 |
| `41_Intake` | 返答の解析。request id 照合、BEGIN/END/COMPLETE/PART、装飾除去、拒否理由 |
| `42_Diff` | 行単位の差分。反映前に何が変わるのかを見せるためだけに使う |
| `43_CodeScreen` | コード編集画面。形式切替、検証、実行、AI への送りと受け、差分と復帰 |
| `44_ScriptRun` | PowerShell の構文検証と実行、VBA の構造検証と VBA ホストでの実行 |
| `35_Verdict` | このセッションが何だったのかの唯一の判断。状態・件数・警告・再生可否・次の一手を1箇所で決め、画面/HTML/session.md が同じ言葉で言う |
| `35_Verdict` | このセッションが何だったのかの唯一の判断。状態・件数・警告・再生可否・次の一手を1箇所で決め、画面/HTML/session.md が同じ言葉で言う |
| `35_Verdict` | このセッションが何だったのかの唯一の判断。状態・件数・警告・再生可否・次の一手を1箇所で決め、画面/HTML/session.md が同じ言葉で言う |

## 試験

全試験:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File .\tests\run-all.ps1
```

runner は相互の WPF/COM/static 状態を持ち越さないよう、各 test を新品の PS5.1 STA process で順次実行する。

主要 runner:

- `test-compile`: 全 source、ASCII/C#5、起動終了
- `test-docs`: 文書に載っている path と語彙が実在すること
- `test-locator`: ロケータ生成の規則、禁止材料の不在、解決の一意／曖昧／不在、**位置と兄弟順が住所として使われないこと**
- `test-privacy`: **キーボード捕捉 API がソースに存在しないこと**、hook は `WH_MOUSE_LL` 1種だけであること、打鍵検出キーが固定監視リストの部分集合であること、監視キー表、秘密判定の3規則、値方針3種、秘密が出力へ出ないこと
- `test-session-store`: 逐次追記の耐久性、実行中の読み戻し、round trip、一覧、壊れたフォルダの報告
- `test-outputs`: 3出力の生成、**AI 添付がちょうど2ファイル**、PDF の xref 健全性、`session.md` の9節と base64 不在、HTML の外部参照0とエスケープ、容量予算（悪化する縮小段を採らないこと、効く場合は採ること、省略の明記）
- `test-replay`: 経路 mode の意味、拒否の試行列、存在しないウィンドウ、許可なしの拒否、住所を持たない要素の拒否
- `test-codegen`: 操作列の生成、PowerShell と VBA が**同じ9操作を持つこと**、住所を持たない step が黙って落ちず停止すること、UIA だけの要素を VBA が理由付きで断ること、位置が住所として書かれないこと、秘密が値ではなく問い合わせになること、生成 PowerShell が実際に parse できること、3版（生成・現行・直前）の保持と復帰
- `test-handoff`: テキスト1個が8節すべてを持つこと、request id と返却形式が入ること、通常のセッションが**1回のコピーで収まること**、長すぎる場合だけ番号付きに分割され再結合で欠落しないこと、依頼テキストが `ai/` を2ファイルのまま保つこと
- `test-intake`: 正常系（1ファイル／複数ファイル／拒否）と20種の不正形、チャット装飾を剥がしても**本文は逐語**であること、PART の欠落・順不同・重複・総数不一致・内容衝突、再結合の順序、差分の増減行数
- `test-code-ui`: 実 GUI を UI Automation で駆動し、結果画面からコード編集画面へ入ること、開いた瞬間に実行可能なコードが入っていること、PowerShell と VBA が同格に並ぶこと、**1回のコピー**で依頼文が出ること、**別の依頼への返答が理由付きで拒否され画面のコードが変わらないこと**、返答が差分として出てから反映されること、反映・取り消し・生成版への復帰、［← 戻る］が結果画面へ返ること
- `test-code-run-e2e`: 生成した PowerShell を実際に走らせ、**fixture の状態が実際に変わること**を合格条件にする。スクリプトが最後まで走ったことではなく、対象アプリが変わったことを見る
- `test-vba-host`: 生成した VBA を実際の VBA ホストへ取り込んで走らせる。ホストが無い／VBA プロジェクトへのアクセスが信頼されていない／走って停止した、の**どれであっても名指しで報告されること**を確認し、どれも「合格」に丸めない。あわせて全 `Declare` が `Alias` を持つこと、呼出しに上限があること、**ホストを残さないこと**を見る
- `test-diagnostics` / `test-acq-diagnostics`: 診断 code が全投影へ届くこと、画像0のとき PDF 不在の理由が出ること
- `test-hang-recovery`: 恒久 hang 20回、直後正常取得、resource/orphan/queue/UI
- `test-live-basic`: Win32/WPF、deep tree、画像と黒塗り表明
- `test-live-move`: 物理座標、別 monitor、録画枠が取得矩形に一致すること、**撮影前に画面から消え、あとで戻ること**
- `test-live-restart`: 再起動後も住所が生き残ること、消えた要素が `not-found` になること
- `test-capture-policy`: password 矩形が実際に黒いこと、全景撮影の明示要求、値漏れ0
- `test-live-canvas`: child HWND なし、UIA-EMPTYTREE、**構造を公開しないことが両出力に明記されること**
- `test-scan`: 4経路の統合、HWND 無し要素、独自描画時の sampling 起動、値漏れ0
- `test-autosave`: **製品が実際に書く `SessionStore.Append` 経路で**、強制終了しても記録と索引が残ること、書けない時に `STORE-WRITE` の理由を出して別の場所へ勝手に出さないこと
- `test-ui-flow`: 実 GUI を UI Automation で駆動し、ランチャが小さく結果領域を持たないこと、設定がダイアログであること、再生許可が既定で切であること、MSAA が経路として出ないこと、結果画面が結論→詳細の順で、**折り畳みが入れ子になっていないこと**
- `test-report-states`: 成功／一部失敗／多数失敗／空／長文の5状態で `report.html` を生成し、結論が最初に出ること、`<details>` が入れ子でないこと、各節が要約1行＋詳細1段であること、長文が桁溢れしないことを確認する
- `test-input-timeline`: **実マウス・実キー**で、click／double click／drag／wheel／command key の down と up／録画開始時点で既にフォーカスがある欄への打鍵／Tab 移動が、生タイムラインと step の両方に落ちることを確認する
- `test-calculator-e2e`: **実マウス**で Windows 標準の電卓を相手に、ランチャの大きさ、スナップ後にフォーカスが戻ること、複数系列の押下が1つも欠けずに記録されること、再生が許可を尋ねること、そして**電卓の表示が実際に期待値へ変わること**を確認する。再生が呼ばれたことではなく、対象アプリが変わったことを合格条件にする
- `test-gesture-e2e`: **実マウス**で、double click／drag／wheel を「何が届いたかを言う fixture」相手に録画し、再生する。合格条件は fixture 側が同じ操作の到着を再度記録すること、および drag が実際に距離を持つこと
- `test-ime-e2e`: **実キー**で Windows 標準 IME 経由の日本語入力を記録→再生する。ひらがな、漢字変換の確定、日本語の句読点のみ、録画開始前からフォーカス済みの欄、同一ウィンドウ内の Tab 移動を1続きで行い、**対象欄の最終値**で合否を判定する。文字は KEYEVENTF_UNICODE で注入せず、必ず IME を通す
- `test-notepad-e2e`: **実マウス・実キー**で Windows 標準のメモ帳を相手に、開始時点で既にフォーカスのある本文への入力、Tab／shortcut、click、間隔を含む録画を行い、再生後に**メモ帳の本文が期待どおりに変わること**を合格条件にする
- `test-notepad-e2e`: **実マウス・実キー**で Windows 標準のメモ帳を相手に、開始時点で既にフォーカスのある本文への入力、Tab／shortcut、click、間隔を含む録画を行い、再生後に**メモ帳の本文が期待どおりに変わること**を合格条件にする
- `test-notepad-e2e`: **実マウス・実キー**で Windows 標準のメモ帳を相手に、開始時点で既にフォーカスのある本文への入力、Tab／shortcut、click、間隔を含む録画を行い、再生後に**メモ帳の本文が期待どおりに変わること**を合格条件にする
- `test-gui-e2e`: **実マウス・実キー**で2つの実アプリを相手に、選択→取得→出力、アプリ横断の録画、秘密欄の非保存、キーフレームの黒塗り、自分のウィンドウが証拠へ混入しないこと、再生と試行列、秘密ステップでの操作者への問い合わせまで通す

fixture だけを build:

```powershell
.\tests\build-fixtures.ps1
```

`FixtureWinForms`、`FixtureWin32`、`FixtureWpf`、`FixtureCanvas`、`FixtureInputTarget`、`FixtureIme` を `tests/.build` に生成する。

`test-live-probe`、`test-input-probe`、`test-packaged-target`、`test-input-timeline`、`test-gesture-e2e`、`test-ime-e2e`、`test-gui-e2e`、`test-notepad-e2e`、`test-calculator-e2e`、`test-code-run-e2e` は**実マウス・実キーを出すため `run-all.ps1` の既定では実行されない**。`test-code-run-e2e` が駆動するのは UIA パターンを持つ fixture なので実際には合成入力へ落ちないが、`InvokeElement` はパターンが無ければ合成入力へ落ちる設計であり、**落ちうる以上こちら側に置く**。`test-vba-host` は VBA ホスト（Excel）を起動するため同じ扱いにする。実行するのは `APPSTUDIO_ALLOW_REAL_INPUT=1` を立てたときだけで、立てない場合は `SKIP` 行を出して黙って飛ばさない。**誰かが使っている端末では立てないこと。**

`test-gui-e2e` は自分で起動した fixture だけを操作する。全 click は押す直前に `WindowTools.ProcessIdAt` で対象プロセスのものだと確認し、違えば一度だけ対象を前面に出して確認し直し、それでも違えば例外にして何も押さない。再生の前には、記録した各ウィンドウ記述に一致する窓がちょうど1つであることを確かめ、複数あれば再生を行わずその事実を出力する。

`test-packaged-target` は自分で起動した電卓だけを操作し、自分で閉じる。起動前から画面にあった電卓の window は触らない。

`test-ui-flow` は実 GUI を起動し、**物理カーソルを動かさず UI Automation の pattern でボタンを押す**。App Studio 窓の画像が要るときだけ `APPSTUDIO_UI_SHOTS=1` を立てる（画像には利用者の窓一覧が写るため既定では撮らない）。

## 出力を目で確かめる

自動試験は `report.html` の外部参照0や `screens.pdf` の xref 健全性までは固定するが、**読めるかどうかは実際に開かないと分からない**。受け入れの前に、生成された `runtime/sessions/<id>/out/` の3ファイルを実ブラウザと実 PDF 表示で開き、表・画像・ページ番号・黒塗りを目で確認すること。

## schema 変更

`meta.json` の `schema` は `app-studio/session/2`。既存 key の意味を変える破壊的変更では版を上げる。key の追加だけでも reader が未知 key/type を捨てずに素通しできることを維持する。`steps.jsonl` は同じ stepId が複数行現れる前提であり、読み手は最後の行を採る。

## WP-S 再測定

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File .\tests\wp-s\run-wp-s.ps1
```

WP-S 成果は `artifacts/wp-s/<run>/` に出る。WP-S 文書の数値と製品回帰の数値を混同しない。

## UI 規約

画面の見た目と操作導線は、共通の design system に揃える。

- 色・余白・角丸・型スケールは `src/00_Theme.cs` の token だけを使う。`Color.FromRgb` や `Brushes.White` を UI コードへ直接書かない。
- 画面は3形態。**ランチャ 660 x 356**（メニューと設定だけ、結果領域を持たない）、**結果画面 1120 x 840**（左レール＋詳細）、**コード編集画面 1120 x 840**（形式切替＋エディタ＋AI 相談）。いずれも topbar / progress track / 本体 / status bar の4帯。
- コード編集画面は結果画面の［コード編集］からだけ入り、［← 戻る］は結果画面へ返す。ランチャへ一足飛びに戻さない。
- **PowerShell と VBA は同じ大きさ・同じ位置・同じボタンで扱う。** 片方を既定にしたり、片方だけ「書き出し」の扱いにしたりしない。
- 結果画面は上から **結論 → 数値 → 警告 → 次の一手 → 詳細** の順。結論は状態チップ＋1文で、主語と結果が曖昧にならないこと。
- **折り畳みは1段だけ。** 折り畳みを開いた先に折り畳みを置かない。開いた先は平らな一覧を持つ1つのスクロール箱にする。
- 生ログ・全要素・環境・取得方法は詳細側に置く。最初の画面には出さない。
- 状態語（成功／一部失敗／失敗／記録なし）と件数の言い回しは `35_Verdict` が唯一の出所。画面と HTML と `session.md` で同じ語を使う。
- 結果画面は上から **結論 → 数値 → 警告 → 次の一手 → 詳細** の順。結論は状態チップ＋1文で、主語と結果が曖昧にならないこと。
- **折り畳みは1段だけ。** 折り畳みを開いた先に折り畳みを置かない。開いた先は平らな一覧を持つ1つのスクロール箱にする。
- 生ログ・全要素・環境・取得方法は詳細側に置く。最初の画面には出さない。
- 状態語（成功／一部失敗／失敗／記録なし）と件数の言い回しは `35_Verdict` が唯一の出所。画面と HTML と `session.md` で同じ語を使う。
- 結果画面は上から **結論 → 数値 → 警告 → 次の一手 → 詳細** の順。結論は状態チップ＋1文で、主語と結果が曖昧にならないこと。
- **折り畳みは1段だけ。** 折り畳みを開いた先に折り畳みを置かない。開いた先は平らな一覧を持つ1つのスクロール箱にする。
- 生ログ・全要素・環境・取得方法は詳細側に置く。最初の画面には出さない。
- 状態語（成功／一部失敗／失敗／記録なし）と件数の言い回しは `35_Verdict` が唯一の出所。画面と HTML と `session.md` で同じ語を使う。
- 結果領域を起動時から抱えない。読むものが無いうちに大きな窓を出さない。
- **共有しているコントロール（status / progress / 一覧 / 詳細 / 主ボタン）は、形態を切り替える前に必ず親から外す。** WPF は同じ要素に2つ目の親を許さず、これを怠ると再構築が例外になる。
- 録画中の停止操作は大きく取る。見失って押せないことがあってはならない。
- **主役は「スナップ」「録画」の2ボタンと、その下のセッション一覧だけ。** 再生・レポート・AI 向け出力・書き出しはセッションを選んだときの従属操作。経路選択・値方針・容量予算・診断は［詳細設定］の折り畳みの中。
- 補足・詳細・上級者向け追跡情報は Accordion へ入れる。**閉じた状態で中身の量や状態が分かる要約を必ず付ける。**
- 許可の可否は必ず tick box と警告枠、状態の一言は Badge、件数は StatCard。
- 成功・失敗・拒否は色で区別する（`Success` / `Danger` / `Caution`）。灰色一色の段落にしない。

worker protocol の debug が要るときは `APPSTUDIO_WORKER_TRACE=<path>` を立てる。

## 配布 folder の確認

持込み対象は `launch.vbs / launch.bat / app-studio*.ps1 / src / assets / docs`。`tests/.build`、`runtime`、`artifacts` は除外する。未署名 EXE や SDK interop DLL を加えない。
