using System;
using System.IO;
using System.Threading.Tasks;
using AliDDNSNet.Request;
using AliDDNSNet.Utility;
using Microsoft.Extensions.CommandLineUtils;
using Newtonsoft.Json.Linq;

namespace AliDDNSNet
{
    static class Program
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
                
                // 加载当前目录的配置文件
                var filePath = attachments.HasValue()
                    ? attachments.Value()
                    : $"{Environment.CurrentDirectory}{Path.DirectorySeparatorChar}settings.json";

                if (!File.Exists(filePath))
                {
                    Console.WriteLine("当前目录没有配置文件，或者配置文件位置不正确。");
                    return -1;
                }

                Utils.Configuration = await Utils.ReadConfigFile(filePath);
                
                #endregion

                #region > 校验 IP 是否需要进行更改 <
                
                // 获得当前机器的公网 IP 地址
                var currentIpAddress = (await Utils.GetCurrentPublicIpAddress()).Replace("\n", "");
                var subDomainsJObject = JObject.Parse(await Utils.SendGetRequest(new DescribeDomainRecordsRequest(Utils.Configuration.domain)));

                if (subDomainsJObject.SelectToken($"$.DomainRecords.Record[?(@.RR == '{Utils.Configuration.sub_domain}')]") == null)
                {
                    Console.WriteLine("指定的子域名不存在，请新建一个子域名解析。");
                    return 0;
                }

                Console.WriteLine("已经找到对应的域名与解析。");
                Console.WriteLine($"{'='.BuildLineCharacter(20)}");
                Console.WriteLine($"子域名:{Utils.Configuration.sub_domain}{Utils.Configuration.domain}");

                var dnsIp = subDomainsJObject.SelectToken($"$.DomainRecords.Record[?(@.RR == '{Utils.Configuration.sub_domain}')].Value").Value<string>();

                Console.WriteLine($"目前的 A 记录解析 IP 地址: {dnsIp}");
                if (currentIpAddress == dnsIp)
                {
                    Console.WriteLine("解析地址与当前主机 IP 地址一致，无需更改。");
                    return 0;
                }
                
                #endregion

                Console.WriteLine("检测到 IP 地址不一致，正在更改中......");
                var rrId = subDomainsJObject.SelectToken($"$.DomainRecords.Record[?(@.RR == '{Utils.Configuration.sub_domain}')].RecordId").Value<string>();

                var response = await Utils.SendGetRequest(new UpdateDomainRecordRequest(rrId, Utils.Configuration.sub_domain, Utils.Configuration.type, currentIpAddress, Utils.Configuration.interval.ToString()));

                var resultRrId = JObject.Parse(response).SelectToken("$.RecordId").Value<string>();
                if (resultRrId == null || resultRrId != rrId)
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
