using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace DomainSuffixParser;

public class DomainSuffixService
{
    private static readonly DateTime CreatedBaseTime = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// public_suffix_list.dat の内容から有効な 2LD/TLD エントリを抽出する。
    /// jp prefecture type names / kr geographical names セクションのみスキップする。
    /// </summary>
    public List<DomainSuffix> CreateDomainSuffix(string suffixList)
    {
        var alphaNumRegex = new Regex("^[a-zA-Z0-9]+$", RegexOptions.Compiled);
        var lines = suffixList.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

        // 事前に各行のスキップフラグを作成
        var skipFlags = new bool[lines.Length];
        bool skipRegion = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("// jp prefecture type names") || line.StartsWith("// kr geographical names"))
            {
                skipRegion = true;
            }
            if (skipRegion && string.IsNullOrWhiteSpace(line))
            {
                skipRegion = false;
            }
            skipFlags[i] = skipRegion;
        }

        ConcurrentBag<DomainSuffix> resultBag = [];

        // 並列処理で有効な2LD/TLD行のみDomainSuffixとして抽出
        Parallel.For(0, lines.Length, i =>
        {
            if (skipFlags[i]) return;
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("//")) return;

            var parts = line.Split('.');
            if (parts.Length == 2 && parts[0] != "*" &&
                alphaNumRegex.IsMatch(parts[0]) && alphaNumRegex.IsMatch(parts[1]))
            {
                resultBag.Add(new DomainSuffix
                {
                    TLD = parts[1],
                    SecondLD = parts[0],
                    LatestUpdate = CreatedBaseTime
                });
            }
        });

        if (resultBag.Count == 0)
            throw new InvalidOperationException(
                "No valid domain suffix entries were created. Please check the input data and parsing logic.");

        return resultBag.ToList();
    }
}
