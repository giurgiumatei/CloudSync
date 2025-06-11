# Simple CloudSync API Performance Test
# Tests API response times and measures performance improvements

param(
    [string]$ApiUrl = "http://localhost:5000",
    [int]$MaxRPS = 100,
    [int]$TestDurationSeconds = 60
)

Write-Host "========================================" -ForegroundColor Blue
Write-Host "CloudSync API Performance Test" -ForegroundColor Blue  
Write-Host "========================================" -ForegroundColor Blue
Write-Host "API URL: $ApiUrl"
Write-Host "Max RPS: $MaxRPS"
Write-Host "Test Duration: $TestDurationSeconds seconds"
Write-Host ""

# Function to test API health
function Test-APIHealth {
    param([string]$Url)
    
    try {
        Write-Host "Testing API health..." -ForegroundColor Yellow
        $healthResponse = Invoke-RestMethod -Uri "$Url/api/health" -Method Get -TimeoutSec 10
        Write-Host "API is healthy: $healthResponse" -ForegroundColor Green
        return $true
    }
    catch {
        Write-Host "API health check failed: $($_.Exception.Message)" -ForegroundColor Red
        
        # Try alternative health endpoint
        try {
            $altResponse = Invoke-RestMethod -Uri "$Url/health" -Method Get -TimeoutSec 10
            Write-Host "API is healthy (alternative endpoint): $altResponse" -ForegroundColor Green
            return $true
        }
        catch {
            Write-Host "Both health endpoints failed" -ForegroundColor Red
            return $false
        }
    }
}

# Function to send test message
function Send-TestMessage {
    param(
        [string]$Url,
        [int]$MessageNumber
    )
    
    $timestamp = Get-Date
    $testData = @{
        id = "perf-test-$MessageNumber-$(Get-Date -Format 'yyyyMMddHHmmss')"
        data = "Performance test message $MessageNumber - $(Get-Date)"
        timestamp = $timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
    } | ConvertTo-Json
    
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    
    try {
        $response = Invoke-RestMethod -Uri "$Url/api/datasync" -Method Post -Body $testData -ContentType "application/json" -TimeoutSec 30
        $stopwatch.Stop()
        
        return @{
            Success = $true
            ResponseTime = $stopwatch.ElapsedMilliseconds
            StatusCode = 200
            MessageNumber = $MessageNumber
        }
    }
    catch {
        $stopwatch.Stop()
        
        return @{
            Success = $false
            ResponseTime = $stopwatch.ElapsedMilliseconds
            StatusCode = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
            Error = $_.Exception.Message
            MessageNumber = $MessageNumber
        }
    }
}

# Function to run performance test
function Start-PerformanceTest {
    param(
        [string]$Url,
        [int]$RPS,
        [int]$Duration
    )
    
    Write-Host "Starting performance test: $RPS RPS for $Duration seconds..." -ForegroundColor Yellow
    
    $results = @()
    $startTime = Get-Date
    $endTime = $startTime.AddSeconds($Duration)
    $messageCount = 0
    $delayMs = [math]::Max(1, 1000 / $RPS)
    
    while ((Get-Date) -lt $endTime) {
        $messageCount++
        $result = Send-TestMessage -Url $Url -MessageNumber $messageCount
        $results += $result
        
        # Check for 100% error rate (stop condition)
        if ($results.Count -ge 10) {
            $recentErrors = ($results | Select-Object -Last 10 | Where-Object { -not $_.Success }).Count
            $errorRate = $recentErrors / 10
            
            if ($errorRate -eq 1.0) {
                Write-Host "ERROR: 100% error rate detected! Stopping test." -ForegroundColor Red
                break
            }
        }
        
        # Progress reporting
        if ($messageCount % [math]::Max(1, $RPS / 4) -eq 0) {
            $elapsedSeconds = ((Get-Date) - $startTime).TotalSeconds
            $currentRPS = $messageCount / $elapsedSeconds
            $successCount = ($results | Where-Object { $_.Success }).Count
            $currentSuccessRate = if ($results.Count -gt 0) { $successCount / $results.Count } else { 0 }
            
            Write-Host "Progress: $messageCount messages sent, Current RPS: $([math]::Round($currentRPS, 1)), Success Rate: $($currentSuccessRate.ToString('P1'))" -ForegroundColor Cyan
        }
        
        # Control request rate
        Start-Sleep -Milliseconds $delayMs
    }
    
    return $results
}

# Function to analyze results
function Get-ResultAnalysis {
    param([array]$Results)
    
    $successfulResults = $Results | Where-Object { $_.Success }
    $failedResults = $Results | Where-Object { -not $_.Success }
    
    $analysis = @{
        TotalRequests = $Results.Count
        SuccessfulRequests = $successfulResults.Count
        FailedRequests = $failedResults.Count
        SuccessRate = if ($Results.Count -gt 0) { $successfulResults.Count / $Results.Count } else { 0 }
        ErrorRate = if ($Results.Count -gt 0) { $failedResults.Count / $Results.Count } else { 0 }
    }
    
    if ($successfulResults.Count -gt 0) {
        $responseTimes = $successfulResults | ForEach-Object { $_.ResponseTime }
        $analysis.AvgResponseTime = ($responseTimes | Measure-Object -Average).Average
        $analysis.MinResponseTime = ($responseTimes | Measure-Object -Minimum).Minimum
        $analysis.MaxResponseTime = ($responseTimes | Measure-Object -Maximum).Maximum
        
        # Calculate percentiles
        $sortedTimes = $responseTimes | Sort-Object
        $count = $sortedTimes.Count
        $analysis.P50ResponseTime = $sortedTimes[[math]::Floor($count * 0.5)]
        $analysis.P90ResponseTime = $sortedTimes[[math]::Floor($count * 0.9)]
        $analysis.P95ResponseTime = $sortedTimes[[math]::Floor($count * 0.95)]
        $analysis.P99ResponseTime = $sortedTimes[[math]::Floor($count * 0.99)]
    }
    
    return $analysis
}

# Main execution
try {
    # Test API health first
    if (-not (Test-APIHealth -Url $ApiUrl)) {
        Write-Host "API is not responding. Cannot run performance tests." -ForegroundColor Red
        Write-Host "Please ensure the CloudSync API is running on $ApiUrl" -ForegroundColor Yellow
        exit 1
    }
    
    Write-Host ""
    Write-Host "API is healthy. Starting performance tests..." -ForegroundColor Green
    Write-Host ""
    
    # Test different RPS levels
    $testRates = @(1, 5, 10, 25, 50, $MaxRPS)
    $allResults = @()
    
    foreach ($rate in $testRates) {
        Write-Host "===== Testing $rate RPS =====" -ForegroundColor Blue
        
        $testResults = Start-PerformanceTest -Url $ApiUrl -RPS $rate -Duration 30
        $analysis = Get-ResultAnalysis -Results $testResults
        
        Write-Host ""
        Write-Host "Results for $rate RPS:" -ForegroundColor Green
        Write-Host "  Total Requests: $($analysis.TotalRequests)"
        Write-Host "  Success Rate: $($analysis.SuccessRate.ToString('P2'))"
        Write-Host "  Error Rate: $($analysis.ErrorRate.ToString('P2'))"
        
        if ($analysis.SuccessfulRequests -gt 0) {
            Write-Host "  Avg Response Time: $($analysis.AvgResponseTime.ToString('F2'))ms"
            Write-Host "  Min Response Time: $($analysis.MinResponseTime)ms"
            Write-Host "  Max Response Time: $($analysis.MaxResponseTime)ms"
            Write-Host "  P50 Response Time: $($analysis.P50ResponseTime)ms"
            Write-Host "  P90 Response Time: $($analysis.P90ResponseTime)ms"
            Write-Host "  P95 Response Time: $($analysis.P95ResponseTime)ms"
            Write-Host "  P99 Response Time: $($analysis.P99ResponseTime)ms"
        }
        
        $allResults += @{
            RPS = $rate
            Analysis = $analysis
        }
        
        Write-Host ""
        
        # Stop if error rate is too high
        if ($analysis.ErrorRate -gt 0.5) {
            Write-Host "High error rate ($($analysis.ErrorRate.ToString('P1'))) detected. Stopping further tests." -ForegroundColor Yellow
            break
        }
        
        # Brief pause between tests
        Start-Sleep -Seconds 5
    }
    
    # Generate summary report
    Write-Host "========================================" -ForegroundColor Blue
    Write-Host "PERFORMANCE TEST SUMMARY" -ForegroundColor Blue
    Write-Host "========================================" -ForegroundColor Blue
    
    foreach ($result in $allResults) {
        $rate = $result.RPS
        $analysis = $result.Analysis
        
        Write-Host "$rate RPS: Success Rate: $($analysis.SuccessRate.ToString('P1')), Avg Response: $($analysis.AvgResponseTime.ToString('F1'))ms" -ForegroundColor Cyan
    }
    
    Write-Host ""
    Write-Host "Performance test completed successfully!" -ForegroundColor Green
}
catch {
    Write-Host "Performance test failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
} 