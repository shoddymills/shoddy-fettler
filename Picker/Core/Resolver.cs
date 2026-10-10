namespace Picker.Core;

/// <summary>
/// One object, resolved: where it is, what the catalog says it is, and
/// what the grant says may be done with it.
/// </summary>
public sealed record ResolvedObject(
    ServerDecl Server, DatabaseDecl Database, CatalogObject Object,
    Verb Can, Screened Screen, ColumnRule Columns)
{
    public string Display => $"{Database.Name}.{Object.Schema}.{Object.Name}";

    /// <summary>The columns that exist for this caller, in catalog
    /// order. Everything outside this list is not hidden but absent -
    /// including from DESCRIBE, including from <c>*</c>.</summary>
    public IReadOnlyList<CatalogColumn> Permitted =>
        [.. Object.Columns.Where(c => Columns.Allows(c.Name)).OrderBy(c => c.Ordinal)];

    /// <summary>Whether the object HAS this column and the grant lets it
    /// exist. One question, because the two must never answer
    /// differently worded refusals.</summary>
    public bool HasColumn(string name) =>
        Find(name) is not null && Columns.Allows(name);

    /// <summary>Whether the object has this column at all - permitted or
    /// hidden. The gate needs the difference internally (a hidden name
    /// must refuse rather than reach the server), but no message path
    /// may distinguish them.</summary>
    public bool CarriesColumn(string name) => Find(name) is not null;

    CatalogColumn? Find(string name)
    {
        foreach (CatalogColumn c in Object.Columns)
            if (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return c;
        return null;
    }
}

/// <summary>
/// Turning the names callers type into resolved objects, through the
/// grant.
///
/// <para><b>Hidden is indistinguishable from absent, and this class is
/// where that is enforced.</b> An object without <see cref="Verb.List"/>
/// is skipped by every search and refused by every direct naming with
/// <see cref="Names.NotThere"/> - the same factory, the same words, as
/// an object that was never there. An undeclared database goes the same
/// way, which is why there is no OutsideRoot here.</para>
/// </summary>
public sealed class Resolver
{
    readonly Boundary boundary;
    readonly ICatalogSource source;
    readonly Dictionary<string, CatalogSnapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);
    readonly Lock gate = new();

    public Resolver(Boundary boundary, ICatalogSource source)
    {
        this.boundary = boundary;
        this.source = source;
    }

    public Boundary Boundary => boundary;

    IReadOnlyList<(ServerDecl Server, DatabaseDecl Database)>? surface;

    /// <summary>
    /// Every database there is for this call: the declared ones, and for
    /// an open server, the ones the login can reach.
    ///
    /// <para>Read once per resolver, which lives for one request, so a
    /// database created on the server shows on the next call. An open
    /// database gets <see cref="ServerDecl.OpenGrant"/> and the screen
    /// <c>screenings</c> names for it. A source that cannot list
    /// databases leaves an open server with none.</para>
    /// </summary>
    public async Task<Result<IReadOnlyList<(ServerDecl Server, DatabaseDecl Database)>>> DatabasesAsync(
        CancellationToken cancel)
    {
        lock (gate)
        {
            if (surface is not null)
                return Result<IReadOnlyList<(ServerDecl, DatabaseDecl)>>.Ok(surface);
        }

        var found = new List<(ServerDecl, DatabaseDecl)>();
        foreach (ServerDecl server in boundary.Servers)
        {
            if (!server.Open)
            {
                foreach (DatabaseDecl database in server.Databases) found.Add((server, database));
                continue;
            }

            if (source is not IDatabaseSource lister) continue;

            Result<IReadOnlyList<string>> listed = await lister.DatabasesAsync(server, cancel).ConfigureAwait(false);
            if (!listed.IsOk) return listed.Carry<IReadOnlyList<(ServerDecl, DatabaseDecl)>>();

            foreach (string name in listed.Value)
            {
                Screened screen = boundary.OpenScreens.TryGetValue(name, out Screened s) ? s : Screened.None;
                found.Add((server, new DatabaseDecl(name, ServerDecl.OpenGrant, screen, [])));
            }
        }

        lock (gate)
        {
            surface = found;
        }

        return Result<IReadOnlyList<(ServerDecl, DatabaseDecl)>>.Ok(found);
    }

    /// <summary>
    /// The snapshot for one declared database, read once per resolver
    /// and held. A resolver lives for one request in the serve path -
    /// the boundary is re-read per call - so "cached" here means "read
    /// once while answering this call", never "stale across edits".
    /// </summary>
    public async Task<Result<CatalogSnapshot>> SnapshotAsync(
        ServerDecl server, DatabaseDecl database, CancellationToken cancel)
    {
        string key = $"{server.Name}\n{database.Name}";

        lock (gate)
        {
            if (snapshots.TryGetValue(key, out CatalogSnapshot? held))
                return Result<CatalogSnapshot>.Ok(held);
        }

        Result<CatalogSnapshot> read = await source
            .LoadAsync(server, database, cancel).ConfigureAwait(false);
        if (!read.IsOk) return read;

        lock (gate)
        {
            snapshots[key] = read.Value;
        }

        return read;
    }

    /// <summary>
    /// Resolve a typed name to one object the caller may reach with
    /// <paramref name="needed"/>.
    /// </summary>
    public async Task<Result<ResolvedObject>> ResolveAsync(
        ObjectName name, Verb needed, CancellationToken cancel)
    {
        if (Names.IsSystemSchema(name.Schema) || Names.IsSystemSchema(name.Database))
            return Result<ResolvedObject>.Fail(Names.SystemCatalog(name.Typed));

        return name.Database is not null
            ? await Qualified(name, needed, cancel).ConfigureAwait(false)
            : await Searched(name, needed, cancel).ConfigureAwait(false);
    }

    async Task<Result<ResolvedObject>> Qualified(
        ObjectName name, Verb needed, CancellationToken cancel)
    {
        var all = await DatabasesAsync(cancel).ConfigureAwait(false);
        if (!all.IsOk) return all.Carry<ResolvedObject>();

        var declared = new List<(ServerDecl Server, DatabaseDecl Database)>();
        foreach ((ServerDecl server, DatabaseDecl database) in all.Value)
            if (database.Name.Equals(name.Database, StringComparison.OrdinalIgnoreCase))
                declared.Add((server, database));

        // An undeclared database and a missing table answer in the SAME
        // words: a three-part name that leaves the surface is not a
        // different kind of wrong, because telling them apart is how a
        // caller maps the server.
        if (declared.Count == 0)
            return Result<ResolvedObject>.Fail(Names.NotThere(name.Typed));

        // The declared name IS the name the rewrite sends to the server,
        // so "rename one of them" is no remedy - a renamed declaration
        // would name a database the server does not have. One
        // configuration reaches one of the two.
        if (declared.Count > 1)
            return Result<ResolvedObject>.Fail(Outcome.Invalid,
                $"'{name.Database}' is declared on more than one server ("
                + string.Join(", ", declared.Select(d => d.Server.Name))
                + "). Declare it on one server in this configuration",
                name.Typed);

        (ServerDecl on, DatabaseDecl db) = declared[0];
        string schema = name.Schema ?? "dbo";

        Result<CatalogSnapshot> snapshot = await SnapshotAsync(on, db, cancel).ConfigureAwait(false);
        if (!snapshot.IsOk) return snapshot.Carry<ResolvedObject>();

        CatalogObject? found = snapshot.Value.Find(schema, name.Name);
        if (found is null)
            return Result<ResolvedObject>.Fail(Names.NotThere(name.Typed));

        return Grant(on, db, found, name.Typed, needed);
    }

    async Task<Result<ResolvedObject>> Searched(
        ObjectName name, Verb needed, CancellationToken cancel)
    {
        var all = await DatabasesAsync(cancel).ConfigureAwait(false);
        if (!all.IsOk) return all.Carry<ResolvedObject>();

        var candidates = new List<ResolvedObject>();

        foreach ((ServerDecl server, DatabaseDecl database) in all.Value)
        {
            Result<CatalogSnapshot> snapshot =
                await SnapshotAsync(server, database, cancel).ConfigureAwait(false);

            // An unqualified name is resolved by proving uniqueness
            // across EVERY declared database, so a database that cannot
            // answer stops the proof - resolving against the ones that
            // could would quietly bind the name to the wrong place the
            // day the dead one comes back.
            if (!snapshot.IsOk) return snapshot.Carry<ResolvedObject>();

            IEnumerable<CatalogObject> matches = name.Schema is not null
                ? snapshot.Value.Find(name.Schema, name.Name) is { } one ? [one] : []
                : snapshot.Value.Named(name.Name);

            foreach (CatalogObject match in matches)
            {
                (Verb can, Screened screen, ColumnRule columns, _) =
                    database.At(match.Schema, match.Name);

                // Hidden objects are not candidates: they do not exist,
                // so they neither resolve nor make a name ambiguous.
                if (!can.HasFlag(Verb.List)) continue;

                candidates.Add(new ResolvedObject(server, database, match, can, screen, columns));
            }
        }

        if (candidates.Count == 0)
            return Result<ResolvedObject>.Fail(Names.NotThere(name.Typed));

        // Two candidates is a refusal naming both, never a guess - and
        // both are listable, so naming them discloses nothing.
        if (candidates.Count > 1)
            return Result<ResolvedObject>.Fail(Outcome.Invalid,
                $"'{name.Typed}' is in more than one place: "
                + string.Join(", ", candidates.Select(c => c.Display))
                + ". Qualify it", name.Typed);

        return Check(candidates[0], name.Typed, needed);
    }

    Result<ResolvedObject> Grant(
        ServerDecl server, DatabaseDecl database, CatalogObject found,
        string typed, Verb needed)
    {
        (Verb can, Screened screen, ColumnRule columns, _) =
            database.At(found.Schema, found.Name);

        return Check(
            new ResolvedObject(server, database, found, can, screen, columns), typed, needed);
    }

    static Result<ResolvedObject> Check(ResolvedObject resolved, string typed, Verb needed)
    {
        // No list means hidden, and hidden means the not-there words -
        // whatever else the caller asked for.
        if (!resolved.Can.HasFlag(Verb.List))
            return Result<ResolvedObject>.Fail(Names.NotThere(typed));

        if (!resolved.Can.HasFlag(needed))
            return Result<ResolvedObject>.Fail(Outcome.Refused,
                $"{Verbs.NameOf(needed)} is not allowed on {resolved.Display}. "
                + $"It allows: {Verbs.Write(resolved.Can)}", typed);

        return Result<ResolvedObject>.Ok(resolved);
    }
}
