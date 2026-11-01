// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Globalization;
using System.Numerics;
using System.Text;
using Content.Meta.Bytecode;
using Content.Meta.Language;

namespace Content.Meta.Runtime;

public sealed partial class MetaProcess
{
    /// <summary>Read-only expression inspection. It never invokes guest/host code or allocates in the guest heap.</summary>
    public bool TryEvaluateDebug(string expression, out string display, int workLimit = 256)
    {
        display = string.Empty;
        if (!DebugPaused || _running || _frames.Count == 0 || expression == null || Encoding.UTF8.GetByteCount(expression) > 1024 || workLimit is < 1 or > 1024) return false;
        try
        {
            var source = new SourceText("debug.meta", "module debug_inspection; fn inspect()->void { return (" + expression + "); }");
            var parsed = MetaParser.Parse(source, new MetaCompilerLimits(MaxFileBytes: 2048, MaxProjectBytes: 2048, MaxTokens: workLimit + 32, MaxNodes: workLimit + 16, MaxSyntaxDepth: 24, MaxDiagnostics: 1, MaxStringBytes: 1024));
            if (parsed.Diagnostics.Count != 0 || parsed.Unit?.Declarations.Count != 1 || parsed.Unit.Declarations[0] is not MetaFunctionSyntax { Body.Statements.Count: 1 } function || function.Body.Statements[0] is not MetaReturnSyntax { Value: { } syntax }) return false;
            var work = 0; var frame = _frames[^1];
            object Normalize(MetaValue value) => value.Kind switch
            {
                MetaValueKind.Bool => value.AsBool(), MetaValueKind.Int => value.AsInt(), MetaValueKind.Byte => (long) value.Payload,
                MetaValueKind.Float => value.AsFloat(), MetaValueKind.String => GetString(value),
                MetaValueKind.Capability or MetaValueKind.Opaque => throw new InvalidOperationException(),
                _ => value
            };
            object Evaluate(MetaExpression node, int depth)
            {
                if (++work > workLimit || depth > 16) throw new InvalidOperationException();
                switch (node)
                {
                    case MetaLiteralSyntax literal:
                        return literal.Kind switch
                        {
                            "Integer" => ParseDebugInteger((string) literal.Value!),
                            "Float" => FiniteDebugNumber(double.Parse((string) literal.Value!, NumberStyles.Float, CultureInfo.InvariantCulture)),
                            "String" => (string) literal.Value!, "Bool" => (bool) literal.Value!, "Unit" => MetaValue.Unit,
                            _ => throw new InvalidOperationException()
                        };
                    case MetaNameSyntax name:
                        var local = _module.Debug?.Locals.SingleOrDefault(entry => entry.FunctionId == frame.Function.Id && entry.Name == name.Name)?.LocalId ?? -1;
                        if (local < 0 && name.Name.StartsWith("local", StringComparison.Ordinal) && int.TryParse(name.Name.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var slot)) local = slot;
                        if (local >= 0 && local < frame.Locals.Length)
                        {
                            if (frame.Locals[local].TypeId != frame.Function.Locals[local].TypeId) throw new InvalidOperationException();
                            return Normalize(frame.Locals[local]);
                        }
                        var global = Array.FindIndex(_module.Globals, entry => entry.Name == name.Name || entry.Name.EndsWith("." + name.Name, StringComparison.Ordinal));
                        if (global >= 0) return Normalize(_globals[global]);
                        throw new InvalidOperationException();
                    case MetaMemberSyntax member:
                        var target = Evaluate(member.Target, depth + 1);
                        if (target is string text && member.Member == "length") return (long) text.EnumerateRunes().Count();
                        if (target is not MetaValue record) throw new InvalidOperationException();
                        if (member.Member == "length" && record.Kind is MetaValueKind.Array or MetaValueKind.List) return (long) _heap.Get(record).Count;
                        if (record.Kind != MetaValueKind.Record) throw new InvalidOperationException();
                        var field = Array.FindIndex(_module.Types[record.TypeId].Fields, definition => definition.Name == member.Member);
                        if (field < 0) throw new InvalidOperationException();
                        return Normalize(GetField(record, field));
                    case MetaIndexSyntax indexed:
                        var sequence = Evaluate(indexed.Target, depth + 1); var index = Evaluate(indexed.Index, depth + 1);
                        if (sequence is not MetaValue collection || collection.Kind is not (MetaValueKind.Array or MetaValueKind.List) || index is not long at) throw new InvalidOperationException();
                        var contents = _heap.Get(collection); return Normalize(contents.Values[CheckIndex(at, contents.Count)]);
                    case MetaUnarySyntax { Operator: "-", Operand: MetaLiteralSyntax { Kind: "Integer", Value: string magnitude } }:
                        var raw = magnitude.StartsWith("0x", StringComparison.Ordinal) ? BigInteger.Parse("0" + magnitude[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture) : BigInteger.Parse(magnitude, CultureInfo.InvariantCulture);
                        return checked((long) -raw);
                    case MetaUnarySyntax unary:
                        var operand = Evaluate(unary.Operand, depth + 1);
                        return (unary.Operator, operand) switch
                        {
                            ("-", long integer) => checked(-integer), ("-", double number) => FiniteDebugNumber(-number),
                            ("!", bool boolean) => !boolean, ("~", long integer) => ~integer,
                            ("+", long integer) => integer, ("+", double number) => number,
                            _ => throw new InvalidOperationException()
                        };
                    case MetaBinarySyntax binary:
                        var left = Evaluate(binary.Left, depth + 1);
                        if (binary.Operator == "&&" && left is false) return false;
                        if (binary.Operator == "||" && left is true) return true;
                        var right = Evaluate(binary.Right, depth + 1);
                        return EvaluateDebugBinary(binary.Operator, left, right);
                    default: throw new InvalidOperationException(); // Calls, constructors, await, assignment and variants cannot enter execution.
                }
            }
            var result = Evaluate(syntax, 0);
            display = result switch
            {
                long integer => integer.ToString(CultureInfo.InvariantCulture),
                double number when double.IsFinite(number) => (number == 0 ? 0 : number).ToString("R", CultureInfo.InvariantCulture),
                bool boolean => boolean ? "true" : "false",
                string text => TruncateUtf8(text, 512),
                MetaValue value when value.Kind is not (MetaValueKind.Capability or MetaValueKind.Opaque) => DescribeValue(value).Display,
                _ => throw new InvalidOperationException()
            };
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) { display = string.Empty; return false; }
    }

    private static long ParseDebugInteger(string value) => value.StartsWith("0x", StringComparison.Ordinal)
        ? checked((long) BigInteger.Parse("0" + value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture))
        : long.Parse(value, CultureInfo.InvariantCulture);

    private static double FiniteDebugNumber(double value) => double.IsFinite(value) ? value == 0 ? 0 : value : throw new InvalidOperationException();

    private static object EvaluateDebugBinary(string op, object left, object right)
    {
        if (left is long l && right is long r)
        {
            return op switch
            {
                "+" => checked(l + r), "-" => checked(l - r), "*" => checked(l * r),
                "/" when r != 0 && !(l == long.MinValue && r == -1) => l / r,
                "%" when r != 0 => l == long.MinValue && r == -1 ? 0L : l % r,
                "<<" when r is >= 0 and <= 63 => l << (int) r, ">>" when r is >= 0 and <= 63 => l >> (int) r,
                "&" => l & r, "|" => l | r, "^" => l ^ r,
                "==" => l == r, "!=" => l != r, "<" => l < r, "<=" => l <= r, ">" => l > r, ">=" => l >= r,
                _ => throw new InvalidOperationException()
            };
        }
        if (left is double lf && right is double rf)
        {
            return op switch
            {
                "+" => FiniteDebugNumber(lf + rf), "-" => FiniteDebugNumber(lf - rf), "*" => FiniteDebugNumber(lf * rf), "/" when rf != 0 => FiniteDebugNumber(lf / rf), "%" when rf != 0 => FiniteDebugNumber(lf % rf),
                "==" => lf == rf, "!=" => lf != rf, "<" => lf < rf, "<=" => lf <= rf, ">" => lf > rf, ">=" => lf >= rf,
                _ => throw new InvalidOperationException()
            };
        }
        if (left is string ls && right is string rs)
        {
            var comparison = CompareScalars(ls, rs);
            return op switch { "+" when Encoding.UTF8.GetByteCount(ls) + Encoding.UTF8.GetByteCount(rs) <= 512 => ls + rs, "==" => comparison == 0, "!=" => comparison != 0, "<" => comparison < 0, "<=" => comparison <= 0, ">" => comparison > 0, ">=" => comparison >= 0, _ => throw new InvalidOperationException() };
        }
        if (left is bool lb && right is bool rb) return op switch { "&&" => lb && rb, "||" => lb || rb, "==" => lb == rb, "!=" => lb != rb, _ => throw new InvalidOperationException() };
        if (left is MetaValue lv && right is MetaValue rv && lv.Kind == MetaValueKind.Enum && rv.Kind == MetaValueKind.Enum && lv.TypeId == rv.TypeId)
            return op switch { "==" => lv == rv, "!=" => lv != rv, _ => throw new InvalidOperationException() };
        throw new InvalidOperationException();
    }
}
