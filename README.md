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

发布工作流将镜像推送到 `ghcr.io/real-zony/ali-cloud-dynamic-dns`，镜像标签与选定的 Release tag 一致。使用已发布镜像前确认对应标签存在；本地构建不会自动推送。

多架构构建使用 Buildx，支持 `linux/amd64` 和 `linux/arm64`。SDK 构建阶段运行在构建机原生架构，再按目标架构交叉发布，不依赖 QEMU 执行 .NET SDK。容器使用框架依赖的 DLL 部署，与二进制分发的自包含单文件发布分开。

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

### 二进制打包

需要 Bash 和上述 .NET SDK。ZIP 由 SDK 内置的 MSBuild `ZipDirectory` 生成，不再依赖外部 `zip` 命令。

```sh
# 显式传入版本；第二个参数可指定输出父目录
bash ./scripts/build.sh v0.1.0
bash ./scripts/build.sh v0.1.0 ./release-output
```

每次在输出父目录下创建独立的 `release-<版本>.<随机后缀>` 目录，并打印其路径，不清理其他用户目录。生成 win-x64、linux-x64、osx-x64、linux-arm、linux-arm64 五种裁剪、自包含、单文件 ZIP。包内保留 `settings.json.example`，不复制真实 `settings.json`。

Windows 生成的 ZIP 不携带 Unix 执行权限；在 Linux/macOS 解压后先执行 `chmod +x AliCloudDynamicDNS`。Ubuntu 发布工作流保留源文件的执行位，并实际解压后运行冒烟检查；本机 Windows 交叉打包不能替代这项原生验证。

对本机可执行的发布/解压目录，可运行发布入口冒烟检查（需要 PowerShell 7）：

```sh
pwsh ./scripts/smoke-publish.ps1 -PublishDirectory ./path/to/publish
```

该检查覆盖真实入口、配置读取、错误退出和敏感信息排除，不代表已验证线上 DNS 写入或全部目标平台。

### 运行环境边界

自包含包不要求另装 .NET 运行时，但操作系统和本机库仍需符合 [.NET 10 支持要求](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)。尤其注意 Linux Arm32 需要 Y2038 兼容的 glibc，基线为 2.35；Linux x64/Arm64 的 glibc 基线为 2.27。相同 RID 不代表所有旧群晖、路由器或 NAS 系统都能继续直接运行。

CI 定义了 Windows x64、Linux x64/Arm64、Intel macOS 原生产物冒烟；Arm32 仅交叉发布。交叉发布成功不等于已经在目标设备运行成功。

## 5. 下载

[GitHub Releases](https://github.com/real-zony/AliDDNSNet/releases)。下载与你的系统和架构匹配的产物；只有包含本次改动的新版本才具备上述行为，旧发布包不会因源码更新而自动修复。
