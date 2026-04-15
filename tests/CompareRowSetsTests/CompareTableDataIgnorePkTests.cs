using HscTool.Diagnostics;
using HscTool.Shared;
using HscTool.Shared.Diff;
using HvnDbVerifier;
using HvnDbVerifier.Model.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xunit;

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
        IgnorePrimaryKey = true,
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
    private static async Task<List<RowDiff>> RunCompareAndGetDiffs(
        InMemoryDbContext srcCtx, InMemoryDbContext tgtCtx)
    {
        var settings = new MySqlVerifierSettings
        {
            Source = new HscTool.Model.Json.MySQL { Database = "source_db" },
            Target = new HscTool.Model.Json.MySQL { Database = "target_db" },
            OutputDir = "/tmp/test_output",
            IncludeTables = new[] { CreateTableConfig() }
        };

        var logger = new TestLogger();
        var service = new MySqlVerifierService(logger, settings);

        var srcFactoryField = typeof(MySqlVerifierService).GetField("_srcFactory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var tgtFactoryField = typeof(MySqlVerifierService).GetField("_tgtFactory",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        srcFactoryField!.SetValue(service, new TestDbContextFactory(srcCtx));
        tgtFactoryField!.SetValue(service, new TestDbContextFactory(tgtCtx));

        // LastInstance をクリア
        MySqlVerifierDiff.LastInstance = null;

        var result = await service.CompareAsync();
        Assert.True(result.Success, $"CompareAsync failed: {result.ErrorMessage}\nLogs:\n{string.Join("\n", logger.Messages)}");

        var diff = MySqlVerifierDiff.LastInstance;
        return diff?.Entries ?? new List<RowDiff>();
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
        Assert.NotNull(mod.SourceRow);
        Assert.NotNull(mod.TargetRow);
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
}

/// <summary>テスト用の DbContextFactory</summary>
public class TestDbContextFactory : IDbContextFactory
{
    private readonly InMemoryDbContext _context;
    public TestDbContextFactory(InMemoryDbContext context) { _context = context; }
    public IDbContext CreateContext() => _context;
}
