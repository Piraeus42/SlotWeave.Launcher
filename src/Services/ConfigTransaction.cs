using System.Text.Json;
using SlotWeave.Launcher.Models;

namespace SlotWeave.Launcher.Services;

/// <summary>
/// Two-phase config transaction.
///
/// Creates deep copies of the original config — _modified is the working copy.
/// Commit writes _modified to disk. Rollback writes _original back to disk.
/// Abort discards changes without touching the file.
///
/// Usage:
///   var tx = new ConfigTransaction(configPath, config);
///   tx.Update(c => c.LauncherVersion = "1.0.3");
///   // ... on success:
///   tx.Commit();
///   // ... on failure that needs file restore:
///   tx.Rollback();
///   // ... on cancel:
///   tx.Abort();
/// </summary>
public class ConfigTransaction
{
    private readonly string _path;
    private readonly LauncherConfig _original;   // snapshot for rollback
    private readonly LauncherConfig _modified;   // working copy
    private bool _committed;

    public ConfigTransaction(string path, LauncherConfig config)
    {
        _path = path;

        // Deep-copy: serialize once, deserialize twice
        // (fixes the reference-copy bug from the original proposal)
        var json = JsonSerializer.Serialize(config);
        _original = JsonSerializer.Deserialize<LauncherConfig>(json)!;
        _modified = JsonSerializer.Deserialize<LauncherConfig>(json)!;
    }

    /// <summary>
    /// Modify the working copy within this transaction.
    /// Throws if the transaction has already been finalized.
    /// </summary>
    public void Update(Action<LauncherConfig> modifier)
    {
        if (_committed)
            throw new InvalidOperationException("Transaction already finalized — cannot modify");

        modifier(_modified);
    }

    /// <summary>
    /// Commit: write _modified to the config file.
    /// After commit the transaction is finalized and cannot be modified further.
    /// </summary>
    public Result Commit()
    {
        if (_committed)
            return Result.Failure("Transaction already finalized");

        try
        {
            var json = JsonSerializer.Serialize(_modified, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(_path, json);
            _committed = true;

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Config save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Rollback: write _original back to the config file.
    /// Used when an operation fails and config must revert to its previous state.
    /// After rollback the transaction is finalized.
    /// </summary>
    public Result Rollback()
    {
        if (_committed)
            return Result.Failure("Transaction already finalized — cannot rollback");

        try
        {
            var json = JsonSerializer.Serialize(_original, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(_path, json);
            _committed = true;

            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Config rollback failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Abort: discard all changes without writing to disk.
    /// After abort the transaction is finalized.
    /// </summary>
    public void Abort()
    {
        _committed = true;
    }
}
