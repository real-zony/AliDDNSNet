## 0.简要介绍

AliDDNSNet 是基于 .NET Core 开发的动态 DNS 解析工具，借助于阿里云的 DNS API 来实现域名与动态 IP 的绑定功能。这样你随时就可以通过域名来访问你的设备，而不需要担心 IP 变动的问题。

## 1.使用说明

使用本工具的时候，请详细阅读使用说明。

### 1.1 配置说明

通过更改 ```settings.json.example``` 的内容来实现 DDNS 更新，其文件内部各个选项的说明如下：

```json
{
  // 阿里云的 Access Id。
  "AccessId": "AccessId",
  // 阿里云的 Access Key。
  "AccessKey": "AccessKey",
  // 主域名。
  "MainDomain": "example.com",
  // 公网 IP 获取服务器地址。
  "PublicIpServer": "https://api.myzony.com/get-ip",
  // 需要批量变更的子域名记录集合。
  "SubDomains": [
    {
      // 子域名记录类型。
      "Type": "A",
      // 子域名记录前缀。
      "SubDomain": "sub1",
      // TTL 时间。
      "Interval": 600
    },
    {
      "Type": "A",
      "SubDomain": "sub2",
      "Interval": 600
    }
  ]
}
```

其中 ```Access Id``` 与 ```Access Key``` 可以登录阿里云之后在右上角可以得到。

### 1.2 命令参数说明

在运行程序的时候，请建立一个新的 ```settings.json``` 文件，在里面填入以上配置内容，然后执行以下命令:

```shell
./AliCloudDynamicDNS
```

当然如果你有其他的配置文件也可以通过指定 ```-f``` 参数来制定配置文件路径。例如：

```shell
./AliCloudDynamicDNS -f ./settings.json
```

如果你需要启动自动周期检测的话，请通过 `-i` 参数指定执行周期，单位是秒。为了防止频繁请求导致 API 接口受限，周期最小值为 `30 秒`，请设置大于 `30` 的数值。

```shell
./AliCloudDynamicDNS -f ./settings.json -i 3600
```

## 2. 如何安装

### 2.1 基于 Docker 直接运行

本程序已经打包了 x64 的 Linux Docker 镜像，基于 X86 的机器可以直接执行以下命令拉取并快速执行命令。

```
docker run -d -ti -v <你的配置文件路径(完整)>:/app/settings.json --name=aliyun-ddns myzony/ali-cloud-dynamic-dns:latest
```

### 2.2 基于 Docker Compose 运行 (推荐)

下面是一个 Docker-Compose 的示例:

*docker-compose.yaml*

```yaml
version: '3'
services:
	ddns:
		container_name: aliyun-ddns
		image: myzony/ali-cloud-dynamic-dns:latest
		volume:
			- <你的配置文件路径(完整)>:/app/settings.json
		restart: always
```



### 2.3 基于群晖 Docker

TODO

### 2.4 基于二进制程序运行

TODO

## 3.下载地址

本项目打包了常见系统、架构的二进制可执行文件，你可以直接下载对应的压缩包解压到你的路由器或者 NAS 里面进行运行。

**[下载地址在这儿](https://github.com/GameBelial/AliDDNSNet/releases)**

## 4. 自部署的 API 服务

你可以直接使用我提供的 `https://api.myzony.com/get-ip` 作为 *PublicIpServer* 查询接口，如果服务不可用，你可以使用 NGINX 结合以下配置文件(*.config)部署自己的 IP 地址查询接口。

```nginx
server{
    listen 80;
    server_name yourdomain.com;
    return 301 https://yourdomain.com$request_uri;
}

server{
    listen 443 ssl;
    http2 on;
    server_name yourdomain.com;

    ssl_certificate   /opt/cert/yourdomain.com.cer;
    ssl_certificate_key  /opt/cert/yourdomain.com.key;
    ssl_session_timeout 5m;
    ssl_ciphers ECDHE-RSA-AES128-GCM-SHA256:ECDHE:ECDH:AES:HIGH:!NULL:!aNULL:!MD5:!ADH:!RC4;
    ssl_protocols TLSv1 TLSv1.1 TLSv1.2;
    ssl_prefer_server_ciphers on;

    location /get-ip {
        add_header Content-Type application/json;
        return 200 '$remote_addr';
    }
}
```

