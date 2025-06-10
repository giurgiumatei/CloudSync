# CloudSync Kafka Idempotency Test Script (PowerShell)
# Tests duplicate message handling and idempotency across AWS and Azure consumers

param(
    [string]$ApiUrl = "http://localhost:5000",
    [string]$KafkaContainer = "cloudsync-kafka-1",
    [int]$TestDuration = 30,
    [int]$DuplicateRate = 20  # Percentage of messages to send as duplicates
)

# Error handling
$ErrorActionPreference = "Stop"

Write-Host "======================================" -ForegroundColor Blue
Write-Host "CloudSync Kafka Idempotency Test Suite" -ForegroundColor Blue
Write-Host "======================================" -ForegroundColor Blue
Write-Host ""
Write-Host "Testing idempotent message processing with duplicate detection"
Write-Host "API URL: $ApiUrl"
Write-Host "Test Duration: ${TestDuration}s"
Write-Host "Duplicate Rate: ${DuplicateRate}%"
Write-Host ""

# Function to print colored output
function Write-Status {
    param(
        [string]$Status,
        [string]$Message
    )
    
    switch ($Status) {
        "INFO" { 
            Write-Host "[INFO] $Message" -ForegroundColor Blue 
        }
        "SUCCESS" { 
            Write-Host "[SUCCESS] $Message" -ForegroundColor Green 
        }
        "WARNING" { 
            Write-Host "[WARNING] $Message" -ForegroundColor Yellow 
        }
        "ERROR" { 
            Write-Host "[ERROR] $Message" -ForegroundColor Red 
        }
    }
}

# Function to check if service is healthy
function Test-ServiceHealth {
    param(
        [string]$ServiceName,
        [string]$Url
    )
    
    Write-Status "INFO" "Checking $ServiceName health..."
    
    try {
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 10
        if ($response.StatusCode -eq 200) {
            Write-Status "SUCCESS" "$ServiceName is healthy"
            return $true
        }
    }
    catch {
        Write-Status "ERROR" "$ServiceName is not responding: $($_.Exception.Message)"
        return $false
    }
    
    return $false
}

# Function to get Kafka topic info
function Get-KafkaTopicInfo {
    Write-Status "INFO" "Getting Kafka topic information..."
    
    try {
        $result = docker exec $KafkaContainer kafka-topics --bootstrap-server localhost:9092 --describe --topic data-topic 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host $result
        } else {
            Write-Status "WARNING" "Could not get Kafka topic info"
        }
    }
    catch {
        Write-Status "WARNING" "Could not get Kafka topic info: $($_.Exception.Message)"
    }
}

# Function to send test message
function Send-TestMessage {
    param(
        [string]$MessageId,
        [string]$Data,
        [bool]$IsDuplicate = $false
    )
    
    $timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
    
    $jsonPayload = @{
        messageId = $MessageId
        data = $Data
        isDuplicate = $IsDuplicate
        timestamp = $timestamp
    } | ConvertTo-Json -Depth 3
    
    try {
        $response = Invoke-RestMethod -Uri "$ApiUrl/api/datasync" -Method Post -Body $jsonPayload -ContentType "application/json" -TimeoutSec 30
        
        if ($IsDuplicate) {
            Write-Host "DUPLICATE Message $MessageId sent successfully" -ForegroundColor Yellow
        } else {
            Write-Host "ORIGINAL Message $MessageId sent successfully" -ForegroundColor Green
        }
        return $true
    }
    catch {
        Write-Status "ERROR" "Failed to send message $MessageId. Error: $($_.Exception.Message)"
        return $false
    }
}

# Function to run idempotency test
function Invoke-IdempotencyTest {
    param(
        [string]$TestName,
        [int]$MessageCount,
        [double]$DelayBetweenMessages
    )
    
    Write-Status "INFO" "Running $TestName..."
    Write-Status "INFO" "Sending $MessageCount messages with ${DelayBetweenMessages}s delay"
    
    $successCount = 0
    $duplicateCount = 0
    $errorCount = 0
    
    for ($i = 1; $i -le $MessageCount; $i++) {
        $messageId = "test-$($TestName.ToLower())-$(Get-Date -Format 'yyyyMMdd-HHmmss')-$i"
        $testData = "Idempotency test data for message $i - $(Get-Date)"
        
        # Determine if this should be a duplicate
        $isDuplicate = $false
        if ((Get-Random -Maximum 100) -lt $DuplicateRate) {
            $isDuplicate = $true
            $duplicateCount++
        }
        
        if (Send-TestMessage -MessageId $messageId -Data $testData -IsDuplicate $isDuplicate) {
            $successCount++
        } else {
            $errorCount++
        }
        
        # Show progress
        if ($i % 10 -eq 0) {
            Write-Status "INFO" "Progress: $i/$MessageCount messages sent"
        }
        
        Start-Sleep -Seconds $DelayBetweenMessages
    }
    
    Write-Status "SUCCESS" "$TestName completed"
    Write-Status "INFO" "Results: $successCount sent, $duplicateCount duplicates, $errorCount errors"
    Write-Host ""
}

# Function to get consumer statistics
function Get-ConsumerStats {
    Write-Status "INFO" "Getting consumer statistics..."
    
    # Try to get consumer group info
    try {
        Write-Status "INFO" "AWS Consumer Group Info:"
        $awsResult = docker exec $KafkaContainer kafka-consumer-groups --bootstrap-server localhost:9092 --describe --group aws-sync-group 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host $awsResult
        } else {
            Write-Status "WARNING" "Could not get AWS consumer group info"
        }
    }
    catch {
        Write-Status "WARNING" "Could not get AWS consumer group info"
    }
    
    try {
        Write-Status "INFO" "Azure Consumer Group Info:"
        $azureResult = docker exec $KafkaContainer kafka-consumer-groups --bootstrap-server localhost:9092 --describe --group azure-sync-group 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host $azureResult
        } else {
            Write-Status "WARNING" "Could not get Azure consumer group info"
        }
    }
    catch {
        Write-Status "WARNING" "Could not get Azure consumer group info"
    }
}

# Function to check idempotency service health
function Test-IdempotencyHealth {
    Write-Status "INFO" "Checking idempotency service health via logs..."
    
    # Check AWS consumer logs for idempotency messages
    try {
        Write-Status "INFO" "AWS Consumer idempotency activity (last 20 lines):"
        $awsLogs = docker logs cloudsync-aws-consumer-1 --tail 20 2>$null
        if ($awsLogs) {
            $idempotencyLogs = $awsLogs | Select-String -Pattern "duplicate|idempotency|processed" -CaseSensitive:$false
            if ($idempotencyLogs) {
                $idempotencyLogs | ForEach-Object { Write-Host $_.Line }
            } else {
                Write-Host "No idempotency logs found"
            }
        }
    }
    catch {
        Write-Host "Could not retrieve AWS consumer logs"
    }
    
    Write-Host ""
    
    # Check Azure consumer logs for idempotency messages
    try {
        Write-Status "INFO" "Azure Consumer idempotency activity (last 20 lines):"
        $azureLogs = docker logs cloudsync-azure-consumer-1 --tail 20 2>$null
        if ($azureLogs) {
            $idempotencyLogs = $azureLogs | Select-String -Pattern "duplicate|idempotency|processed" -CaseSensitive:$false
            if ($idempotencyLogs) {
                $idempotencyLogs | ForEach-Object { Write-Host $_.Line }
            } else {
                Write-Host "No idempotency logs found"
            }
        }
    }
    catch {
        Write-Host "Could not retrieve Azure consumer logs"
    }
    
    Write-Host ""
}

# Function to simulate duplicate processing scenarios
function Invoke-DuplicateScenarios {
    Write-Status "INFO" "Simulating specific duplicate message scenarios..."
    
    # Scenario 1: Send the same message multiple times rapidly
    $baseId = "duplicate-scenario-1-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    $duplicateData = "Duplicate scenario test data - $(Get-Date)"
    
    Write-Status "INFO" "Scenario 1: Rapid duplicate messages"
    for ($i = 1; $i -le 5; $i++) {
        Send-TestMessage -MessageId $baseId -Data $duplicateData -IsDuplicate $false
        Start-Sleep -Milliseconds 100  # Very short delay to test concurrent processing
    }
    
    Start-Sleep -Seconds 2
    
    # Scenario 2: Send duplicates with delays to test cache behavior
    $baseId2 = "duplicate-scenario-2-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    $duplicateData2 = "Delayed duplicate test data - $(Get-Date)"
    
    Write-Status "INFO" "Scenario 2: Delayed duplicate messages"
    Send-TestMessage -MessageId $baseId2 -Data $duplicateData2 -IsDuplicate $false
    Start-Sleep -Seconds 5
    Send-TestMessage -MessageId $baseId2 -Data $duplicateData2 -IsDuplicate $true
    Start-Sleep -Seconds 5
    Send-TestMessage -MessageId $baseId2 -Data $duplicateData2 -IsDuplicate $true
    
    Write-Status "SUCCESS" "Duplicate scenarios completed"
    Write-Host ""
}

# Function to monitor processing locks
function Invoke-ProcessingLockMonitoring {
    Write-Status "INFO" "Monitoring processing locks (check logs for lock acquisition/release)..."
    
    # Send multiple messages rapidly to trigger lock contention
    $lockTestId = "lock-test-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    $lockTestData = "Lock contention test - $(Get-Date)"
    
    # Send same message 3 times rapidly using background jobs
    $jobs = @()
    for ($i = 1; $i -le 3; $i++) {
        $job = Start-Job -ScriptBlock {
            param($ApiUrl, $MessageId, $Data)
            
            $jsonPayload = @{
                messageId = $MessageId
                data = $Data
                isDuplicate = $false
                timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
            } | ConvertTo-Json -Depth 3
            
            try {
                Invoke-RestMethod -Uri "$ApiUrl/api/datasync" -Method Post -Body $jsonPayload -ContentType "application/json" -TimeoutSec 30
                return $true
            }
            catch {
                return $false
            }
        } -ArgumentList $ApiUrl, $lockTestId, $lockTestData
        
        $jobs += $job
    }
    
    # Wait for all jobs to complete
    $jobs | Wait-Job | Out-Null
    $jobs | Remove-Job
    
    Write-Status "INFO" "Lock contention test completed - check consumer logs for lock behavior"
    Write-Host ""
}

# Main test execution
function Invoke-Main {
    Write-Status "INFO" "Starting CloudSync Idempotency Test Suite..."
    Write-Host ""
    
    # Pre-test checks
    Write-Status "INFO" "Performing pre-test health checks..."
    
    $apiHealthy = Test-ServiceHealth -ServiceName "CloudSync API" -Url "$ApiUrl/api/health"
    if (-not $apiHealthy) {
        $apiHealthy = Test-ServiceHealth -ServiceName "CloudSync API (alternative)" -Url "$ApiUrl/health"
        if (-not $apiHealthy) {
            Write-Status "ERROR" "CloudSync API is not available. Please start the services first."
            exit 1
        }
    }
    
    Get-KafkaTopicInfo
    Write-Host ""
    
    # Test 1: Basic idempotency test
    Invoke-IdempotencyTest -TestName "BasicIdempotency" -MessageCount 20 -DelayBetweenMessages 0.5
    
    # Test 2: High frequency test
    Invoke-IdempotencyTest -TestName "HighFrequency" -MessageCount 50 -DelayBetweenMessages 0.1
    
    # Test 3: Duplicate scenarios
    Invoke-DuplicateScenarios
    
    # Test 4: Processing lock monitoring
    Invoke-ProcessingLockMonitoring
    
    # Wait for processing to complete
    Write-Status "INFO" "Waiting ${TestDuration}s for message processing to complete..."
    Start-Sleep -Seconds $TestDuration
    
    # Post-test analysis
    Write-Status "INFO" "Performing post-test analysis..."
    Get-ConsumerStats
    Write-Host ""
    
    Test-IdempotencyHealth
    
    # Final health check
    Write-Status "INFO" "Final service health check..."
    $finalHealth = Test-ServiceHealth -ServiceName "CloudSync API" -Url "$ApiUrl/api/health"
    if (-not $finalHealth) {
        Test-ServiceHealth -ServiceName "CloudSync API (alternative)" -Url "$ApiUrl/health" | Out-Null
    }
    
    Write-Status "SUCCESS" "Idempotency test suite completed!"
    Write-Host ""
    Write-Status "INFO" "Check the consumer logs for detailed idempotency behavior:"
    Write-Host "  docker logs cloudsync-aws-consumer-1 | Select-String -Pattern 'duplicate|idempotency' -CaseSensitive:`$false"
    Write-Host "  docker logs cloudsync-azure-consumer-1 | Select-String -Pattern 'duplicate|idempotency' -CaseSensitive:`$false"
    Write-Host ""
    Write-Status "INFO" "To view comprehensive consumer logs:"
    Write-Host "  docker logs cloudsync-aws-consumer-1"
    Write-Host "  docker logs cloudsync-azure-consumer-1"
}

# Run the main function
try {
    Invoke-Main
}
catch {
    Write-Status "ERROR" "Test suite failed: $($_.Exception.Message)"
    exit 1
} 