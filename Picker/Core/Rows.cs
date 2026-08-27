namespace Picker.Core;

/// <summary>What kind of value a cell holds, so a front end can write
/// it as the JSON type it is rather than as a string wearing quotes.</summary>
public enum CellKind
{
    Null,
    Boolean,
    Integer,
    Decimal,
    Float,
    Text,
    DateTime,
    Binary,
    Other,
}

/// <summary>One value, already rendered to text, with the kind that says
/// how a front end should carry it.</summary>
public sealed record Cell(CellKind Kind, string? Text);

/// <summary>One result column: name, declared type, nullability - stated
/// with the rows so the answer is usable without a second trip.</summary>
public sealed record ColumnFact(string Name, string Type, bool Nullable);

/// <summary>
/// A result set, bounded and honest about it: <see cref="Truncated"/>
/// says rows were withheld, and the remedy - TOP, WHERE, OFFSET - is the
/// caller's.
/// </summary>
public sealed record Table(
    IReadOnlyList<ColumnFact> Columns,
    IReadOnlyList<IReadOnlyList<Cell>> Rows,
    bool Truncated);

/// <summary>One index, as DESCRIBE reports it.</summary>
public sealed record IndexFact(
    string Name, bool Unique, bool PrimaryKey, IReadOnlyList<string> Columns);

/// <summary>One foreign key. <see cref="Target"/> is null when the
/// referenced object is not there for this caller - undeclared or
/// hidden, which are the same fact - and the relationship is reported
/// as present with its target elided.</summary>
public sealed record ForeignKeyFact(
    string Name, IReadOnlyList<string> Columns, string? Target,
    IReadOnlyList<string> TargetColumns);

/// <summary>
/// What DESCRIBE answers: generated from the catalog, filtered by the
/// column scope, and never the stored text of anything.
///
/// <para><b>An index or key that names a hidden column is absent
/// whole.</b> The requirements allow summarising such a constraint
/// without naming the column, but a summary that says "one column not
/// shown" says a hidden column exists - so absence, the stricter
/// reading, is what ships.</para>
/// </summary>
public sealed record Described(
    string Display,
    bool IsView,
    IReadOnlyList<ColumnFact> Columns,
    IReadOnlyList<IndexFact> Indexes,
    IReadOnlyList<ForeignKeyFact> ForeignKeys,
    string Hash);

/// <summary>One row of an objects listing.</summary>
public sealed record ListedObject(
    string Database, string Schema, string Name, bool IsView, int Columns, Verb Can);
