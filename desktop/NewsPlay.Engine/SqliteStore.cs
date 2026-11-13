using Microsoft.Data.Sqlite;
using NewsPlay.Core;
using System.Text.Json;

namespace NewsPlay.Engine;

public sealed class SqliteStore : IStateStore, IDisposable
{
    private readonly SqliteConnection db;
    public SqliteStore(string directory)
    {
        Directory.CreateDirectory(directory);
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = System.IO.Path.Combine(directory, "newsplay.db") }.ToString()); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS state (id INTEGER PRIMARY KEY CHECK(id=1), payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS as_run (id TEXT PRIMARY KEY, at TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS audit (id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, action TEXT NOT NULL, detail TEXT NOT NULL);
            """; cmd.ExecuteNonQuery();
    }
    public EngineState? Load() { using var c = db.CreateCommand(); c.CommandText = "SELECT payload FROM state WHERE id=1"; return c.ExecuteScalar() is string s ? JsonSerializer.Deserialize<EngineState>(s, Json.Options) : null; }
    public void Save(EngineState state) { using var c = db.CreateCommand(); c.CommandText = "INSERT INTO state VALUES(1,$p) ON CONFLICT(id) DO UPDATE SET payload=$p"; c.Parameters.AddWithValue("$p", JsonSerializer.Serialize(state, Json.Options)); c.ExecuteNonQuery(); }
    public void Log(AsRun row) { using var c = db.CreateCommand(); c.CommandText = "INSERT OR IGNORE INTO as_run VALUES($id,$at,$p)"; c.Parameters.AddWithValue("$id", row.Id.ToString()); c.Parameters.AddWithValue("$at", row.StartedAt.ToString("O")); c.Parameters.AddWithValue("$p", JsonSerializer.Serialize(row, Json.Options)); c.ExecuteNonQuery(); }
    public void Audit(string action, string detail) { using var c = db.CreateCommand(); c.CommandText = "INSERT INTO audit(at,action,detail) VALUES($at,$a,$d)"; c.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O")); c.Parameters.AddWithValue("$a", action); c.Parameters.AddWithValue("$d", detail); c.ExecuteNonQuery(); }
    public List<AsRun> Logs() { using var c = db.CreateCommand(); c.CommandText = "SELECT payload FROM as_run ORDER BY at DESC LIMIT 10000"; using var r = c.ExecuteReader(); var rows = new List<AsRun>(); while (r.Read()) rows.Add(JsonSerializer.Deserialize<AsRun>(r.GetString(0), Json.Options)!); return rows; }
    public void Dispose() => db.Dispose();
}
