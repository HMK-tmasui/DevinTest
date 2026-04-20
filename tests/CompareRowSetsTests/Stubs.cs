// Minimal stubs for external dependencies used by MySqlVerifierService
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;

namespace HscTool.Diagnostics
{
    public interface ILogger
    {
        void LogInfo(string message);
        void LogDebug(string message);
        void LogWarning(string message);
        void LogError(string message);
        void NewLine();
    }

    public class TestLogger : ILogger
    {
        public List<string> Messages { get; } = new();
        public void LogInfo(string message) => Messages.Add($"[INFO] {message}");
        public void LogDebug(string message) => Messages.Add($"[DEBUG] {message}");
        public void LogWarning(string message) => Messages.Add($"[WARN] {message}");
        public void LogError(string message) => Messages.Add($"[ERROR] {message}");
        public void NewLine() { }
    }
}

namespace HscTool.Shared
{
    public static class ToolSet
    {
        public static string GetCurrentMethod() => "TestMethod";
    }

    public class DbConnectionHelper : IDisposable, IAsyncDisposable
    {
        private readonly HscTool.Diagnostics.ILogger _logger;
        private readonly IDbContext _context;

        public DbConnectionHelper(HscTool.Diagnostics.ILogger logger, IDbContext context)
        {
            _logger = logger;
            _context = context;
        }

        public DbCommandWrapper CreateCommand() => new(_context);
        public static void AddParameter(DbCommandWrapper cmd, string name, object value) { }
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public class DbCommandWrapper : IDisposable
    {
        private readonly IDbContext _context;
        public int CommandTimeout { get; set; }

        public DbCommandWrapper(IDbContext context) { _context = context; }

        public Task<DbReaderWrapper> ExecuteReaderAsync(string sql)
        {
            return Task.FromResult(new DbReaderWrapper(_context.ExecuteQuery(sql)));
        }

        public void Dispose() { }
    }

    public class DbReaderWrapper : IDisposable
    {
        private readonly List<Dictionary<string, object?>> _rows;
        private int _currentIndex = -1;

        public DbReaderWrapper(List<Dictionary<string, object?>> rows) { _rows = rows; }

        public int FieldCount => _rows.Count > 0 && _currentIndex >= 0 ? _rows[_currentIndex].Count : 0;

        public Task<bool> ReadAsync()
        {
            _currentIndex++;
            return Task.FromResult(_currentIndex < _rows.Count);
        }

        public string GetName(int ordinal)
        {
            if (_currentIndex < 0 || _currentIndex >= _rows.Count) return "";
            return _rows[_currentIndex].Keys.ElementAt(ordinal);
        }

        public int GetOrdinal(string name)
        {
            if (_currentIndex < 0 || _currentIndex >= _rows.Count) return -1;
            var keys = _rows[_currentIndex].Keys.ToList();
            return keys.FindIndex(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        }

        public bool IsDBNull(int ordinal)
        {
            if (_currentIndex < 0 || _currentIndex >= _rows.Count) return true;
            var val = _rows[_currentIndex].Values.ElementAt(ordinal);
            return val == null;
        }

        public object? GetValue(int ordinal)
        {
            if (_currentIndex < 0 || _currentIndex >= _rows.Count) return null;
            return _rows[_currentIndex].Values.ElementAt(ordinal);
        }

        public object? this[string name]
        {
            get
            {
                if (_currentIndex < 0 || _currentIndex >= _rows.Count) return null;
                return _rows[_currentIndex].TryGetValue(name, out var val) ? val : null;
            }
        }

        public object? this[int ordinal] => GetValue(ordinal);

        public void Dispose() { }
    }

    public interface IDbContext : IAsyncDisposable
    {
        List<Dictionary<string, object?>> ExecuteQuery(string sql);
    }

    public interface IDbContextFactory
    {
        IDbContext CreateContext();
    }

    public class MySqlDbContextFactory : IDbContextFactory
    {
        private readonly HscTool.Model.Json.MySQL _config;
        public MySqlDbContextFactory(HscTool.Model.Json.MySQL config) { _config = config; }
        public IDbContext CreateContext() => new InMemoryDbContext();
    }

    public class InMemoryDbContext : IDbContext
    {
        public Dictionary<string, List<Dictionary<string, object?>>> Tables { get; } = new();
        public Func<string, List<Dictionary<string, object?>>>? QueryHandler { get; set; }

        public List<Dictionary<string, object?>> ExecuteQuery(string sql)
        {
            if (QueryHandler != null) return QueryHandler(sql);
            return new List<Dictionary<string, object?>>();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

namespace HscTool.Shared.Diff
{
    public enum DiffType { Delete, Addition, Modify }

    /// <summary>
    /// IRowDiff の非ジェネリック抽象基底クラス（実 HSCTOOL の RowDiff に対応）。
    /// DiffEntry ベースの差分管理・CSV 出力ロジックを統合。
    /// </summary>
    public abstract class RowDiff
    {
        /// <summary>1行分の差分データ</summary>
        public class DiffEntry
        {
            public DiffType DiffType { get; init; }
            public Dictionary<string, object?> SourceValues { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, object?> TargetValues { get; init; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> ChangedColumns { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        }

        protected List<string> AllColumns { get; set; } = new();
        protected HashSet<string>? FixedColumnSet;

        public List<DiffEntry> Entries { get; } = new();
        public int DiffCount => Entries.Count;

        /// <summary>CSV形式の値フォーマット（DateTime/TimeSpan 対応）</summary>
        public static string FormatCsvValue(object? value)
        {
            if (value == null) return "";
            if (value is DateTime dt) return dt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            if (value is TimeSpan ts) return ts.ToString(@"hh\:mm\:ss");
            return value.ToString() ?? "";
        }

        public virtual List<string> BuildCsvLine()
        {
            var lines = new List<string> { string.Join(",", AllColumns.Select(c => $"\"{c}\"")) };
            foreach (var entry in Entries)
            {
                var row = entry.SourceValues.Count > 0 ? entry.SourceValues : entry.TargetValues;
                if (row.Count == 0) continue;
                lines.Add(string.Join(",", AllColumns.Select(c =>
                    $"\"{FormatCsvValue(row.GetValueOrDefault(c))}\"")));
            }
            return lines;
        }
    }

    public class MySqlVerifierDiff : RowDiff
    {
        /// <summary>テスト用: 最後に Init された diff インスタンスを保持</summary>
        public static MySqlVerifierDiff? LastInstance { get; set; }

        public void Init(List<string> allColumns, string[]? pkColumns)
        {
            AllColumns = allColumns;
            FixedColumnSet = pkColumns != null ? new HashSet<string>(pkColumns, StringComparer.OrdinalIgnoreCase) : null;
            LastInstance = this;
        }

        public void AddEntry(DiffType diffType, List<string> pkColumns,
            Dictionary<string, object?>? sourceRow, Dictionary<string, object?>? targetRow,
            List<string> compareColumns)
        {
            var diffCols = new List<string>();
            if (diffType == DiffType.Modify && sourceRow != null && targetRow != null)
            {
                foreach (var col in compareColumns)
                {
                    var srcVal = sourceRow.GetValueOrDefault(col)?.ToString() ?? "";
                    var tgtVal = targetRow.GetValueOrDefault(col)?.ToString() ?? "";
                    if (srcVal != tgtVal) diffCols.Add(col);
                }
            }
            Entries.Add(new DiffEntry
            {
                DiffType = diffType,
                SourceValues = sourceRow ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
                TargetValues = targetRow ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
                ChangedColumns = new HashSet<string>(diffCols, StringComparer.OrdinalIgnoreCase)
            });
        }
    }
}

namespace HscTool.Model.Json
{
    public class MySQL
    {
        public string Database { get; set; } = "";
    }
}

namespace HscTool.Shared.Json
{
    public class JsonSettingLoader<T> where T : new() 
    {
        protected static T CreateInstance(object logger, string file) => new();
    }
}

namespace HvnDbVerifier
{
    public class HvnDbVerifierLogger
    {
        public static HscTool.Diagnostics.ILogger Instance { get; } = new HscTool.Diagnostics.TestLogger();
    }

    public class MySqlVerifierResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = "";
        public List<string> MissingInTarget { get; set; } = new();
        public List<string> MissingInSource { get; set; } = new();
        public List<string> PrimaryKeyMismatch { get; set; } = new();
        public List<string> NoPrimaryKey { get; set; } = new();
        public Dictionary<string, int> TableDiffs { get; set; } = new();
    }

    public class ColumnInfo
    {
        public string ColumnName { get; set; } = "";
        public string DataType { get; set; } = "";
        public bool IsNullable { get; set; }
        public string ColumnType { get; set; } = "";
        public bool IsPrimaryKey { get; set; }
        public int PrimaryKeyOrdinal { get; set; }
    }

    public class MySqlVerifierMetadata
    {
        public string TableName { get; set; } = "";
        public List<ColumnInfo> Columns { get; set; } = new();
        public List<ColumnInfo> PrimaryKeys { get; set; } = new();

        public List<string> GetPrimaryKeyNames() => PrimaryKeys.Select(c => c.ColumnName).ToList();
        public List<ColumnInfo> GetCompareColumns() =>
            Columns.Where(c => !PrimaryKeys.Any(pk => pk.ColumnName == c.ColumnName)).ToList();
    }
}

// FrozenSet is provided by .NET 8 System.Collections.Frozen — no stub needed
