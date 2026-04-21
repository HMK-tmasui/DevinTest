using HscTool.Shared.Json;
using HscTool.Model.Json;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace HvnDbVerifier.Model.Json;

public class SqLiteVerifierSettings
{
	    /// <summary>
    /// SqliteVerifierSource MySQL connection settings
    /// </summary>
    [JsonProperty("source")]
    public MySQL Source { get; set; } = new();
}

/// <summary>
/// exclude_conditions に指定する除外条件
/// </summary>
public class ExcludeCondition
{
    [JsonProperty("column")]
    public string Column { get; set; } = "";

    [JsonProperty("value")]
    public object? Value { get; set; }
}

/// <summary>
/// reference_table の設定（JOIN 対象テーブル）
/// </summary>
public class ReferenceTableConfig
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("join_column")]
    public string JoinColumn { get; set; } = "";

    [JsonProperty("exclude_conditions")]
    public ExcludeCondition[] ExcludeConditions { get; set; } = [];
}

/// <summary>
/// include_tables の各テーブル設定
/// </summary>
public class IncludeTableConfig
{
	[JsonProperty("name")]
	public string Name { get; set; } = "";

	[JsonProperty("columns")]
	public string[] Columns { get; set; } = [];

	[JsonProperty("exclude_conditions")]
	public ExcludeCondition[] ExcludeConditions { get; set; } = [];

	[JsonProperty("reference_table")]
	public ReferenceTableConfig? ReferenceTable { get; set; }

	[JsonProperty("force_pk")]
	public List<string> ForcePK { get; set; } = [];

	//IDが衝突するケースがあり、主キーを無視して比較するオプションを追加
	[JsonProperty("ignore_pk")]
	public bool IgnorePK { get; set; } = false;

	//変更と判断するカラム
	[JsonProperty("modified_key")]
	public string ModifiedKey { get; set; } = "";

	//不変と判断するカラム　このカラムが変更されていた場合、Delete/Additionペアか記録される
	[JsonProperty("alternative_key")]
	public List<string> AlternativeKey { get; set; } = [];
}

public class MySqlVerifierSettings
{
    /// <summary>
    /// MySqlVerifierSource MySQL connection settings
    /// </summary>
    [JsonProperty("source")]
    public MySQL Source { get; set; } = new();

	/// <summary>
    /// MySqlVerifierTarget MySQL connection settings
    /// </summary>
    [JsonProperty("target")]
    public MySQL Target { get; set; } = new();

    /// <summary>
    /// Output directory for CSV files
    /// </summary>
    [JsonProperty("output_dir")]
    public string OutputDir { get; set; } = "./output";

    /// <summary>
    /// Maximum CSV file size in bytes (default: 1MiB)
    /// </summary>
    [JsonProperty("max_file_size", Required = Required.Default)]
    public long MaxFileSize { get; set; } = 1024 * 1024;

    /// <summary>
    /// Tables to include with column/condition configuration
    /// </summary>
    [JsonProperty("include_tables", Required = Required.Default)]
    public IncludeTableConfig[] IncludeTables { get; set; } = [];
}
/// <summary>
/// Configuration for schema comparison
/// </summary>
public class HvnDbVerifierSettings : JsonSettingLoader<HvnDbVerifierSettings>
{

	[JsonProperty("sqlite_verifier")]
    public SqLiteVerifierSettings SqLiteVerifier { get; set; } = new();

	[JsonProperty("mysql_verifier")]
    public MySqlVerifierSettings MySqlVerifier { get; set; } = new();

	// ファクトリメソッド
	public static HvnDbVerifierSettings Instance => CreateInstance(HvnDbVerifierLogger.Instance, "hvndb_verifier_settings.json");

	[JsonConstructor]
	public HvnDbVerifierSettings(){ }
}
