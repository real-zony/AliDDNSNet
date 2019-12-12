using System.Net.Http;
using System.Threading.Tasks;

namespace AliCloudDynamicDNS.Utility
{
    public static class NetworkHelper
    {
        public static async Task<string> GetPublicNetworkIp()
        {
            using (var client = new HttpClient())
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, "http://members.3322.org/dyndns/getip"))
                {
                    using (var response = await client.SendAsync(request))
                    {
                        return await response.Content.ReadAsStringAsync();
                    }
                }
            }
        }
    }
}