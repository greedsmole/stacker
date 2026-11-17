#:package Microsoft.CodeAnalysis.CSharp@5.9.0
#:property PublishAot=false

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args is not ["--check"] and not ["--write"])
{
    Console.Error.WriteLine("Usage: dotnet run --file scripts/format-sql.cs -- --check|--write");
    return 2;
}

const RegexOptions sqlOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
const string sqlPrefix = @"^\s*(?:(?:--[^\r\n]*\r?\n|/\*[\s\S]*?\*/)\s*)*";
var sqlStart = new Regex(
    sqlPrefix + @"(?:select\b|with\b|insert\b|replace\s+into\b|update\b|delete\s+from\b|pragma\b|(?:create|drop|alter)\b|vacuum\b|explain\b|reindex\b|analyze\b|attach\b|detach\b|begin\b|commit\b|rollback\b|savepoint\b|release\b|end\b)",
    sqlOptions);
var standaloneSql = new Regex(
    sqlPrefix + """
    (?:
        select\s+(?:[\d'":@$?*+-]|\w+\s*\(|.+?\s+from\s+[\w.]+\s*(?:$|where\b|(?:\w+\s+)?join\b|order\s+by\b|group\s+by\b|limit\b|;))
        |with\s+(?:recursive\s+)?\w+\s*(?:\([^)]*\))?\s+as\s*\(
        |(?:insert(?:\s+or\s+\w+)?|replace)\s+into\s+\w+
        |update\s+\w+\s+set\b|delete\s+from\s+\w+|pragma\s+[\w.]+\s*(?:$|[=(;])
        |(?:create|drop|alter)\s+(?:temp(?:orary)?\s+|unique\s+)?(?:table|index|view|trigger)\b
    )
    """,
    sqlOptions | RegexOptions.Singleline | RegexOptions.IgnorePatternWhitespace);

// Match quoted text, comments, parameters and interpolation holes before SQL keywords.
var sqlTokens = new Regex(
    """
    (?<protected>--[^\r\n]*|/\*[\s\S]*?(?:\*/|\z)|'(?:''|[^'])*'|"(?:""|[^"])*"|`(?:``|[^`])*`|\[[^\]]*\]|\uE000\d+\uE001|[$:@][\w\u0080-\uFFFF]+(?:::[\w\u0080-\uFFFF]+)*(?:\([^\s)]*\))?)
    |(?<clause>\b(?:INSERT(?:\s+OR\s+\w+)?\s+INTO|REPLACE\s+INTO|DELETE\s+FROM|DO\s+UPDATE\s+SET|UNION(?:\s+ALL)?|(?:LEFT|RIGHT|FULL)(?:\s+OUTER)?\s+JOIN|(?:INNER|CROSS|NATURAL)\s+JOIN|ORDER\s+BY|GROUP\s+BY|ON\s+CONFLICT|WITH(?:\s+RECURSIVE)?|CREATE\s+(?:TEMP(?:ORARY)?\s+|UNIQUE\s+)?(?:TABLE|INDEX|VIEW|TRIGGER)|ALTER\s+TABLE|DROP\s+(?:TABLE|INDEX|VIEW|TRIGGER)|FROM|WHERE|JOIN|HAVING|LIMIT|RETURNING|SELECT|VALUES|UPDATE|SET|ON|AND|OR|PRAGMA|EXCEPT|INTERSECT|OFFSET)\b)
    |[\w\u0080-\uFFFF]+|[^\s]
    """,
    sqlOptions | RegexOptions.IgnorePatternWhitespace);
var quoteRuns = new Regex("\"+");
var braceRuns = new Regex(@"{+|}+");
var lineBreaks = new Regex("\r\n|\n|\r");

var repo = new DirectoryInfo(Directory.GetCurrentDirectory());
if (!File.Exists(Path.Combine(repo.FullName, "Stacker.slnx")))
{
    Console.Error.WriteLine("Run this command from the Stacker repository root.");
    return 2;
}

var updatedFiles = new Dictionary<string, string>();
bool foundInvalidSql = false;
foreach (var folder in new[] { "src", "tests" })
{
    foreach (string path in Directory.EnumerateFiles(Path.Combine(repo.FullName, folder), "*.cs", SearchOption.AllDirectories))
    {
        string relativePath = Path.GetRelativePath(repo.FullName, path);
        if (relativePath.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
        {
            continue;
        }

        string source = File.ReadAllText(path);
        string newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var syntax = CSharpSyntaxTree.ParseText(source).GetRoot();
        var changes = new List<(int Start, int End, string Text)>();

        foreach (var expression in syntax.DescendantNodes(node => node is not InterpolatedStringExpressionSyntax).OfType<ExpressionSyntax>())
        {
            var holes = new List<string>();
            string? sql = ReadLiteral(expression, holes);
            if (sql is null || !(standaloneSql.IsMatch(sql) || IsSqlArgument(expression) && sqlStart.IsMatch(sql)))
            {
                continue;
            }

            int line = expression.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            if (expression.Ancestors().TakeWhile(node => node is ExpressionSyntax)
                .Any(node => node.IsKind(SyntaxKind.AddExpression)))
            {
                Console.Error.WriteLine($"{relativePath}:{line}: combine SQL fragments into one interpolated raw string");
                foundInvalidSql = true;
                continue;
            }

            int lineStart = expression.SpanStart == 0 ? 0 : source.LastIndexOf('\n', expression.SpanStart - 1) + 1;
            string indent = new string(source[lineStart..expression.SpanStart].TakeWhile(c => c is ' ' or '\t').ToArray());
            string formatted = RawLiteral(FormatSql(sql), holes, indent + "    ", newline);
            if (!source.AsSpan(expression.SpanStart, expression.Span.Length).SequenceEqual(formatted))
            {
                Console.WriteLine($"{relativePath}:{line}: format SQL as a multiline raw string");
                changes.Add((expression.SpanStart, expression.Span.End, formatted));
            }
        }

        if (changes.Count == 0)
        {
            continue;
        }

        var rewritten = new StringBuilder(source);
        foreach (var (start, end, replacement) in changes.OrderByDescending(change => change.Start))
        {
            rewritten.Remove(start, end - start).Insert(start, replacement);
        }

        string result = rewritten.ToString();
        if (CSharpSyntaxTree.ParseText(result).GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            Console.Error.WriteLine($"Formatting would produce invalid C# in {relativePath}.");
            return 2;
        }

        updatedFiles.Add(path, result);
    }
}

if (foundInvalidSql)
{
    return 1;
}

if (updatedFiles.Count > 0 && args[0] == "--check")
{
    Console.Error.WriteLine("Fix SQL formatting with: dotnet run --file scripts/format-sql.cs -- --write");
    return 1;
}

foreach (var (path, contents) in updatedFiles)
{
    File.WriteAllText(path, contents);
}

return 0;

static bool IsSqlArgument(ExpressionSyntax expression)
{
    for (SyntaxNode? parent = expression.Parent; parent is not null; parent = parent.Parent)
    {
        switch (parent)
        {
            case AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CommandText" } }:
                return true;
            case ArgumentSyntax argument when argument.Parent is ArgumentListSyntax list:
                return list.Parent switch
                {
                    InvocationExpressionSyntax { Expression: IdentifierNameSyntax name } =>
                        list.Arguments.IndexOf(argument) == (name.Identifier.ValueText switch
                        {
                            "Command" => 2,
                            "Scalar" or "Execute" => 0,
                            _ => -1
                        }),
                    ObjectCreationExpressionSyntax creation when creation.Type.ToString().EndsWith("SqliteCommand", StringComparison.Ordinal) =>
                        list.Arguments.IndexOf(argument) == 0,
                    _ => false
                };
            case StatementSyntax:
                return false;
        }
    }

    return false;
}

static string? ReadLiteral(ExpressionSyntax expression, List<string> holes)
{
    if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
    {
        return literal.Token.ValueText;
    }

    if (expression is not InterpolatedStringExpressionSyntax interpolated)
    {
        return null;
    }

    var sql = new StringBuilder();
    bool raw = interpolated.StringStartToken.IsKind(SyntaxKind.InterpolatedMultiLineRawStringStartToken)
        || interpolated.StringStartToken.IsKind(SyntaxKind.InterpolatedSingleLineRawStringStartToken);
    foreach (var content in interpolated.Contents)
    {
        if (content is InterpolatedStringTextSyntax text)
        {
            string value = text.TextToken.ValueText;
            sql.Append(raw ? value : value.Replace("{{", "{").Replace("}}", "}"));
        }
        else if (content is InterpolationSyntax hole)
        {
            sql.Append($"\uE000{holes.Count}\uE001");
            holes.Add(hole.ToString()[hole.OpenBraceToken.Text.Length..^hole.CloseBraceToken.Text.Length]);
        }
    }

    return sql.ToString();
}

string FormatSql(string sql)
{
    Match[] tokens = sqlTokens.Matches(sql).ToArray();
    var multiline = new HashSet<int>();
    var parentheses = new Stack<int>();
    for (int i = 0; i < tokens.Length; i++)
    {
        string value = tokens[i].Value;
        switch (value)
        {
            case "(":
                parentheses.Push(i);
                if (i + 1 < tokens.Length && tokens[i + 1].Value.Equals("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    multiline.Add(i);
                }

                break;
            case "," when parentheses.TryPeek(out int opening):
                multiline.Add(opening);
                break;
            case ")" when parentheses.Count > 0:
                if (multiline.Contains(parentheses.Pop()))
                {
                    multiline.Add(i);
                }

                break;
        }
    }

    var rows = new List<SqlRow>();
    var scopes = new List<(int Indent, int KeywordWidth)> { (0, 0) };
    var activeScopes = new Stack<int>();
    activeScopes.Push(0);
    SqlRow? row = null;
    int end = 0;

    for (int i = 0; i < tokens.Length; i++)
    {
        Match token = tokens[i];
        string value = token.Value;
        string previous = i == 0 ? "" : tokens[i - 1].Value;
        string gap = sql[end..token.Index];
        bool newStatement = previous == ";" && activeScopes.Count == 1;
        bool afterOpen = previous == "(" && multiline.Contains(i - 1);
        bool beforeClose = value == ")" && multiline.Contains(i);
        bool clause = token.Groups["clause"].Success && (i == 0 || gap.Length > 0 || afterOpen);

        if (newStatement)
        {
            activeScopes.Pop();
            activeScopes.Push(scopes.Count);
            scopes.Add((0, 0));
        }

        if (beforeClose)
        {
            activeScopes.Pop();
        }

        int scopeIndex = activeScopes.Peek();
        var scope = scopes[scopeIndex];
        if (row is null || newStatement || afterOpen || beforeClose || previous == "," || clause)
        {
            string keyword = clause ? Regex.Replace(value, @"\s+", " ") : "";
            scopes[scopeIndex] = (scope.Indent, Math.Max(scope.KeywordWidth, keyword.Length));
            row = new SqlRow(scopeIndex, keyword, previous == "," && !clause, new StringBuilder());
            rows.Add(row);

            if (!clause)
            {
                row.Text.Append(value);
            }
        }
        else
        {
            if (row.Keyword.Length > 0 && row.Text.Length == 0 && !gap.Contains('\n'))
            {
                gap = " ";
            }

            row.Text.Append(gap).Append(value);
        }

        if (value == "(" && multiline.Contains(i))
        {
            activeScopes.Push(scopes.Count);
            scopes.Add((scope.Indent + 2, 0));
        }

        end = token.Index + token.Length;
    }

    return string.Join('\n', rows.Select(line =>
    {
        var scope = scopes[line.Scope];
        int padding = scope.Indent;
        if (line.Keyword.Length > 0)
        {
            padding += scope.KeywordWidth - line.Keyword.Length;
        }
        else if (line.Continuation && scope.KeywordWidth > 0)
        {
            padding += scope.KeywordWidth + 1;
        }

        return new string(' ', padding) + line.Keyword + line.Text;
    }));
}

string RawLiteral(string sql, List<string> holes, string indent, string newline)
{
    int quotes = Math.Max(3, quoteRuns.Matches(sql).Select(run => run.Length + 1).DefaultIfEmpty().Max());
    string delimiter = new('"', quotes);
    int dollars = holes.Count == 0 ? 0 : braceRuns.Matches(sql).Select(run => run.Length).DefaultIfEmpty().Max() + 1;
    string body = string.Join(newline, lineBreaks.Split(sql).Select(line => indent + line));
    for (int i = holes.Count - 1; i >= 0; i--)
    {
        body = body.Replace($"\uE000{i}\uE001", new string('{', dollars) + holes[i] + new string('}', dollars));
    }

    return new string('$', dollars) + delimiter + newline + body + newline + indent + delimiter;
}

sealed record SqlRow(int Scope, string Keyword, bool Continuation, StringBuilder Text);
