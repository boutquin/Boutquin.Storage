// Copyright (c) 2024 Pierre G. Boutquin. All rights reserved.
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
/// Custom logger for tracking and displaying the progress of benchmark runs with color support.
/// </summary>
public sealed class CustomLogger : ILogger
{
    private readonly int _totalBenchmarks;
    private int _currentBenchmark;
    private bool _hasWrittenHeader;
    private readonly DateTime _startTime;
    private readonly Timer _titleUpdateTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="CustomLogger"/> class.
    /// </summary>
    /// <param name="totalBenchmarks">The total number of benchmarks to be run.</param>
    public CustomLogger(int totalBenchmarks)
    {
        _totalBenchmarks = totalBenchmarks;
        _currentBenchmark = 0;
        _hasWrittenHeader = false;
        _startTime = DateTime.Now;
        _titleUpdateTimer = new Timer(1000); // Update the title every second
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
    /// Writes a specified text to the console.
    /// </summary>
    /// <param name="logKind">The kind of log entry.</param>
    /// <param name="text">The text to write.</param>
    public void Write(LogKind logKind, string text)
    {
        SetConsoleColor(logKind);
        if (logKind == LogKind.Header && !_hasWrittenHeader)
        {
            _currentBenchmark++;
            Console.WriteLine($"Running benchmark {_currentBenchmark} of {_totalBenchmarks}...");
            _hasWrittenHeader = true;
        }
        Console.Write(text);
        ResetConsoleColor();
    }

    /// <summary>
    /// Writes a new line to the console.
    /// </summary>
    public void WriteLine()
    {
        Console.WriteLine();
    }

    /// <summary>
    /// Writes a specified text followed by a new line to the console.
    /// </summary>
    /// <param name="logKind">The kind of log entry.</param>
    /// <param name="text">The text to write.</param>
    public void WriteLine(LogKind logKind, string text)
    {
        SetConsoleColor(logKind);
        if (logKind == LogKind.Header && !_hasWrittenHeader)
        {
            _currentBenchmark++;
            Console.WriteLine($"Running benchmark {_currentBenchmark} of {_totalBenchmarks}...");
            _hasWrittenHeader = true;
        }
        Console.WriteLine(text);
        ResetConsoleColor();
    }

    /// <summary>
    /// Flushes the logger, ensuring any buffered messages are written out.
    /// </summary>
    public void Flush()
    {
        // No operation needed for console logger
    }

    /// <summary>
    /// Sets the console color based on the log kind.
    /// </summary>
    /// <param name="logKind">The kind of log entry.</param>
    private void SetConsoleColor(LogKind logKind)
    {
        switch (logKind)
        {
            case LogKind.Default:
                Console.ForegroundColor = ConsoleColor.White;
                break;
            case LogKind.Header:
                Console.ForegroundColor = ConsoleColor.Green;
                break;
            case LogKind.Help:
                Console.ForegroundColor = ConsoleColor.Cyan;
                break;
            case LogKind.Result:
                Console.ForegroundColor = ConsoleColor.Yellow;
                break;
            case LogKind.Statistic:
                Console.ForegroundColor = ConsoleColor.Blue;
                break;
            case LogKind.Info:
                Console.ForegroundColor = ConsoleColor.White;
                break;
            case LogKind.Error:
                Console.ForegroundColor = ConsoleColor.Red;
                break;
            case LogKind.Hint:
                Console.ForegroundColor = ConsoleColor.Magenta;
                break;
            default:
                Console.ResetColor();
                break;
        }
    }

    /// <summary>
    /// Resets the console color to the default.
    /// </summary>
    private void ResetConsoleColor()
    {
        Console.ResetColor();
    }

    /// <summary>
    /// Resets the header written flag, called before each benchmark run.
    /// </summary>
    public void ResetHeader()
    {
        _hasWrittenHeader = false;
    }

    /// <summary>
    /// Updates the console title with the current benchmark progress and estimated time remaining.
    /// </summary>
    private void UpdateConsoleTitle(object sender, ElapsedEventArgs e)
    {
        var elapsed = DateTime.Now - _startTime;
        var averageTimePerBenchmark = elapsed.TotalSeconds / Math.Max(_currentBenchmark, 1);
        var remainingTime = TimeSpan.FromSeconds(averageTimePerBenchmark * (_totalBenchmarks - _currentBenchmark));

        Console.Title = $"Running benchmark {_currentBenchmark} of {_totalBenchmarks}... Estimated time remaining: {remainingTime:hh\\:mm\\:ss}";
    }

    /// <summary>
    /// Stops the timer for updating the console title.
    /// </summary>
    public void StopTitleUpdate()
    {
        _titleUpdateTimer.Stop();
    }
}