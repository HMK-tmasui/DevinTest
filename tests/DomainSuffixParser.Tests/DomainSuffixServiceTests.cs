using DomainSuffixParser;

namespace DomainSuffixParser.Tests;

public class DomainSuffixServiceTests
{
    private readonly DomainSuffixService _service = new();
    private readonly DomainSuffixServiceBefore _serviceBefore = new();

    /// <summary>
    /// 修正前コードが ICANN/PRIVATE セクション内の全行をスキップし 0 件になることを確認
    /// </summary>
    [Fact]
    public void Before_ReturnsZero_BecauseIcannAndPrivateSectionsAreSkipped()
    {
        var input = """
            // ===BEGIN ICANN DOMAINS===

            // ac : http://nic.ac/rules.htm
            ac
            com.ac
            edu.ac

            // ===END ICANN DOMAINS===

            // ===BEGIN PRIVATE DOMAINS===

            // Submitted by ...
            app.example

            // ===END PRIVATE DOMAINS===
            """;

        var result = _serviceBefore.CreateDomainSuffix(input);

        Assert.Empty(result);
    }

    /// <summary>
    /// 修正後コードが ICANN/PRIVATE セクション内の 2LD/TLD を正しく抽出できることを確認
    /// </summary>
    [Fact]
    public void After_ReturnsEntries_FromIcannAndPrivateSections()
    {
        var input = """
            // ===BEGIN ICANN DOMAINS===

            // ac : http://nic.ac/rules.htm
            ac
            com.ac
            edu.ac

            // ===END ICANN DOMAINS===

            // ===BEGIN PRIVATE DOMAINS===

            // Submitted by ...
            app.example

            // ===END PRIVATE DOMAINS===
            """;

        var result = _service.CreateDomainSuffix(input);

        Assert.NotEmpty(result);
        Assert.Equal(3, result.Count); // com.ac, edu.ac, app.example
        Assert.Contains(result, x => x.SecondLD == "com" && x.TLD == "ac");
        Assert.Contains(result, x => x.SecondLD == "edu" && x.TLD == "ac");
        Assert.Contains(result, x => x.SecondLD == "app" && x.TLD == "example");
    }

    /// <summary>
    /// jp prefecture type names セクションが正しくスキップされることを確認
    /// </summary>
    [Fact]
    public void After_SkipsJpPrefectureTypeNames()
    {
        var input = """
            // ===BEGIN ICANN DOMAINS===

            com.jp
            // jp prefecture type names
            aichi.jp
            akita.jp
            aomori.jp

            net.jp

            // ===END ICANN DOMAINS===
            """;

        var result = _service.CreateDomainSuffix(input);

        // aichi.jp, akita.jp, aomori.jp はスキップされるべき
        Assert.DoesNotContain(result, x => x.SecondLD == "aichi" && x.TLD == "jp");
        Assert.DoesNotContain(result, x => x.SecondLD == "akita" && x.TLD == "jp");
        Assert.DoesNotContain(result, x => x.SecondLD == "aomori" && x.TLD == "jp");
        // com.jp と net.jp は抽出されるべき
        Assert.Contains(result, x => x.SecondLD == "com" && x.TLD == "jp");
        Assert.Contains(result, x => x.SecondLD == "net" && x.TLD == "jp");
    }

    /// <summary>
    /// kr geographical names セクションが正しくスキップされることを確認
    /// </summary>
    [Fact]
    public void After_SkipsKrGeographicalNames()
    {
        var input = """
            // ===BEGIN ICANN DOMAINS===

            com.kr
            // kr geographical names
            busan.kr
            daegu.kr

            net.kr

            // ===END ICANN DOMAINS===
            """;

        var result = _service.CreateDomainSuffix(input);

        Assert.DoesNotContain(result, x => x.SecondLD == "busan" && x.TLD == "kr");
        Assert.DoesNotContain(result, x => x.SecondLD == "daegu" && x.TLD == "kr");
        Assert.Contains(result, x => x.SecondLD == "com" && x.TLD == "kr");
        Assert.Contains(result, x => x.SecondLD == "net" && x.TLD == "kr");
    }

    /// <summary>
    /// TLD のみの行(例: "ac")、3LD以上の行(例: "sth.ac.at")、
    /// ワイルドカード行(例: "*.nom.br")が除外されることを確認
    /// </summary>
    [Fact]
    public void After_ExcludesTldOnly_ThreeLevelDomains_AndWildcards()
    {
        var input = """
            // ===BEGIN ICANN DOMAINS===

            ac
            com.ac
            sth.ac.at
            *.nom.br
            edu.ac

            // ===END ICANN DOMAINS===
            """;

        var result = _service.CreateDomainSuffix(input);

        Assert.Equal(2, result.Count); // com.ac, edu.ac のみ
        Assert.DoesNotContain(result, x => x.TLD == "ac" && x.SecondLD == "");
        Assert.DoesNotContain(result, x => x.SecondLD == "sth");
        Assert.DoesNotContain(result, x => x.SecondLD == "*");
    }

    /// <summary>
    /// コメント行と空行がスキップされることを確認
    /// </summary>
    [Fact]
    public void After_SkipsCommentsAndEmptyLines()
    {
        var input = """
            // ===BEGIN ICANN DOMAINS===

            // This is a comment
            com.ac

            // Another comment
            edu.ac

            // ===END ICANN DOMAINS===
            """;

        var result = _service.CreateDomainSuffix(input);

        Assert.Equal(2, result.Count);
    }

    /// <summary>
    /// 非英数字を含む行（例: 国際化ドメイン名）が除外されることを確認
    /// </summary>
    [Fact]
    public void After_ExcludesNonAlphaNumericDomains()
    {
        var input = """
            // ===BEGIN ICANN DOMAINS===

            com.ac
            aéroport.ci

            // ===END ICANN DOMAINS===
            """;

        var result = _service.CreateDomainSuffix(input);

        Assert.Single(result);
        Assert.Contains(result, x => x.SecondLD == "com" && x.TLD == "ac");
    }

    /// <summary>
    /// 実際の public_suffix_list.dat ファイルで修正後コードが
    /// 有効なエントリを多数抽出できることを確認する統合テスト
    /// </summary>
    [Fact]
    public void After_WithRealData_ReturnsNonZeroEntries()
    {
        var testDataPath = GetTestDataPath();
        if (!File.Exists(testDataPath))
        {
            // テストデータファイルが無い場合はスキップ
            return;
        }

        var suffixList = File.ReadAllText(testDataPath);

        var result = _service.CreateDomainSuffix(suffixList);

        // 実データから大量の 2LD/TLD が取得できることを確認
        Assert.True(result.Count > 100,
            $"Expected more than 100 entries but got {result.Count}");

        // 代表的な 2LD/TLD が含まれていることを確認
        Assert.Contains(result, x => x.SecondLD == "com" && x.TLD == "ac");
        Assert.Contains(result, x => x.SecondLD == "co" && x.TLD == "jp");
        Assert.Contains(result, x => x.SecondLD == "com" && x.TLD == "au");
    }

    /// <summary>
    /// 実際の public_suffix_list.dat ファイルで修正前コードが 0 件になることを確認
    /// </summary>
    [Fact]
    public void Before_WithRealData_ReturnsZeroEntries()
    {
        var testDataPath = GetTestDataPath();
        if (!File.Exists(testDataPath))
        {
            return;
        }

        var suffixList = File.ReadAllText(testDataPath);

        var result = _serviceBefore.CreateDomainSuffix(suffixList);

        Assert.Empty(result);
    }

    private static string GetTestDataPath()
    {
        // テストデータは tests/DomainSuffixParser.Tests/TestData/public_suffix_list.dat に配置
        var dir = AppContext.BaseDirectory;
        return Path.Combine(dir, "TestData", "public_suffix_list.dat");
    }
}
