#!/bin/bash

# Test Script for Kafka Error Handling and Retry Logic
# This script demonstrates how the system behaves during failures

set -e

echo "🔥 Starting Error Handling and Retry Logic Tests"
echo "=================================================="

API_URL="http://localhost:5000/api/datasync"
KAFKA_CONTAINER="kafka"

# Function to send test message
send_message() {
    local message="$1"
    echo "📤 Sending: $message"
    curl -s -X POST "$API_URL" \
        -H "Content-Type: application/json" \
        -d "\"$message\"" \
        || echo "❌ Failed to send message"
    echo ""
}

# Function to check consumer logs
check_logs() {
    local consumer="$1"
    local duration="$2"
    echo "📋 Checking $consumer logs for last $duration seconds..."
    docker logs "$consumer" --since="${duration}s" --tail 20 | grep -E "(processing message|retry|DLQ|error|failed)" || echo "No relevant logs found"
    echo ""
}

# Function to check DLQ messages
check_dlq() {
    echo "💀 Checking Dead Letter Queue..."
    docker exec "$KAFKA_CONTAINER" kafka-console-consumer \
        --bootstrap-server localhost:9092 \
        --topic data-topic-dlq \
        --from-beginning \
        --max-messages 5 \
        --timeout-ms 5000 \
        2>/dev/null || echo "No messages in DLQ (or timeout)"
    echo ""
}

# Function to check consumer group lag
check_consumer_lag() {
    echo "📊 Checking consumer group lag..."
    echo "AWS Consumer Group:"
    docker exec "$KAFKA_CONTAINER" kafka-consumer-groups \
        --bootstrap-server localhost:9092 \
        --describe \
        --group aws-sync-group \
        2>/dev/null || echo "AWS group not found"
    
    echo ""
    echo "Azure Consumer Group:"
    docker exec "$KAFKA_CONTAINER" kafka-consumer-groups \
        --bootstrap-server localhost:9092 \
        --describe \
        --group azure-sync-group \
        2>/dev/null || echo "Azure group not found"
    echo ""
}

echo "🔧 Test 1: Normal Message Processing"
echo "------------------------------------"
send_message "Normal test message - should succeed"
sleep 3
check_logs "aws-consumer" 5
check_logs "azure-consumer" 5

echo "🔧 Test 2: Multiple Messages (should trigger some simulated failures)"
echo "---------------------------------------------------------------------"
for i in {1..10}; do
    send_message "Test message $i - mixed success/failure simulation"
    sleep 0.5
done

echo "⏳ Waiting 30 seconds for retry logic to complete..."
sleep 30

check_logs "aws-consumer" 35
check_logs "azure-consumer" 35

echo "🔧 Test 3: High Volume (should trigger circuit breaker if configured)"
echo "--------------------------------------------------------------------"
for i in {1..20}; do
    send_message "High volume message $i" &
done
wait

echo "⏳ Waiting 20 seconds for processing..."
sleep 20

check_logs "aws-consumer" 25
check_logs "azure-consumer" 25

echo "🔧 Test 4: Checking Dead Letter Queue"
echo "------------------------------------"
check_dlq

echo "🔧 Test 5: Consumer Group Status"
echo "-------------------------------"
check_consumer_lag

echo "🔧 Test 6: Verify Consumer Health"
echo "--------------------------------"
echo "AWS Consumer Status:"
docker ps --filter "name=aws-consumer" --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"

echo ""
echo "Azure Consumer Status:"
docker ps --filter "name=azure-consumer" --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"

echo ""
echo "🎯 Test Complete!"
echo "================="
echo "✅ If you see retry attempts in the logs, exponential backoff is working"
echo "✅ If you see messages in DLQ, dead letter queue handling is working"
echo "✅ If consumers show healthy status, graceful error handling is working"
echo "✅ Check the logs above for detailed retry and error handling behavior"

echo ""
echo "📋 To manually inspect DLQ messages with headers:"
echo "docker exec kafka kafka-console-consumer --bootstrap-server localhost:9092 --topic data-topic-dlq --from-beginning --property print.headers=true --max-messages 10"

echo ""
echo "📋 To monitor consumer groups in real-time:"
echo "watch 'docker exec kafka kafka-consumer-groups --bootstrap-server localhost:9092 --describe --group aws-sync-group && echo && docker exec kafka kafka-consumer-groups --bootstrap-server localhost:9092 --describe --group azure-sync-group'" 