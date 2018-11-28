using System.Collections.Generic;
using AliDDNSNet.Utility;

namespace AliDDNSNet.Request
{
    /// <summary>
    /// 获取解析记录列表
    /// </summary>
    public class DescribeDomainRecordsRequest : IRequest
    {
        public SortedDictionary<string, string> Parameters { get; }

        /// <summary>
        /// 获取解析记录列表
        /// </summary>
        /// <param name="domainName">域名名称</param>
        public DescribeDomainRecordsRequest(string domainName)
        {
            Parameters = Utils.GenerateGenericParameters();
            Parameters.Add("Action", "DescribeDomainRecords");
            Parameters.Add("DomainName", domainName);
        }
    }
}