using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Picker.Core;

/// <summary>
/// The connection to the server: catalog reads, gated SELECTs, and the
/// DESCRIBE queries - every byte that crosses to SQL Server crosses
/// here.
///
/// <para><b>Every query this class composes is metadata over
/// three-part, bracket-quoted names</b> - and the one query it runs on a
/// caller's behalf has already been through the gate, which rewrote its
/// references the same way. The connection's default database decides
/// nothing.</para>
///
/// <para><b>Failure mapping is deliberate.</b> A timeout is
/// <see cref="Outcome.TimedOut"/>; everything else the server or the
/// network declines is <see cref="Outcome.Denied"/>, because it is not
/// Picker's decision and must never read as one.</para>
/// </summary>
public sealed class SqlData : ICatalogSource, IQuerySource, IConstraintSource
{
    /// <summary>How long one statement may run before it is a refusal
    /// rather than a wait, unless the call says otherwise.</summary>
    public const int DefaultTimeoutSeconds = 30;

    /// <summary>Rows returned per call unless the call says otherwise;
    /// the answer says when it bites, and the remedy - TOP, WHERE,
    /// OFFSET - is the caller's.</summary>
    public const int DefaultRowCap = 100;

    /// <summary>The character budget across a whole result, the 40,000
    /// discipline: how much of an answer survives the trip.</summary>
    public const int CharCap = 40_000;

    /// <summary>One text cell's ceiling; past it the cell says what was
    /// withheld rather than silently trimming.</summary>
    public const int CellCap = 2_000;

    readonly int timeoutSeconds;

    public SqlData(int timeoutSeconds = DefaultTimeoutSeconds)
    {
        this.timeoutSeconds = timeoutSeconds;
    }

    static Result<SqlConnection> Open(ServerDecl server)
    {
        Result<string> connect = server.Connect.Resolve();
        if (!connect.IsOk) return connect.Carry<SqlConnection>();

        try
        {
            var connection = new SqlConnection(connect.Value);
            connection.Open();
            return Result<SqlConnection>.Ok(connection);
        }
        catch (Exception e) when (e is SqlException or InvalidOperationException or ArgumentException)
        {
            // The connection string itself is never in the message - the
            // server's declared name is the only name a report gets.
            return Result<SqlConnection>.Fail(Outcome.Denied,
                $"could not connect to server '{server.Name}': {Trimmed(e)}",
                server.Name);
        }
    }

    /// <summary>An exception message with nothing appended: no stack,
    /// no server-generated help. A credential never appears in
    /// SqlException text, but the trim keeps this honest anyway.</summary>
    static string Trimmed(Exception e)
    {
        string message = e.Message;
        int line = message.IndexOf('\n');
        return line > 0 ? message[..line].TrimEnd() : message;
    }

    static Failure Declined(ServerDecl server, SqlException e) =>
        e.Number == -2
            ? new Failure(Outcome.TimedOut,
                $"the query did not finish within the timeout on '{server.Name}'. Narrow it, "
                + "or raise the timeout", server.Name)
            : new Failure(Outcome.Denied,
                $"server '{server.Name}' refused the query: {Trimmed(e)}", server.Name);

    // ---- the catalog ----

    public Task<Result<CatalogSnapshot>> LoadAsync(
        ServerDecl server, DatabaseDecl database, CancellationToken cancel) =>
        Task.Run(() => Load(server, database), cancel);

    Result<CatalogSnapshot> Load(ServerDecl server, DatabaseDecl database)
    {
        Result<SqlConnection> opened = Open(server);
        if (!opened.IsOk) return opened.Carry<CatalogSnapshot>();

        using SqlConnection connection = opened.Value;
        string db = Names.Quote(database.Name);

        // Tables and views with their columns, one round trip. sys.types
        // joined on user_type_id names the type a person declared
        // (nvarchar, not sysname's underlying id).
        string sql = $"""
            SELECT s.name, o.name, o.type, c.name, t.name,
                   c.max_length, c.precision, c.scale, c.is_nullable, c.column_id
            FROM {db}.sys.objects o
            JOIN {db}.sys.schemas s ON s.schema_id = o.schema_id
            JOIN {db}.sys.columns c ON c.object_id = o.object_id
            JOIN {db}.sys.types t ON t.user_type_id = c.user_type_id
            WHERE o.type IN ('U', 'V')
            ORDER BY s.name, o.name, c.column_id
            """;

        var objects = new Dictionary<string, (string Schema, string Name, bool IsView, List<CatalogColumn> Columns)>();

        try
        {
            using var command = new SqlCommand(sql, connection) { CommandTimeout = timeoutSeconds };
            using SqlDataReader reader = command.ExecuteReader();

            while (reader.Read())
            {
                string schema = reader.GetString(0);
                string name = reader.GetString(1);
                bool isView = reader.GetString(2).Trim() == "V";
                string column = reader.GetString(3);
                string type = TypeOf(reader.GetString(4),
                    reader.GetInt16(5), reader.GetByte(6), reader.GetByte(7));
                bool nullable = reader.GetBoolean(8);
                int ordinal = reader.GetInt32(9);

                string key = schema + "\n" + name;
                if (!objects.TryGetValue(key, out var entry))
                {
                    entry = (schema, name, isView, []);
                    objects[key] = entry;
                }
                entry.Columns.Add(new CatalogColumn(column, type, nullable, ordinal));
            }
        }
        catch (SqlException e)
        {
            return Result<CatalogSnapshot>.Fail(Declined(server, e));
        }

        var made = new List<CatalogObject>();
        foreach (var entry in objects.Values)
            made.Add(new CatalogObject(entry.Schema, entry.Name, entry.IsView, entry.Columns));

        return Result<CatalogSnapshot>.Ok(
            new CatalogSnapshot(database.Name, made, CatalogSnapshot.HashOf(made)));
    }

    /// <summary>A type written the way a person would declare it:
    /// nvarchar(80), decimal(10,2), varbinary(max).</summary>
    static string TypeOf(string name, short maxLength, byte precision, byte scale)
    {
        switch (name)
        {
            case "varchar" or "char" or "varbinary" or "binary":
                return maxLength < 0 ? $"{name}(max)" : $"{name}({maxLength})";
            case "nvarchar" or "nchar":
                return maxLength < 0 ? $"{name}(max)" : $"{name}({maxLength / 2})";
            case "decimal" or "numeric":
                return $"{name}({precision},{scale})";
            case "datetime2" or "datetimeoffset" or "time":
                return $"{name}({scale})";
            default:
                return name;
        }
    }

    // ---- the one gated query ----

    public Task<Result<Table>> QueryAsync(
        ServerDecl server, string sql, int rowCap, CancellationToken cancel) =>
        Task.Run(() => Query(server, sql, rowCap), cancel);

    Result<Table> Query(ServerDecl server, string sql, int rowCap)
    {
        Result<SqlConnection> opened = Open(server);
        if (!opened.IsOk) return opened.Carry<Table>();

        using SqlConnection connection = opened.Value;

        try
        {
            using var command = new SqlCommand(sql, connection) { CommandTimeout = timeoutSeconds };
            using SqlDataReader reader = command.ExecuteReader();

            var columns = new List<ColumnFact>();
            for (int i = 0; i < reader.FieldCount; i++)
                columns.Add(new ColumnFact(
                    reader.GetName(i), reader.GetDataTypeName(i), true));

            var rows = new List<IReadOnlyList<Cell>>();
            bool truncated = false;
            int spent = 0;

            while (reader.Read())
            {
                if (rows.Count >= rowCap || spent >= CharCap)
                {
                    truncated = true;
                    break;
                }

                var row = new List<Cell>(columns.Count);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    Cell cell = Render(reader, i);
                    spent += cell.Text?.Length ?? 4;
                    row.Add(cell);
                }
                rows.Add(row);
            }

            return Result<Table>.Ok(new Table(columns, rows, truncated));
        }
        catch (SqlException e)
        {
            return Result<Table>.Fail(Declined(server, e));
        }
    }

    /// <summary>One value as a cell. Binary and oversize values are
    /// summarised, never streamed - a varbinary(max) is not a payload an
    /// answer is improved by, and the summary says what was withheld.</summary>
    static Cell Render(SqlDataReader reader, int i)
    {
        if (reader.IsDBNull(i)) return new Cell(CellKind.Null, null);

        object value = reader.GetValue(i);
        switch (value)
        {
            case bool b:
                return new Cell(CellKind.Boolean, b ? "true" : "false");
            case byte or short or int or long:
                return new Cell(CellKind.Integer,
                    Convert.ToInt64(value, CultureInfo.InvariantCulture)
                        .ToString(CultureInfo.InvariantCulture));
            case decimal d:
                return new Cell(CellKind.Decimal, d.ToString(CultureInfo.InvariantCulture));
            case float or double:
                return new Cell(CellKind.Float,
                    Convert.ToDouble(value, CultureInfo.InvariantCulture)
                        .ToString("R", CultureInfo.InvariantCulture));
            case DateTime t:
                return new Cell(CellKind.DateTime, t.ToString("o", CultureInfo.InvariantCulture));
            case DateTimeOffset t:
                return new Cell(CellKind.DateTime, t.ToString("o", CultureInfo.InvariantCulture));
            case TimeSpan t:
                return new Cell(CellKind.DateTime, t.ToString("c", CultureInfo.InvariantCulture));
            case Guid g:
                return new Cell(CellKind.Other, g.ToString());
            case byte[] bytes:
            {
                string prefix = Convert.ToHexStringLower(
                    bytes, 0, Math.Min(bytes.Length, 32));
                return new Cell(CellKind.Binary,
                    $"0x{prefix}{(bytes.Length > 32 ? "…" : "")} ({bytes.Length} bytes)");
            }
            case string s:
                return s.Length <= CellCap
                    ? new Cell(CellKind.Text, s)
                    : new Cell(CellKind.Text,
                        s[..CellCap] + $"… ({s.Length} characters; select a substring for the rest)");
            default:
                return new Cell(CellKind.Other,
                    Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
        }
    }

    // ---- DESCRIBE's extra facts ----

    public Task<Result<(IReadOnlyList<IndexFact> Indexes, IReadOnlyList<ForeignKeyFact> ForeignKeys)>>
        ConstraintsAsync(ResolvedObject on, CancellationToken cancel) =>
        Task.Run(() => Constraints(on), cancel);

    Result<(IReadOnlyList<IndexFact>, IReadOnlyList<ForeignKeyFact>)> Constraints(
        ResolvedObject on)
    {
        Result<SqlConnection> opened = Open(on.Server);
        if (!opened.IsOk)
            return opened.Carry<(IReadOnlyList<IndexFact>, IReadOnlyList<ForeignKeyFact>)>();

        using SqlConnection connection = opened.Value;
        string db = Names.Quote(on.Database.Name);
        string qualified =
            $"{Names.Quote(on.Database.Name)}.{Names.Quote(on.Object.Schema)}.{Names.Quote(on.Object.Name)}";

        try
        {
            var indexes = new Dictionary<string, (bool Unique, bool Primary, List<string> Columns)>();
            string indexSql = $"""
                SELECT i.name, i.is_unique, i.is_primary_key, c.name, ic.key_ordinal
                FROM {db}.sys.indexes i
                JOIN {db}.sys.index_columns ic
                  ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN {db}.sys.columns c
                  ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID(@name) AND i.name IS NOT NULL
                ORDER BY i.name, ic.key_ordinal
                """;

            using (var command = new SqlCommand(indexSql, connection) { CommandTimeout = timeoutSeconds })
            {
                command.Parameters.AddWithValue("@name", qualified);
                using SqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string name = reader.GetString(0);
                    if (!indexes.TryGetValue(name, out var entry))
                    {
                        entry = (reader.GetBoolean(1), reader.GetBoolean(2), []);
                        indexes[name] = entry;
                    }
                    entry.Columns.Add(reader.GetString(3));
                }
            }

            var keys = new Dictionary<string, (List<string> Columns, string RefSchema, string RefTable, List<string> RefColumns)>();
            string keySql = $"""
                SELECT fk.name, pc.name, rs.name, rt.name, rc.name
                FROM {db}.sys.foreign_keys fk
                JOIN {db}.sys.foreign_key_columns fkc
                  ON fkc.constraint_object_id = fk.object_id
                JOIN {db}.sys.columns pc
                  ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
                JOIN {db}.sys.objects rt ON rt.object_id = fkc.referenced_object_id
                JOIN {db}.sys.schemas rs ON rs.schema_id = rt.schema_id
                JOIN {db}.sys.columns rc
                  ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
                WHERE fk.parent_object_id = OBJECT_ID(@name)
                ORDER BY fk.name, fkc.constraint_column_id
                """;

            using (var command = new SqlCommand(keySql, connection) { CommandTimeout = timeoutSeconds })
            {
                command.Parameters.AddWithValue("@name", qualified);
                using SqlDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    string name = reader.GetString(0);
                    if (!keys.TryGetValue(name, out var entry))
                    {
                        entry = ([], reader.GetString(2), reader.GetString(3), []);
                        keys[name] = entry;
                    }
                    entry.Columns.Add(reader.GetString(1));
                    entry.RefColumns.Add(reader.GetString(4));
                }
            }

            // An index naming a hidden column is absent whole - see
            // Described's summary for why absence beats summarising.
            var shownIndexes = new List<IndexFact>();
            foreach ((string name, (bool unique, bool primary, List<string> columns)) in indexes)
                if (columns.All(on.HasColumn))
                    shownIndexes.Add(new IndexFact(name, unique, primary, columns));

            var shownKeys = new List<ForeignKeyFact>();
            foreach ((string name, (List<string> columns, string refSchema, string refTable,
                List<string> refColumns)) in keys)
            {
                if (!columns.All(on.HasColumn)) continue;

                // The target is named only when this caller could reach
                // it: an undeclared and a hidden target elide the same
                // way, or the elision itself would be a probe.
                string? target = null;
                IReadOnlyList<string> targetColumns = [];

                (Verb can, _, ColumnRule ruled, _) = on.Database.At(refSchema, refTable);
                if (can.HasFlag(Verb.List))
                {
                    bool allShown = refColumns.All(ruled.Allows);
                    if (allShown)
                    {
                        target = $"{on.Database.Name}.{refSchema}.{refTable}";
                        targetColumns = refColumns;
                    }
                }

                shownKeys.Add(new ForeignKeyFact(name, columns, target, targetColumns));
            }

            return Result<(IReadOnlyList<IndexFact>, IReadOnlyList<ForeignKeyFact>)>.Ok(
                (shownIndexes, shownKeys));
        }
        catch (SqlException e)
        {
            return Result<(IReadOnlyList<IndexFact>, IReadOnlyList<ForeignKeyFact>)>.Fail(
                Declined(on.Server, e));
        }
    }
}
