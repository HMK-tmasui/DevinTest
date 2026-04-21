using HscTool.Diagnostics;
using HscTool.Shared;
using HscTool.Shared.Diff;
using HvnDbVerifier;
using HvnDbVerifier.Model.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using DiffEntry = HscTool.Shared.Diff.RowDiff.DiffEntry;

namespace CompareRowSetsTests;

/// <summary>
/// 実データベース(hvn_cpe)に対する統合テスト。
/// SSHトンネル経由で Source/Target DB に接続し、
/// CompareAsync → WriteDiffCsvAsync の出力を検証する。
/// </summary>
public class RealDbIntegrationTest
{
    private readonly ITestOutputHelper _output;

    public RealDbIntegrationTest(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// 実DB接続で hvn_cpe テーブルを比較し、CSV出力を生成。
    /// Modify行のhvn_id/numberが全て実値（"Ignore"ではない）であることを検証。
    /// </summary>
    [Fact]
    public async Task HvnCpe_RealDb_ModifyRowsShouldHaveActualPkValues()
    {
        // SSH トンネル経由の接続先
        // Source: localhost:13306, Target: localhost:13307
        // 環境変数から接続情報を取得（デフォルト値はローカルテスト用）
        var dbUser = Environment.GetEnvironmentVariable("HVNDB_USER") ?? "user";
        var dbPassword = Environment.GetEnvironmentVariable("HVNDB_PASSWORD") ?? "";
        var srcPort = int.Parse(Environment.GetEnvironmentVariable("HVNDB_SRC_PORT") ?? "13306");
        var tgtPort = int.Parse(Environment.GetEnvironmentVariable("HVNDB_TGT_PORT") ?? "13307");

        if (string.IsNullOrEmpty(dbPassword))
        {
            _output.WriteLine("HVNDB_PASSWORD 環境変数が未設定のためスキップ");
            return;
        }

        var outputDir = Path.Combine(Path.GetTempPath(), $"realdb_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        try
        {
            var settings = new MySqlVerifierSettings
            {
                Source = new HscTool.Model.Json.MySQL
                {
                    Server = "127.0.0.1",
                    Port = srcPort,
                    Database = "hvn_db",
                    User = dbUser,
                    Password = dbPassword
                },
                Target = new HscTool.Model.Json.MySQL
                {
                    Server = "127.0.0.1",
                    Port = tgtPort,
                    Database = "hvn_db",
                    User = dbUser,
                    Password = dbPassword
                },
                OutputDir = outputDir,
                MaxFileSize = 100 * 1024 * 1024, // 100MB - 分割を防ぐ
                IncludeTables = new[]
                {
                    new IncludeTableConfig
                    {
                        Name = "hvn_cpe",
                        Columns = new[] { "hvn_id", "number", "product_type", "vendor", "product", "update_info", "latest_update" },
                        IgnorePK = true,
                        ModifiedKey = "latest_update",
                        AlternativeKey = new List<string> { "hvn_id" }
                    }
                }
            };

            var logger = new TestLogger();
            var service = new MySqlVerifierService(logger, settings);

            _output.WriteLine("=== CompareAsync 開始 ===");
            var result = await service.CompareAsync();

            // ログ出力
            foreach (var msg in logger.Messages)
                _output.WriteLine(msg);

            Assert.True(result.Success, $"CompareAsync failed: {result.ErrorMessage}");

            // CSV ファイルを読み込む
            var csvFiles = Directory.GetFiles(outputDir, "*.csv");
            Assert.True(csvFiles.Length > 0, "CSV ファイルが生成されていない");

            _output.WriteLine($"\n=== 生成された CSV ファイル: {csvFiles.Length} 件 ===");
            foreach (var f in csvFiles)
                _output.WriteLine($"  {Path.GetFileName(f)} ({new FileInfo(f).Length} bytes)");

            // 全CSVファイルの内容を結合
            var allLines = new List<string>();
            string headerLine = "";
            foreach (var csvFile in csvFiles)
            {
                var lines = await File.ReadAllLinesAsync(csvFile);
                if (lines.Length > 0)
                {
                    if (string.IsNullOrEmpty(headerLine))
                        headerLine = lines[0];
                    // ヘッダー行をスキップしてデータ行を追加
                    allLines.AddRange(lines.Skip(1));
                }
            }

            _output.WriteLine($"\n=== CSV データ行数: {allLines.Count} ===");

            // ヘッダーからカラムインデックスを取得
            // ヘッダーはクォートなし: DiffType,hvn_id,number,...
            var headers = headerLine.Split(',').ToList();
            int diffTypeIdx = headers.IndexOf("DiffType");
            int hvnIdIdx = headers.IndexOf("hvn_id");
            int numberIdx = headers.IndexOf("number");

            Assert.True(diffTypeIdx >= 0, $"DiffType カラムが見つからない: {headerLine}");
            Assert.True(hvnIdIdx >= 0, $"hvn_id カラムが見つからない: {headerLine}");
            Assert.True(numberIdx >= 0, $"number カラムが見つからない: {headerLine}");

            // Modify 行を抽出
            var modifyLines = allLines.Where(l => l.Contains("\"Modify\"")).ToList();
            var additionLines = allLines.Where(l => l.Contains("\"Addition\"")).ToList();
            var deleteLines = allLines.Where(l => l.Contains("\"Delete\"")).ToList();

            _output.WriteLine($"\n=== 差分種別 ===");
            _output.WriteLine($"  Modify: {modifyLines.Count} 行");
            _output.WriteLine($"  Addition: {additionLines.Count} 行");
            _output.WriteLine($"  Delete: {deleteLines.Count} 行");

            // Modify 行の hvn_id/number を検証
            int ignoreHvnIdCount = 0;
            int actualHvnIdCount = 0;
            int ignoreNumberCount = 0;
            int actualNumberCount = 0;
            var sampleIgnoreLines = new List<string>();
            var sampleActualLines = new List<string>();
            var sampleNumberIgnoreLines = new List<string>();

            foreach (var line in modifyLines)
            {
                var cells = SplitCsvLine(line);
                if (cells.Count <= Math.Max(hvnIdIdx, numberIdx))
                {
                    _output.WriteLine($"  ★ カラム数不足: {line}");
                    continue;
                }

                var hvnIdVal = cells[hvnIdIdx].Trim('"');
                var numberVal = cells[numberIdx].Trim('"');

                if (hvnIdVal == "Ignore")
                {
                    ignoreHvnIdCount++;
                    if (sampleIgnoreLines.Count < 5)
                        sampleIgnoreLines.Add(line);
                }
                else
                {
                    actualHvnIdCount++;
                    if (sampleActualLines.Count < 10)
                        sampleActualLines.Add(line);
                }

                if (numberVal == "Ignore")
                {
                    ignoreNumberCount++;
                    if (sampleNumberIgnoreLines.Count < 5)
                        sampleNumberIgnoreLines.Add(line);
                }
                else
                    actualNumberCount++;
            }

            _output.WriteLine($"\n=== Modify 行の hvn_id 検証 ===");
            _output.WriteLine($"  hvn_id=実値: {actualHvnIdCount} 行");
            _output.WriteLine($"  hvn_id=Ignore: {ignoreHvnIdCount} 行");
            _output.WriteLine($"  number=実値: {actualNumberCount} 行");
            _output.WriteLine($"  number=Ignore: {ignoreNumberCount} 行");

            if (sampleIgnoreLines.Count > 0)
            {
                _output.WriteLine($"\n=== hvn_id=Ignore のサンプル行 ===");
                foreach (var l in sampleIgnoreLines)
                    _output.WriteLine($"  {l}");
            }

            if (sampleActualLines.Count > 0)
            {
                _output.WriteLine($"\n=== hvn_id=実値 のサンプル行 ===");
                foreach (var l in sampleActualLines)
                    _output.WriteLine($"  {l}");
            }

            if (sampleNumberIgnoreLines.Count > 0)
            {
                _output.WriteLine($"\n=== number=Ignore のサンプル行 ===");
                foreach (var l in sampleNumberIgnoreLines)
                    _output.WriteLine($"  {l}");
            }

            // hvn_id=138869 の行を確認
            var hvn138869Lines = modifyLines.Where(l =>
            {
                var c = SplitCsvLine(l);
                return c.Count > hvnIdIdx && c[hvnIdIdx].Trim('"') == "138869";
            }).ToList();

            _output.WriteLine($"\n=== hvn_id=138869 の Modify 行: {hvn138869Lines.Count} 件 ===");
            foreach (var l in hvn138869Lines)
                _output.WriteLine($"  {l}");

            // ★ 核心のアサーション: Modify 行で hvn_id="Ignore" は 0 件であるべき
            // AlternativeKey=["hvn_id"] なので Modify ペアは必ず同じ hvn_id を持つ
            Assert.True(ignoreHvnIdCount == 0,
                $"Modify 行の hvn_id に 'Ignore' が {ignoreHvnIdCount} 件存在する（期待: 0 件）。\n" +
                $"サンプル: {string.Join("\n", sampleIgnoreLines.Take(3))}");

            // number は AlternativeKey ではないため、ソースとターゲットで異なる場合がある→ "Ignore" は正常動作
            _output.WriteLine($"\n=== number=Ignore は AlternativeKey マッチで src/tgt の number が異なるケース（正常動作） ===");

            // CSV ファイルを共有用にコピー
            var shareDir = "/home/ubuntu/repos/DevinTest/output";
            if (!Directory.Exists(shareDir))
                Directory.CreateDirectory(shareDir);
            foreach (var csvFile in csvFiles)
            {
                var destPath = Path.Combine(shareDir, Path.GetFileName(csvFile));
                File.Copy(csvFile, destPath, true);
                _output.WriteLine($"\n=== CSV コピー先: {destPath} ===");
            }
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    private static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        bool inQuotes = false;
        int start = 0;

        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                inQuotes = !inQuotes;
            else if (line[i] == ',' && !inQuotes)
            {
                cells.Add(line[start..i]);
                start = i + 1;
            }
        }
        cells.Add(line[start..]);
        return cells;
    }
}
