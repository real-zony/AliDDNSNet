using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AliCloudDynamicDNS.AliCloud.ApiRequest;
using AliCloudDynamicDNS.AliCloud.Models;
using AliCloudDynamicDNS.Configuration;
using AliCloudDynamicDNS.Threading;
using AliCloudDynamicDNS.Utility;
using McMaster.Extensions.CommandLineUtils;
using Newtonsoft.Json.Linq;
using Nito.AsyncEx;

namespace AliCloudDynamicDNS
{
    [HelpOption("-?|-h|--help")]
    public class Program
    {
        [Option("-f|--file <FILE>", "指定自定义的配置文件，请传入配置文件的路径。", CommandOptionType.SingleValue)]
        public string FilePath { get; set; }

        [Option("-i|--interval <INTERVAL>", "指定程序的自动检测周期，单位是秒。", CommandOptionType.SingleValue)]
        public uint Interval { get; set; }

        private StrongTimer _strongTimer;
        private readonly ApiRequestTool _apiRequestTool = new ApiRequestTool();

        public async Task OnExecuteAsync()
        {
            await InitializeConfigurationAsync();
            InitializeStrongTimer();

            await ConsoleAwaitExitAsync();
        }

        private async Task ConsoleAwaitExitAsync()
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("");
            Console.WriteLine("==================== 可执行命令（不区分大小写） ====================");
            Console.WriteLine("输入“cls”清屏");
            Console.WriteLine("输入“ip”查看当前公网IP");
            Console.WriteLine("输入“config”查看当前配置信息");
            Console.WriteLine("输入“update”强制更新IP解析");
            Console.WriteLine("输入“exit”退出程序");
            Console.WriteLine("==================== 可执行命令（不区分大小写） ====================");
            Console.WriteLine("请输入：");
            Console.ForegroundColor = ConsoleColor.Gray;

            string command = Console.ReadLine().ToLower();
            Console.WriteLine("");
            switch (command)
            {
                case "exit":
                    break;
                case "cls":
                    Console.Clear();
                    ConsoleWriteConfigInfo();
                    await ConsoleWriteIpInfo();
                    break;
                case "ip":
                    await ConsoleWriteIpInfo();
                    break;
                case "update":
                    await UpdateRecord(true);
                    await ConsoleAwaitExitAsync();
                    break;
                case "config":
                    ConsoleWriteConfigInfo();
                    await ConsoleAwaitExitAsync();
                    break;
                default:
                    await ConsoleAwaitExitAsync();
                    break;
            }
        }

        private async Task ConsoleWriteIpInfo()
        {
            ConsoleHelper.WriteInfo($"正在获取当前公网IP，请稍等...");
            var currentPubicIp = (await NetworkHelper.GetPublicNetworkIp()).Replace("\n", "");
            ConsoleHelper.WriteInfo($"当前的公网IP：{currentPubicIp}");
            await ConsoleAwaitExitAsync();
        }

        private void ConsoleWriteConfigInfo()
        {
            var intervalSec = (int) TimeSpan.FromSeconds(Interval).TotalSeconds;
            StringBuilder iniConfMsg = new StringBuilder();
            iniConfMsg.AppendLine($"\t当前配置内容如下：");
            iniConfMsg.AppendLine($"\t监听的时间周期：{intervalSec} 秒");
            iniConfMsg.AppendLine($"\tAccessId：{ConfigurationHelper.Configuration.AccessId}");
            iniConfMsg.AppendLine($"\t监听的主域名：{ConfigurationHelper.Configuration.MainDomain}");
            iniConfMsg.AppendLine($"\t监听的子域名：");
            int subDomainSerialNumber = 0;
            foreach (var subDomain in ConfigurationHelper.Configuration.SubDomains)
            {
                subDomainSerialNumber++;
                iniConfMsg.AppendLine($"\t\t子域名地址{subDomainSerialNumber}：{subDomain.SubDomain}.{ConfigurationHelper.Configuration.MainDomain} - 记录类型：{subDomain.Type}");
            }

            ConsoleHelper.WriteInfo(iniConfMsg.ToString());
        }

        private async Task InitializeConfigurationAsync()
        {
            var filePath = FilePath ?? Path.Combine(Environment.CurrentDirectory, "settings.json");
            if (!File.Exists(filePath))
            {
                ConsoleHelper.WriteError("当前目录不存在配置文件，或者指定的配置文件路径不正确。");
                Environment.Exit(-1);
            }

            await ConfigurationHelper.ReadConfigFileAsync(filePath);
            ConsoleWriteConfigInfo();
        }

        private void InitializeStrongTimer()
        {
            int minInterval = 30;
            if (Interval < minInterval && Interval != 0)
            {
                ConsoleHelper.WriteError($"指定的时间周期必须大于或等于 {minInterval} 秒，用户指定的值：{Interval}");
                Environment.Exit(-1);
            }

            var intervalSec = (int) TimeSpan.FromSeconds(Interval).TotalMilliseconds;
            _strongTimer = new StrongTimer
            {
                Period = intervalSec == 0 ? 5000 : intervalSec,
                RunOnStart = true
            };

            _strongTimer.Elapsed += (sender, args) => { AsyncContext.Run(async () => { await UpdateRecord(); }); };

            _strongTimer.Start();
            ConsoleHelper.WriteMessage("程序已经开始运行...");

            if (Interval == 0)
            {
                _strongTimer.Stop();
                ConsoleHelper.WriteMessage("程序执行完成...");
                Environment.Exit(0);
            }
        }

        private async Task UpdateRecord(bool isWriteNoUpdateInfo = false)
        {
            try
            {
                var records = (await _apiRequestTool.GetRecordsWithMainDomainAsync(ConfigurationHelper.Configuration.MainDomain))
                    .SelectTokens($"$.DomainRecords.Record[*]")
                    .Select(x => new AliCloudRecordModel
                    {
                        RecordId = x.SelectToken("$.RecordId")?.Value<string>(),
                        SubName = x.SelectToken("$.RR")?.Value<string>(),
                        Value = x.SelectToken("$.Value")?.Value<string>()
                    })
                    .ToList();
                ConsoleHelper.WriteInfo($"远程API获取的域名[{ConfigurationHelper.Configuration.MainDomain}]的解析记录有：{records.Count}条");
                var currentPubicIp = (await NetworkHelper.GetPublicNetworkIp()).Replace("\n", "");
                if (!string.IsNullOrEmpty(currentPubicIp))
                {
                    ConsoleHelper.WriteInfo($"已获取本机公网IP：[{currentPubicIp}]");
                    foreach (var subDomain in ConfigurationHelper.Configuration.SubDomains)
                    {
                        var record = records.FirstOrDefault(x => x.SubName == subDomain.SubDomain);
                        if (record == null)
                        {
                            ConsoleHelper.WriteError($" {record.SubName}.{ConfigurationHelper.Configuration.MainDomain} 在远程API获取的域名中未找到，无法进行更新IP操作...");
                            continue;
                        }

                        if (record.Value == currentPubicIp)
                        {
                            if (isWriteNoUpdateInfo)
                            {
                                ConsoleHelper.WriteInfo($"{record.SubName}.{ConfigurationHelper.Configuration.MainDomain} 记录的IP与当前获取的公网IP一致，无需更新");
                            }

                            continue;
                        }

                        // 更新指定的子域名 IP。
                        var result = (await _apiRequestTool.UpdateRecordAsync(record.RecordId, currentPubicIp, subDomain)).SelectToken("$.RecordId").Value<string>();
                        if (result == null || result != record.RecordId)
                        {
                            ConsoleHelper.WriteError($" {record.SubName}.{ConfigurationHelper.Configuration.MainDomain} 更新失败...");
                        }
                        else
                        {
                            ConsoleHelper.WriteInfo($" {record.SubName}.{ConfigurationHelper.Configuration.MainDomain} 更新成功，IP：{record.Value} => {currentPubicIp}");
                        }
                    }
                }
                else
                {
                    ConsoleHelper.WriteError($"获取本机公网IP失败");
                }
            }
            catch (Exception ex)
            {
                ConsoleHelper.WriteError($"更新API解析出错，错误原因为：\r\n{ex.Message}");
            }
        }

        public static Task<int> Main(string[] args) => CommandLineApplication.ExecuteAsync<Program>(args);
    }
}