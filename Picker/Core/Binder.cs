using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Picker.Core;

/// <summary>What binding proved: every base object the statement
/// reaches, with the AST already rewritten in place.</summary>
public sealed record Bound(IReadOnlyList<ResolvedObject> Objects);

/// <summary>
/// Binding: every object reference resolved through the grant, every
/// column reference proven to exist FOR THIS CALLER, every <c>*</c>
/// expanded to the columns that do, and every object name rewritten to
/// its three-part form so the connection's default database decides
/// nothing.
///
/// <para><b>A hidden column refuses wherever it appears</b> - select
/// list, WHERE, JOIN ON, ORDER BY, inside an expression - in the same
/// words as a column that was never there. Checking the output schema
/// alone was rejected in the requirements: <c>WHERE ssn = '...'</c>
/// discloses through behaviour, and <c>ssn + '' AS x</c> slips a schema
/// check.</para>
///
/// <para><b><c>SELECT *</c> expands rather than refuses.</b> Hidden
/// means not there, so <c>*</c> over what-is-there is the honest
/// reading: the expansion happens here, against the catalog, and the
/// response states its column list as every response does.</para>
///
/// <para><b>Unresolvable means refused.</b> A qualifier that names
/// nothing, a column that resolves nowhere, a construct the walk cannot
/// account for - each refuses the query rather than letting the server
/// decide, because the server's own error messages know nothing about
/// hidden columns and would happily disclose their existence.</para>
/// </summary>
public static class Binder
{
    public static async Task<Result<Bound>> BindAsync(
        SelectStatement select, Resolver resolver, CancellationToken cancel)
    {
        var work = new Work(resolver, cancel);

        // CTEs first, in order, so later ones (and the main query) can
        // reference earlier ones.
        if (select.WithCtesAndXmlNamespaces is { } with)
            foreach (CommonTableExpression cte in with.CommonTableExpressions)
            {
                Result<IReadOnlyList<string?>> made =
                    await work.ProcessCte(cte).ConfigureAwait(false);
                if (!made.IsOk) return made.Carry<Bound>();
            }

        Result<IReadOnlyList<string?>> outputs = await work
            .ProcessQuery(select.QueryExpression, Scopes.None).ConfigureAwait(false);
        if (!outputs.IsOk) return outputs.Carry<Bound>();

        return Result<Bound>.Ok(new Bound(work.Touched));
    }

    // ---- the scope model ----

    /// <summary>One thing standing in a FROM: a base object under the
    /// grant, or a derived shape - subquery, VALUES, CTE - whose columns
    /// are the names its projection exposes.</summary>
    sealed class Entry
    {
        public string? Alias;
        public ResolvedObject? Base;
        public IReadOnlyList<string?>? Derived;
        public string Display = "";

        /// <summary>The exposed name parts when there is no alias:
        /// database, schema, object - so a qualifier written as any
        /// suffix of that still finds it after the three-part rewrite.</summary>
        public IReadOnlyList<string> Exposed = [];

        public bool AnswersTo(IReadOnlyList<string> qualifier)
        {
            if (Alias is not null)
                return qualifier.Count == 1
                    && qualifier[0].Equals(Alias, StringComparison.OrdinalIgnoreCase);

            if (qualifier.Count > Exposed.Count) return false;
            for (int i = 0; i < qualifier.Count; i++)
                if (!qualifier[^(1 + i)].Equals(Exposed[^(1 + i)], StringComparison.OrdinalIgnoreCase))
                    return false;
            return qualifier.Count > 0;
        }

        public bool CarriesDerived(string column)
        {
            if (Derived is null) return false;
            foreach (string? name in Derived)
                if (name is not null && name.Equals(column, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }

    /// <summary>An immutable chain of scopes, innermost first, so a
    /// correlated subquery sees its outer query without the outer ever
    /// seeing in.</summary>
    sealed class Scopes
    {
        public static readonly Scopes None = new(null, null);

        Scopes(List<Entry>? entries, Scopes? outer)
        {
            Entries = entries ?? [];
            Outer = outer;
        }

        public List<Entry> Entries { get; }
        public Scopes? Outer { get; }

        public Scopes Push() => new([], this);
    }

    // ---- the walk ----

    sealed class Work
    {
        readonly Resolver resolver;
        readonly CancellationToken cancel;
        readonly Dictionary<string, IReadOnlyList<string?>> ctes = new(StringComparer.OrdinalIgnoreCase);
        readonly List<ResolvedObject> touched = [];

        public Work(Resolver resolver, CancellationToken cancel)
        {
            this.resolver = resolver;
            this.cancel = cancel;
        }

        public IReadOnlyList<ResolvedObject> Touched => touched;

        public async Task<Result<IReadOnlyList<string?>>> ProcessCte(CommonTableExpression cte)
        {
            string name = cte.ExpressionName.Value;

            // A recursive CTE references itself before its shape is
            // known, which only works when the columns are stated - and
            // T-SQL requires exactly that of a recursive CTE anyway.
            if (cte.Columns is { Count: > 0 })
                ctes[name] = [.. cte.Columns.Select(c => (string?)c.Value)];

            Result<IReadOnlyList<string?>> made =
                await ProcessQuery(cte.QueryExpression, Scopes.None).ConfigureAwait(false);
            if (!made.IsOk) return made;

            if (cte.Columns is not { Count: > 0 }) ctes[name] = made.Value;
            return made;
        }

        public async Task<Result<IReadOnlyList<string?>>> ProcessQuery(
            QueryExpression query, Scopes outer)
        {
            switch (query)
            {
                case QuerySpecification spec:
                    return await ProcessSpec(spec, outer).ConfigureAwait(false);

                case QueryParenthesisExpression paren:
                {
                    Result<IReadOnlyList<string?>> inner =
                        await ProcessQuery(paren.QueryExpression, outer).ConfigureAwait(false);
                    if (!inner.IsOk) return inner;

                    Failure? sorted = CheckSetOrderBy(paren, inner.Value);
                    return sorted is null ? inner : Result<IReadOnlyList<string?>>.Fail(sorted);
                }

                case BinaryQueryExpression pair:
                {
                    Result<IReadOnlyList<string?>> first =
                        await ProcessQuery(pair.FirstQueryExpression, outer).ConfigureAwait(false);
                    if (!first.IsOk) return first;

                    Result<IReadOnlyList<string?>> second =
                        await ProcessQuery(pair.SecondQueryExpression, outer).ConfigureAwait(false);
                    if (!second.IsOk) return second;

                    // A set operation's ORDER BY refers to the FIRST
                    // arm's output names - the T-SQL rule.
                    Failure? sorted = CheckSetOrderBy(pair, first.Value);
                    return sorted is null ? first : Result<IReadOnlyList<string?>>.Fail(sorted);
                }

                default:
                    return Result<IReadOnlyList<string?>>.Fail(Outcome.Refused,
                        $"this query uses {query.GetType().Name}, a shape this gate does "
                        + "not know how to check - and what is not checked does not run");
            }
        }

        /// <summary>An ORDER BY on a set operation or a parenthesised
        /// query can only name output columns, so it is checked against
        /// those - by name, with nothing else in scope.</summary>
        static Failure? CheckSetOrderBy(QueryExpression query, IReadOnlyList<string?> outputs)
        {
            if (query.OrderByClause is not { } sort) return null;

            foreach (ExpressionWithSortOrder element in sort.OrderByElements)
            {
                if (element.Expression is IntegerLiteral) continue;      // ORDER BY 1

                if (element.Expression is not ColumnReferenceExpression column
                    || column.MultiPartIdentifier is not { Identifiers.Count: 1 } name)
                    return new Failure(Outcome.Refused,
                        "an ORDER BY on a set operation can only name an output column");

                string word = name.Identifiers[0].Value;
                bool known = outputs.Any(o =>
                    o is not null && o.Equals(word, StringComparison.OrdinalIgnoreCase));
                if (!known)
                    return new Failure(Outcome.Refused,
                        $"'{word}' is not a column this set operation outputs");
            }

            return null;
        }

        async Task<Result<IReadOnlyList<string?>>> ProcessSpec(
            QuerySpecification spec, Scopes outer)
        {
            Scopes scope = outer.Push();

            // 1. The FROM, building the scope and rewriting names.
            if (spec.FromClause is { } from)
                foreach (TableReference reference in from.TableReferences)
                {
                    Failure? walked = await WalkTable(reference, scope).ConfigureAwait(false);
                    if (walked is not null) return Result<IReadOnlyList<string?>>.Fail(walked);
                }

            // 2. Stars expanded against the scope, before anything else
            // reads the select list.
            Failure? expanded = ExpandStars(spec, scope);
            if (expanded is not null) return Result<IReadOnlyList<string?>>.Fail(expanded);

            // 3. The output names this projection exposes.
            var outputs = new List<string?>();
            foreach (SelectElement element in spec.SelectElements)
                if (element is SelectScalarExpression scalar)
                    outputs.Add(scalar.ColumnName?.Value
                        ?? (scalar.Expression as ColumnReferenceExpression)
                            ?.MultiPartIdentifier?.Identifiers[^1].Value);

            // 4. Every column reference in this spec - clauses, ON
            // conditions, expressions - collected without descending
            // into subqueries, which are processed with their own scope.
            var collector = new Collector();
            spec.Accept(collector);

            foreach (ColumnReferenceExpression column in collector.Columns)
            {
                Failure? checked_ = CheckColumn(column, spec, scope, outputs);
                if (checked_ is not null) return Result<IReadOnlyList<string?>>.Fail(checked_);
            }

            foreach (FunctionCall function in collector.Functions)
            {
                Failure? called = CheckFunction(function);
                if (called is not null) return Result<IReadOnlyList<string?>>.Fail(called);
            }

            // 5. Subqueries, correlated: they see this scope and its
            // outers, and nothing sees into them.
            foreach (ScalarSubquery subquery in collector.Subqueries)
            {
                Result<IReadOnlyList<string?>> inner =
                    await ProcessQuery(subquery.QueryExpression, scope).ConfigureAwait(false);
                if (!inner.IsOk) return inner;
            }

            return Result<IReadOnlyList<string?>>.Ok(outputs);
        }

        // ---- FROM ----

        async Task<Failure?> WalkTable(TableReference reference, Scopes scope)
        {
            switch (reference)
            {
                case NamedTableReference named:
                    return await WalkNamed(named, scope).ConfigureAwait(false);

                case QueryDerivedTable derived:
                {
                    // The derived table sees the scope chain as it
                    // stands, siblings included - a shade wider than
                    // T-SQL grants a plain derived table, and exactly
                    // what an APPLY's right side needs. The width is
                    // safe: every name it lets through was proven
                    // in-grant, and the server still refuses the
                    // correlations T-SQL does not have.
                    Result<IReadOnlyList<string?>> inner =
                        await ProcessQuery(derived.QueryExpression, scope).ConfigureAwait(false);
                    if (!inner.IsOk) return inner.Failure;

                    IReadOnlyList<string?> columns = derived.Columns is { Count: > 0 }
                        ? [.. derived.Columns.Select(c => (string?)c.Value)]
                        : inner.Value;

                    scope.Entries.Add(new Entry
                    {
                        Alias = derived.Alias?.Value,
                        Derived = columns,
                        Display = derived.Alias?.Value ?? "a derived table",
                    });
                    return null;
                }

                case InlineDerivedTable values:
                {
                    scope.Entries.Add(new Entry
                    {
                        Alias = values.Alias?.Value,
                        Derived = [.. values.Columns.Select(c => (string?)c.Value)],
                        Display = values.Alias?.Value ?? "a VALUES table",
                    });
                    return null;
                }

                case QualifiedJoin join:
                {
                    Failure? left = await WalkTable(join.FirstTableReference, scope).ConfigureAwait(false);
                    if (left is not null) return left;
                    return await WalkTable(join.SecondTableReference, scope).ConfigureAwait(false);
                }

                case UnqualifiedJoin join:
                {
                    Failure? left = await WalkTable(join.FirstTableReference, scope).ConfigureAwait(false);
                    if (left is not null) return left;
                    return await WalkTable(join.SecondTableReference, scope).ConfigureAwait(false);
                }

                case JoinParenthesisTableReference paren:
                    return await WalkTable(paren.Join, scope).ConfigureAwait(false);

                default:
                    return new Failure(Outcome.Refused,
                        $"this query reads from {reference.GetType().Name}, a source this "
                        + "gate does not know how to check - and what is not checked does not run");
            }
        }

        async Task<Failure?> WalkNamed(NamedTableReference named, Scopes scope)
        {
            SchemaObjectName name = named.SchemaObject;

            if (name.ServerIdentifier is not null || name.Identifiers.Count > 3)
            {
                string typed = string.Join(".", name.Identifiers.Select(i => i.Value));
                return new Failure(Outcome.Refused,
                    $"'{typed}' has four parts, and a four-part name leaves the declared "
                    + "surface: a linked server is not reachable from here", typed);
            }

            var parts = new List<string>();
            foreach (Identifier identifier in name.Identifiers) parts.Add(identifier.Value);
            string display = string.Join(".", parts);

            // A one-part name that matches a CTE is that CTE - the T-SQL
            // precedence - and never reaches the grant.
            if (parts.Count == 1 && ctes.TryGetValue(parts[0], out IReadOnlyList<string?>? cte))
            {
                scope.Entries.Add(new Entry
                {
                    Alias = named.Alias?.Value ?? parts[0],
                    Derived = cte,
                    Display = parts[0],
                });
                return null;
            }

            ObjectName asked = parts.Count switch
            {
                1 => new ObjectName(null, null, parts[0], display),
                2 => new ObjectName(null, parts[0], parts[1], display),
                _ => new ObjectName(parts[0], parts[1], parts[2], display),
            };

            Result<ResolvedObject> resolved =
                await resolver.ResolveAsync(asked, Verb.Select, cancel).ConfigureAwait(false);
            if (!resolved.IsOk) return resolved.Failure;

            touched.Add(resolved.Value);

            // The rewrite: three parts, bracket-quoted, so the
            // connection's default database decides nothing and a
            // creative name smuggles no syntax.
            name.Identifiers.Clear();
            name.Identifiers.Add(Bracketed(resolved.Value.Database.Name));
            name.Identifiers.Add(Bracketed(resolved.Value.Object.Schema));
            name.Identifiers.Add(Bracketed(resolved.Value.Object.Name));

            scope.Entries.Add(new Entry
            {
                Alias = named.Alias?.Value,
                Base = resolved.Value,
                Display = resolved.Value.Display,
                Exposed =
                [
                    resolved.Value.Database.Name,
                    resolved.Value.Object.Schema,
                    resolved.Value.Object.Name,
                ],
            });
            return null;
        }

        // ---- stars ----

        Failure? ExpandStars(QuerySpecification spec, Scopes scope)
        {
            var rewritten = new List<SelectElement>();
            bool any = false;

            foreach (SelectElement element in spec.SelectElements)
            {
                if (element is not SelectStarExpression star)
                {
                    rewritten.Add(element);
                    continue;
                }

                any = true;

                List<Entry> targets;
                if (star.Qualifier is { Identifiers.Count: > 0 } qualifier)
                {
                    var parts = new List<string>();
                    foreach (Identifier identifier in qualifier.Identifiers)
                        parts.Add(identifier.Value);

                    Entry? found = scope.Entries.Find(e => e.AnswersTo(parts));
                    if (found is null)
                        return new Failure(Outcome.Refused,
                            $"'{string.Join(".", parts)}.*' does not name anything in this "
                            + "query's FROM");
                    targets = [found];
                }
                else
                {
                    targets = scope.Entries;
                }

                if (targets.Count == 0)
                    return new Failure(Outcome.Refused,
                        "SELECT * with nothing in FROM selects nothing this gate can name");

                int before = rewritten.Count;

                foreach (Entry entry in targets)
                {
                    if (entry.Base is { } based)
                    {
                        foreach (CatalogColumn column in based.Permitted)
                            rewritten.Add(Column(entry, column.Name));
                        continue;
                    }

                    foreach (string? column in entry.Derived!)
                    {
                        if (column is null)
                            return new Failure(Outcome.Refused,
                                $"selecting * over {entry.Display} needs every column named; "
                                + "give the unnamed expression a name (AS ...) first");
                        rewritten.Add(Column(entry, column));
                    }
                }

                if (rewritten.Count == before)
                    return new Failure(Outcome.Refused,
                        "no columns exist to select here; the * expands to nothing");
            }

            if (!any) return null;

            spec.SelectElements.Clear();
            foreach (SelectElement element in rewritten) spec.SelectElements.Add(element);
            return null;
        }

        /// <summary>One expanded column: qualified by the alias when
        /// there is one, and by the full three-part name when there is
        /// not - a four-part column reference is valid T-SQL, and it is
        /// the only qualifier that still exists after the rewrite.</summary>
        static SelectScalarExpression Column(Entry entry, string column)
        {
            var name = new MultiPartIdentifier();

            if (entry.Alias is { } alias)
            {
                name.Identifiers.Add(Bracketed(alias));
            }
            else
            {
                foreach (string part in entry.Exposed)
                    name.Identifiers.Add(Bracketed(part));
            }

            name.Identifiers.Add(Bracketed(column));

            return new SelectScalarExpression
            {
                Expression = new ColumnReferenceExpression
                {
                    ColumnType = ColumnType.Regular,
                    MultiPartIdentifier = name,
                },
            };
        }

        static Identifier Bracketed(string value) =>
            new() { Value = value, QuoteType = QuoteType.SquareBracket };

        // ---- columns ----

        Failure? CheckColumn(
            ColumnReferenceExpression column, QuerySpecification spec,
            Scopes scope, IReadOnlyList<string?> outputs)
        {
            if (column.MultiPartIdentifier is not { Identifiers.Count: > 0 } name)
                return null;

            var parts = new List<string>();
            foreach (Identifier identifier in name.Identifiers) parts.Add(identifier.Value);

            string word = parts[^1];

            // An ORDER BY may name an output column - the one place a
            // bare name can be something no table carries.
            if (parts.Count == 1 && Within(spec.OrderByClause, column)
                && outputs.Any(o => o is not null && o.Equals(word, StringComparison.OrdinalIgnoreCase)))
                return null;

            return parts.Count == 1
                ? CheckBare(word, scope)
                : CheckQualified(parts[..^1], word, scope);
        }

        static bool Within(TSqlFragment? clause, TSqlFragment node) =>
            clause is not null
            && node.StartOffset >= clause.StartOffset
            && node.StartOffset < clause.StartOffset + clause.FragmentLength;

        static Failure? CheckBare(string word, Scopes scope)
        {
            for (Scopes? level = scope; level is not null; level = level.Outer)
            {
                var permitted = new List<Entry>();
                bool hidden = false;

                foreach (Entry entry in level.Entries)
                {
                    if (entry.Base is { } based)
                    {
                        if (based.HasColumn(word)) permitted.Add(entry);
                        else if (based.CarriesColumn(word)) hidden = true;
                    }
                    else if (entry.CarriesDerived(word))
                    {
                        permitted.Add(entry);
                    }
                }

                // A name that is permitted somewhere and hidden somewhere
                // else in the same scope must be qualified: letting the
                // server bind it would end in the server's own ambiguity
                // error, which knows nothing about hiding and would
                // disclose what this gate exists to keep unprobeable.
                if (permitted.Count > 0 && hidden)
                    return new Failure(Outcome.Refused,
                        $"qualify '{word}': more than one table in this query could carry it");

                if (permitted.Count > 0) return null;

                // A hidden name stops the walk exactly where the server
                // would have bound it - letting it fall through to an
                // outer scope would check a different column than the
                // one the server reads. The refusal is the SAME one an
                // absent name earns below, naming no object, so the two
                // cannot be told apart from outside.
                if (hidden) return Names.NoColumnAnywhere(word);
            }

            return Names.NoColumnAnywhere(word);
        }

        static Failure? CheckQualified(List<string> qualifier, string word, Scopes scope)
        {
            for (Scopes? level = scope; level is not null; level = level.Outer)
                foreach (Entry entry in level.Entries)
                {
                    if (!entry.AnswersTo(qualifier)) continue;

                    if (entry.Base is { } based)
                        return based.HasColumn(word)
                            ? null
                            : Names.NoSuchColumn(word, based.Display);

                    return entry.CarriesDerived(word)
                        ? null
                        : Names.NoSuchColumn(word, entry.Display);
                }

            return new Failure(Outcome.Refused,
                $"'{string.Join(".", qualifier)}' does not name anything in this query's "
                + $"FROM, so '{word}' cannot be checked - and what is not checked does not run");
        }

        // ---- functions ----

        /// <summary>Metadata leaks through builtin functions: each of
        /// these answers questions about objects the grant may hide, so
        /// they are refused with the system-catalog reason. A
        /// schema-qualified call is a user function, and a user function
        /// reads whatever IT can see - which is the login's view, not
        /// this caller's - so those are refused whole.</summary>
        static readonly HashSet<string> CatalogFunctions = new(StringComparer.OrdinalIgnoreCase)
        {
            "OBJECT_DEFINITION", "OBJECT_NAME", "OBJECT_ID", "OBJECT_SCHEMA_NAME",
            "OBJECTPROPERTY", "OBJECTPROPERTYEX", "SCHEMA_NAME", "SCHEMA_ID",
            "DB_NAME", "DB_ID", "ORIGINAL_DB_NAME", "COL_NAME", "COLUMNPROPERTY",
            "INDEXPROPERTY", "INDEX_COL", "FILE_NAME", "FILEGROUP_NAME",
            "TYPE_NAME", "TYPE_ID", "IDENT_CURRENT", "SERVERPROPERTY",
            "DATABASEPROPERTYEX", "HAS_PERMS_BY_NAME", "PERMISSIONS",
        };

        static Failure? CheckFunction(FunctionCall function)
        {
            if (function.CallTarget is not null)
                return new Failure(Outcome.Refused,
                    "a schema-qualified call is a user function, and a user function reads "
                    + "with the LOGIN's eyes rather than through this grant; only builtin "
                    + "functions run here");

            string name = function.FunctionName.Value;
            if (CatalogFunctions.Contains(name))
                return new Failure(Outcome.Refused,
                    $"{name.ToUpperInvariant()} reaches the system catalog, which is not "
                    + "queryable here: it would disclose objects the grant does not list. "
                    + "The objects and describe verbs answer metadata questions under the grant");

            return null;
        }
    }

    /// <summary>
    /// Everything in one query specification that needs checking,
    /// WITHOUT descending into subqueries - those are processed with
    /// their own scope, and a boundary crossed here would check an inner
    /// reference against an outer FROM.
    /// </summary>
    sealed class Collector : TSqlFragmentVisitor
    {
        public readonly List<ColumnReferenceExpression> Columns = [];
        public readonly List<ScalarSubquery> Subqueries = [];
        public readonly List<FunctionCall> Functions = [];

        bool entered;

        public override void ExplicitVisit(QuerySpecification node)
        {
            // The collector is handed one spec; a nested one (inside a
            // derived table) is someone else's job.
            if (entered) return;
            entered = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ScalarSubquery node) => Subqueries.Add(node);

        public override void ExplicitVisit(QueryDerivedTable node)
        {
            // Processed by the FROM walk with its own scope; not
            // descended into here.
        }

        public override void Visit(ColumnReferenceExpression node) => Columns.Add(node);

        public override void Visit(FunctionCall node) => Functions.Add(node);
    }
}
