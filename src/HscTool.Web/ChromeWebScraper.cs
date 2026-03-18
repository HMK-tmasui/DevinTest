using HscTool.Diagnostics;
using HscTool.Scraper.Model;
using HscTool.Shared;
using Newtonsoft.Json;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace HscTool.Web;

public class WebScraperException : Exception
{
	public int ErrorCode { get; }

	public WebScraperException(int errorCode, string message)
		: base(message)
	{
		ErrorCode = errorCode;
	}

	public WebScraperException(string message)
		: base(message)
	{
	}

	public WebScraperException(string message, Exception innerException)
		: base(message, innerException)
	{
	}

	public WebScraperException()
	{
	}
}

public class ChromeWebScraper : IWebScraper
{
	private readonly ILogWatcher _watcher;
	private readonly ILogger _logger;
	private readonly ChromeDriver _chromeDriver;

	private string _chromeWorkDir;
	private JobManager _jobManager = new();
	private IRateLimiter _rateLimiter;
	private string  _userAgent;

	public string Url => _chromeDriver.Url;

	/// <summary>
	/// コンストラクタ
	/// </summary>
	/// <param name="options"></param>
	/// <exception cref="ArgumentNullException"></exception>
	public ChromeWebScraper(ILogger logger, ChromeOptionFactory options)
	{
		_chromeWorkDir = options.ChromeWorkDir;
		_watcher =  new LogWatcher(_logger, options.ChromeLogPath);
		_rateLimiter = options.RateLimiter;
		_userAgent = options.UserAgent;
		_logger = logger;

		
		// 生テキスト差分
		_watcher.OnText += (s, text) => _logger.LogDebug(text);

		// 行単位差分
		_watcher.OnLines += (s, lines) =>
		{
			foreach (var line in lines)
			{
				if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
					_logger.LogWarning($"ChromeDriver ERROR: {line}");
			}
		};

		_watcher.Start();

		_chromeDriver = new ChromeDriver(options.CreateDriverService(), options.Options);

		//windowsのみ
		if (OperatingSystem.IsWindows())
		{
			try
			{
				AddChromeDriverProcessToJob();

			}
			catch (Exception ex)
			{
				_logger.LogWarning($"Failed to add ChromeDriver process to Job: {ex.Message}");
			}
		}
	}


	/// <summary>
	/// URLから安全なファイル名を生成する
	/// </summary>
	/// <param name="url"></param>
	/// <returns></returns>
	private string CreateFileNameFromUrl(string url)
	{
		try
		{
			var uri = new Uri(url);
			// パス部を取得し、先頭/を除去
			string path = uri.AbsolutePath.TrimStart('/');
			// / を _ に置換
			string fileName = path.Replace('/', '_');
			// 空の場合は "root" などに
			if (string.IsNullOrEmpty(fileName))
				fileName = "root";

			// クエリ（パラメータ部）がある場合は、? を _ に置換して結合
			int qIndex = url.IndexOf('?');
			if (qIndex >= 0)
			{
				string paramPart = url.Substring(qIndex).Replace('?', '_');
				fileName += paramPart;
			}

			// ファイル名に使えない文字を '_' に置換して安全化
			var invalid = Path.GetInvalidFileNameChars();
			var chars = fileName.ToCharArray();
			for (int i = 0; i < chars.Length; i++)
			{
				if (invalid.Contains(chars[i]))
					chars[i] = '_';
			}
			fileName = new string(chars);

			// .html がなければ追加
			if (!string.Equals(Path.GetExtension(fileName), ".html", StringComparison.OrdinalIgnoreCase))
				fileName += ".html";

			return fileName;
		}
		catch(Exception ex)
		{
			_logger.LogError($"{ToolSet.GetCurrentMethod()}() [{url}]{ex.Message}");
			throw;
		}
	}
	[SupportedOSPlatform("windows")]
	private void AddChromeDriverProcessToJob()
	{
		var chromeDriverProcess = Process.GetProcessesByName("chromedriver").FirstOrDefault();
		if (chromeDriverProcess == null)
		{
			return;
		}
		using (chromeDriverProcess)
		{
			_jobManager.AddProcess(chromeDriverProcess.Handle);
			_logger.LogWarning($"test1");
			// chromedriver.exeの全ての子孫プロセスもジョブに追加
			foreach (var proc in GetDescendantProcesses(chromeDriverProcess.Id))
			{
				try
				{
					_jobManager.AddProcess(proc.Handle);
				}
				catch (Exception ex)
				{
					_logger.LogWarning($"Failed to add process {proc.ProcessName} (PID: {proc.Id}) to Job: {ex.Message}");
				}
			}
			_logger.LogWarning($"test2");
		}
	}
	[SupportedOSPlatform("windows")]
	private IEnumerable<Process> GetDescendantProcesses(int parentPid)
	{
		Dictionary<int, List<int>> childPidsByParentPid = [];
		Queue<int> pendingParentPids = new();
		HashSet<int> visitedPids = [parentPid];
		List<Process> descendants = [];

		using ToolHelp32Snapshot snapshot = new(ToolHelp32Snapshot.TH32CS_SNAPPROCESS);

		if (snapshot.TryGetFirst(out ToolHelp32Snapshot.PROCESSENTRY32 entry))
		{
			do
			{
				int pid = checked((int)entry.th32ProcessID);
				int ppid = checked((int)entry.th32ParentProcessID);

				if (!childPidsByParentPid.TryGetValue(ppid, out List<int>? childPids))
				{
					childPids = [];
					childPidsByParentPid[ppid] = childPids;
				}

				childPids.Add(pid);
			}
			while (snapshot.TryGetNext(out entry));
		}

		pendingParentPids.Enqueue(parentPid);

		while (pendingParentPids.Count > 0)
		{
			int currentParentPid = pendingParentPids.Dequeue();

			if (!childPidsByParentPid.TryGetValue(currentParentPid, out List<int>? childPids))
			{
				continue;
			}

			foreach (int childPid in childPids)
			{
				if (!visitedPids.Add(childPid))
				{
					continue;
				}

				try
				{
					Process child = Process.GetProcessById(childPid);
					descendants.Add(child);
					pendingParentPids.Enqueue(childPid);
				}
				catch (Exception ex) when (
					ex is InvalidOperationException or
					ArgumentException or
					System.ComponentModel.Win32Exception)
				{
					_logger.LogDebug(
						$"{ToolSet.GetCurrentMethod()}() Skip process while opening PID {childPid}: {ex.Message}");
				}
			}
		}

		return descendants;
	}
	/// <summary>
	/// コンテンツの読み込み条件に従い待機
	/// </summary>
	/// <param name="config"></param>
	private void WaitForDynamicContentLoaded(DynamicContentConfig config)
	{
		WebDriverWait wait = new (_chromeDriver, TimeSpan.FromSeconds(config.TimeoutSeconds));

		foreach (var condition in config.WaitConditions)
		{
			try
			{
				switch (condition.Type)
				{
					case WaitConditionType.ElementCount:
						wait.Until(d => d.FindElements(By.CssSelector(condition.Selector)).Count >= condition.MinCount);
						break;
					case WaitConditionType.ElementExists:
						wait.Until(d => d.FindElement(By.CssSelector(condition.Selector)));
						break;
					case WaitConditionType.ElementVisible:
						wait.Until(d =>
						{
							var element = d.FindElement(By.CssSelector(condition.Selector));
							return element.Displayed ? element : null;
						});
						break;
					case WaitConditionType.TextContains:
						wait.Until(d =>
						{
							var element = d.FindElement(By.CssSelector(condition.Selector));
							return element.Text.Contains(condition.ExpectedText) ? element : null;
						});
						break;
					case WaitConditionType.JavaScriptCondition:
						wait.Until(d =>
						{
							if (string.IsNullOrWhiteSpace(condition.JavaScriptCondition)) return false;
							var result = _chromeDriver.ExecuteScript($"return {condition.JavaScriptCondition}");
							return result is bool b && b;
						});
						break;
				}
			}
			catch (WebDriverTimeoutException)
			{
				_logger.LogWarning($"Timeout waiting for condition: {condition.Type} Selector: {condition.Selector}");
			}
			catch (Exception ex)
			{
				_logger.LogError($"{ToolSet.GetCurrentMethod()}() Error for {condition.Type} selector='{condition.Selector}': {ex.Message}");
				throw;
			}
		}
	}
	private void SetUserAgent()
	{
		string chromeVersion = _chromeDriver.Capabilities.GetCapability("browserVersion")?.ToString() ?? "140.0.0.0";

		var userAgent = $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{chromeVersion} Safari/537.36";

		var userAgentParams = new Dictionary<string, object?>
		{
			{ "userAgent", userAgent }
		};

		_chromeDriver.ExecuteCdpCommand("Network.setUserAgentOverride", userAgentParams);
	}
	/// <summary>
	/// ChromeDriverのナビゲーションをリセットします。
	/// </summary>
	/// <param name="driver"></param>
	/// <param name="baseUrl"></param>
	/// <param name="interval"></param>
	/// <param name="lastCallTime"></param>
	/// <returns></returns>
	public async Task ResetNavigation(string baseUrl, DynamicContentConfig? dynamicContentConfig = default)
	{
		_logger.LogInfo($"Chrome Navigate[{baseUrl}]");

		if(string.IsNullOrEmpty(_userAgent)) SetUserAgent();

		_chromeDriver.Navigate().GoToUrl("about:blank");

		await _rateLimiter.WaitAsync();

		_chromeDriver.Navigate().GoToUrl(baseUrl);

		// コンテンツの読み込み条件に従い待機（Task.Runでスレッドプールに委譲し、デッドロックを回避）
		if (dynamicContentConfig != null) await Task.Run(() => WaitForDynamicContentLoaded(dynamicContentConfig));

		var body = _chromeDriver.FindElement(By.TagName("body"));
		string? html = body.GetAttribute("innerHTML");
		if (html == null)
		{
			throw new InvalidOperationException("Failed to retrieve page content.");
		}

		//ロードしたHTMLを保存
		await File.WriteAllTextAsync(Path.Combine(_chromeWorkDir, CreateFileNameFromUrl(baseUrl)), html);

#if DEBUG
		_logger.LogDebug($"{ToolSet.GetCurrentMethod()}() Content[{ToolSet.GetContentSummary(html, 50)}]");
#endif
	}
	/// <summary>
	/// 指定したURLで指定したスクレイパーを実行して結果を取得する
	/// </summary>
	/// <typeparam name="TModel"></typeparam>
	/// <param name="url"></param>
	/// <param name="scraper"></param>
	/// <param name="args"></param>
	/// <returns></returns>
	public Task<TModel> ScrapeInPage<TModel>(string url, string scraper, params object[] args)
		where TModel : HscScraperError
	{
		return ScrapeInPage<TModel>(url, scraper, null, args);
	}
	/// <summary>
	/// 指定したURLで指定したスクレイパーを実行して結果を取得する
	/// </summary>
	/// <typeparam name="TModel"></typeparam>
	/// <param name="url"></param>
	/// <param name="scraper"></param>
	/// <param name="dynamicContentConfig"></param>
	/// <param name="args"></param>
	/// <returns></returns>
	public async Task<TModel> ScrapeInPage<TModel>(string url, string scraper, DynamicContentConfig? dynamicContentConfig, params object[] args)
		where TModel : HscScraperError
	{
		string? result = null;
		try
		{
			await ResetNavigation(url, dynamicContentConfig);

			result = _chromeDriver.ExecuteScript(scraper, args) as string
						 ?? throw new InvalidOperationException($"The execution result of {typeof(TModel).Name} is failed.");

			TModel? model = JsonConvert.DeserializeObject<TModel>(result);

			if (model == null) throw new InvalidOperationException($"scraper result is null");

			if (model.ErrorCode != 0) throw new WebScraperException(model.ErrorCode, model.ErrorMessage);

			return model;
		}
		catch (Exception ex)
		{
			_logger.LogError($"{ToolSet.GetCurrentMethod()}() [{url}][{ex.Message}]");
			throw;
		}
	}
	/// <summary>
	/// 破棄
	/// </summary>
	/// <param name="disposing"></param>
	public void Dispose()
	{
		try
		{
			_chromeDriver.Dispose();
			_jobManager.Dispose();
			_watcher.Dispose();
		}
		catch (Exception ex)
		{
			_logger.LogWarning($"Error during base disposal: {ex.Message}");
		}
	}
}
