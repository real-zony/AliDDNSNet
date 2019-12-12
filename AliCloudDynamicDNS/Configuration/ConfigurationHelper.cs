using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AliCloudDynamicDNS.Configuration
{
    /// <summary>
    /// 配置文件相关的工具类定义。
    /// </summary>
    public static class ConfigurationHelper
    {
        /// <summary>
        /// 从指定的文件当中，读取程序的配置参数。
        /// </summary>
        /// <param name="filePath">需要读取数据的配置文件路径。</param>
        /// <returns>配置文件当中存储的配置项实例。</returns>
        public static async Task<ConfigurationModel> ReadConfigFileAsync(string filePath)
        {
            using (var fs = File.Open(filePath, FileMode.Open, FileAccess.Read))
            {
                using (var read = new StreamReader(fs))
                {
                    Configuration = JsonConvert.DeserializeObject<ConfigurationModel>(await read.ReadToEndAsync());
                }
            }

            return Configuration;
        }
        
        public static ConfigurationModel Configuration { get; private set; }
    }
}