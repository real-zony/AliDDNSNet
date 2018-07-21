using System.Collections.Generic;

namespace AliDDNSNet.Request
{
    public class UpdateDomainRecordRequest : IRequest
    {
        public SortedDictionary<string, string> Parameters { get; }

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
