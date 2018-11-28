using System.Collections.Generic;
using AliDDNSNet.Utility;

namespace AliDDNSNet.Request
{
    /// <summary>
    /// 修改解析记录
    /// </summary>
    public class UpdateDomainRecordRequest : IRequest
    {
        /// <inheritdoc />
        public SortedDictionary<string, string> Parameters { get; }

        /// <summary>
        /// 修改解析记录
        /// </summary>
        /// <param name="recordId">解析记录的 ID</param>
        /// <param name="rr">主机记录</param>
        /// <param name="type">解析记录类型</param>
        /// <param name="value">记录值</param>
        /// <param name="ttl">生存时间</param>
        public UpdateDomainRecordRequest(string recordId, string rr, string type, string value, string ttl)
        {
            Parameters = Utils.GenerateGenericParameters();
            Parameters.Add("Action", "UpdateDomainRecord");
            Parameters.Add("RecordId", recordId);
            Parameters.Add("RR", rr);
            Parameters.Add("Type", type);
            Parameters.Add("Value", value);
            Parameters.Add("TTL", ttl);
        }
    }
}