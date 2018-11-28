using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace AliDDNSNet.Utility
{
    public static class UtilExtensions
    {
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
        /// 生成请求签名
        /// </summary>
        /// <param name="srcStr">请求体</param>
        /// <param name="accessKey">访问密钥</param>
        /// <returns>HMAC-SHA1 的 Base64 编码</returns>
        public static string GenerateSignature(this string srcStr,string accessKey)
        {
            var signStr = $"GET&{HttpUtility.UrlEncode("/")}&{HttpUtility.UrlEncode(srcStr)}";

            // 替换已编码的 URL 字符为大写字符
            signStr = signStr.Replace("%2f", "%2F").Replace("%3d", "%3D").Replace("%2b", "%2B")
                .Replace("%253a", "%253A");

            var hmac = new HMACSHA1(Encoding.UTF8.GetBytes($"{accessKey}&"));
            return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signStr)));
        }

        /// <summary>
        /// 使用指定的字符构造一行分隔符
        /// </summary>
        /// <param name="character">指定的字符</param>
        /// <param name="length">分隔符的长度</param>
        public static string BuildLineCharacter(this char character,int length)
        {
            var sb = new StringBuilder();
            
            for (int i = 0; i < length; i++)
            {
                sb.Append(character);
            }

            return sb.ToString();
        }
    }
}