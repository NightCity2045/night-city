// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
namespace Content.Meta.Language;

public abstract record MetaSyntaxNode(SourceSpan Span)
{
    internal int SyntaxDepth { get; set; } = 1;
}
public sealed record MetaTypeSyntax(string Name, IReadOnlyList<MetaTypeSyntax> Arguments, SourceSpan Span) : MetaSyntaxNode(Span);
public sealed record MetaCompilationUnit(string Module, IReadOnlyList<MetaImportSyntax> Imports,
    IReadOnlyList<MetaDeclaration> Declarations, SourceSpan Span) : MetaSyntaxNode(Span);
public sealed record MetaImportSyntax(string Module, string Alias, SourceSpan Span) : MetaSyntaxNode(Span);
public abstract record MetaDeclaration(string Name, bool Public, SourceSpan Span) : MetaSyntaxNode(Span);
public sealed record MetaParameter(string Name, MetaTypeSyntax Type, SourceSpan Span) : MetaSyntaxNode(Span);
public sealed record MetaFunctionSyntax(string Name, bool Public, bool Async, IReadOnlyList<MetaParameter> Parameters,
    MetaTypeSyntax ReturnType, MetaBlockSyntax Body, SourceSpan Span) : MetaDeclaration(Name, Public, Span);
public sealed record MetaStructSyntax(string Name, bool Public, IReadOnlyList<MetaParameter> Fields, SourceSpan Span) : MetaDeclaration(Name, Public, Span);
public sealed record MetaEnumSyntax(string Name, bool Public, IReadOnlyList<string> Variants, SourceSpan Span) : MetaDeclaration(Name, Public, Span);
public sealed record MetaGlobalSyntax(string Name, bool Public, string Storage, MetaTypeSyntax Type,
    MetaExpression Initializer, SourceSpan Span) : MetaDeclaration(Name, Public, Span);
public sealed record MetaLinkSyntax(string Name, MetaTypeSyntax Type, string Slot, SourceSpan Span) : MetaDeclaration(Name, false, Span);
public sealed record MetaWatchSyntax(string Link, string Event, bool Async, IReadOnlyList<string> Parameters,
    MetaBlockSyntax Body, SourceSpan Span) : MetaDeclaration(Link + "." + Event, false, Span);
public abstract record MetaStatement(SourceSpan Span) : MetaSyntaxNode(Span);
public sealed record MetaBlockSyntax(IReadOnlyList<MetaStatement> Statements, SourceSpan Span) : MetaStatement(Span);
public sealed record MetaVariableSyntax(string Name, string Storage, MetaTypeSyntax? Type, MetaExpression Initializer,
    SourceSpan Span) : MetaStatement(Span);
public sealed record MetaIfSyntax(MetaExpression Condition, MetaBlockSyntax Then, MetaStatement? Else, SourceSpan Span) : MetaStatement(Span);
public sealed record MetaWhileSyntax(MetaExpression Condition, MetaBlockSyntax Body, SourceSpan Span) : MetaStatement(Span);
public sealed record MetaForSyntax(string Name, MetaExpression Collection, MetaBlockSyntax Body, SourceSpan Span) : MetaStatement(Span);
public sealed record MetaPattern(string Tag, string? Binding, SourceSpan Span) : MetaSyntaxNode(Span);
public sealed record MetaMatchArm(MetaPattern Pattern, MetaBlockSyntax Body, SourceSpan Span) : MetaSyntaxNode(Span);
public sealed record MetaMatchSyntax(MetaExpression Value, IReadOnlyList<MetaMatchArm> Arms, SourceSpan Span) : MetaStatement(Span);
public sealed record MetaReturnSyntax(MetaExpression? Value, SourceSpan Span) : MetaStatement(Span);
public sealed record MetaControlSyntax(string Kind, SourceSpan Span) : MetaStatement(Span);
public sealed record MetaAssignmentSyntax(MetaExpression Target, MetaExpression Value, SourceSpan Span) : MetaStatement(Span);
public sealed record MetaExpressionStatement(MetaExpression Value, SourceSpan Span) : MetaStatement(Span);
public abstract record MetaExpression(SourceSpan Span) : MetaSyntaxNode(Span);
public sealed record MetaLiteralSyntax(string Kind, object? Value, SourceSpan Span) : MetaExpression(Span);
public sealed record MetaNameSyntax(string Name, SourceSpan Span) : MetaExpression(Span);
public sealed record MetaUnarySyntax(string Operator, MetaExpression Operand, SourceSpan Span) : MetaExpression(Span);
public sealed record MetaBinarySyntax(string Operator, MetaExpression Left, MetaExpression Right, SourceSpan Span) : MetaExpression(Span);
public sealed record MetaCallSyntax(MetaExpression Target, IReadOnlyList<MetaExpression> Arguments, SourceSpan Span) : MetaExpression(Span);
public sealed record MetaMemberSyntax(MetaExpression Target, string Member, SourceSpan Span) : MetaExpression(Span);
public sealed record MetaIndexSyntax(MetaExpression Target, MetaExpression Index, SourceSpan Span) : MetaExpression(Span);
public sealed record MetaArraySyntax(IReadOnlyList<MetaExpression> Elements, SourceSpan Span) : MetaExpression(Span);
public sealed record MetaConstructorArgument(string? Name, MetaExpression Value, SourceSpan Span) : MetaSyntaxNode(Span);
public sealed record MetaNewSyntax(MetaTypeSyntax Type, IReadOnlyList<MetaConstructorArgument> Arguments, SourceSpan Span) : MetaExpression(Span);
public sealed record MetaVariantSyntax(string Tag, MetaExpression? Value, SourceSpan Span) : MetaExpression(Span);
public sealed record MetaParseResult(SourceText Source, IReadOnlyList<MetaToken> Tokens, MetaCompilationUnit? Unit,
    IReadOnlyList<MetaDiagnostic> Diagnostics);
