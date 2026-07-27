using SpreadTrader.Core.Brokers;

namespace LiveSpreadCalculator.Brokers;

/// <summary>
/// Persists an operator's choice of broker platform made from the dashboard (see
/// BrokerSwitchState/BrokerPlatformController) to a small text file. The switch itself takes
/// effect immediately, in-process, with no restart involved - this file exists purely so a LATER,
/// unrelated process restart (a redeploy, a crash, a reboot) also comes back up on whichever
/// platform was last switched to, instead of reverting to whatever Broker:Platform/
/// Broker__Platform says in static config. Read once at startup (BrokerRegistration) to seed
/// BrokerSwitchState's initial value; written every time SwitchTo actually changes the active
/// platform.
///
/// A plain text file (just "Kite" or "Dhan") rather than JSON: there is exactly one value to
/// store, and BrokerPlatform already round-trips through Enum.Parse cleanly.
/// </summary>
public static class BrokerPlatformOverrideStore
{
    /// <summary>Best-effort read: a missing file (nothing ever switched from the UI - the common
    /// case) or one that fails to parse both mean "no override", never a startup failure - the app
    /// falls back to whatever Broker:Platform config already says, exactly as it did before this
    /// existed.</summary>
    public static BrokerPlatform? TryRead(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var raw = File.ReadAllText(path).Trim();
            return Enum.TryParse<BrokerPlatform>(raw, ignoreCase: true, out var platform) ? platform : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not read the broker platform override at {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Atomic temp-file-then-move, same pattern as every other on-disk store in this app
    /// (CsvUserStore, DhanInstrumentMasterService's scrip master cache) - a process killed
    /// mid-write must never leave a half-written file for the next startup's TryRead to trip on.
    /// </summary>
    public static void Write(string path, BrokerPlatform platform)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, platform.ToString());
        File.Move(tempPath, path, overwrite: true);
    }
}
