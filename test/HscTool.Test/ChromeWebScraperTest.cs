using HscTool.Scraper.Model;
using HscTool.Shared;
using HscTool.Test.Fixture;
using HscTool.Test.Server;
using HscTool.Web;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace HscTool.Test;

public class ChromeWebScraperTestFixture : IDisposable
{
    public TestProxyServer ProxyServer { get; }
    public TestWebServer WebServer { get; }

    public ChromeWebScraperTestFixture()
    {
        ProxyServer = new TestProxyServer();
        WebServer = new TestWebServer();
    }

    public void Dispose()
    {
        WebServer.Dispose();
        ProxyServer.Dispose();
    }
}

[CollectionDefinition("ChromeWebScraperTests")]
public class ChromeWebScraperTestCollection : ICollectionFixture<ChromeWebScraperTestFixture>
{
}

[Collection("ChromeWebScraperTests")]
public class ChromeWebScraperTest : HscToolTestLogFixture, IClassFixture<ChromeWebScraperTestFixture>, IDisposable
{
    private readonly string _testCacheDir;
    private readonly ChromeWebScraperTestFixture _fixture;

    public ChromeWebScraperTest(ChromeWebScraperTestFixture fixture, ITestOutputHelper output) : base(output)
    {
        _fixture = fixture;
        _testCacheDir = Path.Combine(Path.GetTempPath(), $"HscToolTest_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testCacheDir);

        Logger.LogDebug($"Proxy server at {_fixture.ProxyServer.Host}:{_fixture.ProxyServer.Port}");
        Logger.LogDebug($"Web server at {_fixture.WebServer.Url}");
    }

    public override void Dispose()
    {
        base.Dispose();
        try
        {
            if (Directory.Exists(_testCacheDir))
            {
                Directory.Delete(_testCacheDir, true);
            }
        }
        catch { }
    }

    private MockWebClientParam CreateDefaultParam()
    {
        return new MockWebClientParam
        {
            WorkDir = _testCacheDir,
            UserAgent = "TestUserAgent/1.0",
            RateLimiter = new MockRateLimiter(),
            Proxy = new MockHttpProxy
            {
                UseProxy = true,
                DefaultProxy = false,
                Host = _fixture.ProxyServer.Host,
                Port = _fixture.ProxyServer.Port,
                User = ""
            }
        };
    }

    private MockWebClientParam CreateNoProxyParam()
    {
        return new MockWebClientParam
        {
            WorkDir = _testCacheDir,
            UserAgent = "TestUserAgent/1.0",
            RateLimiter = new MockRateLimiter(),
            Proxy = new MockHttpProxy { UseProxy = false }
        };
    }

    #region Lifecycle Tests

    [Fact]
    public async Task Constructor_CreatesChromeWebScraperSuccessfully()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        Assert.NotNull(scraper);
        Logger.LogDebug("ChromeWebScraper created successfully");
    }

    [Fact]
    public async Task Dispose_CanBeCalledMultipleTimes()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        var scraper = new ChromeWebScraper(Logger, factory);
        scraper.Dispose();
        scraper.Dispose();

        Logger.LogDebug("Dispose called multiple times without exception");
    }

    [Fact]
    public async Task Url_ReturnsCurrentUrl()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        var url = scraper.Url;
        Assert.NotNull(url);
        Logger.LogDebug($"Initial URL: {url}");
    }

    #endregion

    #region ResetNavigation Tests

    [Fact]
    public async Task ResetNavigation_NavigatesToUrl()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        await scraper.ResetNavigation(_fixture.WebServer.Url);

        Assert.Contains(_fixture.WebServer.Host, scraper.Url);
        Logger.LogDebug($"Navigated to: {scraper.Url}");
    }

    [Fact]
    public async Task ResetNavigation_SavesHtmlToFile()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        await scraper.ResetNavigation(_fixture.WebServer.Url);

        var chromeDir = factory.ChromeWorkDir;
        var htmlFiles = Directory.GetFiles(chromeDir, "*.html");
        Assert.NotEmpty(htmlFiles);
        Logger.LogDebug($"HTML file saved: {htmlFiles[0]}");

        var content = await File.ReadAllTextAsync(htmlFiles[0], TestContext.Current.CancellationToken);
        Assert.Contains("Test Content", content);
        Logger.LogDebug($"HTML content verified, length: {content.Length}");
    }

    [Fact]
    public async Task ResetNavigation_CreatesUniqueFilesForDifferentUrls()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        await scraper.ResetNavigation(_fixture.WebServer.Url);
        await scraper.ResetNavigation(_fixture.WebServer.FormUrl);
        await scraper.ResetNavigation(_fixture.WebServer.ScraperUrl);

        var chromeDir = factory.ChromeWorkDir;
        var htmlFiles = Directory.GetFiles(chromeDir, "*.html");
        Assert.True(htmlFiles.Length >= 3, $"Expected at least 3 HTML files, got {htmlFiles.Length}");
        Logger.LogDebug($"Created {htmlFiles.Length} HTML files for different URLs");
    }

    [Fact]
    public async Task ResetNavigation_WithQueryParameters_SavesCorrectly()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        await scraper.ResetNavigation($"{_fixture.WebServer.QueryUrl}?param1=value1&param2=value2");

        var chromeDir = factory.ChromeWorkDir;
        var htmlFiles = Directory.GetFiles(chromeDir, "*.html");
        Assert.NotEmpty(htmlFiles);
        Logger.LogDebug($"Query URL saved to: {htmlFiles[0]}");
    }

    #endregion

    #region WaitCondition Tests

    [Fact]
    public async Task ResetNavigation_WithElementCountCondition_WaitsForElements()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        var config = new DynamicContentConfig
        {
            TimeoutSeconds = 5,
            WaitConditions = new System.Collections.Generic.List<WaitCondition>
            {
                new WaitCondition(WaitConditionType.ElementCount)
                {
                    Selector = ".item",
                    MinCount = 2
                }
            }
        };

        await scraper.ResetNavigation(_fixture.WebServer.DynamicUrl, config);

        Assert.Contains(_fixture.WebServer.Host, scraper.Url);
        Logger.LogDebug("ElementCount condition satisfied");
    }

    [Fact]
    public async Task ResetNavigation_WithElementExistsCondition_WaitsForElement()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        var config = new DynamicContentConfig
        {
            TimeoutSeconds = 5,
            WaitConditions = new System.Collections.Generic.List<WaitCondition>
            {
                new WaitCondition(WaitConditionType.ElementExists)
                {
                    Selector = "#exists"
                }
            }
        };

        await scraper.ResetNavigation(_fixture.WebServer.DynamicUrl, config);

        Assert.Contains(_fixture.WebServer.Host, scraper.Url);
        Logger.LogDebug("ElementExists condition satisfied");
    }

    [Fact]
    public async Task ResetNavigation_WithElementVisibleCondition_WaitsForVisibility()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        var config = new DynamicContentConfig
        {
            TimeoutSeconds = 5,
            WaitConditions = new System.Collections.Generic.List<WaitCondition>
            {
                new WaitCondition(WaitConditionType.ElementVisible)
                {
                    Selector = "#visible"
                }
            }
        };

        await scraper.ResetNavigation(_fixture.WebServer.DynamicUrl, config);

        Assert.Contains(_fixture.WebServer.Host, scraper.Url);
        Logger.LogDebug("ElementVisible condition satisfied");
    }

    [Fact]
    public async Task ResetNavigation_WithTextContainsCondition_WaitsForText()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        var config = new DynamicContentConfig
        {
            TimeoutSeconds = 5,
            WaitConditions = new System.Collections.Generic.List<WaitCondition>
            {
                new WaitCondition(WaitConditionType.TextContains)
                {
                    Selector = "#text",
                    ExpectedText = "Ready"
                }
            }
        };

        await scraper.ResetNavigation(_fixture.WebServer.DynamicUrl, config);

        Assert.Contains(_fixture.WebServer.Host, scraper.Url);
        Logger.LogDebug("TextContains condition satisfied");
    }

    [Fact]
    public async Task ResetNavigation_WithJavaScriptCondition_WaitsForCondition()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        var config = new DynamicContentConfig
        {
            TimeoutSeconds = 5,
            WaitConditions = new System.Collections.Generic.List<WaitCondition>
            {
                new WaitCondition(WaitConditionType.JavaScriptCondition)
                {
                    JavaScriptCondition = "window.__ready === true"
                }
            }
        };

        await scraper.ResetNavigation(_fixture.WebServer.DynamicUrl, config);

        Assert.Contains(_fixture.WebServer.Host, scraper.Url);
        Logger.LogDebug("JavaScriptCondition satisfied");
    }

    [Fact]
    public async Task ResetNavigation_WithTimeoutCondition_DoesNotThrow()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        var config = new DynamicContentConfig
        {
            TimeoutSeconds = 1,
            WaitConditions = new System.Collections.Generic.List<WaitCondition>
            {
                new WaitCondition(WaitConditionType.ElementExists)
                {
                    Selector = "#nonexistent-element"
                }
            }
        };

        await scraper.ResetNavigation(_fixture.WebServer.Url, config);

        Assert.Contains(_fixture.WebServer.Host, scraper.Url);
        Logger.LogDebug("Timeout condition handled gracefully (no exception)");
    }

    #endregion

    #region ScrapeInPage Success Tests

    [Fact]
    public async Task ScrapeInPage_WithValidScript_ReturnsResult()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        var script = @"return JSON.stringify({ error_code: 0, error_msg: '', logs: 'success' });";

        var result = await scraper.ScrapeInPage<HscScraperError>(_fixture.WebServer.ScraperUrl, script);

        Assert.NotNull(result);
        Assert.Equal(0, result.ErrorCode);
        Assert.Equal("success", result.Logs);
        Logger.LogDebug($"Scraper result: ErrorCode={result.ErrorCode}, Logs={result.Logs}");
    }

    [Fact]
    public async Task ScrapeInPage_WithHyperLinkResult_ReturnsHyperLink()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        var script = @"
            var link = document.getElementById('link1');
            return JSON.stringify({
                error_code: 0,
                error_msg: '',
                logs: '',
                text: link ? link.innerText : '',
                href: link ? link.href : ''
            });
        ";

        var result = await scraper.ScrapeInPage<HyperLink>(_fixture.WebServer.ScraperUrl, script);

        Assert.NotNull(result);
        Assert.Equal(0, result.ErrorCode);
        Assert.Equal("Example Link", result.Text);
        Assert.Contains("example.com", result.Href);
        Logger.LogDebug($"HyperLink result: Text={result.Text}, Href={result.Href}");
    }

    [Fact]
    public async Task ScrapeInPage_WithArguments_PassesArgumentsToScript()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        var script = @"
            var arg1 = arguments[0];
            var arg2 = arguments[1];
            return JSON.stringify({
                error_code: 0,
                error_msg: '',
                logs: 'arg1=' + arg1 + ',arg2=' + arg2
            });
        ";

        var result = await scraper.ScrapeInPage<HscScraperError>(_fixture.WebServer.ScraperUrl, script, "value1", 42);

        Assert.NotNull(result);
        Assert.Equal(0, result.ErrorCode);
        Assert.Contains("arg1=value1", result.Logs);
        Assert.Contains("arg2=42", result.Logs);
        Logger.LogDebug($"Arguments passed correctly: {result.Logs}");
    }

    [Fact]
    public async Task ScrapeInPage_WithDynamicContentConfig_WaitsBeforeScraping()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        var config = new DynamicContentConfig
        {
            TimeoutSeconds = 5,
            WaitConditions = new System.Collections.Generic.List<WaitCondition>
            {
                new WaitCondition(WaitConditionType.ElementCount)
                {
                    Selector = ".item",
                    MinCount = 2
                }
            }
        };

        var script = @"
            var items = document.querySelectorAll('.item');
            return JSON.stringify({
                error_code: 0,
                error_msg: '',
                logs: 'items=' + items.length
            });
        ";

        var result = await scraper.ScrapeInPage<HscScraperError>(_fixture.WebServer.DynamicUrl, script, config);

        Assert.NotNull(result);
        Assert.Equal(0, result.ErrorCode);
        Assert.Contains("items=2", result.Logs);
        Logger.LogDebug($"Dynamic content scraped: {result.Logs}");
    }

    #endregion

    #region ScrapeInPage Error Tests

    [Fact]
    public async Task ScrapeInPage_WithErrorCode_ThrowsWebScraperException()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        var script = @"return JSON.stringify({ error_code: 100, error_msg: 'Test Error', logs: '' });";

        var ex = await Assert.ThrowsAsync<WebScraperException>(
            () => scraper.ScrapeInPage<HscScraperError>(_fixture.WebServer.ScraperUrl, script));

        Assert.Equal(100, ex.ErrorCode);
        Assert.Equal("Test Error", ex.Message);
        Logger.LogDebug($"WebScraperException thrown: ErrorCode={ex.ErrorCode}, Message={ex.Message}");
    }

    [Fact]
    public async Task ScrapeInPage_WithNullResult_ThrowsInvalidOperationException()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        var script = @"return null;";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scraper.ScrapeInPage<HscScraperError>(_fixture.WebServer.ScraperUrl, script));

        Logger.LogDebug("InvalidOperationException thrown for null result");
    }

    [Fact]
    public async Task ScrapeInPage_WithInvalidJson_ThrowsException()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        var script = @"return 'invalid json {';";

        await Assert.ThrowsAnyAsync<Exception>(
            () => scraper.ScrapeInPage<HscScraperError>(_fixture.WebServer.ScraperUrl, script));

        Logger.LogDebug("Exception thrown for invalid JSON");
    }

    [Fact]
    public async Task ScrapeInPage_WithNegativeErrorCode_ThrowsWebScraperException()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        var script = @"return JSON.stringify({ error_code: -99, error_msg: 'UNKNOWN_ERROR', logs: '' });";

        var ex = await Assert.ThrowsAsync<WebScraperException>(
            () => scraper.ScrapeInPage<HscScraperError>(_fixture.WebServer.ScraperUrl, script));

        Assert.Equal(-99, ex.ErrorCode);
        Assert.Equal("UNKNOWN_ERROR", ex.Message);
        Logger.LogDebug($"Negative error code handled: {ex.ErrorCode}");
    }

    #endregion

    #region Proxy Tests

    [Fact]
    public async Task ChromeWebScraper_WithProxy_NavigatesThroughProxy()
    {
        var param = CreateDefaultParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        var initialCount = _fixture.ProxyServer.RequestCount;

        using var scraper = new ChromeWebScraper(Logger, factory);

        await scraper.ResetNavigation(_fixture.WebServer.Url);

        var finalCount = _fixture.ProxyServer.RequestCount;
        Assert.True(finalCount > initialCount, $"Proxy should have received requests. Initial: {initialCount}, Final: {finalCount}");
        Logger.LogDebug($"Proxy request count increased from {initialCount} to {finalCount}");
    }

    [Fact]
    public async Task ChromeWebScraper_WithProxy_CanScrape()
    {
        var param = CreateDefaultParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        var script = @"return JSON.stringify({ error_code: 0, error_msg: '', logs: 'proxy-test' });";

        var result = await scraper.ScrapeInPage<HscScraperError>(_fixture.WebServer.ScraperUrl, script);

        Assert.NotNull(result);
        Assert.Equal(0, result.ErrorCode);
        Assert.Equal("proxy-test", result.Logs);
        Logger.LogDebug($"Scraping through proxy successful: {result.Logs}");
    }

    #endregion

    #region RateLimiter Tests

    [Fact]
    public async Task ChromeWebScraper_CallsRateLimiter()
    {
        var param = CreateNoProxyParam();
        var countingRateLimiter = new CountingRateLimiter();
        param.RateLimiter = countingRateLimiter;

        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        var initialCount = countingRateLimiter.WaitCount;

        await scraper.ResetNavigation(_fixture.WebServer.Url);

        Assert.True(countingRateLimiter.WaitCount > initialCount, 
            $"RateLimiter should have been called. Initial: {initialCount}, Final: {countingRateLimiter.WaitCount}");
        Logger.LogDebug($"RateLimiter called {countingRateLimiter.WaitCount - initialCount} times");
    }

    [Fact]
    public async Task ChromeWebScraper_MultipleNavigations_CallsRateLimiterEachTime()
    {
        var param = CreateNoProxyParam();
        var countingRateLimiter = new CountingRateLimiter();
        param.RateLimiter = countingRateLimiter;

        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        await scraper.ResetNavigation(_fixture.WebServer.Url);
        var countAfterFirst = countingRateLimiter.WaitCount;

        await scraper.ResetNavigation(_fixture.WebServer.FormUrl);
        var countAfterSecond = countingRateLimiter.WaitCount;

        await scraper.ResetNavigation(_fixture.WebServer.ScraperUrl);
        var countAfterThird = countingRateLimiter.WaitCount;

        Assert.True(countAfterSecond > countAfterFirst, "RateLimiter should be called on second navigation");
        Assert.True(countAfterThird > countAfterSecond, "RateLimiter should be called on third navigation");
        Logger.LogDebug($"RateLimiter counts: {countAfterFirst}, {countAfterSecond}, {countAfterThird}");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task ScrapeInPage_WithComplexScript_ExecutesCorrectly()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        var script = @"
            try {
                var content = document.getElementById('content');
                var data = document.getElementById('data');
                return JSON.stringify({
                    error_code: 0,
                    error_msg: '',
                    logs: 'content=' + (content ? content.innerText : 'null') + ',data=' + (data ? data.getAttribute('data-value') : 'null')
                });
            } catch (error) {
                return JSON.stringify({
                    error_code: -99,
                    error_msg: error.message,
                    logs: ''
                });
            }
        ";

        var result = await scraper.ScrapeInPage<HscScraperError>(_fixture.WebServer.ScraperUrl, script);

        Assert.NotNull(result);
        Assert.Equal(0, result.ErrorCode);
        Assert.Contains("content=Test Content for Scraping", result.Logs);
        Assert.Contains("data=12345", result.Logs);
        Logger.LogDebug($"Complex script result: {result.Logs}");
    }

    [Fact]
    public async Task ResetNavigation_WithNullDynamicConfig_WorksCorrectly()
    {
        var param = CreateNoProxyParam();
        var factory = await ChromeOptionFactory.Create(Logger, param);

        using var scraper = new ChromeWebScraper(Logger, factory);

        await scraper.ResetNavigation(_fixture.WebServer.Url, null);

        Assert.Contains(_fixture.WebServer.Host, scraper.Url);
        Logger.LogDebug("Navigation with null config successful");
    }

    #endregion
}

public class CountingRateLimiter : IRateLimiter
{
    private int _waitCount;

    public int WaitCount => _waitCount;

    public Task WaitAsync(Action? onWait = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _waitCount);
        onWait?.Invoke();
        return Task.CompletedTask;
    }
}
