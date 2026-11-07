ARG VERSION=0.0.5
ARG REVISION=

# Only Alpine package tools run on the target platform (qemu for Arm32).
FROM --platform=$TARGETPLATFORM alpine:3.24 AS sysroot
RUN apk add --no-cache gcc musl-dev zlib-dev

# Restore, IL compilation and clang/lld always execute on the build host, never qemu.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.403-alpine3.24-aot AS builder
ARG TARGETOS
ARG TARGETARCH
ARG TARGETVARIANT
ARG VERSION
RUN apk add --no-cache lld llvm \
    && command -v clang && command -v ld.lld && command -v llvm-objcopy
COPY --from=sysroot /lib/ /crossrootfs/lib/
COPY --from=sysroot /usr/lib/ /crossrootfs/usr/lib/
COPY --from=sysroot /usr/include/ /crossrootfs/usr/include/
COPY --from=sysroot /etc/os-release /crossrootfs/etc/os-release
WORKDIR /build
COPY . .
RUN set -eu; \
    case "$TARGETOS/$TARGETARCH/${TARGETVARIANT:-}" in \
      linux/amd64/) rid=linux-musl-x64; triple=x86_64-alpine-linux-musl ;; \
      linux/arm64/|linux/arm64/v8) rid=linux-musl-arm64; triple=aarch64-alpine-linux-musl ;; \
      linux/arm/v7) rid=linux-musl-arm; triple=armv7-alpine-linux-musleabihf ;; \
      *) echo "Unsupported target platform: $TARGETOS/$TARGETARCH/${TARGETVARIANT:-}" >&2; exit 1 ;; \
    esac; \
    test -d "/crossrootfs/usr/lib/gcc/$triple"; \
    test -f /crossrootfs/usr/lib/crti.o; \
    test -f /crossrootfs/usr/lib/libc.so; \
    test -f /crossrootfs/usr/lib/libz.so; \
    set -- --runtime "$rid" -p:PublishProfile=NativeAot -p:Version="$VERSION" \
      -p:SysRoot=/crossrootfs -p:LinkerFlavor=lld -p:ObjCopyName=llvm-objcopy \
      -p:InvariantGlobalization=false; \
    dotnet restore ./AliCloudDynamicDNS.csproj "$@"; \
    dotnet publish ./AliCloudDynamicDNS.csproj --no-restore --output /publish "$@"; \
    test -x /publish/AliCloudDynamicDNS; \
    test -f /publish/settings.json.example; \
    test ! -e /publish/settings.json

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.14-alpine3.24-extra AS runtime
ARG VERSION
ARG REVISION
LABEL org.opencontainers.image.source="https://github.com/real-zony/AliDDNSNet" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$REVISION"
WORKDIR /app
COPY --from=builder /publish/AliCloudDynamicDNS /publish/settings.json.example ./
ENTRYPOINT ["/app/AliCloudDynamicDNS"]
CMD ["-i","30"]
