using AliDDNSNet.Request;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web;

namespace AliDDNSNet
{
    public static class Utils
    {
        public static ConfigurationClass config { get; set; }

        /// <summary>
        /// 生成请求签名
        /// </summary>
        /// <param name="srcStr">请求体</param>
        /// <returns>HMAC-SHA1 的 Base64 编码</returns>
        public static string GenerateSignature(this string srcStr)
        {
            var signStr = $"GET&{HttpUtility.UrlEncode("/")}&{HttpUtility.UrlEncode(srcStr)}";

            // 替换已编码的 URL 字符为大写字符
            signStr = signStr.Replace("%2f", "%2F").Replace("%3d", "%3D").Replace("%2b", "%2B")
                .Replace("%253a", "%253A");

            var hmac = new HMACSHA1(Encoding.UTF8.GetBytes($"{config.access_key}&"));
            return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signStr)));
        }

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
        /// 追加签名参数
        /// </summary>
        /// <param name="parameters">参数列表</param>
        public static string AppendSignature(this SortedDictionary<string, string> parameters, string sign)
        {
            parameters.Add("Signature", sign);
            return parameters.BuildRequestString();
        }

        /// <summary>
        /// 生成通用参数字典
        /// </summary>
        public static SortedDictionary<string, string> GenerateGenericParameters()
        {
            var dict = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                {"Format", "json"},
                {"AccessKeyId", config.access_id},
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
            var sign = request.Parameters.BuildRequestString().GenerateSignature();
            var postUri = $"http://alidns.aliyuncs.com/?{request.Parameters.AppendSignature(sign)}";

            using (var client = new HttpClient())
            {
                using (var resuest = new HttpRequestMessage(HttpMethod.Get, postUri))
                {
                    using (var response = await client.SendAsync(resuest))
                    {
                        return await response.Content.ReadAsStringAsync();
                    }
                }
            }
        }

        /// <summary>
        /// 获得当前机器的公网 IP
        /// </summary>
        public static async Task<string> GetCurentPublicIP()
        {
            using (var client = new HttpClient())
            {
                using (var resuest = new HttpRequestMessage(HttpMethod.Get, "http://members.3322.org/dyndns/getip"))
                {
                    using (var response = await client.SendAsync(resuest))
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
