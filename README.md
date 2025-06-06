# CloudSync Performance Testing Framework

A comprehensive performance testing suite for the CloudSync API that synchronizes data between Azure and AWS cloud databases. This framework supports testing from 1 to 1,000,000 requests per second with detailed reporting and analysis.

## Project Overview

This repository contains a small .NET API along with an extensive performance
testing framework. The solution is organized under `CloudSyncSolution` and the
most important folders include:

```
CloudSyncSolution/
├── CloudSync.Api/             # Main API project
├── CloudSync.PerformanceTests/# Enhanced performance testing
├── CloudSync.Core/            # Business logic
├── CloudSync.Data/            # Data access layer
├── docker-compose.yml         # Container orchestration
├── run-performance-tests.sh   # Automated test runner
├── monitoring/                # Prometheus configuration
└── performance-results/       # Auto-generated test results
```

The API exposes controllers for data synchronization and health checks, wired up
in `Program.cs`. Business logic lives in `CloudSync.Core` while database access
is implemented in `CloudSync.Data`. Performance tests are driven by a custom
load generator located in `CloudSync.PerformanceTests` and can reach from 1 to
1,000,000 requests per second. Docker and helper scripts make it easy to run the
entire stack locally.

## 🚀 Features

- **Wide Range Testing**: 1 to 1,000,000 requests per second
- **Round-based Testing**: Each test round generates detailed reports
- **Multiple Report Formats**: CSV, HTML, JSON, and Markdown reports
- **Real-time Monitoring**: Optional Prometheus monitoring
- **Interactive Charts**: HTML reports with Chart.js visualizations
- **Resource Monitoring**: CPU, memory, and thread usage tracking
- **Containerized Testing**: Fully dockerized environment
- **Graceful Error Handling**: Automatic recovery and error logging
- **Performance Analysis**: Automated performance threshold detection

## 📊 Test Metrics

The framework measures and reports:

- **Response Time**: Average, min, max, median, and percentiles (P50, P90, P95, P99)
- **Throughput**: Actual requests per second achieved
- **Success/Error Rates**: Detailed success and error statistics
- **Status Code Distribution**: HTTP status code breakdown
- **Resource Usage**: CPU, memory, and thread consumption
- **Performance Thresholds**: Automatic detection of stable and maximum RPS

## 🏗️ Architecture

```
┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐
│   Performance   │    │   CloudSync     │    │   Databases     │
│   Test Client   ├────┤      API        ├────┤  Azure & AWS    │
│                 │    │                 │    │                 │
└─────────────────┘    └─────────────────┘    └─────────────────┘
         │                       │                       │
         └───────────────────────┼───────────────────────┘
                                 │
                        ┌─────────────────┐
                        │   Monitoring    │
                        │  (Prometheus)   │
                        └─────────────────┘
```

## 🛠️ Prerequisites

- **Docker**: Version 20.0+ with Docker Compose
- **System Resources**: 
  - Minimum: 4 CPU cores, 8GB RAM
  - Recommended: 8+ CPU cores, 16+ GB RAM
- **Network**: Stable network connection for high-load testing
- **Disk Space**: At least 2GB free space for results and logs

## 🚀 Quick Start

### Method 1: Using the Enhanced Script (Recommended)

```bash
# Make the script executable
chmod +x run-performance-tests.sh

# Run basic performance tests
./run-performance-tests.sh

# Run with monitoring enabled
./run-performance-tests.sh --monitoring

# Run with custom duration and cleanup
./run-performance-tests.sh --duration 30 --cleanup
```

### Method 2: Using Docker Compose

```bash
# Start the infrastructure
docker-compose up -d azure-db aws-db api

# Run performance tests
docker-compose up perftest

# View logs
docker-compose logs -f perftest

# With monitoring
docker-compose --profile monitoring up -d
```

## 📁 Project Structure

```
CloudSyncSolution/
├── CloudSync.Api/                     # Main API project
├── CloudSync.PerformanceTests/        # Enhanced performance testing
├── CloudSync.Core/                    # Business logic
├── CloudSync.Data/                    # Data access layer
├── docker-compose.yml                 # Enhanced container orchestration
├── run-performance-tests.sh           # Automated test runner
├── monitoring/
│   └── prometheus.yml                 # Monitoring configuration
└── performance-results/               # Test results (auto-generated)
    ├── performance_results_*.csv      # Raw test data
    ├── summary_report_*.html          # Interactive HTML reports
    ├── round_reports_*/               # Individual round reports
    ├── performance_analysis.md        # Detailed analysis
    └── latest_results_summary.txt     # Quick summary
```

## Next Steps for Newcomers

1. **Understand the API flow** – Review `SyncService`, the controllers, and how the
   EF Core contexts are configured.
2. **Run the system locally** – Use `docker-compose` or the provided scripts to
   start the API, databases, and performance tests (see "Quick Start").
3. **Explore performance results** – After running tests, inspect the CSV and
   HTML files in `performance-results` to see metrics and summaries.
4. **Investigate customization** – Adjust test parameters in
   `CustomPerformanceTest.cs` and experiment with the `GenerateRequestRates`
   method.
5. **Dive deeper** – Look at the Prometheus configuration in
   `monitoring/prometheus.yml` and consider extending the infrastructure or
   common projects for logging or queuing.

## ⚙️ Configuration

### Test Parameters

The performance tests can be configured by modifying `CloudSync.PerformanceTests/CustomPerformanceTest.cs`:

```csharp
// Test range: Automatically generated from 1 to 1,000,000 RPS
private readonly int[] _requestRates = GenerateRequestRates();

// Test duration per round
private readonly TimeSpan _testDuration = TimeSpan.FromSeconds(60);

// Warmup period before testing
private readonly TimeSpan _warmupDuration = TimeSpan.FromSeconds(10);
```

### Environment Variables

```bash
# API endpoint (automatically set in containers)
API_BASE_URL=http://api:80

# .NET Performance optimizations
DOTNET_GCServer=1
DOTNET_GCConcurrent=1
DOTNET_ThreadPool_ForceMaxWorkerThreads=1000
```

## 📊 Understanding Results

### CSV Report Format

| Column | Description |
|--------|-------------|
| Round | Test round number |
| RequestRate | Target requests per second |
| ActualRPS | Actual achieved RPS |
| SuccessRate | Percentage of successful requests |
| AvgResponseTime | Average response time in ms |
| P99ResponseTime | 99th percentile response time |
| ThroughputRPS | Effective throughput |

### Performance Thresholds

The framework automatically identifies:

- **Stable RPS**: Maximum RPS with <1% error rate
- **Usable RPS**: Maximum RPS with <10% error rate
- **Breaking Point**: Where error rate exceeds 50%

### HTML Reports

Interactive HTML reports include:

- **Real-time Charts**: Response time, throughput, and error rate charts
- **Detailed Metrics**: Per-round performance breakdown
- **Visual Analysis**: Logarithmic scale charts for wide RPS ranges
- **Resource Usage**: CPU and memory consumption graphs

## 🔧 Advanced Usage

### Custom Test Ranges

To test specific RPS ranges, modify the `GenerateRequestRates()` method:

```csharp
private static int[] GenerateRequestRates()
{
    // Example: Test only high-load scenarios
    return new[] { 1000, 5000, 10000, 25000, 50000 };
}
```

### Performance Tuning

For optimal results:

1. **System Tuning**:
   ```bash
   # Increase file descriptors
   ulimit -n 65536
   
   # Optimize network settings
   echo 65535 > /proc/sys/net/core/somaxconn
   ```

2. **Container Resources**:
   - Allocate sufficient CPU and memory
   - Use host networking for maximum performance
   - Disable unnecessary services

3. **Database Optimization**:
   - Use SSD storage for databases
   - Optimize connection pool settings
   - Monitor database performance

### Monitoring and Observability

Enable Prometheus monitoring:

```bash
./run-performance-tests.sh --monitoring
```

Access monitoring at:
- **Prometheus**: http://localhost:9090
- **Metrics**: Query system and application metrics

## 🐛 Troubleshooting

### Common Issues

1. **High Error Rates at Low RPS**:
   - Check database connectivity
   - Verify API health endpoint
   - Review container logs

2. **Memory Issues**:
   - Increase Docker memory limits
   - Monitor garbage collection
   - Reduce concurrent connections

3. **Network Timeouts**:
   - Adjust HTTP timeout settings
   - Check Docker network configuration
   - Monitor network latency

### Debugging Commands

```bash
# Check container health
docker-compose ps

# View detailed logs
docker-compose logs api
docker-compose logs perftest

# Monitor resource usage
docker stats

# Check database connectivity
docker-compose exec api curl http://localhost:80/api/healthcheck/test-connections
```

## 📈 Performance Optimization Tips

### For Maximum Throughput

1. **Use Production-like Environment**:
   - SSD storage
   - Dedicated network
   - Sufficient RAM

2. **Optimize Application**:
   - Enable connection pooling
   - Use async/await patterns
   - Minimize allocations

3. **Container Optimization**:
   - Use multi-stage builds
   - Optimize garbage collection
   - Set appropriate resource limits

### For Accurate Results

1. **Consistent Environment**:
   - Close unnecessary applications
   - Use dedicated test machine
   - Stable network conditions

2. **Multiple Test Runs**:
   - Run tests multiple times
   - Compare results across runs
   - Look for patterns and anomalies

## 🤝 Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests if applicable
5. Submit a pull request

## 📄 License

This project is licensed under the MIT License - see the LICENSE file for details.

## 🆘 Support

For issues and questions:

1. Check the troubleshooting section
2. Review container logs
3. Open an issue with detailed information
4. Include system specifications and error logs

---

**Happy Performance Testing! 🚀**