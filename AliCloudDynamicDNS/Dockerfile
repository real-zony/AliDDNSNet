FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine3.18-amd64 AS builder
WORKDIR /build
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /publish

FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine3.18-amd64 AS runtime
WORKDIR /app
COPY --from=builder /publish .

ENTRYPOINT ["dotnet","/app/AliCloudDynamicDNS.dll","-i","30"]