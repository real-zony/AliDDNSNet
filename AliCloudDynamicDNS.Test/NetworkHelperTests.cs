using System.Threading.Tasks;
using AliCloudDynamicDNS.Utility;
using Xunit;

namespace AliCloudDynamicDNS.Test
{
    public class NetworkHelperTests
    {
        [Fact]
        public async Task GetPublicNetworkIp_Test()
        {
            var result = await NetworkHelper.GetPublicNetworkIp();
        }
    }
}