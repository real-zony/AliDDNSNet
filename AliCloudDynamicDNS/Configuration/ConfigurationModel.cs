using System.Collections.Generic;

namespace AliCloudDynamicDNS.Configuration
{
    /// <summary>
    /// 配置文件的相关参数。
    /// </summary>
    public class ConfigurationModel
    {
        /// <summary>
        /// 阿里云的 Access Id。
        /// </summary>
        public string AccessId { get; set; }

        /// <summary>
        /// 阿里云的 Access Key。
        /// </summary>
        public string AccessKey { get; set; }

        // 主域名。
        public string MainDomain { get; set; }

        // 需要批量变更的子域名记录集合。
        public List<SubDomainRecord> SubDomains { get; set; }
    }
}