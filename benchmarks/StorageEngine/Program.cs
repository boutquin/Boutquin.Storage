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
/// Entry point for the benchmarking program.
/// </summary>
public sealed class Program
{
    /// <summary>
    /// Main method that sets up and runs the benchmarks.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    public static void Main(string[] args)
    {
        try
        {
            // List of benchmark types
            var benchmarks = new List<Type>
            {
                typeof(InMemoryKeyValueStoreBenchmark),
                typeof(AppendOnlyFileStorageEngineBenchmark),
                typeof(AppendOnlyFileStorageEngineWithIndexBenchmark),
                typeof(BulkKeyValueStoreWithBloomFilterBenchmark)

                // Add other benchmark types here
            };

            // Create a temporary config to discover the benchmarks
            var discoveryConfig = ManualConfig.CreateEmpty()
                .AddLogger(ConsoleLogger.Default)
                .AddValidator(ExecutionValidator.FailOnError)
                .AddExporter(MarkdownExporter.Default)
                .AddJob(Job.Dry); // Use a dry job to discover benchmarks without running them

            // Discover all benchmarks
            var totalBenchmarkCases = benchmarks.Sum(benchmark => BenchmarkConverter.TypeToBenchmarks(benchmark, discoveryConfig).BenchmarksCases.Length);

            // Custom logger for tracking progress
            var customLogger = new CustomLogger(totalBenchmarkCases);

            // Dictionary to store benchmark results
            var results = new Dictionary<string, List<Summary>>();

            // Run benchmarks for each type
            foreach (var benchmark in benchmarks)
            {
                customLogger.ResetHeader(); // Reset the header before each benchmark run
                // Run the benchmark and capture the summary
                var summary = BenchmarkRunner.Run(benchmark, CreateCustomConfig(customLogger));
                if (summary != null)
                {
                    if (!results.ContainsKey(benchmark.Name))
                    {
                        results[benchmark.Name] = new List<Summary>();
                    }
                    results[benchmark.Name].Add(summary);
                }
            }

            // Stop title update timer
            customLogger.StopTitleUpdate();

            // Display the results
            DisplayResults(results, customLogger);
        }
        catch (Exception ex)
        {
            LogExceptionToFile(ex);
        }
    }

    /// <summary>
    /// Creates a custom BenchmarkDotNet configuration.
    /// </summary>
    /// <param name="logger">The custom logger to use for progress tracking.</param>
    /// <returns>A configured IConfig instance.</returns>
    private static IConfig CreateCustomConfig(ILogger logger)
    {
        // Get the solution directory
        var solutionDirectory = Directory.GetParent(Directory.GetCurrentDirectory()).Parent.Parent.FullName;
        var artifactsPath = Path.Combine(solutionDirectory, "BenchmarkDotNet.Artifacts");

        return ManualConfig.CreateEmpty()
            .WithOptions(ConfigOptions.DisableOptimizationsValidator)
            .AddLogger(logger) // Use only the custom logger
            .AddColumnProvider(DefaultColumnProviders.Instance)
            .AddExporter(MarkdownExporter.Default)
            .AddJob(Job.Default
                .WithIterationCount(5) // Reduce the number of iterations
                .WithInvocationCount(16) // Set InvocationCount to 16, a multiple of UnrollFactor
                .WithWarmupCount(1) // Reduce the number of warm-up iterations
                .WithRuntime(CoreRuntime.Core80))
            .WithOrderer(new DefaultOrderer(SummaryOrderPolicy.FastestToSlowest))
            .WithArtifactsPath(artifactsPath);
    }

    /// <summary>
    /// Displays the benchmark results.
    /// </summary>
    /// <param name="results">The dictionary containing the benchmark results.</param>
    /// <param name="logger">The custom logger to use for displaying the results.</param>
    private static void DisplayResults(Dictionary<string, List<Summary>> results, ILogger logger)
    {
        foreach (var implementation in results)
        {
            logger.WriteLine(LogKind.Default, $"Results for {implementation.Key}:");

            var groupedReports = implementation.Value
                .SelectMany(summary => summary.Reports)
                .GroupBy(report => report.BenchmarkCase.Descriptor.WorkloadMethod.Name);

            foreach (var group in groupedReports)
            {
                logger.WriteLine(LogKind.Default, $"  {group.Key}:");
                foreach (var report in group)
                {
                    var benchmarkCase = report.BenchmarkCase;
                    var parameters = benchmarkCase.Parameters;
                    var metrics = report.ResultStatistics;

                    logger.WriteLine(LogKind.Default, $"    Parameters: {parameters.DisplayInfo}");
                    logger.WriteLine(LogKind.Default, $"      Mean: {metrics.Mean} ms");
                    logger.WriteLine(LogKind.Default, $"      Error: {metrics.StandardError} ms");
                    logger.WriteLine(LogKind.Default, $"      StdDev: {metrics.StandardDeviation} ms");
                }
                logger.WriteLine();
            }
        }
    }

    /// <summary>
    /// Logs the exception details to a file.
    /// </summary>
    /// <param name="ex">The exception to log.</param>
    private static void LogExceptionToFile(Exception ex)
    {
        var logFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "error.log");
        using (var writer = new StreamWriter(logFilePath, true))
        {
            writer.WriteLine($"[{DateTime.Now}] Exception: {ex.Message}");
            writer.WriteLine(ex.StackTrace);
            if (ex.InnerException != null)
            {
                writer.WriteLine($"Inner Exception: {ex.InnerException.Message}");
                writer.WriteLine(ex.InnerException.StackTrace);
            }
            writer.WriteLine();
        }
    }
}