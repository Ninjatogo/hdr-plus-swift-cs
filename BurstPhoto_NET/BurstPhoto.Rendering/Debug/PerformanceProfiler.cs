using System.Diagnostics;

namespace BurstPhoto.Rendering.Debug;

/// <summary>
/// Lightweight performance profiler for measuring and reporting pipeline stage timings.
/// When enabled, outputs timing information in a format parseable by profiling scripts.
/// </summary>
public class PerformanceProfiler
{
    private readonly Dictionary<string, List<long>> _stageTimes = new();
    private readonly Dictionary<string, Stopwatch> _activeStopwatches = new();
    private readonly Stopwatch _totalTimer = new();
    private readonly object _lock = new();

    /// <summary>
    /// Enable/disable profiling output. When disabled, methods are near-zero cost.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Start timing a named stage. Call EndStage with the same name to record the time.
    /// </summary>
    public void BeginStage(string stageName)
    {
        if (!Enabled) return;

        lock (_lock)
        {
            if (!_activeStopwatches.ContainsKey(stageName))
            {
                _activeStopwatches[stageName] = new Stopwatch();
            }
            _activeStopwatches[stageName].Restart();
        }
    }

    /// <summary>
    /// End timing a stage and record the elapsed time.
    /// </summary>
    public void EndStage(string stageName)
    {
        if (!Enabled) return;

        lock (_lock)
        {
            if (_activeStopwatches.TryGetValue(stageName, out var sw))
            {
                sw.Stop();
                if (!_stageTimes.ContainsKey(stageName))
                {
                    _stageTimes[stageName] = new List<long>();
                }
                _stageTimes[stageName].Add(sw.ElapsedMilliseconds);
            }
        }
    }

    /// <summary>
    /// Measure a stage using a disposable scope. Usage: using (profiler.MeasureStage("Name")) { ... }
    /// </summary>
    public IDisposable MeasureStage(string stageName)
    {
        if (!Enabled) return NullDisposable.Instance;
        return new StageScope(this, stageName);
    }

    /// <summary>
    /// Start the total processing timer.
    /// </summary>
    public void StartTotal()
    {
        if (!Enabled) return;
        _totalTimer.Restart();
    }

    /// <summary>
    /// Stop the total processing timer.
    /// </summary>
    public void StopTotal()
    {
        if (!Enabled) return;
        _totalTimer.Stop();
    }

    /// <summary>
    /// Get the total elapsed time in milliseconds.
    /// </summary>
    public long TotalElapsedMs => _totalTimer.ElapsedMilliseconds;

    /// <summary>
    /// Clear all recorded timings.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _stageTimes.Clear();
            _activeStopwatches.Clear();
            _totalTimer.Reset();
        }
    }

    /// <summary>
    /// Print all recorded timings in a format parseable by profiling scripts.
    /// Format: [PERF] StageName: XXXms
    /// </summary>
    public void PrintResults()
    {
        if (!Enabled) return;

        Console.WriteLine();
        Console.WriteLine("[PERF] ========== Performance Profile ==========");
        Console.WriteLine($"[PERF] Total: {_totalTimer.ElapsedMilliseconds}ms");

        lock (_lock)
        {
            // Sort stages by total time descending
            var sortedStages = _stageTimes
                .Select(kvp => new
                {
                    Name = kvp.Key,
                    TotalMs = kvp.Value.Sum(),
                    Count = kvp.Value.Count,
                    AvgMs = kvp.Value.Count > 0 ? kvp.Value.Average() : 0
                })
                .OrderByDescending(x => x.TotalMs)
                .ToList();

            foreach (var stage in sortedStages)
            {
                if (stage.Count > 1)
                {
                    Console.WriteLine($"[PERF] {stage.Name}: {stage.TotalMs}ms (x{stage.Count}, avg={stage.AvgMs:F1}ms)");
                }
                else
                {
                    Console.WriteLine($"[PERF] {stage.Name}: {stage.TotalMs}ms");
                }
            }

            // Calculate unaccounted time
            var accountedTime = sortedStages.Sum(s => s.TotalMs);
            var unaccounted = _totalTimer.ElapsedMilliseconds - accountedTime;
            if (unaccounted > 0)
            {
                Console.WriteLine($"[PERF] Unaccounted: {unaccounted}ms");
            }
        }

        Console.WriteLine("[PERF] =============================================");
        Console.WriteLine();
    }

    /// <summary>
    /// Get a summary dictionary of stage timings (total ms per stage).
    /// </summary>
    public Dictionary<string, long> GetStageTotals()
    {
        lock (_lock)
        {
            return _stageTimes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Sum());
        }
    }

    private class StageScope : IDisposable
    {
        private readonly PerformanceProfiler _profiler;
        private readonly string _stageName;

        public StageScope(PerformanceProfiler profiler, string stageName)
        {
            _profiler = profiler;
            _stageName = stageName;
            _profiler.BeginStage(_stageName);
        }

        public void Dispose()
        {
            _profiler.EndStage(_stageName);
        }
    }

    private class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        public void Dispose() { }
    }
}
