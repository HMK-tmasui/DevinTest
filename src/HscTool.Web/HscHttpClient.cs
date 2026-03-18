using HscTool.Diagnostics;
using HscTool.Shared;
using HscTool.Common;
using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace HscTool.Web;

public enum HscHttpMethod
{
	GET,
	POST,
	PATCH,
	PUT,
	DELETE,
	HEAD
}

public class HscHttpClient : ProcessingContext, IHscHttpClient
{
	private readonly HttpClient _http;
	private readonly IRateLimiter _rateLimiter;

	private bool _disposed;

	public Task<HttpResponseMessage> GetAsync(string? requestUri) => _http.GetAsync(requestUri);
	public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) => _http.SendAsync(request);
	public Task<HttpResponseMessage> PostAsync(string? requestUri, HttpContent? content) => _http.PostAsync(requestUri, content);
	public Task<HttpResponseMessage> PutAsync(string? requestUri, HttpContent? content) => _http.PutAsync(requestUri, content);
	public Task<HttpResponseMessage> DeleteAsync(string? requestUri) => _http.DeleteAsync(requestUri);
	public Task<string> GetStringAsync(string? requestUri) => _http.GetStringAsync(requestUri);
	public HttpRequestHeaders DefaultRequestHeaders => _http.DefaultRequestHeaders;
	public TimeSpan Timeout
	{
		get => _http.Timeout;
		set => _http.Timeout = value;
	}

	/// <summary>
	/// コンストラクタ
	/// </summary>
	public HscHttpClient(ILogger logger, IWebClientParam apiClientParam) : base(logger)
	{
		_http = new HttpClient(CreateHttpClientHandler(logger, apiClientParam.Proxy));

		_rateLimiter = apiClientParam.RateLimiter;

		var userAgent = apiClientParam.UserAgent;
		if (!string.IsNullOrEmpty(userAgent))
		{
			_http.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
		}
		// HttpClient のタイムアウトを 10 秒に設定
		_http.Timeout = TimeSpan.FromSeconds(apiClientParam.TimeoutSec);
	}
	/// <summary>
	/// Send a message to Microsoft Teams. 
	/// </summary>
	/// <param name="text">The message text to send.</param>
	/// <param name="channel">The Teams channel URL.</param>
	/// <exception cref="CloudCheckerException">Thrown when text or channel is null or empty.</exception>
	/// <returns>Task</returns>
	public async Task PostTeams(string message, string? channel)
	{
		if (string.IsNullOrWhiteSpace(message) || string.IsNullOrWhiteSpace(message))
		{
			throw new ArgumentNullException("text or channel is null or empty");
		}

		using var post = new StringContent(message, Encoding.UTF8, "application/json");

		var response = await _http.PostAsync(channel, post).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();
		var content = await response.Content.ReadAsStringAsync();
	}

	/// <summary>
	/// 指定されたURLのファイルサイズを取得します。
	/// </summary>
	/// <param name="url">ファイルのURL</param>
	/// <returns>ファイルサイズ（バイト単位）</returns>
	/// <exception cref="InvalidOperationException">ファイルサイズが取得できない場合</exception>
	public async Task<long> GetFileSize(string url, int retry)
	{
		HttpResponseMessage? response = null;
		for (int i = 0; i < retry; i++)
		{
			try
			{
				await _rateLimiter.WaitAsync();
				using HttpRequestMessage request = new(HttpMethod.Get, url);
				request.Headers.Range = new RangeHeaderValue(0, 0); // Request only first byte

				// HEADリクエストを送信
				response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

				// ステータスコードを確認
				response.EnsureSuccessStatusCode();

				var contentRange = response.Content.Headers.ContentRange;
				if (contentRange?.Length.HasValue == false) throw new InvalidOperationException($"File size not found[{url}]");

				 return contentRange!.Length!.Value;
			}
			catch (Exception ex)
			{
				if (response is not null)
				{
					var sb = new StringBuilder();
					sb.AppendLine($"HTTP/{response.Version} {(int)response.StatusCode} {response.StatusCode}");
					// レスポンスヘッダー
					foreach (var header in response.Headers)
					{
						sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
					}
					// コンテンツヘッダー
					foreach (var header in response.Content.Headers)
					{
						sb.AppendLine($"{header.Key}: {string.Join(", ", header.Value)}");
					}

					// ログ出力（デバッグレベル）
					Logger.LogWarning($"{ToolSet.GetCurrentMethod()}() [{sb.ToString()}]");
				}
				if (i + 1 < retry)
				{
					Logger.LogWarning($"{ToolSet.GetCurrentMethod()}() Retry[{i}]: {ex.Message}");
					continue;
				}
				throw;
			}
			finally
			{
				response?.Dispose();
				response = null;
			}
		}

		throw new InvalidOperationException($"{ToolSet.GetCurrentMethod()}() Failed [{retry}] retries.");
	}
	/// <summary>
	/// 
	/// </summary>
	/// <param name="proxy"></param>
	/// <param name="SslProtocols"></param>
	/// <returns></returns>
	private static HttpClientHandler CreateHttpClientHandler(ILogger logger, IHttpProxy? proxy = default)
	{
		//SSLは自動選択
		HttpClientHandler handler = new()
		{
			AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
		};

		// プロキシ設定
		if (!(proxy?.UseProxy ?? false)) return handler;

		handler.UseProxy = proxy.UseProxy;

		if (proxy.DefaultProxy)
		{
			handler.Proxy = WebRequest.DefaultWebProxy;
			return handler;
		}

		try
		{

			proxy.AssertProxySettings();

			handler.Proxy = new WebProxy()
			{
				Address = new Uri($"http://{proxy.Host}:{proxy.Port}"),
				Credentials = new NetworkCredential(proxy.User, proxy.Password)
			};

		}
		catch (Exception ex)
		{
			logger.LogError($"{ToolSet.GetCurrentMethod()}() Proxy configuration error. Proxy disabled: {proxy.Host ?? "unknown"}:{proxy.Port} message:{ex.Message}");
			handler.UseProxy = false;
			handler.Proxy = null;
		}

		return handler;
	}

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _http.Dispose();
    }
}