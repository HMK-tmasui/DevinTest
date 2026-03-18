using HscTool.Diagnostics;
using HscTool.Json.Application;
using HscTool.Shared;
using System.Threading.Tasks;

namespace HscTool.Web;

public abstract class HscWebClient(ProgramSettings settings, string workDir, int timeoutSec) : IWebClientParam, IWebClientFactory
{
	public virtual IRateLimiter RateLimiter { get; } = new RateLimiter(settings.App);
	public virtual IHttpProxy Proxy { get; } = settings.HttpProxy;
	public virtual string UserAgent { get; } = settings.App.UserAgent ?? string.Empty;
	public virtual string WorkDir { get; set; } = workDir;
	public virtual int TimeoutSec { get; } = timeoutSec;

	public IHscHttpClient CreateApiClient(ILogger logger)　=> new HscHttpClient(logger, this);

	/// <summary>
	/// IWebScraperの生成
	/// </summary>
	/// <param name="logger"></param>
	/// <returns></returns>
	public async Task<IWebScraper> CreateScraper(ILogger logger) => new ChromeWebScraper(logger, await ChromeOptionFactory.Create(logger, this));

	public async Task<IWebScraper> CreateScraper(ILogger logger, string langTag)
	{
		var option = await ChromeOptionFactory.Create(logger, this, langTag);
		return new ChromeWebScraper(logger, option);
	}
}

public class JvnApiClient(ProgramSettings settings, string workDir) : HscWebClient(settings, workDir, settings.Jvn.ApiTimeoutSec)
{
	public override IRateLimiter RateLimiter { get; } = new RateLimiter(settings.Jvn);
}

public class NvdApiClient(ProgramSettings settings, string workDir) : HscWebClient(settings, workDir, settings.Nvd.ApiTimeoutSec)
{
	public override IRateLimiter RateLimiter { get; } = new RateLimiter(settings.Nvd);
}

public class CvrfApiClient(ProgramSettings settings, string workDir, int timeoutSec = 10) : HscWebClient(settings, workDir, timeoutSec)
{
	public override IRateLimiter RateLimiter { get; } = new RateLimiter(settings.Cvrf);
}

public class MicrosoftWebClient(ProgramSettings settings, string workDir, int timeoutSec = 10) : HscWebClient(settings, workDir, timeoutSec)
{
	public override IRateLimiter RateLimiter { get; } = new RateLimiter(settings.Microsoft);
}

public class AdobeWebClient(ProgramSettings settings, string workDir, int timeoutSec = 10) : HscWebClient(settings, workDir, timeoutSec)
{
	public override IRateLimiter RateLimiter { get; } = new RateLimiter(settings.Adobe);
}

public class JustSystemsWebClient(ProgramSettings settings, string workDir, int timeoutSec = 10) : HscWebClient(settings, workDir, timeoutSec)
{
	public override IRateLimiter RateLimiter { get; } = new RateLimiter(settings.JustSystems);
}

public class VendorWebClient(ProgramSettings settings, string cacheDir, int timeoutSec = 10) : HscWebClient(settings, cacheDir, timeoutSec);