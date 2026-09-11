using System.IO;
using Microsoft.Data.Sqlite;

namespace XAssistant.Services;

public record WordFrequencyRow(string Word, long Count, long Corrections, long CorrectedUses, int Mark);
public record WordOccurrence(string Word, string StartedAt, string FinishedAt, long Corrections)
{
    public string TimeText => DateTime.TryParse(FinishedAt, out var time) ? time.ToString("MM-dd HH:mm:ss") : FinishedAt;
}
public record WordFrequencySnapshot(List<WordFrequencyRow> Words, List<WordOccurrence> Recent, string Through, long Cursor, string Pending);

/// <summary>Disposable projection of the durable hook log. Checkpoint and deltas commit together.</summary>
public sealed class WordFrequencyStore(string keyDatabase, string summaryDatabase, string marksDatabase)
{
    private readonly object _gate = new();
    private static SqliteConnection Open(string path, bool readOnly = false)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        c.Open(); return c;
    }
    private static SqliteCommand Command(SqliteConnection c, string sql, params object[] values)
    {
        var cmd = c.CreateCommand(); cmd.CommandText = sql;
        for (int i = 0; i < values.Length; i++) cmd.Parameters.AddWithValue("@p" + i, values[i]);
        return cmd;
    }
    private static void Exec(SqliteConnection c, string sql, params object[] values)
    { using var cmd = Command(c, sql, values); cmd.ExecuteNonQuery(); }
    private static long Number(SqliteConnection c, string sql, params object[] values)
    { using var cmd = Command(c, sql, values); return Convert.ToInt64(cmd.ExecuteScalar() ?? 0); }
    private SqliteConnection Summary()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(summaryDatabase))!);
        var c = Open(summaryDatabase);
        Exec(c, """
            CREATE TABLE IF NOT EXISTS State(Id INTEGER PRIMARY KEY CHECK(Id=1), Cursor INTEGER NOT NULL, Through TEXT NOT NULL, Anchor TEXT NOT NULL);
            INSERT OR IGNORE INTO State VALUES(1,0,'','');
            CREATE TABLE IF NOT EXISTS Fragments(Id INTEGER PRIMARY KEY AUTOINCREMENT, Word TEXT NOT NULL, Edits INTEGER NOT NULL, Separators INTEGER NOT NULL, Started TEXT NOT NULL, Finished TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Totals(Word TEXT PRIMARY KEY, Uses INTEGER NOT NULL, Edits INTEGER NOT NULL, CorrectedUses INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Totals_Uses ON Totals(Uses DESC,Word);
            CREATE INDEX IF NOT EXISTS IX_Totals_Corrected ON Totals(CorrectedUses DESC,Word);
            CREATE TABLE IF NOT EXISTS Batches(LastId INTEGER PRIMARY KEY, FirstId INTEGER NOT NULL, Started TEXT NOT NULL, Finished TEXT NOT NULL, RecordCount INTEGER NOT NULL);
            """);
        return c;
    }
    private SqliteConnection Marks()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(marksDatabase))!);
        var c = Open(marksDatabase);
        Exec(c, "CREATE TABLE IF NOT EXISTS Marks(Word TEXT PRIMARY KEY, Value INTEGER NOT NULL CHECK(Value BETWEEN -1 AND 1));");
        return c;
    }
    public void SetMark(string word, int mark)
    {
        if (mark is < -1 or > 1) throw new ArgumentOutOfRangeException(nameof(mark));
        lock (_gate)
        {
            using var c = Marks();
            Exec(c, "INSERT INTO Marks VALUES(@p0,@p1) ON CONFLICT(Word) DO UPDATE SET Value=excluded.Value", word.ToLowerInvariant(), mark);
        }
    }
    /// <summary>At most one bounded batch per call; caller yields between batches.</summary>
    public bool Update(bool rebuild = false)
    {
        lock (_gate)
        {
            using var source = Open(keyDatabase, true);
            using var c = Summary();
            long cursor = Number(c, "SELECT Cursor FROM State WHERE Id=1");
            using var anchorCmd = Command(c, "SELECT Anchor FROM State WHERE Id=1");
            string anchor = (string)anchorCmd.ExecuteScalar()!;
            using var sourceAnchor = Command(source, "SELECT Key || char(9) || PressTime FROM KeyPressRecords WHERE Id=@p0", cursor);
            bool reset = rebuild || (cursor > 0 && !Equals(sourceAnchor.ExecuteScalar(), anchor));
            using var tx = c.BeginTransaction();
            if (reset)
            {
                Exec(c, "DELETE FROM Fragments; DELETE FROM Totals; DELETE FROM Batches; UPDATE State SET Cursor=0,Through='',Anchor='' WHERE Id=1;");
                cursor = 0;
            }
            using var read = Command(source, "SELECT Id,Key,PressTime FROM KeyPressRecords WHERE Id>@p0 ORDER BY Id LIMIT 2000", cursor);
            using var reader = read.ExecuteReader();
            long first = 0, last = cursor; int count = 0; string started = "", finished = "", lastKey = "";
            while (reader.Read())
            {
                last = reader.GetInt64(0); lastKey = reader.GetString(1); finished = reader.GetString(2);
                if (count++ == 0) { first = last; started = finished; }
                Apply(c, DecodeKey(lastKey), finished);
            }
            if (count > 0)
            {
                Exec(c, "UPDATE State SET Cursor=@p0,Through=@p1,Anchor=@p2 WHERE Id=1", last, finished, lastKey + "\t" + finished);
                Exec(c, "INSERT INTO Batches VALUES(@p0,@p1,@p2,@p3,@p4)", last, first, started, finished, count);
            }
            tx.Commit();
            return count == 2000;
        }
    }
    // Physical letters/digits are the canonical source, so live and historical replay use exactly the same rules.
    public static char DecodeKey(string key)
    {
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) return char.ToLowerInvariant(key[0]);
        string k = key.Replace(" ", "").ToLowerInvariant();
        if (k is "backspace" or "back" or "退格") return '\b';
        foreach (string prefix in new[] { "numpad", "num", "数字键盘" })
            if (k.StartsWith(prefix) && k.Length == prefix.Length + 1 && char.IsAsciiDigit(k[^1])) return k[^1];
        return ' ';
    }
    private record Fragment(long Id, string Word, long Edits, long Separators, string Started, string Finished);
    private static Fragment? Last(SqliteConnection c)
    {
        using var cmd = Command(c, "SELECT Id,Word,Edits,Separators,Started,Finished FROM Fragments ORDER BY Id DESC LIMIT 1");
        using var r = cmd.ExecuteReader();
        return r.Read() ? new(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), r.GetString(5)) : null;
    }
    private static void Adjust(SqliteConnection c, Fragment f, int direction)
    {
        Exec(c, """
            INSERT INTO Totals VALUES(@p0,@p1,@p2,@p3) ON CONFLICT(Word) DO UPDATE SET
            Uses=Uses+excluded.Uses, Edits=Edits+excluded.Edits, CorrectedUses=CorrectedUses+excluded.CorrectedUses
            """, f.Word, direction, f.Edits * direction, f.Edits > 0 ? direction : 0);
        Exec(c, "DELETE FROM Totals WHERE Word=@p0 AND Uses=0", f.Word);
    }
    private static void Apply(SqliteConnection c, char input, string time)
    {
        var f = Last(c);
        if (input == '\b')
        {
            // Empty rewritten fragments carry edits until replacement; another Backspace crosses to the previous separator.
            while (f is { Word.Length: 0, Separators: 0 }) { Exec(c, "DELETE FROM Fragments WHERE Id=@p0", f.Id); f = Last(c); }
            if (f == null) return;
            if (f.Separators > 0)
            {
                if (f.Separators == 1 && f.Word.Length > 0) Adjust(c, f, -1);
                Exec(c, "UPDATE Fragments SET Separators=Separators-1 WHERE Id=@p0", f.Id);
            }
            else if (f.Word.Length > 0)
                Exec(c, "UPDATE Fragments SET Word=@p0,Edits=Edits+1,Finished=@p1 WHERE Id=@p2", f.Word[..^1], time, f.Id);
            return;
        }
        if (input == ' ')
        {
            if (f == null) { Exec(c, "INSERT INTO Fragments(Word,Edits,Separators,Started,Finished) VALUES('',0,1,@p0,@p0)", time); return; }
            if (f.Separators == 0 && f.Word.Length > 0) Adjust(c, f, 1);
            Exec(c, "UPDATE Fragments SET Separators=Separators+1 WHERE Id=@p0", f.Id);
        }
        else if (f == null || f.Separators > 0)
            Exec(c, "INSERT INTO Fragments(Word,Edits,Separators,Started,Finished) VALUES(@p0,0,0,@p1,@p1)", input.ToString(), time);
        else
            Exec(c, "UPDATE Fragments SET Word=Word || @p0,Finished=@p1 WHERE Id=@p2", input.ToString(), time, f.Id);
    }
    public WordFrequencySnapshot Read(string filter = "全部")
    {
        lock (_gate)
        {
            using var c = Summary(); using var m = Marks();
            Exec(c, "ATTACH DATABASE @p0 AS annotations", Path.GetFullPath(marksDatabase));
            // Annotated words remain available even if editing subsequently removes all their occurrences.
            var rows = new List<WordFrequencyRow>();
            string condition = filter switch { "易错" => "Edits>0", "已标记" => "Mark<>0", _ => "1=1" };
            string order = filter == "易错" ? "CorrectedUses" : "Uses";
            using (var cmd = Command(c, $"""
                SELECT Word,Uses,Edits,CorrectedUses,Mark FROM (
                  SELECT t.Word,t.Uses,t.Edits,t.CorrectedUses,COALESCE(m.Value,0) AS Mark
                  FROM Totals t LEFT JOIN annotations.Marks m ON m.Word=t.Word
                  UNION ALL SELECT m.Word,0,0,0,m.Value FROM annotations.Marks m
                  WHERE m.Value<>0 AND NOT EXISTS(SELECT 1 FROM Totals t WHERE t.Word=m.Word)
                ) WHERE {condition} ORDER BY {order} DESC,Word LIMIT 100
                """))
            using (var r = cmd.ExecuteReader()) while (r.Read()) rows.Add(new(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt32(4)));
            var recent = new List<WordOccurrence>();
            using (var cmd = Command(c, "SELECT Word,Started,Finished,Edits FROM Fragments WHERE Separators>0 AND Word<>'' ORDER BY Id DESC LIMIT 50"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) recent.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt64(3)));
            using var state = Command(c, "SELECT Cursor,Through FROM State WHERE Id=1"); using var sr = state.ExecuteReader(); sr.Read();
            var last = Last(c);
            return new(rows, recent, sr.GetString(1), sr.GetInt64(0), last?.Separators == 0 ? last.Word : "");
        }
    }
}
