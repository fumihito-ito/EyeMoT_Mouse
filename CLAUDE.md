# TobiiEyeMouse — プロジェクト概要

Tobii Eye Tracker 5 の視線データでマウスカーソルを制御する Windows WPF アプリ（C#）。
Tobii Experience の DLL を動的ロードするため SDK のインストール不要。

## 動作要件

- Windows 10 / 11
- Tobii Eye Tracker 5（USB接続）
- Tobii Experience インストール済み
- .NET 8 以上

## ビルド

```bash
dotnet build -c Release

# 単体EXE
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

## ファイル構成

| ファイル | 役割 |
|---------|------|
| `TobiiEyeMouse.csproj` | プロジェクト定義 |
| `App.xaml / .cs` | エントリーポイント・アプリ初期化 |
| `MainWindow.xaml / .cs` | メインUI・全体ロジック |
| `TobiiStreamEngine.cs` | Tobii DLL 動的ロード・視線データ受信 |
| `GazeFilter.cs` | EMAフィルタ・DwellClicker・GazeScroller・AppSettings |
| `GazeOverlay.cs` | 全画面透明オーバーレイ（WPF描画） |
| `CursorHelper.cs` | カーソル形状生成 |
| `GlobalKeyHook.cs` | グローバルキーフック |
| `ScreenHelper.cs` | 物理解像度取得 |
| `MiniPanelWindow.xaml / .cs` | 最小化時のミニパネル |

## アーキテクチャ

```
MainWindow
├── TobiiStreamEngine   視線DLL動的ロード・データ受信
├── GazeFilter          EMAフィルタ・デッドゾーン・最大速度
├── DwellClicker        注視クリック判定・ドラッグ管理
├── GazeScroller        画面端スクロール
├── GazeOverlay         全画面透明オーバーレイ描画
├── MiniPanelWindow     最小化パネル
├── GlobalKeyHook       グローバルキーフック
└── AppSettings         JSON設定管理（%LOCALAPPDATA%\TobiiEyeMouse\settings.json）
```

---

## 操作モード

| モード | 説明 |
|--------|------|
| 視線UIモード | WPFオーバーレイで視線カーソルを描画し、注視でUIを操作。デフォルト。 |
| マウスモード | Win32 `SetCursorPos` でシステムカーソルを直接移動。 |

- **注視操作枠（Dwell Click）は視線UIモード時に `IsEnabled=false`** で無効化される。

## 注視クリック（Dwell Click）— マウスモード専用

| パラメータ | 範囲 | デフォルト | 単位 |
|-----------|------|-----------|------|
| クリック種別 | なし / クリック / ダブルクリック / ドラッグ | クリック | — |
| 注視時間 | 200〜3000 | 2000 | ms |
| 判定半径 | 10〜150 | 50 | px |
| クールダウン | 100〜2000 | 1000 | ms |

ドラッグ: 注視完了でドラッグ開始 → 次の注視完了でドロップ → クリックモードに自動復帰。ドラッグ中は判定半径 1.5 倍。

## ブレ補正・フィルタ設定

| パラメータ | 範囲 | デフォルト | 説明 |
|-----------|------|-----------|------|
| 応答性（α） | 5〜100% | 90% | EMA の重み。高いほど追従が速い |
| デッドゾーン | 0〜60 px | 0 | この距離未満の移動を無視 |
| 最大速度 | 500〜15000 | 5000 | px/s |
| ダイレクトモード | ON/OFF | OFF | フィルタをすべてバイパス |

## カーソル設定

形状 6 種（∅ / ✚ / ● / ◆ / ◎ / ⚫）、サイズ 20〜200 px（デフォルト 100）。ドラッグ中はオレンジ系に配色変更。

## スクロール設定

| パラメータ | 範囲 | デフォルト |
|-----------|------|-----------|
| 有効/無効 | ON/OFF | OFF |
| エッジ判定幅 | 50〜400 px | 100 px |
| スクロール速度 | 1〜20 | 3 |

最大発火レート 20回/秒。上端→アップ、下端→ダウン。

## グローバルキーショートカット

| キー | 動作 |
|------|------|
| F5 | 開始 / 停止 |
| F6 | 一時停止 / 再開 |
| Esc | 終了 |

一時停止中は左上コーナー（150×150 px）に注視すると再開。

---

## UI 設計方針

- **視線で操作できること**を最優先。全ての設定変更は視線ホバーで完結できること。
- ボタンは注視プログレス（Dwell）で発火する。スライダーには必ず ー / ＋ ボタンを添える。
- カスタムテンプレートは使わない（デザイナー編集との互換性維持）。
- ダーク UI（背景 `#1E1E2E`）、フォント Segoe UI 16px ベース。

## 実装上の注意事項

- `GazeOverlay` はクリック透過（`WS_EX_TRANSPARENT`）を維持すること。
- グローバルキーフックはアプリ終了時に必ず解放すること（`Window_Closing`）。
- `SetCursorPos` / `mouse_event` は物理ピクセル座標で渡す。DPI変換に注意。
- ドラッグ完了後はクリックモード（`ClickType=1`）に自動復帰する。

## 開発フロー

```bash
git pull          # 作業前に最新を取得
# ... 変更 ...
git add <ファイル>
git commit -m "説明"
git push
```

ブランチ: `master`（常に動作する状態）/ `feature/*` / `fix/*`
