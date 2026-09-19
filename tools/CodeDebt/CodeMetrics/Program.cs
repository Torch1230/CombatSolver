using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length < 2)
    throw new ArgumentException("Usage: CodeMetrics <repository> <output-directory>");
string repo = Path.GetFullPath(args[0]);
string output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
string Rel(string p) => Path.GetRelativePath(repo, p).Replace('\\', '/');
int Line(SyntaxNode n) => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
string Owner(SyntaxNode n) => string.Join(".", n.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText));
string Key(ISymbol s) => s.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
void Write(string name, object data) => File.WriteAllText(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
SyntaxTree[] trees = Directory.GetFiles(Path.Combine(repo, "src"), "*.cs", SearchOption.AllDirectories)
    .Order(StringComparer.Ordinal).Select(p => CSharpSyntaxTree.ParseText(File.ReadAllText(p), new CSharpParseOptions(LanguageVersion.CSharp13), p)).ToArray();
// Framework metadata only. Missing game types are intentionally not guessed.
var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? throw new InvalidOperationException("No framework metadata"))
    .Split(Path.PathSeparator).Select(p => MetadataReference.CreateFromFile(p));
var compilation = CSharpCompilation.Create("CodeDebtReadOnly", trees, references,
    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
if (args.Length == 3 && args[2] == "--propose-safe")
{
    SafeCleanup.Propose(repo, output, compilation);
    return;
}
var methods = new List<object>();
var typeParts = new List<TypePart>();
var refs = new List<ReferenceRow>();
var catches = new List<object>();
var comments = new List<object>();
var switches = new List<object>();
var members = new List<object>();
var tokens = new List<object>();
var usings = new List<object>();
var strings = new List<object>();
var parseErrors = new List<string>();
int unresolvedNames = 0, resolvedNames = 0;
string[] auditedTypes = ["SolverSettings", "SolverSettingsData", "SearchPolicySnapshot", "SolverWeights", "SolverSearchProfile", "NoveltySearchOptions"];
foreach (SyntaxTree tree in trees)
{
    SyntaxNode root = tree.GetRoot();
    string file = Rel(tree.FilePath);
    SemanticModel model = compilation.GetSemanticModel(tree);
    parseErrors.AddRange(tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()));
    foreach (var u in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        usings.Add(new { file, line = Line(u), text = u.Name?.ToString() });
    foreach (var t in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
    {
        var symbol = model.GetDeclaredSymbol(t);
        if (symbol == null) continue;
        int count = t is TypeDeclarationSyntax td ? td.Members.Count : t is EnumDeclarationSyntax ed ? ed.Members.Count : 0;
        typeParts.Add(new(Key(symbol), file, Line(t), count));
    }
    foreach (var m in root.DescendantNodes().Where(n => n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax))
    {
        ParameterListSyntax? parameters = m switch { BaseMethodDeclarationSyntax b => b.ParameterList, LocalFunctionStatementSyntax l => l.ParameterList, _ => null };
        string name = m switch { MethodDeclarationSyntax x => x.Identifier.ValueText, ConstructorDeclarationSyntax x => x.Identifier.ValueText, LocalFunctionStatementSyntax x => x.Identifier.ValueText, AccessorDeclarationSyntax x => x.Keyword.ValueText, _ => m.Kind().ToString() };
        var descendants = m.DescendantNodes(n => n == m || n is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax)).ToArray();
        bool Control(SyntaxNode n) => n is IfStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax or SwitchStatementSyntax or SwitchExpressionSyntax or CatchClauseSyntax or ConditionalExpressionSyntax;
        int complexity = 1 + descendants.Count(n => n is IfStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax or CatchClauseSyntax or ConditionalExpressionSyntax or CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax || n.IsKind(SyntaxKind.LogicalAndExpression) || n.IsKind(SyntaxKind.LogicalOrExpression) || n.IsKind(SyntaxKind.CoalesceExpression))
            + descendants.OfType<SwitchExpressionSyntax>().Sum(x => Math.Max(0, x.Arms.Count - 1));
        int depth = descendants.Where(Control).Select(n => 1 + n.Ancestors().TakeWhile(a => a != m).Count(Control)).DefaultIfEmpty(0).Max();
        var symbol = model.GetDeclaredSymbol(m);
        methods.Add(new { file, line = Line(m), endLine = m.GetLocation().GetLineSpan().EndLinePosition.Line + 1,
            owner = Owner(m), name, symbol = symbol == null ? null : Key(symbol), kind = m.Kind().ToString(),
            accessibility = symbol?.DeclaredAccessibility.ToString(), isStatic = symbol?.IsStatic,
            lines = m.GetLocation().GetLineSpan().EndLinePosition.Line - Line(m) + 2,
            complexity, depth, parameters = parameters?.Parameters.Count ?? 0,
            locals = descendants.OfType<VariableDeclaratorSyntax>().Count() + descendants.OfType<SingleVariableDesignationSyntax>().Count(),
            parameterNames = parameters?.Parameters.Select(p => p.Identifier.ValueText).ToArray(),
            attributes = m.ChildNodes().OfType<AttributeListSyntax>().Select(a => a.ToString()).ToArray() });
    }
    foreach (var n in root.DescendantNodes().OfType<IdentifierNameSyntax>())
    {
        var s = model.GetSymbolInfo(n).Symbol;
        if (s == null) { unresolvedNames++; continue; }
        resolvedNames++;
        var locs = s.OriginalDefinition.Locations.Where(l => l.IsInSource).ToArray();
        if (locs.Length == 0) continue;
        var assignment = n.AncestorsAndSelf().OfType<AssignmentExpressionSyntax>().FirstOrDefault(a => a.Left.Span.Contains(n.Span));
        string role = assignment != null ? "write" : n.Parent is ArgumentSyntax a && a.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) ? "out" : "read-or-name";
        refs.Add(new(file, Line(n), n.Identifier.ValueText, Key(s), s.Kind.ToString(), s.DeclaredAccessibility.ToString(),
            locs.Select(l => Rel(l.SourceTree!.FilePath)).Distinct().ToArray(), role, assignment?.Right.ToString(),
            n.Ancestors().Any(a => a is InvocationExpressionSyntax i && i.Expression.ToString() == "nameof"),
            s is IFieldSymbol && s.ContainingType.DeclaringSyntaxReferences.Select(x => x.SyntaxTree.FilePath).Contains(tree.FilePath)));
    }
    foreach (var c in root.DescendantNodes().OfType<CatchClauseSyntax>())
    {
        string type = c.Declaration?.Type.ToString() ?? "<all>";
        var throws = c.Block.DescendantNodes().OfType<ThrowStatementSyntax>().ToArray();
        bool broad = type is "Exception" or "System.Exception" or "<all>";
        catches.Add(new { file, line = Line(c), owner = Owner(c), type, filter = c.Filter?.ToString(), broad,
            rethrow = throws.Any(t => t.Expression == null), throws = throws.Select(t => t.ToString()).ToArray(),
            returns = c.Block.DescendantNodes().OfType<ReturnStatementSyntax>().Select(r => r.ToString()).ToArray(),
            continues = c.Block.DescendantNodes().OfType<ContinueStatementSyntax>().Count(),
            suspicious = broad && throws.Length == 0, body = c.Block.ToString() });
    }
    foreach (var tr in root.DescendantTrivia(descendIntoTrivia: false))
    {
        if (tr.Kind() is not (SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia or SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia)) continue;
        string text = tr.ToString();
        if (Regex.IsMatch(text, @"\d|\.cs\b|当前|默认|现在|<remarks>|[A-Z][a-z]+[A-Z]"))
            comments.Add(new { file, line = tr.GetLocation().GetLineSpan().StartLinePosition.Line + 1, text });
    }
    foreach (var n in root.DescendantNodes().Where(n => n is PropertyDeclarationSyntax or VariableDeclaratorSyntax or EnumMemberDeclarationSyntax or ParameterSyntax))
    {
        if (n is VariableDeclaratorSyntax && n.Parent?.Parent is not FieldDeclarationSyntax) continue;
        if (n is ParameterSyntax && n.Parent?.Parent is not RecordDeclarationSyntax) continue;
        var s = model.GetDeclaredSymbol(n);
        if (s == null) continue;
        if (n is ParameterSyntax && s.ContainingType?.GetMembers(s.Name).OfType<IPropertySymbol>().FirstOrDefault() is { } property) s = property;
        members.Add(new { file, line = Line(n), owner = Owner(n), name = s.Name, symbol = Key(s), declaration = n.ToString() });
        ITypeSymbol? type = s switch { IFieldSymbol field => field.Type, IPropertySymbol p => p.Type, _ => null };
        bool constant = s is IFieldSymbol { IsConst: true };
        string? ownerType = n.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
        bool settingsEnum = n is EnumMemberDeclarationSyntax && Path.GetFileName(tree.FilePath) == "SolverSettings.cs";
        if (!auditedTypes.Contains(ownerType) && !settingsEnum) continue;
        bool nullableEnum = type is INamedTypeSymbol named && named.TypeArguments.Any(a => a.TypeKind == TypeKind.Enum);
        if (type?.SpecialType != SpecialType.System_Boolean && type?.TypeKind != TypeKind.Enum && !nullableEnum && !constant && ownerType != "NoveltySearchOptions") continue;
        switches.Add(new { file, line = Line(n), owner = Owner(n), name = s.Name, symbol = Key(s), type = type?.ToString(), constant,
            value = s is IFieldSymbol { HasConstantValue: true } f ? f.ConstantValue : null,
            declaration = n.ToString(), accessibility = s.DeclaredAccessibility.ToString() });
    }
    foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>().Where(n => n.IsKind(SyntaxKind.StringLiteralExpression)))
        strings.Add(new { file, line = Line(literal), value = literal.Token.ValueText, kind = "literal" });
    foreach (var interpolation in root.DescendantNodes().OfType<InterpolatedStringExpressionSyntax>())
    {
        int index = 0;
        string value = string.Concat(interpolation.Contents.Select(c => c is InterpolatedStringTextSyntax text ? text.TextToken.ValueText : c is InterpolationSyntax i ? "{" + index++ + i.AlignmentClause?.ToString() + i.FormatClause?.ToString() + "}" : ""));
        strings.Add(new { file, line = Line(interpolation), value, kind = "format-template" });
    }
    // Roslyn token boundaries preserve strings/comments correctly for clone normalization.
    tokens.Add(new { file, lines = root.DescendantTokens().GroupBy(t => t.GetLocation().GetLineSpan().StartLinePosition.Line + 1)
        .Select(g => new { line = g.Key, tokens = g.Select(t => new { kind = t.Kind().ToString(), text = t.Text }).ToArray() }).ToArray() });
}
Write("methods", methods); Write("type-parts", typeParts);
Write("types", typeParts.GroupBy(p => p.type).Select(g => new { type = g.Key, members = g.Sum(p => p.members), files = g.Select(p => p.file).Distinct().Order().ToArray(), partialFiles = g.Select(p => p.file).Distinct().Count() }).OrderByDescending(x => x.members));
Write("references", refs);
Write("coupling", refs.Where(r => r.kind == "Field" && r.accessibility == "Private" && r.samePartialOwner && !r.definitions.Contains(r.file))
    .GroupBy(r => r.file).Select(g => new { file = g.Key, distinctFields = g.Select(r => r.symbol).Distinct().Count(),
        fields = g.GroupBy(r => r.symbol).Select(f => new { symbol = f.Key, definitions = f.First().definitions, lines = f.Select(r => r.line).Distinct().Order().ToArray() }).ToArray() }).OrderByDescending(x => x.distinctFields));
Write("catches", catches); Write("comments", comments); Write("switches", switches); Write("members", members); Write("tokens", tokens); Write("usings", usings); Write("strings", strings);
Write("parse", new { files = trees.Length, parseErrors, resolvedNames, unresolvedNames,
    limitations = "Framework metadata only; unresolved game symbols excluded from bound reference counts. Complexity excludes local function/lambda bodies; accessors and local functions are separate rows. Coupling counts bound private fields declared in another file, including nested owners; inspect symbol owner for cross-partial subset." });
Console.WriteLine($"CODE_METRICS files={trees.Length} methods={methods.Count} types={typeParts.Select(p => p.type).Distinct().Count()} parse_errors={parseErrors.Count} resolved_names={resolvedNames} unresolved_names={unresolvedNames}");

record TypePart(string type, string file, int line, int members);
record ReferenceRow(string file, int line, string name, string symbol, string kind, string accessibility, string[] definitions, string role, string? value, bool nameofUse, bool samePartialOwner);
