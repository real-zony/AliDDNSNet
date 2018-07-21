using System.Collections.Generic;

namespace AliDDNSNet.Request
{
    public interface IRequest
    {
        SortedDictionary<string, string> Parameters { get; }
    }
}
