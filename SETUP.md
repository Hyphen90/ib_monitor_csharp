# IBClient Setup Guide

This guide explains how to set up the Interactive Brokers API client for this project.

## Why This Setup is Required

For legal and licensing reasons, the Interactive Brokers API client code cannot be distributed with this project. Each developer must download and configure their own copy of the IB API.

## Step-by-Step Setup

### 1. Download Interactive Brokers API

1. Visit: https://interactivebrokers.github.io/
2. Download the **TWS API v10.30** (or latest version)
3. Extract/install the API to your preferred location

### 2. Locate the CSharpClient Directory

After extraction, find the CSharpClient/client directory. This contains the C# source files we need.

**Common locations:**
- **Windows:** `C:\TWS API\source\CSharpClient\client`
- **WSL:** `/mnt/c/TWS API/source/CSharpClient/client` 
- **Linux:** `/home/user/IBAPI/source/CSharpClient/client`

### 3. Run Setup Script

**Windows (PowerShell):**
```powershell
powershell -ExecutionPolicy Bypass -File Setup-IBClient.ps1
```

**Linux/WSL:**
```bash
./setup-ibclient.sh
```

### 4. Build Project

After setup completion:
```bash
dotnet build
```

## Manual Configuration

If you prefer manual configuration, create `IBClientConfig.json`:

```json
{
  "IBClientPath": "/path/to/your/CSharpClient/client",
  "SetupCompleted": true,
  "RequiredVersion": "10.30",
  "LastUpdated": "2024-01-07 10:00:00"
}
```

## Troubleshooting

### Build Error: "IBClient not configured"
- Run the setup script: `./setup-ibclient.sh` or `Setup-IBClient.ps1`
- Ensure you have downloaded the IB API v10.30

### Build Error: "IBClient path does not exist"
- Verify the path in `IBClientConfig.json` is correct
- Re-run the setup script to reconfigure

### Build Error: Missing IBClient files
- Ensure you're pointing to the `CSharpClient/client` directory, not the root
- The directory should contain files like `EClient.cs`, `EWrapper.cs`, `Contract.cs`

### Setup Script Not Found
- Ensure you have execution permissions: `chmod +x setup-ibclient.sh`
- For Windows, use PowerShell, not Command Prompt

## Files Created

The setup process creates:
- `IBClientConfig.json` - Local configuration (git-ignored)

## API Version Compatibility

This project is designed for IB API v10.30. Other versions may work but are not tested.

## Security Note

`IBClientConfig.json` is automatically added to `.gitignore` to prevent accidental commits of local paths.
