using HscTool.Diagnostics;
using System.Threading.Tasks;

namespace HscTool.Web;

public interface IWebClientFactory
{
	IHscHttpClient CreateApiClient(ILogger logger);
	Task<IWebScraper> CreateScraper(ILogger logger);
	Task<IWebScraper> CreateScraper(ILogger logger, string langTag);
}
