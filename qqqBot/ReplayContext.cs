namespace qqqBot;

/// <summary>
/// Per-replay-run context that replaces the static fields formerly on ProgramRefactored.
/// Each parallel replay pipeline gets its own ReplayContext instance, eliminating
/// all shared mutable static state.
/// 
/// For single-replay mode (--mode=replay), this is registered as a singleton in DI.
/// For parallel-replay mode, each pipeline constructs its own instance.
/// </summary>
public sealed class ReplayContext
{
    /// <summary>
    /// The configuration file used for this replay run.
    /// </summary>
    public string ConfigFileName { get; init; } = "appsettings.json";

    /// <summary>
    /// Whether this run is in replay mode (vs live).
    /// </summary>
    public bool IsReplayMode { get; init; }

    /// <summary>
    /// The parsed command-line overrides for this run.
    /// </summary>
    public CommandLineOverrides? Overrides { get; init; }

    /// <summary>
    /// The FileLoggerProvider instance for this run.
    /// Used to set per-tick clock override without static state.
    /// Null when logging is not file-based (e.g., during tests).
    /// </summary>
    public FileLoggerProvider? FileLogger { get; set; }
}
