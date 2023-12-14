using System.Threading.Tasks;
using AliCloudDynamicDNS.Configuration;
using AliCloudDynamicDNS.Utility;
using Shouldly;
using Xunit;

namespace AliCloudDynamicDNS.Test
{
    public class NetworkHelperTests
    {
        public NetworkHelperTests()
        {
            ConfigurationHelper.Configuration = new ConfigurationModel
            {
                PublicIpServer = "https://api.myzony.com/get-ip"
            };
        }
        
        [Fact]
        public async Task GetPublicNetworkIp_Test()
        {
            var result = await NetworkHelper.GetPublicNetworkIp();
            result.ShouldNotBeNull();
        }
    }
}