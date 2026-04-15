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
			list = new List<TVal>();
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

				// IgnorePrimaryKey モード判定
				var ignorePk = tableConfig?.IgnorePrimaryKey == true;
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
					// IgnorePrimaryKey モード: ModifiedKey で行をマッチし、PK以外のカラムのハッシュで比較
					Logger.LogInfo($"  Table '{tableName}' using IgnorePrimaryKey mode (DateTimeKey='{tableConfig!.ModifiedKey}')");
					diff = await CompareTableDataIgnorePkAsync(
						tableName, srcConnection, tgtConnection, pkColumns, compareColumns, tableConfig, sourceMetadata, tableConfig!.AlternativeKey);
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

		var sourceKeys = await GetPkSetAsync(srcConnection, sql, pkColumns);
		Logger.LogDebug($"\tFetching PK set from Source: {sourceKeys.Count} rows");
		var targetKeys = await GetPkSetAsync(tgtConnection, sql, pkColumns);
		Logger.LogDebug($"\tFetching PK set from Target: {targetKeys.Count} rows");

		var deletedKeys = sourceKeys.Except(targetKeys).ToList();
		var addedKeys = targetKeys.Except(sourceKeys).ToList();
		Logger.LogDebug($"\tDeleted rows[{deletedKeys.Count}] Added rows[{addedKeys.Count}] Modified rows[0]");

		var emptyCompareColumns = new List<string>();
		await AddSingleSideDiffsAsync(diff, srcConnection, tableName, pkColumns, emptyCompareColumns, deletedKeys, DiffType.Delete, isSource: true);
		await AddSingleSideDiffsAsync(diff, tgtConnection, tableName, pkColumns, emptyCompareColumns, addedKeys, DiffType.Addition, isSource: false);
		return diff;
	}

	private async Task<HashSet<string>> GetPkSetAsync(DbConnectionHelper dbConnection, string sql, List<string> pkColumns)
	{
		var keys = new HashSet<string>();
		using var cmd = dbConnection.CreateCommand();
		cmd.CommandTimeout = 600;
		using var reader = await cmd.ExecuteReaderAsync(sql);
		while (await reader.ReadAsync())
		{
			var pkParts = pkColumns.Select(col =>
			{
				var val = reader.IsDBNull(reader.GetOrdinal(col)) ? "" : reader[col]?.ToString() ?? "";
				return $"{col}={val}";
			});
			keys.Add(string.Join("||", pkParts));
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

		var sourceHashes = await GetRowHashesAsync(srcConnection, hashSql, pkColumns);
		Logger.LogDebug($"\tFetching row hashes from Source: {sourceHashes.Count} rows");
		var targetHashes = await GetRowHashesAsync(tgtConnection, hashSql, pkColumns);
		Logger.LogDebug($"\tFetching row hashes from Target: {targetHashes.Count} rows");

		var sourceKeys = sourceHashes.Keys.ToHashSet();
		var targetKeys = targetHashes.Keys.ToHashSet();
		var deletedKeys = sourceKeys.Except(targetKeys).ToList();
		var addedKeys = targetKeys.Except(sourceKeys).ToList();
		var modifiedKeys = sourceKeys.Intersect(targetKeys).Where(k => sourceHashes[k] != targetHashes[k]).ToList();
		Logger.LogDebug($"\tDeleted rows[{deletedKeys.Count}] Added rows[{addedKeys.Count}] Modified rows[{modifiedKeys.Count}]");

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
	/// IgnorePrimaryKey モード: ModifiedKey で行をマッチし、PK を除いた値のハッシュで比較。
	/// グループ内余りは横断比較で直接 Modify / Delete / Addition を確定する。
	/// ハッシュ一致グループの行も AlternativeKey 照合用に保持し、
	/// 横断比較後に残った Addition/Delete を再照合して Modify を検出する。
	/// </summary>
	private async Task<MySqlVerifierDiff> CompareTableDataIgnorePkAsync(
		string tableName, DbConnectionHelper srcConnection, DbConnectionHelper tgtConnection,
		List<string> pkColumns, List<string> compareColumns,
		IncludeTableConfig tableConfig, MySqlVerifierMetadata metadata, List<string> alternativeKey)
	{
		var dateTimeKey = tableConfig.ModifiedKey;
		var allColumns = pkColumns.Concat(compareColumns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var diff = new MySqlVerifierDiff();
		diff.Init(allColumns, pkColumns.ToArray());

		var columnTypes = BuildColumnTypes(metadata);
		var hashSql = BuildGroupHashSqlIgnorePk(tableName, dateTimeKey, compareColumns, tableConfig, columnTypes);

		var sourceHashes = await GetGroupHashesByDateTimeKeyAsync(srcConnection, hashSql);
		Logger.LogDebug($"\tFetching group hashes from Source (IgnorePK): {sourceHashes.Count} datetime groups");
		var targetHashes = await GetGroupHashesByDateTimeKeyAsync(tgtConnection, hashSql);
		Logger.LogDebug($"\tFetching group hashes from Target (IgnorePK): {targetHashes.Count} datetime groups");

		var sourceKeys = sourceHashes.Keys.ToHashSet();
		var targetKeys = targetHashes.Keys.ToHashSet();
		var deletedKeys = sourceKeys.Except(targetKeys).ToList();
		var addedKeys = targetKeys.Except(sourceKeys).ToList();
		var matchedKeys = sourceKeys.Intersect(targetKeys).Where(k => sourceHashes[k] == targetHashes[k]).ToList();
		var modifiedKeys = sourceKeys.Intersect(targetKeys).Where(k => sourceHashes[k] != targetHashes[k]).ToList();
		Logger.LogDebug($"\tDeleted groups[{deletedKeys.Count}] Added groups[{addedKeys.Count}] Matched groups[{matchedKeys.Count}] Modified groups[{modifiedKeys.Count}]");

		var nonPkCompareColumns = compareColumns
			.Where(c => !pkColumns.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
		var fetchColumns = pkColumns.Concat(nonPkCompareColumns).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		if (!fetchColumns.Contains(dateTimeKey, StringComparer.OrdinalIgnoreCase))
			fetchColumns.Add(dateTimeKey);

		var srcOnlyRows = deletedKeys.Count > 0
			? await FetchRowsByDateTimeKeyAsync(srcConnection, tableName, fetchColumns, dateTimeKey, deletedKeys)
			: new List<Dictionary<string, object?>>();
		var tgtOnlyRows = addedKeys.Count > 0
			? await FetchRowsByDateTimeKeyAsync(tgtConnection, tableName, fetchColumns, dateTimeKey, addedKeys)
			: new List<Dictionary<string, object?>>();

		// グループ内比較（余りは srcOnlyRows / tgtOnlyRows に集約）
		if (modifiedKeys.Count > 0)
		{
			var sourceRows = await FetchRowsByDateTimeKeyAsync(srcConnection, tableName, fetchColumns, dateTimeKey, modifiedKeys);
			var targetRows = await FetchRowsByDateTimeKeyAsync(tgtConnection, tableName, fetchColumns, dateTimeKey, modifiedKeys);
			var srcGroups = GroupRowsByDateTimeKey(sourceRows, dateTimeKey);
			var tgtGroups = GroupRowsByDateTimeKey(targetRows, dateTimeKey);

			foreach (var key in modifiedKeys)
			{
				var srcGroup = srcGroups.GetValueOrDefault(key) ?? new List<Dictionary<string, object?>>();
				var tgtGroup = tgtGroups.GetValueOrDefault(key) ?? new List<Dictionary<string, object?>>();
				CompareRowSets(diff, pkColumns, nonPkCompareColumns, alternativeKey, srcGroup, tgtGroup,
					unmatchedSrcOut: srcOnlyRows, unmatchedTgtOut: tgtOnlyRows);
			}
		}

		// 横断比較（余りを収集する形式で実行）
		var finalSrcOnly = new List<Dictionary<string, object?>>();
		var finalTgtOnly = new List<Dictionary<string, object?>>();
		if (srcOnlyRows.Count > 0 || tgtOnlyRows.Count > 0)
			CompareRowSets(diff, pkColumns, nonPkCompareColumns, alternativeKey, srcOnlyRows, tgtOnlyRows,
				unmatchedSrcOut: finalSrcOnly, unmatchedTgtOut: finalTgtOnly);

		// ── ハッシュ一致グループの行を使った再照合 ──
		// 横断比較後に残った余り行は、ハッシュ一致で完全スキップされた
		// グループ内の行と AlternativeKey が一致する可能性がある。
		// その場合、ハッシュ一致グループの行を「代理ソース/ターゲット」として
		// Modify ペアを生成する。残りは Delete/Addition として報告する。
		if ((finalTgtOnly.Count > 0 || finalSrcOnly.Count > 0) && matchedKeys.Count > 0)
		{
			var matchedSrcRows = finalTgtOnly.Count > 0
				? await FetchRowsByDateTimeKeyAsync(srcConnection, tableName, fetchColumns, dateTimeKey, matchedKeys)
				: new List<Dictionary<string, object?>>();
			var matchedTgtRows = finalSrcOnly.Count > 0
				? await FetchRowsByDateTimeKeyAsync(tgtConnection, tableName, fetchColumns, dateTimeKey, matchedKeys)
				: new List<Dictionary<string, object?>>();

			string AltKey(Dictionary<string, object?> row)
				=> string.Join("|", alternativeKey.Select(c => FormatColumnValue(row.GetValueOrDefault(c))));

			// Addition 候補の再照合: ハッシュ一致グループのソース行と AlternativeKey マッチ → Modify
			if (finalTgtOnly.Count > 0 && matchedSrcRows.Count > 0)
			{
				var matchedSrcByAltKey = new Dictionary<string, List<Dictionary<string, object?>>>();
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
						diff.AddEntry(DiffType.Modify, pkColumns,
							SetPkToIgnore(srcRow, pkColumns),
							SetPkToIgnore(finalTgtOnly[i], pkColumns), nonPkCompareColumns);
						resolved.Add(i);
					}
				}
				// 残りを Addition として報告
				for (int i = 0; i < finalTgtOnly.Count; i++)
				{
					if (!resolved.Contains(i))
						diff.AddEntry(DiffType.Addition, pkColumns, null,
							SetPkToIgnore(finalTgtOnly[i], pkColumns), nonPkCompareColumns);
				}
			}
			else
			{
				// ハッシュ一致グループなし or Addition 候補なし → そのまま Addition
				foreach (var row in finalTgtOnly)
					diff.AddEntry(DiffType.Addition, pkColumns, null,
						SetPkToIgnore(row, pkColumns), nonPkCompareColumns);
			}

			// Delete 候補の再照合: ハッシュ一致グループのターゲット行と AlternativeKey マッチ → Modify
			if (finalSrcOnly.Count > 0 && matchedTgtRows.Count > 0)
			{
				var matchedTgtByAltKey = new Dictionary<string, List<Dictionary<string, object?>>>();
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
						diff.AddEntry(DiffType.Modify, pkColumns,
							SetPkToIgnore(finalSrcOnly[i], pkColumns),
							SetPkToIgnore(tgtRow, pkColumns), nonPkCompareColumns);
						resolved.Add(i);
					}
				}
				// 残りを Delete として報告
				for (int i = 0; i < finalSrcOnly.Count; i++)
				{
					if (!resolved.Contains(i))
						diff.AddEntry(DiffType.Delete, pkColumns,
							SetPkToIgnore(finalSrcOnly[i], pkColumns), null, nonPkCompareColumns);
				}
			}
			else
			{
				// ハッシュ一致グループなし or Delete 候補なし → そのまま Delete
				foreach (var row in finalSrcOnly)
					diff.AddEntry(DiffType.Delete, pkColumns,
						SetPkToIgnore(row, pkColumns), null, nonPkCompareColumns);
			}
		}
		else
		{
			// ハッシュ一致グループがない場合、余りをそのまま報告
			foreach (var row in finalTgtOnly)
				diff.AddEntry(DiffType.Addition, pkColumns, null,
					SetPkToIgnore(row, pkColumns), nonPkCompareColumns);
			foreach (var row in finalSrcOnly)
				diff.AddEntry(DiffType.Delete, pkColumns,
					SetPkToIgnore(row, pkColumns), null, nonPkCompareColumns);
		}

		return diff;
	}

	/// <summary>行セットを比較: 完全一致消し込み → AlternativeKeyマッチ(Modify) → 余り(Delete/Addition)</summary>
	private static void CompareRowSets(
		MySqlVerifierDiff diff, List<string> pkColumns, List<string> nonPkCompareColumns,
		List<string> alternativeKey,
		List<Dictionary<string, object?>> srcGroup, List<Dictionary<string, object?>> tgtGroup,
		List<Dictionary<string, object?>>? unmatchedSrcOut = null,
		List<Dictionary<string, object?>>? unmatchedTgtOut = null)
	{
		string RowContentKey(Dictionary<string, object?> row)
			=> string.Join("|", nonPkCompareColumns.Select(c => FormatColumnValue(row.GetValueOrDefault(c))));

		string AltKey(Dictionary<string, object?> row)
			=> string.Join("|", alternativeKey.Select(c => FormatColumnValue(row.GetValueOrDefault(c))));

		// Step 1: 完全一致行を消し込み
		var srcBag = new Dictionary<string, List<Dictionary<string, object?>>>();
		foreach (var row in srcGroup) AddToList(srcBag, RowContentKey(row), row);

		var tgtBag = new Dictionary<string, List<Dictionary<string, object?>>>();
		foreach (var row in tgtGroup) AddToList(tgtBag, RowContentKey(row), row);

		var unmatchedSrc = new List<Dictionary<string, object?>>();
		var unmatchedTgt = new List<Dictionary<string, object?>>();

		foreach (var contentKey in srcBag.Keys.Union(tgtBag.Keys))
		{
			var srcRows = srcBag.GetValueOrDefault(contentKey) ?? new List<Dictionary<string, object?>>();
			var tgtRows = tgtBag.GetValueOrDefault(contentKey) ?? new List<Dictionary<string, object?>>();
			var matchCount = Math.Min(srcRows.Count, tgtRows.Count);
			for (int i = matchCount; i < srcRows.Count; i++) unmatchedSrc.Add(srcRows[i]);
			for (int i = matchCount; i < tgtRows.Count; i++) unmatchedTgt.Add(tgtRows[i]);
		}

		// Step 2: AlternativeKey マッチ → Modify
		var srcByAltKey = new Dictionary<string, List<int>>();
		for (int i = 0; i < unmatchedSrc.Count; i++)
			AddToList(srcByAltKey, AltKey(unmatchedSrc[i]), i);

		var tgtByAltKey = new Dictionary<string, List<int>>();
		for (int i = 0; i < unmatchedTgt.Count; i++)
			AddToList(tgtByAltKey, AltKey(unmatchedTgt[i]), i);

		var pairedSrc = new HashSet<int>();
		var pairedTgt = new HashSet<int>();

		foreach (var key in srcByAltKey.Keys.Intersect(tgtByAltKey.Keys))
		{
			var srcIndices = srcByAltKey[key];
			var tgtIndices = tgtByAltKey[key];
			var pairCount = Math.Min(srcIndices.Count, tgtIndices.Count);
			for (int i = 0; i < pairCount; i++)
			{
				pairedSrc.Add(srcIndices[i]);
				pairedTgt.Add(tgtIndices[i]);
				diff.AddEntry(DiffType.Modify, pkColumns,
					SetPkToIgnore(unmatchedSrc[srcIndices[i]], pkColumns),
					SetPkToIgnore(unmatchedTgt[tgtIndices[i]], pkColumns), nonPkCompareColumns);
			}
		}

		// Step 3: 余りを集約 or 直接報告
		ReportOrCollectUnmatched(unmatchedSrc, pairedSrc, unmatchedSrcOut, row =>
			diff.AddEntry(DiffType.Delete, pkColumns, SetPkToIgnore(row, pkColumns), null, nonPkCompareColumns));
		ReportOrCollectUnmatched(unmatchedTgt, pairedTgt, unmatchedTgtOut, row =>
			diff.AddEntry(DiffType.Addition, pkColumns, null, SetPkToIgnore(row, pkColumns), nonPkCompareColumns));
	}


	private static Dictionary<string, List<Dictionary<string, object?>>> GroupRowsByDateTimeKey(
		List<Dictionary<string, object?>> rows, string dateTimeKey)
	{
		var groups = new Dictionary<string, List<Dictionary<string, object?>>>();
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
		var rows = new List<Dictionary<string, object?>>();
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

		Logger.LogDebug($"\tFetching all rows from Source...");
		var sourceRows = await FetchAllRowsAsync(srcConnection, fetchSql);
		Logger.LogDebug($"\tFetched {sourceRows.Count} rows from Source");
		Logger.LogDebug($"\tFetching all rows from Target...");
		var targetRows = await FetchAllRowsAsync(tgtConnection, fetchSql);
		Logger.LogDebug($"\tFetched {targetRows.Count} rows from Target");

		var sourceMultiset = BuildRowMultiset(sourceRows, allColumns);
		var targetMultiset = BuildRowMultiset(targetRows, allColumns);

		foreach (var sig in sourceMultiset.Keys.Union(targetMultiset.Keys))
		{
			var srcList = sourceMultiset.GetValueOrDefault(sig, new List<Dictionary<string, object?>>());
			var tgtList = targetMultiset.GetValueOrDefault(sig, new List<Dictionary<string, object?>>());
			for (int i = tgtList.Count; i < srcList.Count; i++)
				diff.AddEntry(DiffType.Delete, new List<string>(), srcList[i], null, allColumns);
			for (int i = srcList.Count; i < tgtList.Count; i++)
				diff.AddEntry(DiffType.Addition, new List<string>(), null, tgtList[i], allColumns);
		}

		Logger.LogDebug($"\tDeleted rows[{diff.Entries.Count(e => e.DiffType == DiffType.Delete)}] Added rows[{diff.Entries.Count(e => e.DiffType == DiffType.Addition)}]");
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
		var multiset = new Dictionary<string, List<Dictionary<string, object?>>>();
		foreach (var row in rows)
			AddToList(multiset, string.Join("||", allColumns.Select(c => RowDiff.FormatCsvValue(row.GetValueOrDefault(c)))), row);
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
		var hashes = new Dictionary<string, uint>();
		using var cmd = dbConnection.CreateCommand();
		cmd.CommandTimeout = 600;
		using var reader = await cmd.ExecuteReaderAsync(sql);
		while (await reader.ReadAsync())
		{
			var key = string.Join("||", pkColumns.Select(col =>
			{
				var val = reader.IsDBNull(reader.GetOrdinal(col)) ? "" : reader[col]?.ToString() ?? "";
				return $"{col}={val}";
			}));
			var hashOrdinal = reader.GetOrdinal("row_hash");
			hashes[key] = reader.IsDBNull(hashOrdinal) ? 0u : Convert.ToUInt32(reader[hashOrdinal]);
		}
		return hashes;
	}

	private async Task<List<Dictionary<string, object?>>> FetchRowsAsync(
		DbConnectionHelper dbConnection, string tableName,
		List<string> pkColumns, List<string> dataColumns, List<string> pkKeys)
	{
		var rows = new List<Dictionary<string, object?>>();
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
					var parts = k.Split("||");
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
		=> string.Join("||", pkColumns.Select(c => $"{c}={row.GetValueOrDefault(c)?.ToString() ?? ""}"));

	private async Task WriteDiffCsvAsync(
		string tableName,
		MySqlVerifierDiff diff,
		List<string> allColumns,
		List<string> pkColumns,
		bool ignorePk = false)
	{
		var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
		var pkSuffix = ignorePk ? "_IgnorePK"
			: pkColumns.Count > 0 ? $"_PK({string.Join(",", pkColumns)})" : "_NoPK";
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

