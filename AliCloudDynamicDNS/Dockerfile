FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.403-alpine3.24 AS builder
ARG TARGETARCH
ARG VERSION=0.0.5
WORKDIR /build
COPY AliCloudDynamicDNS.csproj .
RUN case "$TARGETARCH" in \
      amd64) rid=linux-musl-x64 ;; \
      arm64) rid=linux-musl-arm64 ;; \
      *) echo "Unsupported target architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac \
    && dotnet restore ./AliCloudDynamicDNS.csproj --runtime "$rid" -p:UseAppHost=false
COPY . .
RUN case "$TARGETARCH" in \
      amd64) rid=linux-musl-x64 ;; \
      arm64) rid=linux-musl-arm64 ;; \
      *) echo "Unsupported target architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac \
    && dotnet publish ./AliCloudDynamicDNS.csproj \
      --configuration Release --framework net10.0 --runtime "$rid" \
      --no-restore --output /publish --self-contained false \
      -p:Version="$VERSION" -p:UseAppHost=false \
      -p:PublishSingleFile=false -p:PublishTrimmed=false

FROM mcr.microsoft.com/dotnet/runtime:10.0.14-alpine3.24 AS runtime
WORKDIR /app
COPY --from=builder /publish .
ENTRYPOINT ["dotnet","/app/AliCloudDynamicDNS.dll","-i","30"]