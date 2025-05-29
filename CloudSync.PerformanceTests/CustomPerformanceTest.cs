using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace CloudSync.PerformanceTests;

public class CustomPerformanceTest
{
    private static readonly string ApiBaseUrl = Environment.GetEnvironmentVariable("API_BASE_URL") ?? "http://localhost:5000";
    private static readonly HttpClient HttpClient = new HttpClient();
    
    // Test configuration
    private readonly int[] _requestRates = { 10, 100, 1000 };  // Requests per second
    private readonly TimeSpan _testDuration = TimeSpan.FromSeconds(30); // Duration per test
    private readonly string _resultsDirectory;
    private readonly string _logFilePath;

    public CustomPerformanceTest()
    {
        // Check if we're running in a container with a mounted volume at /app/results
        _resultsDirectory = Directory.Exists("/app/results") ? "/app/results" : Directory.GetCurrentDirectory();
        _logFilePath = Path.Combine(_resultsDirectory, $"performance_results_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
    }

    public async Task RunTests()
    {
        Console.WriteLine($"Starting performance tests... Results will be saved to {_logFilePath}");
        
        // Create or overwrite the log file with headers
        File.WriteAllText(_logFilePath, "RequestRate,Timestamp,TotalRequests,SuccessCount,ErrorCount,SuccessRate,ErrorRate,AvgResponseTime,MinResponseTime,MaxResponseTime,P50ResponseTime,P90ResponseTime,P95ResponseTime,P99ResponseTime\n");
        
        foreach (var requestsPerSecond in _requestRates)
        {
            Console.WriteLine($"Running test at {requestsPerSecond} requests per second for {_testDuration.TotalSeconds} seconds");
            
            var results = await RunTestAtRate(requestsPerSecond);
            
            // Log results
            LogResults(requestsPerSecond, results);
            
            // Give system a short break between tests
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        
        Console.WriteLine($"Performance tests completed. Results saved to {_logFilePath}");
    }
    
    private async Task<TestResults> RunTestAtRate(int requestsPerSecond)
    {
        // Calculate test parameters
        var totalRequests = (int)(_testDuration.TotalSeconds * requestsPerSecond);
        var delayBetweenRequests = TimeSpan.FromSeconds(1.0 / requestsPerSecond);
        
        var results = new ConcurrentBag<RequestResult>();
        var stopwatch = Stopwatch.StartNew();
        var taskList = new List<Task>();
        
        for (int i = 0; i < totalRequests; i++)
        {
            // Check if the test duration has already passed
            if (stopwatch.Elapsed > _testDuration)
                break;
            
            // Delay to maintain the rate
            var currentDelay = TimeSpan.FromMilliseconds(Math.Max(0, i * delayBetweenRequests.TotalMilliseconds - stopwatch.ElapsedMilliseconds));
            
            if (currentDelay > TimeSpan.Zero)
                await Task.Delay(currentDelay);
            
            // Send a request without awaiting it
            var task = SendRequestAsync(results);
            taskList.Add(task);
            
            // Print progress every 10% of requests
            if (i % Math.Max(1, totalRequests / 10) == 0)
            {
                Console.WriteLine($"Progress: {i * 100 / totalRequests}% - Sent {i} requests");
            }
        }
        
        // Wait for all tasks to complete
        await Task.WhenAll(taskList);
        stopwatch.Stop();
        
        // Calculate statistics
        return CalculateResults(results);
    }
    
    private async Task SendRequestAsync(ConcurrentBag<RequestResult> results)
    {
        var requestResult = new RequestResult();
        var stopwatch = Stopwatch.StartNew();
        
        try
        {
            // Prepare the request payload - the API expects a string parameter
            var jsonString = "\"test data\"";
            var content = new StringContent(jsonString, Encoding.UTF8, "application/json");
            
            // Send the request
            var response = await HttpClient.PostAsync($"{ApiBaseUrl}/api/datasync", content);
            stopwatch.Stop();
            
            requestResult.ResponseTimeMs = stopwatch.ElapsedMilliseconds;
            requestResult.IsSuccess = response.IsSuccessStatusCode;
            requestResult.ErrorMessage = !response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            requestResult.ResponseTimeMs = stopwatch.ElapsedMilliseconds;
            requestResult.IsSuccess = false;
            requestResult.ErrorMessage = ex.Message;
        }
        
        results.Add(requestResult);
    }
    
    private TestResults CalculateResults(ConcurrentBag<RequestResult> requestResults)
    {
        var results = new TestResults
        {
            TotalRequests = requestResults.Count,
            SuccessCount = requestResults.Count(r => r.IsSuccess),
            ResponseTimes = requestResults.Select(r => r.ResponseTimeMs).ToArray()
        };
        
        // Calculate error count and rates
        results.ErrorCount = results.TotalRequests - results.SuccessCount;
        results.SuccessRate = results.TotalRequests > 0 ? (double)results.SuccessCount / results.TotalRequests : 0;
        results.ErrorRate = 1 - results.SuccessRate;
        
        // Calculate response time statistics
        if (results.ResponseTimes.Any())
        {
            results.AvgResponseTimeMs = results.ResponseTimes.Average();
            results.MinResponseTimeMs = results.ResponseTimes.Min();
            results.MaxResponseTimeMs = results.ResponseTimes.Max();
            
            // Calculate percentiles
            var sortedTimes = results.ResponseTimes.OrderBy(t => t).ToArray();
            results.P50ResponseTimeMs = CalculatePercentile(sortedTimes, 50);
            results.P90ResponseTimeMs = CalculatePercentile(sortedTimes, 90);
            results.P95ResponseTimeMs = CalculatePercentile(sortedTimes, 95);
            results.P99ResponseTimeMs = CalculatePercentile(sortedTimes, 99);
        }
        
        return results;
    }
    
    private long CalculatePercentile(long[] sortedData, int percentile)
    {
        if (sortedData.Length == 0)
            return 0;
            
        double index = (percentile / 100.0) * (sortedData.Length - 1);
        int lowerIndex = (int)Math.Floor(index);
        int upperIndex = (int)Math.Ceiling(index);
        
        if (lowerIndex == upperIndex)
            return sortedData[lowerIndex];
            
        // Interpolate between the values
        double weight = index - lowerIndex;
        return (long)(sortedData[lowerIndex] * (1 - weight) + sortedData[upperIndex] * weight);
    }
    
    private void LogResults(int requestRate, TestResults results)
    {
        // Format: RequestRate,Timestamp,TotalRequests,SuccessCount,ErrorCount,SuccessRate,ErrorRate,AvgResponseTime,MinResponseTime,MaxResponseTime,P50,P90,P95,P99
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var line = $"{requestRate},{timestamp},{results.TotalRequests},{results.SuccessCount},{results.ErrorCount}," +
                   $"{results.SuccessRate:P2},{results.ErrorRate:P2},{results.AvgResponseTimeMs:F2}," +
                   $"{results.MinResponseTimeMs},{results.MaxResponseTimeMs},{results.P50ResponseTimeMs}," +
                   $"{results.P90ResponseTimeMs},{results.P95ResponseTimeMs},{results.P99ResponseTimeMs}\n";
        
        File.AppendAllText(_logFilePath, line);
        
        // Also write a summary to a separate file for easy access
        var summaryPath = Path.Combine(_resultsDirectory, "latest_results_summary.txt");
        var summary = $"Test Results Summary - {DateTime.Now}\n" +
                      $"=============================\n" +
                      $"API URL: {ApiBaseUrl}\n" +
                      $"Request Rate: {requestRate} requests per second\n" +
                      $"Total Requests: {results.TotalRequests}\n" +
                      $"Success Rate: {results.SuccessRate:P2}\n" +
                      $"Error Rate: {results.ErrorRate:P2}\n" +
                      $"Avg Response Time: {results.AvgResponseTimeMs:F2}ms\n" +
                      $"Min Response Time: {results.MinResponseTimeMs}ms\n" +
                      $"Max Response Time: {results.MaxResponseTimeMs}ms\n" +
                      $"P50 Response Time: {results.P50ResponseTimeMs}ms\n" +
                      $"P90 Response Time: {results.P90ResponseTimeMs}ms\n" +
                      $"P95 Response Time: {results.P95ResponseTimeMs}ms\n" +
                      $"P99 Response Time: {results.P99ResponseTimeMs}ms\n\n";
        
        File.AppendAllText(summaryPath, summary);
        
        // Also print to console
        Console.WriteLine($"Test Results for {requestRate} requests per second:");
        Console.WriteLine($"- Total Requests: {results.TotalRequests}");
        Console.WriteLine($"- Success Rate: {results.SuccessRate:P2}");
        Console.WriteLine($"- Error Rate: {results.ErrorRate:P2}");
        Console.WriteLine($"- Avg Response Time: {results.AvgResponseTimeMs:F2}ms");
        Console.WriteLine($"- Min Response Time: {results.MinResponseTimeMs}ms");
        Console.WriteLine($"- Max Response Time: {results.MaxResponseTimeMs}ms");
        Console.WriteLine($"- P99 Response Time: {results.P99ResponseTimeMs}ms");
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
    public long P50ResponseTimeMs { get; set; }
    public long P90ResponseTimeMs { get; set; }
    public long P95ResponseTimeMs { get; set; }
    public long P99ResponseTimeMs { get; set; }
}

public class RequestResult
{
    public bool IsSuccess { get; set; }
    public long ResponseTimeMs { get; set; }
    public string? ErrorMessage { get; set; }
}