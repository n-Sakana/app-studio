# App Studio 開発手順

## 前提

- Windows PowerShell 5.1
- .NET Framework 4.x と `csc.exe`
- WPF、Windows Forms、UIAutomationClient/UIAutomationTypes
- 開発試験用fixtureを表示できるinteractive desktop

外部package restoreやnetwork accessは不要である。fixtureのEXEは `tests/.build` に開発時だけ生成し、持込み配布物には含めない。

## 起動

利用者向けの通常起動:

```powershell
wscript.exe .\launch.vbs
```

consoleを見ながら起動:

```powershell
.\launch.bat
```

compileだけを確認:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File .\app-studio.ps1 -CompileOnly
```

headless環境診断:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File .\app-studio.ps1 -Headless -DiagnosticsPath .\runtime\headless-diagnostics.json
```

## 実装規約

- `src/*.cs` はC# 5.0構文かつASCIIだけを使う。文字列補間、`nameof`、pattern matching、tuple、null条件演算子などを使わない。
- 日本語UI文言は `assets/messages/*.txt` に置く。
- `launch.vbs` はASCIIを保つ。
- PowerShellは5.1互換にし、三項演算子やPowerShell 7専用構文を使わない。
- UI threadは対象へ直接問い合わせない。UIA取得・UIA操作は構成C workerへ値渡しする。
- WinEvent callbackはqueueへ積むだけにし、UI timerがdrainする。
- `LiveValue` をSession、locator、event、diagnostics、reportへ渡さない。永続経路は`RecordedValue`だけを使う。
- timeout、partial、fallback、worker restart、request dropを握り潰さない。
- 既存の期待値を緩める、assertionを削る、skipを追加することで試験を緑にしない。

## Source map

| file | 責務 |
|---|---|
| `00_Theme` | 共通design system準拠のcolor token・spacing・型スケールと、Button/TextBox/List/Expander/CheckBox/ComboBox/ScrollBarのControlTemplate。light/darkの切替と`runtime/settings/theme.txt`への保存 |
| `01_App` | 起動、DPI、fatal error |
| `02_Shell` | 段階UI（対象→目的→実行）、hover表示、記録、操作、出力 |
| `03_Overlay` | passive overlay |
| `04_Hotkeys` | RegisterHotKey、代替、設定保存 |
| `05_Native` | Win32宣言、DPI/monitor/input/window helper |
| `06_Win32Probe` | bounded Win32取得 |
| `07_UiaProbe` | UIA snapshotとpattern操作 |
| `08_Snapshot` | layer contractとworker入口 |
| `09_WinEvents` | OUTOFCONTEXT event queue |
| `10_Locator` | 純logicのlocator生成・初期確度 |
| `11_Resolver` | 実解決とverification |
| `12_Probe` | 操作試験、guard、fallback、undo |
| `13_Capture` | BitBlt/PrintWindow、mask、hash |
| `14_Session` | session、RecordedValue、live isolation |
| `15_PackWriter` | schema、pack、manifest、diagnostics.log |
| `16_Report` | 単一self-contained HTML generator |
| `17_Diagnostics` | environment採取 |
| `18_Json` | 順序安定JSON writer |
| `19_Worker` | active+warm spare process、JSONL、watchdog、scan streaming |
| `20_SessionLog` | 明示保存なしの逐次追記log（JSONL＋要約）|
| `21_Scan` | 自動調査のmodelとWin32 child列挙、provider統合 |
| `22_ScanProviders` | UIA/MSAA treeのwalkと座標sampling（worker側）|
| `23_ScanRunner` | 専用worker、進捗、打切り理由、人向け要約 |
| `24_Messages` | `assets/messages` の日本語文言読み出し |
| `25_Observation` | 手動観察の集約記録とmouse button監視 |

## 試験

全試験:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File .\tests\run-all.ps1
```

runnerは相互のWPF/COM/static状態を持ち越さないよう、各testを新品のPS5.1 STA processで順次実行する。所要時間はrunner数と実行環境に比例するため、実測値は各自の環境で取ること。

主要runner:

- `test-compile`: 全source、ASCII/C#5、起動終了
- `test-hang-recovery`: 恒久hang 20回、直後正常取得、resource/orphan/queue/UI
- `test-live-basic`: Win32/WPF、pin、tree、crop
- `test-live-move`: 物理座標、別monitor、overlay
- `test-live-events`: menu/focus/window eventと再入
- `test-live-restart`: 全locatorのrestart verification
- `test-live-probe`: 10 kind、5 outcome、method、guard、undo（前後でカーソル位置を保存・復元する）
- `test-input-probe`: 実clickと実キーが**専用の受信アプリへ実際に届いたか**、覆われた点が `policy.covered` でblockedされ誤送信が0であること、カーソルが元位置へ戻ること
- `test-live-canvas`: child HWNDなし、UIA-EMPTYTREE、座標fallback、一連動線
- `test-live-value-isolation`: session/report/log/event/locator/clipboard横断
- `test-scan`: UIA/MSAA/Win32/座標samplingの統合、HWND無し要素、独自描画時のsampling起動、要素logの値漏れ0
- `test-observe`: 要素遷移・滞在・click前後差分・対象外の非記録・一時停止、およびsourceにkey入力捕捉APIが存在しないことの固定
- `test-autosave`: 強制終了しても記録が残ること、書けない時に理由を出すこと、実行中でもlogが読めること
- `test-ui-flow`: 実GUIをUI Automationで駆動し、対象選択→自動調査→結果→操作試験(読取)→手動観察まで通す
- `test-gui-e2e`: **実マウス・実キー**でメモ帳と電卓を対象に、対象選択→自動調査→手動観察の自動記録→AI依頼文生成→回答取込→実行→履歴→テーマ切替まで通す。回答は正答・散文・存在しない部品・途中で切れたJSONの4種を投入し、実行可否の判定を固定する
- `test-handoff`: Screen台帳とComponent IDが添付2点の両方で同じものを指すこと、PDFのxrefが実オブジェクトを指すこと、格納画像をinflateして元PNGと画素一致（無劣化）すること、画面や部品が動くと `premiseHash` が変わること
- `test-packaged-target`: **実アプリ**（電卓）で、frame windowを持つpackaged applicationが自分自身に覆われたと判定されないこと、window全体への `focus` が経路を使い切って失敗しないこと。frameとcontentが分かれていない環境では通ったふりをせず SKIP する
- `test-ai-calculator-e2e`: **実アプリ**。AI経路の縦切りを2段で回す driver（`-Phase request` / `-Phase answer`）。回答は外部で書かれるため `run-all.ps1` には入れず、手で回す
- `test-schema / test-manifest / test-report / test-mask / test-diagnostics`: 純logicと出力

fixtureだけをbuild:

```powershell
.\tests\build-fixtures.ps1
```

`FixtureWinForms`、`FixtureWin32`、`FixtureWpf`、`FixtureCanvas`を `tests/.build` に生成する。`FixtureWpf`は正常・一時停止・恒久停止を兼ねる。

`test-live-probe`、`test-input-probe`、`test-packaged-target`、`test-case-real-input`、`test-gui-e2e` は**実マウス・実キーを出すため `run-all.ps1` の既定では実行されない**。実行するのは `APPSTUDIO_ALLOW_REAL_INPUT=1` を立てたときだけで、立てない場合は `SKIP` 行を出して黙って飛ばさない。**誰かが使っている端末では立てないこと。**

`test-packaged-target` と `test-ai-calculator-e2e` は自分で起動した電卓だけを操作し、自分で閉じる。起動前から画面にあった電卓のwindowは触らない。`test-ai-calculator-e2e` は回答を書く相手が外部にいるため2段に分かれており、`-Phase request` が App Studio と電卓を起動したまま状態fileを残し、`-Phase answer` がその同じprocessへ接続して回答を投入する。

```powershell
$env:APPSTUDIO_ALLOW_REAL_INPUT = '1'
.\tests\test-ai-calculator-e2e.ps1 -Phase request -Goal '7 に 8 を足した答えを電卓の画面に出す' -Evidence .\artifacts\ai-e2e\run1
# <case>/handoff/ の2fileをAIへ渡し、返ってきた本文をそのままfileへ保存する
.\tests\test-ai-calculator-e2e.ps1 -Phase answer -Evidence .\artifacts\ai-e2e\run1 -AnswerFile <返答file>
```

`test-input-probe` は `FixtureInputTarget`（クリックとキーの到達だけを記録し、押された文字そのものは保存しない窓）を**現在のカーソル位置の真下へ移してから**実入力を出し、最後にカーソルを元へ戻す。`test-live-probe` も同じく復元する。

`test-ui-flow` は実GUIを起動し、**物理カーソルを動かさずUI Automationのpatternでボタンを押す**。App Studio窓の画像が要るときだけ `APPSTUDIO_UI_SHOTS=1` を立てる（画像には利用者の窓一覧が写るため既定では撮らない）。`test-live-probe` は fallbackで `SetCursorPos`＋`SendInput` を出す唯一のtestであり、他人がdesktopを使っている間は流さない。

`test-gui-e2e` は自分で起動したメモ帳と電卓だけを操作する。全clickは押す直前に `WindowTools.ProcessIdAt` で対象processのものだと確認し、違えば例外にして何も押さない。メモ帳は保存せずに閉じる。

## UI規約

画面の見た目と操作導線は、共通のdesign systemに揃える。

- 色・余白・角丸・型スケールは `src/00_Theme.cs` のtokenだけを使う。`Color.FromRgb` や `Brushes.White` をUIコードへ直接書かない。
- 画面は上から topbar(46) / progress track(4) / screen header / status badge / workspace / action bar(62) の6帯。この並びを画面ごとに変えない。
- 運転を前へ進める操作は action bar に置く。カード内には「そのカードの中身に対する補助操作」だけを compact button で置く。
- 補足・詳細・上級者向け追跡情報は `Accordion(...)` へ入れる。**閉じた状態で中身の量や状態が分かる要約を必ず付ける**。要約が空になる実装にしない。
- 同じ意味には同じ部品を使う。許可の可否は必ず `PermissionSwitch` + `PermissionBox`、状態の一言は `Badge`、結果の一段落は `Callout`、件数は `StatCard`。
- 成功・失敗・拒否は色で区別する（`Success` / `Danger` / `Caution`）。灰色一色の段落にしない。
- 一文が入る行は折り返す。ListBoxへ文字列を入れるなら `AppWrapRow` を `ItemTemplate` に付ける。

worker protocolのdebugが要るときは `APPSTUDIO_WORKER_TRACE=<path>` を立てる。workerが受け取った要求行の長さと先頭だけをその file へ追記する。

## schema変更

`session.json` の既存keyの意味を変える破壊的変更では `schemaVersion` を上げる。keyの追加だけでもreaderが未知key/typeを捨てずに素通しできることを維持する。key順、ID、時刻、RecordedValue、events.sourceは `test-schema.ps1` で固定する。

## WP-S再測定

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File .\tests\wp-s\run-wp-s.ps1
```

WP-S成果5点は `artifacts/wp-s/<run>/` に出る。現在の採用は構成C。hover 1,500ms、pin 3,000ms、操作5,000msが推奨値である。WP-S文書の数値と製品回帰の数値を混同しない。

## 配布folderの確認

持込み対象は `launch.vbs / launch.bat / app-studio*.ps1 / src / assets / docs`。`tests/.build`、`runtime`、開発時の調査packは除外する。未署名EXEやSDK interop DLLを加えない。
