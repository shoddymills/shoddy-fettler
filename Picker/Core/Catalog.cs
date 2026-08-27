using System.Security.Cryptography;
using System.Text;

namespace Picker.Core;

/// <summary>One column, as the catalog states it.</summary>
public sealed record CatalogColumn(
    string Name, string Type, bool Nullable, int Ordinal);

/// <summary>One table or view, as the catalog states it. Nothing else -
/// no procedures, no functions, no synonyms - because nothing else is
/// selectable or describable here.</summary>
public sealed record CatalogObject(
    string Schema, string Name, bool IsView, IReadOnlyList<CatalogColumn> Columns);

/// <summary>
/// One database's tables and views, read at one moment, with a hash
/// that names that moment.
///
/// <para>The hash is the read-hash discipline applied to schemas: a
/// describe answer carries it so a later call can say "unchanged"
/// cheaply, and the query rewrite is versioned by it so a stale
/// expansion refetches rather than executing.</para>
/// </summary>
public sealed record CatalogSnapshot(
    string Database, IReadOnlyList<CatalogObject> Objects, string Hash)
{
    public CatalogObject? Find(string schema, string name)
    {
        foreach (CatalogObject o in Objects)
            if (o.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)
                && o.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return o;
        return null;
    }

    /// <summary>Objects whose bare name matches, in any schema - what a
    /// one-part name has to work with.</summary>
    public IEnumerable<CatalogObject> Named(string name)
    {
        foreach (CatalogObject o in Objects)
            if (o.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                yield return o;
    }

    /// <summary>The hash of a set of catalog rows, stable across
    /// orderings the server does not promise.</summary>
    public static string HashOf(IEnumerable<CatalogObject> objects)
    {
        var text = new StringBuilder();
        foreach (CatalogObject o in objects
            .OrderBy(o => o.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase))
        {
            text.Append(o.Schema).Append('.').Append(o.Name)
                .Append(o.IsView ? ":view" : ":table").Append('\n');
            foreach (CatalogColumn c in o.Columns.OrderBy(c => c.Ordinal))
                text.Append("  ").Append(c.Name).Append(' ').Append(c.Type)
                    .Append(c.Nullable ? " null" : " not-null").Append('\n');
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}

/// <summary>
/// Where catalog snapshots come from.
///
/// <para><b>An interface for the reason IScreener is one</b>: the grant
/// matrix, the resolver and the whole query gate must be provable
/// without a server in the room. The SQL implementation lives in the
/// data layer; the tests hand in a catalog made of literals.</para>
/// </summary>
public interface ICatalogSource
{
    Task<Result<CatalogSnapshot>> LoadAsync(
        ServerDecl server, DatabaseDecl database, CancellationToken cancel);
}
