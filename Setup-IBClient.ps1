# IBClient Setup Script
# This script helps configure the local IBClient path for the project

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "🔧 IBClient Setup Required" -ForegroundColor Yellow
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "This project requires the Interactive Brokers API v10.30" -ForegroundColor White
Write-Host ""
Write-Host "📥 Please download and install IB API v10.30 from:" -ForegroundColor Green
Write-Host "   https://interactivebrokers.github.io/" -ForegroundColor Cyan
Write-Host ""
Write-Host "💡 After installation, locate the CSharpClient\client directory" -ForegroundColor Green
Write-Host ""

# Check if config already exists
if (Test-Path "IBClientConfig.json") {
    $existingConfig = Get-Content "IBClientConfig.json" | ConvertFrom-Json
    Write-Host "⚠️  IBClient is already configured:" -ForegroundColor Yellow
    Write-Host "   Current path: $($existingConfig.IBClientPath)" -ForegroundColor Gray
    Write-Host ""
    $reconfigure = Read-Host "Do you want to reconfigure? (y/N)"
    if ($reconfigure -ne "y" -and $reconfigure -ne "Y") {
        Write-Host "✅ Setup cancelled. Using existing configuration." -ForegroundColor Green
        exit 0
    }
}

Write-Host "📁 Please enter the full path to your IBClient directory:" -ForegroundColor White
Write-Host "   Examples:" -ForegroundColor Gray
Write-Host "   Windows: C:\TWS API\source\CSharpClient\client" -ForegroundColor Gray
Write-Host "   WSL:     /mnt/c/TWS API/source/CSharpClient/client" -ForegroundColor Gray
Write-Host ""

do {
    $ibPath = Read-Host "IBClient Path"
    
    if ([string]::IsNullOrWhiteSpace($ibPath)) {
        Write-Host "❌ Path cannot be empty!" -ForegroundColor Red
        continue
    }
    
    # Validate path exists
    if (-not (Test-Path $ibPath)) {
        Write-Host "❌ Path does not exist: $ibPath" -ForegroundColor Red
        Write-Host "   Please check the path and try again." -ForegroundColor Yellow
        continue
    }
    
    # Check for key IBClient files
    $requiredFiles = @("EClient.cs", "EWrapper.cs", "Contract.cs")
    $missingFiles = @()
    
    foreach ($file in $requiredFiles) {
        if (-not (Test-Path (Join-Path $ibPath $file))) {
            $missingFiles += $file
        }
    }
    
    if ($missingFiles.Count -gt 0) {
        Write-Host "❌ IBClient directory appears invalid. Missing files:" -ForegroundColor Red
        foreach ($file in $missingFiles) {
            Write-Host "   - $file" -ForegroundColor Red
        }
        Write-Host "   Please ensure you're pointing to the correct CSharpClient\client directory." -ForegroundColor Yellow
        continue
    }
    
    # Path is valid
    break
    
} while ($true)

# Create configuration
$config = @{
    IBClientPath = $ibPath
    SetupCompleted = $true
    RequiredVersion = "10.30"
    LastUpdated = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss")
}

$configJson = $config | ConvertTo-Json -Depth 2
$configJson | Out-File -FilePath "IBClientConfig.json" -Encoding UTF8

Write-Host ""
Write-Host "✅ IBClient configuration completed successfully!" -ForegroundColor Green
Write-Host "   Configuration saved to: IBClientConfig.json" -ForegroundColor Gray
Write-Host ""
Write-Host "🚀 You can now build the project with:" -ForegroundColor Cyan
Write-Host "   dotnet build" -ForegroundColor White
Write-Host ""
