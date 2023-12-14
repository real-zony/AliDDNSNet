#!/bin/bash

# 从命令行参数获取版本号，而不是交互式输入
RELEASE_VERSION=$1
PROGRAM_NAME=AliCloudDynamicDNS
DOTNET_VERSION=8.0

Platforms=('win-x64' 'linux-x64' 'osx-x64' 'linux-arm' 'linux-arm64')

if ! [ -d './TempFiles' ]; then
    mkdir ./TempFiles
fi

rm -rf ./TempFiles/*

for platform in "${Platforms[@]}"; do
    dotnet publish -r "$platform" -c Release -p:PublishSingleFile=true -p:PublishTrimmed=true --self-contained true /p:useapphost=true -p:PublishWithAspNetCoreTargetManifest=false
    zip -r -j "./AliCloudDynamicDNS/bin/Release/net$DOTNET_VERSION/$platform/publish/${PROGRAM_NAME}_${platform}_$RELEASE_VERSION.zip" "./AliCloudDynamicDNS/bin/Release/net$DOTNET_VERSION/$platform/publish/"*
    mv "./AliCloudDynamicDNS/bin/Release/net$DOTNET_VERSION/$platform/publish/${PROGRAM_NAME}_${platform}_$RELEASE_VERSION.zip" ./TempFiles
done
