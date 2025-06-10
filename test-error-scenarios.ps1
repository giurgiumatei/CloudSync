# Test Script for Kafka Error Handling and Retry Logic (PowerShell)
# This script demonstrates how the system behaves during failures

Write-Host "🔥 Starting Error Handling and Retry Logic Tests" -ForegroundColor Yellow
Write-Host "==================================================" -ForegroundColor Yellow

$ApiUrl = "http://localhost:5000/api/datasync"
$KafkaContainer = "kafka"

# Function to send test message
function Send-Message {
    param([string]$Message)
    Write-Host "📤 Sending: $Message" -ForegroundColor Cyan
    try {
        $body = @{ } | ConvertTo-Json
        $response = Invoke-RestMethod -Uri $ApiUrl -Method Post -Body """$Message""" -ContentType "application/json"
        Write-Host "✅ Response: $response" -ForegroundColor Green
    }
    catch {
        Write-Host "❌ Failed to send message: $($_.Exception.Message)" -ForegroundColor Red
    }
    Write-Host ""
}

# Function to check consumer logs
function Check-Logs {
    param([string]$Consumer, [int]$Duration)
    Write-Host "📋 Checking $Consumer logs for last $Duration seconds..." -ForegroundColor Cyan
    try {
        $logs = docker logs $Consumer --since="${Duration}s" --tail 20 2>$null
        $filteredLogs = $logs | Select-String -Pattern "(processing message|retry|DLQ|error|failed)"
        if ($filteredLogs) {
            $filteredLogs | ForEach-Object { Write-Host $_ -ForegroundColor Gray }
        } else {
            Write-Host "No relevant logs found" -ForegroundColor Yellow
        }
    }
    catch {
        Write-Host "Error checking logs: $($_.Exception.Message)" -ForegroundColor Red
    }
    Write-Host ""
}

# Function to check DLQ messages
function Check-DLQ {
    Write-Host "💀 Checking Dead Letter Queue..." -ForegroundColor Magenta
    try {
        $dlqMessages = docker exec $KafkaContainer kafka-console-consumer --bootstrap-server localhost:9092 --topic data-topic-dlq --from-beginning --max-messages 5 --timeout-ms 5000 2>$null
        if ($dlqMessages) {
            Write-Host "DLQ Messages found:" -ForegroundColor Red
            $dlqMessages | ForEach-Object { Write-Host $_ -ForegroundColor Gray }
        } else {
            Write-Host "No messages in DLQ (or timeout)" -ForegroundColor Green
        }
    }
    catch {
        Write-Host "Error checking DLQ: $($_.Exception.Message)" -ForegroundColor Red
    }
    Write-Host ""
}

# Function to check consumer group lag
function Check-ConsumerLag {
    Write-Host "📊 Checking consumer group lag..." -ForegroundColor Cyan
    
    Write-Host "AWS Consumer Group:" -ForegroundColor Yellow
    try {
        $awsGroup = docker exec $KafkaContainer kafka-consumer-groups --bootstrap-server localhost:9092 --describe --group aws-sync-group 2>$null
        if ($awsGroup) {
            $awsGroup | ForEach-Object { Write-Host $_ -ForegroundColor Gray }
        } else {
            Write-Host "AWS group not found" -ForegroundColor Yellow
        }
    }
    catch {
        Write-Host "Error checking AWS group" -ForegroundColor Red
    }
    
    Write-Host ""
    Write-Host "Azure Consumer Group:" -ForegroundColor Yellow
    try {
        $azureGroup = docker exec $KafkaContainer kafka-consumer-groups --bootstrap-server localhost:9092 --describe --group azure-sync-group 2>$null
        if ($azureGroup) {
            $azureGroup | ForEach-Object { Write-Host $_ -ForegroundColor Gray }
        } else {
            Write-Host "Azure group not found" -ForegroundColor Yellow
        }
    }
    catch {
        Write-Host "Error checking Azure group" -ForegroundColor Red
    }
    Write-Host ""
}

Write-Host "🔧 Test 1: Normal Message Processing" -ForegroundColor Blue
Write-Host "------------------------------------" -ForegroundColor Blue
Send-Message "Normal test message - should succeed"
Start-Sleep 3
Check-Logs "aws-consumer" 5
Check-Logs "azure-consumer" 5

Write-Host "🔧 Test 2: Multiple Messages (should trigger some simulated failures)" -ForegroundColor Blue
Write-Host "---------------------------------------------------------------------" -ForegroundColor Blue
for ($i = 1; $i -le 10; $i++) {
    Send-Message "Test message $i - mixed success/failure simulation"
    Start-Sleep 0.5
}

Write-Host "⏳ Waiting 30 seconds for retry logic to complete..." -ForegroundColor Yellow
Start-Sleep 30

Check-Logs "aws-consumer" 35
Check-Logs "azure-consumer" 35

Write-Host "🔧 Test 3: High Volume (should trigger circuit breaker if configured)" -ForegroundColor Blue
Write-Host "--------------------------------------------------------------------" -ForegroundColor Blue
$jobs = @()
for ($i = 1; $i -le 20; $i++) {
    $jobs += Start-Job -ScriptBlock {
        param($ApiUrl, $i)
        try {
            Invoke-RestMethod -Uri $ApiUrl -Method Post -Body """High volume message $i""" -ContentType "application/json"
        } catch { }
    } -ArgumentList $ApiUrl, $i
}
$jobs | Wait-Job | Remove-Job

Write-Host "⏳ Waiting 20 seconds for processing..." -ForegroundColor Yellow
Start-Sleep 20

Check-Logs "aws-consumer" 25
Check-Logs "azure-consumer" 25

Write-Host "🔧 Test 4: Checking Dead Letter Queue" -ForegroundColor Blue
Write-Host "------------------------------------" -ForegroundColor Blue
Check-DLQ

Write-Host "🔧 Test 5: Consumer Group Status" -ForegroundColor Blue
Write-Host "-------------------------------" -ForegroundColor Blue
Check-ConsumerLag

Write-Host "🔧 Test 6: Verify Consumer Health" -ForegroundColor Blue
Write-Host "--------------------------------" -ForegroundColor Blue
Write-Host "AWS Consumer Status:" -ForegroundColor Yellow
docker ps --filter "name=aws-consumer" --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"

Write-Host ""
Write-Host "Azure Consumer Status:" -ForegroundColor Yellow
docker ps --filter "name=azure-consumer" --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"

Write-Host ""
Write-Host "🎯 Test Complete!" -ForegroundColor Green
Write-Host "=================" -ForegroundColor Green
Write-Host "✅ If you see retry attempts in the logs, exponential backoff is working" -ForegroundColor Green
Write-Host "✅ If you see messages in DLQ, dead letter queue handling is working" -ForegroundColor Green
Write-Host "✅ If consumers show healthy status, graceful error handling is working" -ForegroundColor Green
Write-Host "✅ Check the logs above for detailed retry and error handling behavior" -ForegroundColor Green

Write-Host ""
Write-Host "📋 To manually inspect DLQ messages with headers:" -ForegroundColor Cyan
Write-Host "docker exec kafka kafka-console-consumer --bootstrap-server localhost:9092 --topic data-topic-dlq --from-beginning --property print.headers=true --max-messages 10" -ForegroundColor Gray

Write-Host ""
Write-Host "📋 To monitor consumer groups in real-time:" -ForegroundColor Cyan
Write-Host "Use separate PowerShell window and run the consumer group describe commands in a loop" -ForegroundColor Gray 