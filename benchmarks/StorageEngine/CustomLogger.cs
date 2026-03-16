// Copyright (c) 2024-2026 Pierre G. Boutquin. All rights reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License").
//  You may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//
//  See the License for the specific language governing permissions and
//  limitations under the License.
//
namespace Boutquin.Storage.BenchMark;

/// <summary>
/// Custom logger for tracking and displaying the progress of benchmark runs.
/// Tracks progress at both the class level (which benchmark suite) and the case level
/// by parsing BDN's "// Benchmark:" marker which is emitted exactly once per case.
/// </summary>
public sealed class CustomLogger : ILogger
{
    private readonly int _totalClasses;
    private readonly int _totalCases;
    private int _currentClass;
    private int _completedCases;
    private string _currentClassName = "";
    private readonly DateTime _startTime;
    private readonly Timer _titleUpdateTimer;

    // Track per-case timing for better ETA
    private DateTime _lastCaseStart;
    private readonly List<double> _caseElapsedSeconds = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="CustomLogger"/> class.
    /// </summary>
    /// <param name="totalClasses">The total number of benchmark classes to be run.</param>
    /// <param name="totalCases">The total number of benchmark cases across all classes.</param>
    public CustomLogger(int totalClasses, int totalCases)
    {
        _totalClasses = totalClasses;
        _totalCases = totalCases;
        _startTime = DateTime.Now;
        _lastCaseStart = _startTime;
        _titleUpdateTimer = new Timer(1000);
        _titleUpdateTimer.Elapsed += UpdateConsoleTitle;
        _titleUpdateTimer.Start();
    }

    /// <summary>
    /// Gets the unique identifier for this logger.
    /// </summary>
    public string Id => nameof(CustomLogger);

    /// <summary>
    /// Gets the priority of this logger.
    /// </summary>
    public int Priority => 0;

    /// <summary>
    /// Writes a specified text to the console, detecting BDN case-start markers.
    /// </summary>
    /// <param name="logKind">The kind of log entry.</param>
    /// <param name="text">The text to write.</param>
    public void Write(LogKind logKind, string text)
    {
        DetectCaseStart(text);
        SetConsoleColor(logKind);
        Console.Write(text);
        Console.ResetColor();
    }

    /// <summary>
    /// Writes a new line to the console.
    /// </summary>
    public void WriteLine()
    {
        Console.WriteLine();
    }

    /// <summary>
    /// Writes a specified text followed by a new line to the console, detecting BDN case-start markers.
    /// </summary>
    /// <param name="logKind">The kind of log entry.</param>
    /// <param name="text">The text to write.</param>
    public void WriteLine(LogKind logKind, string text)
    {
        DetectCaseStart(text);
        SetConsoleColor(logKind);
        Console.WriteLine(text);
        Console.ResetColor();
    }

    /// <summary>
    /// Flushes the logger.
    /// </summary>
    public void Flush()
    {
    }

    /// <summary>
    /// Signals the start of a new benchmark class. Called from the runner loop.
    /// </summary>
    /// <param name="className">The name of the benchmark class about to run.</param>
    public void StartClass(string className)
    {
        _currentClass++;
        _currentClassName = className;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n========================================");
        Console.WriteLine($"  [{_currentClass}/{_totalClasses}] {className}");
        Console.WriteLine($"  Overall progress: {_completedCases}/{_totalCases} cases completed");
        Console.WriteLine($"========================================");
        Console.ResetColor();
    }

    /// <summary>
    /// Stops the timer for updating the console title.
    /// </summary>
    public void StopTitleUpdate()
    {
        _titleUpdateTimer.Stop();
        Console.Title = $"Benchmarks complete — {_completedCases} cases in {DateTime.Now - _startTime:hh\\:mm\\:ss}";
    }

    /// <summary>
    /// Detects BDN's "// Benchmark:" marker which is emitted exactly once at the start of each case.
    /// Uses this to track accurate case-level progress.
    /// </summary>
    private void DetectCaseStart(string text)
    {
        if (!text.StartsWith("// Benchmark:", StringComparison.Ordinal))
        {
            return;
        }

        // Record timing for the previous case (if any)
        if (_completedCases > 0)
        {
            _caseElapsedSeconds.Add((DateTime.Now - _lastCaseStart).TotalSeconds);
        }

        _completedCases++;
        _lastCaseStart = DateTime.Now;
    }

    /// <summary>
    /// Updates the console title with class-level and case-level progress plus ETA.
    /// ETA uses a weighted recent average (last 5 cases) for better accuracy when
    /// benchmark costs vary widely (fast in-memory vs slow fsync).
    /// </summary>
    private void UpdateConsoleTitle(object? sender, ElapsedEventArgs e)
    {
        var remaining = _totalCases - _completedCases;
        var etaText = "calculating...";

        if (_caseElapsedSeconds.Count > 0)
        {
            // Use the average of the last 5 cases for a responsive ETA
            var recentCount = Math.Min(5, _caseElapsedSeconds.Count);
            var recentAvg = _caseElapsedSeconds
                .Skip(_caseElapsedSeconds.Count - recentCount)
                .Average();
            var eta = TimeSpan.FromSeconds(recentAvg * remaining);
            etaText = $"ETA: {eta:hh\\:mm\\:ss}";
        }

        var elapsed = DateTime.Now - _startTime;
        Console.Title = $"[{_currentClass}/{_totalClasses}] {_currentClassName} | Case {_completedCases}/{_totalCases} | {etaText} | Elapsed: {elapsed:hh\\:mm\\:ss}";
    }

    /// <summary>
    /// Sets the console color based on the log kind.
    /// </summary>
    private static void SetConsoleColor(LogKind logKind)
    {
        Console.ForegroundColor = logKind switch
        {
            LogKind.Header => ConsoleColor.Green,
            LogKind.Help => ConsoleColor.Cyan,
            LogKind.Result => ConsoleColor.Yellow,
            LogKind.Statistic => ConsoleColor.Blue,
            LogKind.Error => ConsoleColor.Red,
            LogKind.Hint => ConsoleColor.Magenta,
            _ => ConsoleColor.White,
        };
    }
}
