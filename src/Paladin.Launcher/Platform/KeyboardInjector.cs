using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Paladin.Core.Dump;
using Paladin.Core.Logging;

namespace Paladin.Launcher.Platform;

/// <summary>
/// The keyboard and the clipboard, as the dump needs them (§717 §3.2, §3.4, §3.6).
///
/// Keys go in through SendInput, which is what AutoHotkey v2's default SendMode does —
/// the mode the kit's 89 successful runs used (console-dump.ahk:18-23). Every key is
/// sent by SCAN CODE, not by virtual key: the console's toggle is the physical key left
/// of 1 ({sc29} = 0x29), and on the owner's en-GB layout that key is not the US
/// backtick's virtual key. A scan code is the same physical key on both layouts, which
/// is exactly why the kit used one. The other three keys the dump needs — Ctrl, V and
/// Return — are ordinary physical keys too, so they are sent the same way and no virtual
/// key is needed anywhere in this class.
///
/// Every sequence checks that the game holds the foreground first and once more at the
/// end (console-dump.ahk:45-54, 95-96). If it does not, one BringToFront is tried; if it
/// still does not, NOTHING is sent and the caller is told — §3.6 rule 3 makes that the
/// end of the run, because a half-typed line joined to the next one is the 247446929
/// fatal error.
///
/// D5: a line is delivered by clipboard paste, which is the proven path; the user's own
/// text clipboard is read before the first line and put back at the end. Only text can be
/// put back — an image is reported lost in the console — and a clipboard that was empty
/// when the run borrowed it is emptied again rather than left holding a line of Lua. The
/// clipboard is always opened with this exe's own console window as its owner, never
/// NULL, because EmptyClipboard() on a NULL-owned clipboard makes the SetClipboardData
/// after it fail: the user's text would be gone with nothing put back. If the clipboard
/// cannot be opened at all, the line is typed as Unicode key events instead and the
/// record says so.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KeyboardInjector : IConsoleKeys
{
    // ---- SendInput ---------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public IntPtr ExtraInfo;
    }

    /// <summary>
    /// INPUT's union. Only the keyboard arm is ever filled, but the union is the size of
    /// its largest arm (MOUSEINPUT, 32 bytes on x64) and SendInput refuses a structure
    /// whose size is not the one it expects — hence the explicit size rather than a
    /// mouse field that is never assigned. Measured on this runtime: KEYBDINPUT 24,
    /// the union 32, INPUT 40 with the union at offset 8, which is the Win32 x64 layout.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Union;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    // ---- the clipboard -----------------------------------------------------------------

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr handle);

    /// <summary>This console exe's own window: the clipboard's owner for the whole run (see <see cref="TryOpenClipboard"/>).</summary>
    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();

    private const uint InputKeyboard = 1;
    private const uint KeyEventScanCode = 0x0008;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;

    /// <summary>The key left of 1 — the console's toggle, by scan code so the layout cannot move it.</summary>
    public const ushort ScanConsoleKey = 0x29;
    public const ushort ScanLeftAlt = 0x38;
    public const ushort ScanLeftShift = 0x2A;
    public const ushort ScanLeftControl = 0x1D;
    public const ushort ScanV = 0x2F;
    public const ushort ScanReturn = 0x1C;

    private const uint CfUnicodeText = 13;

    /// <summary>How many times the clipboard is asked for, 50 ms apart: another process can hold it for a moment.</summary>
    public const int ClipboardTries = 10;
    public const int ClipboardRetryMs = 50;

    /// <summary>What the user's clipboard held when the run first borrowed it, and so what can be done at the end.</summary>
    private enum SavedClipboard
    {
        /// <summary>Never borrowed: no line was ever pasted, so there is nothing to put back or clear.</summary>
        NotTouched,
        /// <summary>Text, kept in <see cref="_savedClipboardText"/>: it goes back.</summary>
        Text,
        /// <summary>An image, a file list, a spreadsheet's own format: it cannot go back, and the console says so.</summary>
        NonText,
        /// <summary>Opened and found to hold nothing: a line of this run's may be cleared off it at the end.</summary>
        Empty,
        /// <summary>
        /// The clipboard could not be opened to look. Whatever is on it is unknown, so it
        /// is never emptied — that would throw away something this run never even read.
        /// </summary>
        Unreadable,
    }

    private readonly int _pid;
    private readonly PaladinLog _log;
    private readonly IntPtr _clipboardOwner;
    private IntPtr _hWnd;
    private string? _savedClipboardText;
    private SavedClipboard _saved = SavedClipboard.NotTouched;
    /// <summary>True once one of this run's console lines actually reached the clipboard.</summary>
    private bool _clipboardWritten;

    public KeyboardInjector(int pid, IntPtr hWnd, PaladinLog log)
    {
        _pid = pid;
        _hWnd = hWnd;
        _log = log;
        // EmptyClipboard() sets the clipboard owner to NULL when the clipboard was opened
        // with a NULL window, and SetClipboardData is then documented to fail — which
        // would empty the user's clipboard and never put it back. This process has a
        // console window, so it has a real handle to own it with. Zero only if it somehow
        // has not (a detached console); the calls still go through, as they always did.
        _clipboardOwner = GetConsoleWindow();
        if (_clipboardOwner == IntPtr.Zero)
            _log.Warn("This process has no console window to own the clipboard with; the clipboard may refuse the paste path.");
    }

    /// <summary>"paste" until a clipboard failure forces the typed path; from then on "type".</summary>
    public string InputMethod { get; private set; } = "paste";

    /// <summary>True when the user's clipboard held something that is not text, so it could not be put back.</summary>
    public bool ClipboardImageLost => _saved == SavedClipboard.NonText;

    // ---- IConsoleKeys ---------------------------------------------------------------------

    public bool IsGameInFront()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        if (foreground == _hWnd) return true;
        // The game swaps its splash window for the real one a few seconds in; follow it
        // rather than insist on the handle found at launch.
        GetWindowThreadProcessId(foreground, out var owner);
        if (owner != (uint)_pid) return false;
        _hWnd = foreground;
        return true;
    }

    public KeySendResult Chord(ConsoleChord chord)
    {
        var modifier = chord == ConsoleChord.AltShift ? ScanLeftAlt : ScanLeftControl;
        var keys = new[]
        {
            Down(modifier), Down(ScanLeftShift), Down(ScanConsoleKey),
            Up(ScanConsoleKey), Up(ScanLeftShift), Up(modifier),
        };
        return Deliver($"the {ConsoleChords.Name(chord)} chord", keys);
    }

    public KeySendResult PasteLine(string text)
    {
        if (InputMethod == "paste")
        {
            SaveClipboardOnce();
            if (TrySetClipboardText(text))
                return Deliver("Ctrl+V", new[] { Down(ScanLeftControl), Down(ScanV), Up(ScanV), Up(ScanLeftControl) });

            // D5's fallback. Announced once: from here on every line is typed, because a
            // clipboard that refused once is likely to refuse again and the record must say
            // which path the run actually used.
            _log.Warn("The clipboard could not be set; typing the console lines instead of pasting them.");
            InputMethod = "type";
        }
        return Deliver("the typed line", Unicode(text));
    }

    public KeySendResult Enter() => Deliver("Return", new[] { Down(ScanReturn), Up(ScanReturn) });

    // ---- the run's edges -------------------------------------------------------------------

    /// <summary>
    /// Gives the user's own clipboard back. Called once at the end of a run, whatever the
    /// run did. Returns the console note to print, or null when the clipboard was never
    /// borrowed — every other outcome, the failures included, says something: a clipboard
    /// silently left holding `_=nil PD2(2000,2020)` is the thing the README promises will
    /// not happen.
    /// </summary>
    public string? RestoreClipboard()
    {
        switch (_saved)
        {
            case SavedClipboard.NotTouched:
                return null;

            case SavedClipboard.NonText:
                return DumpConsoleText.ClipboardImageLost;

            case SavedClipboard.Text when _savedClipboardText is not null:
                if (TrySetClipboardText(_savedClipboardText))
                {
                    _log.Info("The user's clipboard text was put back.");
                    return DumpConsoleText.ClipboardRestored;
                }
                _log.Warn("The user's clipboard text could not be put back.");
                return DumpConsoleText.ClipboardNotPutBack;

            // Nothing of this run's ever reached the clipboard, so it is exactly as the
            // user left it and there is nothing to say.
            case SavedClipboard.Empty or SavedClipboard.Unreadable when !_clipboardWritten:
                return null;

            case SavedClipboard.Empty:
                // It held nothing when the run borrowed it, so there is nothing to put
                // back — but a console line is on it now, and leaving that there
                // unmentioned is exactly what the promise rules out.
                if (TryEmptyClipboard())
                {
                    _log.Info("The clipboard was emptied: it held nothing before the run.");
                    return DumpConsoleText.ClipboardCleared;
                }
                _log.Warn("The clipboard could not be emptied after the run.");
                return DumpConsoleText.ClipboardNotPutBack;

            default:
                // Unreadable, and a line of ours did land on it: what was there before is
                // unknown, so it is NOT emptied — that would throw away something this run
                // never managed to read. The user is told instead.
                _log.Warn("The clipboard could not be read before the run, so it cannot be put back.");
                return DumpConsoleText.ClipboardNotPutBack;
        }
    }

    // ---- delivery ----------------------------------------------------------------------------

    /// <summary>
    /// The focus contract of §3.6 rules 2 and 3: in front before, or one re-raise; nothing
    /// sent when that fails; in front after, or the line is treated as possibly half-typed.
    /// </summary>
    private KeySendResult Deliver(string what, Input[] keys)
    {
        var reraised = false;
        if (!IsGameInFront())
        {
            reraised = GameWindow.BringToFront(_hWnd, _log);
            if (!reraised || !IsGameInFront())
            {
                _log.Warn($"{what} was not sent: Age of Empires IV is not the foreground window.");
                return KeySendResult.NotSent("the game was not in front and one re-raise did not get it back");
            }
        }

        var sent = SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>());
        if (sent != keys.Length)
        {
            var error = Marshal.GetLastWin32Error();
            _log.Warn($"SendInput accepted {sent} of {keys.Length} events for {what} (error {error}).");
            return KeySendResult.Failed($"SendInput accepted {sent} of {keys.Length} events (error {error})");
        }

        if (!IsGameInFront())
        {
            _log.Warn($"The foreground changed while {what} was being sent.");
            return KeySendResult.LostAfter("the game had lost the foreground by the end of the sequence");
        }
        return KeySendResult.Ok(reraised);
    }

    private static Input Down(ushort scan) => Key(scan, KeyEventScanCode);
    private static Input Up(ushort scan) => Key(scan, KeyEventScanCode | KeyEventKeyUp);

    private static Input Key(ushort scan, uint flags) => new()
    {
        Type = InputKeyboard,
        Union = new InputUnion
        {
            Keyboard = new KeyboardInput { VirtualKey = 0, ScanCode = scan, Flags = flags, Time = 0, ExtraInfo = IntPtr.Zero },
        },
    };

    /// <summary>The whole line as KEYEVENTF_UNICODE events: the no-clipboard path, one SendInput call.</summary>
    private static Input[] Unicode(string text)
    {
        var keys = new List<Input>(text.Length * 2);
        foreach (var c in text)
        {
            keys.Add(Key(c, KeyEventUnicode));
            keys.Add(Key(c, KeyEventUnicode | KeyEventKeyUp));
        }
        return keys.ToArray();
    }

    // ---- the clipboard ------------------------------------------------------------------------

    private void SaveClipboardOnce()
    {
        if (_saved != SavedClipboard.NotTouched) return;

        if (!TryOpenClipboard())
        {
            // Not "empty": unknown. Nothing will be emptied at the end on the strength of
            // a look that never happened.
            _saved = SavedClipboard.Unreadable;
            _log.Warn("The clipboard could not be opened to save what it held.");
            return;
        }
        _saved = SavedClipboard.Empty;   // until the read below says otherwise
        try
        {
            if (IsClipboardFormatAvailable(CfUnicodeText) && ReadUnicodeText() is { } text)
            {
                _savedClipboardText = text;
                _saved = SavedClipboard.Text;
                _log.Info($"The user's clipboard text was saved ({text.Length} characters) and will be put back.");
            }
            else if (EnumClipboardFormats(0) != 0)
            {
                // Something that is not text: an image, a file list, a spreadsheet's own
                // format. It cannot be put back, and the console says so rather than
                // pretending the clipboard is untouched.
                _saved = SavedClipboard.NonText;
                _log.Warn("The clipboard holds something that is not text; it cannot be put back after the dump.");
            }
        }
        finally { CloseClipboard(); }
    }

    private static string? ReadUnicodeText()
    {
        var handle = GetClipboardData(CfUnicodeText);
        if (handle == IntPtr.Zero) return null;
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(pointer); }
        finally { GlobalUnlock(handle); }
    }

    private bool TrySetClipboardText(string text)
    {
        if (!TryOpenClipboard()) return false;
        var memory = IntPtr.Zero;
        try
        {
            var bytes = (UIntPtr)((text.Length + 1) * 2);
            memory = GlobalAlloc(0x0002 /* GMEM_MOVEABLE */, bytes);
            if (memory == IntPtr.Zero) return false;

            var pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero) return false;
            try { Marshal.Copy((text + "\0").ToCharArray(), 0, pointer, text.Length + 1); }
            finally { GlobalUnlock(memory); }

            if (!EmptyClipboard()) return false;
            if (SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero) return false;

            // The clipboard owns the block now; freeing it would be a double free.
            memory = IntPtr.Zero;
            _clipboardWritten = true;
            return true;
        }
        finally
        {
            if (memory != IntPtr.Zero) GlobalFree(memory);
            CloseClipboard();
        }
    }

    /// <summary>Empties the clipboard, so a console line is not left on it. Used only when there was nothing to put back.</summary>
    private bool TryEmptyClipboard()
    {
        if (!TryOpenClipboard()) return false;
        try { return EmptyClipboard(); }
        finally { CloseClipboard(); }
    }

    /// <summary>
    /// Opens the clipboard, retrying: another process can hold it for a moment. The owner
    /// is this exe's console window, never NULL — EmptyClipboard() on a NULL-owned
    /// clipboard leaves the owner NULL and makes the following SetClipboardData fail,
    /// which would take the user's text away and put nothing back.
    /// </summary>
    private bool TryOpenClipboard()
    {
        for (var attempt = 0; attempt < ClipboardTries; attempt++)
        {
            if (OpenClipboard(_clipboardOwner)) return true;
            Thread.Sleep(ClipboardRetryMs);
        }
        return false;
    }
}
