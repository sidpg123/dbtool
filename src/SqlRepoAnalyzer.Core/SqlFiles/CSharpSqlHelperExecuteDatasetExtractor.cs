using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SqlRepoAnalyzer.Core.Queries;

namespace SqlRepoAnalyzer.Core.SqlFiles;

/// <summary>
/// Extracts T-SQL from classic DAAB <c>SqlHelper</c> call sites (syntax-only Roslyn).
/// Handles <c>ExecuteDataset</c>, <c>ExecuteScalar</c>, <c>ExecuteNonQuery</c>, <c>ExecuteReader</c>
/// with signature <c>(…, CommandType, commandText, …)</c>, resolves <c>const</c>/<c>static readonly string</c> fields,
/// method-scoped <c>string</c>/<c>var</c> locals declared before the call, and permissively stitches <c>+</c> chains
/// (unknown fragments become empty text, flagged <c>partial</c>).
/// </summary>
public static class CSharpSqlHelperExecuteDatasetExtractor
{
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.Preview);

    private static readonly HashSet<string> SqlHelperCommandMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "ExecuteDataset",
        "ExecuteScalar",
        "ExecuteNonQuery",
        "ExecuteReader"
    };

    public static IEnumerable<QueryCandidate> ExtractFromFile(string filePath)
    {
        string text;
        CompilationUnitSyntax root;
        try
        {
            text = File.ReadAllText(filePath);
            root = CSharpSyntaxTree.ParseText(text, ParseOptions, path: filePath, cancellationToken: default)
                .GetCompilationUnitRoot();
        }
        catch
        {
            yield break;
        }

        var lineStarts = BuildLineStarts(text);
        var fileStaticStrings = BuildFileLevelStaticStringMap(root);

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!IsSqlHelperCommandInvocation(invocation, out var commandTextArg))
                continue;

            var scope = FindLocalScopeRoot(invocation);
            var merged = BuildMergedStringEnvironment(invocation, fileStaticStrings, scope);
            if (!TryEvaluateStringForCommandText(commandTextArg, merged, allowIncomplete: true, out var sql, out var complete))
                continue;

            sql = sql.Trim();
            if (string.IsNullOrEmpty(sql) || !SqlTextHeuristics.LooksLikeSql(sql))
                continue;

            var absStart = commandTextArg.Span.Start;
            var absEndExclusive = commandTextArg.Span.End;
            var (sl, sc) = ToLineCol(lineStarts, absStart);
            var (el, ec) = ToLineCol(lineStarts, Math.Max(absStart, absEndExclusive - 1));

            var bindingsJson = TrySerializeSqlParameterBindings(invocation, scope);

            yield return new QueryCandidate(
                SourceKind.CSharpSqlHelperExecuteDataset,
                filePath,
                sl,
                sc,
                el,
                ec,
                sql,
                complete ? "full" : "partial",
                bindingsJson);
        }
    }

    private static bool IsSqlHelperCommandInvocation(InvocationExpressionSyntax invocation, out ExpressionSyntax commandTextArg)
    {
        commandTextArg = null!;
        if (invocation.Expression is not MemberAccessExpressionSyntax ma)
            return false;

        if (!SqlHelperCommandMethods.Contains(ma.Name.Identifier.ValueText))
            return false;

        if (!IsSqlHelperReceiver(ma.Expression))
            return false;

        var args = invocation.ArgumentList.Arguments;
        if (args.Count < 3)
            return false;

        commandTextArg = args[2].Expression;
        return true;
    }

    private static bool IsSqlHelperReceiver(ExpressionSyntax expr)
    {
        expr = StripParens(expr);
        return expr switch
        {
            IdentifierNameSyntax id =>
                string.Equals(id.Identifier.ValueText, "SqlHelper", StringComparison.OrdinalIgnoreCase),
            QualifiedNameSyntax qn =>
                string.Equals(qn.Right.Identifier.ValueText, "SqlHelper", StringComparison.OrdinalIgnoreCase),
            MemberAccessExpressionSyntax m =>
                string.Equals(m.Name.Identifier.ValueText, "SqlHelper", StringComparison.OrdinalIgnoreCase),
            AliasQualifiedNameSyntax aq => IsSqlHelperReceiver(aq.Name),
            _ => false
        };
    }

    private static SyntaxNode? FindLocalScopeRoot(InvocationExpressionSyntax invocation)
    {
        foreach (var ancestor in invocation.Ancestors())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax { Body: { } body }:
                    return body;
                case MethodDeclarationSyntax { ExpressionBody: not null }:
                    return null;
                case LocalFunctionStatementSyntax { Body: { } lfBody }:
                    return lfBody;
                case AccessorDeclarationSyntax { Body: { } accBody }:
                    return accBody;
            }
        }

        return null;
    }

    private sealed record StringSymbolInit(string Name, ExpressionSyntax Initializer);

    private static Dictionary<string, (string Text, bool Complete)> BuildFileLevelStaticStringMap(CompilationUnitSyntax root)
    {
        var inits = new List<StringSymbolInit>();
        foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            if (!IsStaticReadonlyOrConstStringField(field))
                continue;

            foreach (var v in field.Declaration.Variables)
            {
                if (v.Initializer?.Value is { } init)
                    inits.Add(new StringSymbolInit(v.Identifier.ValueText, init));
            }
        }

        foreach (var local in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            if (!local.Modifiers.Any(static m => m.IsKind(SyntaxKind.ConstKeyword)))
                continue;
            if (!LooksLikeStringDeclaration(local.Declaration.Type))
                continue;

            foreach (var v in local.Declaration.Variables)
            {
                if (v.Initializer?.Value is { } init)
                    inits.Add(new StringSymbolInit(v.Identifier.ValueText, init));
            }
        }

        return ResolveStringInits(inits, new Dictionary<string, (string Text, bool Complete)>(StringComparer.OrdinalIgnoreCase),
            allowIncomplete: false);
    }

    private static Dictionary<string, (string Text, bool Complete)> BuildMergedStringEnvironment(
        InvocationExpressionSyntax invocation,
        IReadOnlyDictionary<string, (string Text, bool Complete)> fileMap,
        SyntaxNode? scopeRoot)
    {
        var merged = new Dictionary<string, (string Text, bool Complete)>(fileMap, StringComparer.OrdinalIgnoreCase);
        if (scopeRoot is null)
            return merged;

        var inits = new List<StringSymbolInit>();
        foreach (var localDecl in scopeRoot.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            if (!LooksLikeStringOrVarDeclaration(localDecl.Declaration.Type))
                continue;

            foreach (var v in localDecl.Declaration.Variables)
            {
                if (v.Initializer?.Value is null)
                    continue;
                if (v.Span.End >= invocation.Span.Start)
                    continue;

                inits.Add(new StringSymbolInit(v.Identifier.ValueText, v.Initializer.Value));
            }
        }

        return ResolveStringInits(inits, merged, allowIncomplete: true);
    }

    private static Dictionary<string, (string Text, bool Complete)> ResolveStringInits(
        IReadOnlyList<StringSymbolInit> inits,
        Dictionary<string, (string Text, bool Complete)> seed,
        bool allowIncomplete)
    {
        var map = seed;
        for (var pass = 0; pass < 32; pass++)
        {
            var changed = false;
            foreach (var item in inits)
            {
                if (!TryEvaluateStringForCommandText(item.Initializer, map, allowIncomplete, out var text, out var complete))
                    continue;

                if (map.TryGetValue(item.Name, out var prev) &&
                    string.Equals(prev.Text, text, StringComparison.Ordinal) &&
                    prev.Complete == complete)
                    continue;

                map[item.Name] = (text, complete);
                changed = true;
            }

            if (!changed)
                break;
        }

        return map;
    }

    private static bool TryEvaluateStringForCommandText(
        ExpressionSyntax expr,
        IReadOnlyDictionary<string, (string Text, bool Complete)> symbols,
        bool allowIncomplete,
        out string combined,
        out bool complete)
    {
        combined = "";
        complete = true;
        expr = StripParens(expr);

        switch (expr)
        {
            case LiteralExpressionSyntax les when les.IsKind(SyntaxKind.StringLiteralExpression):
                combined = les.Token.ValueText;
                return true;

            case LiteralExpressionSyntax lesCh when lesCh.IsKind(SyntaxKind.CharacterLiteralExpression):
                combined = lesCh.Token.ValueText;
                return true;

            case BinaryExpressionSyntax bin when bin.IsKind(SyntaxKind.AddExpression):
                var lOk = TryEvaluateStringForCommandText(bin.Left, symbols, allowIncomplete, out var left, out var c1);
                var rOk = TryEvaluateStringForCommandText(bin.Right, symbols, allowIncomplete, out var right, out var c2);
                if (!allowIncomplete && (!lOk || !rOk))
                    return false;
                if (!lOk)
                {
                    left = "";
                    c1 = false;
                }

                if (!rOk)
                {
                    right = "";
                    c2 = false;
                }

                combined = left + right;
                complete = c1 && c2;
                return true;

            case InterpolatedStringExpressionSyntax interp:
                return TryFlattenInterpolatedString(interp, symbols, allowIncomplete, out combined, out complete);

            case IdentifierNameSyntax idRef:
                if (symbols.TryGetValue(idRef.Identifier.ValueText, out var tuple))
                {
                    combined = tuple.Text;
                    complete = tuple.Complete;
                    return true;
                }

                if (allowIncomplete)
                {
                    combined = "";
                    complete = false;
                    return true;
                }

                return false;

            default:
                return false;
        }
    }

    private static bool TryFlattenInterpolatedString(
        InterpolatedStringExpressionSyntax interp,
        IReadOnlyDictionary<string, (string Text, bool Complete)> symbols,
        bool allowIncomplete,
        out string combined,
        out bool complete)
    {
        var sb = new StringBuilder();
        complete = true;
        foreach (var part in interp.Contents)
        {
            switch (part)
            {
                case InterpolatedStringTextSyntax txt:
                    sb.Append(txt.TextToken.Text);
                    break;
                case InterpolationSyntax ip:
                    if (TryEvaluateStringForCommandText(ip.Expression, symbols, allowIncomplete, out var inner, out var ic))
                    {
                        sb.Append(inner);
                        complete = complete && ic;
                    }
                    else if (allowIncomplete)
                    {
                        complete = false;
                    }
                    else
                    {
                        combined = "";
                        complete = false;
                        return false;
                    }

                    break;
                default:
                    if (!allowIncomplete)
                    {
                        combined = "";
                        complete = false;
                        return false;
                    }

                    complete = false;
                    break;
            }
        }

        combined = sb.ToString();
        return true;
    }

    private static bool IsStaticReadonlyOrConstStringField(FieldDeclarationSyntax field)
    {
        var hasStatic = field.Modifiers.Any(static m => m.IsKind(SyntaxKind.StaticKeyword));
        var isConst = field.Modifiers.Any(static m => m.IsKind(SyntaxKind.ConstKeyword));
        var isReadonly = field.Modifiers.Any(static m => m.IsKind(SyntaxKind.ReadOnlyKeyword));
        if (!isConst && !(hasStatic && isReadonly))
            return false;

        return LooksLikeStringDeclaration(field.Declaration.Type);
    }

    private static bool LooksLikeStringDeclaration(TypeSyntax type)
    {
        type = StripNullable(type);
        if (type is PredefinedTypeSyntax pt)
            return pt.Keyword.IsKind(SyntaxKind.StringKeyword);

        if (type is IdentifierNameSyntax id)
            return string.Equals(id.Identifier.ValueText, "String", StringComparison.OrdinalIgnoreCase);

        return false;
    }

    private static bool LooksLikeStringOrVarDeclaration(TypeSyntax type)
    {
        type = StripNullable(type);
        if (type is IdentifierNameSyntax id &&
            string.Equals(id.Identifier.ValueText, "var", StringComparison.OrdinalIgnoreCase))
            return true;

        return LooksLikeStringDeclaration(type);
    }

    private static TypeSyntax StripNullable(TypeSyntax type) =>
        type is NullableTypeSyntax n ? n.ElementType : type;

    private static ExpressionSyntax StripParens(ExpressionSyntax expr)
    {
        while (expr is ParenthesizedExpressionSyntax p)
            expr = p.Expression;
        return expr;
    }

    private static readonly JsonSerializerOptions SqlBindingJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private static string? TrySerializeSqlParameterBindings(
        InvocationExpressionSyntax invocation,
        SyntaxNode? scopeRoot)
    {
        var entries = new List<Dictionary<string, string?>>();
        var args = invocation.ArgumentList.Arguments;
        for (var i = 3; i < args.Count; i++)
        {
            var expanded = ExpandSqlParameterArgumentExpression(args[i].Expression, invocation, scopeRoot);
            foreach (var oce in EnumerateSqlParameterObjectCreations(expanded))
            {
                if (TryExtractSqlParameterBindingEntry(oce, invocation, scopeRoot, out var entry))
                    entries.Add(entry);
            }
        }

        return entries.Count == 0 ? null : JsonSerializer.Serialize(entries, SqlBindingJsonOptions);
    }

    /// <summary>
    /// SqlHelper passes parameter packs as inline <c>new SqlParameter[] {{ ... }}</c> or as a local
    /// (<c>commandParameters</c>) assigned just above the call; resolve the latter for binding metadata.
    /// </summary>
    private static ExpressionSyntax ExpandSqlParameterArgumentExpression(
        ExpressionSyntax expr,
        InvocationExpressionSyntax invocation,
        SyntaxNode? scopeRoot)
    {
        expr = StripParens(expr);
        if (expr is ObjectCreationExpressionSyntax or ArrayCreationExpressionSyntax or ImplicitArrayCreationExpressionSyntax)
            return expr;

        if (expr is IdentifierNameSyntax id &&
            TryResolveLocalInitializerClosestBefore(id, invocation, scopeRoot) is { } init)
            return StripParens(init);

        return expr;
    }

    private static ExpressionSyntax? TryResolveLocalInitializerClosestBefore(
        IdentifierNameSyntax idRef,
        InvocationExpressionSyntax invocation,
        SyntaxNode? scopeRoot)
    {
        if (scopeRoot is null)
            return null;

        var name = idRef.Identifier.ValueText;
        LocalDeclarationStatementSyntax? bestDecl = null;
        VariableDeclaratorSyntax? bestVar = null;

        foreach (var localDecl in scopeRoot.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
        {
            if (localDecl.Span.End >= invocation.Span.Start)
                continue;

            foreach (var v in localDecl.Declaration.Variables)
            {
                if (!string.Equals(v.Identifier.ValueText, name, StringComparison.Ordinal))
                    continue;
                if (v.Initializer?.Value is null)
                    continue;

                if (bestDecl is null || localDecl.Span.End > bestDecl.Span.End)
                {
                    bestDecl = localDecl;
                    bestVar = v;
                }
            }
        }

        return bestVar?.Initializer?.Value;
    }

    private static IEnumerable<ObjectCreationExpressionSyntax> EnumerateSqlParameterObjectCreations(ExpressionSyntax expr)
    {
        expr = StripParens(expr);
        switch (expr)
        {
            case ObjectCreationExpressionSyntax oce when IsSqlParameterTypeSyntax(oce.Type):
                yield return oce;
                yield break;
            case ArrayCreationExpressionSyntax arr when arr.Initializer is not null:
                foreach (var e in arr.Initializer.Expressions)
                {
                    foreach (var o in EnumerateSqlParameterObjectCreations(e))
                        yield return o;
                }

                yield break;
            case ImplicitArrayCreationExpressionSyntax impl when impl.Initializer is not null:
                foreach (var e in impl.Initializer.Expressions)
                {
                    foreach (var o in EnumerateSqlParameterObjectCreations(e))
                        yield return o;
                }

                yield break;
            default:
                yield break;
        }
    }

    private static bool IsSqlParameterTypeSyntax(TypeSyntax? type) =>
        type switch
        {
            IdentifierNameSyntax id =>
                string.Equals(id.Identifier.ValueText, "SqlParameter", StringComparison.OrdinalIgnoreCase),
            QualifiedNameSyntax qn =>
                string.Equals(qn.Right.Identifier.ValueText, "SqlParameter", StringComparison.OrdinalIgnoreCase),
            _ => false
        };

    private static bool TryExtractSqlParameterBindingEntry(
        ObjectCreationExpressionSyntax oce,
        InvocationExpressionSyntax invocation,
        SyntaxNode? scopeRoot,
        out Dictionary<string, string?> entry)
    {
        entry = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (!IsSqlParameterTypeSyntax(oce.Type))
            return false;

        string? name = null;
        string? sqlDbType = null;

        if (oce.ArgumentList is { } argList)
        {
            var args = argList.Arguments;
            if (args.Count > 0 && args[0].Expression is LiteralExpressionSyntax s0 &&
                s0.IsKind(SyntaxKind.StringLiteralExpression))
                name = s0.Token.ValueText;

            if (args.Count > 1)
            {
                var a1 = StripParens(args[1].Expression);
                sqlDbType = InferSqlDbTypeFromSqlParameterValueExpression(a1, invocation, scopeRoot);
            }
        }

        if (oce.Initializer is not null)
        {
            foreach (var ex in oce.Initializer.Expressions)
            {
                if (ex is not AssignmentExpressionSyntax asn)
                    continue;
                if (asn.Left is not IdentifierNameSyntax id)
                    continue;
                var prop = id.Identifier.ValueText;
                if (string.Equals(prop, "ParameterName", StringComparison.OrdinalIgnoreCase) &&
                    asn.Right is LiteralExpressionSyntax lpn && lpn.IsKind(SyntaxKind.StringLiteralExpression))
                    name = lpn.Token.ValueText;
                else if (string.Equals(prop, "SqlDbType", StringComparison.OrdinalIgnoreCase))
                    sqlDbType = TryGetSqlDbTypeEnumMember(asn.Right) ?? sqlDbType;
            }
        }

        if (string.IsNullOrWhiteSpace(name))
            return false;

        sqlDbType ??= TryInferSqlDbTypeFromValueProperty(oce, invocation, scopeRoot);

        entry["name"] = name.StartsWith("@", StringComparison.Ordinal) ? name : "@" + name;
        entry["providerType"] = "sqlClient";
        if (!string.IsNullOrWhiteSpace(sqlDbType))
            entry["sqlDbType"] = sqlDbType;
        return true;
    }

    private static string? TryInferSqlDbTypeFromValueProperty(
        ObjectCreationExpressionSyntax oce,
        InvocationExpressionSyntax invocation,
        SyntaxNode? scopeRoot)
    {
        if (oce.Initializer is null)
            return null;

        foreach (var ex in oce.Initializer.Expressions)
        {
            if (ex is not AssignmentExpressionSyntax asn)
                continue;
            if (asn.Left is not IdentifierNameSyntax id)
                continue;
            if (!string.Equals(id.Identifier.ValueText, "Value", StringComparison.OrdinalIgnoreCase))
                continue;
            return InferSqlDbTypeFromSqlParameterValueExpression(asn.Right, invocation, scopeRoot);
        }

        return null;
    }

    private static string? InferSqlDbTypeFromSqlParameterValueExpression(
        ExpressionSyntax expr,
        InvocationExpressionSyntax invocation,
        SyntaxNode? scopeRoot)
    {
        expr = StripParens(expr);
        if (TryGetSqlDbTypeEnumMember(expr) is { } fromEnum)
            return fromEnum;
        if (InferSqlDbTypeFromLiteral(expr) is { } fromLit)
            return fromLit;

        if (expr is IdentifierNameSyntax id)
        {
            if (TryResolveLocalInitializerClosestBefore(id, invocation, scopeRoot) is { } init)
            {
                if (InferSqlDbTypeFromLiteral(StripParens(init)) is { } fromLocalLit)
                    return fromLocalLit;
            }

            if (TryGetScopedParameterClrTypeName(id, invocation) is { } clr)
                return MapClrTypeNameToSqlDbTypeKeyword(clr);
        }

        return null;
    }

    private static string? TryGetScopedParameterClrTypeName(IdentifierNameSyntax id, InvocationExpressionSyntax invocation)
    {
        var name = id.Identifier.ValueText;
        foreach (var ancestor in invocation.Ancestors())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax m:
                {
                    var t = FindParameterTypeName(m.ParameterList, name);
                    if (t is not null)
                        return t;
                    break;
                }
                case LocalFunctionStatementSyntax lf:
                {
                    var t = FindParameterTypeName(lf.ParameterList, name);
                    if (t is not null)
                        return t;
                    break;
                }
            }
        }

        return null;
    }

    private static string? FindParameterTypeName(ParameterListSyntax list, string parameterName)
    {
        foreach (var p in list.Parameters)
        {
            if (!string.Equals(p.Identifier.ValueText, parameterName, StringComparison.Ordinal))
                continue;
            return TypeSyntaxToClrTypeLabel(p.Type);
        }

        return null;
    }

    private static string? TypeSyntaxToClrTypeLabel(TypeSyntax? type)
    {
        if (type is null)
            return null;
        type = StripNullable(type);
        return type switch
        {
            PredefinedTypeSyntax pt => pt.Keyword.ValueText,
            IdentifierNameSyntax id => id.Identifier.ValueText,
            _ => null
        };
    }

    private static string? MapClrTypeNameToSqlDbTypeKeyword(string name)
    {
        var n = name.Trim();
        if (string.IsNullOrEmpty(n))
            return null;

        if (string.Equals(n, "int", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "uint", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "Int32", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "UInt32", StringComparison.OrdinalIgnoreCase))
            return "Int";

        if (string.Equals(n, "long", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "ulong", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "Int64", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "UInt64", StringComparison.OrdinalIgnoreCase))
            return "BigInt";

        if (string.Equals(n, "short", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "ushort", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "Int16", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "UInt16", StringComparison.OrdinalIgnoreCase))
            return "SmallInt";

        if (string.Equals(n, "byte", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "sbyte", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "Byte", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "SByte", StringComparison.OrdinalIgnoreCase))
            return "TinyInt";

        if (string.Equals(n, "bool", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "Boolean", StringComparison.OrdinalIgnoreCase))
            return "Bit";

        if (string.Equals(n, "string", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "String", StringComparison.OrdinalIgnoreCase))
            return "NVarChar";

        if (string.Equals(n, "decimal", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "Decimal", StringComparison.OrdinalIgnoreCase))
            return "Decimal";

        if (string.Equals(n, "double", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "Double", StringComparison.OrdinalIgnoreCase))
            return "Float";

        if (string.Equals(n, "float", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "Single", StringComparison.OrdinalIgnoreCase))
            return "Real";

        if (string.Equals(n, "char", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(n, "Char", StringComparison.OrdinalIgnoreCase))
            return "NChar";

        if (string.Equals(n, "DateTime", StringComparison.OrdinalIgnoreCase))
            return "DateTime2";

        if (string.Equals(n, "DateTimeOffset", StringComparison.OrdinalIgnoreCase))
            return "DateTimeOffset";

        if (string.Equals(n, "DateOnly", StringComparison.OrdinalIgnoreCase))
            return "Date";

        if (string.Equals(n, "TimeOnly", StringComparison.OrdinalIgnoreCase))
            return "Time";

        if (string.Equals(n, "Guid", StringComparison.OrdinalIgnoreCase))
            return "UniqueIdentifier";

        return null;
    }

    private static string? TryGetSqlDbTypeEnumMember(ExpressionSyntax expr)
    {
        expr = StripParens(expr);
        if (expr is not MemberAccessExpressionSyntax ma)
            return null;
        if (ma.Expression is not IdentifierNameSyntax id)
            return null;
        if (!string.Equals(id.Identifier.ValueText, "SqlDbType", StringComparison.OrdinalIgnoreCase))
            return null;
        return ma.Name.Identifier.ValueText;
    }

    private static string? InferSqlDbTypeFromLiteral(ExpressionSyntax expr)
    {
        if (expr is not LiteralExpressionSyntax les)
            return null;
        if (les.IsKind(SyntaxKind.StringLiteralExpression))
            return "NVarChar";
        if (les.IsKind(SyntaxKind.NumericLiteralExpression))
            return "Int";
        if (les.IsKind(SyntaxKind.TrueKeyword) || les.IsKind(SyntaxKind.FalseKeyword))
            return "Bit";
        return null;
    }

    private static int[] BuildLineStarts(string s)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\n')
                starts.Add(i + 1);
        }

        return starts.ToArray();
    }

    private static (int line, int col) ToLineCol(int[] lineStarts, int absPos)
    {
        var idx = Array.BinarySearch(lineStarts, absPos);
        if (idx < 0)
            idx = ~idx - 1;
        if (idx < 0)
            idx = 0;
        var lineStart = lineStarts[idx];
        return (idx + 1, absPos - lineStart + 1);
    }
}
