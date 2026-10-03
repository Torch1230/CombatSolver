using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class SafeCleanup
{
    internal static void Propose(string repo, string output, CSharpCompilation compilation)
    {
        var diagnostics = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, ".local/debt/before/diagnostics.json"))).RootElement.EnumerateArray();
        var decisions = new List<object>();
        var replacements = new Dictionary<SyntaxNode, SyntaxNode>();
        var targets = new Dictionary<MethodDeclarationSyntax, (bool Static, HashSet<string> Parameters)>();
        foreach (var d in diagnostics)
        {
            string rule = d.GetProperty("rule").GetString()!;
            if (rule is not ("CA1822" or "IDE0060")) continue;
            string file = d.GetProperty("file").GetString()!;
            int line = d.GetProperty("line").GetInt32();
            string message = d.GetProperty("message").GetString()!;
            var tree = compilation.SyntaxTrees.FirstOrDefault(t => Path.GetRelativePath(repo, t.FilePath) == file);
            var method = tree?.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.GetLocation().GetLineSpan().StartLinePosition.Line < line && m.GetLocation().GetLineSpan().EndLinePosition.Line >= line-1)
                .OrderBy(m => m.Span.Length).FirstOrDefault();
            string reason = method == null ? "not a method" : !method.Modifiers.Any(SyntaxKind.PrivateKeyword) ? "not private" :
                file.Contains("/Engine/") || file.Contains("/Prediction/") || file.Contains("/Testing/") || file.Contains("Patch") || method.Identifier.ValueText.Contains("Testing") || method.AttributeLists.Count != 0 ? "protected semantic/test/patch/attribute boundary" : "candidate";
            if (reason != "candidate") { decisions.Add(new { rule, file, line, message, reason }); continue; }
            if (!targets.TryGetValue(method!, out var target)) target = (false, new());
            if (rule == "CA1822") target.Static = true;
            else target.Parameters.Add(Regex.Match(message, "'([^']+)'").Groups[1].Value);
            targets[method!] = target;
        }
        foreach (var (method, target) in targets)
        {
            string name = method.Identifier.ValueText;
            var model = compilation.GetSemanticModel(method.SyntaxTree);
            var symbol = model.GetDeclaredSymbol(method)!;
            var calls = new List<InvocationExpressionSyntax>();
            string? reject = null;
            foreach (var tree in compilation.SyntaxTrees)
            {
                var sm = compilation.GetSemanticModel(tree);
                foreach (var token in tree.GetRoot().DescendantTokens().Where(t => t.IsKind(SyntaxKind.StringLiteralToken) && t.ValueText == name))
                    reject = "string lookup with the same name";
                foreach (var identifier in tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>().Where(n => n.Identifier.ValueText == name))
                {
                    var bound = sm.GetSymbolInfo(identifier).Symbol;
                    if (bound != null && !SymbolEqualityComparer.Default.Equals(bound.OriginalDefinition, symbol)) continue;
                    if (bound == null) { reject = "unresolved same-name reference"; continue; }
                    if (identifier.Parent is not InvocationExpressionSyntax invocation || invocation.Expression != identifier)
                    { reject = "qualified call, method group or nameof reference"; continue; }
                    calls.Add(invocation);
                }
            }
            var remove = method.ParameterList.Parameters.Select((p,i)=>(p,i)).Where(x=>target.Parameters.Contains(x.p.Identifier.ValueText)).Select(x=>x.i).ToHashSet();
            foreach (var call in calls)
            {
                var args = call.ArgumentList.Arguments;
                if (args.Any(a => a.NameColon != null || a.RefKindKeyword.RawKind != 0) || args.Count != method.ParameterList.Parameters.Count)
                { reject = "named/ref/optional call requires manual review"; continue; }
                var sm = compilation.GetSemanticModel(call.SyntaxTree);
                foreach (int i in remove)
                {
                    ExpressionSyntax expr = args[i].Expression;
                    var argSymbol = sm.GetSymbolInfo(expr).Symbol;
                    if (expr is not LiteralExpressionSyntax && !(expr is IdentifierNameSyntax && argSymbol is ILocalSymbol or IParameterSymbol))
                        reject = "removed argument evaluation is not proven inert";
                }
            }
            if (calls.Count == 0) reject = "no resolved direct calls";
            string file = Path.GetRelativePath(repo, method.SyntaxTree.FilePath);
            decisions.Add(new { file, name, makeStatic=target.Static, removeParameters=target.Parameters, calls=calls.Count, reason=reject ?? "eligible", symbol=symbol.ToDisplayString() });
            if (reject != null) continue;
            MethodDeclarationSyntax changed = method;
            if (target.Static) changed = changed.WithModifiers(changed.Modifiers.Insert(1, SyntaxFactory.Token(SyntaxKind.StaticKeyword).WithTrailingTrivia(SyntaxFactory.Space)));
            if (remove.Count > 0)
            {
                var parameters = changed.ParameterList.Parameters;
                foreach (int i in remove.OrderDescending()) parameters = parameters.RemoveAt(i);
                changed = changed.WithParameterList(changed.ParameterList.WithParameters(parameters));
                foreach (var call in calls)
                {
                    var arguments = call.ArgumentList.Arguments;
                    foreach (int i in remove.OrderDescending()) arguments = arguments.RemoveAt(i);
                    replacements[call.ArgumentList] = call.ArgumentList.WithArguments(arguments);
                }
            }
            replacements[method.ParameterList] = changed.ParameterList;
            if (target.Static)
            {
                // A token edit keeps nested call argument edits independent of this declaration.
                var privateToken = method.Modifiers.First(t => t.IsKind(SyntaxKind.PrivateKeyword));
                edits.Add(new(file, privateToken.Span.End, 0, " static"));
            }
        }
        foreach (var (original, replacement) in replacements)
            edits.Add(new(Path.GetRelativePath(repo, original.SyntaxTree.FilePath), original.SpanStart, original.Span.Length, replacement.ToString()));
        var files = edits.GroupBy(e=>e.file).Select(g =>
        {
            var ordered = g.OrderBy(e => e.start).ToArray();
            for (int i = 1; i < ordered.Length; i++)
                if (ordered[i-1].start + ordered[i-1].length > ordered[i].start)
                    throw new InvalidOperationException("Overlapping edits require manual review: " + g.Key);
            string original = File.ReadAllText(Path.Combine(repo,g.Key));
            string changed = original;
            foreach (var edit in g.OrderByDescending(e=>e.start)) changed = changed.Remove(edit.start,edit.length).Insert(edit.start,edit.text);
            return new { file=g.Key, original, changed };
        }).ToArray();
        File.WriteAllText(Path.Combine(output,"safe-proposal.json"),JsonSerializer.Serialize(new { decisions, files },new JsonSerializerOptions { WriteIndented=true }));
        Console.WriteLine($"SAFE_PROPOSAL files={files.Length} edits={edits.Count}");
    }
    private static readonly List<Edit> edits = new();
    private record Edit(string file, int start, int length, string text);
}
