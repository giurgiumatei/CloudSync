using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using System.Threading;

namespace CloudSync.PerformanceTests;

public class CustomPerformanceTest
{
    private static readonly string ApiBaseUrl = Environment.GetEnvironmentVariable("API_BASE_URL") ?? "http://localhost:5000";
    private static readonly HttpClient HttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    
    // Enhanced test configuration for 1 to 1,000,000 RPS
    private readonly int[] _requestRates = GenerateRequestRates();
    private readonly TimeSpan _testDuration = TimeSpan.FromSeconds(60); // Extended duration for more accurate results
    private readonly TimeSpan _warmupDuration = TimeSpan.FromSeconds(10); // Warmup period
    private readonly string _resultsDirectory;
    private readonly string _logFilePath;
    private readonly string _roundReportPath;
    private readonly string _summaryReportPath;
    private int _currentRound = 0;
    
    // Performance monitoring
    private readonly Timer _resourceMonitor;
    private readonly ConcurrentBag<ResourceUsage> _resourceUsages = new();

    public CustomPerformanceTest()
    {
        // Check if we're running in a container with a mounted volume at /app/results
        _resultsDirectory = Directory.Exists("/app/results") ? "/app/results" : Directory.GetCurrentDirectory();
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _logFilePath = Path.Combine(_resultsDirectory, $"performance_results_{timestamp}.csv");
        _roundReportPath = Path.Combine(_resultsDirectory, $"round_reports_{timestamp}");
        _summaryReportPath = Path.Combine(_resultsDirectory, $"summary_report_{timestamp}.html");
        
        // Create round reports directory
        Directory.CreateDirectory(_roundReportPath);
        
        // Setup resource monitoring
        _resourceMonitor = new Timer(MonitorResources, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        
        // Configure HttpClient for high throughput
        HttpClient.DefaultRequestHeaders.ConnectionClose = false;
    }

    private static int[] GenerateRequestRates()
    {
        var rates = new List<int>();
        
        // Low range: 1-10 RPS
        rates.AddRange(new[] { 1, 2, 5, 10 });
        
        // Medium range: 10-1000 RPS
        for (int i = 20; i <= 100; i += 20) rates.Add(i);
        for (int i = 200; i <= 1000; i += 200) rates.Add(i);
        
        // High range: 1K-10K RPS
        for (int i = 2000; i <= 10000; i += 2000) rates.Add(i);
        
        // Very high range: 10K-100K RPS
        for (int i = 20000; i <= 100000; i += 20000) rates.Add(i);
        
        // Extreme range: 100K-1M RPS
        for (int i = 200000; i <= 1000000; i += 200000) rates.Add(i);
        
        return rates.ToArray();
    }

    public async Task RunTests()
    {
        Console.WriteLine($"Starting comprehensive performance tests...");
        Console.WriteLine($"Test range: 1 to 1,000,000 requests per second");
        Console.WriteLine($"Test duration per round: {_testDuration.TotalSeconds}s");
        Console.WriteLine($"Warmup duration: {_warmupDuration.TotalSeconds}s");
        Console.WriteLine($"Results directory: {_resultsDirectory}");
        
        // Initialize CSV log file
        InitializeLogFile();
        
        // Generate HTML summary report header
        await InitializeSummaryReport();
        
        var overallStopwatch = Stopwatch.StartNew();
        var allResults = new List<(int Rate, TestResults Results)>();
        
        // Warmup
        await PerformWarmup();
        
        foreach (var requestsPerSecond in _requestRates)
        {
            _currentRound++;
            Console.WriteLine($"\n=== ROUND {_currentRound} ===");
            Console.WriteLine($"Testing {requestsPerSecond:N0} requests per second...");
            
            try
            {
                var results = await RunTestAtRate(requestsPerSecond);
                allResults.Add((requestsPerSecond, results));
                
                // Log results to CSV
                LogResults(requestsPerSecond, results);
                
                // Generate individual round report
                await GenerateRoundReport(_currentRound, requestsPerSecond, results);
                
                // Update summary report
                await UpdateSummaryReport(requestsPerSecond, results);
                
                // Break between tests for system recovery
                if (requestsPerSecond >= 10000)
                {
                    Console.WriteLine($"Cooling down for 30 seconds...");
                    await Task.Delay(TimeSpan.FromSeconds(30));
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
                
                // Early termination if error rate is too high
                if (results.ErrorRate > 0.5 && requestsPerSecond > 1000)
                {
                    Console.WriteLine($"High error rate detected ({results.ErrorRate:P2}). Consider stopping or adjusting system capacity.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during round {_currentRound}: {ex.Message}");
                await LogError(_currentRound, requestsPerSecond, ex);
            }
        }
        
        overallStopwatch.Stop();
        
        // Generate final comprehensive report
        await GenerateFinalReport(allResults, overallStopwatch.Elapsed);
        
        Console.WriteLine($"\n=== TESTING COMPLETE ===");
        Console.WriteLine($"Total test duration: {overallStopwatch.Elapsed:hh\\:mm\\:ss}");
        Console.WriteLine($"Results saved to: {_resultsDirectory}");
    }
    
    private async Task PerformWarmup()
    {
        Console.WriteLine("\n=== WARMUP PHASE ===");
        Console.WriteLine("Warming up the system with light load...");
        
        var warmupTasks = new List<Task>();
        var warmupStopwatch = Stopwatch.StartNew();
        
        while (warmupStopwatch.Elapsed < _warmupDuration)
        {
            warmupTasks.Add(SendSimpleRequest());
            await Task.Delay(100); // 10 RPS during warmup
            
            // Limit concurrent warmup requests
            if (warmupTasks.Count >= 50)
            {
                await Task.WhenAll(warmupTasks);
                warmupTasks.Clear();
            }
        }
        
        if (warmupTasks.Any())
            await Task.WhenAll(warmupTasks);
        
        Console.WriteLine("Warmup completed.");
    }
    
    private async Task SendSimpleRequest()
    {
        try
        {
            var content = new StringContent("\"warmup data\"", Encoding.UTF8, "application/json");
            await HttpClient.PostAsync($"{ApiBaseUrl}/api/datasync", content);
        }
        catch
        {
            // Ignore warmup errors
        }
    }
    
    private async Task<TestResults> RunTestAtRate(int requestsPerSecond)
    {
        // Clear previous resource usage data
        _resourceUsages.Clear();
        
        // Calculate optimal concurrency and batching based on request rate
        var (concurrency, batchSize, delayBetweenBatches) = CalculateOptimalConcurrency(requestsPerSecond);
        
        Console.WriteLine($"Using concurrency: {concurrency}, batch size: {batchSize}");
        
        var results = new ConcurrentBag<RequestResult>();
        var stopwatch = Stopwatch.StartNew();
        var totalRequestsToSend = (int)(_testDuration.TotalSeconds * requestsPerSecond);
        var requestsSent = 0;
        var semaphore = new SemaphoreSlim(concurrency);
        
        var tasks = new List<Task>();
        
        while (stopwatch.Elapsed < _testDuration && requestsSent < totalRequestsToSend)
        {
            var batchTasks = new List<Task>();
            
            // Create a batch of requests
            for (int i = 0; i < batchSize && requestsSent < totalRequestsToSend && stopwatch.Elapsed < _testDuration; i++)
            {
                requestsSent++;
                batchTasks.Add(SendControlledRequest(results, semaphore));
            }
            
            tasks.AddRange(batchTasks);
            
            // Progress reporting
            if (requestsSent % Math.Max(1, totalRequestsToSend / 20) == 0)
            {
                var percentage = (requestsSent * 100) / totalRequestsToSend;
                Console.WriteLine($"Progress: {percentage}% - Sent {requestsSent:N0}/{totalRequestsToSend:N0} requests - Current RPS: {requestsSent / Math.Max(1, stopwatch.Elapsed.TotalSeconds):F0}");
            }
            
            // Apply delay between batches to control rate
            if (delayBetweenBatches > TimeSpan.Zero)
                await Task.Delay(delayBetweenBatches);
        }
        
        // Wait for all requests to complete
        Console.WriteLine("Waiting for all requests to complete...");
        await Task.WhenAll(tasks);
        stopwatch.Stop();
        
        // Calculate and return results
        var testResults = CalculateResults(results, stopwatch.Elapsed);
        testResults.ActualRPS = requestsSent / stopwatch.Elapsed.TotalSeconds;
        testResults.ResourceUsages = _resourceUsages.ToArray();
        
        return testResults;
    }
    
    private (int concurrency, int batchSize, TimeSpan delayBetweenBatches) CalculateOptimalConcurrency(int requestsPerSecond)
    {
        int concurrency;
        int batchSize;
        TimeSpan delayBetweenBatches;
        
        if (requestsPerSecond <= 10)
        {
            concurrency = Math.Min(requestsPerSecond, 5);
            batchSize = 1;
            delayBetweenBatches = TimeSpan.FromMilliseconds(1000.0 / requestsPerSecond);
        }
        else if (requestsPerSecond <= 100)
        {
            concurrency = Math.Min(requestsPerSecond, 20);
            batchSize = Math.Min(5, requestsPerSecond / 10);
            delayBetweenBatches = TimeSpan.FromMilliseconds(100);
        }
        else if (requestsPerSecond <= 1000)
        {
            concurrency = Math.Min(requestsPerSecond / 2, 100);
            batchSize = 10;
            delayBetweenBatches = TimeSpan.FromMilliseconds(10);
        }
        else if (requestsPerSecond <= 10000)
        {
            concurrency = Math.Min(requestsPerSecond / 5, 500);
            batchSize = 50;
            delayBetweenBatches = TimeSpan.FromMilliseconds(5);
        }
        else
        {
            concurrency = Math.Min(requestsPerSecond / 10, 1000);
            batchSize = 100;
            delayBetweenBatches = TimeSpan.FromMilliseconds(1);
        }
        
        return (concurrency, batchSize, delayBetweenBatches);
    }
    
    private async Task SendControlledRequest(ConcurrentBag<RequestResult> results, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();
        try
        {
            var requestResult = new RequestResult();
            var stopwatch = Stopwatch.StartNew();
            
            try
            {
                var testData = GenerateTestData();
                var jsonString = JsonSerializer.Serialize(testData);
                var content = new StringContent(jsonString, Encoding.UTF8, "application/json");
                
                var response = await HttpClient.PostAsync($"{ApiBaseUrl}/api/datasync", content);
                stopwatch.Stop();
                
                requestResult.ResponseTimeMs = stopwatch.ElapsedMilliseconds;
                requestResult.IsSuccess = response.IsSuccessStatusCode;
                requestResult.StatusCode = (int)response.StatusCode;
                
                if (!response.IsSuccessStatusCode)
                {
                    requestResult.ErrorMessage = await response.Content.ReadAsStringAsync();
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                requestResult.ResponseTimeMs = stopwatch.ElapsedMilliseconds;
                requestResult.IsSuccess = false;
                requestResult.ErrorMessage = ex.Message;
                requestResult.StatusCode = 0; // Indicates network/timeout error
            }
            
            results.Add(requestResult);
        }
        finally
        {
            semaphore.Release();
        }
    }
    
    private string GenerateTestData()
    {
        // Generate varied test data to simulate real-world scenarios
        var random = new Random();
        var dataSize = random.Next(100, 1000); // Variable data size
        return new string('A', dataSize) + DateTime.UtcNow.Ticks;
    }
    
    private void MonitorResources(object state)
    {
        try
        {
            var process = Process.GetCurrentProcess();
            _resourceUsages.Add(new ResourceUsage
            {
                Timestamp = DateTime.UtcNow,
                CpuTimeMs = process.TotalProcessorTime.TotalMilliseconds,
                WorkingSetMB = process.WorkingSet64 / (1024 * 1024),
                ThreadCount = process.Threads.Count
            });
        }
        catch
        {
            // Ignore monitoring errors
        }
    }
    
    private TestResults CalculateResults(ConcurrentBag<RequestResult> requestResults, TimeSpan actualDuration)
    {
        var resultsArray = requestResults.ToArray();
        
        var results = new TestResults
        {
            TotalRequests = resultsArray.Length,
            SuccessCount = resultsArray.Count(r => r.IsSuccess),
            ResponseTimes = resultsArray.Select(r => r.ResponseTimeMs).ToArray(),
            ActualTestDuration = actualDuration,
            StatusCodeDistribution = resultsArray
                .GroupBy(r => r.StatusCode)
                .ToDictionary(g => g.Key, g => g.Count())
        };
        
        // Calculate error metrics
        results.ErrorCount = results.TotalRequests - results.SuccessCount;
        results.SuccessRate = results.TotalRequests > 0 ? (double)results.SuccessCount / results.TotalRequests : 0;
        results.ErrorRate = 1 - results.SuccessRate;
        
        // Calculate response time statistics
        if (results.ResponseTimes.Any())
        {
            results.AvgResponseTimeMs = results.ResponseTimes.Average();
            results.MinResponseTimeMs = results.ResponseTimes.Min();
            results.MaxResponseTimeMs = results.ResponseTimes.Max();
            results.MedianResponseTimeMs = CalculatePercentile(results.ResponseTimes.OrderBy(t => t).ToArray(), 50);
            
            // Calculate percentiles
            var sortedTimes = results.ResponseTimes.OrderBy(t => t).ToArray();
            results.P50ResponseTimeMs = CalculatePercentile(sortedTimes, 50);
            results.P90ResponseTimeMs = CalculatePercentile(sortedTimes, 90);
            results.P95ResponseTimeMs = CalculatePercentile(sortedTimes, 95);
            results.P99ResponseTimeMs = CalculatePercentile(sortedTimes, 99);
            
            // Calculate throughput
            results.ThroughputRPS = results.TotalRequests / actualDuration.TotalSeconds;
        }
        
        return results;
    }
    
    private long CalculatePercentile(long[] sortedData, int percentile)
    {
        if (sortedData.Length == 0) return 0;
        
        double index = (percentile / 100.0) * (sortedData.Length - 1);
        int lowerIndex = (int)Math.Floor(index);
        int upperIndex = (int)Math.Ceiling(index);
        
        if (lowerIndex == upperIndex)
            return sortedData[lowerIndex];
        
        double weight = index - lowerIndex;
        return (long)(sortedData[lowerIndex] * (1 - weight) + sortedData[upperIndex] * weight);
    }
    
    private void InitializeLogFile()
    {
        var headers = "Round,RequestRate,Timestamp,ActualRPS,TotalRequests,SuccessCount,ErrorCount,SuccessRate,ErrorRate," +
                     "AvgResponseTime,MinResponseTime,MaxResponseTime,MedianResponseTime," +
                     "P50ResponseTime,P90ResponseTime,P95ResponseTime,P99ResponseTime,ThroughputRPS," +
                     "TestDurationSeconds,StatusCode200,StatusCode400,StatusCode500,OtherStatusCodes\n";
        
        File.WriteAllText(_logFilePath, headers);
    }
    
    private async Task InitializeSummaryReport()
    {
        var html = @"<!DOCTYPE html>
<html>
<head>
    <title>CloudSync Performance Test Report</title>
    <style>
        body { font-family: Arial, sans-serif; margin: 20px; }
        .header { background-color: #f0f8ff; padding: 20px; border-radius: 5px; margin-bottom: 20px; }
        .round { background-color: #f9f9f9; padding: 15px; margin: 10px 0; border-radius: 5px; border-left: 4px solid #007acc; }
        .metrics { display: grid; grid-template-columns: repeat(auto-fit, minmax(200px, 1fr)); gap: 10px; margin: 10px 0; }
        .metric { background-color: white; padding: 10px; border: 1px solid #ddd; border-radius: 3px; }
        .success { color: #28a745; }
        .warning { color: #ffc107; }
        .error { color: #dc3545; }
        .chart-container { margin: 20px 0; }
    </style>
    <script src=""https://cdn.jsdelivr.net/npm/chart.js""></script>
</head>
<body>
    <div class=""header"">
        <h1>CloudSync Performance Test Report</h1>
        <p>Test started: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + @"</p>
        <p>API Endpoint: " + ApiBaseUrl + @"/api/datasync</p>
        <p>Test Range: 1 to 1,000,000 requests per second</p>
    </div>
    <div id=""results"">
";
        
        await File.WriteAllTextAsync(_summaryReportPath, html);
    }
    
    private async Task GenerateRoundReport(int round, int requestRate, TestResults results)
    {
        var reportPath = Path.Combine(_roundReportPath, $"round_{round:D3}_{requestRate}rps.json");
        
        var roundData = new
        {
            Round = round,
            RequestRate = requestRate,
            Timestamp = DateTime.Now,
            Results = new
            {
                results.TotalRequests,
                results.SuccessCount,
                results.ErrorCount,
                results.SuccessRate,
                results.ErrorRate,
                results.ActualRPS,
                results.AvgResponseTimeMs,
                results.MinResponseTimeMs,
                results.MaxResponseTimeMs,
                results.MedianResponseTimeMs,
                results.P50ResponseTimeMs,
                results.P90ResponseTimeMs,
                results.P95ResponseTimeMs,
                results.P99ResponseTimeMs,
                results.ThroughputRPS,
                TestDurationSeconds = results.ActualTestDuration.TotalSeconds,
                results.StatusCodeDistribution
            },
            ResourceUsages = results.ResourceUsages?.Select(r => new
            {
                Timestamp = r.Timestamp,
                CpuTimeMs = r.CpuTimeMs,
                WorkingSetMB = r.WorkingSetMB,
                ThreadCount = r.ThreadCount
            }).ToArray()
        };
        
        var json = JsonSerializer.Serialize(roundData, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(reportPath, json);
        
        Console.WriteLine($"Round {round} report saved: {reportPath}");
    }
    
    private async Task UpdateSummaryReport(int requestRate, TestResults results)
    {
        var statusClass = results.ErrorRate < 0.01 ? "success" : results.ErrorRate < 0.1 ? "warning" : "error";
        
        var roundHtml = $@"
    <div class=""round"">
        <h3>Round {_currentRound}: {requestRate:N0} RPS</h3>
        <div class=""metrics"">
            <div class=""metric"">
                <strong>Success Rate:</strong> 
                <span class=""{statusClass}"">{results.SuccessRate:P2}</span>
            </div>
            <div class=""metric"">
                <strong>Actual RPS:</strong> {results.ActualRPS:N0}
            </div>
            <div class=""metric"">
                <strong>Avg Response Time:</strong> {results.AvgResponseTimeMs:F2}ms
            </div>
            <div class=""metric"">
                <strong>P99 Response Time:</strong> {results.P99ResponseTimeMs}ms
            </div>
            <div class=""metric"">
                <strong>Total Requests:</strong> {results.TotalRequests:N0}
            </div>
            <div class=""metric"">
                <strong>Errors:</strong> {results.ErrorCount:N0}
            </div>
        </div>
        <p><strong>Status Codes:</strong> {string.Join(", ", results.StatusCodeDistribution.Select(kvp => $"{kvp.Key}: {kvp.Value:N0}"))}</p>
    </div>";
        
        await File.AppendAllTextAsync(_summaryReportPath, roundHtml);
    }
    
    private async Task LogError(int round, int requestRate, Exception ex)
    {
        var errorLogPath = Path.Combine(_resultsDirectory, "errors.log");
        var errorEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Round {round} ({requestRate} RPS): {ex.Message}\n{ex.StackTrace}\n\n";
        await File.AppendAllTextAsync(errorLogPath, errorEntry);
        
        // Also log to CSV with error indicator
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var errorLine = $"{round},{requestRate},{timestamp},0,0,0,0,0%,100%,0,0,0,0,0,0,0,0,0,0,0,0,0\n";
        File.AppendAllText(_logFilePath, errorLine);
    }
    
    private async Task GenerateFinalReport(List<(int Rate, TestResults Results)> allResults, TimeSpan totalDuration)
    {
        // Close the HTML report
        var chartScript = GenerateChartScript(allResults);
        var finalHtml = $@"
    </div>
    <div class=""chart-container"">
        <h2>Performance Charts</h2>
        <canvas id=""responseTimeChart"" width=""400"" height=""200""></canvas>
        <canvas id=""throughputChart"" width=""400"" height=""200""></canvas>
        <canvas id=""errorRateChart"" width=""400"" height=""200""></canvas>
    </div>
    <div class=""header"">
        <h2>Test Summary</h2>
        <p><strong>Total Test Duration:</strong> {totalDuration:hh\\:mm\\:ss}</p>
        <p><strong>Total Rounds:</strong> {allResults.Count}</p>
        <p><strong>Max Successful RPS:</strong> {allResults.Where(r => r.Results.ErrorRate < 0.1).DefaultIfEmpty().Max(r => r.Rate):N0}</p>
        <p><strong>Average Success Rate:</strong> {allResults.Average(r => r.Results.SuccessRate):P2}</p>
    </div>
    {chartScript}
</body>
</html>";
        
        await File.AppendAllTextAsync(_summaryReportPath, finalHtml);
        
        // Generate performance analysis
        await GeneratePerformanceAnalysis(allResults);
        
        Console.WriteLine($"Final comprehensive report saved: {_summaryReportPath}");
    }
    
    private string GenerateChartScript(List<(int Rate, TestResults Results)> allResults)
    {
        var rates = string.Join(",", allResults.Select(r => r.Rate));
        var responseTimes = string.Join(",", allResults.Select(r => r.Results.AvgResponseTimeMs));
        var throughputs = string.Join(",", allResults.Select(r => r.Results.ThroughputRPS));
        var errorRates = string.Join(",", allResults.Select(r => r.Results.ErrorRate * 100));
        
        return $@"
    <script>
        // Response Time Chart
        const responseCtx = document.getElementById('responseTimeChart').getContext('2d');
        new Chart(responseCtx, {{
            type: 'line',
            data: {{
                labels: [{rates}],
                datasets: [{{
                    label: 'Average Response Time (ms)',
                    data: [{responseTimes}],
                    borderColor: 'rgb(75, 192, 192)',
                    backgroundColor: 'rgba(75, 192, 192, 0.2)',
                    tension: 0.1
                }}]
            }},
            options: {{
                responsive: true,
                plugins: {{
                    title: {{
                        display: true,
                        text: 'Response Time vs Request Rate'
                    }}
                }},
                scales: {{
                    x: {{
                        type: 'logarithmic',
                        title: {{
                            display: true,
                            text: 'Request Rate (RPS)'
                        }}
                    }},
                    y: {{
                        title: {{
                            display: true,
                            text: 'Response Time (ms)'
                        }}
                    }}
                }}
            }}
        }});
        
        // Throughput Chart
        const throughputCtx = document.getElementById('throughputChart').getContext('2d');
        new Chart(throughputCtx, {{
            type: 'line',
            data: {{
                labels: [{rates}],
                datasets: [{{
                    label: 'Actual Throughput (RPS)',
                    data: [{throughputs}],
                    borderColor: 'rgb(255, 99, 132)',
                    backgroundColor: 'rgba(255, 99, 132, 0.2)',
                    tension: 0.1
                }}]
            }},
            options: {{
                responsive: true,
                plugins: {{
                    title: {{
                        display: true,
                        text: 'Throughput vs Request Rate'
                    }}
                }},
                scales: {{
                    x: {{
                        type: 'logarithmic',
                        title: {{
                            display: true,
                            text: 'Target Request Rate (RPS)'
                        }}
                    }},
                    y: {{
                        title: {{
                            display: true,
                            text: 'Actual Throughput (RPS)'
                        }}
                    }}
                }}
            }}
        }});
        
        // Error Rate Chart
        const errorCtx = document.getElementById('errorRateChart').getContext('2d');
        new Chart(errorCtx, {{
            type: 'line',
            data: {{
                labels: [{rates}],
                datasets: [{{
                    label: 'Error Rate (%)',
                    data: [{errorRates}],
                    borderColor: 'rgb(255, 159, 64)',
                    backgroundColor: 'rgba(255, 159, 64, 0.2)',
                    tension: 0.1
                }}]
            }},
            options: {{
                responsive: true,
                plugins: {{
                    title: {{
                        display: true,
                        text: 'Error Rate vs Request Rate'
                    }}
                }},
                scales: {{
                    x: {{
                        type: 'logarithmic',
                        title: {{
                            display: true,
                            text: 'Request Rate (RPS)'
                        }}
                    }},
                    y: {{
                        title: {{
                            display: true,
                            text: 'Error Rate (%)'
                        }}
                    }}
                }}
            }}
        }});
    </script>";
    }
    
    private async Task GeneratePerformanceAnalysis(List<(int Rate, TestResults Results)> allResults)
    {
        var analysisPath = Path.Combine(_resultsDirectory, "performance_analysis.md");
        
        var analysis = new StringBuilder();
        analysis.AppendLine("# Performance Analysis Report");
        analysis.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        analysis.AppendLine();
        
        // Find performance thresholds
        var maxStableRps = allResults.Where(r => r.Results.ErrorRate < 0.01).DefaultIfEmpty().Max(r => r.Rate);
        var maxUsableRps = allResults.Where(r => r.Results.ErrorRate < 0.1).DefaultIfEmpty().Max(r => r.Rate);
        
        analysis.AppendLine("## Key Findings");
        analysis.AppendLine($"- **Maximum Stable RPS (< 1% errors):** {maxStableRps:N0}");
        analysis.AppendLine($"- **Maximum Usable RPS (< 10% errors):** {maxUsableRps:N0}");
        
        // Response time analysis
        var lowLoadAvgResponse = allResults.Where(r => r.Rate <= 100).Average(r => r.Results.AvgResponseTimeMs);
        var highLoadAvgResponse = allResults.Where(r => r.Rate >= 1000 && r.Results.ErrorRate < 0.5).DefaultIfEmpty().Average(r => r.Results?.AvgResponseTimeMs ?? 0);
        
        analysis.AppendLine($"- **Low Load Avg Response Time:** {lowLoadAvgResponse:F2}ms (≤ 100 RPS)");
        analysis.AppendLine($"- **High Load Avg Response Time:** {highLoadAvgResponse:F2}ms (≥ 1000 RPS)");
        analysis.AppendLine($"- **Response Time Degradation:** {((highLoadAvgResponse - lowLoadAvgResponse) / lowLoadAvgResponse * 100):F1}%");
        
        analysis.AppendLine();
        analysis.AppendLine("## Detailed Results");
        analysis.AppendLine("| RPS | Success Rate | Avg Response | P99 Response | Throughput |");
        analysis.AppendLine("|-----|--------------|--------------|--------------|------------|");
        
        foreach (var (rate, results) in allResults)
        {
            analysis.AppendLine($"| {rate:N0} | {results.SuccessRate:P1} | {results.AvgResponseTimeMs:F1}ms | {results.P99ResponseTimeMs}ms | {results.ThroughputRPS:F0} |");
        }
        
        await File.WriteAllTextAsync(analysisPath, analysis.ToString());
    }
    
    private void LogResults(int requestRate, TestResults results)
    {
        // Get status code counts safely
        var statusCode200 = results.StatusCodeDistribution.GetValueOrDefault(200, 0);
        var statusCode400 = results.StatusCodeDistribution.GetValueOrDefault(400, 0);
        var statusCode500 = results.StatusCodeDistribution.GetValueOrDefault(500, 0);
        var otherStatusCodes = results.StatusCodeDistribution.Where(kvp => kvp.Key != 200 && kvp.Key != 400 && kvp.Key != 500).Sum(kvp => kvp.Value);
        
        // Format: Round,RequestRate,Timestamp,ActualRPS,TotalRequests,SuccessCount,ErrorCount,SuccessRate,ErrorRate,AvgResponseTime,MinResponseTime,MaxResponseTime,MedianResponseTime,P50,P90,P95,P99,ThroughputRPS,TestDurationSeconds,StatusCode200,StatusCode400,StatusCode500,OtherStatusCodes
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var line = $"{_currentRound},{requestRate},{timestamp},{results.ActualRPS:F2},{results.TotalRequests},{results.SuccessCount},{results.ErrorCount}," +
                   $"{results.SuccessRate:F4},{results.ErrorRate:F4},{results.AvgResponseTimeMs:F2}," +
                   $"{results.MinResponseTimeMs},{results.MaxResponseTimeMs},{results.MedianResponseTimeMs}," +
                   $"{results.P50ResponseTimeMs},{results.P90ResponseTimeMs},{results.P95ResponseTimeMs},{results.P99ResponseTimeMs}," +
                   $"{results.ThroughputRPS:F2},{results.ActualTestDuration.TotalSeconds:F2}," +
                   $"{statusCode200},{statusCode400},{statusCode500},{otherStatusCodes}\n";
        
        File.AppendAllText(_logFilePath, line);
        
        // Also write a summary to a separate file for easy access
        var summaryPath = Path.Combine(_resultsDirectory, "latest_results_summary.txt");
        var summary = $"Test Results Summary - {DateTime.Now}\n" +
                      $"=============================\n" +
                      $"Round: {_currentRound}\n" +
                      $"API URL: {ApiBaseUrl}\n" +
                      $"Target Request Rate: {requestRate:N0} requests per second\n" +
                      $"Actual RPS: {results.ActualRPS:F2}\n" +
                      $"Total Requests: {results.TotalRequests:N0}\n" +
                      $"Success Rate: {results.SuccessRate:P2}\n" +
                      $"Error Rate: {results.ErrorRate:P2}\n" +
                      $"Avg Response Time: {results.AvgResponseTimeMs:F2}ms\n" +
                      $"Min Response Time: {results.MinResponseTimeMs}ms\n" +
                      $"Max Response Time: {results.MaxResponseTimeMs}ms\n" +
                      $"Median Response Time: {results.MedianResponseTimeMs}ms\n" +
                      $"P50 Response Time: {results.P50ResponseTimeMs}ms\n" +
                      $"P90 Response Time: {results.P90ResponseTimeMs}ms\n" +
                      $"P95 Response Time: {results.P95ResponseTimeMs}ms\n" +
                      $"P99 Response Time: {results.P99ResponseTimeMs}ms\n" +
                      $"Throughput: {results.ThroughputRPS:F2} RPS\n" +
                      $"Test Duration: {results.ActualTestDuration:hh\\:mm\\:ss}\n" +
                      $"Status Codes: {string.Join(", ", results.StatusCodeDistribution.Select(kvp => $"{kvp.Key}: {kvp.Value:N0}"))}\n\n";
        
        File.AppendAllText(summaryPath, summary);
        
        // Also print to console
        Console.WriteLine($"Test Results for {requestRate:N0} requests per second:");
        Console.WriteLine($"- Total Requests: {results.TotalRequests:N0}");
        Console.WriteLine($"- Success Rate: {results.SuccessRate:P2}");
        Console.WriteLine($"- Error Rate: {results.ErrorRate:P2}");
        Console.WriteLine($"- Actual RPS: {results.ActualRPS:F2}");
        Console.WriteLine($"- Avg Response Time: {results.AvgResponseTimeMs:F2}ms");
        Console.WriteLine($"- Min Response Time: {results.MinResponseTimeMs}ms");
        Console.WriteLine($"- Max Response Time: {results.MaxResponseTimeMs}ms");
        Console.WriteLine($"- Median Response Time: {results.MedianResponseTimeMs}ms");
        Console.WriteLine($"- P99 Response Time: {results.P99ResponseTimeMs}ms");
        Console.WriteLine($"- Throughput: {results.ThroughputRPS:F2} RPS");
    }
}

public class TestResults
{
    public int TotalRequests { get; set; }
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
    public double SuccessRate { get; set; }
    public double ErrorRate { get; set; }
    public long[] ResponseTimes { get; set; } = Array.Empty<long>();
    public double AvgResponseTimeMs { get; set; }
    public long MinResponseTimeMs { get; set; }
    public long MaxResponseTimeMs { get; set; }
    public long MedianResponseTimeMs { get; set; }
    public long P50ResponseTimeMs { get; set; }
    public long P90ResponseTimeMs { get; set; }
    public long P95ResponseTimeMs { get; set; }
    public long P99ResponseTimeMs { get; set; }
    public double ThroughputRPS { get; set; }
    public double ActualRPS { get; set; }
    public TimeSpan ActualTestDuration { get; set; }
    public Dictionary<int, int> StatusCodeDistribution { get; set; } = new();
    public ResourceUsage[]? ResourceUsages { get; set; }
}

public class RequestResult
{
    public bool IsSuccess { get; set; }
    public long ResponseTimeMs { get; set; }
    public string? ErrorMessage { get; set; }
    public int StatusCode { get; set; }
}

public class ResourceUsage
{
    public DateTime Timestamp { get; set; }
    public double CpuTimeMs { get; set; }
    public long WorkingSetMB { get; set; }
    public int ThreadCount { get; set; }
}