using System;
using System.Threading.Tasks;
using CloudSync.PerformanceTests;

// Main entry point
await RunPerformanceTestsAsync();

async Task RunPerformanceTestsAsync()
{
    Console.WriteLine("CloudSync Performance Tests");
    Console.WriteLine("==========================");
    
    // Check if API_BASE_URL environment variable is set
    var apiBaseUrl = Environment.GetEnvironmentVariable("API_BASE_URL");
    Console.WriteLine($"API Base URL: {apiBaseUrl ?? "http://localhost:5000 (default)"}");
    
    try
    {
        var performanceTest = new CustomPerformanceTest();
        await performanceTest.RunTests();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"An error occurred during performance testing: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
        Environment.ExitCode = 1;
    }
}
