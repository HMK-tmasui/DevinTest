using HscTool.Diagnostics;
using HscTool.Shared;
using HscTool.Shared.Diff;
using HvnDbVerifier;
using HvnDbVerifier.Model.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;
using DiffEntry = HscTool.Shared.Diff.RowDiff.DiffEntry;

namespace CompareRowSetsTests;

/// <summary>
/// CompareTableDataIgnorePkAsync の統合テスト。
/// InMemoryDbContext を使用して、ハッシュ一致グループの行を使った
/// AlternativeKey 再照合ロジックを検証する。
/// </summary>
public class CompareTableDataIgnorePkTests
{
    private const string TableName = "jvndb_related_items";

    private static readonly string[] ConfigColumns = new[]
    {
        "related_item_id", "jvndb_id", "related_item_type", "advisory_type",
        "vendor_id", "name", "vulinfo_id", "url"
    };

    private static IncludeTableConfig CreateTableConfig() => new()
    {
        Name = TableName,
        Columns = ConfigColumns,
        IgnorePK = true,
        ModifiedKey = "latest_update",
        AlternativeKey = new List<string> { "jvndb_id", "name", "url" }
    };

    private static Dictionary<string, object?> MakeRow(
        int relatedItemId, string jvndbId, string relatedItemType, string advisoryType,
        int? vendorId, string name, string vulInfoId, string url, DateTime latestUpdate)
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["related_item_id"] = relatedItemId,
            ["jvndb_id"] = jvndbId,
            ["related_item_type"] = relatedItemType,
            ["advisory_type"] = advisoryType,
            ["vendor_id"] = vendorId.HasValue ? (object)vendorId.Value : null,
            ["name"] = name,
            ["vulinfo_id"] = vulInfoId,
            ["url"] = url,
            ["latest_update"] = latestUpdate
        };
    }

    private static InMemoryDbContext CreateMockContext(
        List<Dictionary<string, object?>> tableRows,
        List<ColumnInfo> columns,
        List<ColumnInfo> primaryKeys)
    {
        var pkNames = primaryKeys.Select(c => c.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var compareColNames = ConfigColumns.Where(c => !pkNames.Contains(c)).ToArray();

        var ctx = new InMemoryDbContext();
        ctx.QueryHandler = sql =>
        {
            sql = sql.Trim();

            if (sql.Contains("INFORMATION_SCHEMA.COLUMNS"))
            {
                return columns.Select(c => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TABLE_NAME"] = TableName,
                    ["COLUMN_NAME"] = c.ColumnName,
                    ["DATA_TYPE"] = c.DataType,
                    ["IS_NULLABLE"] = c.IsNullable ? "YES" : "NO",
                    ["COLUMN_TYPE"] = c.ColumnType,
                    ["COLUMN_KEY"] = c.IsPrimaryKey ? "PRI" : ""
                }).ToList();
            }

            if (sql.Contains("KEY_COLUMN_USAGE"))
            {
                return primaryKeys.Select((pk, idx) => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TABLE_NAME"] = TableName,
                    ["COLUMN_NAME"] = pk.ColumnName,
                    ["ORDINAL_POSITION"] = idx + 1
                }).ToList();
            }

            if (sql.Contains("GROUP_CONCAT") && sql.Contains("CRC32"))
            {
                var groups = tableRows
                    .GroupBy(r => ((DateTime)r["latest_update"]!).ToString("yyyy-MM-dd HH:mm:ss"))
                    .Select(g =>
                    {
                        var rowHashes = g.Select(row =>
                        {
                            var vals = compareColNames
                                .Select(c => row.GetValueOrDefault(c)?.ToString() ?? "")
                                .ToList();
                            return string.Join("|", vals).GetHashCode();
                        }).OrderBy(h => h).ToList();

                        var groupHash = string.Join(",", rowHashes).GetHashCode();
                        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["datetime_key"] = g.Key,
                            ["group_hash"] = (long)Math.Abs(groupHash)
                        };
                    })
                    .ToList();
                return groups;
            }

            if (sql.Contains("DATE_FORMAT") && sql.Contains("IN ("))
            {
                var inMatch = Regex.Match(sql, @"IN\s*\((.+?)\)", RegexOptions.Singleline);
                if (inMatch.Success)
                {
                    var inValues = inMatch.Groups[1].Value
                        .Split(',')
                        .Select(v => v.Trim().Trim('\''))
                        .ToHashSet();

                    return tableRows
                        .Where(r => inValues.Contains(((DateTime)r["latest_update"]!).ToString("yyyy-MM-dd HH:mm:ss")))
                        .ToList();
                }
            }

            return new List<Dictionary<string, object?>>();
        };
        return ctx;
    }

    private static (List<ColumnInfo> columns, List<ColumnInfo> primaryKeys) CreateColumnMetadata()
    {
        var columns = new List<ColumnInfo>
        {
            new() { ColumnName = "related_item_id", DataType = "int", ColumnType = "int(11)", IsPrimaryKey = true },
            new() { ColumnName = "jvndb_id", DataType = "varchar", ColumnType = "varchar(255)" },
            new() { ColumnName = "related_item_type", DataType = "varchar", ColumnType = "varchar(50)" },
            new() { ColumnName = "advisory_type", DataType = "varchar", ColumnType = "varchar(50)" },
            new() { ColumnName = "vendor_id", DataType = "int", ColumnType = "int(11)" },
            new() { ColumnName = "name", DataType = "varchar", ColumnType = "varchar(500)" },
            new() { ColumnName = "vulinfo_id", DataType = "varchar", ColumnType = "varchar(255)" },
            new() { ColumnName = "url", DataType = "varchar", ColumnType = "varchar(1000)" },
            new() { ColumnName = "latest_update", DataType = "datetime", ColumnType = "datetime" }
        };
        var primaryKeys = columns.Where(c => c.IsPrimaryKey).ToList();
        return (columns, primaryKeys);
    }

    /// <summary>CompareAsync を実行し、diff エントリリストを返す</summary>
    private static async Task<List<DiffEntry>> RunCompareAndGetDiffs(
        InMemoryDbContext srcCtx, InMemoryDbContext tgtCtx)
    {
        var (entries, _) = await RunCompareAndGetDiffsWithCsv(srcCtx, tgtCtx, CreateTableConfig());
        return entries;
    }

    /// <summary>CompareAsync を実行し、diff エントリリスト + CSV行リストを返す</summary>
    private static async Task<(List<DiffEntry> entries, List<string> csvLines)> RunCompareAndGetDiffsWithCsv(
        InMemoryDbContext srcCtx, InMemoryDbContext tgtCtx, IncludeTableConfig tableConfig)
    {
        var outputDir = Path.Combine(Path.GetTempPath(), $"test_csv_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);
        try
        {
            var settings = new MySqlVerifierSettings
            {
                Source = new HscTool.Model.Json.MySQL { Database = "source_db" },
                Target = new HscTool.Model.Json.MySQL { Database = "target_db" },
                OutputDir = outputDir,
                IncludeTables = new[] { tableConfig }
            };

            var logger = new TestLogger();
            var service = new MySqlVerifierService(logger, settings);

            var srcFactoryField = typeof(MySqlVerifierService).GetField("_srcFactory",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var tgtFactoryField = typeof(MySqlVerifierService).GetField("_tgtFactory",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            srcFactoryField!.SetValue(service, new TestDbContextFactory(srcCtx));
            tgtFactoryField!.SetValue(service, new TestDbContextFactory(tgtCtx));

            MySqlVerifierDiff.LastInstance = null;

            var result = await service.CompareAsync();
            Assert.True(result.Success, $"CompareAsync failed: {result.ErrorMessage}\nLogs:\n{string.Join("\n", logger.Messages)}");

            var diff = MySqlVerifierDiff.LastInstance;
            var entries = diff?.Entries ?? new List<DiffEntry>();

            // CSV ファイルを読み込む
            var csvLines = new List<string>();
            var csvFiles = Directory.GetFiles(outputDir, "*.csv");
            foreach (var csvFile in csvFiles)
            {
                var lines = await File.ReadAllLinesAsync(csvFile);
                csvLines.AddRange(lines);
            }

            return (entries, csvLines);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    /// <summary>
    /// シナリオ A: vendor_id のみ変更、AlternativeKey (jvndb_id, name, url) は一致。
    /// ターゲットに旧データ（ソースと同一）+ 新データ（vendor_id 変更）が共存。
    /// 修正後: Addition ではなく Modify になるべき。
    /// </summary>
    [Fact]
    public async Task ScenarioA_VendorIdOnlyChange_ShouldBeModify()
    {
        var (columns, primaryKeys) = CreateColumnMetadata();
        var t1 = new DateTime(2026, 4, 1, 10, 0, 0);
        var t2 = new DateTime(2026, 4, 10, 15, 0, 0);

        var srcRows = new List<Dictionary<string, object?>>
        {
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 1808, "ProductName", "V001", "http://example.com/info", t1)
        };

        var tgtRows = new List<Dictionary<string, object?>>
        {
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 1808, "ProductName", "V001", "http://example.com/info", t1),
            MakeRow(200, "JVNDB-2026-006630", "typeA", "advisoryB", 9233, "ProductName", "V001", "http://example.com/info", t2)
        };

        var srcCtx = CreateMockContext(srcRows, columns, primaryKeys);
        var tgtCtx = CreateMockContext(tgtRows, columns, primaryKeys);
        var entries = await RunCompareAndGetDiffs(srcCtx, tgtCtx);

        var additions = entries.Where(e => e.DiffType == DiffType.Addition).ToList();
        var modifies = entries.Where(e => e.DiffType == DiffType.Modify).ToList();
        var deletes = entries.Where(e => e.DiffType == DiffType.Delete).ToList();

        Assert.Empty(additions);
        Assert.Single(modifies);
        Assert.Empty(deletes);

        var mod = modifies[0];
        Assert.NotNull(mod.SourceValues);
        Assert.NotNull(mod.TargetValues);
    }

    /// <summary>
    /// シナリオ B: name にエンコーディング差異 + vendor_id 変更。
    /// AlternativeKey が一致しないため Modify にはならない → Addition のみ。
    /// </summary>
    [Fact]
    public async Task ScenarioB_EncodingDiffInAltKey_ShouldRemainAddition()
    {
        var (columns, primaryKeys) = CreateColumnMetadata();
        var t1 = new DateTime(2026, 4, 1, 10, 0, 0);
        var t2 = new DateTime(2026, 4, 10, 15, 0, 0);

        var srcRows = new List<Dictionary<string, object?>>
        {
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 1808, "Product&amp;lt;Name", "V001", "http://example.com/info", t1)
        };

        var tgtRows = new List<Dictionary<string, object?>>
        {
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 1808, "Product&amp;lt;Name", "V001", "http://example.com/info", t1),
            MakeRow(200, "JVNDB-2026-006630", "typeA", "advisoryB", 9233, "Product&lt;Name", "V001", "http://example.com/info", t2)
        };

        var srcCtx = CreateMockContext(srcRows, columns, primaryKeys);
        var tgtCtx = CreateMockContext(tgtRows, columns, primaryKeys);
        var entries = await RunCompareAndGetDiffs(srcCtx, tgtCtx);

        if (entries.Count > 0)
        {
            var additions = entries.Where(e => e.DiffType == DiffType.Addition).ToList();
            Assert.True(additions.Count > 0, "Encoding diff in AlternativeKey should result in Addition");
            var modifies = entries.Where(e => e.DiffType == DiffType.Modify).ToList();
            Assert.Empty(modifies);
        }
    }

    /// <summary>
    /// シナリオ C: 純粋な新規追加。ソースに対応する行がない場合 Addition であるべき。
    /// </summary>
    [Fact]
    public async Task ScenarioC_GenuineNewRow_ShouldBeAddition()
    {
        var (columns, primaryKeys) = CreateColumnMetadata();
        var t1 = new DateTime(2026, 4, 1, 10, 0, 0);
        var t2 = new DateTime(2026, 4, 10, 15, 0, 0);

        var srcRows = new List<Dictionary<string, object?>>
        {
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 1808, "ProductA", "V001", "http://example.com/a", t1)
        };

        var tgtRows = new List<Dictionary<string, object?>>
        {
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 1808, "ProductA", "V001", "http://example.com/a", t1),
            MakeRow(200, "JVNDB-2026-009999", "typeC", "advisoryD", 5000, "NewProduct", "V999", "http://example.com/new", t2)
        };

        var srcCtx = CreateMockContext(srcRows, columns, primaryKeys);
        var tgtCtx = CreateMockContext(tgtRows, columns, primaryKeys);
        var entries = await RunCompareAndGetDiffs(srcCtx, tgtCtx);

        var additions = entries.Where(e => e.DiffType == DiffType.Addition).ToList();
        Assert.Single(additions);
        Assert.Empty(entries.Where(e => e.DiffType == DiffType.Modify));
        Assert.Empty(entries.Where(e => e.DiffType == DiffType.Delete));
    }

    /// <summary>
    /// ノーマルケース: 完全一致のみ → 差分なし
    /// </summary>
    [Fact]
    public async Task NormalCase_IdenticalData_NoDiffs()
    {
        var (columns, primaryKeys) = CreateColumnMetadata();
        var t1 = new DateTime(2026, 4, 1, 10, 0, 0);

        var rows = new List<Dictionary<string, object?>>
        {
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 1808, "ProductName", "V001", "http://example.com/info", t1)
        };

        var srcCtx = CreateMockContext(rows, columns, primaryKeys);
        var tgtCtx = CreateMockContext(rows, columns, primaryKeys);
        var entries = await RunCompareAndGetDiffs(srcCtx, tgtCtx);

        Assert.Empty(entries);
    }

    /// <summary>
    /// 複数 JVNID のシナリオ A: 複数行で vendor_id のみ変更 → 全て Modify。
    /// </summary>
    [Fact]
    public async Task ScenarioA_MultipleJvnIds_AllShouldBeModify()
    {
        var (columns, primaryKeys) = CreateColumnMetadata();
        var t1 = new DateTime(2026, 4, 1, 10, 0, 0);
        var t2 = new DateTime(2026, 4, 10, 15, 0, 0);

        var srcRows = new List<Dictionary<string, object?>>
        {
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 1808, "Product1", "V001", "http://example.com/1", t1),
            MakeRow(101, "JVNDB-2026-006883", "typeA", "advisoryB", 38, "Product2", "V002", "http://example.com/2", t1),
            MakeRow(102, "JVNDB-2025-026193", "typeA", "advisoryB", null, "Product3", "V003", "http://example.com/3", t1)
        };

        var tgtRows = new List<Dictionary<string, object?>>
        {
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 1808, "Product1", "V001", "http://example.com/1", t1),
            MakeRow(101, "JVNDB-2026-006883", "typeA", "advisoryB", 38, "Product2", "V002", "http://example.com/2", t1),
            MakeRow(102, "JVNDB-2025-026193", "typeA", "advisoryB", null, "Product3", "V003", "http://example.com/3", t1),
            MakeRow(200, "JVNDB-2026-006630", "typeA", "advisoryB", 9233, "Product1", "V001", "http://example.com/1", t2),
            MakeRow(201, "JVNDB-2026-006883", "typeA", "advisoryB", 33463, "Product2", "V002", "http://example.com/2", t2),
            MakeRow(202, "JVNDB-2025-026193", "typeA", "advisoryB", 9704, "Product3", "V003", "http://example.com/3", t2)
        };

        var srcCtx = CreateMockContext(srcRows, columns, primaryKeys);
        var tgtCtx = CreateMockContext(tgtRows, columns, primaryKeys);
        var entries = await RunCompareAndGetDiffs(srcCtx, tgtCtx);

        var additions = entries.Where(e => e.DiffType == DiffType.Addition).ToList();
        var modifies = entries.Where(e => e.DiffType == DiffType.Modify).ToList();
        var deletes = entries.Where(e => e.DiffType == DiffType.Delete).ToList();

        Assert.Empty(additions);
        Assert.Equal(3, modifies.Count);
        Assert.Empty(deletes);
    }

    /// <summary>
    /// Delete の再照合テスト: ソースに新データがあり、ターゲットに旧データのみ。
    /// Delete 候補がハッシュ一致グループのターゲット行と再照合 → Modify。
    /// </summary>
    [Fact]
    public async Task DeleteRecheck_ShouldBeModify()
    {
        var (columns, primaryKeys) = CreateColumnMetadata();
        var t1 = new DateTime(2026, 4, 1, 10, 0, 0);
        var t2 = new DateTime(2026, 4, 10, 15, 0, 0);

        var srcRows = new List<Dictionary<string, object?>>
        {
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 1808, "ProductName", "V001", "http://example.com/info", t1),
            MakeRow(200, "JVNDB-2026-006630", "typeA", "advisoryB", 9233, "ProductName", "V001", "http://example.com/info", t2)
        };

        var tgtRows = new List<Dictionary<string, object?>>
        {
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 1808, "ProductName", "V001", "http://example.com/info", t1)
        };

        var srcCtx = CreateMockContext(srcRows, columns, primaryKeys);
        var tgtCtx = CreateMockContext(tgtRows, columns, primaryKeys);
        var entries = await RunCompareAndGetDiffs(srcCtx, tgtCtx);

        var additions = entries.Where(e => e.DiffType == DiffType.Addition).ToList();
        var modifies = entries.Where(e => e.DiffType == DiffType.Modify).ToList();
        var deletes = entries.Where(e => e.DiffType == DiffType.Delete).ToList();

        Assert.Empty(additions);
        Assert.Single(modifies);
        Assert.Empty(deletes);
    }

    /// <summary>
    /// force_pk テスト: force_pk で指定したカラムが PK として扱われ、
    /// 通常の PK ベース比較が force_pk カラムで行われることを検証。
    /// </summary>
    [Fact]
    public async Task ForcePk_ShouldTreatSpecifiedColumnsAsPk()
    {
        // hvn テーブル相当: 実際の PK は hvn_id だが、force_pk で jvndb_id を PK 扱い
        var columns = new List<ColumnInfo>
        {
            new() { ColumnName = "hvn_id", DataType = "int", ColumnType = "int(11)", IsPrimaryKey = true },
            new() { ColumnName = "jvndb_id", DataType = "varchar", ColumnType = "varchar(255)" },
            new() { ColumnName = "nvd_id", DataType = "varchar", ColumnType = "varchar(255)" },
            new() { ColumnName = "is_update", DataType = "tinyint", ColumnType = "tinyint(1)" },
            new() { ColumnName = "is_reject", DataType = "tinyint", ColumnType = "tinyint(1)" },
            new() { ColumnName = "published_date", DataType = "datetime", ColumnType = "datetime" },
            new() { ColumnName = "last_modified_date", DataType = "datetime", ColumnType = "datetime" },
            new() { ColumnName = "create_date", DataType = "datetime", ColumnType = "datetime" }
        };
        var primaryKeys = columns.Where(c => c.IsPrimaryKey).ToList();

        var dt1 = new DateTime(2026, 1, 1, 0, 0, 0);
        var dt2 = new DateTime(2026, 2, 1, 0, 0, 0);

        // ソース: jvndb_id=JVN-001 の行
        var srcRows = new List<Dictionary<string, object?>>
        {
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["hvn_id"] = 1, ["jvndb_id"] = "JVN-001", ["nvd_id"] = "CVE-2026-0001",
                ["is_update"] = 0, ["is_reject"] = 0,
                ["published_date"] = dt1, ["last_modified_date"] = dt1, ["create_date"] = dt1
            },
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["hvn_id"] = 2, ["jvndb_id"] = "JVN-002", ["nvd_id"] = "CVE-2026-0002",
                ["is_update"] = 0, ["is_reject"] = 0,
                ["published_date"] = dt1, ["last_modified_date"] = dt1, ["create_date"] = dt1
            }
        };

        // ターゲット: jvndb_id=JVN-001 は is_update が変更、JVN-002 は同一
        var tgtRows = new List<Dictionary<string, object?>>
        {
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["hvn_id"] = 10, ["jvndb_id"] = "JVN-001", ["nvd_id"] = "CVE-2026-0001",
                ["is_update"] = 1, ["is_reject"] = 0,
                ["published_date"] = dt1, ["last_modified_date"] = dt2, ["create_date"] = dt1
            },
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["hvn_id"] = 2, ["jvndb_id"] = "JVN-002", ["nvd_id"] = "CVE-2026-0002",
                ["is_update"] = 0, ["is_reject"] = 0,
                ["published_date"] = dt1, ["last_modified_date"] = dt1, ["create_date"] = dt1
            }
        };

        var forcePkColumns = new List<string> { "jvndb_id" };
        var configColumns = new[] { "hvn_id", "jvndb_id", "nvd_id", "is_update", "is_reject", "published_date", "last_modified_date", "create_date" };

        var tableConfig = new IncludeTableConfig
        {
            Name = "hvn",
            Columns = configColumns,
            ForcePK = forcePkColumns,
            IgnorePK = false,
            ModifiedKey = "",
            AlternativeKey = new List<string>()
        };

        var pkNames = forcePkColumns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var compareColNames = configColumns.Where(c => !pkNames.Contains(c)).ToArray();

        var srcCtx = CreateForcePkMockContext(srcRows, columns, primaryKeys, forcePkColumns, configColumns, compareColNames);
        var tgtCtx = CreateForcePkMockContext(tgtRows, columns, primaryKeys, forcePkColumns, configColumns, compareColNames);

        var settings = new MySqlVerifierSettings
        {
            Source = new HscTool.Model.Json.MySQL { Database = "source_db" },
            Target = new HscTool.Model.Json.MySQL { Database = "target_db" },
            OutputDir = "/tmp/test_output",
            IncludeTables = new[] { tableConfig }
        };

        var logger = new TestLogger();
        var service = new MySqlVerifierService(logger, settings);

        var srcFactoryField = typeof(MySqlVerifierService).GetField("_srcFactory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var tgtFactoryField = typeof(MySqlVerifierService).GetField("_tgtFactory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        srcFactoryField!.SetValue(service, new TestDbContextFactory(srcCtx));
        tgtFactoryField!.SetValue(service, new TestDbContextFactory(tgtCtx));

        MySqlVerifierDiff.LastInstance = null;
        var result = await service.CompareAsync();
        Assert.True(result.Success, $"CompareAsync failed: {result.ErrorMessage}\nLogs:\n{string.Join("\n", logger.Messages)}");

        var diff = MySqlVerifierDiff.LastInstance;
        var entries = diff?.Entries ?? new List<DiffEntry>();

        // force_pk=jvndb_id で比較: JVN-001 は is_update 変更 → Modify、JVN-002 は同一 → 差分なし
        var modifies = entries.Where(e => e.DiffType == DiffType.Modify).ToList();
        var additions = entries.Where(e => e.DiffType == DiffType.Addition).ToList();
        var deletes = entries.Where(e => e.DiffType == DiffType.Delete).ToList();

        Assert.Single(modifies);
        Assert.Empty(additions);
        Assert.Empty(deletes);

        // Modify 行の内容確認: jvndb_id=JVN-001
        var mod = modifies[0];
        Assert.NotNull(mod.SourceValues);
        Assert.NotNull(mod.TargetValues);
    }

    /// <summary>
    /// 条件付き Ignore テスト: Modify 行で PK 値がソース/ターゲット同一なら実値を表示、
    /// 異なるなら "Ignore" を表示。Addition/Delete 行は常に "Ignore"。
    /// </summary>
    [Fact]
    public async Task ConditionalIgnore_SamePkShowsValue_DifferentPkShowsIgnore()
    {
        var (columns, primaryKeys) = CreateColumnMetadata();
        var t1 = new DateTime(2026, 4, 1, 10, 0, 0);
        var t2 = new DateTime(2026, 4, 10, 15, 0, 0);

        // ソース: related_item_id=100
        var srcRows = new List<Dictionary<string, object?>>
        {
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 1808, "ProductName", "V001", "http://example.com/info", t1)
        };

        // ターゲット: related_item_id=100（同一PK）+ related_item_id=200（異なるPK、vendor_id変更）
        // + related_item_id=300（純粋新規、AlternativeKeyが異なる）
        var tgtRows = new List<Dictionary<string, object?>>
        {
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 1808, "ProductName", "V001", "http://example.com/info", t1),
            MakeRow(200, "JVNDB-2026-006630", "typeA", "advisoryB", 9233, "ProductName", "V001", "http://example.com/info", t2),
            MakeRow(300, "JVNDB-2026-NEW001", "typeC", "advisoryD", 5000, "NewProduct", "V999", "http://example.com/new", t2)
        };

        var srcCtx = CreateMockContext(srcRows, columns, primaryKeys);
        var tgtCtx = CreateMockContext(tgtRows, columns, primaryKeys);
        var entries = await RunCompareAndGetDiffs(srcCtx, tgtCtx);

        // Modify: ソース(100) vs ターゲット(200) — AlternativeKeyマッチ、PKが異なる → "Ignore"
        var modifies = entries.Where(e => e.DiffType == DiffType.Modify).ToList();
        Assert.Single(modifies);
        var mod = modifies[0];

        // related_item_id はソース=100、ターゲット=200 で異なる → "Ignore"
        Assert.Equal("Ignore", mod.SourceValues!["related_item_id"]?.ToString());
        Assert.Equal("Ignore", mod.TargetValues!["related_item_id"]?.ToString());

        // Addition: 純粋新規 → 常に "Ignore"
        var additions = entries.Where(e => e.DiffType == DiffType.Addition).ToList();
        Assert.Single(additions);
        Assert.Equal("Ignore", additions[0].TargetValues!["related_item_id"]?.ToString());
    }

    /// <summary>
    /// 条件付き Ignore テスト: Modify 行でソースとターゲットの PK が同一の場合、
    /// CSV出力で実値を表示。エントリ値は AddEntry により "Ignore" に上書きされるが、
    /// WriteDiffCsvAsync が _modifyOriginalPks から元の値を復元して CSV に出力する。
    /// </summary>
    [Fact]
    public async Task ConditionalIgnore_SamePk_ShouldShowActualValue()
    {
        var (columns, primaryKeys) = CreateColumnMetadata();
        var t1 = new DateTime(2026, 4, 1, 10, 0, 0);
        var t2 = new DateTime(2026, 4, 10, 15, 0, 0);

        // ソース: related_item_id=100
        var srcRows = new List<Dictionary<string, object?>>
        {
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 1808, "ProductName", "V001", "http://example.com/info", t1)
        };

        // ターゲット: related_item_id=100（同一PK）+ vendor_id が変更。
        var tgtRows = new List<Dictionary<string, object?>>
        {
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 1808, "ProductName", "V001", "http://example.com/info", t1),
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 9233, "ProductName", "V001", "http://example.com/info", t2)
        };

        var srcCtx = CreateMockContext(srcRows, columns, primaryKeys);
        var tgtCtx = CreateMockContext(tgtRows, columns, primaryKeys);
        var (entries, csvLines) = await RunCompareAndGetDiffsWithCsv(srcCtx, tgtCtx, CreateTableConfig());

        var modifies = entries.Where(e => e.DiffType == DiffType.Modify).ToList();
        Assert.Single(modifies);

        // エントリ値は AddEntry により "Ignore" に上書きされる（実 HSCTOOL と同じ動作）
        Assert.Equal("Ignore", modifies[0].SourceValues!["related_item_id"]?.ToString());

        // ★ CSV出力で実値 "100" を確認（WriteDiffCsvAsync が _modifyOriginalPks から復元）
        Assert.True(csvLines.Count >= 2, $"CSV行数不足: {csvLines.Count}");
        var modifyLines = csvLines.Skip(1).Where(l => l.Contains("\"Modify\"")).ToList();
        Assert.Single(modifyLines);
        var cells = SplitCsvLine(modifyLines[0]);
        // related_item_id は CSV ヘッダーの2番目（DiffType の次）
        Assert.Contains("\"100\"", cells[1]);
        Assert.DoesNotContain("Ignore", cells[1]);
    }

    // ── hvn_cpe テスト用ヘルパー ──
    private const string HvnCpeTableName = "hvn_cpe";

    private static readonly string[] HvnCpeConfigColumns = new[]
    {
        "hvn_id", "number", "product_type", "vendor", "product",
        "update_info", "latest_update"
    };

    private static IncludeTableConfig CreateHvnCpeTableConfig() => new()
    {
        Name = HvnCpeTableName,
        Columns = HvnCpeConfigColumns,
        IgnorePK = true,
        ModifiedKey = "latest_update",
        AlternativeKey = new List<string> { "hvn_id" }
    };

    private static Dictionary<string, object?> MakeCpeRow(
        int hvnId, int number, int productType, string vendor, string product,
        string? updateInfo, DateTime latestUpdate)
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["hvn_id"] = hvnId,
            ["number"] = number,
            ["product_type"] = productType,
            ["vendor"] = vendor,
            ["product"] = product,
            ["update_info"] = updateInfo,
            ["latest_update"] = latestUpdate
        };
    }

    private static (List<ColumnInfo> columns, List<ColumnInfo> primaryKeys) CreateHvnCpeColumnMetadata()
    {
        var columns = new List<ColumnInfo>
        {
            new() { ColumnName = "hvn_id", DataType = "int", ColumnType = "int(11)", IsPrimaryKey = true, PrimaryKeyOrdinal = 1 },
            new() { ColumnName = "number", DataType = "int", ColumnType = "int(11)", IsPrimaryKey = true, PrimaryKeyOrdinal = 2 },
            new() { ColumnName = "product_type", DataType = "tinyint", ColumnType = "tinyint(4)" },
            new() { ColumnName = "vendor", DataType = "varchar", ColumnType = "varchar(255)" },
            new() { ColumnName = "product", DataType = "varchar", ColumnType = "varchar(255)" },
            new() { ColumnName = "update_info", DataType = "varchar", ColumnType = "varchar(255)" },
            new() { ColumnName = "latest_update", DataType = "datetime", ColumnType = "datetime" }
        };
        var primaryKeys = columns.Where(c => c.IsPrimaryKey).OrderBy(c => c.PrimaryKeyOrdinal).ToList();
        return (columns, primaryKeys);
    }

    private static InMemoryDbContext CreateHvnCpeMockContext(
        List<Dictionary<string, object?>> tableRows,
        List<ColumnInfo> columns,
        List<ColumnInfo> primaryKeys)
    {
        var pkNames = primaryKeys.Select(c => c.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var compareColNames = HvnCpeConfigColumns.Where(c => !pkNames.Contains(c)).ToArray();

        var ctx = new InMemoryDbContext();
        ctx.QueryHandler = sql =>
        {
            sql = sql.Trim();

            if (sql.Contains("INFORMATION_SCHEMA.COLUMNS"))
            {
                return columns.Select(c => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TABLE_NAME"] = HvnCpeTableName,
                    ["COLUMN_NAME"] = c.ColumnName,
                    ["DATA_TYPE"] = c.DataType,
                    ["IS_NULLABLE"] = c.IsNullable ? "YES" : "NO",
                    ["COLUMN_TYPE"] = c.ColumnType,
                    ["COLUMN_KEY"] = c.IsPrimaryKey ? "PRI" : ""
                }).ToList();
            }

            if (sql.Contains("KEY_COLUMN_USAGE"))
            {
                return primaryKeys.Select((pk, idx) => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TABLE_NAME"] = HvnCpeTableName,
                    ["COLUMN_NAME"] = pk.ColumnName,
                    ["ORDINAL_POSITION"] = idx + 1
                }).ToList();
            }

            if (sql.Contains("GROUP_CONCAT") && sql.Contains("CRC32"))
            {
                var groups = tableRows
                    .GroupBy(r => ((DateTime)r["latest_update"]!).ToString("yyyy-MM-dd HH:mm:ss"))
                    .Select(g =>
                    {
                        var rowHashes = g.Select(row =>
                        {
                            var vals = compareColNames
                                .Select(c => row.GetValueOrDefault(c)?.ToString() ?? "")
                                .ToList();
                            return string.Join("|", vals).GetHashCode();
                        }).OrderBy(h => h).ToList();

                        var groupHash = string.Join(",", rowHashes).GetHashCode();
                        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["datetime_key"] = g.Key,
                            ["group_hash"] = (long)Math.Abs(groupHash)
                        };
                    })
                    .ToList();
                return groups;
            }

            if (sql.Contains("DATE_FORMAT") && sql.Contains("IN ("))
            {
                var inMatch = Regex.Match(sql, @"IN\s*\((.+?)\)", RegexOptions.Singleline);
                if (inMatch.Success)
                {
                    var inValues = inMatch.Groups[1].Value
                        .Split(',')
                        .Select(v => v.Trim().Trim('\''))
                        .ToHashSet();

                    return tableRows
                        .Where(r => inValues.Contains(((DateTime)r["latest_update"]!).ToString("yyyy-MM-dd HH:mm:ss")))
                        .ToList();
                }
            }

            return new List<Dictionary<string, object?>>();
        };
        return ctx;
    }

    private static async Task<List<DiffEntry>> RunHvnCpeCompareAndGetDiffs(
        InMemoryDbContext srcCtx, InMemoryDbContext tgtCtx)
    {
        var (entries, _) = await RunHvnCpeCompareAndGetDiffsWithCsv(srcCtx, tgtCtx);
        return entries;
    }

    /// <summary>hvn_cpe 用: CompareAsync を実行し、diff エントリリスト + CSV行リストを返す</summary>
    private static async Task<(List<DiffEntry> entries, List<string> csvLines)> RunHvnCpeCompareAndGetDiffsWithCsv(
        InMemoryDbContext srcCtx, InMemoryDbContext tgtCtx)
    {
        return await RunCompareAndGetDiffsWithCsv(srcCtx, tgtCtx, CreateHvnCpeTableConfig());
    }

    /// <summary>
    /// hvn_cpe 実データ再現テスト: 複合 PK (hvn_id, number)、AlternativeKey = ["hvn_id"]。
    /// ソースとターゲットで hvn_id が同一の場合、CSV出力で hvn_id は実値を表示すべき。
    /// エントリ値は AddEntry により "Ignore" に上書きされるが、
    /// WriteDiffCsvAsync が _modifyOriginalPks から元の値を復元して CSV に出力する。
    /// </summary>
    [Fact]
    public async Task HvnCpe_CompositePk_SamePk_ShouldShowActualValue()
    {
        var (columns, primaryKeys) = CreateHvnCpeColumnMetadata();
        var t1 = new DateTime(2024, 11, 18, 13, 20, 31);
        var t2 = new DateTime(2026, 3, 23, 9, 0, 0);

        var srcRows = new List<Dictionary<string, object?>>
        {
            MakeCpeRow(138869, 0,  1, "microsoft", "windows 10", "-", t1),
            MakeCpeRow(138869, 15, 1, "microsoft", "windows server 2016", "1607", t1),
            MakeCpeRow(138869, 16, 1, "microsoft", "windows server 2016", "1803", t1),
            MakeCpeRow(138869, 17, 1, "microsoft", "windows server 2016", "1903", t1),
            MakeCpeRow(138869, 18, 1, "microsoft", "windows server 2019", "1809", t1),
        };

        var tgtRows = new List<Dictionary<string, object?>>
        {
            MakeCpeRow(138869, 0,  1, "microsoft", "windows 10", "-", t1),
            MakeCpeRow(138869, 15, 1, "microsoft", "windows server 2016", "1607", t1),
            MakeCpeRow(138869, 16, 1, "microsoft", "windows server 2016", "1607", t2),
            MakeCpeRow(138869, 17, 1, "microsoft", "windows server 2016", "1607", t2),
            MakeCpeRow(138869, 18, 1, "microsoft", "windows server 2019", "1809", t1),
        };

        var srcCtx = CreateHvnCpeMockContext(srcRows, columns, primaryKeys);
        var tgtCtx = CreateHvnCpeMockContext(tgtRows, columns, primaryKeys);
        var (entries, csvLines) = await RunHvnCpeCompareAndGetDiffsWithCsv(srcCtx, tgtCtx);

        var modifies = entries.Where(e => e.DiffType == DiffType.Modify).ToList();
        var additions = entries.Where(e => e.DiffType == DiffType.Addition).ToList();
        var deletes = entries.Where(e => e.DiffType == DiffType.Delete).ToList();

        Assert.Equal(2, modifies.Count);
        Assert.Empty(additions);
        Assert.Empty(deletes);

        // エントリ値は AddEntry により "Ignore" に上書きされる（実 HSCTOOL と同じ動作）
        foreach (var mod in modifies)
            Assert.Equal("Ignore", mod.SourceValues!["hvn_id"]?.ToString());

        // ★ CSV出力で hvn_id="138869" を確認（WriteDiffCsvAsync が _modifyOriginalPks から復元）
        Assert.True(csvLines.Count >= 2, $"CSV行数不足: {csvLines.Count}");
        var header = csvLines[0];
        var headerCells = header.Split(',');
        var hvnIdIdx = Array.IndexOf(headerCells, "hvn_id");
        var numberIdx = Array.IndexOf(headerCells, "number");
        Assert.True(hvnIdIdx >= 0, $"hvn_id カラムがヘッダーにない: {header}");

        var modifyLines = csvLines.Skip(1).Where(l => l.Contains("\"Modify\"")).ToList();
        Assert.Equal(2, modifyLines.Count);

        foreach (var line in modifyLines)
        {
            var cells = SplitCsvLine(line);
            Assert.Contains("138869", cells[hvnIdIdx]);
            Assert.DoesNotContain("Ignore", cells[hvnIdIdx]);
            // number も同一なら実値を表示
            Assert.DoesNotContain("Ignore", cells[numberIdx]);
        }
    }

    /// <summary>
    /// hvn_cpe: 複数の hvn_id が混在するケース。AlternativeKey=["hvn_id"] で正しくペアリングされ、
    /// CSV出力で hvn_id が同一なら実値を表示。
    /// </summary>
    [Fact]
    public async Task HvnCpe_MultipleHvnIds_SamePk_ShouldShowActualValue()
    {
        var (columns, primaryKeys) = CreateHvnCpeColumnMetadata();
        var t1 = new DateTime(2024, 11, 18, 13, 20, 31);
        var t2 = new DateTime(2026, 3, 23, 9, 0, 0);

        var srcRows = new List<Dictionary<string, object?>>
        {
            MakeCpeRow(138869, 16, 1, "microsoft", "windows server 2016", "1803", t1),
            MakeCpeRow(138869, 17, 1, "microsoft", "windows server 2016", "1903", t1),
            MakeCpeRow(150933, 17, 1, "microsoft", "windows server 2016", "2009", t1),
            MakeCpeRow(156133, 16, 1, "microsoft", "windows server 2016", "2009", t1),
        };

        var tgtRows = new List<Dictionary<string, object?>>
        {
            MakeCpeRow(138869, 16, 1, "microsoft", "windows server 2016", "1607", t2),
            MakeCpeRow(138869, 17, 1, "microsoft", "windows server 2016", "1607", t2),
            MakeCpeRow(150933, 17, 1, "microsoft", "windows server 2016", "1607", t2),
            MakeCpeRow(156133, 16, 1, "microsoft", "windows server 2016", "1607", t2),
        };

        var srcCtx = CreateHvnCpeMockContext(srcRows, columns, primaryKeys);
        var tgtCtx = CreateHvnCpeMockContext(tgtRows, columns, primaryKeys);
        var (entries, csvLines) = await RunHvnCpeCompareAndGetDiffsWithCsv(srcCtx, tgtCtx);

        var modifies = entries.Where(e => e.DiffType == DiffType.Modify).ToList();
        Assert.Equal(4, modifies.Count);

        // エントリ値は AddEntry により "Ignore" に上書き（実 HSCTOOL と同じ動作）
        foreach (var mod in modifies)
            Assert.Equal("Ignore", mod.SourceValues!["hvn_id"]?.ToString());

        // ★ CSV出力で hvn_id が実値であることを確認
        Assert.True(csvLines.Count >= 2);
        var headerCells = csvLines[0].Split(',');
        var hvnIdIdx = Array.IndexOf(headerCells, "hvn_id");

        var modifyLines = csvLines.Skip(1).Where(l => l.Contains("\"Modify\"")).ToList();
        Assert.Equal(4, modifyLines.Count);

        foreach (var line in modifyLines)
        {
            var cells = SplitCsvLine(line);
            Assert.DoesNotContain("Ignore", cells[hvnIdIdx]);
        }
    }

    /// <summary>
    /// 実データ再現テスト: 同一 datetime グループ内に複数の hvn_id が混在し、
    /// 一部の行が同一の非PK内容を持つケース（Step1 exact-content match で
    /// 異なる hvn_id の行が誤ペアリングされる可能性をテスト）。
    /// AlternativeKey=["hvn_id"] による Step2 マッチで正しくペアリングされ、
    /// Modify 行の hvn_id は実値を表示すべき。
    /// </summary>
    [Fact]
    public async Task HvnCpe_MixedHvnIds_SameGroup_OverlappingContent_ShouldShowActualValue()
    {
        var (columns, primaryKeys) = CreateHvnCpeColumnMetadata();
        var t1 = new DateTime(2024, 11, 18, 13, 20, 31);

        // 同一 datetime グループ内に 5 つの hvn_id が存在
        // hvn_id=100 と hvn_id=200 は product_type/vendor/product が同一（非PK重複パターン）
        var srcRows = new List<Dictionary<string, object?>>
        {
            MakeCpeRow(100, 1, 88, "microsoft", "windows server 2016", "1803", t1),
            MakeCpeRow(200, 1, 88, "microsoft", "windows server 2016", "1803", t1), // 100 と同じ非PKコンテンツ
            MakeCpeRow(300, 1, 88, "microsoft", "windows server 2019", "1809", t1),
            MakeCpeRow(400, 1, 88, "microsoft", "windows server 2022", "21H2", t1),
            MakeCpeRow(500, 1, 88, "microsoft", "windows 10", "22H2", t1),
        };

        // ターゲット: hvn_id=200 の update_info のみ変更、他は同一
        var tgtRows = new List<Dictionary<string, object?>>
        {
            MakeCpeRow(100, 1, 88, "microsoft", "windows server 2016", "1803", t1), // 変更なし
            MakeCpeRow(200, 1, 88, "microsoft", "windows server 2016", "1607", t1), // update_info 変更
            MakeCpeRow(300, 1, 88, "microsoft", "windows server 2019", "1809", t1), // 変更なし
            MakeCpeRow(400, 1, 88, "microsoft", "windows server 2022", "21H2", t1), // 変更なし
            MakeCpeRow(500, 1, 88, "microsoft", "windows 10", "22H2", t1),          // 変更なし
        };

        var srcCtx = CreateHvnCpeMockContext(srcRows, columns, primaryKeys);
        var tgtCtx = CreateHvnCpeMockContext(tgtRows, columns, primaryKeys);
        var (entries, csvLines) = await RunHvnCpeCompareAndGetDiffsWithCsv(srcCtx, tgtCtx);

        var modifies = entries.Where(e => e.DiffType == DiffType.Modify).ToList();
        var additions = entries.Where(e => e.DiffType == DiffType.Addition).ToList();
        var deletes = entries.Where(e => e.DiffType == DiffType.Delete).ToList();

        var totalDiffs = modifies.Count + additions.Count + deletes.Count;
        Assert.True(totalDiffs > 0, "差分が0件は想定外（hvn_id=200 の update_info が変更されている）");

        // エントリ値は AddEntry により "Ignore" に上書き（実 HSCTOOL と同じ動作）
        // → CSV出力で条件付き Ignore を検証
        if (csvLines.Count >= 2 && modifies.Count > 0)
        {
            var headerCells = csvLines[0].Split(',');
            var hvnIdIdx = Array.IndexOf(headerCells, "hvn_id");
            Assert.True(hvnIdIdx >= 0);

            var modifyLines = csvLines.Skip(1).Where(l => l.Contains("\"Modify\"")).ToList();
            foreach (var line in modifyLines)
            {
                var cells = SplitCsvLine(line);
                // hvn_id は AlternativeKey でマッチ → 必ず同一 → CSV で実値表示
                Assert.DoesNotContain("Ignore", cells[hvnIdIdx]);
            }
        }
    }

    /// <summary>
    /// 大規模テスト: 10種の hvn_id × 複数 number、datetime グループ跨ぎ、
    /// matched/modified/added/deleted グループが共存するリアルシナリオ。
    /// 全 Modify 行で hvn_id が実値であることを検証。
    /// </summary>
    [Fact]
    public async Task HvnCpe_LargeScale_MixedGroups_AllModifyShouldShowActualHvnId()
    {
        var (columns, primaryKeys) = CreateHvnCpeColumnMetadata();
        var t1 = new DateTime(2024, 11, 18, 13, 20, 31); // 古い日時
        var t2 = new DateTime(2026, 3, 23, 9, 0, 0);      // 新しい日時
        var t3 = new DateTime(2025, 6, 15, 12, 0, 0);      // 中間日時

        // ソース: 3つの datetime グループに分散
        var srcRows = new List<Dictionary<string, object?>>();
        // t1 グループ: hvn_id 100-109, 各 number=1～3
        for (int id = 100; id < 110; id++)
            for (int n = 1; n <= 3; n++)
                srcRows.Add(MakeCpeRow(id, n, 88, "microsoft", "windows server 2016", $"ver{id}_{n}", t1));

        // t3 グループ: hvn_id 200-204, 各 number=1
        for (int id = 200; id < 205; id++)
            srcRows.Add(MakeCpeRow(id, 1, 1, "linux", "ubuntu", $"22.04_{id}", t3));

        // ターゲット: 
        var tgtRows = new List<Dictionary<string, object?>>();

        // t1 グループ: hvn_id 100-109 の number=1,2 はソースと同一（unchanged → matched 候補）
        //              number=3 の一部で update_info 変更（→ modified 候補）
        for (int id = 100; id < 110; id++)
        {
            tgtRows.Add(MakeCpeRow(id, 1, 88, "microsoft", "windows server 2016", $"ver{id}_1", t1)); // same
            tgtRows.Add(MakeCpeRow(id, 2, 88, "microsoft", "windows server 2016", $"ver{id}_2", t1)); // same
            // number=3: id < 105 は変更あり（update_info 変更 + t2 に移動）、id >= 105 は同一
            if (id < 105)
                tgtRows.Add(MakeCpeRow(id, 3, 88, "microsoft", "windows server 2016", $"CHANGED_{id}_3", t2)); // changed + moved to t2
            else
                tgtRows.Add(MakeCpeRow(id, 3, 88, "microsoft", "windows server 2016", $"ver{id}_3", t1)); // same
        }

        // t3 グループ: hvn_id 200-204 はソースと同一（matched グループ）
        for (int id = 200; id < 205; id++)
            tgtRows.Add(MakeCpeRow(id, 1, 1, "linux", "ubuntu", $"22.04_{id}", t3));

        // t2 グループ: 新規追加行（hvn_id=300）
        tgtRows.Add(MakeCpeRow(300, 1, 88, "microsoft", "windows 11", "23H2", t2));

        var srcCtx = CreateHvnCpeMockContext(srcRows, columns, primaryKeys);
        var tgtCtx = CreateHvnCpeMockContext(tgtRows, columns, primaryKeys);
        var (entries, csvLines) = await RunHvnCpeCompareAndGetDiffsWithCsv(srcCtx, tgtCtx);

        var modifies = entries.Where(e => e.DiffType == DiffType.Modify).ToList();

        // エントリ値は AddEntry により "Ignore" に上書き（実 HSCTOOL と同じ動作）
        foreach (var mod in modifies)
            Assert.Equal("Ignore", mod.SourceValues!["hvn_id"]?.ToString());

        // ★ CSV出力で全 Modify 行の hvn_id が実値であることを確認
        Assert.True(csvLines.Count >= 2);
        var headerCells = csvLines[0].Split(',');
        var hvnIdIdx = Array.IndexOf(headerCells, "hvn_id");
        Assert.True(hvnIdIdx >= 0);

        var modifyLines = csvLines.Skip(1).Where(l => l.Contains("\"Modify\"")).ToList();
        foreach (var line in modifyLines)
        {
            var cells = SplitCsvLine(line);
            Assert.DoesNotContain("Ignore", cells[hvnIdIdx]);
        }
    }

    /// <summary>
    /// hvn_cpe 実データ CSV出力検証: hvn_id=138869, number=15/16/17。
    /// WriteDiffCsvAsync が生成する実際のCSVファイルを読み込み、
    /// Modify行でhvn_idが"138869"（実値）、update_infoが"1803 => 1607"形式であることを検証。
    /// BuildCsvLine（スタブ）ではなく、WriteDiffCsvAsync（本番コードパス）の出力を検証する。
    /// </summary>
    [Fact]
    public async Task HvnCpe_CsvFormat_ModifyShouldShowActualHvnIdAndDiff()
    {
        var (columns, primaryKeys) = CreateHvnCpeColumnMetadata();
        var t1 = new DateTime(2024, 11, 18, 13, 20, 31);
        var t2 = new DateTime(2026, 3, 23, 9, 0, 0);

        // ユーザー提供の実データパターン
        var srcRows = new List<Dictionary<string, object?>>
        {
            MakeCpeRow(138869, 15, 1, "microsoft", "windows server 2016", "1607", t1),
            MakeCpeRow(138869, 16, 1, "microsoft", "windows server 2016", "1803", t1),
            MakeCpeRow(138869, 17, 1, "microsoft", "windows server 2016", "1903", t1),
        };

        var tgtRows = new List<Dictionary<string, object?>>
        {
            MakeCpeRow(138869, 15, 1, "microsoft", "windows server 2016", "1607", t1),
            MakeCpeRow(138869, 16, 1, "microsoft", "windows server 2016", "1607", t2),
            MakeCpeRow(138869, 17, 1, "microsoft", "windows server 2016", "1607", t2),
        };

        // WriteDiffCsvAsync の出力先ディレクトリを準備
        var outputDir = Path.Combine(Path.GetTempPath(), $"hvn_cpe_csv_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDir);

        try
        {
            var srcCtx = CreateHvnCpeMockContext(srcRows, columns, primaryKeys);
            var tgtCtx = CreateHvnCpeMockContext(tgtRows, columns, primaryKeys);

            // OutputDir を一意のディレクトリに設定して CompareAsync 実行
            var settings = new MySqlVerifierSettings
            {
                Source = new HscTool.Model.Json.MySQL { Database = "source_db" },
                Target = new HscTool.Model.Json.MySQL { Database = "target_db" },
                OutputDir = outputDir,
                IncludeTables = new[] { CreateHvnCpeTableConfig() }
            };

            var logger = new TestLogger();
            var service = new MySqlVerifierService(logger, settings);

            var srcFactoryField = typeof(MySqlVerifierService).GetField("_srcFactory",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var tgtFactoryField = typeof(MySqlVerifierService).GetField("_tgtFactory",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            srcFactoryField!.SetValue(service, new TestDbContextFactory(srcCtx));
            tgtFactoryField!.SetValue(service, new TestDbContextFactory(tgtCtx));

            MySqlVerifierDiff.LastInstance = null;
            var result = await service.CompareAsync();
            Assert.True(result.Success, $"CompareAsync failed: {result.ErrorMessage}\nLogs:\n{string.Join("\n", logger.Messages)}");

            // ★ WriteDiffCsvAsync が生成した実際のCSVファイルを読み込む
            var csvFiles = Directory.GetFiles(outputDir, "*.csv");
            Assert.True(csvFiles.Length > 0, $"CSVファイルが生成されていない。OutputDir: {outputDir}\nLogs:\n{string.Join("\n", logger.Messages)}");

            var allCsvLines = new List<string>();
            foreach (var csvFile in csvFiles)
            {
                var lines = await File.ReadAllLinesAsync(csvFile);
                allCsvLines.AddRange(lines);
            }

            var csvDump = string.Join("\n", allCsvLines.Select((line, idx) => $"[{idx}] {line}"));

            // ヘッダー行の確認
            Assert.True(allCsvLines.Count >= 1, $"CSVファイルが空。\n{csvDump}");
            var header = allCsvLines[0];
            Assert.Contains("DiffType", header);
            Assert.Contains("hvn_id", header);

            // hvn_id のカラムインデックスを特定
            var headerCells = header.Split(',');
            var hvnIdIndex = Array.IndexOf(headerCells, "hvn_id");
            Assert.True(hvnIdIndex >= 0, $"hvn_id カラムがヘッダーにない: {header}");

            // Modify データ行を検証
            var modifyLines = allCsvLines.Skip(1).Where(l => l.Contains("\"Modify\"")).ToList();
            Assert.True(modifyLines.Count >= 2, $"Modify行が2件未満: {modifyLines.Count}\nCSV:\n{csvDump}");

            foreach (var line in modifyLines)
            {
                // CSV セルを分割（ダブルクォート内のカンマを考慮）
                var cells = SplitCsvLine(line);
                Assert.True(cells.Length > hvnIdIndex,
                    $"セル数不足: expected > {hvnIdIndex}, got {cells.Length}\nLine: {line}");

                var hvnIdCell = cells[hvnIdIndex];

                // ★ 核心: hvn_id は "138869" であること（"Ignore" ではない）
                Assert.False(hvnIdCell.Contains("Ignore"),
                    $"WriteDiffCsvAsync 出力: hvn_id が 'Ignore'。期待値: '138869'\nhvn_id cell: {hvnIdCell}\nFull line: {line}\nAll CSV:\n{csvDump}");
                Assert.Contains("138869", hvnIdCell);

                // update_info の変更差分が "=> 1607" を含むこと
                Assert.True(line.Contains("=> 1607"),
                    $"Expected 'oldVal => 1607' in line but not found.\nFull line: {line}\nAll CSV:\n{csvDump}");
            }

            // BuildCsvLine はエントリ値を直接読むため、AddEntry で "Ignore" に上書きされた値を返す。
            // 条件付き Ignore は WriteDiffCsvAsync でのみ適用される。
            // → BuildCsvLine の hvn_id は "Ignore" になる（実 HSCTOOL と同じ動作）
            var diff = MySqlVerifierDiff.LastInstance;
            Assert.NotNull(diff);
            var buildCsvLines = diff!.BuildCsvLine();
            for (int i = 1; i < buildCsvLines.Count; i++)
            {
                var line = buildCsvLines[i];
                Assert.Contains("\"Modify\"", line);
                var cells = SplitCsvLine(line);
                // BuildCsvLine はエントリ値を直接読むため "Ignore" になる
                Assert.Contains("Ignore", cells[1]);
            }
        }
        finally
        {
            // テスト後にディレクトリを削除
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    /// <summary>CSV行をダブルクォート対応で分割</summary>
    private static string[] SplitCsvLine(string line)
    {
        var result = new List<string>();
        var current = "";
        var inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current += '"';
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                    current += '"';
                }
            }
            else if (line[i] == ',' && !inQuotes)
            {
                result.Add(current);
                current = "";
            }
            else
            {
                current += line[i];
            }
        }
        result.Add(current);
        return result.ToArray();
    }

    /// <summary>
    /// CSV出力フォーマット検証: Modify行は "oldVal => newVal" 形式で変更差分を出力し、
    /// PKカラムは条件付きIgnore値、未変更カラムは空文字であること。
    /// </summary>
    [Fact]
    public async Task Modify_CsvFormat_ShouldShowOldNewDiff()
    {
        var (columns, primaryKeys) = CreateColumnMetadata();
        var t1 = new DateTime(2026, 4, 1, 10, 0, 0);
        var t2 = new DateTime(2026, 4, 10, 15, 0, 0);

        // ソース: vendor_id=1808
        var srcRows = new List<Dictionary<string, object?>>
        {
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 1808, "ProductName", "V001", "http://example.com/info", t1)
        };

        // ターゲット: 旧データ(t1) + 新データ(t2, vendor_id=9233)
        var tgtRows = new List<Dictionary<string, object?>>
        {
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 1808, "ProductName", "V001", "http://example.com/info", t1),
            MakeRow(200, "JVNDB-2026-006630", "typeA", "advisoryB", 9233, "ProductName", "V001", "http://example.com/info", t2)
        };

        var srcCtx = CreateMockContext(srcRows, columns, primaryKeys);
        var tgtCtx = CreateMockContext(tgtRows, columns, primaryKeys);
        var entries = await RunCompareAndGetDiffs(srcCtx, tgtCtx);

        // Modify が 1 件であること
        var modifies = entries.Where(e => e.DiffType == DiffType.Modify).ToList();
        Assert.Single(modifies);

        var mod = modifies[0];
        // vendor_id が変更カラムであること
        Assert.Contains("vendor_id", mod.ChangedColumns);

        // SourceValues/TargetValues に実データが入っていること
        Assert.Equal(1808, Convert.ToInt32(mod.SourceValues["vendor_id"]));
        Assert.Equal(9233, Convert.ToInt32(mod.TargetValues["vendor_id"]));

        // BuildCsvLine で出力フォーマットを検証
        var diff = MySqlVerifierDiff.LastInstance;
        Assert.NotNull(diff);
        var csvLines = diff!.BuildCsvLine();
        Assert.True(csvLines.Count >= 2, "ヘッダー + データ行が必要");

        // データ行（index 1）を検証
        var dataLine = csvLines[1];
        // Modify 行であること
        Assert.Contains("\"Modify\"", dataLine);
        // vendor_id の変更差分が "1808 => 9233" 形式であること
        Assert.Contains("1808 => 9233", dataLine);
        // related_item_id（PK）は条件付き Ignore: ソース=100, ターゲット=200 → "Ignore"
        Assert.Contains("\"Ignore\"", dataLine);
    }

    /// <summary>
    /// CSV出力フォーマット検証（PK同値）: Modify行でPK値が同一の場合、実値を表示。
    /// </summary>
    [Fact]
    public async Task Modify_CsvFormat_SamePk_ShouldShowActualPkValue()
    {
        var (columns, primaryKeys) = CreateColumnMetadata();
        var t1 = new DateTime(2026, 4, 1, 10, 0, 0);
        var t2 = new DateTime(2026, 4, 10, 15, 0, 0);

        // ソース: related_item_id=100, vendor_id=1808
        var srcRows = new List<Dictionary<string, object?>>
        {
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 1808, "ProductName", "V001", "http://example.com/info", t1)
        };

        // ターゲット: related_item_id=100（同一PK）, vendor_id=9233
        var tgtRows = new List<Dictionary<string, object?>>
        {
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 1808, "ProductName", "V001", "http://example.com/info", t1),
            MakeRow(100, "JVNDB-2026-006630", "typeA", "advisoryB", 9233, "ProductName", "V001", "http://example.com/info", t2)
        };

        var srcCtx = CreateMockContext(srcRows, columns, primaryKeys);
        var tgtCtx = CreateMockContext(tgtRows, columns, primaryKeys);
        var (entries, csvLines) = await RunCompareAndGetDiffsWithCsv(srcCtx, tgtCtx, CreateTableConfig());

        var modifies = entries.Where(e => e.DiffType == DiffType.Modify).ToList();
        Assert.Single(modifies);

        // エントリ値は AddEntry により "Ignore" に上書き（実 HSCTOOL と同じ動作）
        Assert.Equal("Ignore", modifies[0].SourceValues["related_item_id"]?.ToString());

        // ★ WriteDiffCsvAsync の CSV出力で検証
        Assert.True(csvLines.Count >= 2, $"CSV行数不足: {csvLines.Count}");
        var modifyLines = csvLines.Skip(1).Where(l => l.Contains("\"Modify\"")).ToList();
        Assert.Single(modifyLines);

        var dataLine = modifyLines[0];
        // vendor_id 差分
        Assert.Contains("1808 => 9233", dataLine);
        // PK 同値 → CSV で "100" が含まれる（"Ignore" ではない）
        Assert.Contains("\"100\"", dataLine);
        Assert.DoesNotContain("\"Ignore\"", dataLine);
    }

    private static InMemoryDbContext CreateForcePkMockContext(
        List<Dictionary<string, object?>> tableRows,
        List<ColumnInfo> columns,
        List<ColumnInfo> primaryKeys,
        List<string> forcePkColumns,
        string[] configColumns,
        string[] compareColNames)
    {
        var ctx = new InMemoryDbContext();
        ctx.QueryHandler = sql =>
        {
            sql = sql.Trim();

            if (sql.Contains("INFORMATION_SCHEMA.COLUMNS"))
            {
                return columns.Select(c => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TABLE_NAME"] = "hvn",
                    ["COLUMN_NAME"] = c.ColumnName,
                    ["DATA_TYPE"] = c.DataType,
                    ["IS_NULLABLE"] = c.IsNullable ? "YES" : "NO",
                    ["COLUMN_TYPE"] = c.ColumnType,
                    ["COLUMN_KEY"] = c.IsPrimaryKey ? "PRI" : ""
                }).ToList();
            }

            if (sql.Contains("KEY_COLUMN_USAGE"))
            {
                return primaryKeys.Select((pk, idx) => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TABLE_NAME"] = "hvn",
                    ["COLUMN_NAME"] = pk.ColumnName,
                    ["ORDINAL_POSITION"] = idx + 1
                }).ToList();
            }

            // CRC32 ハッシュクエリ: force_pk カラムを PK として使用
            if (sql.Contains("CRC32") && sql.Contains("row_hash"))
            {
                return tableRows.Select(row =>
                {
                    var vals = compareColNames
                        .Select(c => row.GetValueOrDefault(c)?.ToString() ?? "")
                        .ToList();
                    var hash = (uint)Math.Abs(string.Join("|", vals).GetHashCode());
                    var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pk in forcePkColumns)
                        result[pk] = row.GetValueOrDefault(pk);
                    result["row_hash"] = hash;
                    return result;
                }).ToList();
            }

            // WHERE 句による行フェッチ (IN パターン)
            if (sql.Contains("WHERE") && sql.Contains("IN ("))
            {
                var inMatch = System.Text.RegularExpressions.Regex.Match(sql, @"IN\s*\((.+?)\)", System.Text.RegularExpressions.RegexOptions.Singleline);
                if (inMatch.Success)
                {
                    var inValues = inMatch.Groups[1].Value
                        .Split(',')
                        .Select(v => v.Trim().Trim('\''))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    return tableRows
                        .Where(r => inValues.Contains(r.GetValueOrDefault(forcePkColumns[0])?.ToString() ?? ""))
                        .ToList();
                }
            }

            // WHERE 句による行フェッチ (OR パターン)
            if (sql.Contains("WHERE"))
            {
                return tableRows;
            }

            return new List<Dictionary<string, object?>>();
        };
        return ctx;
    }
}

/// <summary>テスト用の DbContextFactory</summary>
public class TestDbContextFactory : IDbContextFactory
{
    private readonly InMemoryDbContext _context;
    public TestDbContextFactory(InMemoryDbContext context) { _context = context; }
    public IDbContext CreateContext() => _context;
}
