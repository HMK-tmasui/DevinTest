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

	/// <summary>並列フェッチの最大同時実行数</summary>
	private const int MaxParallelFetches = 4;

	/// <summary>
	/// Modify エントリの元の PK 値を保持。
	/// AddEntry が PK 値を "Ignore" に上書きするため、
	/// AddEntry 呼び出し前に元の値を退避し、WriteDiffCsvAsync で条件付き Ignore を実装する。
	/// Key = diff.Entries のインデックス, Value = { pkColName: (srcVal, tgtVal) }
	/// </summary>
	private Dictionary<int, Dictionary<string, (object? src, object? tgt)>> _modifyOriginalPks = new();

	/// <summary>IgnorePK モードで "Ignore" 対象にする PK カラム名リスト</summary>
	private List<string> _ignorePkColumns = new();

	private IDbContextFactory _srcFactory;
	private IDbContextFactory _tgtFactory;
	public MySqlVerifierService(ILogger logger, MySqlVerifierSettings settings)
	{
		Logger = logger;
		_settings = settings;
		_srcFactory = new MySqlDbContextFactory(_settings.Source);
		_tgtFactory = new MySqlDbContextFactory(_settings.Target);
		_includeTableConfigs = _settings.IncludeTables
			.ToDictionary(t => t.Name, t => t, StringComparer.OrdinalIgnoreCase);
		_includeTableNames = _includeTableConfigs.Keys.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
		if (!Directory.Exists(settings.OutputDir))
			Directory.CreateDirectory(settings.OutputDir);
	}

	// ─────────────────────────────────────────────
	//  共通ヘルパー
	// ─────────────────────────────────────────────

	/// <summary>リスト辞書にエントリを追加</summary>
	private static void AddToList<TKey, TVal>(Dictionary<TKey, List<TVal>> dict, TKey key, TVal value) where TKey : notnull
	{
		if (!dict.TryGetValue(key, out var list))
		{
			list = new List<TVal>(4); // 初期容量を小さく設定
			dict[key] = list;
		}
		list.Add(value);
	}


	/// <summary>カラム値を比較用文字列に変換（DateTime は秒単位に正規化）</summary>
	private static string FormatColumnValue(object? val)
	{
		if (val is DateTime dt) return dt.ToString("yyyy-MM-dd HH:mm:ss");
		return val?.ToString() ?? "";
	}

	/// <summary>FROM / JOIN / WHERE 句を共通生成</summary>
	private static (string prefix, string fromClause, string joinClause, string whereClause)
		BuildFromJoinWhere(string tableName, IncludeTableConfig? tableConfig)
	{
		var useAlias = tableConfig?.ReferenceTable != null;
		var prefix = useAlias ? "t." : "";
		var fromClause = useAlias ? $"`{tableName}` t" : $"`{tableName}`";
		var joinClause = tableConfig?.ReferenceTable != null
			? "\n" + BuildReferenceJoinClause(tableConfig.ReferenceTable, "t") : "";
		var whereClause = tableConfig?.ExcludeConditions is { Length: > 0 } ec
			? "\nWHERE " + BuildExcludeWhereClause(ec, useAlias ? "t" : "") : "";
		return (prefix, fromClause, joinClause, whereClause);
	}


	/// <summary>カラム名→データ型の辞書を生成</summary>
	private static Dictionary<string, string> BuildColumnTypes(MySqlVerifierMetadata metadata)
		=> metadata.Columns.ToDictionary(c => c.ColumnName, c => c.DataType, StringComparer.OrdinalIgnoreCase);

	/// <summary>ペアにならなかった余りを集約リストに追加するか、直接 diff に報告する</summary>
	private static void ReportOrCollectUnmatched(
		List<Dictionary<string, object?>> unmatched, HashSet<int> paired,
		List<Dictionary<string, object?>>? collectOut, Action<Dictionary<string, object?>> report)
	{
		for (int i = 0; i < unmatched.Count; i++)
		{
			if (paired.Contains(i)) continue;
			if (collectOut != null) collectOut.Add(unmatched[i]);
			else report(unmatched[i]);
		}
	}

	/// <summary>
	/// スキーマおよびデータの比較を実行
	/// </summary>
	public async Task<MySqlVerifierResult> CompareAsync()
	{
		MySqlVerifierResult result = new();

		try
		{
			await using var srcContext = _srcFactory.CreateContext();
			await using var tgtContext = _tgtFactory.CreateContext();

			DbConnectionHelper srcConnection = new(Logger, srcContext);
			DbConnectionHelper tgtConnection = new(Logger, tgtContext);

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
				var originalDbPkColumns = pkColumns.ToList(); // 元のDB PKを保持

				// force_pk: 指定されたカラムを PK として強制的に扱う
				if (tableConfig?.ForcePK != null && tableConfig.ForcePK.Count > 0)
				{
					pkColumns = tableConfig.ForcePK.ToList();
					hasPrimaryKey = true;
					Logger.LogInfo($"  Table '{tableName}' using force_pk: [{string.Join(", ", pkColumns)}]");
				}

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

				// IgnorePK モード判定
				var ignorePk = tableConfig?.IgnorePK == true;
				if (ignorePk && string.IsNullOrWhiteSpace(tableConfig?.ModifiedKey))
				{
					Logger.LogWarning($"  Table '{tableName}' has IgnorePrimaryKey=true but DateTimeKey is not set. Skipping.");
					continue;
				}
				if (ignorePk && (tableConfig?.AlternativeKey == null || tableConfig.AlternativeKey.Count == 0))
				{
					Logger.LogWarning($"  Table '{tableName}' has IgnorePrimaryKey=true but AlternativeKey is not set. Skipping.");
					continue;
				}

				MySqlVerifierDiff diff;
				if (ignorePk)
				{
					// IgnorePK モード: ModifiedKey で行をマッチし、PK以外のカラムのハッシュで比較
					Logger.LogInfo($"  Table '{tableName}' using IgnorePrimaryKey mode (DateTimeKey='{tableConfig!.ModifiedKey}')");
					diff = await CompareTableDataIgnorePkAsync(
						tableName, srcConnection, tgtConnection, pkColumns, compareColumns, tableConfig, sourceMetadata, tableConfig!.AlternativeKey, originalDbPkColumns);
				}
				else if (hasPrimaryKey && compareColumns.Count == 0)
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
					await WriteDiffCsvAsync(tableName, diff, csvColumns, pkColumns, ignorePk);
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

	private async Task<Dictionary<string, MySqlVerifierMetadata>> GetTableMetadataAsync(DbConnectionHelper dbConnection, string database)
	{
		var tables = new Dictionary<string, MySqlVerifierMetadata>(StringComparer.OrdinalIgnoreCase);

		var columnsSql = @"
            SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_TYPE, COLUMN_KEY
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @database
            ORDER BY TABLE_NAME, ORDINAL_POSITION";

		using (var colCmd = dbConnection.CreateCommand())
		{
			DbConnectionHelper.AddParameter(colCmd, "@database", database);
			using var colReader = await colCmd.ExecuteReaderAsync(columnsSql);
			while (await colReader.ReadAsync())
			{
				var tableName = colReader["TABLE_NAME"]?.ToString() ?? "";
				if (!tables.TryGetValue(tableName, out var metadata))
				{
					metadata = new MySqlVerifierMetadata { TableName = tableName };
					tables[tableName] = metadata;
				}
				metadata.Columns.Add(new ColumnInfo
				{
					ColumnName = colReader["COLUMN_NAME"]?.ToString() ?? "",
					DataType = colReader["DATA_TYPE"]?.ToString() ?? "",
					IsNullable = colReader["IS_NULLABLE"]?.ToString() == "YES",
					ColumnType = colReader["COLUMN_TYPE"]?.ToString() ?? "",
					IsPrimaryKey = colReader["COLUMN_KEY"]?.ToString() == "PRI"
				});
			}
		}

		var pkSql = @"
            SELECT TABLE_NAME, COLUMN_NAME, ORDINAL_POSITION
            FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = @database AND CONSTRAINT_NAME = 'PRIMARY'
            ORDER BY TABLE_NAME, ORDINAL_POSITION";

		using (var pkCmd = dbConnection.CreateCommand())
		{
			DbConnectionHelper.AddParameter(pkCmd, "@database", database);
			using var pkReader = await pkCmd.ExecuteReaderAsync(pkSql);
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

		foreach (var metadata in tables.Values)
			metadata.PrimaryKeys = metadata.PrimaryKeys.OrderBy(c => c.PrimaryKeyOrdinal).ToList();

		return tables;
	}

	private async Task<MySqlVerifierDiff> CompareTableDataPkOnlyAsync(
		string tableName, DbConnectionHelper srcConnection, DbConnectionHelper tgtConnection,
		List<string> pkColumns, IncludeTableConfig? tableConfig, MySqlVerifierMetadata metadata)
	{
		var diff = new MySqlVerifierDiff();
		diff.Init(pkColumns, pkColumns.ToArray());

		var (prefix, fromClause, joinClause, whereClause) = BuildFromJoinWhere(tableName, tableConfig);
		var pkSelect = string.Join(", ", pkColumns.Select(c => $"{prefix}`{c}`"));
		var sql = $"SELECT {pkSelect} FROM {fromClause}{joinClause}{whereClause} ORDER BY {pkSelect}";

		// 並列フェッチ: ソースとターゲットの PK セットを同時取得
		var srcTask = GetPkSetAsync(srcConnection, sql, pkColumns);
		var tgtTask = GetPkSetAsync(tgtConnection, sql, pkColumns);
		await Task.WhenAll(srcTask, tgtTask);
		var sourceKeys = srcTask.Result;
		var targetKeys = tgtTask.Result;
		Logger.LogDebug($"\tFetching PK set from Source: {sourceKeys.Count} rows");
		Logger.LogDebug($"\tFetching PK set from Target: {targetKeys.Count} rows");

		// FrozenSet で高速 Contains
		var srcFrozen = sourceKeys.ToFrozenSet(StringComparer.Ordinal);
		var tgtFrozen = targetKeys.ToFrozenSet(StringComparer.Ordinal);
		var deletedKeys = new List<string>(sourceKeys.Count / 10);
		var addedKeys = new List<string>(targetKeys.Count / 10);
		foreach (var k in sourceKeys) { if (!tgtFrozen.Contains(k)) deletedKeys.Add(k); }
		foreach (var k in targetKeys) { if (!srcFrozen.Contains(k)) addedKeys.Add(k); }
		Logger.LogDebug($"\tDeleted rows[{deletedKeys.Count}] Added rows[{addedKeys.Count}] Modified rows[0]");

		// Delete/Addition を並列フェッチ
		var emptyCompareColumns = new List<string>();
		var delTask = AddSingleSideDiffsAsync(diff, srcConnection, tableName, pkColumns, emptyCompareColumns, deletedKeys, DiffType.Delete, isSource: true);
		var addTask = AddSingleSideDiffsAsync(diff, tgtConnection, tableName, pkColumns, emptyCompareColumns, addedKeys, DiffType.Addition, isSource: false);
		await Task.WhenAll(delTask, addTask);
		return diff;
	}

	private async Task<HashSet<string>> GetPkSetAsync(DbConnectionHelper dbConnection, string sql, List<string> pkColumns)
	{
		var keys = new HashSet<string>(StringComparer.Ordinal);
		var sb = new StringBuilder(128);
		using var cmd = dbConnection.CreateCommand();
		cmd.CommandTimeout = 600;
		using var reader = await cmd.ExecuteReaderAsync(sql);
		while (await reader.ReadAsync())
		{
			sb.Clear();
			for (int i = 0; i < pkColumns.Count; i++)
			{
				if (i > 0) sb.Append("|");
				var col = pkColumns[i];
				var ordinal = reader.GetOrdinal(col);
				sb.Append(col).Append('=');
				if (!reader.IsDBNull(ordinal))
					sb.Append(reader[col]?.ToString() ?? "");
			}
			keys.Add(sb.ToString());
		}
		return keys;
	}

	private async Task<MySqlVerifierDiff> CompareTableDataAsync(
		string tableName, DbConnectionHelper srcConnection, DbConnectionHelper tgtConnection,
		List<string> pkColumns, List<string> compareColumns,
		IncludeTableConfig? tableConfig, MySqlVerifierMetadata metadata)
	{
		var diff = new MySqlVerifierDiff();
		var allColumns = pkColumns.Concat(compareColumns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		diff.Init(allColumns, pkColumns.ToArray());

		var columnTypes = BuildColumnTypes(metadata);
		var hashSql = BuildRowHashSql(tableName, pkColumns, compareColumns, tableConfig, columnTypes);

		// 並列フェッチ: ソースとターゲットのハッシュを同時取得
		var sourceHashTask = GetRowHashesAsync(srcConnection, hashSql, pkColumns);
		var targetHashTask = GetRowHashesAsync(tgtConnection, hashSql, pkColumns);
		await Task.WhenAll(sourceHashTask, targetHashTask);
		var sourceHashes = sourceHashTask.Result;
		var targetHashes = targetHashTask.Result;
		Logger.LogDebug($"\tFetching row hashes from Source: {sourceHashes.Count} rows");
		Logger.LogDebug($"\tFetching row hashes from Target: {targetHashes.Count} rows");

		// FrozenSet で高速なキー判定
		var sourceKeySet = sourceHashes.Keys.ToFrozenSet(StringComparer.Ordinal);
		var targetKeySet = targetHashes.Keys.ToFrozenSet(StringComparer.Ordinal);

		var deletedKeys = new List<string>(sourceHashes.Count / 10);
		var addedKeys = new List<string>(targetHashes.Count / 10);
		var modifiedKeys = new List<string>(Math.Min(sourceHashes.Count, targetHashes.Count) / 10);

		foreach (var k in sourceHashes.Keys)
		{
			if (!targetKeySet.Contains(k)) deletedKeys.Add(k);
			else if (sourceHashes[k] != targetHashes[k]) modifiedKeys.Add(k);
		}
		foreach (var k in targetHashes.Keys)
		{
			if (!sourceKeySet.Contains(k)) addedKeys.Add(k);
		}
		Logger.LogDebug($"\tDeleted rows[{deletedKeys.Count}] Added rows[{addedKeys.Count}] Modified rows[{modifiedKeys.Count}]");

		// Delete/Addition を並列フェッチ
		var deleteTask = AddSingleSideDiffsAsync(diff, srcConnection, tableName, pkColumns, compareColumns, deletedKeys, DiffType.Delete, isSource: true);
		var addTask = AddSingleSideDiffsAsync(diff, tgtConnection, tableName, pkColumns, compareColumns, addedKeys, DiffType.Addition, isSource: false);
		await Task.WhenAll(deleteTask, addTask);

		if (modifiedKeys.Count > 0)
		{
			// Modified 行を並列フェッチ
			var srcRowsTask = FetchRowsAsync(srcConnection, tableName, pkColumns, compareColumns, modifiedKeys);
			var tgtRowsTask = FetchRowsAsync(tgtConnection, tableName, pkColumns, compareColumns, modifiedKeys);
			await Task.WhenAll(srcRowsTask, tgtRowsTask);

			var sourceRows = srcRowsTask.Result.ToDictionary(r => GetPrimaryKeyString(r, pkColumns), StringComparer.Ordinal);
			var targetRows = tgtRowsTask.Result.ToDictionary(r => GetPrimaryKeyString(r, pkColumns), StringComparer.Ordinal);

			foreach (var key in modifiedKeys)
			{
				if (sourceRows.TryGetValue(key, out var srcRow) && targetRows.TryGetValue(key, out var tgtRow))
					diff.AddEntry(DiffType.Modify, pkColumns, srcRow, tgtRow, compareColumns);
			}
		}
		return diff;
	}

	/// <summary>
	/// IgnorePK モード: ModifiedKey で行をマッチし、PK を除いた値のハッシュで比較。
	/// グループ内余りは横断比較で直接 Modify / Delete / Addition を確定する。
	/// ハッシュ一致グループの行も AlternativeKey 照合用に保持し、
	/// 横断比較後に残った Addition/Delete を再照合して Modify を検出する。
	/// </summary>
	private async Task<MySqlVerifierDiff> CompareTableDataIgnorePkAsync(
		string tableName, DbConnectionHelper srcConnection, DbConnectionHelper tgtConnection,
		List<string> pkColumns, List<string> compareColumns,
		IncludeTableConfig tableConfig, MySqlVerifierMetadata metadata, List<string> alternativeKey,
		List<string>? originalDbPkColumns = null)
	{
		// force_pk 使用時: SetPkToIgnore には元のDB PKを使用（force_pk カラムは実際の値を保持）
		var ignorePkColumns = (originalDbPkColumns != null && originalDbPkColumns.Count > 0
			&& !pkColumns.SequenceEqual(originalDbPkColumns, StringComparer.OrdinalIgnoreCase))
			? originalDbPkColumns : pkColumns;
		// WriteDiffCsvAsync で使用するためにインスタンスフィールドに退避
		_ignorePkColumns = ignorePkColumns;
		_modifyOriginalPks = new Dictionary<int, Dictionary<string, (object? src, object? tgt)>>();
		var dateTimeKey = tableConfig.ModifiedKey;
		var allColumns = pkColumns.Concat(compareColumns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var diff = new MySqlVerifierDiff();
		diff.Init(allColumns, pkColumns.ToArray());

		var columnTypes = BuildColumnTypes(metadata);
		var hashSql = BuildGroupHashSqlIgnorePk(tableName, dateTimeKey, compareColumns, tableConfig, columnTypes);

		// 並列フェッチ: ソースとターゲットのグループハッシュを同時取得
		var srcHashTask = GetGroupHashesByDateTimeKeyAsync(srcConnection, hashSql);
		var tgtHashTask = GetGroupHashesByDateTimeKeyAsync(tgtConnection, hashSql);
		await Task.WhenAll(srcHashTask, tgtHashTask);
		var sourceHashes = srcHashTask.Result;
		var targetHashes = tgtHashTask.Result;
		Logger.LogDebug($"\tFetching group hashes from Source (IgnorePK): {sourceHashes.Count} datetime groups");
		Logger.LogDebug($"\tFetching group hashes from Target (IgnorePK): {targetHashes.Count} datetime groups");

		// FrozenSet で高速なキー判定
		var sourceKeySet = sourceHashes.Keys.ToFrozenSet(StringComparer.Ordinal);
		var targetKeySet = targetHashes.Keys.ToFrozenSet(StringComparer.Ordinal);

		var deletedKeys = new List<string>(sourceHashes.Count / 10);
		var addedKeys = new List<string>(targetHashes.Count / 10);
		var matchedKeys = new List<string>(Math.Min(sourceHashes.Count, targetHashes.Count));
		var modifiedKeys = new List<string>(Math.Min(sourceHashes.Count, targetHashes.Count) / 10);

		foreach (var k in sourceHashes.Keys)
		{
			if (!targetKeySet.Contains(k)) deletedKeys.Add(k);
			else if (sourceHashes[k] == targetHashes[k]) matchedKeys.Add(k);
			else modifiedKeys.Add(k);
		}
		foreach (var k in targetHashes.Keys)
		{
			if (!sourceKeySet.Contains(k)) addedKeys.Add(k);
		}
		Logger.LogDebug($"\tDeleted groups[{deletedKeys.Count}] Added groups[{addedKeys.Count}] Matched groups[{matchedKeys.Count}] Modified groups[{modifiedKeys.Count}]");

		var nonPkCompareColumns = compareColumns
			.Where(c => !pkColumns.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
		var fetchColumns = pkColumns.Concat(nonPkCompareColumns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		if (!fetchColumns.Contains(dateTimeKey, StringComparer.OrdinalIgnoreCase))
			fetchColumns.Add(dateTimeKey);

		// deleted/added/modified の行を並列フェッチ
		var srcDeletedTask = deletedKeys.Count > 0
			? FetchRowsByDateTimeKeyAsync(srcConnection, tableName, fetchColumns, dateTimeKey, deletedKeys)
			: Task.FromResult(new List<Dictionary<string, object?>>());
		var tgtAddedTask = addedKeys.Count > 0
			? FetchRowsByDateTimeKeyAsync(tgtConnection, tableName, fetchColumns, dateTimeKey, addedKeys)
			: Task.FromResult(new List<Dictionary<string, object?>>());
		await Task.WhenAll(srcDeletedTask, tgtAddedTask);
		var srcOnlyRows = srcDeletedTask.Result;
		var tgtOnlyRows = tgtAddedTask.Result;

		// グループ内比較（余りは srcOnlyRows / tgtOnlyRows に集約）
		if (modifiedKeys.Count > 0)
		{
			// modified グループのソース/ターゲット行を並列フェッチ
			var srcModTask = FetchRowsByDateTimeKeyAsync(srcConnection, tableName, fetchColumns, dateTimeKey, modifiedKeys);
			var tgtModTask = FetchRowsByDateTimeKeyAsync(tgtConnection, tableName, fetchColumns, dateTimeKey, modifiedKeys);
			await Task.WhenAll(srcModTask, tgtModTask);
			var srcGroups = GroupRowsByDateTimeKey(srcModTask.Result, dateTimeKey);
			var tgtGroups = GroupRowsByDateTimeKey(tgtModTask.Result, dateTimeKey);

			foreach (var key in modifiedKeys)
			{
				var srcGroup = srcGroups.GetValueOrDefault(key) ?? new List<Dictionary<string, object?>>();
				var tgtGroup = tgtGroups.GetValueOrDefault(key) ?? new List<Dictionary<string, object?>>();
								CompareRowSets(diff, pkColumns, nonPkCompareColumns, alternativeKey, srcGroup, tgtGroup,
							unmatchedSrcOut: srcOnlyRows, unmatchedTgtOut: tgtOnlyRows, ignorePkColumns: ignorePkColumns,
							modifyOriginalPks: _modifyOriginalPks);
			}
		}

		// 横断比較（余りを収集する形式で実行）
		var finalSrcOnly = new List<Dictionary<string, object?>>();
		var finalTgtOnly = new List<Dictionary<string, object?>>();
		if (srcOnlyRows.Count > 0 || tgtOnlyRows.Count > 0)
			CompareRowSets(diff, pkColumns, nonPkCompareColumns, alternativeKey, srcOnlyRows, tgtOnlyRows,
				unmatchedSrcOut: finalSrcOnly, unmatchedTgtOut: finalTgtOnly, ignorePkColumns: ignorePkColumns,
				modifyOriginalPks: _modifyOriginalPks);

		// ── ハッシュ一致グループの行を使った再照合 ──
		// 横断比較後に残った余り行は、ハッシュ一致で完全スキップされた
		// グループ内の行と AlternativeKey が一致する可能性がある。
		// その場合、ハッシュ一致グループの行を「代理ソース/ターゲット」として
		// Modify ペアを生成する。残りは Delete/Addition として報告する。
		if ((finalTgtOnly.Count > 0 || finalSrcOnly.Count > 0) && matchedKeys.Count > 0)
		{
			// matched グループの行を並列フェッチ
			var matchedSrcTask = finalTgtOnly.Count > 0
				? FetchRowsByDateTimeKeyAsync(srcConnection, tableName, fetchColumns, dateTimeKey, matchedKeys)
				: Task.FromResult(new List<Dictionary<string, object?>>());
			var matchedTgtTask = finalSrcOnly.Count > 0
				? FetchRowsByDateTimeKeyAsync(tgtConnection, tableName, fetchColumns, dateTimeKey, matchedKeys)
				: Task.FromResult(new List<Dictionary<string, object?>>());
			await Task.WhenAll(matchedSrcTask, matchedTgtTask);
			var matchedSrcRows = matchedSrcTask.Result;
			var matchedTgtRows = matchedTgtTask.Result;

			string AltKey(Dictionary<string, object?> row)
			{
				var sb = new StringBuilder(64);
				for (int i = 0; i < alternativeKey.Count; i++)
				{
					if (i > 0) sb.Append('|');
					sb.Append(FormatColumnValue(row.GetValueOrDefault(alternativeKey[i])));
				}
				return sb.ToString();
			}

			// Addition 候補の再照合: ハッシュ一致グループのソース行と AlternativeKey マッチ → Modify
			if (finalTgtOnly.Count > 0 && matchedSrcRows.Count > 0)
			{
				var matchedSrcByAltKey = new Dictionary<string, List<Dictionary<string, object?>>>(matchedSrcRows.Count, StringComparer.Ordinal);
				foreach (var row in matchedSrcRows)
					AddToList(matchedSrcByAltKey, AltKey(row), row);

				var resolved = new HashSet<int>();
				for (int i = 0; i < finalTgtOnly.Count; i++)
				{
					var altKey = AltKey(finalTgtOnly[i]);
					if (matchedSrcByAltKey.TryGetValue(altKey, out var srcCandidates) && srcCandidates.Count > 0)
					{
									var srcRow = srcCandidates[0];
						srcCandidates.RemoveAt(0);
								var origPks1 = CaptureOriginalPks(srcRow, finalTgtOnly[i], ignorePkColumns);
								diff.AddEntry(DiffType.Modify, pkColumns, srcRow, finalTgtOnly[i], nonPkCompareColumns);
								_modifyOriginalPks[diff.Entries.Count - 1] = origPks1;
						resolved.Add(i);
					}
				}
				// 残りを Addition として報告
				for (int i = 0; i < finalTgtOnly.Count; i++)
				{
					if (!resolved.Contains(i))
						diff.AddEntry(DiffType.Addition, pkColumns, null,
							SetPkToIgnore(finalTgtOnly[i], ignorePkColumns), nonPkCompareColumns);
				}
			}
			else
			{
				// ハッシュ一致グループなし or Addition 候補なし → そのまま Addition
				foreach (var row in finalTgtOnly)
					diff.AddEntry(DiffType.Addition, pkColumns, null,
						SetPkToIgnore(row, ignorePkColumns), nonPkCompareColumns);
			}

			// Delete 候補の再照合: ハッシュ一致グループのターゲット行と AlternativeKey マッチ → Modify
			if (finalSrcOnly.Count > 0 && matchedTgtRows.Count > 0)
			{
					var matchedTgtByAltKey = new Dictionary<string, List<Dictionary<string, object?>>>(matchedTgtRows.Count, StringComparer.Ordinal);
					foreach (var row in matchedTgtRows)
						AddToList(matchedTgtByAltKey, AltKey(row), row);

				var resolved = new HashSet<int>();
				for (int i = 0; i < finalSrcOnly.Count; i++)
				{
					var altKey = AltKey(finalSrcOnly[i]);
					if (matchedTgtByAltKey.TryGetValue(altKey, out var tgtCandidates) && tgtCandidates.Count > 0)
					{
									var tgtRow = tgtCandidates[0];
						tgtCandidates.RemoveAt(0);
								var origPks2 = CaptureOriginalPks(finalSrcOnly[i], tgtRow, ignorePkColumns);
							diff.AddEntry(DiffType.Modify, pkColumns, finalSrcOnly[i], tgtRow, nonPkCompareColumns);
							_modifyOriginalPks[diff.Entries.Count - 1] = origPks2;
						resolved.Add(i);
					}
				}
				// 残りを Delete として報告
				for (int i = 0; i < finalSrcOnly.Count; i++)
				{
					if (!resolved.Contains(i))
						diff.AddEntry(DiffType.Delete, pkColumns,
							SetPkToIgnore(finalSrcOnly[i], ignorePkColumns), null, nonPkCompareColumns);
				}
			}
			else
			{
				// ハッシュ一致グループなし or Delete 候補なし → そのまま Delete
				foreach (var row in finalSrcOnly)
					diff.AddEntry(DiffType.Delete, pkColumns,
						SetPkToIgnore(row, ignorePkColumns), null, nonPkCompareColumns);
			}
		}
		else
		{
			// ハッシュ一致グループがない場合、余りをそのまま報告
			foreach (var row in finalTgtOnly)
				diff.AddEntry(DiffType.Addition, pkColumns, null,
					SetPkToIgnore(row, ignorePkColumns), nonPkCompareColumns);
			foreach (var row in finalSrcOnly)
				diff.AddEntry(DiffType.Delete, pkColumns,
					SetPkToIgnore(row, ignorePkColumns), null, nonPkCompareColumns);
		}

		return diff;
	}

	/// <summary>行セットを比較: 完全一致消し込み → AlternativeKeyマッチ(Modify) → 余り(Delete/Addition)</summary>
	private static void CompareRowSets(
		MySqlVerifierDiff diff, List<string> pkColumns, List<string> nonPkCompareColumns,
		List<string> alternativeKey,
		List<Dictionary<string, object?>> srcGroup, List<Dictionary<string, object?>> tgtGroup,
		List<Dictionary<string, object?>>? unmatchedSrcOut = null,
		List<Dictionary<string, object?>>? unmatchedTgtOut = null,
		List<string>? ignorePkColumns = null,
		Dictionary<int, Dictionary<string, (object? src, object? tgt)>>? modifyOriginalPks = null)
	{
		// ignorePkColumns が指定されていない場合は pkColumns を使用（従来動作）
		var setPkIgnoreCols = ignorePkColumns ?? pkColumns;
		// StringBuilder ベースのキー生成（LINQ + string.Join の GC 圧を回避）
		var keySb = new StringBuilder(256);

		string RowContentKey(Dictionary<string, object?> row)
		{
			keySb.Clear();
			for (int i = 0; i < nonPkCompareColumns.Count; i++)
			{
				if (i > 0) keySb.Append('|');
				keySb.Append(FormatColumnValue(row.GetValueOrDefault(nonPkCompareColumns[i])));
			}
			return keySb.ToString();
		}

		string AltKey(Dictionary<string, object?> row)
		{
			keySb.Clear();
			for (int i = 0; i < alternativeKey.Count; i++)
			{
				if (i > 0) keySb.Append('|');
				keySb.Append(FormatColumnValue(row.GetValueOrDefault(alternativeKey[i])));
			}
			return keySb.ToString();
		}

		// Step 1: 完全一致行を消し込み（容量を事前推定）
		var srcBag = new Dictionary<string, List<Dictionary<string, object?>>>(srcGroup.Count, StringComparer.Ordinal);
		foreach (var row in srcGroup) AddToList(srcBag, RowContentKey(row), row);

		var tgtBag = new Dictionary<string, List<Dictionary<string, object?>>>(tgtGroup.Count, StringComparer.Ordinal);
		foreach (var row in tgtGroup) AddToList(tgtBag, RowContentKey(row), row);

		var unmatchedSrc = new List<Dictionary<string, object?>>();
		var unmatchedTgt = new List<Dictionary<string, object?>>();

		// srcBag のキーを走査し、tgtBag とマッチングを行う（Union の代わりに直接走査）
		foreach (var kvp in srcBag)
		{
			var srcRows = kvp.Value;
			if (tgtBag.TryGetValue(kvp.Key, out var tgtRows))
			{
				var matchCount = Math.Min(srcRows.Count, tgtRows.Count);
				for (int i = matchCount; i < srcRows.Count; i++) unmatchedSrc.Add(srcRows[i]);
				for (int i = matchCount; i < tgtRows.Count; i++) unmatchedTgt.Add(tgtRows[i]);
			}
			else
			{
				unmatchedSrc.AddRange(srcRows);
			}
		}
		// tgtBag にのみ存在するキーの行を追加
		foreach (var kvp in tgtBag)
		{
			if (!srcBag.ContainsKey(kvp.Key))
				unmatchedTgt.AddRange(kvp.Value);
		}

		// Step 2: AlternativeKey マッチ → Modify（容量を事前推定）
		var srcByAltKey = new Dictionary<string, List<int>>(unmatchedSrc.Count, StringComparer.Ordinal);
		for (int i = 0; i < unmatchedSrc.Count; i++)
			AddToList(srcByAltKey, AltKey(unmatchedSrc[i]), i);

		var tgtByAltKey = new Dictionary<string, List<int>>(unmatchedTgt.Count, StringComparer.Ordinal);
		for (int i = 0; i < unmatchedTgt.Count; i++)
			AddToList(tgtByAltKey, AltKey(unmatchedTgt[i]), i);

		var pairedSrc = new HashSet<int>(unmatchedSrc.Count);
		var pairedTgt = new HashSet<int>(unmatchedTgt.Count);

		// Dictionary の直接走査で Intersect を回避
		foreach (var kvp in srcByAltKey)
		{
			if (!tgtByAltKey.TryGetValue(kvp.Key, out var tgtIndices)) continue;
			var srcIndices = kvp.Value;
			var pairCount = Math.Min(srcIndices.Count, tgtIndices.Count);
			for (int i = 0; i < pairCount; i++)
			{
					pairedSrc.Add(srcIndices[i]);
				pairedTgt.Add(tgtIndices[i]);
				var origPks = modifyOriginalPks != null
					? CaptureOriginalPks(unmatchedSrc[srcIndices[i]], unmatchedTgt[tgtIndices[i]], setPkIgnoreCols)
					: null;
				diff.AddEntry(DiffType.Modify, pkColumns, unmatchedSrc[srcIndices[i]], unmatchedTgt[tgtIndices[i]], nonPkCompareColumns);
				if (origPks != null)
					modifyOriginalPks![diff.Entries.Count - 1] = origPks;
			}
		}

		// Step 3: 余りを集約 or 直接報告
		ReportOrCollectUnmatched(unmatchedSrc, pairedSrc, unmatchedSrcOut, row =>
			diff.AddEntry(DiffType.Delete, pkColumns, SetPkToIgnore(row, setPkIgnoreCols), null, nonPkCompareColumns));
		ReportOrCollectUnmatched(unmatchedTgt, pairedTgt, unmatchedTgtOut, row =>
			diff.AddEntry(DiffType.Addition, pkColumns, null, SetPkToIgnore(row, setPkIgnoreCols), nonPkCompareColumns));
	}


	private static Dictionary<string, List<Dictionary<string, object?>>> GroupRowsByDateTimeKey(
		List<Dictionary<string, object?>> rows, string dateTimeKey)
	{
		var groups = new Dictionary<string, List<Dictionary<string, object?>>>(rows.Count / 4 + 1, StringComparer.Ordinal);
		foreach (var row in rows)
			AddToList(groups, FormatColumnValue(row.GetValueOrDefault(dateTimeKey)), row);
		return groups;
	}

	private static Dictionary<string, object?> SetPkToIgnore(Dictionary<string, object?> row, List<string> pkColumns)
	{
		var newRow = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);
		foreach (var pk in pkColumns) newRow[pk] = "Ignore";
		return newRow;
	}

	/// <summary>
	/// AddEntry 呼び出し前に PK 値をキャプチャする。
	/// AddEntry が元の行辞書を変更する可能性があるため、先に値を取得する。
	/// </summary>
	private static Dictionary<string, (object? src, object? tgt)> CaptureOriginalPks(
		Dictionary<string, object?> srcRow, Dictionary<string, object?> tgtRow,
		List<string> pkColumns)
	{
		var pkVals = new Dictionary<string, (object? src, object? tgt)>(pkColumns.Count, StringComparer.OrdinalIgnoreCase);
		foreach (var pk in pkColumns)
			pkVals[pk] = (srcRow.GetValueOrDefault(pk), tgtRow.GetValueOrDefault(pk));
		return pkVals;
	}

	private string BuildGroupHashSqlIgnorePk(
		string tableName, string dateTimeKey, List<string> compareColumns,
		IncludeTableConfig tableConfig, Dictionary<string, string> columnTypes)
	{
		var (prefix, fromClause, joinClause, whereClause) = BuildFromJoinWhere(tableName, tableConfig);
		var dateTimeKeyExpr = $"DATE_FORMAT({prefix}`{dateTimeKey}`, '%Y-%m-%d %H:%i:%s')";
		var concatExpr = $"CONCAT_WS('|', {string.Join(", ", compareColumns.Select(c => FormatHashColumn(c, prefix, columnTypes)))})";

		return $@"
            SELECT datetime_key, CRC32(GROUP_CONCAT(row_hash ORDER BY row_hash SEPARATOR ',')) AS group_hash
            FROM (
                SELECT {dateTimeKeyExpr} AS datetime_key, CRC32({concatExpr}) AS row_hash
                FROM {fromClause}{joinClause}{whereClause}
            ) sub
            GROUP BY datetime_key
            ORDER BY datetime_key";
	}

	private async Task<Dictionary<string, long>> GetGroupHashesByDateTimeKeyAsync(DbConnectionHelper dbConnection, string sql)
	{
		var hashes = new Dictionary<string, long>();
		using var cmd = dbConnection.CreateCommand();
		cmd.CommandTimeout = 600;
		using var reader = await cmd.ExecuteReaderAsync(sql);
		while (await reader.ReadAsync())
		{
			var dtOrdinal = reader.GetOrdinal("datetime_key");
			var key = reader.IsDBNull(dtOrdinal) ? "" : reader["datetime_key"]?.ToString() ?? "";
			var hashOrdinal = reader.GetOrdinal("group_hash");
			hashes[key] = reader.IsDBNull(hashOrdinal) ? 0L : Convert.ToInt64(reader[hashOrdinal]);
		}
		return hashes;
	}

	private async Task<List<Dictionary<string, object?>>> FetchRowsByDateTimeKeyAsync(
		DbConnectionHelper dbConnection, string tableName, List<string> selectColumns,
		string dateTimeKey, List<string> dateTimeKeys)
	{
		var rows = new List<Dictionary<string, object?>>(dateTimeKeys.Count * 4); // 容量を事前推定
		if (dateTimeKeys.Count == 0) return rows;
		var selectCols = string.Join(", ", selectColumns.Select(c => $"`{c}`"));

		const int batchSize = 1000;
		for (int i = 0; i < dateTimeKeys.Count; i += batchSize)
		{
			var batch = dateTimeKeys.Skip(i).Take(batchSize).ToList();
			var inValues = string.Join(", ", batch.Select(k => $"'{k.Replace("'", "''")}'"));
			var sql = $"SELECT {selectCols} FROM `{tableName}` WHERE DATE_FORMAT(`{dateTimeKey}`, '%Y-%m-%d %H:%i:%s') IN ({inValues})";

			using var cmd = dbConnection.CreateCommand();
			cmd.CommandTimeout = 600;
			using var reader = await cmd.ExecuteReaderAsync(sql);
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
		return rows;
	}

	private async Task AddSingleSideDiffsAsync(
		MySqlVerifierDiff diff, DbConnectionHelper connection, string tableName,
		List<string> pkColumns, List<string> compareColumns, List<string> keys,
		DiffType diffType, bool isSource)
	{
		if (keys.Count == 0) return;
		var rows = await FetchRowsAsync(connection, tableName, pkColumns, compareColumns, keys);
		foreach (var row in rows)
			diff.AddEntry(diffType, pkColumns, isSource ? row : null, isSource ? null : row, compareColumns);
	}

	private async Task<MySqlVerifierDiff> CompareTableDataNoPkAsync(
		string tableName, DbConnectionHelper srcConnection, DbConnectionHelper tgtConnection,
		List<string> allColumns, IncludeTableConfig? tableConfig, MySqlVerifierMetadata metadata)
	{
		var diff = new MySqlVerifierDiff();
		diff.Init(allColumns, null);

		var (prefix, fromClause, joinClause, whereClause) = BuildFromJoinWhere(tableName, tableConfig);
		var selectCols = string.Join(", ", allColumns.Select(c => $"{prefix}`{c}`"));
		var orderBy = string.Join(", ", allColumns.Select(c => $"{prefix}`{c}`"));
		var fetchSql = $"SELECT {selectCols} FROM {fromClause}{joinClause}{whereClause} ORDER BY {orderBy}";

		// 並列フェッチ: ソースとターゲットの全行を同時取得
		Logger.LogDebug($"\tFetching all rows from Source and Target...");
		var srcFetchTask = FetchAllRowsAsync(srcConnection, fetchSql);
		var tgtFetchTask = FetchAllRowsAsync(tgtConnection, fetchSql);
		await Task.WhenAll(srcFetchTask, tgtFetchTask);
		var sourceRows = srcFetchTask.Result;
		var targetRows = tgtFetchTask.Result;
		Logger.LogDebug($"\tFetched {sourceRows.Count} rows from Source, {targetRows.Count} rows from Target");

		var sourceMultiset = BuildRowMultiset(sourceRows, allColumns);
		var targetMultiset = BuildRowMultiset(targetRows, allColumns);

		var emptyPkList = new List<string>();
		// 直接走査で Union を回避
		foreach (var kvp in sourceMultiset)
		{
			var srcList = kvp.Value;
			if (targetMultiset.TryGetValue(kvp.Key, out var tgtList))
			{
				for (int i = tgtList.Count; i < srcList.Count; i++)
					diff.AddEntry(DiffType.Delete, emptyPkList, srcList[i], null, allColumns);
				for (int i = srcList.Count; i < tgtList.Count; i++)
					diff.AddEntry(DiffType.Addition, emptyPkList, null, tgtList[i], allColumns);
			}
			else
			{
				foreach (var row in srcList)
					diff.AddEntry(DiffType.Delete, emptyPkList, row, null, allColumns);
			}
		}
		foreach (var kvp in targetMultiset)
		{
			if (!sourceMultiset.ContainsKey(kvp.Key))
				foreach (var row in kvp.Value)
					diff.AddEntry(DiffType.Addition, emptyPkList, null, row, allColumns);
		}

		var delCount = diff.Entries.Count(e => e.DiffType == DiffType.Delete);
		var addCount = diff.Entries.Count(e => e.DiffType == DiffType.Addition);
		Logger.LogDebug($"\tDeleted rows[{delCount}] Added rows[{addCount}]");
		return diff;
	}

	private async Task<List<Dictionary<string, object?>>> FetchAllRowsAsync(DbConnectionHelper dbConnection, string sql)
	{
		using var cmd = dbConnection.CreateCommand();
		cmd.CommandTimeout = 600;
		using var reader = await cmd.ExecuteReaderAsync(sql);
		var rows = new List<Dictionary<string, object?>>();
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
		return rows;
	}

	private static Dictionary<string, List<Dictionary<string, object?>>> BuildRowMultiset(
		List<Dictionary<string, object?>> rows, List<string> allColumns)
	{
		var multiset = new Dictionary<string, List<Dictionary<string, object?>>>(rows.Count, StringComparer.Ordinal);
		var sb = new StringBuilder(256);
		foreach (var row in rows)
		{
			sb.Clear();
			for (int i = 0; i < allColumns.Count; i++)
			{
				if (i > 0) sb.Append("|");
				sb.Append(RowDiff.FormatCsvValue(row.GetValueOrDefault(allColumns[i])));
			}
			AddToList(multiset, sb.ToString(), row);
		}
		return multiset;
	}

	private string BuildRowHashSql(
		string tableName, List<string> pkColumns, List<string> compareColumns,
		IncludeTableConfig? tableConfig, Dictionary<string, string> columnTypes)
	{
		var (prefix, fromClause, joinClause, whereClause) = BuildFromJoinWhere(tableName, tableConfig);
		var pkSelect = string.Join(", ", pkColumns.Select(c => $"{prefix}`{c}`"));
		var concatExpr = $"CONCAT_WS('|', {string.Join(", ", compareColumns.Select(c => FormatHashColumn(c, prefix, columnTypes)))})";

		return $@"
            SELECT {pkSelect}, CRC32({concatExpr}) AS row_hash
            FROM {fromClause}{joinClause}{whereClause}
            ORDER BY {pkSelect}";
	}

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

	private static string BuildReferenceJoinClause(ReferenceTableConfig refTable, string tableAlias)
	{
		var subWhere = refTable.ExcludeConditions.Length > 0
			? " WHERE " + BuildExcludeWhereClause(refTable.ExcludeConditions)
			: "";

		return $@"INNER JOIN (
                SELECT DISTINCT `{refTable.JoinColumn}` FROM `{refTable.Name}`{subWhere}
            ) _ref ON {tableAlias}.`{refTable.JoinColumn}` = _ref.`{refTable.JoinColumn}`";
	}

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

	private static string FormatSqlValue(object? value)
	{
		if (value == null) return "NULL";
		if (value is string s) return $"'{s.Replace("'", "''")}'";
		return value.ToString() ?? "NULL";
	}


	private async Task<Dictionary<string, uint>> GetRowHashesAsync(
		DbConnectionHelper dbConnection, string sql, List<string> pkColumns)
	{
		var hashes = new Dictionary<string, uint>(StringComparer.Ordinal);
		var sb = new StringBuilder(128);
		using var cmd = dbConnection.CreateCommand();
		cmd.CommandTimeout = 600;
		using var reader = await cmd.ExecuteReaderAsync(sql);
		while (await reader.ReadAsync())
		{
			sb.Clear();
			for (int i = 0; i < pkColumns.Count; i++)
			{
				if (i > 0) sb.Append("|");
				var col = pkColumns[i];
				var ordinal = reader.GetOrdinal(col);
				sb.Append(col).Append('=');
				if (!reader.IsDBNull(ordinal))
					sb.Append(reader[col]?.ToString() ?? "");
			}
			var hashOrdinal = reader.GetOrdinal("row_hash");
			hashes[sb.ToString()] = reader.IsDBNull(hashOrdinal) ? 0u : Convert.ToUInt32(reader[hashOrdinal]);
		}
		return hashes;
	}

	private async Task<List<Dictionary<string, object?>>> FetchRowsAsync(
		DbConnectionHelper dbConnection, string tableName,
		List<string> pkColumns, List<string> dataColumns, List<string> pkKeys)
	{
		var rows = new List<Dictionary<string, object?>>(pkKeys.Count); // 容量を事前推定
		if (pkKeys.Count == 0) return rows;

		var allColumns = pkColumns.Concat(dataColumns).Distinct().ToList();
		var selectCols = string.Join(", ", allColumns.Select(c => $"`{c}`"));

		const int batchSize = 1000;
		for (int i = 0; i < pkKeys.Count; i += batchSize)
		{
			var batch = pkKeys.Skip(i).Take(batchSize).ToList();
			string whereClause;
			if (pkColumns.Count == 1)
			{
				var inValues = string.Join(", ", batch.Select(k =>
					$"'{k.Split('=', 2).Last().Replace("'", "''")}'"));
				whereClause = $"`{pkColumns[0]}` IN ({inValues})";
			}
			else
			{
				whereClause = string.Join(" OR ", batch.Select(k =>
				{
					var parts = k.Split("|");
					var conds = parts.Select((p, idx) =>
						$"`{pkColumns[idx]}` = '{p.Split('=', 2).Last().Replace("'", "''")}'");
					return $"({string.Join(" AND ", conds)})";
				}));
			}

			var sql = $"SELECT {selectCols} FROM `{tableName}` WHERE {whereClause}";
			using var cmd = dbConnection.CreateCommand();
			cmd.CommandTimeout = 600;
			using var reader = await cmd.ExecuteReaderAsync(sql);
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
		return rows;
	}

	private static string GetPrimaryKeyString(Dictionary<string, object?> row, List<string> pkColumns)
	{
		var sb = new StringBuilder(64);
		for (int i = 0; i < pkColumns.Count; i++)
		{
			if (i > 0) sb.Append('|');
			sb.Append(pkColumns[i]).Append('=').Append(row.GetValueOrDefault(pkColumns[i])?.ToString() ?? "");
		}
		return sb.ToString();
	}

	/// <summary>
	/// diff.Entries から直接 CSV 行を生成する。
	/// BuildCsvLine() を経由せず、_modifyOriginalPks に退避した元の PK 値を使って
	/// 条件付き Ignore を WriteDiffCsvAsync 側で適用する。
	/// これにより、実 HSCTOOL の AddEntry が PK 値を "Ignore" に上書きしても正しく動作する。
	///
	/// 出力フォーマット（実 RowDiff.BuildCsvLine() と同一）:
	///   Modify  + ChangedColumn → "oldVal => newVal"
	///   Modify  + FixedColumn(PK) → 条件付き Ignore（src==tgt なら実値、異なれば "Ignore"）
	///   Modify  + 未変更カラム → ""
	///   Delete  → ソース値（IgnorePK の PK カラムは "Ignore"）
	///   Addition → ターゲット値（IgnorePK の PK カラムは "Ignore"）
	/// </summary>
	private async Task WriteDiffCsvAsync(
		string tableName,
		MySqlVerifierDiff diff,
		List<string> allColumns,
		List<string> pkColumns,
		bool ignorePk = false)
	{
		if (diff.DiffCount == 0) return;

		var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
		var pkSuffix = ignorePk ? "_IgnorePK"
			: pkColumns.Count > 0 ? $"_PK({string.Join(",", pkColumns)})" : "_NoPK";
		var baseFileName = $"{tableName}_diff_{timestamp}{pkSuffix}";

		// 固定カラム（PK）セット
		var fixedColumnSet = new HashSet<string>(pkColumns, StringComparer.OrdinalIgnoreCase);
		// IgnorePK モードで "Ignore" 対象にする PK カラムセット
		var ignorePkSet = ignorePk
			? new HashSet<string>(_ignorePkColumns, StringComparer.OrdinalIgnoreCase)
			: new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		// ヘッダー行: "DiffType","col1","col2",...
		var headerLine = string.Join(",", new[] { "DiffType" }.Concat(allColumns));
		var headerBytes = Encoding.UTF8.GetByteCount(headerLine + Environment.NewLine);

			// diff.Entries から直接 CSV 行を生成
			// Modify 行の PK カラムは _modifyOriginalPks に退避した元の値で条件付き Ignore を適用
			var lines = new List<string>();
			for (int entryIdx = 0; entryIdx < diff.Entries.Count; entryIdx++)
			{
				var entry = diff.Entries[entryIdx];
				var cells = allColumns.Select(col =>
				{
					if (entry.DiffType == DiffType.Modify)
					{
						// IgnorePK モード: PK カラムは _modifyOriginalPks から元の値を取得して比較
						// （AddEntry が PK 値を "Ignore" に上書きするため、entry.SourceValues/TargetValues は使えない）
						// ソース PK == ターゲット PK → 実値を表示、異なる → "Ignore"
						if (ignorePk && ignorePkSet.Contains(col)
							&& _modifyOriginalPks.TryGetValue(entryIdx, out var origPks)
							&& origPks.TryGetValue(col, out var pkPair))
						{
							var srcVal = FormatColumnValue(pkPair.src);
							var tgtVal = FormatColumnValue(pkPair.tgt);
							return srcVal == tgtVal
								? RowDiff.FormatCsvValue(pkPair.src)
								: "Ignore";
						}
						if (entry.ChangedColumns.Contains(col))
							return $"{RowDiff.FormatCsvValue(entry.SourceValues.GetValueOrDefault(col))} => {RowDiff.FormatCsvValue(entry.TargetValues.GetValueOrDefault(col))}";
						if (fixedColumnSet.Contains(col))
							return RowDiff.FormatCsvValue(entry.SourceValues.GetValueOrDefault(col));
						return "";
					}
				if (entry.DiffType == DiffType.Delete)
				{
					// IgnorePK の PK カラムは常に "Ignore"
					if (ignorePk && ignorePkSet.Contains(col))
						return "Ignore";
					return RowDiff.FormatCsvValue(entry.SourceValues.GetValueOrDefault(col));
				}
				if (entry.DiffType == DiffType.Addition)
				{
					// IgnorePK の PK カラムは常に "Ignore"
					if (ignorePk && ignorePkSet.Contains(col))
						return "Ignore";
					return RowDiff.FormatCsvValue(entry.TargetValues.GetValueOrDefault(col));
				}
				return "";
			});

			lines.Add(string.Join(",",
				new[] { entry.DiffType.ToString() }.Concat(cells)
					.Select(v => $"\"{(v ?? "").Replace("\"", "\"\"")}\"")));
		}

		if (lines.Count == 0) return;

		var fileIndex = 1;
		var currentSize = 0L;
		StreamWriter? writer = null;
		var currentFilePath = string.Empty;

		try
		{
			for (int i = 0; i < lines.Count; i++)
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
		var sb = new StringBuilder();
		sb.AppendLine("=== Schema Comparison Result ===");
		if (result.Success)
		{
			sb.AppendLine("Comparison completed successfully.");
			if (result.MissingInTarget.Count > 0)
				sb.AppendLine($"Tables missing in target: {string.Join(", ", result.MissingInTarget)}");
			if (result.MissingInSource.Count > 0)
				sb.AppendLine($"Tables missing in source: {string.Join(", ", result.MissingInSource)}");
			if (result.PrimaryKeyMismatch.Count > 0)
				sb.AppendLine($"Primary key mismatch: {string.Join(", ", result.PrimaryKeyMismatch)}");
			if (result.NoPrimaryKey.Count > 0)
				sb.AppendLine($"No primary key (skipped): {string.Join(", ", result.NoPrimaryKey)}");
			if (result.TableDiffs.Count > 0)
			{
				sb.AppendLine("Tables with differences:");
				foreach (var (table, count) in result.TableDiffs)
					sb.AppendLine($"  {table}: {count} rows");
			}
			else sb.AppendLine("No data differences found.");
		}
		else sb.AppendLine($"Comparison failed: {result.ErrorMessage}");

		Logger.NewLine();
		Logger.LogInfo(sb.ToString());
	}
}

