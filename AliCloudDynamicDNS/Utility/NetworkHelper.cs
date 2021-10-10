using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace AliCloudDynamicDNS.Utility
{
    public static class NetworkHelper
    {
        public static async Task<string> GetPublicNetworkIp()
        {
            try
            {
                using (var client = new HttpClient())
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, "https://api.ipify.org/"))
                    {
                        using (var response = await client.SendAsync(request))
                        {
                            return await response.Content.ReadAsStringAsync();
                        }
                    }
                }
            }
            catch(Exception ex)
            {
                string msg = $"获取公网IP出错，错误原因为：\r\n{ex.Message}";
                ConsoleHelper.WriteError(ex.Message);
                return "";
            }
        }
    }
}