// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Meta.Language;

public sealed class MetaParser
{
    private readonly List<MetaToken> _tokens;
    private readonly CompilationBudget _budget;
    private int _position;
    private int _depth;
    private MetaToken Current => _tokens[Math.Min(_position, _tokens.Count - 1)];
    private MetaToken Previous => _tokens[Math.Max(0, _position - 1)];
    private MetaParser(List<MetaToken> tokens, CompilationBudget budget) => (_tokens, _budget) = (tokens, budget);

    public static MetaParseResult Parse(SourceText source, MetaCompilerLimits? limits = null, CancellationToken cancellation = default)
    {
        var budget = new CompilationBudget(limits ?? new MetaCompilerLimits(), cancellation);
        return Parse(source, budget);
    }

    internal static MetaParseResult Parse(SourceText source, CompilationBudget budget)
    {
        var tokens = new List<MetaToken>();
        MetaCompilationUnit? unit = null;
        try
        {
            MetaLexer.Scan(source, budget, tokens);
            unit = new MetaParser(tokens, budget).Program();
        }
        catch (MetaCompilationAbortException) { }
        return new(source, tokens, unit, budget.Diagnostics.ToArray());
    }

    private T Node<T>(T node) where T : MetaSyntaxNode
    {
        _budget.Node(node.Span);
        node.SyntaxDepth = node switch
        {
            MetaUnarySyntax u => 1 + u.Operand.SyntaxDepth,
            MetaBinarySyntax b => 1 + Math.Max(b.Left.SyntaxDepth, b.Right.SyntaxDepth),
            MetaMemberSyntax m => 1 + m.Target.SyntaxDepth,
            MetaIndexSyntax i => 1 + Math.Max(i.Target.SyntaxDepth, i.Index.SyntaxDepth),
            MetaCallSyntax c => 1 + Math.Max(c.Target.SyntaxDepth, c.Arguments.Select(a => a.SyntaxDepth).DefaultIfEmpty().Max()),
            MetaArraySyntax a => 1 + a.Elements.Select(e => e.SyntaxDepth).DefaultIfEmpty().Max(),
            MetaNewSyntax n => 1 + n.Arguments.Select(a => a.Value.SyntaxDepth).DefaultIfEmpty().Max(),
            MetaVariantSyntax v => 1 + (v.Value?.SyntaxDepth ?? 0),
            _ => 1
        };
        if (node.SyntaxDepth > _budget.Limits.MaxSyntaxDepth) throw _budget.Limit("E403", node.Span);
        return node;
    }
    private SourceSpan From(MetaToken start) => SourceSpan.Cover(start.Span, Previous.Span);
    private MetaToken Take() { var token = Current; if (token.Kind != MetaTokenKind.End) _position++; return token; }
    private bool At(string text) => Current.Text == text;
    private bool Eat(string text) { if (!At(text)) return false; Take(); return true; }
    private MetaToken Expect(string text)
    {
        if (At(text)) return Take();
        _budget.Report("E102", Current.Span, text, Current.Text);
        throw new ParseRecoveryException();
    }
    private string Identifier()
    {
        if (Current.Kind == MetaTokenKind.Identifier) return Take().Text;
        _budget.Report("E102", Current.Span, "identifier", Current.Text);
        throw new ParseRecoveryException();
    }
    private string QualifiedName()
    {
        var name = Identifier();
        while (Eat(".")) name += "." + Identifier();
        return name;
    }
    private void Enter()
    {
        if (++_depth > _budget.Limits.MaxSyntaxDepth) throw _budget.Limit("E403", Current.Span);
    }
    private void Recover(bool declaration)
    {
        var before = _position;
        while (Current.Kind != MetaTokenKind.End)
        {
            if (Eat(";")) return;
            if (At("}")) { if (declaration) Take(); return; }
            if (declaration && _position != before && Current.Text is "pub" or "fn" or "async" or "struct" or "enum" or "link" or "watch" or "const" or "let" or "var") return;
            Take();
        }
    }

    private MetaCompilationUnit? Program()
    {
        var start = Current;
        string module;
        try { Expect("module"); module = QualifiedName(); Expect(";"); }
        catch (ParseRecoveryException) { return null; }
        var imports = new List<MetaImportSyntax>();
        while (At("import"))
        {
            var importStart = Take();
            try
            {
                var name = QualifiedName();
                var alias = Eat("as") ? Identifier() : name[(name.LastIndexOf('.') + 1)..];
                Expect(";");
                imports.Add(Node(new MetaImportSyntax(name, alias, From(importStart))));
            }
            catch (ParseRecoveryException) { Recover(true); }
        }
        var declarations = new List<MetaDeclaration>();
        while (Current.Kind != MetaTokenKind.End)
        {
            var before = _position;
            try { declarations.Add(Declaration()); }
            catch (ParseRecoveryException) { Recover(true); }
            if (_position == before) Take();
        }
        return Node(new MetaCompilationUnit(module, imports, declarations, From(start)));
    }

    private MetaDeclaration Declaration()
    {
        var start = Current;
        var exported = Eat("pub");
        var async = Eat("async");
        if (Eat("fn"))
        {
            var name = Identifier();
            Expect("(");
            var parameters = new List<MetaParameter>();
            if (!At(")")) do { parameters.Add(Parameter()); } while (Eat(","));
            Expect(")"); Expect("->");
            var result = Type();
            return Node(new MetaFunctionSyntax(name, exported, async, parameters, result, Block(), From(start)));
        }
        if (Eat("watch") && !exported)
        {
            if (Current.Kind != MetaTokenKind.Identifier) { _budget.Report("E104", Current.Span); throw new ParseRecoveryException(); }
            var link = Identifier(); Expect("."); var evt = Identifier(); Expect("(");
            var names = new List<string>();
            if (!At(")")) do { names.Add(Identifier()); } while (Eat(","));
            Expect(")");
            return Node(new MetaWatchSyntax(link, evt, async, names, Block(), From(start)));
        }
        if (async) { _budget.Report("E102", start.Span, "async-target"); throw new ParseRecoveryException(); }
        if (Eat("struct"))
        {
            var name = Identifier(); Expect("{"); var fields = new List<MetaParameter>();
            while (!At("}") && Current.Kind != MetaTokenKind.End) { fields.Add(Parameter()); Expect(";"); }
            Expect("}");
            return Node(new MetaStructSyntax(name, exported, fields, From(start)));
        }
        if (Eat("enum"))
        {
            var name = Identifier(); Expect("{"); var variants = new List<string> { Identifier() };
            while (Eat(",") && !At("}")) variants.Add(Identifier());
            Expect("}");
            return Node(new MetaEnumSyntax(name, exported, variants, From(start)));
        }
        if (Eat("link") && !exported)
        {
            var name = Identifier(); Expect(":"); var type = Type(); Expect("=");
            if (Current.Kind != MetaTokenKind.String) { _budget.Report("E103", Current.Span); throw new ParseRecoveryException(); }
            var slot = (string) Take().Value!; Expect(";");
            return Node(new MetaLinkSyntax(name, type, slot, From(start)));
        }
        if (Current.Text is "let" or "var" or "const")
        {
            var storage = Take().Text;
            if (exported && storage != "const") _budget.Report("E215", start.Span, "export-storage");
            var name = Identifier(); Expect(":"); var type = Type(); Expect("="); var value = Expression(); Expect(";");
            return Node(new MetaGlobalSyntax(name, exported, storage, type, value, From(start)));
        }
        _budget.Report("E102", Current.Span, "declaration", Current.Text);
        throw new ParseRecoveryException();
    }

    private MetaParameter Parameter()
    {
        var start = Current; var name = Identifier(); Expect(":");
        return Node(new MetaParameter(name, Type(), From(start)));
    }

    private MetaTypeSyntax Type(int depth = 0)
    {
        if (depth >= _budget.Limits.MaxTypeDepth) throw _budget.Limit("E404", Current.Span);
        var start = Current;
        string name;
        if (Current.Text is "void" or "bool" or "byte" or "int" or "float" or "string" or "array" or "list" or "map" or "option" or "result") name = Take().Text;
        else name = QualifiedName();
        var arguments = new List<MetaTypeSyntax>();
        if (name is "array" or "list" or "map" or "option" or "result")
        {
            Expect("<"); arguments.Add(Type(depth + 1));
            if (name == "map") { Expect(","); arguments.Add(Type(depth + 1)); }
            if (At(">>") || At(">="))
            {
                var token = Current;
                _tokens[_position] = token with { Text = ">", Span = token.Span with { EndUtf8Byte = token.Span.StartUtf8Byte + 1 } };
                _tokens.Insert(_position + 1, token with { Text = token.Text[1..], Span = token.Span with { StartUtf8Byte = token.Span.StartUtf8Byte + 1 } });
            }
            Expect(">");
        }
        return Node(new MetaTypeSyntax(name, arguments, From(start)));
    }

    private MetaBlockSyntax Block()
    {
        Enter();
        try
        {
            var start = Expect("{"); var statements = new List<MetaStatement>();
            while (!At("}") && Current.Kind != MetaTokenKind.End)
            {
                var before = _position;
                try { statements.Add(Statement()); }
                catch (ParseRecoveryException) { Recover(false); }
                if (_position == before && !At("}")) Take();
            }
            Expect("}");
            return Node(new MetaBlockSyntax(statements, From(start)));
        }
        finally { _depth--; }
    }

    private MetaStatement Statement()
    {
        Enter();
        try { return StatementCore(); }
        finally { _depth--; }
    }

    private MetaStatement StatementCore()
    {
        var start = Current;
        if (At("{")) return Block();
        if (Current.Text is "let" or "var" or "const")
        {
            var storage = Take().Text; var name = Identifier();
            var type = Eat(":") ? Type() : null;
            if (storage == "const" && type == null) _budget.Report("E203", start.Span, "const-type");
            Expect("="); var value = Expression(); Expect(";");
            return Node(new MetaVariableSyntax(name, storage, type, value, From(start)));
        }
        if (Eat("if"))
        {
            Expect("("); var condition = Expression(); Expect(")"); var then = Block(); MetaStatement? other = null;
            if (Eat("else")) other = At("if") ? Statement() : Block();
            return Node(new MetaIfSyntax(condition, then, other, From(start)));
        }
        if (Eat("while"))
        {
            Expect("("); var condition = Expression(); Expect(")");
            return Node(new MetaWhileSyntax(condition, Block(), From(start)));
        }
        if (Eat("for"))
        {
            Expect("("); Expect("let"); var name = Identifier(); Expect("in"); var collection = Expression(); Expect(")");
            return Node(new MetaForSyntax(name, collection, Block(), From(start)));
        }
        if (Eat("match"))
        {
            Expect("("); var value = Expression(); Expect(")"); Expect("{"); var arms = new List<MetaMatchArm>();
            while (!At("}") && Current.Kind != MetaTokenKind.End)
            {
                var armStart = Current;
                var tag = Take().Text;
                string? binding = null;
                if (tag is "some" or "ok" or "err") { Expect("("); binding = Eat("_") ? "_" : Identifier(); Expect(")"); }
                else if (tag is not ("none" or "true" or "false" or "_")) while (Eat(".")) tag += "." + Identifier();
                var pattern = Node(new MetaPattern(tag, binding, From(armStart)));
                Expect("=>"); var body = Block(); arms.Add(Node(new MetaMatchArm(pattern, body, From(armStart))));
            }
            if (arms.Count == 0) _budget.Report("E102", Current.Span, "match-arm");
            Expect("}");
            return Node(new MetaMatchSyntax(value, arms, From(start)));
        }
        if (Eat("return"))
        {
            var value = At(";") ? null : Expression(); Expect(";"); return Node(new MetaReturnSyntax(value, From(start)));
        }
        if (Current.Text is "break" or "continue" or "yield")
        {
            var kind = Take().Text; Expect(";"); return Node(new MetaControlSyntax(kind, From(start)));
        }
        var expression = Expression();
        if (Eat("=")) { var value = Expression(); Expect(";"); return Node(new MetaAssignmentSyntax(expression, value, From(start))); }
        Expect(";"); return Node(new MetaExpressionStatement(expression, From(start)));
    }

    private MetaExpression Expression(int precedence = 0)
    {
        Enter();
        try
        {
            var left = Unary();
            while (Precedence(Current.Text) > precedence)
            {
                var op = Take(); var right = Expression(Precedence(op.Text));
                left = Node(new MetaBinarySyntax(op.Text, left, right, SourceSpan.Cover(left.Span, right.Span)));
            }
            return left;
        }
        finally { _depth--; }
    }

    private MetaExpression Unary()
    {
        if (Current.Text is "!" or "~" or "+" or "-" or "await")
        {
            var start = Take(); Enter();
            try { return Node(new MetaUnarySyntax(start.Text, Unary(), From(start))); }
            finally { _depth--; }
        }
        var expression = Primary();
        while (true)
        {
            if (Eat(".")) expression = Node(new MetaMemberSyntax(expression, Identifier(), SourceSpan.Cover(expression.Span, Previous.Span)));
            else if (Eat("[")) { var index = Expression(); Expect("]"); expression = Node(new MetaIndexSyntax(expression, index, SourceSpan.Cover(expression.Span, Previous.Span))); }
            else if (Eat("(")) { var args = Arguments(")"); expression = Node(new MetaCallSyntax(expression, args, SourceSpan.Cover(expression.Span, Previous.Span))); }
            else return expression;
        }
    }

    private List<MetaExpression> Arguments(string end)
    {
        var args = new List<MetaExpression>();
        if (!At(end)) do { args.Add(Expression()); } while (Eat(","));
        Expect(end); return args;
    }

    private MetaExpression Primary()
    {
        var token = Take();
        if (token.Kind is MetaTokenKind.Integer or MetaTokenKind.Float or MetaTokenKind.String)
            return Node(new MetaLiteralSyntax(token.Kind.ToString(), token.Value, token.Span));
        if (token.Text is "true" or "false") return Node(new MetaLiteralSyntax("Bool", token.Text == "true", token.Span));
        if (token.Kind == MetaTokenKind.Identifier) return Node(new MetaNameSyntax(token.Text, token.Span));
        if (token.Text == "none") return Node(new MetaVariantSyntax("none", null, token.Span));
        if (token.Text is "some" or "ok" or "err")
        {
            Expect("("); var value = Expression(); Expect(")"); return Node(new MetaVariantSyntax(token.Text, value, From(token)));
        }
        if (token.Text == "(")
        {
            if (Eat(")")) return Node(new MetaLiteralSyntax("Unit", null, From(token)));
            var value = Expression(); Expect(")"); return value;
        }
        if (token.Text == "[") return Node(new MetaArraySyntax(Arguments("]"), From(token)));
        if (token.Text == "new")
        {
            var type = Type(); Expect("("); var args = new List<MetaConstructorArgument>();
            if (!At(")"))
                do
                {
                    var start = Current; string? name = null;
                    if (Current.Kind == MetaTokenKind.Identifier && _position + 1 < _tokens.Count && _tokens[_position + 1].Text == ":") { name = Take().Text; Take(); }
                    args.Add(Node(new MetaConstructorArgument(name, Expression(), From(start))));
                } while (Eat(","));
            Expect(")"); return Node(new MetaNewSyntax(type, args, From(token)));
        }
        _budget.Report("E102", token.Span, "expression", token.Text);
        throw new ParseRecoveryException();
    }

    private static int Precedence(string op) => op switch
    {
        "||" => 1, "&&" => 2, "|" => 3, "^" => 4, "&" => 5, "==" or "!=" => 6,
        "<" or "<=" or ">" or ">=" => 7, "<<" or ">>" => 8, "+" or "-" => 9, "*" or "/" or "%" => 10, _ => 0
    };
    private sealed class ParseRecoveryException : Exception;
}
