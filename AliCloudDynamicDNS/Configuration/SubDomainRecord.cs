namespace AliCloudDynamicDNS.Configuration
{
    /// <summary>
    /// 子域名记录的定义。
    /// </summary>
    public class SubDomainRecord
    {
        /// <summary>
        /// 域名记录的类型。
        /// </summary>
        public string Type { get; private set; }

        /// <summary>
        /// 子域名的名称。
        /// </summary>
        public string SubDomain { get; private set; }

        /// <summary>
        /// 记录的 TTL 时间。
        /// </summary>
        public int Interval { get; private set; }

        /// <summary>
        /// 构建一条新的子域名记录。
        /// </summary>
        /// <param name="type">域名记录的类型。</param>
        /// <param name="subDomain">子域名的名称。</param>
        /// <param name="interval">域名记录的 TTL 时间。</param>
        public SubDomainRecord(string type, string subDomain, int interval)
        {
            Type = type;
            SubDomain = subDomain;
            Interval = interval;
        }
    }
}