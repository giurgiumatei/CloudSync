#!/bin/bash

# CloudSync Performance Testing Script
# This script sets up and runs comprehensive performance tests

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Configuration
RESULTS_DIR="./performance-results"
MONITORING_ENABLED=false
CLEANUP_AFTER=false
TEST_DURATION="60s"

# Print usage
usage() {
    echo "Usage: $0 [OPTIONS]"
    echo "Options:"
    echo "  -m, --monitoring    Enable Prometheus monitoring"
    echo "  -c, --cleanup       Cleanup containers after tests"
    echo "  -d, --duration SEC  Test duration per round (default: 60s)"
    echo "  -h, --help         Show this help message"
    exit 1
}

# Parse command line arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        -m|--monitoring)
            MONITORING_ENABLED=true
            shift
            ;;
        -c|--cleanup)
            CLEANUP_AFTER=true
            shift
            ;;
        -d|--duration)
            TEST_DURATION="$2"
            shift 2
            ;;
        -h|--help)
            usage
            ;;
        *)
            echo "Unknown option $1"
            usage
            ;;
    esac
done

print_banner() {
    echo -e "${BLUE}"
    echo "╔══════════════════════════════════════════════════════════════╗"
    echo "║                CloudSync Performance Tests                   ║"
    echo "║            High-Load Testing: 1 to 1,000,000 RPS           ║"
    echo "╚══════════════════════════════════════════════════════════════╝"
    echo -e "${NC}"
}

log_info() {
    echo -e "${GREEN}[INFO]${NC} $1"
}

log_warning() {
    echo -e "${YELLOW}[WARNING]${NC} $1"
}

log_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

check_prerequisites() {
    log_info "Checking prerequisites..."
    
    if ! command -v docker &> /dev/null; then
        log_error "Docker is not installed or not in PATH"
        exit 1
    fi
    
    if ! command -v docker-compose &> /dev/null; then
        log_error "Docker Compose is not installed or not in PATH"
        exit 1
    fi
    
    # Check available system resources
    TOTAL_MEM=$(free -m | awk 'NR==2{printf "%.0f", $2/1024}')
    if [ "$TOTAL_MEM" -lt 8 ]; then
        log_warning "System has less than 8GB RAM. High-load tests may be limited."
    fi
    
    CPU_CORES=$(nproc)
    if [ "$CPU_CORES" -lt 4 ]; then
        log_warning "System has less than 4 CPU cores. Performance may be limited."
    fi
    
    log_info "System resources: ${CPU_CORES} CPU cores, ${TOTAL_MEM}GB RAM"
}

setup_environment() {
    log_info "Setting up test environment..."
    
    # Create results directory
    mkdir -p "$RESULTS_DIR"
    mkdir -p "./monitoring"
    
    # Set optimal Docker settings for performance testing
    log_info "Optimizing Docker settings for high-load testing..."
    
    # Stop any existing containers
    docker-compose down --remove-orphans 2>/dev/null || true
    
    # Clean up any leftover containers
    docker container prune -f 2>/dev/null || true
    docker network prune -f 2>/dev/null || true
    
    # Build images
    log_info "Building Docker images..."
    docker-compose build --no-cache
    
    # Start infrastructure services first
    log_info "Starting database services..."
    docker-compose up -d azure-db aws-db
    
    # Wait for databases to be ready
    log_info "Waiting for databases to be ready..."
    timeout=120
    while [ $timeout -gt 0 ]; do
        if docker-compose ps azure-db | grep -q "healthy" && \
           docker-compose ps aws-db | grep -q "healthy"; then
            log_info "Databases are ready!"
            break
        fi
        sleep 2
        timeout=$((timeout - 2))
    done
    
    if [ $timeout -eq 0 ]; then
        log_error "Databases failed to start within timeout"
        exit 1
    fi
    
    # Start API service
    log_info "Starting API service..."
    docker-compose up -d api
    
    # Wait for API to be ready
    log_info "Waiting for API service to be ready..."
    timeout=60
    while [ $timeout -gt 0 ]; do
        if curl -f -s http://localhost:5000/api/healthcheck/test-connections >/dev/null 2>&1; then
            log_info "API service is ready!"
            break
        fi
        sleep 2
        timeout=$((timeout - 2))
    done
    
    if [ $timeout -eq 0 ]; then
        log_error "API service failed to start within timeout"
        exit 1
    fi
}

start_monitoring() {
    if [ "$MONITORING_ENABLED" = true ]; then
        log_info "Starting monitoring services..."
        docker-compose --profile monitoring up -d monitoring
        log_info "Prometheus monitoring available at http://localhost:9090"
    fi
}

run_performance_tests() {
    log_info "Starting performance tests..."
    log_info "Test configuration:"
    log_info "  - Range: 1 to 1,000,000 RPS"
    log_info "  - Duration per round: $TEST_DURATION"
    log_info "  - Results directory: $RESULTS_DIR"
    
    # Start performance test container
    docker-compose up --no-deps perftest
    
    local exit_code=$?
    
    if [ $exit_code -eq 0 ]; then
        log_info "Performance tests completed successfully!"
        
        # Show results summary
        if [ -f "$RESULTS_DIR/latest_results_summary.txt" ]; then
            echo -e "${BLUE}Latest Results Summary:${NC}"
            echo "========================"
            tail -n 20 "$RESULTS_DIR/latest_results_summary.txt"
        fi
        
        # List all generated reports
        echo -e "${BLUE}Generated Reports:${NC}"
        find "$RESULTS_DIR" -name "*.csv" -o -name "*.html" -o -name "*.md" | while read -r file; do
            echo "  - $file"
        done
        
    else
        log_error "Performance tests failed with exit code $exit_code"
        return $exit_code
    fi
}

generate_summary_report() {
    log_info "Generating summary report..."
    
    # Find the latest CSV results file
    LATEST_CSV=$(find "$RESULTS_DIR" -name "performance_results_*.csv" -type f -printf '%T@ %p\n' | sort -n | tail -1 | cut -d' ' -f2-)
    
    if [ -n "$LATEST_CSV" ] && [ -f "$LATEST_CSV" ]; then
        SUMMARY_FILE="$RESULTS_DIR/test_summary_$(date +%Y%m%d_%H%M%S).txt"
        
        {
            echo "CloudSync Performance Test Summary"
            echo "=================================="
            echo "Generated: $(date)"
            echo "Results file: $LATEST_CSV"
            echo ""
            echo "Key Metrics:"
            echo "============"
            
            # Extract key metrics from CSV
            if command -v awk &> /dev/null; then
                echo "Total test rounds: $(tail -n +2 "$LATEST_CSV" | wc -l)"
                echo "Highest successful RPS: $(tail -n +2 "$LATEST_CSV" | awk -F',' '$8 > 0.9 {print $2}' | sort -n | tail -1)"
                echo "Average success rate: $(tail -n +2 "$LATEST_CSV" | awk -F',' '{sum+=$8; count++} END {if(count>0) printf "%.2f%%", (sum/count)*100}')"
            fi
            
            echo ""
            echo "Files generated:"
            find "$RESULTS_DIR" -newer "$LATEST_CSV" -type f | sort
            
        } > "$SUMMARY_FILE"
        
        log_info "Summary report saved to: $SUMMARY_FILE"
    fi
}

cleanup() {
    if [ "$CLEANUP_AFTER" = true ]; then
        log_info "Cleaning up containers..."
        docker-compose down --volumes --remove-orphans
        docker system prune -f
    else
        log_info "Stopping containers (use --cleanup to remove them)..."
        docker-compose stop
    fi
}

main() {
    print_banner
    
    # Set up signal handlers for graceful shutdown
    trap 'log_warning "Interrupted! Cleaning up..."; cleanup; exit 130' INT TERM
    
    check_prerequisites
    setup_environment
    start_monitoring
    
    # Run the actual performance tests
    if run_performance_tests; then
        generate_summary_report
        
        log_info "Performance testing completed successfully!"
        log_info "Results are available in: $RESULTS_DIR"
        
        if [ "$MONITORING_ENABLED" = true ]; then
            log_info "Monitoring dashboard: http://localhost:9090"
        fi
        
    else
        log_error "Performance testing failed!"
        exit 1
    fi
    
    cleanup
}

# Run main function
main "$@" 