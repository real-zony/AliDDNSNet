using System.Net.Http;
using System.Threading.Tasks;
using AliCloudDynamicDNS.Configuration;
using Newtonsoft.Json.Linq;

namespace AliCloudDynamicDNS.ApiRequest
{
    public class ApiRequestTool
    {
        private readonly HttpClient _httpClient;

        public ApiRequestTool()
        {
            _httpClient = new HttpClient();
        }

        public async Task<JObject> GetRecordsWithMainDomainAsync(string mainDomainName)
        {
            var param = new ApiRequestParameters();
            param.AddParameter("Action", "DescribeDomainRecords");
            param.AddParameter("DomainName", mainDomainName);

            param.GenerateSignature();
            var responseStr = await RequestAsync(param);

            return JObject.Parse(responseStr);
        }

        public async Task<JObject> UpdateRecordAsync(string recordId, string publicIp, SubDomainRecord subDomainRecord)
        {
            var param = new ApiRequestParameters();
            param.AddParameter("Action", "UpdateDomainRecord");
            param.AddParameter("RecordId", recordId);
            param.AddParameter("RR", subDomainRecord.SubDomain);
            param.AddParameter("Type", subDomainRecord.Type);
            param.AddParameter("Value", publicIp);
            param.AddParameter("TTL", subDomainRecord.Interval);

            param.GenerateSignature();
            var responseStr = await RequestAsync(param);

            return JObject.Parse(responseStr);
        }

        private async Task<string> RequestAsync(ApiRequestParameters parameters)
        {
            var requestUrl = $"http://alidns.aliyuncs.com/?{parameters.GenerateSortedQueryString()}";

            using (var request = new HttpRequestMessage(HttpMethod.Get, requestUrl))
            {
                var result = await _httpClient.SendAsync(request);
                string requestContent = await result.Content.ReadAsStringAsync();
                if(string.IsNullOrEmpty(requestContent))
                {
                    AliCloudDynamicDNS.Utility.ConsoleHelper.WriteError($"远程请求出错：{request.ToString()}");
                }
                return requestContent;
            }
        }
    }
}