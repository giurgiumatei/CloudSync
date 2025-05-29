# CloudSync Performance Tests

This project contains a performance test implementation for stress testing the CloudSync API's `DataSyncController` endpoint.

## Overview

The performance tests send HTTP requests to the API at increasing rates (10, 100, and 1000 requests per second) and measure:
- Response time (average, min, max, and percentiles)
- Success rate
- Error rate

Results are logged to a CSV file for further analysis.

## Running the Tests

### Prerequisites

- [Docker](https://www.docker.com/products/docker-desktop) installed on your machine
- [Docker Compose](https://docs.docker.com/compose/install/) installed on your machine

### Using Docker Compose

1. Clone the repository
2. From the root directory, run:

```bash
docker-compose up -d
```

This will:
- Build and start the API container with SQL Server databases (Azure and AWS)
- Build and start the performance test container
- Mount a volume to store the test results

3. View the performance test logs:

```bash
docker-compose logs -f perftest
```

4. Results will be saved in the `./performance-results` directory on your host machine.

### Running the Performance Tests Manually

If you want to run the tests against a different API instance:

1. Navigate to the `CloudSync.PerformanceTests` directory

2. Run the tests, specifying the API URL:

```bash
dotnet run --API_BASE_URL=http://your-api-address
```

## Docker Containers

The solution includes:

- `api`: The CloudSync API container
- `perftest`: The performance test container
- `azure-db`: SQL Server container for Azure database
- `aws-db`: SQL Server container for AWS database

## Configuration

You can modify the test parameters in `CloudSync.PerformanceTests/CustomPerformanceTest.cs`:

- `_requestRates`: Array of request rates to test (requests per second)
- `_testDuration`: Duration for each test

## Results

The test results are saved in two formats:

1. CSV file with detailed metrics for each test run:
   - Request rate
   - Timestamp
   - Total requests
   - Success count
   - Error count
   - Success/error rates
   - Response time metrics (avg, min, max, P50, P90, P95, P99)

2. Summary text file with the latest results in a human-readable format.

Both files are saved to the `/app/results` directory in the container, which is mounted to `./performance-results` on the host.