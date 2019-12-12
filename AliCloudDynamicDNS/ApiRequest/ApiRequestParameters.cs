using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using AliCloudDynamicDNS.Configuration;

namespace AliCloudDynamicDNS.ApiRequest
{
    public class ApiRequestParameters
    {
        public ApiRequestParameters()
        {
            SortedDictionary = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                {"Format", "json"},
                {"AccessKeyId", ConfigurationHelper.Configuration.AccessId},
                {"SignatureMethod", "HMAC-SHA1"},
                {"SignatureNonce", Guid.NewGuid().ToString()},
                {"Version", "2015-01-09"},
                {"SignatureVersion", "1.0"},
                {"Timestamp", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")}
            };
        }

        protected SortedDictionary<string, string> SortedDictionary { get; }

        public void AddParameter(string key, string strValue)
        {
            if (string.IsNullOrEmpty(strValue)) return;
            if (SortedDictionary.ContainsKey(key)) return;

            SortedDictionary.Add(key, strValue);
        }

        public void AddParameter<T>(string key, T intValue)
        {
            SortedDictionary.Add(key, intValue.ToString());
        }

        public void GenerateSignature()
        {
            var queryString = GenerateSortedQueryString();
            var signBuilder = new StringBuilder();
            
            signBuilder.Append(HttpMethod.Get)
                .Append("&")
                .Append(SpecialUrlEncode("/"))
                .Append("&")
                .Append(SpecialUrlEncode(queryString));
            
            var hmac = new HMACSHA1(Encoding.UTF8.GetBytes($"{ConfigurationHelper.Configuration.AccessKey}&"));
            var signStr = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signBuilder.ToString())));
            SortedDictionary.Add("Signature", signStr);
        }

        public virtual string GenerateSortedQueryString()
        {
            var sb = new StringBuilder();
            foreach (var kv in SortedDictionary)
            {
                sb.Append("&")
                    .Append(SpecialUrlEncode(kv.Key))
                    .Append("=")
                    .Append(SpecialUrlEncode(kv.Value));
            }

            return sb.ToString().Substring(1);
        }

        protected virtual string SpecialUrlEncode(string srcStr)
        {
            var stringBuilder = new StringBuilder();
            const string text = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.~";
            var bytes = Encoding.UTF8.GetBytes(srcStr);
            foreach (var @byte in bytes)
            {
                var @char = (char) @byte;
                // 如果值是 text 集合内的数据，则使用默认的值。
                if (text.IndexOf(@char) >= 0)
                {
                    stringBuilder.Append(@char);
                }
                else
                {
                    stringBuilder.Append("%")
                        .Append(string.Format(CultureInfo.InvariantCulture, "{0:X2}", (int) @char));
                }
            }

            return stringBuilder.ToString();
        }
    }
}