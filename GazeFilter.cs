using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace TobiiEyeMouse;

/// <summary>視線スムージングフィルタ（EMA方式）</summary>
public class GazeFilter
{
    public double Responsiveness { get; set; } = 0.9;
    public double DeadzonePx { get; set; } = 0.0;
    public double MaxSpeedPx { get; set; } = 5000.0;
    public bool DirectMode { get; set; } = false;

    private double _emaX, _emaY, _outX, _outY;
    private bool _initialized;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastTicks;
    private readonly object _lock = new();

    public (double X, double Y) Update(double rawX, double rawY)
    {
        lock (_lock)
        {
            double sw = ScreenHelper.PhysicalWidth;
            double sh = ScreenHelper.PhysicalHeight;

            if (DirectMode)
            {
                _outX = Math.Clamp(rawX, 0, sw - 1);
                _outY = Math.Clamp(rawY, 0, sh - 1);
                return (_outX, _outY);
            }

            long now = _sw.ElapsedMilliseconds;
            double dt = _lastTicks == 0 ? 0.016 : (now - _lastTicks) / 1000.0;
            _lastTicks = now;
            if (dt < 0.001) dt = 0.001;

            if (!_initialized)
            {
                _emaX = rawX; _emaY = rawY; _outX = rawX; _outY = rawY;
                _initialized = true;
                return (rawX, rawY);
            }

            double alpha = Math.Clamp(Responsiveness, 0.01, 1.0);
            _emaX = alpha * rawX + (1.0 - alpha) * _emaX;
            _emaY = alpha * rawY + (1.0 - alpha) * _emaY;

            double dx = _emaX - _outX, dy = _emaY - _outY;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist < DeadzonePx) return (_outX, _outY);

            if (MaxSpeedPx > 0)
            {
                double maxMove = MaxSpeedPx * dt;
                if (dist > maxMove) { double r = maxMove / dist; dx *= r; dy *= r; }
            }

            _outX = Math.Clamp(_outX + dx, 0, sw - 1);
            _outY = Math.Clamp(_outY + dy, 0, sh - 1);
            return (_outX, _outY);
        }
    }

    public void Reset() { lock (_lock) { _initialized = false; _lastTicks = 0; } }
}

/// <summary>
/// 注視クリック。マウスモードでは mouse_event、視線UIモードでは Activated イベント。
/// 外部入力（キー/パッド）でも即座にクリックを発行できる TriggerClick メソッドを追加。
/// </summary>
public class DwellClicker
{
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP   = 0x0004;

    public int ClickType { get; set; } = 1; // 0=None, 1=Click, 2=DoubleClick, 3=Drag
    public event Action<int>? ClickTypeChanged;

    public int DwellTimeMs { get; set; } = 2000;
    public double DwellRadiusPx { get; set; } = 50.0;
    public int CooldownMs { get; set; } = 1000;
    public bool UseMouseEvent { get; set; } = true;

    // ── ドラッグ状態 ──
    private bool _isDragging;
    private double _dragStartX, _dragStartY;
    public bool IsDragging => _isDragging;
    public double DragStartX => _dragStartX;
    public double DragStartY => _dragStartY;

    /// <summary>ドラッグ状態変化 (isDragging, startX, startY)</summary>
    public event Action<bool, double, double>? DragStateChanged;

    private double _anchorX, _anchorY;
    private double _lastFeedX, _lastFeedY;
    private readonly Stopwatch _dwellSw = new();
    private readonly Stopwatch _coolSw = new();
    private bool _dwelling;

    public event Action<double, double>? Activated;
    public event Action<double>? ProgressChanged;

    public void Feed(double screenX, double screenY)
    {
        _lastFeedX = screenX;
        _lastFeedY = screenY;

        // ドラッグ中はマウスカーソルを追従させる
        if (_isDragging)
        {
            SetCursorPos((int)screenX, (int)screenY);
        }

        if (ClickType == 0) return;

        if (_coolSw.IsRunning && _coolSw.ElapsedMilliseconds < CooldownMs)
        {
            ProgressChanged?.Invoke(0);
            return;
        }
        _coolSw.Reset();

        double dx = screenX - _anchorX, dy = screenY - _anchorY;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        // ドラッグ中は判定半径を大きくして、ドロップしやすくする
        double effectiveRadius = _isDragging ? DwellRadiusPx * 1.5 : DwellRadiusPx;

        if (!_dwelling || dist > effectiveRadius)
        {
            _anchorX = screenX; _anchorY = screenY;
            _dwellSw.Restart();
            _dwelling = true;
            ProgressChanged?.Invoke(0);
            return;
        }

        double elapsed = _dwellSw.ElapsedMilliseconds;
        double progress = Math.Clamp(elapsed / DwellTimeMs, 0, 1);
        ProgressChanged?.Invoke(progress);

        if (elapsed >= DwellTimeMs)
        {
            FireClick(screenX, screenY);
            _dwelling = false;
            _dwellSw.Reset();
            _coolSw.Restart();
            ProgressChanged?.Invoke(0);
        }
    }

    /// <summary>外部入力（キー/パッド）から即座にクリックを発行</summary>
    public void TriggerClick()
    {
        FireClick(_lastFeedX, _lastFeedY);
        _dwelling = false;
        _dwellSw.Reset();
        _coolSw.Restart();
        ProgressChanged?.Invoke(0);
    }

    private void FireClick(double x, double y)
    {
        // ── ドラッグモード (ClickType == 3) ──
        if (ClickType == 3)
        {
            if (!_isDragging)
            {
                // ドラッグ開始
                _dragStartX = x;
                _dragStartY = y;
                _isDragging = true;
                if (UseMouseEvent)
                {
                    SetCursorPos((int)x, (int)y);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                }
                DragStateChanged?.Invoke(true, x, y);
            }
            else
            {
                // ドロップ（ドラッグ終了）
                if (UseMouseEvent)
                {
                    SetCursorPos((int)x, (int)y);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
                }
                _isDragging = false;
                DragStateChanged?.Invoke(false, 0, 0);

                // 自動的にクリックモードに復帰
                ClickType = 1;
                ClickTypeChanged?.Invoke(1);
            }
            Activated?.Invoke(x, y);
            return;
        }

        // ── 通常クリック / ダブルクリック ──
        if (UseMouseEvent)
        {
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);

            if (ClickType == 2)
            {
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);

                ClickType = 1;
                ClickTypeChanged?.Invoke(1);
            }
        }
        Activated?.Invoke(x, y);
    }

    /// <summary>ドラッグを安全にキャンセル（LEFTUP発行）</summary>
    public void CancelDrag()
    {
        if (!_isDragging) return;
        if (UseMouseEvent)
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        _isDragging = false;
        DragStateChanged?.Invoke(false, 0, 0);
    }

    public void Reset()
    {
        CancelDrag();
        _dwelling = false;
        _dwellSw.Reset();
        _coolSw.Reset();
    }
}

/// <summary>
/// 画面端でのスクロール制御
/// </summary>
public class GazeScroller
{
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, IntPtr dwExtraInfo);
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    public bool Enabled { get; set; } = false;
    public int EdgeSizePx { get; set; } = 100;
    public int ScrollSpeed { get; set; } = 1; // 1=Slow, 5=Fast (Steps per tick)

    private long _lastScrollTicks;
    private const int SCROLL_INTERVAL_MS = 50; // 20 times per second max

    public void Update(double y)
    {
        if (!Enabled) return;

        double sh = ScreenHelper.PhysicalHeight;
        if (sh < 1) return;

        long now = Stopwatch.GetTimestamp() / TimeSpan.TicksPerMillisecond;
        if (now - _lastScrollTicks < SCROLL_INTERVAL_MS) return;

        int scrollAmount = 0;
        // Top edge -> Scroll Up (Positive)
        if (y < EdgeSizePx)
        {
            scrollAmount = 120 * ScrollSpeed;
        }
        // Bottom edge -> Scroll Down (Negative)
        else if (y > sh - EdgeSizePx)
        {
            scrollAmount = -120 * ScrollSpeed;
        }

        if (scrollAmount != 0)
        {
            mouse_event(MOUSEEVENTF_WHEEL, 0, 0, (uint)scrollAmount, IntPtr.Zero);
            _lastScrollTicks = now;
        }
    }
}

public enum ControlMode { MouseMode, GazeUIMode }
public enum CursorStyle { None = 0, Crosshair, Circle, Diamond, Ring, Dot }

/// <summary>JSON設定（デフォルト値を変更済み）</summary>
public class AppSettings
{
    public int ControlModeIndex { get; set; } = 1;    // デフォルト: 視線UIモード
    public int Responsiveness { get; set; } = 90;      // 90%
    public int Deadzone { get; set; } = 0;             // 0
    public int MaxSpeed { get; set; } = 5000;          // 5000
    public bool DirectMode { get; set; } = false;
    public int CursorStyleIndex { get; set; } = 1;     // 十字
    public int CursorSize { get; set; } = 100;         // 100
    public int DwellClickType { get; set; } = 1;       // 1=Click, 2=Double
    public int DwellTimeMs { get; set; } = 2000;       // 2000ms
    public int DwellRadius { get; set; } = 50;         // 50px
    public int DwellCooldownMs { get; set; } = 1000;   // 1000ms
    
    // Scroll Settings
    public bool ScrollEnabled { get; set; } = false;
    public int ScrollEdgeSize { get; set; } = 100;
    public int ScrollSpeed { get; set; } = 1;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TobiiEyeMouse", "settings.json");

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }
}
