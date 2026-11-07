# AliDDNSNet

AliCloudDynamicDNS 是基于 .NET 10 的阿里云动态 DNS 工具。它通过公网地址源获取当前 IP，更新已有的 A / AAAA 记录，支持单次执行、周期运行和终端交互。

## 1. 配置

复制发布包中的 `settings.json.example` 为 `settings.json`，填入自己的凭据和域名。源码中的示例位于 `AliCloudDynamicDNS/settings.json.example`。

下面是同时更新 `home.example.com` 的 IPv4 和 IPv6 的配置；只需要 IPv4 时，删除 AAAA 条目即可。

```jsonc
{
  "AccessId": "your-access-id",
  "AccessKey": "your-access-key",
  "MainDomain": "example.com",
  "PublicIpServer": "https://api.ipify.org",
  "PublicIpv6Server": "https://api6.ipify.org",
  "SubDomains": [
    { "Type": "A", "SubDomain": "home", "Interval": 600 },
    { "Type": "AAAA", "SubDomain": "home", "Interval": 600 }
  ]
}
```

- `AccessId` / `AccessKey`：阿里云凭据。建议使用仅有目标域名解析查询、更新权限的 RAM 用户；不要把真实配置提交到仓库。
- `MainDomain`：主域名，例如 `example.com`。
- `SubDomains`：需要更新的**已有**记录。`Type` 只支持 A / AAAA，忽略大小写；`SubDomain` 填 RR，例如 `home`、`@`、`*` 或 `a.b`。
- `SubDomains[].Interval`：更新记录时使用的 DNS TTL，单位秒，须符合阿里云账号/解析版本的限制；它不是程序检测周期。
- 配置接受 JSON 注释、尾随逗号和大小写不敏感的属性名。未知属性被忽略；无效配置会在更新前报错。
- 默认使用 UTF-8；旧文件带有 BOM 时，也支持 UTF-16 / UTF-32 的大小端编码。普通 UTF-8 文件直接流式解析，其他 BOM 编码按需转码。

### 地址源与旧配置兼容

地址源应返回一个纯文本 IP 地址，而不是 JSON 或 HTML。首尾空白会被去掉，返回地址必须符合目标记录的地址族。

| 记录类型 | 地址源选择顺序 |
| --- | --- |
| A | 显式 `PublicIpServer`；没有配置时使用 `https://api.ipify.org` |
| AAAA | 显式 `PublicIpv6Server`；其次是显式 `PublicIpServer`；两者都没配置时使用 `https://api6.ipify.org` |

旧配置通过 `PublicIpServer` 指定 IPv6 服务的用法仍有效。混合 A/AAAA 时，如果通用来源只返回 IPv4，需要另外填写 `PublicIpv6Server`，程序不会在地址族不符时偷偷换源。

只配置 A 就不会访问 IPv6 地址源；只配置 AAAA 就不会获取 IPv4 地址。使用 IPv6 专用公网服务需要可用的 IPv6 网络和 DNS 解析，同时仍需能访问阿里云 API。使用代理时，地址源可能看到代理出口，请确认返回的是你希望写入 DNS 的地址。

### 更新规则

- 按“RR + 类型”匹配，区分同名 A 和 AAAA，忽略名称大小写，并读取完整分页结果。
- IP 不变时不调用更新接口；IPv6 的等价缩写视为同一个地址。
- 更新保留已有解析线路；不自动创建、删除或启用记录。
- 找不到记录，或者同名同类型存在多条记录（例如不同线路）时，明确报错，不任意选择目标。
- 某个地址族、某条记录失败，不阻止其他目标继续处理；单次运行有任意目标失败，最终返回非零退出码。
- 每次 HTTP 请求的超时覆盖响应正文；周期模式的失败会在下一正常周期再尝试，不额外增加重试循环。

## 2. 命令行

Linux/macOS：

```sh
# 解压后确保执行权限，尤其是从 Windows 交叉打包的 ZIP
chmod +x ./AliCloudDynamicDNS

# 默认从当前工作目录读取 settings.json，执行一轮后退出
./AliCloudDynamicDNS

# 指定配置文件，执行一轮
./AliCloudDynamicDNS -f ./settings.json

# 周期运行：启动立即执行，之后每轮完成后等待 3600 秒
./AliCloudDynamicDNS -f ./settings.json -i 3600

./AliCloudDynamicDNS --help
```

Windows 使用 `AliCloudDynamicDNS.exe`，参数相同：

```powershell
.\AliCloudDynamicDNS.exe -f .\settings.json -i 3600
```

| 参数 | 含义 |
| --- | --- |
| `-f`, `--file` | 配置文件路径；默认是当前工作目录的 `settings.json` |
| `-i`, `--interval` | 自动检测周期，单位秒；0 或省略表示单次运行，非零必须至少 30 秒 |
| `-h`, `--help` | 帮助，不读取配置或访问网络 |

定时与手动更新不会并发写 DNS。周期模式在输入关闭后仍继续运行；Docker、任务调度器和重定向场景不需要 TTY。

退出状态：成功（包括无需更新）为 0，参数/配置/更新失败为非零，Ctrl+C 等取消为 130。交互命令 `exit` 正常退出，返回 0。

### 终端显示与交互

交互终端使用 Spectre.Console 表格展示配置和结果，区分已更新、无需更新、失败；AccessId 脱敏，AccessKey 不显示。

周期模式下支持以下文本命令，不区分大小写：

- `ip`：查看配置涉及的 IPv4 / IPv6 地址，不写 DNS。
- `config`：查看配置摘要。
- `update`：立即重新检查并更新；IP 不变仍会跳过写入。
- `cls`：清屏并重新显示配置。
- `exit`：结束运行。也可以使用 Ctrl+C。

输出到文件/管道时为逐行纯文本，不输出光标控制和交互菜单；设置 `NO_COLOR` 可禁用富样式。Linux 后台运行示例：

```sh
./AliCloudDynamicDNS -f ./settings.json -i 3600 > ddns.log 2>&1 &
```

## 3. Docker

从当前源码构建本地镜像：

```sh
docker build -f AliCloudDynamicDNS/Dockerfile -t ali-ddnsnet:local AliCloudDynamicDNS
```

把填写好的配置作为只读文件挂载，后台运行无需 `-t`：

```sh
docker run -d --name aliyun-ddns --restart unless-stopped \
  --mount type=bind,src=/absolute/path/settings.json,dst=/app/settings.json,readonly \
  ali-ddnsnet:local
```

将 `/absolute/path/settings.json` 换成宿主机配置文件的绝对路径。镜像不包含真实配置，默认以 `-i 30` 运行。

Docker Compose 示例，先把配置放到仓库根目录的 `settings.json`：

```yaml
services:
  ddns:
    build:
      context: ./AliCloudDynamicDNS
      dockerfile: Dockerfile
    image: ali-ddnsnet:local
    container_name: aliyun-ddns
    volumes:
      - ./settings.json:/app/settings.json:ro
    restart: unless-stopped
```

发布工作流使用仓库的 `GITHUB_TOKEN` 推送 GHCR，不需要配置 `DOCKER_USERNAME` / `DOCKER_PASSWORD`。镜像地址按仓库所有者生成，当前为 `ghcr.io/real-zony/ali-cloud-dynamic-dns:<Release tag>`。同一个标签包含 `linux/amd64`、`linux/arm64`、`linux/arm/v7` 三种架构，Docker 自动选择；不会自动更新 `latest`。使用前确认对应标签已经发布；本地构建不会自动推送。

镜像使用 Native AOT 可执行文件和带 ICU 的 Alpine `runtime-deps`，不再通过 `dotnet ...dll` 启动。SDK、ILCompiler、clang/lld 始终在构建机原生 CPU 上执行，使用目标架构 sysroot 链接。CI 的 amd64、arm64 各用原生 runner；Arm32 的目标系统包安装和最终程序冒烟使用 QEMU，不模拟运行 .NET SDK。

每种架构只构建一次：加载本地镜像，检查平台、ELF、版本和源码提交，再在禁网容器中实际测试帮助、缺失配置和嵌套配置校验。测试通过后才推送相同 image ID；三种架构全部成功后，按不可变 digest 合成并检查 Release 标签。中间架构镜像使用独立的 `build-...` 标签，不会提前改动正式标签。

如果同名 GHCR 包以前由 PAT 手工创建，需要在包设置的 **Manage Actions access** 中授予本仓库写权限；工作流仅在发布任务申请 `packages: write`。PR 和普通 CI 不登录、不推送镜像。

## 4. 构建与测试

安装 .NET SDK 10.0.403（`global.json` 允许同一 feature band 的更新补丁，不使用预览版）。

```sh
dotnet restore AliCloudDynamicDNS.sln
dotnet build AliCloudDynamicDNS.sln -c Release
dotnet test AliCloudDynamicDNS.sln -c Release --collect:"XPlat Code Coverage;Format=opencover" --results-directory TestResults
```

测试采用 xUnit v3 的 VSTest 变体与单一 Coverlet collector；不混用 MTP 或旧的 `/p:CollectCoverage` 接入。测试使用受控 HTTP、假时间和临时文件，不需要真实阿里云凭据。

从仓库根目录运行构建后的应用：

```sh
dotnet ./AliCloudDynamicDNS/bin/Release/net10.0/AliCloudDynamicDNS.dll -f ./settings.json
```

### Native AOT 二进制打包

使用原生 PowerShell 7、上述 .NET SDK，以及目标系统的本机工具链：Windows 需要 Visual Studio 2022 或更新版本的 MSVC x64/x86 和 Windows SDK；Linux 需要 clang、LLVM 及目标系统开发库；macOS 需要 Xcode Command Line Tools。CI 自动准备 Linux 工具链。AOT/裁剪警告按错误处理，不回退到 CoreCLR。

| RID | CI 构建环境 | 归档 | 运行验证 |
| --- | --- | --- | --- |
| `win-x64` | `windows-2025` | ZIP | 原生单测及归档冒烟 |
| `linux-x64` | `ubuntu-24.04` | tar.gz | 原生单测及归档冒烟 |
| `linux-arm64` | `ubuntu-24.04-arm` | tar.gz | 原生单测及归档冒烟 |
| `linux-arm` | Ubuntu x64 + armhf 交叉工具链 | tar.gz | QEMU 执行实际 Arm32 归档，不是真机单测 |
| `osx-x64` | `macos-15-intel` | tar.gz | 原生单测及归档冒烟 |
| `osx-arm64` | `macos-15` | tar.gz | 原生单测及归档冒烟 |

在对应系统和 CPU 上运行：

```powershell
# Linux x64 示例；Windows 使用 win-x64，Apple Silicon 使用 osx-arm64
$published = ./scripts/publish.ps1 -RuntimeIdentifier linux-x64 -Version v0.1.0
$published.AotArchive
```

`-Version` 默认取项目版本，`-OutputParent` 默认 `TempFiles`，`-DotnetPath` 可指定 SDK 可执行文件。脚本每次创建独立输出目录，不清理其他目录；归档名为 `AliCloudDynamicDNS_<RID>_v<版本>.zip` / `.tar.gz`。包内包含示例配置及平台调试符号，不包含真实 `settings.json`。

不支持一台机器跨操作系统生成 AOT。Linux Arm32 可在 Linux x64/Arm64 上配齐交叉工具链后，通过 `-Launcher qemu-arm -LauncherArguments ...` 执行冒烟；Arm32 发布默认显式选择 `lld` 交叉链接，避免误用宿主机仅支持 x64 的 `ld.bfd`。CI 的 Ubuntu 24.04 armhf 依赖和启动参数见 `scripts/install-linux-aot-deps.sh` 与 `native-aot.yaml`。

脚本先运行发布目录，再打包、解压并运行最终归档。Unix 使用 tar.gz 保留执行权限和 macOS dSYM 目录。也可单独执行：

```sh
pwsh ./scripts/smoke-publish.ps1 -PublishDirectory ./path/to/extracted-release
```

冒烟覆盖真实入口、配置/BOM 读取、错误退出和敏感信息排除，不写线上 DNS。模拟运行不能替代对应硬件验证。

### Windows AOT 与 CoreCLR 对比

Windows 的可选基线保留原来的裁剪、自包含、单文件部署，仅用于同机对比；常规发版使用 Native AOT。

```powershell
# 同一份源码同时生成 AOT 和现有 CoreCLR 基线；版本默认取项目 Version
$published = ./scripts/publish.ps1 -RuntimeIdentifier win-x64 -IncludeBaseline

# 若 SDK 安装在用户目录，可在上述命令加上：
# -DotnetPath "$HOME/.dotnet/dotnet.exe"

# 对最终 EXE 做同机对比，不重新构建
./scripts/compare-windows-publish.ps1 `
  -BaselineDirectory $published.BaselinePublishDirectory `
  -AotDirectory $published.AotPublishDirectory `
  -Iterations 100
```

`-IncludeBaseline` 仅支持 `win-x64`，返回两套发布目录及已解压冒烟的 ZIP；不加时只生成 AOT。两种 EXE 都可直接运行，不要求目标机另装 .NET。CI 的 Windows 检查也保留这条基线路径。

基线参数为 Release、自包含、裁剪、单文件、原生库自解压。比较脚本默认每场景每种模式预热 5 次、测量 40 次，交替启动两种 EXE；场景为 `--help` 和嵌套配置校验，不使用真实凭据或访问阿里云。`comparison.json` 保留环境、文件哈希、原始样本和 median/p95，分别统计 EXE、部署文件、PDB、总目录及同算法 ZIP 大小。

这是**热文件缓存下的新进程启动/CLI 测量**，不是磁盘冷启动、DNS 吞吐量或常驻进程内存测试。内存指标是进程峰值工作集；短进程 CPU 计时受 Windows 统计粒度影响，0 不代表没有 CPU 消耗。PDB 不参与运行，比较部署体积时应与包含符号的总大小分开看。

### CI/CD 触发与发版

- `unit-test.yaml`：PR 到 `main`、push 到 `main` 时调用共享检查；五种原生环境跑单测，六种二进制做最终归档冒烟，三种容器做运行冒烟。只有 Linux x64 生成并上传一份覆盖率报告。
- `docker-image.yaml`：发布 GitHub Release，或手动指定已有 Release 的 tag 时触发。先把 tag 解析为固定 commit，所有二进制和镜像统一 checkout 该 commit；六份已验证归档上传到该 Release，三种已验证镜像合成对应 GHCR 标签。
- `native-aot.yaml` / `container-aot.yaml`：CI 与发版复用同一套构建、运行检查；任何必需架构失败都不会被忽略。相同 tag 的发布串行执行。

手动重发的目标 tag 必须包含这套新脚本；不会拿当前分支的程序冒充旧 tag，也不会自动创建 Release。以上是流程中的验收门槛，只有对应 GitHub Actions 运行成功，才代表那次提交在目标 runner 上验证通过。

### 运行环境边界

AOT 不要求另装 .NET，但仍依赖操作系统、本机库、证书及全球化数据。Linux 二进制使用 glibc，Alpine 镜像使用 musl，不能直接互换。实际最低兼容环境由构建机/目标 sysroot 决定，不能仅凭 RID 或 [.NET 10 支持要求](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)推断旧系统可运行。

当前 Linux 二进制流程基于 Ubuntu 24.04，Arm32 使用同发行版的 Y2038 兼容 armhf 库；不保证旧群晖、路由器或 NAS 兼容。macOS 分别在 Intel/Apple Silicon runner 上验证，未配置 Developer ID 签名或公证。Arm32 的 QEMU 冒烟明确属于模拟执行，不是真机验证。

## 5. 下载

[GitHub Releases](https://github.com/real-zony/AliDDNSNet/releases)。下载与你的系统和架构匹配的产物；只有包含本次改动的新版本才具备上述行为，旧发布包不会因源码更新而自动修复。
