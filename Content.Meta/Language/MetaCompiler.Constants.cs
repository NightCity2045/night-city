// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Globalization;
using System.Numerics;
using System.Text;
using Content.Meta.Bytecode;

namespace Content.Meta.Language;

public sealed partial class MetaCompiler
{
    private sealed partial class Session
    {
        private int _constantDepth;
        private void ConstantError(string code, SourceSpan span, params string[] arguments)
        {
            _budget.Report(code, span, arguments); throw new SemanticAbortException();
        }
        private ConstantValue Literal(MetaLiteralSyntax literal, int? expected)
        {
            if (literal.Kind == "Unit") return new(0, null);
            if (literal.Kind == "Bool") return new(1, literal.Value);
            if (literal.Kind == "String") return new(5, literal.Value);
            var raw = (string) literal.Value!;
            if (literal.Kind == "Float")
            {
                if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value)) ConstantError("E204", literal.Span, raw);
                return new(4, value == 0 ? 0.0 : value);
            }
            var integer = IntegerMagnitude(raw, literal.Span);
            if (expected == 2)
            {
                if (integer < 0 || integer > 255) ConstantError("E204", literal.Span, raw);
                return new(2, (byte) integer);
            }
            if (integer > long.MaxValue) ConstantError("E204", literal.Span, raw);
            return new(3, (long) integer);
        }

        private BigInteger IntegerMagnitude(string raw, SourceSpan span)
        {
            if (raw.Length > 1024) ConstantError("E204", span, "integer-size");
            if (raw.StartsWith("0x", StringComparison.Ordinal))
            {
                if (BigInteger.TryParse("0" + raw[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex)) return hex;
            }
            else if (BigInteger.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var integer)) return integer;
            ConstantError("E204", span, raw); return 0;
        }

        private ConstantValue EvaluateGlobalConstant(VariableSymbol symbol)
        {
            if (symbol.Constant != null) return symbol.Constant;
            if (symbol.Evaluating) ConstantError("E213", symbol.Span, symbol.Name);
            if (++_constantDepth > _budget.Limits.MaxSyntaxDepth) throw _budget.Limit("E409", symbol.Span);
            symbol.Evaluating = true;
            try
            {
                var checker = new Emitter(this, new FunctionSymbol(-1, symbol.Module, "$const", false, [], 0, new([], symbol.Span), [], symbol.Span), true);
                checker.CheckConstant(symbol.Initializer!, symbol.Type);
                return symbol.Constant = EvaluateConstant(symbol.Module, symbol.Initializer!, symbol.Type, null);
            }
            finally { symbol.Evaluating = false; _constantDepth--; }
        }

        private ConstantValue EvaluateConstant(ModuleSymbol module, MetaExpression expression, int? expected,
            Dictionary<string, VariableSymbol>? locals)
        {
            _budget.Cancellation.ThrowIfCancellationRequested();
            if (++_budget.ConstantSteps > _budget.Limits.MaxConstantSteps) throw _budget.Limit("E409", expression.Span);
            ConstantValue value;
            try
            {
                value = expression switch
                {
                    MetaLiteralSyntax literal => Literal(literal, expected),
                    MetaNameSyntax name => ConstantName(module, name, locals),
                    MetaUnarySyntax unary => ConstantUnary(module, unary, expected, locals),
                    MetaBinarySyntax binary => ConstantBinary(module, binary, expected, locals),
                    MetaMemberSyntax member => ConstantMember(module, member, locals),
                    MetaNewSyntax constructor => ConstantConstructor(module, constructor, locals),
                    _ => throw new InvalidConstantException()
                };
            }
            catch (Exception e) when (e is OverflowException or DivideByZeroException or ArithmeticException)
            {
                ConstantError("E204", expression.Span, "numeric-fault"); return new(0, null);
            }
            catch (InvalidConstantException)
            {
                ConstantError("E216", expression.Span, "nonconstant-expression"); return new(0, null);
            }
            if (expected != null && value.Type != expected.Value) ConstantError("E201", expression.Span, _types[value.Type].Name, _types[expected.Value].Name);
            return value;
        }

        private ConstantValue ConstantName(ModuleSymbol module, MetaNameSyntax name, Dictionary<string, VariableSymbol>? locals)
        {
            var variable = locals?.GetValueOrDefault(name.Name) ?? module.Globals.GetValueOrDefault(name.Name);
            if (variable == null || variable.Storage != "const") throw new InvalidConstantException();
            variable.Read = true; return variable.Constant ?? EvaluateGlobalConstant(variable);
        }

        private ConstantValue ConstantUnary(ModuleSymbol module, MetaUnarySyntax unary, int? expected, Dictionary<string, VariableSymbol>? locals)
        {
            if (unary.Operator == "-" && unary.Operand is MetaLiteralSyntax { Kind: "Integer", Value: string raw } && IntegerMagnitude(raw, unary.Span) == (BigInteger) long.MaxValue + 1)
                return new(3, long.MinValue);
            var value = EvaluateConstant(module, unary.Operand, expected, locals);
            return (unary.Operator, value.Type) switch
            {
                ("!", 1) => new(1, !(bool) value.Value!), ("~", 3) => new(3, ~(long) value.Value!),
                ("-", 3) => new(3, checked(-(long) value.Value!)), ("-", 4) => Finite(-(double) value.Value!),
                ("+", 3 or 4) => value, _ => throw new InvalidConstantException()
            };
        }

        private ConstantValue ConstantBinary(ModuleSymbol module, MetaBinarySyntax binary, int? expected, Dictionary<string, VariableSymbol>? locals)
        {
            var left = EvaluateConstant(module, binary.Left, null, locals);
            if (binary.Operator == "&&" && left.Type == 1 && !(bool) left.Value!) return new(1, false);
            if (binary.Operator == "||" && left.Type == 1 && (bool) left.Value!) return new(1, true);
            var right = EvaluateConstant(module, binary.Right, left.Type, locals);
            if (binary.Operator is "==" or "!=")
            {
                if (!Comparable(left.Type, new())) throw new InvalidConstantException();
                var equal = ConstantEquals(left, right); return new(1, binary.Operator == "==" ? equal : !equal);
            }
            if (binary.Operator is "<" or "<=" or ">" or ">=")
            {
                var compared = left.Type switch
                {
                    2 => ((byte) left.Value!).CompareTo((byte) right.Value!), 3 => ((long) left.Value!).CompareTo((long) right.Value!),
                    4 => ((double) left.Value!).CompareTo((double) right.Value!), 5 => CompareScalarStrings((string) left.Value!, (string) right.Value!),
                    _ => throw new InvalidConstantException()
                };
                return new(1, binary.Operator switch { "<" => compared < 0, "<=" => compared <= 0, ">" => compared > 0, _ => compared >= 0 });
            }
            if (left.Type == 1 && binary.Operator is "&&" or "||") return new(1, (bool) right.Value!);
            if (left.Type == 5 && binary.Operator == "+")
            {
                var joined = (string) left.Value! + (string) right.Value!;
                if (System.Text.Encoding.UTF8.GetByteCount(joined) > _budget.Limits.MaxStringBytes) throw _budget.Limit("E405", binary.Span);
                return new(5, joined);
            }
            if (left.Type == 3)
            {
                var a = (long) left.Value!; var b = (long) right.Value!;
                return new(3, binary.Operator switch
                {
                    "+" => checked(a + b), "-" => checked(a - b), "*" => checked(a * b), "/" => checked(a / b),
                    "%" => a == long.MinValue && b == -1 ? 0L : a % b, "&" => a & b, "|" => a | b, "^" => a ^ b,
                    "<<" when b is >= 0 and <= 63 => unchecked(a << (int) b), ">>" when b is >= 0 and <= 63 => a >> (int) b,
                    _ => throw new InvalidConstantException()
                });
            }
            if (left.Type == 4)
            {
                var a = (double) left.Value!; var b = (double) right.Value!;
                if (b == 0 && binary.Operator is "/" or "%") throw new DivideByZeroException();
                return Finite(binary.Operator switch { "+" => a + b, "-" => a - b, "*" => a * b, "/" => a / b, "%" => a % b, _ => throw new InvalidConstantException() });
            }
            throw new InvalidConstantException();
        }

        private static ConstantValue Finite(double value)
        {
            if (!double.IsFinite(value)) throw new ArithmeticException();
            return new(4, value == 0 ? 0.0 : value);
        }
        private static int CompareScalarStrings(string left, string right)
        {
            var a = left.EnumerateRunes().GetEnumerator(); var b = right.EnumerateRunes().GetEnumerator();
            while (true)
            {
                var hasA = a.MoveNext(); var hasB = b.MoveNext();
                if (!hasA || !hasB) return hasA.CompareTo(hasB);
                var compared = a.Current.Value.CompareTo(b.Current.Value); if (compared != 0) return compared;
            }
        }
        private static bool ConstantEquals(ConstantValue left, ConstantValue right) => left.Value is ConstantValue[] a && right.Value is ConstantValue[] b
            ? a.Length == b.Length && a.Zip(b).All(pair => ConstantEquals(pair.First, pair.Second)) : Equals(left.Value, right.Value);

        private ConstantValue ConstantMember(ModuleSymbol module, MetaMemberSyntax member, Dictionary<string, VariableSymbol>? locals)
        {
            var name = Emitter.ExpressionName(member.Target);
            if (name != null)
            {
                var syntax = new MetaTypeSyntax(name, [], member.Target.Span);
                var possible = _typeNames.ContainsKey(module.Syntax.Module + "." + name) || module.Syntax.Imports.Any(i => _typeNames.ContainsKey(i.Module + "." + name)) || name is "ErrorCode";
                var dot = name.IndexOf('.');
                if (dot > 0 && module.Aliases.TryGetValue(name[..dot], out var target)) possible |= _typeNames.ContainsKey(target + name[dot..]);
                if (possible)
                {
                    var type = ResolveType(module, syntax);
                    if (_types[type].Kind == MetaTypeKind.Enum)
                    {
                        var variant = _types[type].Variants.FirstOrDefault(v => v.Name == member.Member);
                        if (variant == null) ConstantError("E225", member.Span, member.Member);
                        return new(type, variant!.Tag);
                    }
                }
                if (member.Target is MetaNameSyntax alias && module.Aliases.TryGetValue(alias.Name, out var owner) && _modules.TryGetValue(owner, out var other) &&
                    other.Globals.TryGetValue(member.Member, out var constant) && other.Values[member.Member] is MetaGlobalSyntax { Public: true, Storage: "const" })
                    return EvaluateGlobalConstant(constant);
            }
            var value = EvaluateConstant(module, member.Target, null, locals);
            if (_types[value.Type].Kind != MetaTypeKind.Struct) throw new InvalidConstantException();
            var field = Array.FindIndex(_types[value.Type].Fields, f => f.Name == member.Member);
            if (field < 0) ConstantError("E225", member.Span, member.Member);
            return ((ConstantValue[]) value.Value!)[field];
        }

        private ConstantValue ConstantConstructor(ModuleSymbol module, MetaNewSyntax constructor, Dictionary<string, VariableSymbol>? locals)
        {
            var id = ResolveType(module, constructor.Type); var type = _types[id];
            if (type.Kind != MetaTypeKind.Struct) throw new InvalidConstantException();
            var values = new Dictionary<string, ConstantValue>(StringComparer.Ordinal);
            foreach (var argument in constructor.Arguments)
            {
                var field = type.Fields.FirstOrDefault(f => f.Name == argument.Name);
                if (field == null || argument.Name == null || values.ContainsKey(argument.Name)) ConstantError("E228", argument.Span);
                values.Add(argument.Name!, EvaluateConstant(module, argument.Value, field!.TypeId, locals));
            }
            if (values.Count != type.Fields.Length) ConstantError("E228", constructor.Span);
            return new(id, type.Fields.Select(f => values[f.Name]).ToArray());
        }
        private sealed class InvalidConstantException : Exception;
    }
}
