#!/bin/bash

# CloudSync Kafka Idempotency Test Script
# Tests duplicate message handling and idempotency across AWS and Azure consumers

set -e

# Configuration
API_URL="http://localhost:5000"
KAFKA_CONTAINER="cloudsync-kafka-1"
TEST_DURATION=30
DUPLICATE_RATE=20  # Percentage of messages to send as duplicates

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

echo -e "${BLUE}======================================${NC}"
echo -e "${BLUE}CloudSync Kafka Idempotency Test Suite${NC}"
echo -e "${BLUE}======================================${NC}"
echo
echo "Testing idempotent message processing with duplicate detection"
echo "API URL: $API_URL"
echo "Test Duration: ${TEST_DURATION}s"
echo "Duplicate Rate: ${DUPLICATE_RATE}%"
echo

# Function to print colored output
print_status() {
    local status=$1
    local message=$2
    case $status in
        "INFO")
            echo -e "${BLUE}[INFO]${NC} $message"
            ;;
        "SUCCESS")
            echo -e "${GREEN}[SUCCESS]${NC} $message"
            ;;
        "WARNING")
            echo -e "${YELLOW}[WARNING]${NC} $message"
            ;;
        "ERROR")
            echo -e "${RED}[ERROR]${NC} $message"
            ;;
    esac
}

# Function to check if service is healthy
check_service() {
    local service_name=$1
    local url=$2
    
    print_status "INFO" "Checking $service_name health..."
    
    if curl -s -f "$url" > /dev/null 2>&1; then
        print_status "SUCCESS" "$service_name is healthy"
        return 0
    else
        print_status "ERROR" "$service_name is not responding"
        return 1
    fi
}

# Function to get Kafka topic info
get_kafka_topic_info() {
    print_status "INFO" "Getting Kafka topic information..."
    
    docker exec $KAFKA_CONTAINER kafka-topics --bootstrap-server localhost:9092 --describe --topic data-topic 2>/dev/null || {
        print_status "WARNING" "Could not get Kafka topic info"
    }
}

# Function to send test message
send_test_message() {
    local message_id=$1
    local data=$2
    local is_duplicate=${3:-false}
    
    local json_payload=$(cat <<EOF
{
    "messageId": "$message_id",
    "data": "$data",
    "isDuplicate": $is_duplicate,
    "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)"
}
EOF
)
    
    local response=$(curl -s -w "%{http_code}" -X POST \
        -H "Content-Type: application/json" \
        -d "$json_payload" \
        "$API_URL/api/datasync")
    
    local http_code="${response: -3}"
    local response_body="${response%???}"
    
    if [[ "$http_code" == "200" || "$http_code" == "202" ]]; then
        if [ "$is_duplicate" = true ]; then
            echo -e "${YELLOW}DUPLICATE${NC} Message $message_id sent successfully"
        else
            echo -e "${GREEN}ORIGINAL${NC} Message $message_id sent successfully"
        fi
        return 0
    else
        print_status "ERROR" "Failed to send message $message_id. HTTP: $http_code, Response: $response_body"
        return 1
    fi
}

# Function to run idempotency test
run_idempotency_test() {
    local test_name=$1
    local message_count=$2
    local delay_between_messages=$3
    
    print_status "INFO" "Running $test_name..."
    print_status "INFO" "Sending $message_count messages with ${delay_between_messages}s delay"
    
    local success_count=0
    local duplicate_count=0
    local error_count=0
    
    for ((i=1; i<=message_count; i++)); do
        local message_id="test-${test_name,,}-$(date +%s)-$i"
        local test_data="Idempotency test data for message $i - $(date)"
        
        # Determine if this should be a duplicate
        local is_duplicate=false
        if (( RANDOM % 100 < DUPLICATE_RATE )); then
            is_duplicate=true
            ((duplicate_count++))
        fi
        
        if send_test_message "$message_id" "$test_data" "$is_duplicate"; then
            ((success_count++))
        else
            ((error_count++))
        fi
        
        # Show progress
        if (( i % 10 == 0 )); then
            print_status "INFO" "Progress: $i/$message_count messages sent"
        fi
        
        sleep "$delay_between_messages"
    done
    
    print_status "SUCCESS" "$test_name completed"
    print_status "INFO" "Results: $success_count sent, $duplicate_count duplicates, $error_count errors"
    echo
}

# Function to get consumer statistics
get_consumer_stats() {
    print_status "INFO" "Getting consumer statistics..."
    
    # Try to get consumer group info
    docker exec $KAFKA_CONTAINER kafka-consumer-groups --bootstrap-server localhost:9092 --describe --group aws-sync-group 2>/dev/null || {
        print_status "WARNING" "Could not get AWS consumer group info"
    }
    
    docker exec $KAFKA_CONTAINER kafka-consumer-groups --bootstrap-server localhost:9092 --describe --group azure-sync-group 2>/dev/null || {
        print_status "WARNING" "Could not get Azure consumer group info"
    }
}

# Function to check idempotency service health
check_idempotency_health() {
    print_status "INFO" "Checking idempotency service health via logs..."
    
    # Check AWS consumer logs for idempotency messages
    print_status "INFO" "AWS Consumer idempotency activity (last 20 lines):"
    docker logs cloudsync-aws-consumer-1 --tail 20 2>/dev/null | grep -i "duplicate\|idempotency\|processed" || echo "No idempotency logs found"
    
    echo
    
    # Check Azure consumer logs for idempotency messages
    print_status "INFO" "Azure Consumer idempotency activity (last 20 lines):"
    docker logs cloudsync-azure-consumer-1 --tail 20 2>/dev/null | grep -i "duplicate\|idempotency\|processed" || echo "No idempotency logs found"
    
    echo
}

# Function to simulate duplicate processing scenarios
simulate_duplicate_scenarios() {
    print_status "INFO" "Simulating specific duplicate message scenarios..."
    
    # Scenario 1: Send the same message multiple times rapidly
    local base_id="duplicate-scenario-1-$(date +%s)"
    local duplicate_data="Duplicate scenario test data - $(date)"
    
    print_status "INFO" "Scenario 1: Rapid duplicate messages"
    for i in {1..5}; do
        send_test_message "$base_id" "$duplicate_data" false
        sleep 0.1  # Very short delay to test concurrent processing
    done
    
    sleep 2
    
    # Scenario 2: Send duplicates with delays to test cache behavior
    local base_id2="duplicate-scenario-2-$(date +%s)"
    local duplicate_data2="Delayed duplicate test data - $(date)"
    
    print_status "INFO" "Scenario 2: Delayed duplicate messages"
    send_test_message "$base_id2" "$duplicate_data2" false
    sleep 5
    send_test_message "$base_id2" "$duplicate_data2" true
    sleep 5
    send_test_message "$base_id2" "$duplicate_data2" true
    
    print_status "SUCCESS" "Duplicate scenarios completed"
    echo
}

# Function to monitor processing locks
monitor_processing_locks() {
    print_status "INFO" "Monitoring processing locks (check logs for lock acquisition/release)..."
    
    # Send multiple messages rapidly to trigger lock contention
    local lock_test_id="lock-test-$(date +%s)"
    local lock_test_data="Lock contention test - $(date)"
    
    # Send same message 3 times rapidly
    for i in {1..3}; do
        send_test_message "$lock_test_id" "$lock_test_data" false &
    done
    
    wait  # Wait for all background jobs to complete
    
    print_status "INFO" "Lock contention test completed - check consumer logs for lock behavior"
    echo
}

# Main test execution
main() {
    print_status "INFO" "Starting CloudSync Idempotency Test Suite..."
    echo
    
    # Pre-test checks
    print_status "INFO" "Performing pre-test health checks..."
    
    if ! check_service "CloudSync API" "$API_URL/api/health"; then
        if ! check_service "CloudSync API (alternative)" "$API_URL/health"; then
            print_status "ERROR" "CloudSync API is not available. Please start the services first."
            exit 1
        fi
    fi
    
    get_kafka_topic_info
    echo
    
    # Test 1: Basic idempotency test
    run_idempotency_test "BasicIdempotency" 20 0.5
    
    # Test 2: High frequency test
    run_idempotency_test "HighFrequency" 50 0.1
    
    # Test 3: Duplicate scenarios
    simulate_duplicate_scenarios
    
    # Test 4: Processing lock monitoring
    monitor_processing_locks
    
    # Wait for processing to complete
    print_status "INFO" "Waiting ${TEST_DURATION}s for message processing to complete..."
    sleep $TEST_DURATION
    
    # Post-test analysis
    print_status "INFO" "Performing post-test analysis..."
    get_consumer_stats
    echo
    
    check_idempotency_health
    
    # Final health check
    print_status "INFO" "Final service health check..."
    check_service "CloudSync API" "$API_URL/api/health" || check_service "CloudSync API (alternative)" "$API_URL/health"
    
    print_status "SUCCESS" "Idempotency test suite completed!"
    echo
    print_status "INFO" "Check the consumer logs for detailed idempotency behavior:"
    echo "  docker logs cloudsync-aws-consumer-1 | grep -i 'duplicate\|idempotency'"
    echo "  docker logs cloudsync-azure-consumer-1 | grep -i 'duplicate\|idempotency'"
    echo
    print_status "INFO" "To view comprehensive consumer logs:"
    echo "  docker logs cloudsync-aws-consumer-1"
    echo "  docker logs cloudsync-azure-consumer-1"
}

# Run the main function
main "$@" 