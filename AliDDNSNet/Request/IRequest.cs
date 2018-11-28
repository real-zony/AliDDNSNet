using System.Collections.Generic;

namespace AliDDNSNet.Request
{
    /// <summary>
    /// 所有针对于阿里云解析的 API 请求，都应该
    /// 继承自本接口。
    /// </summary>
    public interface IRequest
    {
        /// <summary>
        /// 请求参数字典
        /// </summary>
        SortedDictionary<string, string> Parameters { get; }
    }
}