# App Studio

Windows 上で動いているアプリケーションの UI 部品を調べるための、**テキストファイルだけで動く**調査ツールです。

実行形式 (EXE/DLL) を一切持ち込まず、インストールもレジストリ変更も行いません。`src/*.cs` を Windows PowerShell 5.1 の `Add-Type` でメモリ内コンパイルして起動します。インストール権限が無い端末や、持ち込み物が制限される現場を想定しています。

ライセンスは **CC0 1.0 Universal**（パブリックドメイン提供）です。詳細は [LICENSE](LICENSE) を参照してください。

---

## できること

- **自動調査** — 対象アプリを選ぶと、操作を一切起こさずに現在表示中の要素を集めます。UI Automation (raw view walk)、MSAA (`AccessibleChildren` walk)、Win32 子ウィンドウ列挙、座標グリッドの hit-test という4経路を併用し、重複を統合して1件1行で記録します。HWND を持たない要素も記録し、経路ごとの打ち切り理由も残します。
- **手動観察** — 対象アプリ上で普通にホバー・クリックすると、要素の遷移・滞在・クリック前後の差分が調査ログへ自動追記されます。キーボード入力は読み取りません。
- **操作試験** — 特定した部品に対して読み取りや操作を試し、結果を5値 (`success` / `unknown` / `failed` / `blocked` / `skipped`) で記録します。既定は読み取り専用です。
- **AI への依頼（案件）** — 調査結果から依頼文と添付を生成し、外部 AI が書いた手順 (JSON) を取り込んで検証・実行し、記録します。壊れた回答は推測で補正せず、理由を出して拒否します。
- **自動保存** — 明示的な保存操作は不要です。`runtime/live-session/<sessionId>/` へ逐次追記するため、強制終了しても書けた分は残ります。
- **調査 pack の出力** — 自己完結の HTML レポート、機械可読な `session.json`、`MANIFEST.json`（バイト数と SHA-256）を出力します。

## 安全側の既定

- 対象アプリは既定で**読み取り専用**です。書き込みを伴う操作はトグルを入れるまで実行できません。
- **パスワード欄の値は取得しません。** UI Automation の `IsPassword` を検出した要素は値を捨てます。
- 他のウィンドウに覆われている座標への操作は拒否します。
- 操作を出す直前に、その座標が対象プロセスのものか確認します。違えば何も送りません。
- パターンを公開している要素でも、前後の変化を観測できなければ `unknown` として記録し、`success` へ丸めません。

## 動作要件

- Windows（UI Automation / MSAA / Win32 が利用できること）
- Windows PowerShell **5.1**（`launch.vbs` は明示的に 5.1 を起動します）
- .NET Framework（`Add-Type` による C# 5.0 相当のコンパイルが可能なこと）

## 起動

```
launch.vbs                                      # 非表示の Windows PowerShell 5.1 で起動する
powershell -File app-studio.ps1 -CompileOnly    # コンパイルだけ確認する
powershell -File app-studio.ps1 -Headless       # 診断だけ書き出す
```

UI Automation の取得は `app-studio-worker.ps1` の子プロセスへ隔離されます。対象アプリが応答しなくなっても本体は生き残ります。

調査中の記録は `runtime/live-session/<sessionId>/` へ、案件の記録は `runtime/cases/<caseId>/` へ書かれます。`runtime/` は端末固有の状態（診断、ホットキー設定、画面キャプチャを含む）なのでバージョン管理から除外しています。

## テスト

```
powershell -File tests/build-fixtures.ps1      # fixture アプリを tests/.build/ へ作る
powershell -File tests/run-all.ps1             # 全テストを走らせる
powershell -File tests/wp-s/build-wp-s.ps1     # WP-S ハーネスを作る
powershell -File tests/wp-s/run-wp-s.ps1       # WP-S 計測を artifacts/ へ出す
```

`run-all.ps1` は**実マウス・実キーボードを出すテストを既定では実行しません**。それらは `APPSTUDIO_ALLOW_REAL_INPUT=1` を立てたときだけ走り、立てない場合は黙って飛ばさず `SKIP` 行を出します。**誰かが使っている端末では立てないでください。**

fixture が緑でも受け入れの根拠にはなりません。実際のアプリの実画面で確認してください。

## 文書

| file | 内容 |
|---|---|
| [`docs/SPEC.md`](docs/SPEC.md) | 仕様。画面動線、取得層、locator、操作試験、値と mask、調査 pack、既知の限界 |
| [`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md) | 開発手順、source map、テスト構成、UI 規約 |
| [`docs/ONSITE.md`](docs/ONSITE.md) | 現地実施票。持ち込み前の確認と、最初の10分で記録する事実 |

## 既知の限界

- 対象が内部のアクセシビリティ階層を公開しない場合、取得できる粒度はウィンドウ単位まで落ちます。その場合も無反応にはせず、「内部部品は取得できない」「窓単位で記録中」と画面に出します。
- 画像・UIA・`PrintWindow`・`SendInput` の品質は、対象の実装・権限・デスクトップの状態に依存します。
- OCR と低レベル入力フックは対象外です。

その他の制約は `docs/SPEC.md` の「既知の限界」にまとめています。

## リポジトリの注意

`.gitattributes` は `* -text` を指定しています。`core.autocrlf=true` の環境でチェックアウト時に全ファイルの改行が書き換わるのを防ぐためです。**この指定を外さないでください。**
