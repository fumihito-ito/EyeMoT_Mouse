using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace TobiiEyeMouse;

/// <summary>
/// グローバルキーボードフック（WH_KEYBOARD_LL）+ XInput ゲームパッド監視。
/// ウィンドウが非アクティブでも入力を検出する。
/// 
/// 2重発火防止:
///  - キーダウン時に押下中フラグをセットし、キーアップでクリア
///  - フラグがセット済みならイベントを無視（リピート防止）
/// </summary>
public class GlobalKeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN    = 0x0100;
    private const int WM_KEYUP      = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP   = 0x0105;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint VK_SPACE  = 0x20;
    private const uint VK_RETURN = 0x0D;
    private const uint VK_F5     = 0x74;
    private const uint VK_F6     = 0x75;
    private const uint VK_ESCAPE = 0x1B;

    // ── XInput ──
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(int dwUserIndex, out XINPUT_STATE pState);

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    private const ushort DPAD_MASK   = 0x000F;
    private const ushort BUTTON_MASK = unchecked((ushort)(0xFFFF & ~DPAD_MASK));
    private static bool _xinputAvailable = true;

    // ── イベント ──
    public event Action? ClickInput;
    public event Action? F5Pressed;
    public event Action? F6Pressed;
    public event Action? EscPressed;

    // ── 内部 ──
    private IntPtr _hookId;
    private LowLevelKeyboardProc? _hookProc;
    private Thread? _gamepadThread;
    private volatile bool _gamepadRunning;
    private ushort _prevButtons;
    private bool _disposed;

    // 押下状態管理（キーリピート防止）
    private readonly HashSet<uint> _keysDown = new();
    private readonly object _keyLock = new();

    public void Install()
    {
        _hookProc = HookCallback;
        using var proc = Process.GetCurrentProcess();
        using var mod = proc.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc,
            GetModuleHandle(mod.ModuleName), 0);

        _gamepadRunning = true;
        _gamepadThread = new Thread(GamepadLoop) { IsBackground = true, Name = "GamepadPoll" };
        _gamepadThread.Start();
    }

    public void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
        _gamepadRunning = false;
        _gamepadThread?.Join(500);
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            bool isDown = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
            bool isUp   = (msg == WM_KEYUP || msg == WM_SYSKEYUP);

            if (isDown || isUp)
            {
                var kbd = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                uint vk = kbd.vkCode;

                // 対象キー（アプリで処理するキー）かどうか
                bool isTarget = (vk == VK_SPACE || vk == VK_RETURN || vk == VK_F5 || vk == VK_F6 || vk == VK_ESCAPE);

                if (isDown)
                {
                    bool isRepeat;
                    lock (_keyLock) { isRepeat = !_keysDown.Add(vk); }

                    if (isRepeat)
                    {
                        // リピート時：対象キーなら消費してリピート防止、それ以外はシステムへ
                        if (isTarget) return (IntPtr)1;
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    // 初回押下処理
                    if (isTarget)
                    {
                        switch (vk)
                        {
                            case VK_SPACE:
                            case VK_RETURN:
                                ClickInput?.Invoke();
                                break;
                            case VK_F5:
                                F5Pressed?.Invoke();
                                break;
                            case VK_F6:
                                F6Pressed?.Invoke();
                                break;
                            case VK_ESCAPE:
                                EscPressed?.Invoke();
                                break;
                        }
                        return (IntPtr)1; // 入力を消費
                    }
                }
                else // isUp
                {
                    lock (_keyLock) { _keysDown.Remove(vk); }
                    if (isTarget) return (IntPtr)1; // 入力を消費
                }
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void GamepadLoop()
    {
        while (_gamepadRunning)
        {
            try
            {
                if (!_xinputAvailable) { Thread.Sleep(1000); continue; }

                for (int i = 0; i < 4; i++)
                {
                    int result = XInputGetState(i, out var state);
                    if (result != 0) continue;

                    ushort buttons = (ushort)(state.Gamepad.wButtons & BUTTON_MASK);
                    ushort pressed = (ushort)(buttons & ~_prevButtons);
                    _prevButtons = buttons;

                    if (pressed != 0)
                    {
                        ClickInput?.Invoke();
                        break;
                    }
                }
            }
            catch (DllNotFoundException) { _xinputAvailable = false; }
            catch { }

            Thread.Sleep(16);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Uninstall();
    }
}
