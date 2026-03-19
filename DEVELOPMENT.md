# 開発ガイド — TobiiEyeMouse

## 環境構築

### 必要なツール
- .NET 8 SDK
- Visual Studio 2022 または VS Code（C# Dev Kit拡張）
- Git
- GitHub CLI (`gh`)

### クローン後の初回セットアップ
```bash
git clone https://github.com/fumihito-ito/EyeMoT_Mouse.git
cd EyeMoT_Mouse
dotnet restore
```

---

## ビルド

```bash
# デバッグビルド
dotnet build

# リリースビルド
dotnet build -c Release

# 単体EXE（自己完結）
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

出力先: `binpublish/` （`.gitignore` で除外済み）

---

## 開発フロー

### 日常の作業サイクル

```bash
# 作業前に最新を取得
git pull

# 変更したファイルをステージ
git add <ファイル>

# コミット
git commit -m "変更内容の簡潔な説明"

# プッシュ
git push
```

### ブランチ運用

| ブランチ | 用途 |
|---------|------|
| `master` | 常に動作する状態を維持 |
| `feature/*` | 新機能開発 |
| `fix/*` | バグ修正 |

機能追加・バグ修正はブランチを切って PR でマージすることを推奨。

---

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

---

## 設定ファイル

- 場所: `%LOCALAPPDATA%\TobiiEyeMouse\settings.json`
- クラス: `AppSettings`（`GazeFilter.cs` 内）
- 変更時は即時保存される

---

## UI 設計方針

- **視線で操作できること**を最優先。全ての設定変更は視線ホバーで完結できること。
- ボタンは注視プログレス（Dwell）で発火する。スライダーには必ず ー / ＋ ボタンを添える。
- カスタムテンプレートは使わない（デザイナー編集との互換性維持のため）。
- ダーク UI（背景 `#1E1E2E`）、フォント Segoe UI 16px ベース。

---

## 注意事項

- `GbDwell`（注視操作枠）は視線UIモード時に `IsEnabled=false` にすること。
- ドラッグ完了後はクリックモード（`ClickType=1`）に自動復帰する。
- `GazeOverlay` はクリック透過（`WS_EX_TRANSPARENT`）を維持すること。
- グローバルキーフックはアプリ終了時に必ず解放すること（`Window_Closing`）。
- `SetCursorPos` / `mouse_event` は物理ピクセル座標で渡す。DPI変換に注意。

---

## 仕様書

詳細な機能仕様は [SPEC.md](SPEC.md) を参照。
