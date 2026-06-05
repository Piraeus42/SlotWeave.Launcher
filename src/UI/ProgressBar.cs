namespace SlotWeave.Launcher;

/// <summary>
/// Simple console progress bar rendering.
/// </summary>
public static class ProgressBar
{
    private const int BarWidth = 30;
    private const char FilledChar = '█';
    private const char EmptyChar = '░';
    private static readonly char[] IndeterminateChars = { '⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏' };

    /// <summary>
    /// Draw a determinate progress bar directly to console.
    /// </summary>
    public static void Draw(long current, long total)
    {
        Console.Write(Build(current, total));
    }

    /// <summary>
    /// Draw an indeterminate (spinning) progress indicator directly to console.
    /// </summary>
    public static void DrawIndeterminate(int position)
    {
        Console.Write(BuildIndeterminate(position));
    }

    /// <summary>
    /// Build a determinate progress bar string.
    /// </summary>
    public static string Build(long current, long total)
    {
        if (total <= 0)
            return BuildIndeterminate((int)(current % 100));

        var percentage = (double)current / total;
        var filledWidth = (int)(BarWidth * percentage);
        var emptyWidth = BarWidth - filledWidth;

        return $"[{new string(FilledChar, filledWidth)}{new string(EmptyChar, emptyWidth)}] {percentage,3:0}%";
    }

    /// <summary>
    /// Build an indeterminate (spinning) progress indicator string.
    /// </summary>
    public static string BuildIndeterminate(int position)
    {
        var c = IndeterminateChars[position % IndeterminateChars.Length];
        return $"[{c}] ...";
    }
}
