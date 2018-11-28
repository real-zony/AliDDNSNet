using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using AliDDNSNet.Request;
using Newtonsoft.Json;

namespace AliDDNSNet.Utility
{
    public static class Utils
    {
        /// <summary>
        /// 配置文件所读取的配置项结果
        /// </summary>
        public static ConfigurationClass Configuration { get; set; }

        /// <summary>
        /// 根据字典构建请求字符串
        /// </summary>
        /// <param name="parameters">参数字典</param>
        /// <returns></returns>
        public static string BuildRequestString(this SortedDictionary<string, string> parameters)
        {
            var sb = new StringBuilder();
            foreach (var kvp in parameters)
            {
                sb.Append("&");
                sb.Append(HttpUtility.UrlEncode(kvp.Key));
                sb.Append("=");
                sb.Append(HttpUtility.UrlEncode(kvp.Value));
            }

            return sb.ToString().Substring(1);
        }

        /// <summary>
        /// 生成通用参数字典
        /// </summary>
        public static SortedDictionary<string, string> GenerateGenericParameters()
        {
            var dict = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                {"Format", "json"},
                {"AccessKeyId", Configuration.access_id},
                {"SignatureMethod", "HMAC-SHA1"},
                {"SignatureNonce", Guid.NewGuid().ToString()},
                {"Version", "2015-01-09"},
                {"SignatureVersion", "1.0"},
                {"Timestamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")}
            };

            return dict;
        }

        /// <summary>
        /// 对阿里云 API 发送 GET 请求
        /// </summary>
        public static async Task<string> SendGetRequest(IRequest request)
        {
            var sign = request.Parameters.BuildRequestString().GenerateSignature(Configuration.access_key);
            var postUri = $"http://alidns.aliyuncs.com/?{request.Parameters.AppendSignature(sign)}";

            using (var client = new HttpClient())
            {
                using (var httpRequest = new HttpRequestMessage(HttpMethod.Get, postUri))
                {
                    using (var httpResponse = await client.SendAsync(httpRequest))
                    {
                        return await httpResponse.Content.ReadAsStringAsync();
                    }
                }
            }
        }

        /// <summary>
        /// 获得当前机器的公网 IP
        /// </summary>
        public static async Task<string> GetCurrentPublicIpAddress()
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

        /// <summary>
        /// 读取配置文件
        /// </summary>
        /// <param name="filePath">配置文件路径</param>
        public static async Task<ConfigurationClass> ReadConfigFile(string filePath)
        {
            using (var fs = File.Open(filePath, FileMode.Open, FileAccess.Read))
            {
                using (var read = new StreamReader(fs))
                {
                    return JsonConvert.DeserializeObject<ConfigurationClass>(await read.ReadToEndAsync());
                }
            }
        }
    }
}
