namespace AliCloudDynamicDNS.AliCloud.Models
{
    public class AliCloudRecordModel
    {
        public string SubName { get; set; }

        public string RecordId { get; set; }

        public string Value { get; set; }

        public AliCloudRecordModel(string subName, string recordId, string value)
        {
            SubName = subName;
            RecordId = recordId;
            Value = value;
        }

        public AliCloudRecordModel()
        {
            
        }
    }
}