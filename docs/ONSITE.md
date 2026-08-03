# App Studio 現地実施票

この文書は対象端末で事実を記録するためのchecklistである。未確認項目を禁止条件へ読み替えない。警告や遮断が出た場合は、発生操作と表示を記録し、対象機能だけを止めて相談する。

## 持込み前

- [ ] 配布folderに未署名EXE、fixture EXE、開発時 `.build`、資格情報がない。
- [ ] GitHub ZIP由来の場合、開発端末側でMark of the Webを解除した。
- [ ] 使用API、書込先、通信なしを説明できる。
- [ ] 正本hashと配布folderのhash/内容一覧を記録した。
- [ ] 出力先と持ち帰り手続を確認した。

## 最初の10分

| # | 確認 | 実測値・表示・時刻 |
|---|---|---|
| 1 | `launch.vbs` から起動できるか。失敗時の全文 | |
| 2 | PowerShell版 | |
| 3 | ExecutionPolicy | |
| 4 | LanguageMode | |
| 5 | `Add-Type` 可否 | |
| 6 | 一時file作成可否 | |
| 7 | .NET Framework版 | |
| 8 | `UIAutomationCore.dll` 版 | |
| 9 | WebView2 Runtime有無・版（将来判断用） | |
| 10 | OS: Windows 10 / 11、build | |
| 11 | user権限、tool/対象の昇格差 | |
| 12 | monitor数、各解像度・DPI・scale・配置 | |
| 13 | AppLocker/WDAC/CLMの観測事実 | |
| 14 | startup diagnosticsの保存path | |

## 対象選択と代表10部品

対象アプリ名:  
process/class/FrameworkId/bitness:  
対象run ID:  

| # | 部品 | Win32 | UIA | AutomationId | Name安定性 | locator最高確度 | 所要ms | 備考 |
|---|---|---|---|---|---|---|---|---|
| 1 | | | | | | | | |
| 2 | | | | | | | | |
| 3 | | | | | | | | |
| 4 | | | | | | | | |
| 5 | | | | | | | | |
| 6 | | | | | | | | |
| 7 | | | | | | | | |
| 8 | | | | | | | | |
| 9 | | | | | | | | |
| 10 | | | | | | | | |

## 実運用確認

- [ ] 対象一覧から選択できた。
- [ ] 照準dragから選択できた。
- [ ] hover確定から選択できた。
- [ ] 対象を前面のままoverlay枠が一致した。
- [ ] 100%/150%または現地DPIで枠位置を照合した。
- [ ] 別monitorへ移動後も枠位置を照合した。
- [ ] RDP利用時の位置・重さを記録した。
- [ ] menu、popup、modalをfreezeして固定できた。
- [ ] 切抜きが黒くない。黒い場合はcaptureMethodと `CAP-BLACK` を記録した。
- [ ] password矩形が黒塗りされた。
- [ ] 全景の写り込み警告を確認し、必要時だけ撮影した。

## 再起動・操作試験

- [ ] 対象を再起動し、全locator候補を再検証した。
- [ ] 再起動前後のHWNDとtargetRunIdを記録した。
- [ ] 候補総数、同一再解決数、0件/複数件を記録した。
- [ ] 操作試験の許可範囲と試験dataを現地責任者に確認した。
- [ ] 既定read-onlyで書込みkindがblockedになった。
- [ ] focus / invoke / setValueを許可範囲だけで試した。
- [ ] 各probeのmethod、outcome、duration、副作用を確認した。
- [ ] `IsPassword` のsetValue/keysがblockedになった。
- [ ] setValueのundoを確認した。
- [ ] 緊急停止でread-onlyへ戻った。

## 出力と持ち帰り

- [ ] 指定先へpackを出力できた。出力先: |
- [ ] USB/DLPに遮断された場合、操作、時刻、製品表示を記録した。
- [ ] `report.html` が対象アプリなしで単体表示できた。
- [ ] `session.json / shots / diagnostics.log / MANIFEST.json / README.txt` が揃った。
- [ ] MANIFESTのbytes/SHA-256を照合した。
- [ ] `maskedOnly` で値本文がJSON/report/log/event/locatorへ出ていない。
- [ ] `full` を使った場合、画面・mode.change・report結論帯に明示された。
- [ ] `environment.writeTargets` と実際の書込場所・残留物を照合した。

## EDR/DLP観測

| 時刻 | 操作/API経路 | 警告・遮断 | 対象機能を止めたか | 相談結果 |
|---|---|---|---|---|
| | | | | |

対象端末のEDR/DLP製品や規則は事前の設計制約にしていない。ここで初めて観測する。

## 通らなかった場合

| 事象 | 選択肢 |
|---|---|
| PowerShell/Add-Type不可 | 許可申請、署名済み構成への移行、Microsoft既製toolで最低限調査 |
| 特定APIだけ遮断 | その機能だけ止め、Win32/UIA/画像の別経路へ切替 |
| 対象が昇格 | 管理者実行の可否を確認。不可なら `WIN32-ACCESS` を記録 |
| USBへ書けない | 許可された別出力先・持出し手続を確認。toolが勝手にfallbackしないことを確認 |
| `SysListView32/TreeView32` の行dataが必要 | `NEEDS-B3` の実対象証拠を持ってWP-B3を別判断 |
| Java/SAPでUIAが薄い | Java Access Bridge / SAP GUI Scriptingの追加判断 |

## 終了記録

開始:  
終了:  
固定部品数:  
操作probe数:  
取得failure数:  
再起動後の再解決率:  
1部品あたり中央値/最大時間:  
writeTargetsと残留物:  
未解決事項:  

