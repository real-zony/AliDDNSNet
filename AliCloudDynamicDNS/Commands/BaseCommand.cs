using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;

namespace AliCloudDynamicDNS.Commands
{
    [HelpOption("-?|-h|--help", Description = "欢迎使用 AliDDNSNet 工具。")]
    public abstract class BaseCommand
    {
        protected virtual Task<int> OnExecuteAsync(CommandLineApplication app)
        {
            return Task.FromResult(0);
        }
    }
}