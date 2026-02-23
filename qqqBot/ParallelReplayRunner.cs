using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace qqqBot;

/// <summary>
/// Runs multiple replay pipelines concurrently using SemaphoreSlim for throttling.
/// Collects structured ReplayResult objects and outputs a summary table + optional CSV.
/// </summary>
internal sealed class ParallelReplayRunner
{
    private readonly ILogger _logger;
    private readonly ReplayPipelineFactory _factory;

    public ParallelReplayRunner(ILogger logger, ReplayPipelineFactory? factory = null)
    {
        _logger = logger;
        _factory = factory ?? new ReplayPipelineFactory();
    }

    /// <summary>
    /// Runs all specs in parallel (up to maxParallelism at a time) and returns results.
    /// </summary>
    public async Task<List<ReplayResult>> RunAllAsync(
        IReadOnlyList<ReplayRunSpec> specs,
        int maxParallelism,
        string? csvOutputPath = null,
        CancellationToken ct = default)
    {
        if (specs.Count == 0)
        {
            _logger.LogWarning("[PARALLEL] No replay specs to run.");
            return [];
        }

        var effectiveParallelism = Math.Min(maxParallelism, specs.Count);
        _logger.LogInformation("[PARALLEL] Starting {Count} replay(s) with parallelism={Parallelism}",
            specs.Count, effectiveParallelism);

        var sw = Stopwatch.StartNew();
        var semaphore = new SemaphoreSlim(effectiveParallelism, effectiveParallelism);
        var results = new ReplayResult[specs.Count];
        var completed = 0;

        var tasks = specs.Select(async (spec, index) =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var result = await _factory.RunReplayAsync(spec, ct);
                results[index] = result;
                var done = Interlocked.Increment(ref completed);
                _logger.LogInformation("[PARALLEL] [{Done}/{Total}] {Date} seed={Seed}: P/L=${PnL:N2} ({Ret:P2}) in {Ms}ms",
                    done, specs.Count, spec.Date, spec.Seed,
                    result.RealizedPnL, result.NetReturnFraction,
                    result.WallClockDuration.TotalMilliseconds);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        await Task.WhenAll(tasks);
        sw.Stop();

        var resultList = results.ToList();

        // Print summary
        PrintSummaryTable(resultList, sw.Elapsed);

        // Write CSV if requested
        if (!string.IsNullOrEmpty(csvOutputPath))
        {
            WriteCsv(resultList, csvOutputPath);
            _logger.LogInformation("[PARALLEL] Results written to {Path}", csvOutputPath);
        }

        return resultList;
    }

    private void PrintSummaryTable(List<ReplayResult> results, TimeSpan totalElapsed)
    {
        var successful = results.Where(r => r.Success).ToList();
        var failed = results.Where(r => !r.Success).ToList();

        _logger.LogInformation("");
        _logger.LogInformation("[PARALLEL] =====================================================");
        _logger.LogInformation("[PARALLEL]  P A R A L L E L   R E P L A Y   S U M M A R Y");
        _logger.LogInformation("[PARALLEL] =====================================================");
        _logger.LogInformation("[PARALLEL]  Total Runs:     {Count} ({Success} succeeded, {Failed} failed)",
            results.Count, successful.Count, failed.Count);
        _logger.LogInformation("[PARALLEL]  Wall Clock:     {Elapsed:mm\\:ss\\.fff}", totalElapsed);

        if (successful.Count > 0)
        {
            var pnls = successful.Select(r => r.RealizedPnL).OrderBy(x => x).ToList();
            var returns = successful.Select(r => r.NetReturnFraction).ToList();

            var mean = pnls.Average();
            var median = pnls[pnls.Count / 2];
            var stdDev = pnls.Count > 1
                ? (decimal)Math.Sqrt((double)pnls.Sum(x => (x - mean) * (x - mean)) / (pnls.Count - 1))
                : 0m;
            var winRate = (decimal)pnls.Count(x => x > 0) / pnls.Count;
            var min = pnls.First();
            var max = pnls.Last();

            _logger.LogInformation("[PARALLEL]  ──────────────────────────────────────────────────");
            _logger.LogInformation("[PARALLEL]  Mean P/L:       ${Mean:N2}", mean);
            _logger.LogInformation("[PARALLEL]  Median P/L:     ${Median:N2}", median);
            _logger.LogInformation("[PARALLEL]  Std Dev:        ${StdDev:N2}", stdDev);
            _logger.LogInformation("[PARALLEL]  Min P/L:        ${Min:N2}", min);
            _logger.LogInformation("[PARALLEL]  Max P/L:        ${Max:N2}", max);
            _logger.LogInformation("[PARALLEL]  Win Rate:       {WinRate:P1}", winRate);
            _logger.LogInformation("[PARALLEL]  Mean Return:    {Return:P2}", returns.Average());
            _logger.LogInformation("[PARALLEL]  ──────────────────────────────────────────────────");

            // Per-date breakdown if multiple dates
            var byDate = successful.GroupBy(r => r.ReplayDate).OrderBy(g => g.Key).ToList();
            if (byDate.Count > 1)
            {
                _logger.LogInformation("[PARALLEL]  Per-Date Breakdown:");
                foreach (var group in byDate)
                {
                    var datePnls = group.Select(r => r.RealizedPnL).ToList();
                    var dateMean = datePnls.Average();
                    var dateWins = (decimal)datePnls.Count(x => x > 0) / datePnls.Count;
                    _logger.LogInformation("[PARALLEL]    {Date}: mean=${Mean:N2} win={Win:P0} (n={N})",
                        group.Key, dateMean, dateWins, datePnls.Count);
                }
            }
        }

        if (failed.Count > 0)
        {
            _logger.LogWarning("[PARALLEL]  Failed runs:");
            foreach (var f in failed)
            {
                _logger.LogWarning("[PARALLEL]    {Date} seed={Seed}: {Error}",
                    f.ReplayDate, f.RngSeed, f.ErrorMessage);
            }
        }

        _logger.LogInformation("[PARALLEL] =====================================================");
    }

    private static void WriteCsv(List<ReplayResult> results, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        sb.AppendLine(ReplayResult.CsvHeader);
        foreach (var r in results.OrderBy(r => r.ReplayDate).ThenBy(r => r.RngSeed))
        {
            sb.AppendLine(r.ToCsvLine());
        }
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }
}
