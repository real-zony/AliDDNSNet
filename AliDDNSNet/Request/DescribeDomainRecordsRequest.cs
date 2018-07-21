using System.Collections.Generic;

namespace AliDDNSNet.Request
{
    public class DescribeDomainRecordsRequest : IRequest
    {
        public SortedDictionary<string, string> Parameters { get; }

        public DescribeDomainRecordsRequest(string domainName)
        {
            Parameters = Utils.GenerateGenericParameters();
            Parameters.Add("Action", "DescribeDomainRecords");
            Parameters.Add("DomainName", domainName);
        }
    }
}