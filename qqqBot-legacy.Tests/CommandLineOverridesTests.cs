namespace qqqBot.Tests;

/// <summary>
/// Tests for the CommandLineOverrides parsing — specifically the new replay/data flags:
///   --mode=replay, --fetch-history, --date=, --speed=, --symbols=
/// </summary>
public class CommandLineOverridesTests
{
    // ───────────────────────── --mode= ─────────────────────────

    /// <summary>
    /// --mode=replay sets Mode to "replay".
    /// </summary>
    [Fact]
    public void Mode_Replay_ParsesCorrectly()
    {
        var result = CommandLineOverrides.Parse(new[] { "--mode=replay" });

        Assert.NotNull(result);
        Assert.Equal("replay", result!.Mode);
    }

    /// <summary>
    /// --mode=REPLAY is case-insensitive (normalized to lowercase).
    /// </summary>
    [Fact]
    public void Mode_CaseInsensitive_NormalizedToLower()
    {
        var result = CommandLineOverrides.Parse(new[] { "--mode=REPLAY" });

        Assert.NotNull(result);
        Assert.Equal("replay", result!.Mode);
    }

    /// <summary>
    /// Mode is null by default when --mode is not specified.
    /// </summary>
    [Fact]
    public void Mode_NotSpecified_IsNull()
    {
        var result = CommandLineOverrides.Parse(Array.Empty<string>());

        Assert.NotNull(result);
        Assert.Null(result!.Mode);
    }

    // ───────────────────────── --fetch-history ─────────────────────────

    /// <summary>
    /// --fetch-history sets the flag to true.
    /// </summary>
    [Fact]
    public void FetchHistory_Flag_SetsTrue()
    {
        var result = CommandLineOverrides.Parse(new[] { "--fetch-history" });

        Assert.NotNull(result);
        Assert.True(result!.FetchHistory);
    }

    /// <summary>
    /// FetchHistory is false by default.
    /// </summary>
    [Fact]
    public void FetchHistory_NotSpecified_IsFalse()
    {
        var result = CommandLineOverrides.Parse(Array.Empty<string>());

        Assert.NotNull(result);
        Assert.False(result!.FetchHistory);
    }

    // ───────────────────────── --date= ─────────────────────────

    /// <summary>
    /// --date=2026-02-06 stores the date string.
    /// </summary>
    [Fact]
    public void Date_ParsesDateString()
    {
        var result = CommandLineOverrides.Parse(new[] { "--date=2026-02-06" });

        Assert.NotNull(result);
        Assert.Equal("2026-02-06", result!.ReplayDate);
    }

    /// <summary>
    /// ReplayDate is null by default.
    /// </summary>
    [Fact]
    public void Date_NotSpecified_IsNull()
    {
        var result = CommandLineOverrides.Parse(Array.Empty<string>());

        Assert.NotNull(result);
        Assert.Null(result!.ReplayDate);
    }

    // ───────────────────────── --speed= ─────────────────────────

    /// <summary>
    /// --speed=10 sets the multiplier.
    /// </summary>
    [Fact]
    public void Speed_ParsesMultiplier()
    {
        var result = CommandLineOverrides.Parse(new[] { "--speed=10" });

        Assert.NotNull(result);
        Assert.Equal(10.0, result!.ReplaySpeed);
    }

    /// <summary>
    /// --speed=0 is valid (instant replay).
    /// </summary>
    [Fact]
    public void Speed_Zero_IsValid()
    {
        var result = CommandLineOverrides.Parse(new[] { "--speed=0" });

        Assert.NotNull(result);
        Assert.Equal(0.0, result!.ReplaySpeed);
    }

    /// <summary>
    /// --speed=1.5 supports decimal multipliers.
    /// </summary>
    [Fact]
    public void Speed_Decimal_ParsesCorrectly()
    {
        var result = CommandLineOverrides.Parse(new[] { "--speed=1.5" });

        Assert.NotNull(result);
        Assert.Equal(1.5, result!.ReplaySpeed);
    }

    /// <summary>
    /// Default speed is 10.0x when not specified.
    /// </summary>
    [Fact]
    public void Speed_NotSpecified_DefaultsTo10()
    {
        var result = CommandLineOverrides.Parse(Array.Empty<string>());

        Assert.NotNull(result);
        Assert.Equal(10.0, result!.ReplaySpeed);
    }

    /// <summary>
    /// --speed=-1 (negative) returns null (parse failure).
    /// </summary>
    [Fact]
    public void Speed_Negative_ReturnsNull()
    {
        var result = CommandLineOverrides.Parse(new[] { "--speed=-1" });

        Assert.Null(result);
    }

    /// <summary>
    /// --speed=abc (non-numeric) returns null (parse failure).
    /// </summary>
    [Fact]
    public void Speed_NonNumeric_ReturnsNull()
    {
        var result = CommandLineOverrides.Parse(new[] { "--speed=abc" });

        Assert.Null(result);
    }

    // ───────────────────────── --symbols= ─────────────────────────

    /// <summary>
    /// --symbols=QQQ,TQQQ,SQQQ stores the comma-separated string.
    /// </summary>
    [Fact]
    public void Symbols_ParsesCommaSeparatedList()
    {
        var result = CommandLineOverrides.Parse(new[] { "--symbols=QQQ,TQQQ,SQQQ" });

        Assert.NotNull(result);
        Assert.Equal("QQQ,TQQQ,SQQQ", result!.SymbolsOverride);
    }

    /// <summary>
    /// SymbolsOverride is null by default.
    /// </summary>
    [Fact]
    public void Symbols_NotSpecified_IsNull()
    {
        var result = CommandLineOverrides.Parse(Array.Empty<string>());

        Assert.NotNull(result);
        Assert.Null(result!.SymbolsOverride);
    }

    // ───────────────────────── Combined Args ─────────────────────────

    /// <summary>
    /// "Full Replay Command" — all replay flags parsed together.
    /// Simulates: dotnet run -- --mode=replay --date=2026-02-06 --speed=20 --symbols=QQQ,TQQQ
    /// </summary>
    [Fact]
    public void FullReplayCommand_AllFlagsParsed()
    {
        var args = new[]
        {
            "--mode=replay",
            "--date=2026-02-06",
            "--speed=20",
            "--symbols=QQQ,TQQQ",
        };

        var result = CommandLineOverrides.Parse(args);

        Assert.NotNull(result);
        Assert.Equal("replay", result!.Mode);
        Assert.Equal("2026-02-06", result.ReplayDate);
        Assert.Equal(20.0, result.ReplaySpeed);
        Assert.Equal("QQQ,TQQQ", result.SymbolsOverride);
    }

    /// <summary>
    /// "Fetch History Command" — fetch-history with date and symbols.
    /// Simulates: dotnet run -- --fetch-history --date=2026-02-06 --symbols=QQQ,TQQQ,SQQQ
    /// </summary>
    [Fact]
    public void FetchHistoryCommand_AllFlagsParsed()
    {
        var args = new[]
        {
            "--fetch-history",
            "--date=2026-02-06",
            "--symbols=QQQ,TQQQ,SQQQ",
        };

        var result = CommandLineOverrides.Parse(args);

        Assert.NotNull(result);
        Assert.True(result!.FetchHistory);
        Assert.Equal("2026-02-06", result.ReplayDate);
        Assert.Equal("QQQ,TQQQ,SQQQ", result.SymbolsOverride);
    }

    /// <summary>
    /// "Mixed Legacy + New" — old-style flags coexist with new replay flags.
    /// </summary>
    [Fact]
    public void MixedLegacyAndNewFlags_AllParsed()
    {
        var args = new[]
        {
            "-bull=TQQQ",
            "-bear=SQQQ",
            "--mode=replay",
            "--speed=5",
        };

        var result = CommandLineOverrides.Parse(args);

        Assert.NotNull(result);
        Assert.Equal("TQQQ", result!.BullTicker);
        Assert.Equal("SQQQ", result.BearTicker);
        Assert.Equal("replay", result.Mode);
        Assert.Equal(5.0, result.ReplaySpeed);
    }
}
