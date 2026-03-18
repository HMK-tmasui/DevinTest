using HscTool.Diagnostics;
using HscTool.Shared;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using System;
using System.IO;
using System.Threading.Tasks;

namespace HscTool.Web;

public class ChromeOptionFactory
{
	public string ChromeWorkDir { get; }
	public string LangTag { get; set; } = "en-US";
	public string UserAgent { get; } = string.Empty;
	public IRateLimiter RateLimiter{ get; }
	public string ChromeLogPath { get; }
	public ChromeOptions Options { get; } = new ChromeOptions();

	private readonly ILogger _logger;
	private readonly IWebClientParam _apiClientParam;

	/// <summary>
	/// 古いユーザーデータディレクトリをクリーンアップ
	/// </summary>
	/// <param name="chromeDir"></param>
	private static void CleanupUserDataDirectories(string chromeDir)
	{
		var userDataDirs = Directory.GetDirectories(chromeDir, "chrome_user_data_*");
		foreach (var dir in userDataDirs)
		{
			try
			{
				var dirInfo = new DirectoryInfo(dir);
				if (DateTime.Now - dirInfo.CreationTime > TimeSpan.FromHours(1))
				{
					Directory.Delete(dir, true);
				}
			}
			catch
			{
			}
		}
	}

	public static async Task<ChromeOptionFactory> Create(ILogger logger, IWebClientParam apiClientParam, string? langTag = null)
	{
		var factory = new ChromeOptionFactory(logger, apiClientParam);

		if (!string.IsNullOrEmpty(langTag)) factory.LangTag = langTag;

		await factory.Initialize();

		return factory;
	}

	/// <summary>
	/// コンストラクタ
	/// </summary>
	/// <param name="logger"></param>
	private ChromeOptionFactory(ILogger logger, IWebClientParam apiClientParam)
	{
		_apiClientParam = apiClientParam;
		_logger = logger;
		UserAgent = apiClientParam.UserAgent;
		RateLimiter = apiClientParam.RateLimiter;
		ChromeWorkDir = Path.Combine(apiClientParam.WorkDir, "chrome");
		ChromeLogPath = Path.Combine(ChromeWorkDir, "chromedriver.log");

	}

	private async Task Initialize()
	{
		try
		{
			Directory.CreateDirectory(ChromeWorkDir);

#if DEBUG
			//service.EnableVerboseLogging = true;
#endif
			var finder = new DriverFinder(Options);
			string driverPath = await finder.GetDriverPathAsync();
			string browserPath = await finder.GetBrowserPathAsync();

			_logger.LogInfo(
				$"Chrome Log[{ChromeLogPath}]" +
				$"ChromeDrive[{driverPath}]" +
				$"Browser[{browserPath}]"
				);

			// 古いユーザーデータディレクトリをクリーンアップ
			CleanupUserDataDirectories(ChromeWorkDir);

			// ChromeDriverのオプションを作成

			Options.AddArgument("--headless"); // ヘッドレスモードで実行（画面を表示しない）ボット判定リスクあり

			string uniqueUserDataDir = Path.Combine(ChromeWorkDir, $"chrome_user_data_{DateTime.Now.Ticks}_{Environment.ProcessId}");
			Directory.CreateDirectory(uniqueUserDataDir);
			Options.AddArgument($"--user-data-dir={uniqueUserDataDir}");

			Options.AddArgument("--no-sandbox");
			Options.AddArgument("--disable-dev-shm-usage");
			Options.AddArgument("--disable-gpu");
			Options.AddArgument("--disable-extensions");
			Options.AddArgument("--no-first-run");
			Options.AddArgument("--no-default-browser-check");
			//言語設定
			Options.AddArgument($"--lang={LangTag}");
			Options.AddArgument($"--accept-lang={LangTag}");

			Options.AddUserProfilePreference("intl.accept_languages", $"{LangTag},en;q=0.9");
			Options.AddUserProfilePreference("intl.charset_default", "UTF-8");

			Options.AddUserProfilePreference("download.prompt_for_download", false);
			Options.AddUserProfilePreference("download.default_directory", ""); // ダウンロードを無効化

			if (_apiClientParam.Proxy.UseProxy == true)
			{
				var proxy = _apiClientParam.Proxy;
				proxy.AssertProxySettings();
				string proxyArg = proxy.DefaultProxy
					? "--proxy-auto-detect"
					: GetChromeDriverProxyArg(proxy);

				Options.AddArgument(proxyArg);
			}

			if (File.Exists(ChromeLogPath)) File.WriteAllText(ChromeLogPath, string.Empty);
		}
		catch (Exception ex)
		{
			_logger.LogError($"{GetType().Name}() [{ex.Message}]");
			throw;
		}
	}

	public ChromeDriverService CreateDriverService(string? driverPath = null)
	{
		var driverService = ChromeDriverService.CreateDefaultService(driverPath);
		driverService.LogPath = ChromeLogPath;
		driverService.HideCommandPromptWindow = true;

		return driverService;
	}
	/// Chromeのプロキシ設定を作成
	/// </summary>
	/// <param name="proxy"></param>
	/// <returns></returns>
	private string GetChromeDriverProxyArg(IHttpProxy proxy)
	{
		try
		{
			if (proxy.User != null)
			{
				return $"--proxy-server={proxy.Host}:{proxy.Port}";
			}
			// 拡張機能のスクリプトを作成
			string proxyAuthScript = $@"
            var config = {{
                mode: 'fixed_servers',
                rules: {{
                    singleProxy: {{
                        scheme: 'http',
                        host: '{proxy.Host}',
                        port: {proxy.Port}
                    }},
                    bypassList: []
                }}
            }};
            chrome.proxy.settings.set({{value: config, scope: 'regular'}}, function() {{}});
            chrome.webRequest.onAuthRequired.addListener(
                function(details) {{
                    return {{
                        authCredentials: {{
                            username: '{proxy.User}',
                            password: '{proxy.Password}'
                        }}
                    }};
                }},
                {{urls: ['<all_urls>']}},
                ['blocking']
            );
        ";

			// 拡張機能のディレクトリを作成
			string extensionDir = Path.Combine(ChromeWorkDir, "chrome-proxy-auth");
			if (Directory.Exists(extensionDir))
			{
				Directory.Delete(extensionDir, true);
				Directory.CreateDirectory(extensionDir);
			}

			File.WriteAllText(Path.Combine(extensionDir, "background.js"), proxyAuthScript);

			File.WriteAllText(Path.Combine(extensionDir, "manifest.json"), @"
		{
			""version"": ""1.0"",
			""manifest_version"": 2,
			""permissions"": [
				""proxy"",
				""tabs"",
				""unlimitedStorage"",
				""storage"",
				""<all_urls>"",
				""webRequest"",
				""webRequestBlocking""
			],
			""background"": {
				""scripts"": [""background.js""]
			}
		}");


			return "--load-extension=" + extensionDir;
		}
		catch (Exception ex)
		{
			_logger.LogError($"{ToolSet.GetCurrentMethod()}() {ex.Message}");
			throw;
		}
	}
}