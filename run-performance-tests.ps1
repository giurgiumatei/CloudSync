# CloudSync Performance Testing Script for Windows
# This script sets up and runs comprehensive performance tests

param(
    [switch]$Monitoring,
    [switch]$Cleanup,
    [string]$Duration = "60",
    [switch]$Help
)

# Colors for output
$ErrorColor = "Red"
$SuccessColor = "Green"
$WarningColor = "Yellow"
$InfoColor = "Cyan"

# Configuration
$ResultsDir = "./performance-results"
$TestDuration = $Duration

function Show-Usage {
    Write-Host "Usage: .\run-performance-tests.ps1 [OPTIONS]" -ForegroundColor $InfoColor
    Write-Host "Options:" -ForegroundColor $InfoColor
    Write-Host "  -Monitoring         Enable Prometheus monitoring" -ForegroundColor $InfoColor
    Write-Host "  -Cleanup           Cleanup containers after tests" -ForegroundColor $InfoColor
    Write-Host "  -Duration SEC      Test duration per round (default: 60s)" -ForegroundColor $InfoColor
    Write-Host "  -Help             Show this help message" -ForegroundColor $InfoColor
    exit 0
}

function Show-Banner {
    Write-Host "╔══════════════════════════════════════════════════════════════╗" -ForegroundColor $InfoColor
    Write-Host "║                CloudSync Performance Tests                   ║" -ForegroundColor $InfoColor
    Write-Host "║            High-Load Testing: 1 to 1,000,000 RPS           ║" -ForegroundColor $InfoColor
    Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor $InfoColor
}

function Write-InfoLog {
    param([string]$Message)
    Write-Host "[INFO] $Message" -ForegroundColor $SuccessColor
}

function Write-WarningLog {
    param([string]$Message)
    Write-Host "[WARNING] $Message" -ForegroundColor $WarningColor
}

function Write-ErrorLog {
    param([string]$Message)
    Write-Host "[ERROR] $Message" -ForegroundColor $ErrorColor
}

function Test-Prerequisites {
    Write-InfoLog "Checking prerequisites..."
    
    # Check Docker
    try {
        $dockerVersion = docker --version
        if (-not $dockerVersion) {
            throw "Docker not found"
        }
        Write-InfoLog "Docker: $dockerVersion"
    }
    catch {
        Write-ErrorLog "Docker is not installed or not in PATH"
        exit 1
    }
    
    # Check Docker Compose
    try {
        $composeVersion = docker-compose --version
        if (-not $composeVersion) {
            throw "Docker Compose not found"
        }
        Write-InfoLog "Docker Compose: $composeVersion"
    }
    catch {
        Write-ErrorLog "Docker Compose is not installed or not in PATH"
        exit 1
    }
    
    # Check system resources
    $totalMemoryGB = [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB, 1)
    $cpuCores = (Get-CimInstance Win32_ComputerSystem).NumberOfLogicalProcessors
    
    if ($totalMemoryGB -lt 8) {
        Write-WarningLog "System has less than 8GB RAM ($totalMemoryGB GB). High-load tests may be limited."
    }
    
    if ($cpuCores -lt 4) {
        Write-WarningLog "System has less than 4 CPU cores ($cpuCores cores). Performance may be limited."
    }
    
    Write-InfoLog "System resources: $cpuCores CPU cores, $totalMemoryGB GB RAM"
}

function Setup-Environment {
    Write-InfoLog "Setting up test environment..."
    
    # Create results directory
    if (-not (Test-Path $ResultsDir)) {
        New-Item -ItemType Directory -Path $ResultsDir -Force | Out-Null
    }
    
    if (-not (Test-Path "./monitoring")) {
        New-Item -ItemType Directory -Path "./monitoring" -Force | Out-Null
    }
    
    Write-InfoLog "Optimizing Docker settings for high-load testing..."
    
    # Stop any existing containers
    try {
        docker-compose down --remove-orphans 2>$null
    } catch {}
    
    # Clean up any leftover containers
    try {
        docker container prune -f 2>$null
        docker network prune -f 2>$null
    } catch {}
    
    # Build images
    Write-InfoLog "Building Docker images..."
    docker-compose build --no-cache
    if ($LASTEXITCODE -ne 0) {
        Write-ErrorLog "Failed to build Docker images"
        exit 1
    }
    
    # Start infrastructure services first
    Write-InfoLog "Starting database services..."
    docker-compose up -d azure-db aws-db
    if ($LASTEXITCODE -ne 0) {
        Write-ErrorLog "Failed to start database services"
        exit 1
    }
    
    # Wait for databases to be ready
    Write-InfoLog "Waiting for databases to be ready..."
    $timeout = 120
    while ($timeout -gt 0) {
        $azureStatus = docker-compose ps azure-db | Select-String "healthy"
        $awsStatus = docker-compose ps aws-db | Select-String "healthy"
        
        if ($azureStatus -and $awsStatus) {
            Write-InfoLog "Databases are ready!"
            break
        }
        Start-Sleep -Seconds 2
        $timeout -= 2
    }
    
    if ($timeout -eq 0) {
        Write-ErrorLog "Databases failed to start within timeout"
        exit 1
    }
    
    # Start API service
    Write-InfoLog "Starting API service..."
    docker-compose up -d api
    if ($LASTEXITCODE -ne 0) {
        Write-ErrorLog "Failed to start API service"
        exit 1
    }
    
    # Wait for API to be ready
    Write-InfoLog "Waiting for API service to be ready..."
    $timeout = 60
    while ($timeout -gt 0) {
        try {
            $response = Invoke-WebRequest -Uri "http://localhost:5000/api/healthcheck/test-connections" -TimeoutSec 3 -ErrorAction SilentlyContinue
            if ($response.StatusCode -eq 200) {
                Write-InfoLog "API service is ready!"
                break
            }
        }
        catch {}
        Start-Sleep -Seconds 2
        $timeout -= 2
    }
    
    if ($timeout -eq 0) {
        Write-ErrorLog "API service failed to start within timeout"
        exit 1
    }
}

function Start-Monitoring {
    if ($Monitoring) {
        Write-InfoLog "Starting monitoring services..."
        docker-compose --profile monitoring up -d monitoring
        Write-InfoLog "Prometheus monitoring available at http://localhost:9090"
    }
}

function Start-PerformanceTests {
    Write-InfoLog "Starting performance tests..."
    Write-InfoLog "Test configuration:"
    Write-InfoLog "  - Range: 1 to 1,000,000 RPS"
    Write-InfoLog "  - Duration per round: $TestDuration seconds"
    Write-InfoLog "  - Results directory: $ResultsDir"
    
    # Start performance test container
    docker-compose up --no-deps perftest
    $exitCode = $LASTEXITCODE
    
    if ($exitCode -eq 0) {
        Write-InfoLog "Performance tests completed successfully!"
        
        # Show results summary
        $summaryFile = "$ResultsDir/latest_results_summary.txt"
        if (Test-Path $summaryFile) {
            Write-Host "Latest Results Summary:" -ForegroundColor $InfoColor
            Write-Host "========================" -ForegroundColor $InfoColor
            Get-Content $summaryFile | Select-Object -Last 20
        }
        
        # List all generated reports
        Write-Host "Generated Reports:" -ForegroundColor $InfoColor
        Get-ChildItem $ResultsDir -Include "*.csv", "*.html", "*.md" -Recurse | ForEach-Object {
            Write-Host "  - $($_.FullName)" -ForegroundColor $InfoColor
        }
        
    } else {
        Write-ErrorLog "Performance tests failed with exit code $exitCode"
        return $false
    }
    
    return $true
}

function New-SummaryReport {
    Write-InfoLog "Generating summary report..."
    
    # Find the latest CSV results file
    $latestCsv = Get-ChildItem $ResultsDir -Filter "performance_results_*.csv" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    
    if ($latestCsv) {
        $summaryFile = "$ResultsDir/test_summary_$(Get-Date -Format 'yyyyMMdd_HHmmss').txt"
        
        $summary = @"
CloudSync Performance Test Summary
==================================
Generated: $(Get-Date)
Results file: $($latestCsv.FullName)

Key Metrics:
============
Total test rounds: $(((Get-Content $latestCsv.FullName | Measure-Object).Count - 1))

Files generated:
$(Get-ChildItem $ResultsDir -Recurse | Where-Object { $_.LastWriteTime -gt $latestCsv.LastWriteTime } | ForEach-Object { $_.FullName } | Sort-Object)
"@
        
        $summary | Out-File -FilePath $summaryFile -Encoding UTF8
        Write-InfoLog "Summary report saved to: $summaryFile"
    }
}

function Stop-Environment {
    if ($Cleanup) {
        Write-InfoLog "Cleaning up containers..."
        docker-compose down --volumes --remove-orphans
        docker system prune -f
    } else {
        Write-InfoLog "Stopping containers (use -Cleanup to remove them)..."
        docker-compose stop
    }
}

# Main execution
if ($Help) {
    Show-Usage
}

try {
    Show-Banner
    
    Test-Prerequisites
    Setup-Environment
    Start-Monitoring
    
    # Run the actual performance tests
    if (Start-PerformanceTests) {
        New-SummaryReport
        
        Write-InfoLog "Performance testing completed successfully!"
        Write-InfoLog "Results are available in: $ResultsDir"
        
        if ($Monitoring) {
            Write-InfoLog "Monitoring dashboard: http://localhost:9090"
        }
        
    } else {
        Write-ErrorLog "Performance testing failed!"
        exit 1
    }
    
    Stop-Environment
    
} catch {
    Write-ErrorLog "An error occurred: $($_.Exception.Message)"
    Stop-Environment
    exit 1
} 