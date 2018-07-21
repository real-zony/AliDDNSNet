using AliDDNSNet.Request;
using Microsoft.Extensions.CommandLineUtils;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Threading.Tasks;

namespace AliDDNSNet
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            var app = new CommandLineApplication();
            app.Name = "AliDDNSNet";
            app.HelpOption("-?|-h|--help");

            var attachments = app.Option("-f|--file <FILE>", "配置文件路径.", CommandOptionType.SingleValue);

            app.OnExecute(async () =>
            {
                #region > 配置初始化 <
                // 加载配置文件：
                var filePath = attachments.HasValue()
                    ? attachments.Value()
                    : $"{Environment.CurrentDirectory}{Path.DirectorySeparatorChar}settings.json";

                if (!File.Exists(filePath))
                {
                    Console.WriteLine("当前目录没有配置文件，或者配置文件位置不正确。");
                    return -1;
                }

                var config = await Utils.ReadConfigFile(filePath);
                Utils.config = config;
                #endregion

                // 获得当前 IP
                var currentIP = (await Utils.GetCurentPublicIP()).Replace("\n", "");
                var subDomains = JObject.Parse(await Utils.SendGetRequest(new DescribeDomainRecordsRequest(config.domain)));

                if (subDomains.SelectToken($"$.DomainRecords.Record[?(@.RR == '{config.sub_domain}')]") == null)
                {
                    Console.WriteLine("指定的子域名不存在，请新建一个子域名解析。");
                    return 0;
                }

                Console.WriteLine("已经找到对应的域名与解析");
                Console.WriteLine("======================");
                Console.WriteLine($"子域名:{config.sub_domain}{config.domain}");

                var dnsIp = subDomains.SelectToken($"$.DomainRecords.Record[?(@.RR == '{config.sub_domain}')].Value").Value<string>();

                Console.WriteLine($"目前的 A 记录解析 IP 地址:{dnsIp}");
                if (currentIP == dnsIp)
                {
                    Console.WriteLine("解析地址与当前主机 IP 地址一致，无需更改.");
                    return 0;
                }

                Console.WriteLine("检测到 IP 地址不一致，正在更改中......");
                var rrId = subDomains.SelectToken($"$.DomainRecords.Record[?(@.RR == '{config.sub_domain}')].RecordId").Value<string>();

                var response = await Utils.SendGetRequest(new UpdateDomainRecordRequest(rrId, config.sub_domain, config.type, currentIP, config.interval.ToString()));

                var resultRRId = JObject.Parse(response).SelectToken("$.RecordId").Value<string>();

                if (resultRRId == null || resultRRId != rrId)
                {
                    Console.WriteLine("更改记录失败，请稍后再试。");
                }
                else
                {
                    Console.WriteLine("更改记录成功。");
                }

                return 0;
            });

            return await Task.FromResult(app.Execute(args));
        }
    }
}
