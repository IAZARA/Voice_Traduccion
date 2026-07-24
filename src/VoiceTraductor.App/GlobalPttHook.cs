using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VoiceTraductor.App;

public sealed class GlobalPttHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private readonly HookProcedure _procedure;
    private nint _hook;
    private bool _pressed;

    public GlobalPttHook(int virtualKey)
    {
        VirtualKey = virtualKey;
        _procedure = HookCallback;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = module is null ? 0 : GetModuleHandle(module.ModuleName);
        _hook = SetWindowsHookEx(WhKeyboardLl, _procedure, moduleHandle, 0);
        if (_hook == 0)
        {
            throw new InvalidOperationException(
                $"No se pudo registrar la tecla PTT. Error de Windows: {Marshal.GetLastWin32Error()}.");
        }
    }

    public int VirtualKey { get; }

    public event EventHandler? Pressed;
    public event EventHandler? Released;

    public void Dispose()
    {
        if (_hook == 0)
        {
            return;
        }

        UnhookWindowsHookEx(_hook);
        _hook = 0;
    }

    private nint HookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var key = Marshal.ReadInt32(lParam);
            var message = unchecked((int)wParam);
            if (key == VirtualKey && message is WmKeyDown or WmSysKeyDown && !_pressed)
            {
                _pressed = true;
                Pressed?.Invoke(this, EventArgs.Empty);
            }
            else if (key == VirtualKey &&
                     message is WmKeyUp or WmSysKeyUp &&
                     _pressed)
            {
                _pressed = false;
                Released?.Invoke(this, EventArgs.Empty);
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private delegate nint HookProcedure(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hookId,
        HookProcedure callback,
        nint module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(
        nint hook,
        int code,
        nint wParam,
        nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? moduleName);
}
