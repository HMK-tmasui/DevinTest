using HscTool.Scraper.Model;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace HscTool.Web;

public class DynamicContentConfig
{
    public int TimeoutSeconds { get; set; } = 60;
    public List<WaitCondition> WaitConditions { get; set; } = [];
}

public class WaitCondition
{
    public WaitConditionType Type { get; set; }
    public string Selector { get; set; } = string.Empty;
    public int MinCount { get; set; }
    public string ExpectedText { get; set; } = string.Empty;
    public string JavaScriptCondition { get; set; } = string.Empty;

	public WaitCondition(WaitConditionType type)
	{
		Type = type;
	}
}

public enum WaitConditionType
{
    ElementCount,
    ElementExists,
    ElementVisible,
    TextContains,
    JavaScriptCondition
}

public interface IWebScraper : IDisposable
{
	string Url { get; }

	Task ResetNavigation(string baseUrl, DynamicContentConfig? dynamicContentConfig = default);
	Task<TModel> ScrapeInPage<TModel>(string url, string scraper, params object[] args) where TModel : HscScraperError;
	Task<TModel> ScrapeInPage<TModel>(string url, string scraper, DynamicContentConfig? dynamicContentConfig, params object[] args) where TModel : HscScraperError;
}
