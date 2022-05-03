using System;
using System.Net.Http;
using System.Text.RegularExpressions;
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
                    using (var request = new HttpRequestMessage(HttpMethod.Get, "https://pv.sohu.com/cityjson?ie=utf-8"))
                    {
                        using (var response = await client.SendAsync(request))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                var content = await response.Content.ReadAsStringAsync();
                                return Regex.Match(content, @"\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}").Value;
                            }

                            ConsoleHelper.WriteError($"获取公网IP出错。错误码：{response.StatusCode}");
                            return "";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ConsoleHelper.WriteError($"获取公网IP出错，错误原因为：\r\n{ex.Message}");
                return "";
            }
        }
    }
}