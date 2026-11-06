using System.CommandLine;

namespace AliCloudDynamicDNS.Cli;

public sealed record RunOptions(string? FilePath, uint IntervalSeconds);

public static class CliApplication
{
    public static RootCommand CreateRootCommand(Func<RunOptions, CancellationToken, Task<int>> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);

        var file = new Option<string?>("--file", "-f")
        {
            Description = "配置文件路径；默认使用当前目录的 settings.json。"
        };
        var interval = new Option<uint>("--interval", "-i")
        {
            Description = "自动检测周期（秒）：0 为执行一次，周期运行至少为 30 秒。"
        };
        interval.Validators.Add(result =>
        {
            // Option validators run before the built-in uint conversion in System.CommandLine.
            // Match its CurrentCulture uint parser, leaving malformed/out-of-range tokens to its diagnostics.
            if (result.Tokens.Count == 1 &&
                uint.TryParse(result.Tokens[0].Value, out var seconds) &&
                seconds is > 0 and < 30)
            {
                result.AddError("自动检测周期必须为 0（单次运行），或大于等于 30 秒。");
            }
        });

        var root = new RootCommand("AliCloudDynamicDNS：更新阿里云 A / AAAA 动态域名解析。\n示例：AliCloudDynamicDNS -f settings.json -i 300");
        root.Options.Add(file);
        root.Options.Add(interval);
        root.SetAction((parseResult, cancellationToken) => execute(
            new RunOptions(parseResult.GetValue(file), parseResult.GetValue(interval)), cancellationToken));
        return root;
    }
}
