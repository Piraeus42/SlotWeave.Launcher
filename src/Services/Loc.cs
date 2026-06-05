namespace SlotWeave.Launcher.Services;

/// <summary>
/// Shorthand accessor for the localization service singleton.
/// Usage: Loc.T("key") or Loc.T("key", arg1, arg2).
/// </summary>
public static class Loc
{
    public static string T(string key) => LocalizationService.Instance.T(key);
    public static string T(string key, params object[] args) => LocalizationService.Instance.T(key, args);
}
