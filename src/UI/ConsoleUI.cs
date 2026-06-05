using SlotWeave.Launcher.Services;

namespace SlotWeave.Launcher;

/// <summary>
/// Console UI rendering utilities. Used globally by all components.
/// Every page transition clears and redraws the persistent banner at top.
/// </summary>
public static class ConsoleUI
{
    private static readonly object _lock = new();
    private static string _bannerVersion = "1.0.0";

    public static void SetVersion(string version) => _bannerVersion = version;

    /// <summary>
    /// Clear the console screen. Safe for redirected output.
    /// </summary>
    public static void ClearScreen()
    {
        try { Console.Clear(); }
        catch (System.IO.IOException) { /* redirected */ }
    }

    /// <summary>
    /// Clear and draw the persistent banner at the top of every page.
    /// </summary>
    public static void DrawBanner()
    {
        lock (_lock)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(@"╔═══════════════════════════════════════════════════════════════╗");
            Console.WriteLine($@"║              SlotWeave Launcher v{_bannerVersion,-30}║");
            Console.WriteLine(@"║              Created by Piraeus                               ║");
            Console.WriteLine(@"╚═══════════════════════════════════════════════════════════════╝");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Start a new page: clear, draw banner, then section title.
    /// </summary>
    public static void ShowHeader(string title)
    {
        lock (_lock)
        {
            ClearScreen();
            DrawBanner();
            Console.WriteLine();
            Console.WriteLine(new string('═', 64));
            Console.WriteLine($"  {title}");
            Console.WriteLine(new string('═', 64));
        }
    }

    /// <summary>
    /// Start a page with just the banner (for main menu).
    /// </summary>
    public static void ShowPage()
    {
        lock (_lock)
        {
            ClearScreen();
            DrawBanner();
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Full banner + clear for first launch. Deprecated in favor of ShowPage/ShowHeader.
    /// </summary>
    public static void ShowBanner(string version = "1.0.0")
    {
        _bannerVersion = version;
        ShowPage();
    }

    public static void ShowSuccess(string message)
    {
        lock (_lock)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  ✓ {message}");
            Console.ResetColor();
        }
    }

    public static void ShowError(string message)
    {
        lock (_lock)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  ✗ {message}");
            Console.ResetColor();
        }
    }

    public static void ShowWarning(string message)
    {
        lock (_lock)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ⚠ {message}");
            Console.ResetColor();
        }
    }

    public static void ShowInfo(string message)
    {
        lock (_lock)
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"  ℹ {message}");
            Console.ResetColor();
        }
    }

    public static void ShowStatus(string label, string value, bool isWarning = false)
    {
        lock (_lock)
        {
            Console.Write($"  {label}: ");
            if (isWarning)
                Console.ForegroundColor = ConsoleColor.Yellow;
            else
                Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(value);
            Console.ResetColor();
        }
    }

    public static void Separator()
    {
        lock (_lock)
        {
            Console.WriteLine(new string('─', 64));
        }
    }

    /// <summary>
    /// Read a key from console input, safely handling redirected input.
    /// </summary>
    public static ConsoleKeyInfo? TryReadKey(bool intercept = true)
    {
        try { return Console.ReadKey(intercept); }
        catch (InvalidOperationException) { return null; }
    }

    public static void WaitForKey()
    {
        var message = Loc.T("info.wait_return");
        lock (_lock)
        {
            Console.WriteLine();
            Console.Write(message);
            try { Console.ReadKey(intercept: true); }
            catch (InvalidOperationException) { /* redirected input */ }
            Console.WriteLine();
        }
    }
}
