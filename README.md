## 使用说明

AliDDNSNet 是基于 .NET Core 开发的动态 DNS 解析工具，借助于阿里云的 DNS API 来实现域名与动态 IP 的绑定功能。

使用时请更改同目录下的 ```settings.json.example``` 为 ```settings.json``` 文件，然后执行以下命令：
```shell
./AliDDNSNet
```
效果图：  
![1203160-20180722111356778-841949657.png](https://i.loli.net/2018/07/24/5b56ab6161f80.png)

同时也可以显示通过 ```-f``` 参数来制定配置文件路径。例如：
```shell
dotnet ./AliDDNSNet.dll -f ./settings.json2
```

```shell
./AliDDNSNet -f ./settings.json3
```

## 配置说明：

通过更改 ```settings.json```/```settings.json.example``` 的内容来实现 DDNS 更新。

```json
{
  // 阿里云的 Access Id
  "access_id": "",
  // 阿里云的 Access Key
  "access_key": "",
  // TTL 时间
  "interval": 600,
  // 主域名
  "domain": "example.com",
  // 子域名前缀
  "sub_domain": "test",
  // 记录类型
  "type": "A"
}
```

其中 Access Id 与 Access Key 可以登录阿里云之后在右上角可以得到。

## 下载地址

程序打包了 Linux-x64 与 Linux arm 环境的二进制可执行文件，你可以直接下载对应的压缩包解压到你的路由器或者 NAS 里面进行运行。

如果你的设备支持 Docker 环境，建议通过 Docker 运行 .NET Core 2.1 环境来执行本程序。

[下载地址在这儿](https://github.com/GameBelial/AliDDNSNet/releases)
