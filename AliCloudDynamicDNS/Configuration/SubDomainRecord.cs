namespace AliCloudDynamicDNS.Configuration;

public sealed record SubDomainRecord(string Type, string SubDomain, int Interval);