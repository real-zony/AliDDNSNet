using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AliCloudDynamicDNS.Configuration;
using AliCloudDynamicDNS.Updating;
using Spectre.Console;

namespace AliCloudDynamicDNS.Cli;

public sealed partial class ConsolePresenter
{
    private readonly IAnsiConsole _console;
    private readonly bool _rich;
    private readonly object _outputLock = new();
    private ConfigurationModel? _configuration;

    public ConsolePresenter(IAnsiConsole console, bool interactive, ConfigurationModel? configuration = null)
    {
        _console = console;
        _configuration = configuration;
        _rich = interactive && console.Profile.Out.IsTerminal && console.Profile.Capabilities.Interactive
            && console.Profile.Capabilities.Ansi && Environment.GetEnvironmentVariable("NO_COLOR") is null;
    }

    public void WriteConfiguration(ConfigurationModel configuration, uint intervalSeconds)
    {
        lock (_outputLock)
        {
            _configuration = configuration;
            var mode = intervalSeconds == 0 ? "单次运行" : $"周期运行；每轮完成后等待 {intervalSeconds.ToString(CultureInfo.InvariantCulture)} 秒";
            if (!_rich)
            {
                WriteLine($"配置：{mode}；主域名={configuration.MainDomain}；AccessId={MaskAccessId(configuration.AccessId)}");
                foreach (var target in configuration.SubDomains)
                {
                    WriteLine($"目标：{target.Type} {target.SubDomain}.{configuration.MainDomain}；TTL={target.Interval.ToString(CultureInfo.InvariantCulture)} 秒");
                }
                return;
            }

            var summary = new Table().Border(TableBorder.Rounded);
            summary.AddColumn(new TableColumn(new Text("配置")));
            summary.AddColumn(new TableColumn(new Text("当前值")));
            summary.AddRow(new Text("运行模式"), SafeText(mode));
            summary.AddRow(new Text("主域名"), SafeText(configuration.MainDomain));
            summary.AddRow(new Text("AccessId"), SafeText(MaskAccessId(configuration.AccessId)));
            _console.Write(summary);

            var records = new Table().Border(TableBorder.Rounded);
            records.AddColumn(new TableColumn(new Text("类型")));
            records.AddColumn(new TableColumn(new Text("记录")));
            records.AddColumn(new TableColumn(new Text("TTL / 秒")));
            foreach (var target in configuration.SubDomains)
            {
                records.AddRow(SafeText(target.Type), SafeText(target.SubDomain),
                    new Text(target.Interval.ToString(CultureInfo.InvariantCulture)));
            }
            _console.Write(records);
        }
    }

    public void WriteIp(AddressFamily family, IPAddress address)
    {
        lock (_outputLock)
        {
            WriteLine($"公网 {(family == AddressFamily.InterNetwork ? "IPv4" : "IPv6")}：{address}");
        }
    }

    public void WriteUpdateResult(UpdateResult result, bool includeUnchanged)
    {
        lock (_outputLock)
        {
            if (result.Error is not null)
            {
                WriteLine($"错误：{result.Error}", Color.Red);
            }

            Table? table = null;
            foreach (var record in result.Records)
            {
                if (!includeUnchanged && record.Status == RecordUpdateStatus.Unchanged)
                {
                    continue;
                }
                var status = record.Status switch
                {
                    RecordUpdateStatus.Updated => "已更新",
                    RecordUpdateStatus.Unchanged => "无需更新",
                    _ => "失败"
                };
                var color = record.Status switch
                {
                    RecordUpdateStatus.Updated => Color.Green,
                    RecordUpdateStatus.Unchanged => Color.Grey,
                    _ => Color.Red
                };
                var values = record.OldValue is null ? record.NewValue ?? "-"
                    : $"{record.OldValue} => {record.NewValue ?? "-"}";
                if (!_rich)
                {
                    WriteLine($"{status}：{record.Type} {record.SubDomain}；{values}{(record.Error is null ? "" : $"；{record.Error}")}");
                    continue;
                }

                if (table is null)
                {
                    table = new Table().Border(TableBorder.Rounded);
                    table.AddColumn(new TableColumn(new Text("记录")));
                    table.AddColumn(new TableColumn(new Text("状态")));
                    table.AddColumn(new TableColumn(new Text("IP 地址")));
                    table.AddColumn(new TableColumn(new Text("详情")));
                }
                table.AddRow(SafeText($"{record.Type} {record.SubDomain}"), new Text(status, new Style(color)),
                    SafeText(values), SafeText(record.Error ?? "-"));
            }
            if (table is not null)
            {
                _console.Write(table);
            }
            else if (includeUnchanged && result.IsSuccess && result.Records.Count == 0)
            {
                WriteLine("本轮完成，没有需要更新的记录。");
            }
        }
    }

    public void WriteError(string message)
    {
        lock (_outputLock)
        {
            WriteLine($"错误：{message}", Color.Red);
        }
    }

    public void WriteCommandHelp()
    {
        lock (_outputLock)
        {
            WriteLine("命令（不区分大小写）：ip 查看公网 IP；config 查看配置；update 立即更新；cls 清屏；exit 退出。");
        }
    }

    public void Clear()
    {
        lock (_outputLock)
        {
            if (_rich)
            {
                _console.Clear();
            }
        }
    }

    private void WriteLine(string message, Color? color = null)
    {
        var safe = Sanitize(message);
        if (_rich)
        {
            _console.Write(new Text(safe + Environment.NewLine, color is null ? Style.Plain : new Style(color.Value)));
        }
        else
        {
            // Bypass terminal layout/wrapping as well as ANSI when the output is a log stream.
            _console.Profile.Out.Writer.WriteLine(safe);
        }
    }

    private Text SafeText(string value) => new(Sanitize(value));

    private string Sanitize(string value)
    {
        if (_configuration is not null)
        {
            value = Redact(value, _configuration.AccessKey, "[redacted]");
            value = Redact(value, _configuration.AccessId, MaskAccessId(_configuration.AccessId));
        }
        value = UrlPattern().Replace(value, static match =>
        {
            var query = match.Value.IndexOf('?');
            return query < 0 ? match.Value : match.Value[..query] + "?[redacted]";
        });
        StringBuilder? clean = null;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsControl(character) || character is '\u2028' or '\u2029')
            {
                clean ??= new StringBuilder(value.Length).Append(value, 0, index);
                clean.Append(' ');
            }
            else
            {
                clean?.Append(character);
            }
        }
        return clean?.ToString() ?? value;
    }

    private static string Redact(string text, string credential, string replacement)
    {
        if (string.IsNullOrEmpty(credential))
        {
            return text;
        }
        return text.Replace(credential, replacement, StringComparison.Ordinal)
            .Replace(Uri.EscapeDataString(credential), replacement, StringComparison.OrdinalIgnoreCase);
    }

    private static string MaskAccessId(string accessId) => accessId.Length <= 4 ? "****" : "****" + accessId[^4..];

    [GeneratedRegex("""https?://[^\s<>"']+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();
}
