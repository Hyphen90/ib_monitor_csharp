#!/bin/bash

# IBClient Setup Script for Linux/WSL
# This script helps configure the local IBClient path for the project

echo "============================================"
echo "🔧 IBClient Setup Required"
echo "============================================"
echo ""
echo "This project requires the Interactive Brokers API v10.30"
echo ""
echo "📥 Please download and install IB API v10.30 from:"
echo "   https://interactivebrokers.github.io/"
echo ""
echo "💡 After installation, locate the CSharpClient/client directory"
echo ""

# Check if config already exists
if [ -f "IBClientConfig.json" ]; then
    existing_path=$(grep -o '"IBClientPath":"[^"]*"' IBClientConfig.json | cut -d'"' -f4)
    echo "⚠️  IBClient is already configured:"
    echo "   Current path: $existing_path"
    echo ""
    read -p "Do you want to reconfigure? (y/N): " reconfigure
    if [[ ! "$reconfigure" =~ ^[Yy]$ ]]; then
        echo "✅ Setup cancelled. Using existing configuration."
        exit 0
    fi
fi

echo "📁 Please enter the full path to your IBClient directory:"
echo "   Examples:"
echo "   WSL:     /mnt/c/TWS API/source/CSharpClient/client"
echo "   Linux:   /home/user/IBAPI/source/CSharpClient/client"
echo ""

while true; do
    read -p "IBClient Path: " ib_path
    
    if [ -z "$ib_path" ]; then
        echo "❌ Path cannot be empty!"
        continue
    fi
    
    # Validate path exists
    if [ ! -d "$ib_path" ]; then
        echo "❌ Path does not exist: $ib_path"
        echo "   Please check the path and try again."
        continue
    fi
    
    # Check for key IBClient files
    required_files=("EClient.cs" "EWrapper.cs" "Contract.cs")
    missing_files=()
    
    for file in "${required_files[@]}"; do
        if [ ! -f "$ib_path/$file" ]; then
            missing_files+=("$file")
        fi
    done
    
    if [ ${#missing_files[@]} -gt 0 ]; then
        echo "❌ IBClient directory appears invalid. Missing files:"
        for file in "${missing_files[@]}"; do
            echo "   - $file"
        done
        echo "   Please ensure you're pointing to the correct CSharpClient/client directory."
        continue
    fi
    
    # Path is valid
    break
done

# Create configuration
current_time=$(date '+%Y-%m-%d %H:%M:%S')
cat > IBClientConfig.json << EOF
{
  "IBClientPath": "$ib_path",
  "SetupCompleted": true,
  "RequiredVersion": "10.30",
  "LastUpdated": "$current_time"
}
EOF

echo ""
echo "✅ IBClient configuration completed successfully!"
echo "   Configuration saved to: IBClientConfig.json"
echo ""
echo "🚀 You can now build the project with:"
echo "   dotnet build"
echo ""
