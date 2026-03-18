using HscTool.Shared;

namespace HscTool.Web;

public interface IWebClientParam
{
	IRateLimiter RateLimiter { get; }
	string WorkDir { get; }
	IHttpProxy Proxy { get; }
	string UserAgent { get; }
	int TimeoutSec { get; }
}