namespace DomainSuffixParser;

public class DomainSuffix
{
    public string TLD { get; set; } = string.Empty;
    public string SecondLD { get; set; } = string.Empty;
    public DateTime LatestUpdate { get; set; }
}
