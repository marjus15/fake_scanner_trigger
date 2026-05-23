using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

// ── Fake Barcode Scanner Trigger ────────────────────────────────────────────
// Simulates a hardware barcode scanner by injecting keystrokes via SendInput.
// PharmaBuddy's BarcodeScannerListener captures them through its global hook.
// Each character is sent within ~5 ms — well below the 40 ms threshold.
// ────────────────────────────────────────────────────────────────────────────

Console.Title = "Fake Scanner Trigger";
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("╔══════════════════════════════════════╗");
Console.WriteLine("║      Fake Barcode Scanner Trigger    ║");
Console.WriteLine("╚══════════════════════════════════════╝");
Console.ResetColor();
Console.WriteLine();
Console.WriteLine("Type a barcode and press Enter to inject it into PharmaBuddy.");
Console.WriteLine("Press Ctrl+C to exit.");
Console.WriteLine();

while (true)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.Write("Barcode > ");
    Console.ResetColor();

    var input = Console.ReadLine()?.Trim();

    if (string.IsNullOrWhiteSpace(input))
        continue;

    Log($"Barcode entered: \"{input}\" ({input.Length} chars)");

    if (input.Length != 13)
        Log($"WARNING: length is {input.Length}, PharmaBuddy expects exactly 13 digits.", ConsoleColor.Yellow);

    foreach (var ch in input)
    {
        var vk = BarcodeInjector.CharToVkPublic(ch);
        if (vk == 0)
            Log($"  WARNING: character '{ch}' (0x{(int)ch:X2}) has no VK mapping — will be skipped.", ConsoleColor.Yellow);
    }

    // Give the user a moment to switch focus away from this console window
    // so PharmaBuddy can receive the injected keystrokes.
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"  Injecting in 1 second — switch focus away from this window...");
    Console.ResetColor();
    Thread.Sleep(1000);

    Log("Starting injection...");
    var sw = Stopwatch.StartNew();
    BarcodeInjector.Send(input, verbose: true);
    sw.Stop();
    Log($"Injection complete in {sw.ElapsedMilliseconds} ms total.", ConsoleColor.Green);

    // The injected keystrokes (including ENTER) can land in our own console input
    // buffer if focus drifts back. Drain anything pending so the next ReadLine
    // doesn't immediately pick them up and loop forever.
    Thread.Sleep(100);
    while (Console.KeyAvailable)
        Console.ReadKey(intercept: true);

    Console.WriteLine();
}

static void Log(string message, ConsoleColor color = ConsoleColor.Gray)
{
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.Write($"[{DateTime.Now:HH:mm:ss.fff}] ");
    Console.ForegroundColor = color;
    Console.WriteLine(message);
    Console.ResetColor();
}

// ── Keystroke injector ───────────────────────────────────────────────────────

static class BarcodeInjector
{
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const ushort VK_RETURN = 0x0D;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public KEYBDINPUT ki;
        private long _padding;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>
    /// Sends each character of <paramref name="barcode"/> as rapid VK keystrokes
    /// followed by VK_RETURN.  Inter-keystroke delay is ~5 ms (well under the
    /// 40 ms threshold used by BarcodeScannerListener).
    /// </summary>
    public static void Send(string barcode, bool verbose = false)
    {
        var sw = Stopwatch.StartNew();
        long prevTick = 0;

        foreach (var ch in barcode)
        {
            var vk = CharToVk(ch);
            if (vk == 0)
            {
                if (verbose) LogInjection($"  SKIP '{ch}' — no VK mapping", ConsoleColor.Yellow);
                continue;
            }

            var sent = PressKey(vk);
            var now = sw.ElapsedMilliseconds;
            var delta = prevTick == 0 ? 0 : now - prevTick;
            prevTick = now;

            if (verbose)
                LogInjection($"  KEY '{ch}' VK=0x{vk:X2}  sent={sent}  t={now}ms  Δ={delta}ms",
                    sent > 0 ? ConsoleColor.Green : ConsoleColor.Red);

            Thread.Sleep(5);
        }

        // Send Enter
        var enterSent = PressKey(VK_RETURN);
        var enterDelta = sw.ElapsedMilliseconds - prevTick;
        if (verbose)
            LogInjection($"  ENTER VK=0x0D  sent={enterSent}  Δ={enterDelta}ms from last char",
                enterSent > 0 ? ConsoleColor.Green : ConsoleColor.Red);
    }

    private static uint PressKey(ushort vk)
    {
        var inputs = new INPUT[]
        {
            new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = 0 }
            },
            new INPUT
            {
                type = INPUT_KEYBOARD,
                ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = KEYEVENTF_KEYUP }
            }
        };

        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static ushort CharToVkPublic(char ch) => CharToVk(ch);

    /// <summary>
    /// Maps a barcode character to the VK code that
    /// BarcodeScannerListener.TranslateBarcodeCharacter expects.
    /// Digits → VK_0..VK_9, letters → VK_A..VK_Z (upper-case only).
    /// </summary>
    private static ushort CharToVk(char ch)
    {
        if (ch >= '0' && ch <= '9')
            return (ushort)(0x30 + (ch - '0'));   // VK_0 = 0x30

        var upper = char.ToUpperInvariant(ch);
        if (upper >= 'A' && upper <= 'Z')
            return (ushort)(0x41 + (upper - 'A')); // VK_A = 0x41

        return 0; // unsupported character — skip
    }

    private static void LogInjection(string message, ConsoleColor color)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"[{DateTime.Now:HH:mm:ss.fff}] ");
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ResetColor();
    }
}
