using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace TobiiEyeMouse;

public partial class MiniPanelWindow : Window
{
    private static readonly TimeSpan GazeHoverTimeout = TimeSpan.FromMilliseconds(100);

    private readonly Stopwatch _restoreDwellSw = new();
    private readonly DispatcherTimer _hoverTimer = new();
    private bool _restoreDwelling;
    private int _dwellTimeMs = 2000;
    private DateTime _lastGazeHoverUtc = DateTime.MinValue;

    public event Action? RestoreRequested;

    public MiniPanelWindow()
    {
        InitializeComponent();
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(16);
        _hoverTimer.Tick += HoverTimer_Tick;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // 画面右下に配置
        var workArea = SystemParameters.WorkArea;
        this.Left = workArea.Right - this.Width - 20;
        this.Top = workArea.Bottom - this.Height - 20;
        _hoverTimer.Start();
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        ResetRestoreHoverState();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _hoverTimer.Stop();
        ResetRestoreHoverState();
    }

    public void SetDwellTimeMs(int dwellTimeMs)
    {
        _dwellTimeMs = Math.Max(200, dwellTimeMs);
        ResetRestoreHoverState();
    }

    public void CheckGazeIntersect(double gazePhysicalX, double gazePhysicalY)
    {
        double pLeft = this.Left * ScreenHelper.DpiScaleX;
        double pTop = this.Top * ScreenHelper.DpiScaleY;
        double pWidth = this.Width * ScreenHelper.DpiScaleX;
        double pHeight = this.Height * ScreenHelper.DpiScaleY;

        // パネルは小さいので、判定を少し周りにも広げる
        double hitMarginPx = 50 * ScreenHelper.DpiScaleX;
        
        bool isHit = gazePhysicalX >= pLeft - hitMarginPx && gazePhysicalX <= pLeft + pWidth + hitMarginPx &&
                     gazePhysicalY >= pTop - hitMarginPx && gazePhysicalY <= pTop + pHeight + hitMarginPx;

        if (isHit)
            _lastGazeHoverUtc = DateTime.UtcNow;
    }

    private void HoverTimer_Tick(object? sender, EventArgs e)
    {
        bool gazeHover = DateTime.UtcNow - _lastGazeHoverUtc <= GazeHoverTimeout;
        bool hoverActive = IsMouseOver || gazeHover;

        if (!hoverActive)
        {
            ResetRestoreHoverState();
            return;
        }

        if (!_restoreDwelling)
        {
            _restoreDwelling = true;
            _restoreDwellSw.Restart();
            UpdateRestoreVisual(_dwellTimeMs);
            return;
        }

        long elapsed = _restoreDwellSw.ElapsedMilliseconds;
        int remainingMs = Math.Max(0, _dwellTimeMs - (int)elapsed);
        UpdateRestoreVisual(remainingMs);

        if (elapsed >= _dwellTimeMs)
        {
            ResetRestoreHoverState();
            RestoreRequested?.Invoke();
        }
    }

    private void ResetRestoreHoverState()
    {
        _restoreDwelling = false;
        _restoreDwellSw.Reset();
        if (TxtRestoreHint != null) TxtRestoreHint.Text = "注視/ホバーで復帰";
        if (PbRestore != null) PbRestore.Value = 0;
    }

    private void UpdateRestoreVisual(int remainingMs)
    {
        if (TxtRestoreHint != null)
            TxtRestoreHint.Text = "注視/ホバーで復帰";

        if (PbRestore != null)
            PbRestore.Value = Math.Clamp(1.0 - (remainingMs / (double)_dwellTimeMs), 0, 1);
    }
}
