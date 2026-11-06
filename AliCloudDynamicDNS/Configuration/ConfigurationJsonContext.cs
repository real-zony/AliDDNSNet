using System.Text.Json;
using System.Text.Json.Serialization;

namespace AliCloudDynamicDNS.Configuration;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ConfigurationModel))]
internal partial class ConfigurationJsonContext : JsonSerializerContext;
