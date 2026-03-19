using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TobiiEyeMouse;

public partial class MainWindow : Window
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    private const string MinimizeButtonText = "最小化";

    private static readonly SolidColorBrush ColAccent  = new(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly SolidColorBrush ColDanger  = new(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush ColSuccess = new(Color.FromRgb(0x10, 0xB9, 0x81));
    private static readonly SolidColorBrush ColWarn    = new(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush ColSub     = new(Color.FromRgb(0x99, 0x99, 0x99));

    private readonly TobiiStreamEngine _tobii = new();
    private readonly GazeFilter _filter = new();
    private readonly DwellClicker _dwell = new();
    private readonly GazeScroller _scroller = new();
    private readonly GlobalKeyHook _keyHook = new();
    private AppSettings _settings = new();
    private Thread? _worker;
    private volatile bool _running;
    private volatile bool _paused;
    private volatile bool _gazeWorkerRunning;
    private int _sampleCount;
    private double _gazeX, _gazeY;
    private double _dwellProgress;
    private readonly DispatcherTimer _uiTimer = new();

    private GazeOverlay? _overlay;
    private volatile ControlMode _activeMode = ControlMode.GazeUIMode;
    private double _screenW, _screenH;
    private bool _sysCursorHidden;
    private MiniPanelWindow? _miniPanel;
    private readonly Stopwatch _actionDwellSw = new();
    private FrameworkElement? _currentHoverTarget;
    private volatile bool _isDragging;
    private List<Button> _hoverableButtons = new();
    private readonly Stopwatch _cornerDwellSw = new();
    private bool _cornerDwelling;
    private const int CornerSizePx = 150;
    private readonly Stopwatch _pauseBlinkSw = new();


    public MainWindow()
    {
        InitializeComponent();

        // ※ WPF InputBindings は使わない（グローバルフックに一本化して2重発火を防止）

        _uiTimer.Interval = TimeSpan.FromMilliseconds(16);
        _uiTimer.Tick += UiTimer_Tick;
        _dwell.ProgressChanged += p => _dwellProgress = p;
        
        // 注視クリック状態イベント。ダブルクリック完了後にシングルクリックに戻すためのUI同期処理
        _dwell.ClickTypeChanged += type => 
        {
            Dispatcher.BeginInvoke(() => {
                _dwell.ClickType = type;
                UpdateDwellButtonsUI();
            });
        };

        // ドラッグ状態変化イベント
        _dwell.DragStateChanged += (isDragging, startX, startY) =>
        {
            _isDragging = isDragging;
            Dispatcher.BeginInvoke(() =>
            {
                _overlay?.SetDragState(isDragging, startX, startY);
            });
        };

        // グローバルキーフック: ウィンドウ非アクティブでも受信、1回のみ発火
        _keyHook.F5Pressed += () => Dispatcher.BeginInvoke(ToggleTracking);
        _keyHook.F6Pressed += () => Dispatcher.BeginInvoke(TogglePause);
        _keyHook.EscPressed += () => Dispatcher.BeginInvoke(() => Close());
        _keyHook.ClickInput += () =>
        {
            if (_running && !_paused)
                _dwell.TriggerClick();
        };
    }

    // ── 初期化 ──
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            CursorHelper.Restore(); // システムカーソルを確実に標準に戻す
            _keyHook.Install();

            _settings = AppSettings.Load();
            SlResponsive.Value = _settings.Responsiveness;
            SlDead.Value = _settings.Deadzone;
            SlSpeed.Value = _settings.MaxSpeed;
            SlCursorSize.Value = _settings.CursorSize;
            SetCursorRadio(_settings.CursorStyleIndex);
            SetDwellRadio(_settings.DwellClickType); // CheckBox -> RadioButtons
            SlDwellTime.Value = _settings.DwellTimeMs;
            SlDwellRadius.Value = _settings.DwellRadius;
            SlDwellCool.Value = _settings.DwellCooldownMs;
            
            // Scroll settings
            if (BtnScrollToggle != null)
            {
                UpdateScrollButtonUI();
            }
            if (SlScrollEdge != null) SlScrollEdge.Value = _settings.ScrollEdgeSize;
            if (SlScrollSpeed != null) SlScrollSpeed.Value = _settings.ScrollSpeed;
            
            if (_settings.ControlModeIndex == 1) RbGazeUI.IsChecked = true;
            else RbMouse.IsChecked = true;
            Dispatcher.BeginInvoke(() => UpdateModeButtonsUI());

            InitHoverableButtons();

            _screenW = ScreenHelper.PhysicalWidth;
            _screenH = ScreenHelper.PhysicalHeight;
            ApplyFilterValues();
            ApplyDwellValues();
            ApplyScrollValues();

            TxtScreenInfo.Text = $"画面: {ScreenHelper.PhysicalWidth} × {ScreenHelper.PhysicalHeight} (スケール {ScreenHelper.ScalePercent})";

            _overlay = new GazeOverlay();
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                _overlay.Show();
                this.Activate();
            });

            bool dllOk = _tobii.LoadDll();
            if (!dllOk)
            {
                SetStatus("DLL 未検出", Brushes.Transparent, ColDanger);
                TxtTrackerInfo.Text = "Tobii Experience をインストールしてください";
                BtnStart.IsEnabled = false;
                _uiTimer.Start();
                return;
            }

            bool connected = _tobii.Connect();
            if (connected)
            {
                SetStatus("接続済み", ColSuccess, ColSuccess);
                TxtTrackerInfo.Text = $"{_tobii.Model}　S/N: {_tobii.Serial}";
                _tobii.GazePointReceived += OnGazePoint;
            }
            else
            {
                SetStatus("トラッカー未検出", Brushes.Transparent, ColWarn);
                TxtTrackerInfo.Text = "USB 接続を確認してください";
                BtnStart.IsEnabled = false;
            }
            _uiTimer.Start();

            // アプリ起動時に自動的に開始する（トラッカー接続時のみ）
            if (connected)
            {
                Dispatcher.BeginInvoke(() => StartTracking());
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"初期化エラー:\n\n{ex.Message}\n\n{ex.StackTrace}",
                "TobiiEyeMouse", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void InitHoverableButtons()
    {
        _hoverableButtons = new List<Button>
        {
            BtnMinimize, BtnStart, BtnPause,
            BtnDwellNone, BtnDwellSingle, BtnDwellDouble, BtnDwellDrag,
            BtnModeGazeUI, BtnModeMouse,
            BtnDirectToggle,
            BtnCurNone, BtnCurCross, BtnCurCircle, BtnCurDiamond, BtnCurRing, BtnCurDot,
            BtnScrollToggle,
            BtnDwellTimeDec, BtnDwellTimeInc, BtnDwellRadiusDec, BtnDwellRadiusInc, BtnDwellCoolDec, BtnDwellCoolInc,
            BtnResponsiveDec, BtnResponsiveInc, BtnDeadDec, BtnDeadInc, BtnSpeedDec, BtnSpeedInc,
            BtnCursorSizeDec, BtnCursorSizeInc, BtnScrollEdgeDec, BtnScrollEdgeInc, BtnScrollSpeedDec, BtnScrollSpeedInc
        };
    }

    // ── 視線コールバック ──
    private void OnGazePoint(float nx, float ny)
    {
        double sw = _screenW, sh = _screenH;
        if (sw < 1 || sh < 1) return;
        var (fx, fy) = _filter.Update(nx * sw, ny * sh);
        Interlocked.Increment(ref _sampleCount);
        _gazeX = fx; _gazeY = fy;

        // 停止中または一時停止中はカーソル移動・Dwell不要
        if (!_running || _paused) return;

        if (_activeMode == ControlMode.MouseMode || _isDragging)
        {
            SetCursorPos((int)fx, (int)fy);
            _scroller.Update(fy);
        }
        _dwell.Feed(fx, fy);
    }

    // ── トラッキング制御 ──
    private void ToggleTracking() { if (!_running) StartTracking(); else StopTracking(); }

    private void StartTracking()
    {
        if (_running || !_tobii.IsConnected) return;
        _running = true; _paused = false; _sampleCount = 0;
        _filter.Reset(); _dwell.Reset();
        ApplyAllSettingsLive();

        // 初回のみ購読とワーカー起動（停止後の再開では既に動いている）
        if (!_tobii.IsSubscribed)
        {
            if (!_tobii.Subscribe())
            { _running = false; SetStatus("購読エラー", Brushes.Transparent, ColDanger); return; }

            _gazeWorkerRunning = true;
            _worker = new Thread(() => _tobii.ProcessLoop(() => _gazeWorkerRunning)) { IsBackground = true };
            _worker.Start();
        }

        ApplyCursorLive();
        _overlay?.StopDimMode();
        _overlay?.StartOverlay();

        BtnStart.Content = "⏹ 停止"; BtnStart.Background = ColDanger;
        if (TxtStartHint != null) TxtStartHint.Text = "注視/ホバーで停止";
        BtnPause.IsEnabled = true;
        SetStatus(_activeMode == ControlMode.MouseMode ? "マウスモード" : "視線UIモード",
                  ColSuccess, ColSuccess);
    }

    private void StopTracking()
    {
        _running = false; _paused = false;
        _cornerDwelling = false; _cornerDwellSw.Reset(); _pauseBlinkSw.Reset();
        _dwell.CancelDrag(); // ドラッグ中なら安全にキャンセル
        _isDragging = false;
        // ワーカーとTobii購読は維持（停止中も視線座標を更新し続けてBtnStartのホバーを可能にする）
        _dwell.Reset();
        CursorHelper.Restore();
        _sysCursorHidden = false;
        _overlay?.SetDragState(false, 0, 0);
        _overlay?.StopOverlay();
        // 停止中も視線位置がわかるようにかすかなカーソルを表示
        if (_tobii.IsConnected) _overlay?.StartDimMode();

        BtnStart.Content = "▶ 開始"; BtnStart.Background = ColSuccess;
        if (TxtStartHint != null) TxtStartHint.Text = "注視/ホバーで開始";
        BtnPause.IsEnabled = false;
        BtnPause.Content = "⏸ 一時停止";
        if (TxtPauseHint != null) TxtPauseHint.Text = "注視/ホバーで一時停止";
        SetStatus("停止", Brushes.Transparent, ColSub);
        ResetActionHoverState();
    }

    private void TogglePause()
    {
        if (!_running) return;
        _paused = !_paused;
        if (_paused)
        {
            BtnPause.Content = "▶ 再開";
            if (TxtPauseHint != null) TxtPauseHint.Text = "注視/ホバーで再開";
            SetStatus("一時停止", Brushes.Transparent, ColWarn);
            _pauseBlinkSw.Restart();
        }
        else
        {
            _filter.Reset(); _dwell.Reset();
            BtnPause.Content = "⏸ 一時停止";
            if (TxtPauseHint != null) TxtPauseHint.Text = "注視/ホバーで一時停止";
            SetStatus("トラッキング中", ColSuccess, ColSuccess);
            _pauseBlinkSw.Reset();
        }
    }

    // ── ダイナミック設定適用 ──
    private void ApplyAllSettingsLive()
    {
        var newMode = RbGazeUI?.IsChecked == true ? ControlMode.GazeUIMode : ControlMode.MouseMode;
        _activeMode = newMode;
        _dwell.UseMouseEvent = (newMode == ControlMode.MouseMode);
        _screenW = ScreenHelper.PhysicalWidth;
        _screenH = ScreenHelper.PhysicalHeight;
        ApplyFilterValues();
        ApplyDwellValues();
        ApplyScrollValues();
    }

    private void ApplyCursorLive()
    {
        if (!_running) return;
        // マウスアイコンが黒くなる問題や非表示になる問題を回避するため
        // システムカーソルは隠蔽(Hide)せず、標準アイコンを表示する。
        // （オーバーレイで別途視線カーソルを描画する）
        if (_sysCursorHidden)
        {
            CursorHelper.Restore();
            _sysCursorHidden = false;
        }
    }

    // ── UI ──
    private void SetStatus(string text, SolidColorBrush dot, SolidColorBrush fg)
    { StatusDot.Fill = dot; TxtStatus.Text = text; TxtStatus.Foreground = fg; }

    private static readonly Color PauseBlinkDim    = Color.FromRgb(0x78, 0x4F, 0x06);
    private static readonly Color PauseBlinkBright = Color.FromRgb(0xF5, 0x9E, 0x0B);

    private void UpdatePauseButtonBlink()
    {
        if (!_paused || !_running || _currentHoverTarget == BtnPause)
            return;

        double t = (_pauseBlinkSw.ElapsedMilliseconds % 1200) / 1200.0;
        double brightness = 0.5 + 0.5 * Math.Sin(t * Math.PI * 2);
        var c = Color.FromRgb(
            (byte)(PauseBlinkDim.R + (PauseBlinkBright.R - PauseBlinkDim.R) * brightness),
            (byte)(PauseBlinkDim.G + (PauseBlinkBright.G - PauseBlinkDim.G) * brightness),
            (byte)(PauseBlinkDim.B + (PauseBlinkBright.B - PauseBlinkDim.B) * brightness));
        BtnPause.Background = new SolidColorBrush(c);
    }

    private void UiTimer_Tick(object? s, EventArgs e)
    {
        UpdateActionHoverStates();
        UpdatePauseButtonBlink();

        if (_running)
            TxtGaze.Text = $"({_gazeX:F0}, {_gazeY:F0})\n{_sampleCount:#,0} samples";

        if (TxtResponsive != null) TxtResponsive.Text = $"{(int)SlResponsive.Value}%";
        if (TxtDead != null) TxtDead.Text = $"{(int)SlDead.Value} px";
        if (TxtSpeed != null) TxtSpeed.Text = $"{(int)SlSpeed.Value}";
        if (TxtCursorSize != null) TxtCursorSize.Text = $"{(int)SlCursorSize.Value} px";
        if (TxtDwellTime != null) TxtDwellTime.Text = $"{(int)SlDwellTime.Value} ms";
        if (TxtDwellRadius != null) TxtDwellRadius.Text = $"{(int)SlDwellRadius.Value} px";
        if (TxtDwellCool != null) TxtDwellCool.Text = $"{(int)SlDwellCool.Value} ms";
        
        if (TxtScrollEdge != null) TxtScrollEdge.Text = $"{(int)SlScrollEdge.Value} px";
        if (TxtScrollSpeed != null) TxtScrollSpeed.Text = $"{(int)SlScrollSpeed.Value}";

        // 停止中でも視線座標をオーバーレイに反映（かすかなカーソル表示のため）
        if (_tobii.IsConnected)
            _overlay?.UpdateGaze(_gazeX, _gazeY);

        if (_running)
        {
            ApplyAllSettingsLive();
            ApplyCursorLive();

            if (!_paused)
            {
                var modeText = _activeMode == ControlMode.MouseMode ? "マウスモード" : "視線UIモード";
                if (TxtStatus.Text != modeText)
                    SetStatus(modeText, ColSuccess, ColSuccess);
            }

            if (_overlay != null)
            {
                _overlay.SetCursorAppearance(GetSelectedCursorStyle(), (int)SlCursorSize.Value);
                bool dwellOn = _activeMode == ControlMode.MouseMode && _dwell.ClickType != 0 && _dwellProgress > 0.01;
                _overlay.SetDwellState(dwellOn, _dwellProgress, (int)SlDwellRadius.Value);

                _overlay.SetScrollState(_settings.ScrollEnabled, (int)(SlScrollEdge?.Value ?? 100));
            }

            _miniPanel?.CheckGazeIntersect(_gazeX, _gazeY);

            // 一時停止中のコーナートリガー（左上隅を注視で再開）
            if (_paused)
            {
                bool inCorner = _gazeX < CornerSizePx && _gazeY < CornerSizePx;
                if (inCorner)
                {
                    if (!_cornerDwelling) { _cornerDwelling = true; _cornerDwellSw.Restart(); }
                    double progress = Math.Clamp(_cornerDwellSw.ElapsedMilliseconds / (double)GetConfiguredDwellTimeMs(), 0, 1);
                    _overlay?.SetPauseState(true, CornerSizePx, progress);
                    if (_cornerDwellSw.ElapsedMilliseconds >= GetConfiguredDwellTimeMs())
                    {
                        _cornerDwelling = false; _cornerDwellSw.Reset();
                        TogglePause();
                    }
                }
                else
                {
                    _cornerDwelling = false; _cornerDwellSw.Reset();
                    _overlay?.SetPauseState(true, CornerSizePx, 0);
                }
            }
            else
            {
                _overlay?.SetPauseState(false, CornerSizePx, 0);
            }
        }
    }

    private void UpdateActionHoverStates()
    {
        if (!IsVisible)
        {
            ResetActionHoverState();
            return;
        }

        FrameworkElement? hoveredElement = null;

        foreach (var btn in _hoverableButtons)
        {
            if (IsHovered(btn))
            {
                hoveredElement = btn;
                break;
            }
        }

        if (hoveredElement == null)
        {
            ResetActionHoverState();
            return;
        }

        if (_currentHoverTarget != hoveredElement)
        {
            ResetActionHoverState();
            _currentHoverTarget = hoveredElement;
            _actionDwellSw.Restart();
            UpdateHoverHintText(_currentHoverTarget);
            UpdateActionVisual(_currentHoverTarget, GetConfiguredDwellTimeMs());
            return;
        }

        int dwellTimeMs = GetConfiguredDwellTimeMs();
        long elapsed = _actionDwellSw.ElapsedMilliseconds;
        
        UpdateActionVisual(_currentHoverTarget, Math.Max(0, dwellTimeMs - (int)elapsed));
        
        if (elapsed < dwellTimeMs) return;

        var target = _currentHoverTarget;
        ResetActionHoverState();
        ExecuteActionFor(target);
    }

    private bool IsHovered(FrameworkElement? element)
    {
        if (element == null || !element.IsEnabled) return false;

        // 一時停止中・視線UIモード時はマウスホバーを無視
        bool mouseHover = !_paused && _activeMode != ControlMode.GazeUIMode && element.IsMouseOver;

        // 視線ホバーを許可する条件:
        //   動作中: 全ボタン（ただし一時停止中はBtnPauseのみ）
        //   停止中: BtnStartのみ（視線で再開できるように）
        bool allowGaze = _running
            ? (!_paused || element == BtnPause)
            : (_tobii.IsConnected && element == BtnStart);

        bool gazeHover = allowGaze && IsGazeOverElement(element, _gazeX, _gazeY, 24);

        return mouseHover || gazeHover;
    }

    private void ResetActionHoverState()
    {
        _currentHoverTarget = null;
        _actionDwellSw.Reset();

        if (BtnMinimize != null) BtnMinimize.Content = MinimizeButtonText;
        if (TxtMinimizeHint != null) TxtMinimizeHint.Text = "注視/ホバーで最小化";

        if (BtnStart != null) BtnStart.Content = _running ? "⏹ 停止" : "▶ 開始";
        if (TxtStartHint != null) TxtStartHint.Text = "注視/ホバーで" + (_running ? "停止" : "開始");

        if (BtnPause != null) BtnPause.Content = _paused ? "▶ 再開" : "⏸ 一時停止";
        if (TxtPauseHint != null) TxtPauseHint.Text = "注視/ホバーで" + (_paused ? "再開" : "一時停止");

        // ボタンの背景色を元に戻す
        ResetButtonBackground(BtnMinimize, Color.FromRgb(0x37, 0x41, 0x51));
        ResetButtonBackground(BtnStart, _running ? Color.FromRgb(0xEF, 0x44, 0x44) : Color.FromRgb(0x10, 0xB9, 0x81)); // ColDanger or ColSuccess
        ResetButtonBackground(BtnPause, Color.FromRgb(0xF5, 0x9E, 0x0B)); // ColWarn

        UpdateDwellButtonsUI();
        UpdateModeButtonsUI();
        UpdateCursorButtonsUI();
        UpdateDirectButtonUI();
        UpdateScrollButtonUI();
        
        // --- Slider Buttons background reset will be handled by their SolidColorBrush styles directly,
        // unless they are currently hovered or animating.
        foreach (var btn in _hoverableButtons)
        {
            if (btn != BtnMinimize && btn != BtnStart && btn != BtnPause &&
                btn != BtnDwellNone && btn != BtnDwellSingle && btn != BtnDwellDouble && btn != BtnDwellDrag &&
                !btn.Name.StartsWith("BtnMode") &&
                !btn.Name.StartsWith("BtnDirectToggle") &&
                !btn.Name.StartsWith("BtnCur") && !btn.Name.StartsWith("BtnScrollToggle") &&
                btn.Name.StartsWith("Btn"))
            {
                ResetButtonBackground(btn, Color.FromRgb(0x4B, 0x55, 0x63));
            }
        }
    }

    private void ResetButtonBackground(Button? btn, Color defaultColor)
    {
        if (btn != null)
        {
            btn.Background = new SolidColorBrush(defaultColor);
        }
    }

    private void UpdateActionVisual(FrameworkElement target, int remainingMs)
    {
        double progress = Math.Clamp(1.0 - (remainingMs / (double)GetConfiguredDwellTimeMs()), 0, 1);

        if (target is Button btnTarget)
        {
            Color baseColor = GetBaseColorForButton(btnTarget);
            Color progressColor = GetSmartProgressColor(btnTarget, baseColor);
            ApplyButtonProgress(btnTarget, progress, baseColor, progressColor);
        }
    }

    private Color GetBaseColorForButton(Button btn)
    {
        if (btn == BtnMinimize) return Color.FromRgb(0x37, 0x41, 0x51);
        if (btn == BtnStart) return _running ? Color.FromRgb(0xEF, 0x44, 0x44) : Color.FromRgb(0x10, 0xB9, 0x81);
        if (btn == BtnPause) return Color.FromRgb(0xF5, 0x9E, 0x0B);

        if (btn.Background is SolidColorBrush solidBrush)
            return solidBrush.Color;

        return Color.FromRgb(0x4B, 0x55, 0x63);
    }

    // ─── ON/OFF 判定 ───────────────────────────────────────────
    private bool IsButtonOn(Button btn)
    {
        if (btn == BtnDwellNone)   return _dwell.ClickType == 0;
        if (btn == BtnDwellSingle) return _dwell.ClickType == 1;
        if (btn == BtnDwellDouble) return _dwell.ClickType == 2;
        if (btn == BtnDwellDrag)   return _dwell.ClickType == 3;
        if (btn == BtnModeGazeUI)  return RbGazeUI?.IsChecked == true;
        if (btn == BtnModeMouse)   return RbGazeUI?.IsChecked != true;
        if (btn == BtnDirectToggle) return _settings.DirectMode;
        if (btn == BtnScrollToggle) return _settings.ScrollEnabled;
        if (btn == BtnCurNone)    return _settings.CursorStyleIndex == 0;
        if (btn == BtnCurCross)   return _settings.CursorStyleIndex == 1;
        if (btn == BtnCurCircle)  return _settings.CursorStyleIndex == 2;
        if (btn == BtnCurDiamond) return _settings.CursorStyleIndex == 3;
        if (btn == BtnCurRing)    return _settings.CursorStyleIndex == 4;
        if (btn == BtnCurDot)     return _settings.CursorStyleIndex == 5;
        if (btn == BtnStart) return _running;
        if (btn == BtnPause) return _paused;
        return false;
    }

    // ラジオ式（選択中を再選択しても意味なし）
    private bool IsRadioStyleButton(Button btn) =>
        btn == BtnDwellNone || btn == BtnDwellSingle || btn == BtnDwellDouble || btn == BtnDwellDrag ||
        btn == BtnModeGazeUI || btn == BtnModeMouse ||
        btn == BtnCurNone || btn == BtnCurCross || btn == BtnCurCircle ||
        btn == BtnCurDiamond || btn == BtnCurRing || btn == BtnCurDot;

    // 真のトグル（ON/OFFを反転する）
    private bool IsTrueToggleButton(Button btn) =>
        btn == BtnDirectToggle || btn == BtnScrollToggle;

    // そのボタンが選択されたときに使う色
    private Color GetSelectionColor(Button btn)
    {
        if (btn == BtnDwellDrag)    return Color.FromRgb(0xF5, 0x9E, 0x0B); // amber
        if (btn == BtnDirectToggle) return Color.FromRgb(0xDC, 0x26, 0x26); // red
        if (btn == BtnStart)        return _running ? Color.FromRgb(0xEF, 0x44, 0x44) : Color.FromRgb(0x10, 0xB9, 0x81);
        if (btn == BtnPause)        return Color.FromRgb(0xF5, 0x9E, 0x0B);
        return Color.FromRgb(0x3B, 0x82, 0xF6); // ColAccent
    }

    private static Color Brighten(Color c, int amount) => Color.FromRgb(
        (byte)Math.Min(255, c.R + amount),
        (byte)Math.Min(255, c.G + amount),
        (byte)Math.Min(255, c.B + amount));

    /// <summary>ON/OFFの状態に応じてプログレス色を決定する</summary>
    private Color GetSmartProgressColor(Button btn, Color baseColor)
    {
        if (IsRadioStyleButton(btn))
        {
            // 既選択中 → そのまま明るく（再選択しても変化なし）
            // 未選択   → 選択されたときの色に向かって塗られる
            return IsButtonOn(btn) ? Brighten(baseColor, 35) : GetSelectionColor(btn);
        }

        if (IsTrueToggleButton(btn))
        {
            // ON → OFF: 解除を示す暗い警告色
            // OFF → ON: 有効化を示す選択色
            return IsButtonOn(btn)
                ? Color.FromRgb(0x78, 0x1C, 0x1C)
                : GetSelectionColor(btn);
        }

        // BtnStart / BtnPause / BtnMinimize / スライダー系: 従来通り明るくする
        return Brighten(baseColor, 40);
    }

    // ─── ホバー時ヒント更新 ────────────────────────────────────
    private void UpdateHoverHintText(FrameworkElement? target)
    {
        if (target is not Button btn) return;

        bool isOn = IsButtonOn(btn);

        if (btn == BtnStart)
        {
            if (TxtStartHint != null)
                TxtStartHint.Text = _running
                    ? "注視で停止  ─  現在: 動作中"
                    : "注視で開始  ─  現在: 停止中";
            return;
        }
        if (btn == BtnPause)
        {
            if (TxtPauseHint != null)
                TxtPauseHint.Text = _paused
                    ? "注視で再開  ─  現在: 一時停止中"
                    : "注視で一時停止  ─  現在: 動作中";
            return;
        }

    }

    private void ApplyButtonProgress(Button btn, double progress, Color baseColor, Color progressColor)
    {
        if (progress <= 0)
        {
            btn.Background = new SolidColorBrush(baseColor);
            return;
        }
        if (progress >= 1)
        {
            btn.Background = new SolidColorBrush(progressColor);
            return;
        }

        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0)
        };

        // 進行度までの色
        gradient.GradientStops.Add(new GradientStop(progressColor, 0.0));
        gradient.GradientStops.Add(new GradientStop(progressColor, progress));
        
        // それ以降はベース色
        gradient.GradientStops.Add(new GradientStop(baseColor, progress));
        gradient.GradientStops.Add(new GradientStop(baseColor, 1.0));

        btn.Background = gradient;
    }

    private void ExecuteActionFor(FrameworkElement target)
    {
        if (target == BtnMinimize) MinimizeToMiniPanel();
        else if (target == BtnStart) ToggleTracking();
        else if (target == BtnPause) TogglePause();
        else if (target == BtnDwellNone) BtnDwellNone_Click(this, new RoutedEventArgs());
        else if (target == BtnDwellSingle) BtnDwellSingle_Click(this, new RoutedEventArgs());
        else if (target == BtnDwellDouble) BtnDwellDouble_Click(this, new RoutedEventArgs());
        else if (target == BtnDwellDrag) BtnDwellDrag_Click(this, new RoutedEventArgs());
        else if (target == BtnModeGazeUI) BtnModeGazeUI_Click(this, new RoutedEventArgs());
        else if (target == BtnModeMouse)  BtnModeMouse_Click(this, new RoutedEventArgs());
        else if (target == BtnDirectToggle) BtnDirectToggle_Click(this, new RoutedEventArgs());
        
        // Cursor and Scroll
        else if (target == BtnCurNone || target == BtnCurCross || target == BtnCurCircle || target == BtnCurDiamond || target == BtnCurRing || target == BtnCurDot)
            BtnCurStyle_Click(target, new RoutedEventArgs());
        else if (target == BtnScrollToggle) BtnScrollToggle_Click(this, new RoutedEventArgs());

        // Sliders
        else if (target == BtnDwellTimeDec) BtnDwellTimeDec_Click(this, new RoutedEventArgs());
        else if (target == BtnDwellTimeInc) BtnDwellTimeInc_Click(this, new RoutedEventArgs());
        else if (target == BtnDwellRadiusDec) BtnDwellRadiusDec_Click(this, new RoutedEventArgs());
        else if (target == BtnDwellRadiusInc) BtnDwellRadiusInc_Click(this, new RoutedEventArgs());
        else if (target == BtnDwellCoolDec) BtnDwellCoolDec_Click(this, new RoutedEventArgs());
        else if (target == BtnDwellCoolInc) BtnDwellCoolInc_Click(this, new RoutedEventArgs());
        else if (target == BtnResponsiveDec) BtnResponsiveDec_Click(this, new RoutedEventArgs());
        else if (target == BtnResponsiveInc) BtnResponsiveInc_Click(this, new RoutedEventArgs());
        else if (target == BtnDeadDec) BtnDeadDec_Click(this, new RoutedEventArgs());
        else if (target == BtnDeadInc) BtnDeadInc_Click(this, new RoutedEventArgs());
        else if (target == BtnSpeedDec) BtnSpeedDec_Click(this, new RoutedEventArgs());
        else if (target == BtnSpeedInc) BtnSpeedInc_Click(this, new RoutedEventArgs());
        else if (target == BtnCursorSizeDec) BtnCursorSizeDec_Click(this, new RoutedEventArgs());
        else if (target == BtnCursorSizeInc) BtnCursorSizeInc_Click(this, new RoutedEventArgs());
        else if (target == BtnScrollEdgeDec) BtnScrollEdgeDec_Click(this, new RoutedEventArgs());
        else if (target == BtnScrollEdgeInc) BtnScrollEdgeInc_Click(this, new RoutedEventArgs());
        else if (target == BtnScrollSpeedDec) BtnScrollSpeedDec_Click(this, new RoutedEventArgs());
        else if (target == BtnScrollSpeedInc) BtnScrollSpeedInc_Click(this, new RoutedEventArgs());
    }

    private int GetConfiguredDwellTimeMs()
        => SlDwellTime != null ? (int)SlDwellTime.Value : _settings.DwellTimeMs;

    private bool IsGazeOverElement(FrameworkElement element, double gazePhysicalX, double gazePhysicalY, double marginPx = 0)
    {
        if (!element.IsVisible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return false;

        Point topLeft = element.PointToScreen(new Point(0, 0));
        double left = topLeft.X;
        double top = topLeft.Y;
        double width = element.ActualWidth * ScreenHelper.DpiScaleX;
        double height = element.ActualHeight * ScreenHelper.DpiScaleY;

        return gazePhysicalX >= left - marginPx && gazePhysicalX <= left + width + marginPx &&
               gazePhysicalY >= top - marginPx && gazePhysicalY <= top + height + marginPx;
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ApplyFilterValues();
        ApplyDwellValues();
        ApplyScrollValues();
    }
    
    // Add handler for Dwell buttons
    private void CancelDragIfActive()
    {
        if (!_isDragging) return;
        _dwell.CancelDrag();
        _isDragging = false;
        _overlay?.SetDragState(false, 0, 0);
    }

    private void BtnDwellSingle_Click(object sender, RoutedEventArgs e)
    {
        CancelDragIfActive();
        _dwell.ClickType = 1;
        UpdateDwellButtonsUI();
    }

    private void BtnDwellNone_Click(object sender, RoutedEventArgs e)
    {
        CancelDragIfActive();
        _dwell.ClickType = 0;
        UpdateDwellButtonsUI();
    }

    private void BtnDwellDouble_Click(object sender, RoutedEventArgs e)
    {
        CancelDragIfActive();
        _dwell.ClickType = 2;
        UpdateDwellButtonsUI();
    }

    private void BtnDwellDrag_Click(object sender, RoutedEventArgs e)
    {
        // ドラッグ中に再選択された場合はドラッグをキャンセルして初期状態に戻す
        CancelDragIfActive();
        _dwell.ClickType = 3;
        UpdateDwellButtonsUI();
    }

    private void BtnModeGazeUI_Click(object sender, RoutedEventArgs e)
    {
        RbGazeUI.IsChecked = true;
        UpdateModeButtonsUI();
    }

    private void BtnModeMouse_Click(object sender, RoutedEventArgs e)
    {
        RbMouse.IsChecked = true;
        UpdateModeButtonsUI();
    }

    private void Scroll_Checked(object sender, RoutedEventArgs e)
    {
        ApplyScrollValues();
    }

    private void UpdateDwellButtonsUI()
    {
        if (BtnDwellNone == null || BtnDwellSingle == null || BtnDwellDouble == null || BtnDwellDrag == null) return;

        var selectedBrush = ColAccent;
        var normalBrush = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63));
        var selectedBorder = Brushes.White;
        var normalBorder = Brushes.Transparent;
        var dragSelectedBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        var dragSelectedBorder = Brushes.White;

        BtnDwellNone.Background = _dwell.ClickType == 0 ? selectedBrush : normalBrush;
        BtnDwellNone.BorderBrush = _dwell.ClickType == 0 ? selectedBorder : normalBorder;

        BtnDwellSingle.Background = _dwell.ClickType == 1 ? selectedBrush : normalBrush;
        BtnDwellSingle.BorderBrush = _dwell.ClickType == 1 ? selectedBorder : normalBorder;

        BtnDwellDouble.Background = _dwell.ClickType == 2 ? selectedBrush : normalBrush;
        BtnDwellDouble.BorderBrush = _dwell.ClickType == 2 ? selectedBorder : normalBorder;

        BtnDwellDrag.Background = _dwell.ClickType == 3 ? dragSelectedBrush : normalBrush;
        BtnDwellDrag.BorderBrush = _dwell.ClickType == 3 ? dragSelectedBorder : normalBorder;
    }

    private void UpdateModeButtonsUI()
    {
        if (BtnModeGazeUI == null || BtnModeMouse == null) return;

        bool isGazeUI = RbGazeUI?.IsChecked == true;
        var selectedBrush = ColAccent;
        var normalBrush = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63));

        BtnModeGazeUI.Background  = isGazeUI  ? selectedBrush : normalBrush;
        BtnModeGazeUI.BorderBrush = isGazeUI  ? Brushes.White : Brushes.Transparent;
        BtnModeMouse.Background   = !isGazeUI ? selectedBrush : normalBrush;
        BtnModeMouse.BorderBrush  = !isGazeUI ? Brushes.White : Brushes.Transparent;

        // 視線UIモードのときは注視操作枠を無効化する
        if (GbDwell != null)
            GbDwell.IsEnabled = !isGazeUI;

        if (TxtModeDesc != null)
            TxtModeDesc.Text = isGazeUI
                ? "WPFのオーバーレイを使って快適に操作。通常はこちらを使用。"
                : "Windowsのマウスカーソル自体を動かす。他のアプリの干渉が強め。";
    }

    private void BtnDirectToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.DirectMode = !_settings.DirectMode;
        UpdateDirectButtonUI();
        ApplyFilterValues();
    }

    private void UpdateDirectButtonUI()
    {
        if (BtnDirectToggle == null) return;

        bool enabled = _settings.DirectMode;
        var selectedBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
        var normalBrush = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63));
        var selectedBorder = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
        var normalBorder = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));

        BtnDirectToggle.Background = enabled ? selectedBrush : normalBrush;
        BtnDirectToggle.BorderBrush = enabled ? selectedBorder : normalBorder;
    }

    private void ApplyFilterValues()
    {
        if (SlResponsive == null || SlDead == null || SlSpeed == null) return;
        _filter.Responsiveness = (int)SlResponsive.Value / 100.0;
        _filter.DeadzonePx = (int)SlDead.Value;
        _filter.MaxSpeedPx = (int)SlSpeed.Value;
        _filter.DirectMode = _settings.DirectMode;
    }

    private void ApplyDwellValues()
    {
        if (SlDwellTime == null || SlDwellRadius == null || SlDwellCool == null) return;

        // Remove logic that overwrites ClickType from UI
        // ClickType is now the master

        _dwell.DwellTimeMs = (int)SlDwellTime.Value;
        _dwell.DwellRadiusPx = (int)SlDwellRadius.Value;
        _dwell.CooldownMs = (int)SlDwellCool.Value;
    }

    private void ApplyScrollValues()
    {
        if (BtnScrollToggle == null || SlScrollEdge == null || SlScrollSpeed == null) return;
        _scroller.Enabled = _settings.ScrollEnabled;
        _scroller.EdgeSizePx = (int)SlScrollEdge.Value;
        _scroller.ScrollSpeed = (int)SlScrollSpeed.Value;
    }

    private CursorStyle GetSelectedCursorStyle()
    {
        return _settings.CursorStyleIndex switch
        {
            1 => CursorStyle.Crosshair,
            2 => CursorStyle.Circle,
            3 => CursorStyle.Diamond,
            4 => CursorStyle.Ring,
            5 => CursorStyle.Dot,
            _ => CursorStyle.None,
        };
    }

    private void SetCursorRadio(int i)
    {
        // 起動時にボタンの色と状態を更新
        _settings.CursorStyleIndex = i;
        UpdateCursorButtonsUI();
    }

    private void BtnCurStyle_Click(object sender, RoutedEventArgs e)
    {
        if (sender == BtnCurNone) _settings.CursorStyleIndex = 0;
        else if (sender == BtnCurCross) _settings.CursorStyleIndex = 1;
        else if (sender == BtnCurCircle) _settings.CursorStyleIndex = 2;
        else if (sender == BtnCurDiamond) _settings.CursorStyleIndex = 3;
        else if (sender == BtnCurRing) _settings.CursorStyleIndex = 4;
        else if (sender == BtnCurDot) _settings.CursorStyleIndex = 5;

        UpdateCursorButtonsUI();
    }

    private void UpdateCursorButtonsUI()
    {
        if (BtnCurNone == null) return;
        
        var selectedBrush = ColAccent;
        var normalBrush = new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63));
        var selectedBorder = Brushes.White;
        var normalBorder = Brushes.Transparent;

        BtnCurNone.Background = _settings.CursorStyleIndex == 0 ? selectedBrush : normalBrush;
        BtnCurNone.BorderBrush = _settings.CursorStyleIndex == 0 ? selectedBorder : normalBorder;
        BtnCurCross.Background = _settings.CursorStyleIndex == 1 ? selectedBrush : normalBrush;
        BtnCurCross.BorderBrush = _settings.CursorStyleIndex == 1 ? selectedBorder : normalBorder;
        BtnCurCircle.Background = _settings.CursorStyleIndex == 2 ? selectedBrush : normalBrush;
        BtnCurCircle.BorderBrush = _settings.CursorStyleIndex == 2 ? selectedBorder : normalBorder;
        BtnCurDiamond.Background = _settings.CursorStyleIndex == 3 ? selectedBrush : normalBrush;
        BtnCurDiamond.BorderBrush = _settings.CursorStyleIndex == 3 ? selectedBorder : normalBorder;
        BtnCurRing.Background = _settings.CursorStyleIndex == 4 ? selectedBrush : normalBrush;
        BtnCurRing.BorderBrush = _settings.CursorStyleIndex == 4 ? selectedBorder : normalBorder;
        BtnCurDot.Background = _settings.CursorStyleIndex == 5 ? selectedBrush : normalBrush;
        BtnCurDot.BorderBrush = _settings.CursorStyleIndex == 5 ? selectedBorder : normalBorder;
    }

    private void SetDwellRadio(int i)
    {
        // This method is deprecated as naming implies Radio, but we use it to Update Buttons for now (initial load)
        _dwell.ClickType = i; 
        UpdateDwellButtonsUI();
    }

    // Removed GetDwellClickType as we can just use _dwell.ClickType directly

    private void BtnStart_Click(object s, RoutedEventArgs e) => ToggleTracking();
    private void BtnPause_Click(object s, RoutedEventArgs e) => TogglePause();

    private void BtnScrollToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.ScrollEnabled = !_settings.ScrollEnabled;
        UpdateScrollButtonUI();
        ApplyScrollValues();
    }

    private void UpdateScrollButtonUI()
    {
        if (BtnScrollToggle == null) return;
        BtnScrollToggle.Background = _settings.ScrollEnabled ? ColAccent : new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63));
        BtnScrollToggle.BorderBrush = _settings.ScrollEnabled ? new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)) : new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    }

    // --- Slider UI Buttons Events ---
    private void ChangeSliderValue(Slider slider, double direction)
    {
        if (slider == null) return;
        double change = slider.TickFrequency > 0 ? slider.TickFrequency : 1.0;
        slider.Value = Math.Clamp(slider.Value + (change * direction), slider.Minimum, slider.Maximum);
    }

    private void BtnDwellTimeDec_Click(object s, RoutedEventArgs e) => ChangeSliderValue(SlDwellTime, -1);
    private void BtnDwellTimeInc_Click(object s, RoutedEventArgs e) => ChangeSliderValue(SlDwellTime, 1);
    private void BtnDwellRadiusDec_Click(object s, RoutedEventArgs e) => ChangeSliderValue(SlDwellRadius, -1);
    private void BtnDwellRadiusInc_Click(object s, RoutedEventArgs e) => ChangeSliderValue(SlDwellRadius, 1);
    private void BtnDwellCoolDec_Click(object s, RoutedEventArgs e) => ChangeSliderValue(SlDwellCool, -1);
    private void BtnDwellCoolInc_Click(object s, RoutedEventArgs e) => ChangeSliderValue(SlDwellCool, 1);
    private void BtnResponsiveDec_Click(object s, RoutedEventArgs e) => ChangeSliderValue(SlResponsive, -1);
    private void BtnResponsiveInc_Click(object s, RoutedEventArgs e) => ChangeSliderValue(SlResponsive, 1);
    private void BtnDeadDec_Click(object s, RoutedEventArgs e) => ChangeSliderValue(SlDead, -1);
    private void BtnDeadInc_Click(object s, RoutedEventArgs e) => ChangeSliderValue(SlDead, 1);
    private void BtnSpeedDec_Click(object s, RoutedEventArgs e) => ChangeSliderValue(SlSpeed, -1);
    private void BtnSpeedInc_Click(object s, RoutedEventArgs e) => ChangeSliderValue(SlSpeed, 1);
    private void BtnCursorSizeDec_Click(object s, RoutedEventArgs e) => ChangeSliderValue(SlCursorSize, -1);
    private void BtnCursorSizeInc_Click(object s, RoutedEventArgs e) => ChangeSliderValue(SlCursorSize, 1);
    private void BtnScrollEdgeDec_Click(object s, RoutedEventArgs e) => ChangeSliderValue(SlScrollEdge, -1);
    private void BtnScrollEdgeInc_Click(object s, RoutedEventArgs e) => ChangeSliderValue(SlScrollEdge, 1);
    private void BtnScrollSpeedDec_Click(object s, RoutedEventArgs e) => ChangeSliderValue(SlScrollSpeed, -1);
    private void BtnScrollSpeedInc_Click(object s, RoutedEventArgs e) => ChangeSliderValue(SlScrollSpeed, 1);

    private void BtnMinimize_Click(object s, RoutedEventArgs e)
        => MinimizeToMiniPanel();

    private void MinimizeToMiniPanel()
    {
        if (!IsVisible) return;

        ResetActionHoverState();

        if (_miniPanel == null)
        {
            _miniPanel = new MiniPanelWindow();
            _miniPanel.RestoreRequested += RestoreFromMiniPanel;
        }

        _miniPanel.SetDwellTimeMs(GetConfiguredDwellTimeMs());
        _miniPanel.Show();
        this.Hide();
    }

    private void RestoreFromMiniPanel()
    {
        Show();
        Activate();
        ResetActionHoverState();

        if (_miniPanel == null) return;

        _miniPanel.RestoreRequested -= RestoreFromMiniPanel;
        _miniPanel.Close();
        _miniPanel = null;
    }

    private void BtnReset_Click(object s, RoutedEventArgs e)
    {
        RbGazeUI.IsChecked = true;
        UpdateModeButtonsUI();
        SlResponsive.Value = 90; SlDead.Value = 0; SlSpeed.Value = 5000;
        _settings.DirectMode = false;
        UpdateDirectButtonUI();
        
        // Reset Cursor Style to Cross (index 1)
        _settings.CursorStyleIndex = 1;
        UpdateCursorButtonsUI();
        SlCursorSize.Value = 100;
        
        _dwell.CancelDrag(); _isDragging = false;
        _overlay?.SetDragState(false, 0, 0);
        _dwell.ClickType = 1; UpdateDwellButtonsUI();
        SlDwellTime.Value = 2000; SlDwellRadius.Value = 50; SlDwellCool.Value = 1000;
        
        _settings.ScrollEnabled = false;
        UpdateScrollButtonUI();
        SlScrollEdge.Value = 100; SlScrollSpeed.Value = 1;

        _filter.Reset(); _dwell.Reset();
    }

    private void Window_Closing(object? s, CancelEventArgs e)
    {
        _dwell.CancelDrag(); // ドラッグ中なら安全にキャンセル
        _isDragging = false;
        StopTracking();
        // ワーカースレッドとTobii購読をここで終了（StopTracking では維持していたため）
        _gazeWorkerRunning = false;
        _worker?.Join(2000);
        _tobii.Unsubscribe();
        ResetActionHoverState();
        _uiTimer.Stop();
        _keyHook.Dispose();
        _overlay?.Close();
        _miniPanel?.Close();

        _settings.ControlModeIndex = RbGazeUI?.IsChecked == true ? 1 : 0;
        _settings.Responsiveness = (int)SlResponsive.Value;
        _settings.Deadzone = (int)SlDead.Value;
        _settings.MaxSpeed = (int)SlSpeed.Value;
        // _settings.CursorStyleIndex is already updated dynamically now
        _settings.CursorSize = (int)SlCursorSize.Value;
        // ドラッグモード (3) は保存しない（次回起動時にクリックモードで開始）
        _settings.DwellClickType = _dwell.ClickType == 3 ? 1 : _dwell.ClickType;
        _settings.DwellTimeMs = (int)SlDwellTime.Value;
        _settings.DwellRadius = (int)SlDwellRadius.Value;
        _settings.DwellCooldownMs = (int)SlDwellCool.Value;
        
        // _settings.ScrollEnabled is already updated dynamically now
        _settings.ScrollEdgeSize = (int)SlScrollEdge.Value;
        _settings.ScrollSpeed = (int)SlScrollSpeed.Value;
        
        _settings.Save();
        _tobii.Dispose();
        CursorHelper.Restore();
    }
}

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    public RelayCommand(Action<object?> execute) => _execute = execute;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => _execute(p);
}
