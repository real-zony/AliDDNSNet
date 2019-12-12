using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AliCloudDynamicDNS.ApiRequest;
using AliCloudDynamicDNS.Configuration;
using AliCloudDynamicDNS.Models;
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
            
            Console.ReadLine();
        }

        private async Task InitializeConfigurationAsync()
        {
            var filePath = FilePath ?? Path.Combine(Environment.CurrentDirectory,"settings.json");
            if (!File.Exists(filePath))
            {
                ConsoleHelper.WriteError("当前目录不存在配置文件，或者指定的配置文件路径不正确。");
                Environment.Exit(-1);
            }

            await ConfigurationHelper.ReadConfigFileAsync(filePath);
        }

        private void InitializeStrongTimer()
        {
            if (Interval < 60 && Interval != 0)
            {
                ConsoleHelper.WriteError($"指定的时间周期必须大于或等于 60 秒，用户指定的值：{Interval}");
                Environment.Exit(-1);
            }

            var intervalSec = (int) TimeSpan.FromSeconds(Interval).TotalMilliseconds;
            _strongTimer = new StrongTimer
            {
                Period = intervalSec == 0 ? 5000 : intervalSec,
                RunOnStart = true
            };

            _strongTimer.Elapsed += (sender, args) =>
            {
                AsyncContext.Run(async () =>
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

                    var currentPubicIp = (await NetworkHelper.GetPublicNetworkIp()).Replace("\n", "");

                    foreach (var subDomain in ConfigurationHelper.Configuration.SubDomains)
                    {
                        var record = records.FirstOrDefault(x => x.SubName == subDomain.SubDomain);
                        if (record == null) continue;
                        if (record.Value == currentPubicIp) continue;

                        // 更新指定的子域名 IP。
                        var result = (await _apiRequestTool.UpdateRecordAsync(record.RecordId, currentPubicIp, subDomain)).SelectToken("$.RecordId").Value<string>();
                        if (result == null || result != record.RecordId)
                        {
                            ConsoleHelper.WriteError($"记录 {record.SubName} 更新失败...");
                        }
                    }
                });
            };

            _strongTimer.Start();
            ConsoleHelper.WriteMessage("程序已经开始运行...");

            if (Interval == 0)
            {
                _strongTimer.Stop();
                ConsoleHelper.WriteMessage("程序执行完成...");
                Environment.Exit(0);
            }
        }

        public static Task<int> Main(string[] args) => CommandLineApplication.ExecuteAsync<Program>(args);
    }
}