using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Picker.Core;

/// <summary>
/// What the gate approved: the statement as it will actually run, and
/// what it touches.
/// </summary>
/// <param name="Sql">The rewritten statement: every object reference
/// three-part and bracket-quoted so the connection's default database
/// decides nothing, and every <c>*</c> expanded to the columns that
/// exist for this caller.</param>
/// <param name="Objects">Every base table and view the statement
/// reaches, subqueries and CTEs included.</param>
/// <param name="Screen">The union of the referenced scopes' screens: a
/// response disclosing from two scopes is screened for everything
/// either one asks.</param>
public sealed record Approved(
    string Sql, IReadOnlyList<ResolvedObject> Objects, Screened Screen);

/// <summary>
/// The query gate: one SELECT, fully resolved inside the grant, or
/// nothing.
///
/// <para><b>The statement is parsed, not pattern-matched.</b> Comments,
/// CTEs and parentheses all defeat "does it start with SELECT", so the
/// gate reads the AST Microsoft's own parser produces and walks all of
/// it.</para>
///
/// <para><b>Fail closed on what the walker does not recognise.</b> The
/// gate holds an allowlist of the constructs it can check, and a node
/// outside it refuses the query naming the construct - a check that did
/// not happen may not be reported as a check that found nothing.</para>
/// </summary>
public static class Gate
{
    public static async Task<Result<Approved>> ApproveAsync(
        string sql, Resolver resolver, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return Result<Approved>.Fail(Outcome.Invalid, "there is no SQL to run");

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        TSqlFragment parsed;
        IList<ParseError> errors;
        using (var reader = new StringReader(sql))
            parsed = parser.Parse(reader, out errors);

        if (errors.Count > 0)
        {
            ParseError first = errors[0];
            return Result<Approved>.Fail(Outcome.Invalid,
                $"this is not valid T-SQL: {first.Message} "
                + $"(line {first.Line}, column {first.Column})");
        }

        // ---- one statement, and it is a SELECT ----

        var statements = new List<TSqlStatement>();
        if (parsed is TSqlScript script)
            foreach (TSqlBatch batch in script.Batches)
                statements.AddRange(batch.Statements);

        if (statements.Count == 0)
            return Result<Approved>.Fail(Outcome.Invalid, "there is no statement to run");

        if (statements.Count > 1)
            return Result<Approved>.Fail(Outcome.Refused,
                $"one statement per call; {statements.Count} were sent");

        if (statements[0] is not SelectStatement select)
            return Result<Approved>.Fail(NotASelect(statements[0]));

        if (select.Into is not null)
            return Result<Approved>.Fail(Outcome.Refused,
                "SELECT ... INTO writes a table. pick only reads");

        if (select.On is not null)
            return Result<Approved>.Fail(Outcome.Refused,
                "SELECT ... ON <filegroup> is not accepted");

        // ---- every node accounted for ----

        var allowlist = new Allowlist();
        select.Accept(allowlist);
        if (allowlist.Refusal is not null)
            return Result<Approved>.Fail(allowlist.Refusal);

        // ---- names bound, columns proven, stars expanded ----

        Result<Bound> bound = await Binder
            .BindAsync(select, resolver, cancel).ConfigureAwait(false);
        if (!bound.IsOk) return bound.Carry<Approved>();

        Screened screen = Screened.None;
        foreach (ResolvedObject o in bound.Value.Objects) screen |= o.Screen;

        var generator = new Sql160ScriptGenerator(new SqlScriptGeneratorOptions
        {
            KeywordCasing = KeywordCasing.Uppercase,
            IncludeSemicolons = false,
        });
        generator.GenerateScript(select, out string rewritten);

        return Result<Approved>.Ok(new Approved(rewritten, bound.Value.Objects, screen));
    }

    /// <summary>The refusal for every statement kind that is not a
    /// SELECT, with the remedy the specific kind deserves.</summary>
    static Failure NotASelect(TSqlStatement statement)
    {
        string kind = statement.GetType().Name;
        if (kind.EndsWith("Statement", StringComparison.Ordinal)) kind = kind[..^9];

        string detail = statement switch
        {
            InsertStatement or UpdateStatement or DeleteStatement or MergeStatement =>
                "pick only reads",
            UseStatement =>
                "there is no USE. Qualify the object name with its database instead",
            ExecuteStatement or ExecuteAsStatement =>
                "stored procedures are not reachable here; the verbs are select and describe",
            DeclareVariableStatement or SetVariableStatement or PredicateSetStatement
                or SetCommandStatement =>
                "variables and session settings are not available here. Put everything in the one statement",
            BeginTransactionStatement or CommitTransactionStatement
                or RollbackTransactionStatement =>
                "there is no transaction to control; every call is one read",
            _ => "only a single SELECT runs here",
        };

        return new Failure(Outcome.Refused,
            $"only a SELECT runs here; that was {kind.ToUpperInvariant()}. {detail}");
    }

    /// <summary>
    /// The closed list of constructs the gate can check. Everything else
    /// refuses NAMING the construct, so a legitimate query that trips
    /// this reads as a request to extend the gate rather than as a
    /// mystery - and an extension is a reviewed decision, not a drift.
    /// </summary>
    sealed class Allowlist : TSqlFragmentVisitor
    {
        public Failure? Refusal;

        static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
        {
            // statement shell
            "SelectStatement", "WithCtesAndXmlNamespaces", "CommonTableExpression",

            // query shapes
            "QuerySpecification", "QueryParenthesisExpression", "BinaryQueryExpression",

            // select list
            "SelectScalarExpression", "SelectStarExpression",

            // from
            "FromClause", "NamedTableReference", "QueryDerivedTable", "InlineDerivedTable",
            "QualifiedJoin", "UnqualifiedJoin", "JoinParenthesisTableReference", "RowValue",

            // clauses
            "WhereClause", "GroupByClause", "ExpressionGroupingSpecification",
            "HavingClause", "OrderByClause", "ExpressionWithSortOrder", "OffsetClause",
            "TopRowFilter", "JsonForClause", "JsonForClauseOption",

            // names
            "Identifier", "MultiPartIdentifier", "SchemaObjectName",
            "IdentifierOrValueExpression",

            // scalar expressions
            "ColumnReferenceExpression", "IntegerLiteral", "NumericLiteral", "RealLiteral",
            "MoneyLiteral", "StringLiteral", "NullLiteral", "BinaryLiteral", "MaxLiteral",
            "ParenthesisExpression", "UnaryExpression", "BinaryExpression", "FunctionCall",
            "CastCall", "TryCastCall", "ConvertCall", "TryConvertCall", "ParseCall",
            "TryParseCall", "IIfCall", "CoalesceExpression", "NullIfExpression",
            "SearchedCaseExpression", "SimpleCaseExpression", "SearchedWhenClause",
            "SimpleWhenClause", "ScalarSubquery",

            // Allowed through to the binder, which refuses it with the
            // user-function reason - a sharper message than the generic
            // unknown-construct one this list would give it.
            "MultiPartIdentifierCallTarget",

            // booleans
            "BooleanBinaryExpression", "BooleanComparisonExpression",
            "BooleanParenthesisExpression", "BooleanNotExpression",
            "BooleanIsNullExpression", "LikePredicate", "InPredicate", "BetweenPredicate",
            "ExistsPredicate", "SubqueryComparisonPredicate", "BooleanTernaryExpression",

            // windows and aggregates
            "OverClause", "WindowFrameClause", "WindowDelimiter", "WithinGroupClause",
        };

        /// <summary>Constructs that get their own sentence instead of
        /// the generic unknown-construct one, because each is a route
        /// somewhere specific and the message should point back.</summary>
        static Failure? Special(TSqlFragment node) => node.GetType().Name switch
        {
            "OpenRowsetTableReference" or "OpenQueryTableReference"
                or "AdHocTableReference" or "OpenRowsetCosmos" =>
                new Failure(Outcome.Refused,
                    "OPENROWSET, OPENQUERY and similar functions reach outside the declared "
                    + "databases, so they are refused"),

            "VariableReference" or "GlobalVariableExpression" or "VariableTableReference" =>
                new Failure(Outcome.Refused,
                    "variables and session state are not available here"),

            "TableHint" or "IndexTableHint" =>
                new Failure(Outcome.Refused,
                    "table hints are not accepted here"),

            _ => null,
        };

        public override void Visit(TSqlFragment node)
        {
            if (Refusal is not null) return;

            string name = node.GetType().Name;
            if (Allowed.Contains(name)) return;

            Refusal = Special(node) ?? new Failure(Outcome.Refused,
                $"this query uses {name}, which this gate does not check, so it does not run");
        }
    }
}
