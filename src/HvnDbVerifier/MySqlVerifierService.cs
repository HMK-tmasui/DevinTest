using HscTool.Diagnostics;
using HscTool.Shared;
using HvnDbVerifier.Model.Json;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HscTool.Shared.Diff;

namespace HvnDbVerifier;

/// <summary>
/// 2つのデータベース間の MySQL のスキーマおよびデータ比較を行うサービス
/// </summary>
public class MySqlVerifierService
{
	private readonly ILogger Logger;
	private readonly MySqlVerifierSettings _settings;
    private readonly Dictionary<string, IncludeTableConfig> _includeTableConfigs;
    private readonly FrozenSet<string> _includeTableNames;

	private IDbContextFactory _srcFactory;
	private IDbContextFactory _tgtFactory;
    /// <summary>
    /// IDbContextFactory 対応のコンストラクター
    /// </summary>
    public MySqlVerifierService(ILogger logger, MySqlVerifierSettings settings)
    {
		Logger = logger;
		_settings = settings;

		_srcFactory = new MySqlDbContextFactory(_settings.Source);
		_tgtFactory = new MySqlDbContextFactory(_settings.Target);

        // include_tables を名前で辞書化
        _includeTableConfigs = _settings.IncludeTables
            .ToDictionary(t => t.Name, t => t, StringComparer.OrdinalIgnoreCase);
        _includeTableNames = _includeTableConfigs.Keys.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(settings.OutputDir))
        {
            Directory.CreateDirectory(settings.OutputDir);
        }
    }

    /// <summary>
    /// スキーマおよびデータの比較を実行
    /// </summary>
    public async Task<MySqlVerifierResult> CompareAsync()
    {
        MySqlVerifierResult result = new ();

        try
        {
			await using var srcContext = _srcFactory.CreateContext();
			await using var tgtContext = _tgtFactory.CreateContext();

			DbConnectionHelper srcConnection = new (Logger, srcContext);
			DbConnectionHelper tgtConnection = new (Logger, tgtContext);

			// 両データベースからテーブルのメタデータを取得
			var sourceTables = await GetTableMetadataAsync(srcConnection, _settings.Source.Database);
            var targetTables = await GetTableMetadataAsync(tgtConnection, _settings.Target.Database);

            var sourceTableNames = sourceTables.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var targetTableNames = targetTables.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 指定がある場合はテーブルをフィルター
            if (_includeTableNames.Count > 0)
            {
                sourceTableNames.IntersectWith(_includeTableNames);
            }
            // スキーマ差分を検出
            foreach (var tableName in sourceTableNames)
            {
                if (!targetTableNames.Contains(tableName))
                {
                    result.MissingInTarget.Add(tableName);
                    Logger.LogWarning($"[Schema] Table '{tableName}' missing in target");
                }
            }

            foreach (var tableName in targetTableNames)
            {
                if (!sourceTableNames.Contains(tableName) && 
                    (_includeTableNames.Count == 0 || _includeTableNames.Contains(tableName)))
                {
                    result.MissingInSource.Add(tableName);
                    Logger.LogWarning($"[Schema] Table '{tableName}' missing in source");
                }
            }

            // 両方に存在するテーブルのデータ比較
            var commonTables = sourceTableNames.Intersect(targetTableNames, StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var tableName in commonTables)
            {
                Logger.LogInfo($"[Data] Comparing table '{tableName}'...");

                var sourceMetadata = sourceTables[tableName];
                var targetMetadata = targetTables[tableName];

                // 主キーが一致するか確認
                var sourcePks = string.Join(",", sourceMetadata.GetPrimaryKeyNames());
                var targetPks = string.Join(",", targetMetadata.GetPrimaryKeyNames());

                if (sourcePks != targetPks)
                {
                    Logger.LogWarning($"Primary key mismatch for '{tableName}': source=({sourcePks}), target=({targetPks})");
                    result.PrimaryKeyMismatch.Add(tableName);
                    continue;
                }

                // include_tables 設定を取得
                _includeTableConfigs.TryGetValue(tableName, out var tableConfig);

                var pkColumns = sourceMetadata.GetPrimaryKeyNames();
                var hasPrimaryKey = sourceMetadata.PrimaryKeys.Count > 0;

                if (!hasPrimaryKey)
                    Logger.LogInfo($"  Table '{tableName}' has no primary key, using full-row comparison");

                // カラム決定: include_tables の columns が指定されていればそれを使用
                List<string> compareColumns;
                List<string> csvColumns;
                if (tableConfig?.Columns != null && tableConfig.Columns.Length > 0)
                {
                    // 指定カラムを CSV カラムとして使用
                    csvColumns = tableConfig.Columns.ToList();
                    // 比較カラム = 指定カラムから PK を除外
                    compareColumns = tableConfig.Columns
                        .Where(c => !pkColumns.Contains(c, StringComparer.OrdinalIgnoreCase))
                        .ToList();
                }
                else
                {
                    // INFORMATION_SCHEMA から比較カラムを取得（従来動作）
                    compareColumns = sourceMetadata.GetCompareColumns()
                        .Select(c => c.ColumnName)
                        .Intersect(targetMetadata.GetCompareColumns().Select(c => c.ColumnName))
                        .ToList();
                    csvColumns = pkColumns.Concat(compareColumns).Distinct().ToList();
                }

                MySqlVerifierDiff diff;
                if (hasPrimaryKey && compareColumns.Count == 0)
                {
                    // PKあり＆非PKカラムなし: 行ハッシュ比較不要、PK存在チェックのみ（Addition/Delete）
                    Logger.LogInfo($"  Table '{tableName}' has no non-PK columns, comparing row existence only");
                    diff = await CompareTableDataPkOnlyAsync(
                        tableName, srcConnection, tgtConnection, pkColumns, tableConfig, sourceMetadata);
                }
                else if (hasPrimaryKey)
                {
                    // PKあり: 行ハッシュベースでデータ比較
                    diff = await CompareTableDataAsync(
                        tableName, srcConnection, tgtConnection, pkColumns, compareColumns, tableConfig, sourceMetadata);
                }
                else
                {
                    // PKなし: 全行比較（Addition/Delete のみ検出）
                    var noPkColumns = csvColumns.Count > 0 ? csvColumns
                        : sourceMetadata.Columns.Select(c => c.ColumnName)
                            .Intersect(targetMetadata.Columns.Select(c => c.ColumnName), StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    diff = await CompareTableDataNoPkAsync(
                        tableName, srcConnection, tgtConnection, noPkColumns, tableConfig, sourceMetadata);
                }

                if (diff.DiffCount > 0)
                {
                    Logger.LogInfo($"[Data] Found {diff.DiffCount} differences in '{tableName}'");
                    result.TableDiffs[tableName] = diff.DiffCount;

                    // テーブルごとに CSV に出力
                    await WriteDiffCsvAsync(tableName, diff, csvColumns, pkColumns);
                }
                else
                {
                    Logger.LogInfo($"[Data] Table '{tableName}' is identical");
                }
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            Logger.LogError($"{ToolSet.GetCurrentMethod()}() [{ex.Message}]");
        }

        return result;
    }

    /// <summary>
    /// INFORMATION_SCHEMA からテーブルのメタデータを取得
    /// </summary>
    private async Task<Dictionary<string, MySqlVerifierMetadata>> GetTableMetadataAsync(DbConnectionHelper dbConnection, string database)
    {
        var tables = new Dictionary<string, MySqlVerifierMetadata>(StringComparer.OrdinalIgnoreCase);

        // 全カラム情報を取得
        var columnsSql = @"
            SELECT 
                TABLE_NAME,
                COLUMN_NAME,
                DATA_TYPE,
                IS_NULLABLE,
                COLUMN_TYPE,
                COLUMN_KEY
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @database
            ORDER BY TABLE_NAME, ORDINAL_POSITION";

        // using ブロックで DataReader を明示的にクローズしてから次のクエリを実行
        using (var colCmd = dbConnection.CreateCommand())
        {
            DbConnectionHelper.AddParameter(colCmd, "@database", database);
            using (var colReader = await colCmd.ExecuteReaderAsync(columnsSql))
            {
                while (await colReader.ReadAsync())
                {
                    var tableName = colReader["TABLE_NAME"]?.ToString() ?? "";
                    if (!tables.TryGetValue(tableName, out var metadata))
                    {
                        metadata = new MySqlVerifierMetadata { TableName = tableName };
                        tables[tableName] = metadata;
                    }

                    var columnInfo = new ColumnInfo
                    {
                        ColumnName = colReader["COLUMN_NAME"]?.ToString() ?? "",
                        DataType = colReader["DATA_TYPE"]?.ToString() ?? "",
                        IsNullable = colReader["IS_NULLABLE"]?.ToString() == "YES",
                        ColumnType = colReader["COLUMN_TYPE"]?.ToString() ?? "",
                        IsPrimaryKey = colReader["COLUMN_KEY"]?.ToString() == "PRI"
                    };

                    metadata.Columns.Add(columnInfo);
                }
            }
        } // colCmd / colReader は確実にクローズされる

        // 主キーの順序(ORDINAL_POSITION)を取得
        var pkSql = @"
            SELECT 
                TABLE_NAME,
                COLUMN_NAME,
                ORDINAL_POSITION
            FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = @database
              AND CONSTRAINT_NAME = 'PRIMARY'
            ORDER BY TABLE_NAME, ORDINAL_POSITION";

        using (var pkCmd = dbConnection.CreateCommand())
        {
            DbConnectionHelper.AddParameter(pkCmd, "@database", database);
            using (var pkReader = await pkCmd.ExecuteReaderAsync(pkSql))
            {
                while (await pkReader.ReadAsync())
                {
                    var tableName = pkReader["TABLE_NAME"]?.ToString() ?? "";
                    var colName = pkReader["COLUMN_NAME"]?.ToString() ?? "";
                    var ordinal = Convert.ToInt32(pkReader["ORDINAL_POSITION"]);

                    if (tables.TryGetValue(tableName, out var metadata))
                    {
                        var col = metadata.Columns.FirstOrDefault(c => c.ColumnName == colName);
                        if (col != null)
                        {
                            col.PrimaryKeyOrdinal = ordinal;
                            metadata.PrimaryKeys.Add(col);
                        }
                    }
                }
            }
        } // pkCmd / pkReader は確実にクローズされる

        // 主キー順序でソート
        foreach (var metadata in tables.Values)
        {
            metadata.PrimaryKeys = metadata.PrimaryKeys
                .OrderBy(c => c.PrimaryKeyOrdinal)
                .ToList();
        }

        return tables;
    }

    /// <summary>
    /// 全カラムがPKのテーブル用: PK存在チェックのみで Addition/Delete を検出（Modify は発生しない）
    /// </summary>
    private async Task<MySqlVerifierDiff> CompareTableDataPkOnlyAsync(
        string tableName,
        DbConnectionHelper srcConnection,
        DbConnectionHelper tgtConnection,
        List<string> pkColumns,
        IncludeTableConfig? tableConfig,
        MySqlVerifierMetadata metadata)
    {
        var diff = new MySqlVerifierDiff();
        diff.Init(pkColumns, pkColumns.ToArray());

        var columnTypes = metadata.Columns.ToDictionary(c => c.ColumnName, c => c.DataType, StringComparer.OrdinalIgnoreCase);

        // PK のみを SELECT する SQL を生成（exclude_conditions / reference_table 対応）
        var pkSelectSql = BuildPkOnlySelectSql(tableName, pkColumns, tableConfig, columnTypes);

        // 両データベースから PK セットを取得
        var sourceKeys = await GetPkSetAsync(srcConnection, pkSelectSql, pkColumns);
        Logger.LogDebug($"\tFetching PK set from Source: {sourceKeys.Count} rows");

        var targetKeys = await GetPkSetAsync(tgtConnection, pkSelectSql, pkColumns);
        Logger.LogDebug($"\tFetching PK set from Target: {targetKeys.Count} rows");

        // source のみに存在する行(target 側で削除)
        var deletedKeys = sourceKeys.Except(targetKeys).ToList();

        // target のみに存在する行(target 側で追加)
        var addedKeys = targetKeys.Except(sourceKeys).ToList();

        Logger.LogDebug($"\tDeleted rows[{deletedKeys.Count}] Added rows[{addedKeys.Count}] Modified rows[0]");

        // 差分となる行の完全なデータを取得
        var emptyCompareColumns = new List<string>();
        await AddSingleSideDiffsAsync(diff, srcConnection, tableName, pkColumns, emptyCompareColumns, deletedKeys, DiffType.Delete, isSource: true);
        await AddSingleSideDiffsAsync(diff, tgtConnection, tableName, pkColumns, emptyCompareColumns, addedKeys, DiffType.Addition, isSource: false);

        return diff;
    }

    /// <summary>
    /// PKのみ SELECT 用 SQL を生成（exclude_conditions / reference_table 対応）
    /// </summary>
    private string BuildPkOnlySelectSql(
        string tableName,
        List<string> pkColumns,
        IncludeTableConfig? tableConfig,
        Dictionary<string, string> columnTypes)
    {
        var useAlias = tableConfig?.ReferenceTable != null;
        var prefix = useAlias ? "t." : "";

        var pkSelect = string.Join(", ", pkColumns.Select(c => $"{prefix}`{c}`"));
        var fromClause = useAlias ? $"`{tableName}` t" : $"`{tableName}`";

        var joinClause = tableConfig?.ReferenceTable != null
            ? "\n" + BuildReferenceJoinClause(tableConfig.ReferenceTable, "t") : "";

        var whereClause = tableConfig?.ExcludeConditions is { Length: > 0 } ec
            ? "\nWHERE " + BuildExcludeWhereClause(ec, useAlias ? "t" : "") : "";

        return $@"
            SELECT {pkSelect}
            FROM {fromClause}{joinClause}{whereClause}
            ORDER BY {pkSelect}";
    }

    /// <summary>
    /// PK セットを取得
    /// </summary>
    private async Task<HashSet<string>> GetPkSetAsync(
        DbConnectionHelper dbConnection,
        string sql,
        List<string> pkColumns)
    {
        var keys = new HashSet<string>();

        using (var cmd = dbConnection.CreateCommand())
        {
            cmd.CommandTimeout = 600;

            using (var reader = await cmd.ExecuteReaderAsync(sql))
            {
                while (await reader.ReadAsync())
                {
                    var pkParts = pkColumns.Select((col, idx) =>
                    {
                        var val = reader.IsDBNull(reader.GetOrdinal(col)) ? "" : reader[col]?.ToString() ?? "";
                        return $"{col}={val}";
                    });
                    keys.Add(string.Join("||", pkParts));
                }
            }
        }

        return keys;
    }

    /// <summary>
    /// 行ハッシュ比較でテーブルのデータ比較（exclude_conditions / reference_table 対応）
    /// </summary>
    private async Task<MySqlVerifierDiff> CompareTableDataAsync(
        string tableName, 
		DbConnectionHelper srcConnection,
        DbConnectionHelper tgtConnection,
        List<string> pkColumns,
        List<string> compareColumns,
        IncludeTableConfig? tableConfig,
        MySqlVerifierMetadata metadata)
    {
        var diff = new MySqlVerifierDiff();
        var allColumns = pkColumns.Concat(compareColumns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        diff.Init(allColumns, pkColumns.ToArray());

        // カラム名→データ型のルックアップを生成
        var columnTypes = metadata.Columns.ToDictionary(c => c.ColumnName, c => c.DataType, StringComparer.OrdinalIgnoreCase);

        // 行ハッシュ算出用 SQL を生成（exclude_conditions / reference_table 対応）
        var hashSql = BuildRowHashSql(tableName, pkColumns, compareColumns, tableConfig, columnTypes);

        // 両データベースから行ハッシュを取得
        var sourceHashes = await GetRowHashesAsync(srcConnection, hashSql, pkColumns);
        Logger.LogDebug($"\tFetching row hashes from Source: {sourceHashes.Count} rows");

        var targetHashes = await GetRowHashesAsync(tgtConnection, hashSql, pkColumns);
        Logger.LogDebug($"\tFetching row hashes from Target: {targetHashes.Count} rows");

        // ハッシュ比較
        var sourceKeys = sourceHashes.Keys.ToHashSet();
        var targetKeys = targetHashes.Keys.ToHashSet();

        // source のみに存在する行(target 側で削除)
        var deletedKeys = sourceKeys.Except(targetKeys).ToList();

        // target のみに存在する行(target 側で追加)
        var addedKeys = targetKeys.Except(sourceKeys).ToList();

        // 両方に存在するがハッシュが異なる行(更新)
        var commonKeys = sourceKeys.Intersect(targetKeys).ToList();
        var modifiedKeys = commonKeys.Where(k => sourceHashes[k] != targetHashes[k]).ToList();
        Logger.LogDebug($"\tDeleted rows[{deletedKeys.Count}] Added rows[{addedKeys.Count}] Modified rows[{modifiedKeys.Count}]");

        // 差分となる行の完全なデータを取得
        await AddSingleSideDiffsAsync(diff, srcConnection, tableName, pkColumns, compareColumns, deletedKeys, DiffType.Delete, isSource: true);
        await AddSingleSideDiffsAsync(diff, tgtConnection, tableName, pkColumns, compareColumns, addedKeys, DiffType.Addition, isSource: false);

        if (modifiedKeys.Count > 0)
        {
            var sourceRows = (await FetchRowsAsync(srcConnection, tableName, pkColumns, compareColumns, modifiedKeys))
                .ToDictionary(r => GetPrimaryKeyString(r, pkColumns));
            var targetRows = (await FetchRowsAsync(tgtConnection, tableName, pkColumns, compareColumns, modifiedKeys))
                .ToDictionary(r => GetPrimaryKeyString(r, pkColumns));

            foreach (var key in modifiedKeys)
            {
                if (sourceRows.TryGetValue(key, out var srcRow) && targetRows.TryGetValue(key, out var tgtRow))
                    diff.AddEntry(DiffType.Modify, pkColumns, srcRow, tgtRow, compareColumns);
            }
        }

        return diff;
    }

    /// <summary>
    /// Delete/Addition の差分行を一括取得して追加
    /// </summary>
    private async Task AddSingleSideDiffsAsync(
        MySqlVerifierDiff diff,
        DbConnectionHelper connection,
        string tableName,
        List<string> pkColumns,
        List<string> compareColumns,
        List<string> keys,
        DiffType diffType,
        bool isSource)
    {
        if (keys.Count == 0) return;
        var rows = await FetchRowsAsync(connection, tableName, pkColumns, compareColumns, keys);
        foreach (var row in rows)
            diff.AddEntry(diffType, pkColumns, isSource ? row : null, isSource ? null : row, compareColumns);
    }

    /// <summary>
    /// PKなしテーブルの全行比較（行全体をキーとして比較、Addition/Delete のみ検出）
    /// </summary>
    private async Task<MySqlVerifierDiff> CompareTableDataNoPkAsync(
        string tableName,
        DbConnectionHelper srcConnection,
        DbConnectionHelper tgtConnection,
        List<string> allColumns,
        IncludeTableConfig? tableConfig,
        MySqlVerifierMetadata metadata)
    {
        var diff = new MySqlVerifierDiff();
        diff.Init(allColumns, null);

        var columnTypes = metadata.Columns.ToDictionary(c => c.ColumnName, c => c.DataType, StringComparer.OrdinalIgnoreCase);

        // 全行取得用 SQL を生成
        var fetchSql = BuildFetchAllRowsSql(tableName, allColumns, tableConfig, columnTypes);

        // 両DBから全行取得
        Logger.LogDebug($"\tFetching all rows from Source...");
        var sourceRows = await FetchAllRowsAsync(srcConnection, fetchSql);
        Logger.LogDebug($"\tFetched {sourceRows.Count} rows from Source");

        Logger.LogDebug($"\tFetching all rows from Target...");
        var targetRows = await FetchAllRowsAsync(tgtConnection, fetchSql);
        Logger.LogDebug($"\tFetched {targetRows.Count} rows from Target");

        // 行シグネチャ → 行リストのマルチセットを構築
        var sourceMultiset = BuildRowMultiset(sourceRows, allColumns);
        var targetMultiset = BuildRowMultiset(targetRows, allColumns);

        // マルチセット差分比較
        var allSignatures = sourceMultiset.Keys.Union(targetMultiset.Keys);
        foreach (var sig in allSignatures)
        {
            var srcList = sourceMultiset.GetValueOrDefault(sig, new List<Dictionary<string, object?>>());
            var tgtList = targetMultiset.GetValueOrDefault(sig, new List<Dictionary<string, object?>>());

            // ソース側の余剰行 → Delete
            for (int i = tgtList.Count; i < srcList.Count; i++)
                diff.AddEntry(DiffType.Delete, new List<string>(), srcList[i], null, allColumns);

            // ターゲット側の余剰行 → Addition
            for (int i = srcList.Count; i < tgtList.Count; i++)
                diff.AddEntry(DiffType.Addition, new List<string>(), null, tgtList[i], allColumns);
        }

        Logger.LogDebug($"\tDeleted rows[{diff.Entries.Count(e => e.DiffType == DiffType.Delete)}] Added rows[{diff.Entries.Count(e => e.DiffType == DiffType.Addition)}]");

        return diff;
    }

    /// <summary>
    /// PKなしテーブルの全行取得用 SQL を生成（exclude_conditions / reference_table 対応）
    /// </summary>
    private string BuildFetchAllRowsSql(
        string tableName,
        List<string> allColumns,
        IncludeTableConfig? tableConfig,
        Dictionary<string, string> columnTypes)
    {
        var useAlias = tableConfig?.ReferenceTable != null;
        var prefix = useAlias ? "t." : "";

        var selectCols = string.Join(", ", allColumns.Select(c => $"{prefix}`{c}`"));
        var fromClause = useAlias ? $"`{tableName}` t" : $"`{tableName}`";

        var joinClause = tableConfig?.ReferenceTable != null
            ? "\n" + BuildReferenceJoinClause(tableConfig.ReferenceTable, "t") : "";

        var whereClause = tableConfig?.ExcludeConditions is { Length: > 0 } ec
            ? "\nWHERE " + BuildExcludeWhereClause(ec, useAlias ? "t" : "") : "";

        var orderBy = string.Join(", ", allColumns.Select(c => $"{prefix}`{c}`"));

        return $"SELECT {selectCols} FROM {fromClause}{joinClause}{whereClause} ORDER BY {orderBy}";
    }

    /// <summary>
    /// SQL で全行を取得
    /// </summary>
    private async Task<List<Dictionary<string, object?>>> FetchAllRowsAsync(
        DbConnectionHelper dbConnection,
        string sql)
    {
        var rows = new List<Dictionary<string, object?>>();

        using (var cmd = dbConnection.CreateCommand())
        {
            cmd.CommandTimeout = 600;
            using (var reader = await cmd.ExecuteReaderAsync(sql))
            {
                while (await reader.ReadAsync())
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (int c = 0; c < reader.FieldCount; c++)
                    {
                        var colName = reader.GetName(c);
                        row[colName] = reader.IsDBNull(c) ? null : reader.GetValue(c);
                    }
                    rows.Add(row);
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// 行のマルチセットを構築（シグネチャ → 行リスト）
    /// 同一内容の行が複数存在する場合に対応
    /// </summary>
    private static Dictionary<string, List<Dictionary<string, object?>>> BuildRowMultiset(
        List<Dictionary<string, object?>> rows,
        List<string> allColumns)
    {
        var multiset = new Dictionary<string, List<Dictionary<string, object?>>>();

        foreach (var row in rows)
        {
            var sig = string.Join("||", allColumns.Select(c =>
                RowDiff.FormatCsvValue(row.GetValueOrDefault(c))));

            if (!multiset.TryGetValue(sig, out var list))
            {
                list = new List<Dictionary<string, object?>>();
                multiset[sig] = list;
            }
            list.Add(row);
        }

        return multiset;
    }

    /// <summary>
    /// CRC32 を用いた行ハッシュ算出用 SQL を生成（exclude_conditions / reference_table 対応）
    /// </summary>
    private string BuildRowHashSql(
        string tableName,
        List<string> pkColumns,
        List<string> compareColumns,
        IncludeTableConfig? tableConfig,
        Dictionary<string, string> columnTypes)
    {
        var useAlias = tableConfig?.ReferenceTable != null;
        var prefix = useAlias ? "t." : "";

        var pkSelect = string.Join(", ", pkColumns.Select(c => $"{prefix}`{c}`"));
        // DATETIME/TIMESTAMP 型カラムはミリ秒以下を切り捨て（秒単位で比較）
        var concatExpr = $"CONCAT_WS('|', {string.Join(", ", compareColumns.Select(c => FormatHashColumn(c, prefix, columnTypes)))})";
        var fromClause = useAlias ? $"`{tableName}` t" : $"`{tableName}`";

        var joinClause = tableConfig?.ReferenceTable != null
            ? "\n" + BuildReferenceJoinClause(tableConfig.ReferenceTable, "t") : "";

        var whereClause = tableConfig?.ExcludeConditions is { Length: > 0 } ec
            ? "\nWHERE " + BuildExcludeWhereClause(ec, useAlias ? "t" : "") : "";

        return $@"
            SELECT {pkSelect}, CRC32({concatExpr}) AS row_hash
            FROM {fromClause}{joinClause}{whereClause}
            ORDER BY {pkSelect}";
    }

    /// <summary>
    /// exclude_conditions から除外 WHERE 句を生成
    /// </summary>
    private static string BuildExcludeWhereClause(ExcludeCondition[] conditions, string tableAlias = "")
    {
        var prefix = string.IsNullOrEmpty(tableAlias) ? "" : $"{tableAlias}.";
        var parts = conditions.Select(c =>
        {
            var colRef = $"{prefix}`{c.Column}`";
            if (c.Value == null)
                return $"{colRef} IS NOT NULL";
            return $"NOT ({colRef} = {FormatSqlValue(c.Value)})";
        });
        return string.Join(" AND ", parts);
    }

    /// <summary>
    /// reference_table から INNER JOIN 句を生成（reference_table 自身の exclude_conditions も適用）
    /// </summary>
    private static string BuildReferenceJoinClause(ReferenceTableConfig refTable, string tableAlias)
    {
        var subWhere = refTable.ExcludeConditions.Length > 0
            ? " WHERE " + BuildExcludeWhereClause(refTable.ExcludeConditions)
            : "";

        return $@"INNER JOIN (
                SELECT DISTINCT `{refTable.JoinColumn}` FROM `{refTable.Name}`{subWhere}
            ) _ref ON {tableAlias}.`{refTable.JoinColumn}` = _ref.`{refTable.JoinColumn}`";
    }

    /// <summary>
    /// ハッシュ用 SQL カラム式を生成（DATETIME/TIMESTAMP 型はミリ秒以下を切り捨て）
    /// </summary>
    private static string FormatHashColumn(string columnName, string prefix, Dictionary<string, string> columnTypes)
    {
        var colRef = $"{prefix}`{columnName}`";
        if (columnTypes.TryGetValue(columnName, out var dataType))
        {
            var lower = dataType.ToLowerInvariant();
            if (lower.Contains("datetime") || lower.Contains("timestamp"))
                return $"IFNULL(DATE_FORMAT({colRef}, '%Y-%m-%d %H:%i:%s'), '')";
        }
        return $"IFNULL({colRef}, '')";
    }

    /// <summary>
    /// SQL リテラル値のフォーマット
    /// </summary>
    private static string FormatSqlValue(object? value)
    {
        if (value == null) return "NULL";
        if (value is string s) return $"'{s.Replace("'", "''")}'";
        return value.ToString() ?? "NULL";
    }


    /// <summary>
    /// データベースから行ハッシュを取得
    /// </summary>
    private async Task<Dictionary<string, uint>> GetRowHashesAsync(
        DbConnectionHelper dbConnection,
        string sql,
        List<string> pkColumns)
    {
        var hashes = new Dictionary<string, uint>();

        using (var cmd = dbConnection.CreateCommand())
        {
			cmd.CommandTimeout = 600; // 大きなテーブル向けに 10 分

            using (var reader = await cmd.ExecuteReaderAsync(sql))
            {
                while (await reader.ReadAsync())
                {
                    // 複合キーで値が重複していても区別できるよう、インデックス付き PK キーを構築
                    var pkParts = pkColumns.Select((col, idx) =>
                    {
                        var val = reader.IsDBNull(reader.GetOrdinal(col)) ? "" : reader[col]?.ToString() ?? "";
                        return $"{col}={val}";
                    });
                    var key = string.Join("||", pkParts);

                    var hashOrdinal = reader.GetOrdinal("row_hash");
                    var hash = reader.IsDBNull(hashOrdinal) ? 0u : Convert.ToUInt32(reader[hashOrdinal]);

                    hashes[key] = hash;
                }
            }
        }

        return hashes;
    }

    /// <summary>
    /// 指定した主キーに一致する行の完全なデータを取得
    /// </summary>
    private async Task<List<Dictionary<string, object?>>> FetchRowsAsync(
        DbConnectionHelper dbConnection,
        string tableName,
        List<string> pkColumns,
        List<string> dataColumns,
        List<string> pkKeys)
    {
        var rows = new List<Dictionary<string, object?>>();

        if (pkKeys.Count == 0) return rows;

        // 主キー用 WHERE 句を組み立て
        var allColumns = pkColumns.Concat(dataColumns).Distinct().ToList();
        var selectCols = string.Join(", ", allColumns.Select(c => $"`{c}`"));

        // クエリが長くなりすぎないようバッチ処理
        const int batchSize = 1000;
        for (int i = 0; i < pkKeys.Count; i += batchSize)
        {
            var batch = pkKeys.Skip(i).Take(batchSize).ToList();

            string whereClause;
            if (pkColumns.Count == 1)
            {
                var inValues = string.Join(", ", batch.Select(k =>
                {
                    var val = k.Split('=', 2).Last();
                    return $"'{val.Replace("'", "''")}'";
                }));
                whereClause = $"`{pkColumns[0]}` IN ({inValues})";
            }
            else
            {
                // 複合キーで値が重複していても区別できるよう、インデックス付き PK キーを構築
                var rowConditions = batch.Select(k =>
                {
                    var parts = k.Split("||");
                    var conditions = parts.Select((p, idx) =>
                    {
                        var val = p.Split('=', 2).Last();
                        return $"`{pkColumns[idx]}` = '{val.Replace("'", "''")}'";
                    });
                    return $"({string.Join(" AND ", conditions)})";
                });
                whereClause = string.Join(" OR ", rowConditions);
            }

            var sql = $"SELECT {selectCols} FROM `{tableName}` WHERE {whereClause}";

            using (var cmd = dbConnection.CreateCommand())
            {
                cmd.CommandTimeout = 600;

                using (var reader = await cmd.ExecuteReaderAsync(sql))
                {
                    while (await reader.ReadAsync())
                    {
                        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        for (int c = 0; c < reader.FieldCount; c++)
                        {
                            var colName = reader.GetName(c);
                            row[colName] = reader.IsDBNull(c) ? null : reader.GetValue(c);
                        }
                        rows.Add(row);
                    }
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// 行データから主キー文字列を生成
    /// </summary>
    private static string GetPrimaryKeyString(Dictionary<string, object?> row, List<string> pkColumns)
        => string.Join("||", pkColumns.Select(c => $"{c}={row.GetValueOrDefault(c)?.ToString() ?? ""}"));

    /// <summary>
    /// テーブルごとに CSV ファイルへ出力（最大 MaxFileSize で分割）
    /// ファイル名に PK カラム名を含める
    /// BuildCsvLine() で List&lt;string&gt; を取得し、ファイルに書き込む
    /// </summary>
    private async Task WriteDiffCsvAsync(
        string tableName,
        MySqlVerifierDiff diff,
        List<string> allColumns,
        List<string> pkColumns)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var pkSuffix = pkColumns.Count > 0 ? $"_PK({string.Join(",", pkColumns)})" : "_NoPK";
        var baseFileName = $"{tableName}_diff_{timestamp}{pkSuffix}";

        // 全行を一括生成（index 0 = ヘッダー、1以降 = データ行）
        var lines = diff.BuildCsvLine();

        if (lines.Count <= 1) return; // ヘッダーのみ = データなし

        var headerLine = lines[0];
        var headerBytes = Encoding.UTF8.GetByteCount(headerLine + Environment.NewLine);
        var fileIndex = 1;
        var currentSize = 0L;
        StreamWriter? writer = null;
        var currentFilePath = string.Empty;

        try
        {
            for (int i = 1; i < lines.Count; i++)
            {
                var lineBytes = Encoding.UTF8.GetByteCount(lines[i] + Environment.NewLine);

                // 新しいファイルが必要かチェック
                if (writer == null || currentSize + lineBytes > _settings.MaxFileSize && currentSize > headerBytes)
                {
                    if (writer != null)
                    {
                        await writer.DisposeAsync();
                        Logger.LogInfo($"  Written: {Path.GetFullPath(currentFilePath)} ({currentSize} bytes)");
                    }

                    // 新しいファイルを作成(BOM なし UTF-8。HscCsvCache 互換)
                    currentFilePath = Path.Combine(_settings.OutputDir, $"{baseFileName}_{fileIndex:D3}.csv");
                    writer = new StreamWriter(currentFilePath, false, new UTF8Encoding(false));
                    fileIndex++;

                    await writer.WriteLineAsync(headerLine);
                    currentSize = headerBytes;
                }

                await writer.WriteLineAsync(lines[i]);
                currentSize += lineBytes;
            }
        }
        finally
        {
            if (writer != null)
            {
                await writer.DisposeAsync();
                Logger.LogInfo($"  Written: {currentFilePath} ({currentSize} bytes)");
            }
        }
    }


	public void PrintSummary(MySqlVerifierResult result)
	{
		StringBuilder sb = new();
		sb.AppendLine("=== Schema Comparison Result ===");
		if (result.Success)
		{
			sb.AppendLine("Comparison completed successfully.");

			if (result.MissingInTarget.Count > 0)
			{
				sb.AppendLine($"Tables missing in target: {string.Join(", ", result.MissingInTarget)}");
			}

			if (result.MissingInSource.Count > 0)
			{
				sb.AppendLine($"Tables missing in source: {string.Join(", ", result.MissingInSource)}");
			}

			if (result.PrimaryKeyMismatch.Count > 0)
			{
				sb.AppendLine($"Primary key mismatch: {string.Join(", ", result.PrimaryKeyMismatch)}");
			}

			if (result.NoPrimaryKey.Count > 0)
			{
				sb.AppendLine($"No primary key (skipped): {string.Join(", ", result.NoPrimaryKey)}");
			}

			if (result.TableDiffs.Count > 0)
			{
				sb.AppendLine("Tables with differences:");
				foreach (var (table, count) in result.TableDiffs)
				{
					sb.AppendLine($"  {table}: {count} rows");
				}
			}
			else
			{
				sb.AppendLine("No data differences found.");
			}
		}

		else sb.AppendLine($"Comparison failed: {result.ErrorMessage}");

		Logger.NewLine();
		Logger.LogInfo(sb.ToString());
	} 
}

