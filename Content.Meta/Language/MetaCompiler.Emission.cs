// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Globalization;
using System.Numerics;
using Content.Meta.Bytecode;
using Content.Meta.Verification;

namespace Content.Meta.Language;

public sealed partial class MetaCompiler
{
    private sealed partial class Session
    {
        private MetaModule EmitModule()
        {
            foreach (var module in _modules.Values)
                foreach (var constant in module.Globals.Values.Where(v => v.Storage == "const"))
                    try
                    {
                        var value = EvaluateGlobalConstant(constant);
                        if (module.Values[constant.Name].Public) Constant(value);
                    }
                    catch (SemanticAbortException) { }
            var initializers = SortGlobalInitializers();
            FunctionSymbol? initializer = null;
            if (_globals.Count > 0)
            {
                var root = _modules[_request.RootModule];
                initializer = new(_functions.Count, root, "$initialize", false, [], 0, new([], root.Syntax.Span), [], root.Syntax.Span);
                _functions.Add(initializer);
            }
            var definitions = new List<MetaFunctionDef>();
            var code = new List<byte>();
            var spans = new List<MetaDebugSpan>();
            var names = new List<MetaLocalName>();
            foreach (var function in _functions)
            {
                if (_binaryOrigins.ContainsKey(function))
                {
                    definitions.Add(new() { Id = function.Id, Name = function.Module.Syntax.Module + "." + function.Name, ParameterTypes = function.Parameters, ReturnType = function.ReturnType, Async = function.Async });
                    continue;
                }
                var emitter = new Emitter(this, function, function == initializer);
                try
                {
                    if (function == initializer) emitter.Initializers(initializers);
                    else emitter.Body();
                    var emitted = emitter.Finish();
                    definitions.Add(new()
                    {
                        Id = function.Id, Name = function.Module.Syntax.Module + "." + function.Name, ParameterTypes = function.Parameters,
                        ReturnType = function.ReturnType, Async = function.Async, Initializer = function == initializer,
                        Locals = emitter.Locals.ToArray(), CodeOffset = code.Count, CodeLength = emitted.Length, MaxStack = emitter.MaxStack
                    });
                    code.AddRange(emitted);
                    spans.AddRange(emitter.DebugSpans);
                    names.AddRange(emitter.LocalNames);
                }
                catch (SemanticAbortException) { }
            }
            // A monotone fixed point over the bounded static call graph; recursion cannot omit effects.
            for (var pass = 0; pass < _functions.Count; pass++)
            {
                var changed = false;
                foreach (var function in _functions)
                {
                    var effects = function.Calls.Aggregate(function.Effects, (e, id) => e | _functions[id].Effects);
                    if (effects != function.Effects) { function.Effects = effects; changed = true; }
                }
                if (!changed) break;
                _budget.Cancellation.ThrowIfCancellationRequested();
            }
            var main = _modules[_request.RootModule].Functions.GetValueOrDefault("main");
            return LinkBinaryCode(new MetaModule
            {
                Manifest = new()
                {
                    PackageName = _request.PackageName, PackageVersion = _request.PackageVersion, CompilerBuild = BuildId,
                    SourceTreeHash = MetaApiCatalog.HashText(string.Join('\n', _sources.Select(s => s.FileId + ":" + s.Revision))),
                    LockHash = MetaApiCatalog.HashText(LockIdentity()), ApiCatalogHash = _catalog.Hash, RootModule = _request.RootModule,
                    MainFunctionId = main?.Id, InitializerFunctionId = initializer?.Id, RequiredScopes = _scopes.Order(StringComparer.Ordinal).ToArray()
                },
                Types = _types.ToArray(), Constants = _constants.ToArray(), Functions = definitions.ToArray(), Code = code.ToArray(),
                Imports = _imports.ToArray(), Links = _links.ToArray(), Watches = _watches.ToArray(),
                Globals = _globals.Select(g => new MetaGlobalDef(g.Slot, g.Type, g.Mutable, g.Module.Syntax.Module + "." + g.Name)).ToArray(),
                Debug = new(_sources.Select((s, i) => new MetaDebugFile(i, s.FileId, s.Revision)).ToArray(), spans.ToArray(), names.ToArray())
            });
        }

        private List<VariableSymbol> SortGlobalInitializers()
        {
            var result = new List<VariableSymbol>(); var done = new HashSet<VariableSymbol>(); var active = new HashSet<VariableSymbol>();
            void Visit(VariableSymbol symbol)
            {
                if (done.Contains(symbol)) return;
                if (!active.Add(symbol)) { _budget.Report("E213", symbol.Span, symbol.Name); return; }
                foreach (var expression in Descendants(symbol.Initializer!))
                {
                    if (expression is MetaNameSyntax name && symbol.Module.Globals.TryGetValue(name.Name, out var target) && target.Storage != "const") Visit(target);
                    if (expression is MetaCallSyntax or MetaUnarySyntax { Operator: "await" }) _budget.Report("E212", expression.Span, "global-effect");
                }
                active.Remove(symbol); done.Add(symbol); result.Add(symbol);
            }
            foreach (var global in _globals.Where(g => !_binaryGlobalSymbols.Contains(g))) Visit(global);
            return result;
        }

        private static IEnumerable<MetaExpression> Descendants(MetaExpression expression)
        {
            yield return expression;
            var children = expression switch
            {
                MetaUnarySyntax u => [u.Operand], MetaBinarySyntax b => new[] { b.Left, b.Right }, MetaCallSyntax c => c.Arguments.Prepend(c.Target),
                MetaMemberSyntax m => [m.Target], MetaIndexSyntax i => new[] { i.Target, i.Index }, MetaArraySyntax a => a.Elements,
                MetaNewSyntax n => n.Arguments.Select(a => a.Value), MetaVariantSyntax { Value: not null } v => [v.Value], _ => Array.Empty<MetaExpression>()
            };
            foreach (var child in children) foreach (var descendant in Descendants(child)) yield return descendant;
        }

        private int Constant(ConstantValue constant)
        {
            var kind = _types[constant.Type].Kind;
            MetaConstantDef definition;
            if (kind == MetaTypeKind.Struct)
            {
                definition = new() { TypeId = constant.Type, Kind = MetaConstantKind.Struct, Items = ((ConstantValue[]) constant.Value!).Select(Constant).ToArray() };
            }
            else
            {
                var text = kind switch
                {
                    MetaTypeKind.Unit => null, MetaTypeKind.Bool => (bool) constant.Value! ? "true" : "false",
                    MetaTypeKind.Float => unchecked((ulong) BitConverter.DoubleToInt64Bits((double) constant.Value!)).ToString("x16", CultureInfo.InvariantCulture),
                    MetaTypeKind.String => (string) constant.Value!, _ => Convert.ToString(constant.Value, CultureInfo.InvariantCulture)
                };
                definition = new() { TypeId = constant.Type, Kind = Enum.Parse<MetaConstantKind>(kind.ToString()), Value = text };
            }
            var key = definition.TypeId + ":" + definition.Kind + ":" + definition.Value + ":" + string.Join(',', definition.Items);
            if (_constantIds.TryGetValue(key, out var existing)) return existing;
            var id = _constants.Count;
            if (id >= 16384) throw _budget.Limit("E407", EmptySpan);
            _constants.Add(definition); _constantIds.Add(key, id); return id;
        }

        private sealed partial class Emitter
        {
            private readonly Session _s;
            private readonly FunctionSymbol _function;
            private readonly bool _initializer;
            private ModuleSymbol _module;
            private readonly List<MetaIrInstruction> _instructions = new();
            private readonly Dictionary<string, VariableSymbol> _locals = new(StringComparer.Ordinal);
            private readonly Stack<List<string>> _scopes = new();
            private readonly Stack<SourceSpan> _semanticScopes = new();
            private readonly Stack<(List<int> Breaks, List<int> Continues)> _loops = new();
            private readonly Dictionary<int, int> _targets = new();
            private bool _checkingConstant;
            public List<MetaLocalDef> Locals { get; } = new();
            public List<MetaDebugSpan> DebugSpans { get; } = new();
            public List<MetaLocalName> LocalNames { get; } = new();
            public int MaxStack { get; private set; }
            private SourceSpan CurrentSpan => _function.Span;
            private int Position => _instructions.Count;
            public Emitter(Session session, FunctionSymbol function, bool initializer)
            {
                (_s, _function, _module, _initializer) = (session, function, function.Module, initializer);
                _scopes.Push(new());
                for (var i = 0; i < function.Parameters.Length && i < function.ParameterNames.Length; i++)
                {
                    var span = session._semantic?.ParameterSpan(function, i) ?? function.Span;
                    Declare(function.ParameterNames[i], function.Parameters[i], false, true, span);
                }
            }

            private void Fail(string code, SourceSpan span, params string[] args) { _s._budget.Report(code, span, args); throw new SemanticAbortException(); }
            private void Same(int actual, int expected, SourceSpan span)
            {
                if (actual != expected) Fail("E201", span, _s._types[actual].Name, _s._types[expected].Name);
            }
            private int Emit(MetaOpcode op, int a = 0, int b = 0, int c = 0, SourceSpan? span = null, int type = 0, MetaEffectFlags effects = MetaEffectFlags.None)
            {
                _s._budget.Cancellation.ThrowIfCancellationRequested();
                _instructions.Add(new(new(0, op, unchecked((uint) a), unchecked((uint) b), unchecked((uint) c)), type, span ?? CurrentSpan, effects));
                _function.Effects |= effects;
                return _instructions.Count - 1;
            }
            private void Patch(int branch, int target) => _targets[branch] = target;
            private void Push(ConstantValue value, SourceSpan span) => Emit(MetaOpcode.Const, _s.Constant(value), span: span, type: value.Type);
            private void Intrinsic(ushort id, int type, SourceSpan span, MetaEffectFlags effects = MetaEffectFlags.None) => Emit(MetaOpcode.CallIntrinsic, id, type, span: span, effects: effects);
            private int NewTemp(int type, bool mutable = false)
            {
                var slot = Locals.Count; Locals.Add(new(type, mutable, false)); return slot;
            }
            private VariableSymbol Declare(string name, int type, bool mutable, bool parameter, SourceSpan span, string storage = "let", bool startsAtScope = false)
            {
                if (_locals.ContainsKey(name) || _module.Aliases.ContainsKey(name) || _module.Globals.ContainsKey(name) || _module.Links.ContainsKey(name)) Fail("E211", span, name);
                var slot = Locals.Count;
                if (slot >= 4096) throw _s._budget.Limit("E408", span);
                Locals.Add(new(type, mutable, parameter));
                var symbol = new VariableSymbol(name, type, mutable, slot, false, storage, null, _module, span);
                _locals.Add(name, symbol); _scopes.Peek().Add(name);
                _s._semantic?.Variable(symbol, _semanticScopes.TryPeek(out var scope) ? scope : _function.Body.Span, parameter, startsAtScope);
                LocalNames.Add(new(_function.Id, slot, name));
                return symbol;
            }

            public void Body()
            {
                var returned = Block(_function.Body);
                if (!returned && _function.ReturnType != 0) Fail("E214", _function.Span, _function.Name);
                Emit(MetaOpcode.Return);
            }
            public void CheckConstant(MetaExpression expression, int? expected)
            {
                var start = Position; var locals = Locals.Count; var names = LocalNames.Count; var effects = _function.Effects;
                _checkingConstant = true;
                try { var actual = Expression(expression, expected); if (expected != null) Same(actual, expected.Value, expression.Span); }
                finally
                {
                    _checkingConstant = false; _instructions.RemoveRange(start, _instructions.Count - start);
                    foreach (var key in _targets.Keys.Where(k => k >= start).ToArray()) _targets.Remove(key);
                    Locals.RemoveRange(locals, Locals.Count - locals); LocalNames.RemoveRange(names, LocalNames.Count - names); _function.Effects = effects;
                }
            }

            public void Initializers(List<VariableSymbol> globals)
            {
                foreach (var global in globals)
                {
                    _module = global.Module;
                    Same(Expression(global.Initializer!, global.Type), global.Type, global.Span);
                    Emit(MetaOpcode.StoreGlobal, global.Slot, span: global.Span);
                }
                Emit(MetaOpcode.Return);
            }

            public byte[] Finish()
            {
                ComputeStackMaximum();
                var offsets = new int[_instructions.Count + 1];
                for (var i = 0; i < _instructions.Count; i++) offsets[i + 1] = checked(offsets[i] + _instructions[i].Instruction.Size);
                var fileIds = _s._sources.Select((s, i) => (s.FileId, i)).ToDictionary(s => s.FileId, s => s.i, StringComparer.Ordinal);
                var final = new MetaInstruction[_instructions.Count];
                for (var i = 0; i < final.Length; i++)
                {
                    var ir = _instructions[i]; var instruction = ir.Instruction with { Offset = offsets[i] };
                    if (_targets.TryGetValue(i, out var target))
                        instruction = instruction.Opcode == MetaOpcode.BrVariant ? instruction with { C = checked((uint) offsets[target]) } : instruction with { A = checked((uint) offsets[target]) };
                    final[i] = instruction;
                    if (fileIds.TryGetValue(ir.Span.FileId, out var fileId)) DebugSpans.Add(new(_function.Id, offsets[i], fileId, ir.Span.StartUtf8Byte, ir.Span.Length));
                }
                return MetaInstructionCodec.Encode(final);
            }

            private void ComputeStackMaximum()
            {
                var heights = new int?[_instructions.Count]; var pending = new Queue<int>();
                MetaModule? intrinsicTypes = null;
                var intrinsicDeltas = new Dictionary<(uint Id, uint Type), int>();
                heights[0] = 0; pending.Enqueue(0);
                while (pending.TryDequeue(out var index))
                {
                    var instruction = _instructions[index].Instruction; var a = unchecked((int) instruction.A); var op = instruction.Opcode;
                    var delta = op switch
                    {
                        MetaOpcode.Const or MetaOpcode.Unit or MetaOpcode.LoadLocal or MetaOpcode.LoadGlobal or MetaOpcode.LoadLink or MetaOpcode.Dup => 1,
                        MetaOpcode.StoreLocal or MetaOpcode.StoreGlobal or MetaOpcode.Pop or MetaOpcode.BrFalse => -1,
                        MetaOpcode.IAdd or MetaOpcode.ISub or MetaOpcode.IMul or MetaOpcode.IDiv or MetaOpcode.IRem or MetaOpcode.IAnd or MetaOpcode.IOr or MetaOpcode.IXor or MetaOpcode.IShl or MetaOpcode.IShr or
                            MetaOpcode.FAdd or MetaOpcode.FSub or MetaOpcode.FMul or MetaOpcode.FDiv or MetaOpcode.FRem or MetaOpcode.Eq or MetaOpcode.Ne or MetaOpcode.Lt or MetaOpcode.Le or MetaOpcode.Gt or MetaOpcode.Ge => -1,
                        MetaOpcode.Call or MetaOpcode.CallAsync => -_s._functions[a].Parameters.Length + (_s._functions[a].ReturnType == 0 ? 0 : 1),
                        MetaOpcode.Return => _function.ReturnType == 0 ? 0 : -1,
                        MetaOpcode.RecordNew => 1 - _s._types[a].Fields.Length,
                        MetaOpcode.RecordSet => -1,
                        MetaOpcode.VariantNew => _s._types[a].Variants.Single(v => v.Tag == instruction.B).PayloadTypeId == null ? 1 : 0,
                        MetaOpcode.ArrayNew or MetaOpcode.ArrayGet => -1,
                        MetaOpcode.ArraySet => -3,
                        MetaOpcode.ArrayLiteral => 1 - checked((int) instruction.B),
                        MetaOpcode.CallIntrinsic => IntrinsicDelta(instruction),
                        MetaOpcode.HostRead or MetaOpcode.HostAwait => -1 - _s._imports[a].ParameterTypes.Length + (_s._imports[a].ReturnType == 0 ? 0 : 1),
                        _ => 0
                    };
                    var next = heights[index]!.Value + delta;
                    if (next < 0) throw new MetaFormatException("Compiler operand stack underflow.");
                    MaxStack = Math.Max(MaxStack, next);
                    if (op is MetaOpcode.Return or MetaOpcode.Trap) continue;
                    if (op is MetaOpcode.Br or MetaOpcode.BrFalse or MetaOpcode.BrVariant) Merge(_targets[index], next);
                    if (op != MetaOpcode.Br) Merge(index + 1, next);
                }
                void Merge(int target, int height)
                {
                    if ((uint) target >= heights.Length) throw new MetaFormatException("Compiler branch leaves function.");
                    if (heights[target] is { } existing) { if (existing != height) throw new MetaFormatException("Compiler stack height mismatch at merge."); return; }
                    heights[target] = height; pending.Enqueue(target);
                }
                int IntrinsicDelta(MetaInstruction instruction)
                {
                    var key = (instruction.A, instruction.B);
                    if (intrinsicDeltas.TryGetValue(key, out var delta)) return delta;
                    intrinsicTypes ??= new MetaModule { Types = _s._types.ToArray() };
                    var signature = MetaIntrinsics.Resolve(intrinsicTypes, instruction.A, instruction.B);
                    delta = -signature.Parameters.Length + (signature.ReturnType == 0 ? 0 : 1);
                    intrinsicDeltas.Add(key, delta);
                    return delta;
                }
            }

            private bool Block(MetaBlockSyntax block)
            {
                _scopes.Push(new()); _semanticScopes.Push(block.Span); var returned = false;
                foreach (var statement in block.Statements)
                {
                    if (returned) break;
                    returned = Statement(statement);
                }
                foreach (var name in _scopes.Pop())
                {
                    var symbol = _locals[name];
                    if (!symbol.Read && symbol.Storage == "let" && _s._types[symbol.Type].Kind == MetaTypeKind.Result && _s._budget.Diagnostics.Count < _s._budget.Limits.MaxDiagnostics)
                        _s._budget.Diagnostics.Add(MetaDiagnostic.Error("W101", symbol.Span, name) with { Severity = MetaDiagnosticSeverity.Warning });
                    _locals.Remove(name);
                }
                _semanticScopes.Pop();
                return returned;
            }

            private bool Statement(MetaStatement statement)
            {
                switch (statement)
                {
                    case MetaBlockSyntax block: return Block(block);
                    case MetaVariableSyntax variable:
                    {
                        var expected = variable.Type == null ? (int?) null : _s.ResolveType(_module, variable.Type);
                        if (variable.Storage == "const")
                        {
                            CheckConstant(variable.Initializer, expected);
                            var value = _s.EvaluateConstant(_module, variable.Initializer, expected, _locals);
                            var constant = Declare(variable.Name, value.Type, false, false, variable.Span, "const"); constant.Constant = value;
                        }
                        else
                        {
                            var type = Expression(variable.Initializer, expected);
                            if (expected != null) Same(type, expected.Value, variable.Span);
                            var symbol = Declare(variable.Name, type, variable.Storage == "var", false, variable.Span, variable.Storage);
                            Emit(MetaOpcode.ResetLocal, symbol.Slot, span: variable.Span);
                            Emit(MetaOpcode.StoreLocal, symbol.Slot, span: variable.Span);
                        }
                        return false;
                    }
                    case MetaExpressionStatement expression:
                        if (Expression(expression.Value) != 0) Fail("E240", expression.Span);
                        Emit(MetaOpcode.Pop, span: expression.Span); return false;
                    case MetaAssignmentSyntax assignment:
                        Assign(assignment.Target, assignment.Value); return false;
                    case MetaReturnSyntax returned:
                    {
                        var type = returned.Value == null ? 0 : Expression(returned.Value, _function.ReturnType);
                        Same(type, _function.ReturnType, returned.Span);
                        if (returned.Value != null && type == 0) Emit(MetaOpcode.Pop);
                        Emit(MetaOpcode.Return, span: returned.Span); return true;
                    }
                    case MetaIfSyntax conditional:
                    {
                        Condition(conditional.Condition);
                        var other = Emit(MetaOpcode.BrFalse, span: conditional.Condition.Span);
                        var thenReturns = Block(conditional.Then);
                        var end = Emit(MetaOpcode.Br);
                        Patch(other, Position);
                        var elseReturns = conditional.Else != null && Statement(conditional.Else);
                        Patch(end, Position);
                        return thenReturns && elseReturns;
                    }
                    case MetaWhileSyntax loop:
                    {
                        var start = Position; Emit(MetaOpcode.Safepoint, span: loop.Span); Condition(loop.Condition);
                        var end = Emit(MetaOpcode.BrFalse); _loops.Push((new(), new()));
                        Block(loop.Body); var frame = _loops.Pop();
                        var back = Emit(MetaOpcode.Br); Patch(back, start); Patch(end, Position);
                        foreach (var branch in frame.Breaks) Patch(branch, Position);
                        foreach (var branch in frame.Continues) Patch(branch, start);
                        return false;
                    }
                    case MetaForSyntax loop: For(loop); return false;
                    case MetaMatchSyntax match: return Match(match);
                    case MetaControlSyntax control:
                        if (control.Kind == "yield") { Emit(MetaOpcode.Yield, span: control.Span, effects: MetaEffectFlags.MaySuspend); return false; }
                        if (_loops.Count == 0) Fail("E233", control.Span, control.Kind);
                        var jump = Emit(MetaOpcode.Br, span: control.Span);
                        if (control.Kind == "break") _loops.Peek().Breaks.Add(jump); else _loops.Peek().Continues.Add(jump);
                        return false;
                    default: throw new InvalidOperationException("Unknown statement node.");
                }
            }

            private void Condition(MetaExpression expression)
            {
                if (Expression(expression, 1) != 1) Fail("E202", expression.Span);
            }

            private void For(MetaForSyntax loop)
            {
                var type = Expression(loop.Collection); var definition = _s._types[type];
                if (definition.Kind is not (MetaTypeKind.Array or MetaTypeKind.List)) Fail("E234", loop.Collection.Span);
                var collection = NewTemp(type); Emit(MetaOpcode.ResetLocal, collection); Emit(MetaOpcode.StoreLocal, collection);
                var index = NewTemp(3, true); Emit(MetaOpcode.ResetLocal, index); Push(new(3, 0L), loop.Span); Emit(MetaOpcode.StoreLocal, index);
                var version = -1;
                if (definition.Kind == MetaTypeKind.List) { version = NewTemp(3); Emit(MetaOpcode.ResetLocal, version); Emit(MetaOpcode.LoadLocal, collection); Intrinsic(0x106, type, loop.Span); Emit(MetaOpcode.StoreLocal, version); }
                var start = Position; Emit(MetaOpcode.Safepoint, span: loop.Span);
                if (version >= 0) { Emit(MetaOpcode.LoadLocal, collection); Emit(MetaOpcode.LoadLocal, version); Intrinsic(0x107, type, loop.Span); }
                Emit(MetaOpcode.LoadLocal, index); Emit(MetaOpcode.LoadLocal, collection);
                if (definition.Kind == MetaTypeKind.Array) Emit(MetaOpcode.ArrayLen, type); else Intrinsic(0x101, type, loop.Span);
                Emit(MetaOpcode.Lt, 3); var end = Emit(MetaOpcode.BrFalse);
                _scopes.Push(new()); _semanticScopes.Push(loop.Body.Span);
                var item = Declare(loop.Name, definition.Args[0], false, false, loop.Span, startsAtScope: true);
                Emit(MetaOpcode.ResetLocal, item.Slot); Emit(MetaOpcode.LoadLocal, collection); Emit(MetaOpcode.LoadLocal, index);
                if (definition.Kind == MetaTypeKind.Array) Emit(MetaOpcode.ArrayGet, type); else Intrinsic(0x102, type, loop.Span);
                Emit(MetaOpcode.StoreLocal, item.Slot);
                _loops.Push((new(), new())); Block(loop.Body); var frame = _loops.Pop();
                foreach (var name in _scopes.Pop()) _locals.Remove(name);
                _semanticScopes.Pop();
                var increment = Position;
                if (version >= 0) { Emit(MetaOpcode.LoadLocal, collection); Emit(MetaOpcode.LoadLocal, version); Intrinsic(0x107, type, loop.Span); }
                Emit(MetaOpcode.LoadLocal, index); Push(new(3, 1L), loop.Span); Emit(MetaOpcode.IAdd); Emit(MetaOpcode.StoreLocal, index);
                var back = Emit(MetaOpcode.Br); Patch(back, start); Patch(end, Position);
                foreach (var branch in frame.Breaks) Patch(branch, Position);
                foreach (var branch in frame.Continues) Patch(branch, increment);
            }

            private bool Match(MetaMatchSyntax match)
            {
                var type = Expression(match.Value); var definition = _s._types[type];
                if (definition.Kind is not (MetaTypeKind.Result or MetaTypeKind.Option or MetaTypeKind.Enum or MetaTypeKind.Bool)) Fail("E241", match.Span, "match-type");
                var value = NewTemp(type); Emit(MetaOpcode.ResetLocal, value); Emit(MetaOpcode.StoreLocal, value);
                var tags = new HashSet<int>(); var wildcard = false; var allReturn = true; var endBranches = new List<int>();
                for (var i = 0; i < match.Arms.Count; i++)
                {
                    var arm = match.Arms[i]; var pattern = arm.Pattern; int? next = null; int? payload = null;
                    if (wildcard) Fail("E242", pattern.Span, "unreachable-arm");
                    if (pattern.Tag == "_")
                    {
                        wildcard = true; if (i != match.Arms.Count - 1) Fail("E242", pattern.Span, "wildcard-last");
                    }
                    else
                    {
                        int tag;
                        if (definition.Kind == MetaTypeKind.Bool)
                        {
                            if (pattern.Tag is not ("true" or "false") || pattern.Binding != null) Fail("E241", pattern.Span, pattern.Tag);
                            tag = pattern.Tag == "true" ? 1 : 0;
                        }
                        else
                        {
                            var variantName = pattern.Tag[(pattern.Tag.LastIndexOf('.') + 1)..];
                            var variant = definition.Variants.FirstOrDefault(v => v.Name == variantName);
                            if (variant == null) { Fail("E241", pattern.Span, pattern.Tag); return false; }
                            tag = variant.Tag; payload = variant.PayloadTypeId;
                            if (definition.Kind == MetaTypeKind.Enum)
                            {
                                var dot = pattern.Tag.LastIndexOf('.');
                                if (dot < 0 || !TryTypeName(pattern.Tag[..dot], out var enumType) || enumType != type) Fail("E241", pattern.Span, pattern.Tag);
                            }
                            else if ((payload != null) != (pattern.Binding != null)) Fail("E241", pattern.Span, pattern.Tag);
                        }
                        if (!tags.Add(tag)) Fail("E242", pattern.Span, "duplicate-arm");
                        Emit(MetaOpcode.LoadLocal, value);
                        if (definition.Kind is MetaTypeKind.Option or MetaTypeKind.Result)
                        {
                            var success = Emit(MetaOpcode.BrVariant, type, tag); Emit(MetaOpcode.Pop); next = Emit(MetaOpcode.Br);
                            Patch(success, Position);
                        }
                        else
                        {
                            Push(new(type, definition.Kind == MetaTypeKind.Bool ? tag == 1 : tag), pattern.Span); Emit(MetaOpcode.Eq, type); next = Emit(MetaOpcode.BrFalse);
                        }
                    }
                    _scopes.Push(new()); _semanticScopes.Push(arm.Body.Span);
                    if (definition.Kind is MetaTypeKind.Option or MetaTypeKind.Result && pattern.Tag != "_")
                    {
                        if (payload != null && pattern.Binding != "_")
                        {
                            var variant = definition.Variants.Single(v => v.Name == pattern.Tag);
                            Emit(MetaOpcode.VariantPayload, type, variant.Tag);
                            var binding = Declare(pattern.Binding!, payload.Value, false, false, pattern.Span);
                            Emit(MetaOpcode.ResetLocal, binding.Slot); Emit(MetaOpcode.StoreLocal, binding.Slot);
                        }
                        else Emit(MetaOpcode.Pop);
                    }
                    allReturn &= Block(arm.Body);
                    foreach (var name in _scopes.Pop()) _locals.Remove(name);
                    _semanticScopes.Pop();
                    endBranches.Add(Emit(MetaOpcode.Br)); if (next != null) Patch(next.Value, Position);
                }
                var expected = definition.Kind == MetaTypeKind.Bool ? 2 : definition.Variants.Length;
                if (!wildcard && tags.Count != expected) Fail("E241", match.Span, "non-exhaustive");
                // Tag tests retain an impossible unmatched CFG edge even for an exhaustive match.
                // End it explicitly; successful arms continue after this instruction.
                Emit(MetaOpcode.Trap, 5, span: match.Span); // InvalidBytecode: no well-typed value can reach this path.
                foreach (var branch in endBranches) Patch(branch, Position);
                return allReturn;
            }

            private int Expression(MetaExpression expression, int? expected = null, bool awaited = false)
            {
                var type = BindExpression(expression, expected, awaited);
                _s._semantic?.Expression(expression.Span, _s._types[type].Name);
                return type;
            }

            private int BindExpression(MetaExpression expression, int? expected, bool awaited)
            {
                switch (expression)
                {
                    case MetaLiteralSyntax literal:
                    {
                        var value = _s.Literal(literal, expected); Push(value, literal.Span); return value.Type;
                    }
                    case MetaNameSyntax name:
                        if (Variable(name.Name) is { } variable) { Load(variable, name.Span); return variable.Type; }
                        if (_module.Links.TryGetValue(name.Name, out var link)) { _s._semantic?.UseLink(_module, name.Name, name.Span); Emit(MetaOpcode.LoadLink, link, span: name.Span); return _s._links[link].CapabilityTypeId; }
                        Fail("E222", name.Span, name.Name); return 0;
                    case MetaUnarySyntax unary:
                        return Unary(unary, expected);
                    case MetaBinarySyntax binary:
                        return Binary(binary, expected);
                    case MetaMemberSyntax member:
                        return Member(member);
                    case MetaIndexSyntax index:
                    {
                        var target = Expression(index.Target); var type = _s._types[target];
                        if (type.Kind is not (MetaTypeKind.Array or MetaTypeKind.List)) Fail("E225", index.Span, "index");
                        Same(Expression(index.Index, 3), 3, index.Index.Span);
                        if (type.Kind == MetaTypeKind.Array) Emit(MetaOpcode.ArrayGet, target, span: index.Span); else Intrinsic(0x102, target, index.Span);
                        return type.Args[0];
                    }
                    case MetaArraySyntax array:
                    {
                        var item = expected != null && _s._types[expected.Value].Kind == MetaTypeKind.Array ? _s._types[expected.Value].Args[0] : (int?) null;
                        if (array.Elements.Count == 0 && item == null) Fail("E203", array.Span, "array");
                        foreach (var element in array.Elements) { var actual = Expression(element, item); item ??= actual; Same(actual, item.Value, element.Span); }
                        var type = _s.Container("array", [item!.Value], array.Span); Emit(MetaOpcode.ArrayLiteral, type, array.Elements.Count, span: array.Span, effects: MetaEffectFlags.Allocates); return type;
                    }
                    case MetaVariantSyntax variant:
                    {
                        var kind = variant.Tag is "none" or "some" ? MetaTypeKind.Option : MetaTypeKind.Result;
                        int payload;
                        if (expected == null)
                        {
                            if (variant.Tag is "none" or "err") Fail("E203", variant.Span, variant.Tag);
                            payload = Expression(variant.Value!);
                            expected = _s.Container(kind == MetaTypeKind.Option ? "option" : "result", [payload], variant.Span);
                        }
                        else
                        {
                            if (_s._types[expected.Value].Kind != kind) Fail("E201", variant.Span, variant.Tag, _s._types[expected.Value].Name);
                            payload = variant.Tag == "err" ? _s._typeNames["meta.system.Error"] : _s._types[expected.Value].Args[0];
                            if (variant.Value != null) Same(Expression(variant.Value, payload), payload, variant.Span);
                        }
                        Emit(MetaOpcode.VariantNew, expected.Value, variant.Tag is "some" or "err" ? 1 : 0, span: variant.Span); return expected.Value;
                    }
                    case MetaNewSyntax constructor: return Construct(constructor);
                    case MetaCallSyntax call: return Call(call, awaited);
                    default: throw new InvalidOperationException("Unknown expression node.");
                }
            }

            private VariableSymbol? Variable(string name) => _locals.GetValueOrDefault(name) ?? _module.Globals.GetValueOrDefault(name);
            private void Load(VariableSymbol variable, SourceSpan span)
            {
                _s._semantic?.UseVariable(variable, span);
                if (_checkingConstant && variable.Storage != "const") Fail("E216", span, "mutable-constant-dependency");
                variable.Read = true;
                if (variable.Storage == "const") { Push(variable.Constant ?? _s.EvaluateGlobalConstant(variable), span); return; }
                Emit(variable.Global ? MetaOpcode.LoadGlobal : MetaOpcode.LoadLocal, variable.Slot, span: span, type: variable.Type,
                    effects: variable.Global && variable.Mutable ? MetaEffectFlags.UsesMutableGlobal : MetaEffectFlags.None);
            }

            private int Unary(MetaUnarySyntax unary, int? expected)
            {
                if (unary.Operator == "await")
                {
                    if (!_function.Async || _initializer) Fail("E231", unary.Span);
                    if (unary.Operand is not MetaCallSyntax call) { Fail("E232", unary.Span); return 0; }
                    return Call(call, true);
                }
                if (unary.Operator == "-" && unary.Operand is MetaLiteralSyntax { Kind: "Integer", Value: string raw } && _s.IntegerMagnitude(raw, unary.Span) == (BigInteger) long.MaxValue + 1)
                { Push(new(3, long.MinValue), unary.Span); return 3; }
                var type = Expression(unary.Operand, expected);
                if (unary.Operator == "!") { Same(type, 1, unary.Span); Emit(MetaOpcode.Not, span: unary.Span); return 1; }
                if (unary.Operator == "~") { Same(type, 3, unary.Span); Emit(MetaOpcode.INot, span: unary.Span); return 3; }
                if (type is not (3 or 4)) Fail("E201", unary.Span, "numeric-unary");
                if (unary.Operator == "-") Emit(type == 3 ? MetaOpcode.INeg : MetaOpcode.FNeg, span: unary.Span);
                return type;
            }

            private int Binary(MetaBinarySyntax binary, int? expected)
            {
                if (binary.Operator is "&&" or "||")
                {
                    Condition(binary.Left); var branch = Emit(MetaOpcode.BrFalse); int end;
                    if (binary.Operator == "||")
                    {
                        Push(new(1, true), binary.Span); end = Emit(MetaOpcode.Br); Patch(branch, Position); Condition(binary.Right); Patch(end, Position);
                    }
                    else
                    {
                        Condition(binary.Right); end = Emit(MetaOpcode.Br); Patch(branch, Position); Push(new(1, false), binary.Span); Patch(end, Position);
                    }
                    return 1;
                }
                var left = Expression(binary.Left, expected);
                var right = Expression(binary.Right, left); Same(right, left, binary.Span);
                if (binary.Operator is "==" or "!=")
                {
                    if (!_s.Comparable(left, new())) Fail("E201", binary.Span, "not-comparable");
                    Emit(binary.Operator == "==" ? MetaOpcode.Eq : MetaOpcode.Ne, left, span: binary.Span); return 1;
                }
                if (binary.Operator is "<" or "<=" or ">" or ">=")
                {
                    if (left is not (2 or 3 or 4 or 5)) Fail("E201", binary.Span, "not-ordered");
                    Emit(binary.Operator switch { "<" => MetaOpcode.Lt, "<=" => MetaOpcode.Le, ">" => MetaOpcode.Gt, _ => MetaOpcode.Ge }, left, span: binary.Span); return 1;
                }
                if (binary.Operator == "+" && left == 5) { Intrinsic(0x17, -1, binary.Span, MetaEffectFlags.Allocates); return 5; }
                if (binary.Operator is "&" or "|" or "^" or "<<" or ">>")
                {
                    Same(left, 3, binary.Span);
                    Emit(binary.Operator switch { "&" => MetaOpcode.IAnd, "|" => MetaOpcode.IOr, "^" => MetaOpcode.IXor, "<<" => MetaOpcode.IShl, _ => MetaOpcode.IShr }, span: binary.Span); return 3;
                }
                if (left is not (3 or 4)) Fail("E201", binary.Span, "arithmetic-type");
                var op = (binary.Operator, left) switch
                {
                    ("+", 3) => MetaOpcode.IAdd, ("-", 3) => MetaOpcode.ISub, ("*", 3) => MetaOpcode.IMul, ("/", 3) => MetaOpcode.IDiv, ("%", 3) => MetaOpcode.IRem,
                    ("+", 4) => MetaOpcode.FAdd, ("-", 4) => MetaOpcode.FSub, ("*", 4) => MetaOpcode.FMul, ("/", 4) => MetaOpcode.FDiv, _ => MetaOpcode.FRem
                };
                Emit(op, span: binary.Span); return left;
            }

            public static string? ExpressionName(MetaExpression expression) => expression switch
            {
                MetaNameSyntax n => n.Name, MetaMemberSyntax m when ExpressionName(m.Target) is { } name => name + "." + m.Member, _ => null
            };
            private bool TryTypeName(string name, out int type)
            {
                if (_locals.ContainsKey(name.Split('.')[0]) || _module.Globals.ContainsKey(name.Split('.')[0]) || _module.Links.ContainsKey(name.Split('.')[0])) { type = -1; return false; }
                if (_s._typeNames.TryGetValue(_module.Syntax.Module + "." + name, out type)) return true;
                var dot = name.IndexOf('.');
                if (dot > 0 && _module.Aliases.TryGetValue(name[..dot], out var target) && _s._typeNames.TryGetValue(target + name[dot..], out type) && _s.IsVisibleType(_module, target + name[dot..])) return true;
                var matches = _module.Syntax.Imports.Select(i => i.Module + "." + name).Where(n => _s._typeNames.ContainsKey(n) && _s.IsVisibleType(_module, n)).Distinct(StringComparer.Ordinal).ToList();
                if (name is "Error" or "ErrorCode" or "SampleStamp") matches.Add("meta.system." + name);
                matches = matches.Distinct(StringComparer.Ordinal).ToList();
                if (matches.Count > 1) Fail("E224", CurrentSpan, name);
                if (matches.Count == 1) { type = _s._typeNames[matches[0]]; return true; }
                type = -1; return false;
            }

            private int Member(MetaMemberSyntax member)
            {
                if (ExpressionName(member.Target) is { } name && TryTypeName(name, out var enumType) && _s._types[enumType].Kind == MetaTypeKind.Enum)
                {
                    _s._semantic?.UseType(enumType, member.Target.Span);
                    _s._semantic?.Expression(member.Target.Span, _s._types[enumType].Name);
                    var variant = _s._types[enumType].Variants.FirstOrDefault(v => v.Name == member.Member);
                    if (variant == null) Fail("E225", member.Span, member.Member);
                    _s._semantic?.UseMember(_s._types[enumType].Name, member.Member, member.Span);
                    Push(new(enumType, variant!.Tag), member.Span); return enumType;
                }
                if (member.Target is MetaNameSyntax alias && _module.Aliases.TryGetValue(alias.Name, out var moduleName) && _s._modules.TryGetValue(moduleName, out var targetModule) && targetModule.Globals.TryGetValue(member.Member, out var global))
                {
                    if (targetModule.Values[member.Member] is not MetaGlobalSyntax { Public: true, Storage: "const" }) Fail("E229", member.Span, member.Member);
                    _s._semantic?.UseAlias(_module, alias.Name, alias.Span);
                    Load(global, member.Span); return global.Type;
                }
                var target = Expression(member.Target); var definition = _s._types[target];
                if (member.Member == "length")
                {
                    _s._semantic?.UseMember(definition.Name, member.Member, member.Span);
                    if (definition.Kind == MetaTypeKind.Array) { Emit(MetaOpcode.ArrayLen, target, span: member.Span); return 3; }
                    if (definition.Kind == MetaTypeKind.List) { Intrinsic(0x101, target, member.Span); return 3; }
                    if (target == 5) { Intrinsic(0x16, -1, member.Span); return 3; }
                }
                var field = Array.FindIndex(definition.Fields, f => f.Name == member.Member);
                if (field < 0) Fail("E225", member.Span, member.Member);
                _s._semantic?.UseMember(definition.Name, member.Member, member.Span);
                Emit(MetaOpcode.RecordGet, target, field, span: member.Span); return definition.Fields[field].TypeId;
            }

            private int Construct(MetaNewSyntax constructor)
            {
                var type = _s.ResolveType(_module, constructor.Type); var definition = _s._types[type];
                if (definition.Kind is not (MetaTypeKind.Struct or MetaTypeKind.Array or MetaTypeKind.List or MetaTypeKind.Map)) Fail("E226", constructor.Span, definition.Name);
                _function.Effects |= MetaEffectFlags.Allocates;
                if (definition.Kind == MetaTypeKind.Struct)
                {
                    var assigned = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var argument in constructor.Arguments)
                    {
                        if (argument.Name == null || assigned.ContainsKey(argument.Name)) Fail("E228", argument.Span, argument.Name ?? "named-field");
                        var field = definition.Fields.FirstOrDefault(f => f.Name == argument.Name);
                        if (field == null) Fail("E225", argument.Span, argument.Name!);
                        _s._semantic?.UseMember(definition.Name, argument.Name!, argument.Span with { EndUtf8Byte = argument.Value.Span.StartUtf8Byte });
                        Same(Expression(argument.Value, field!.TypeId), field.TypeId, argument.Span);
                        var temporary = NewTemp(field.TypeId); Emit(MetaOpcode.ResetLocal, temporary); Emit(MetaOpcode.StoreLocal, temporary); assigned.Add(argument.Name!, temporary);
                    }
                    foreach (var field in definition.Fields)
                    {
                        if (!assigned.TryGetValue(field.Name, out var slot)) Fail("E228", constructor.Span, field.Name);
                        Emit(MetaOpcode.LoadLocal, slot);
                    }
                    Emit(MetaOpcode.RecordNew, type, span: constructor.Span); return type;
                }
                if (constructor.Arguments.Any(a => a.Name != null)) Fail("E228", constructor.Span, "positional-constructor");
                if (definition.Kind == MetaTypeKind.Array)
                {
                    if (constructor.Arguments.Count != 2) Fail("E227", constructor.Span, "array-constructor");
                    Same(Expression(constructor.Arguments[0].Value, 3), 3, constructor.Span);
                    Same(Expression(constructor.Arguments[1].Value, definition.Args[0]), definition.Args[0], constructor.Span);
                    Emit(MetaOpcode.ArrayNew, type, span: constructor.Span); return type;
                }
                if (definition.Kind is MetaTypeKind.List or MetaTypeKind.Map)
                {
                    if (constructor.Arguments.Count != 0) Fail("E227", constructor.Span, "empty-constructor");
                    Intrinsic(definition.Kind == MetaTypeKind.List ? (ushort) 0x100 : (ushort) 0x110, type, constructor.Span); return type;
                }
                Fail("E226", constructor.Span, definition.Name); return 0;
            }

            private int Call(MetaCallSyntax call, bool awaited)
            {
                if (_checkingConstant) Fail("E216", call.Span, "constant-call");
                if (_initializer) Fail("E212", call.Span);
                FunctionSymbol? target = null;
                if (call.Target is MetaNameSyntax functionName) target = _module.Functions.GetValueOrDefault(functionName.Name);
                if (call.Target is MetaMemberSyntax { Target: MetaNameSyntax alias } qualified && _module.Aliases.TryGetValue(alias.Name, out var moduleName))
                {
                    _s._semantic?.UseAlias(_module, alias.Name, alias.Span);
                    var intrinsic = _s._catalog.Intrinsics.FirstOrDefault(i => i.Module == moduleName && i.Name == qualified.Member);
                    if (intrinsic != null)
                    {
                        _s._semantic?.UseIntrinsic(intrinsic, call.Target.Span);
                        if (awaited) Fail("E232", call.Span, "not-async");
                        Arguments(call, intrinsic.Parameters.Select(_s.ResolveCanonical).ToArray());
                        var type = _s.ResolveCanonical(intrinsic.ReturnType); Intrinsic(intrinsic.Id, -1, call.Span, intrinsic.Effects);
                        if (type == 0) Emit(MetaOpcode.Unit); return type;
                    }
                    if (_s._modules.TryGetValue(moduleName, out var owner) && owner.Functions.TryGetValue(qualified.Member, out target))
                    {
                        if (owner.Values[qualified.Member] is not MetaFunctionSyntax { Public: true }) Fail("E229", call.Span, qualified.Member);
                    }
                    else Fail("E225", call.Span, qualified.Member);
                }
                if (target != null)
                {
                    _s._semantic?.UseFunction(target, call.Target.Span);
                    if (target.Async != awaited) Fail("E232", call.Span, target.Name);
                    if (awaited && !_function.Async) Fail("E231", call.Span);
                    Arguments(call, target.Parameters); _function.Calls.Add(target.Id);
                    Emit(awaited ? MetaOpcode.CallAsync : MetaOpcode.Call, target.Id, span: call.Span, effects: awaited ? MetaEffectFlags.MaySuspend : MetaEffectFlags.None);
                    if (target.ReturnType == 0) Emit(MetaOpcode.Unit); return target.ReturnType;
                }
                if (call.Target is not MetaMemberSyntax member) { Fail("E222", call.Span, ExpressionName(call.Target) ?? "call"); return 0; }
                var receiver = Expression(member.Target); var receiverType = _s._types[receiver];
                if (receiverType.Kind is MetaTypeKind.List or MetaTypeKind.Map)
                {
                    if (awaited) Fail("E232", call.Span, "not-async");
                    var (id, parameters, result) = CollectionMethod(receiverType, member.Member, call.Span);
                    _s._semantic?.UseMember(receiverType.Name, member.Member, member.Span);
                    Arguments(call, parameters); Intrinsic(id, receiver, call.Span, MetaEffectFlags.Allocates);
                    if (result == 0) Emit(MetaOpcode.Unit); return result;
                }
                var api = _s._catalog.Members.FirstOrDefault(m => m.Owner == receiverType.Name && m.Name == member.Member && m.Effect != "Event");
                if (api == null) { Fail("E225", call.Span, member.Member); return 0; }
                _s._semantic?.UseMember(receiverType.Name, member.Member, member.Span);
                if ((api.Effect == "Await") != awaited) Fail("E232", call.Span, member.Member);
                if (awaited && !_function.Async) Fail("E231", call.Span);
                Arguments(call, api.Parameters.Select(_s.ResolveCanonical).ToArray());
                Emit(awaited ? MetaOpcode.HostAwait : MetaOpcode.HostRead, _s.HostImport(api), span: call.Span,
                    effects: MetaEffectFlags.CallsHost | (awaited ? MetaEffectFlags.MaySuspend : MetaEffectFlags.None));
                var resultType = _s.ResolveCanonical(api.ReturnType); if (resultType == 0) Emit(MetaOpcode.Unit); return resultType;
            }

            private (ushort Id, int[] Parameters, int Result) CollectionMethod(MetaTypeDef type, string method, SourceSpan span)
            {
                if (type.Kind == MetaTypeKind.List)
                {
                    if (method == "push") return (0x104, [type.Args[0]], 0);
                    if (method == "pop") return (0x105, [], _s.Container("option", [type.Args[0]], span));
                }
                else
                {
                    if (method == "get") return (0x111, [type.Args[0]], _s.Container("option", [type.Args[1]], span));
                    if (method == "set") return (0x112, type.Args, 0);
                    if (method == "remove") return (0x113, [type.Args[0]], 1);
                    if (method == "keys") return (0x114, [], _s.Container("array", [type.Args[0]], span));
                }
                Fail("E225", span, method); return default;
            }
            private void Arguments(MetaCallSyntax call, int[] parameters)
            {
                if (call.Arguments.Count != parameters.Length) Fail("E227", call.Span, parameters.Length.ToString(CultureInfo.InvariantCulture));
                for (var i = 0; i < parameters.Length; i++) Same(Expression(call.Arguments[i], parameters[i]), parameters[i], call.Arguments[i].Span);
            }

            private void Assign(MetaExpression target, MetaExpression value)
            {
                var lvalue = PrepareLValue(target);
                Same(Expression(value, lvalue.Type), lvalue.Type, value.Span); lvalue.Store();
            }

            private void Store(VariableSymbol variable, SourceSpan span)
            {
                _s._semantic?.UseVariable(variable, span);
                Emit(variable.Global ? MetaOpcode.StoreGlobal : MetaOpcode.StoreLocal, variable.Slot, span: span,
                    effects: variable.Global ? MetaEffectFlags.UsesMutableGlobal : MetaEffectFlags.None);
            }

            private int CaptureTop(int type)
            {
                var slot = NewTemp(type); Emit(MetaOpcode.ResetLocal, slot); Emit(MetaOpcode.StoreLocal, slot); return slot;
            }

            private PreparedLValue PrepareLValue(MetaExpression target)
            {
                if (target is MetaNameSyntax name)
                {
                    var variable = Variable(name.Name);
                    if (variable == null) { Fail("E222", target.Span, name.Name); return null!; }
                    if (!variable.Mutable) Fail("E210", target.Span, name.Name);
                    return new(variable.Type, () => Load(variable, target.Span), () => Store(variable, target.Span));
                }
                if (target is MetaMemberSyntax member)
                {
                    var parent = PrepareLValue(member.Target); parent.Read();
                    var definition = _s._types[parent.Type]; var field = Array.FindIndex(definition.Fields, f => f.Name == member.Member);
                    if (definition.Kind != MetaTypeKind.Struct || field < 0 || definition.Fields[field].Readonly) Fail("E210", target.Span);
                    _s._semantic?.UseMember(definition.Name, member.Member, member.Span);
                    var record = CaptureTop(parent.Type); var fieldType = definition.Fields[field].TypeId;
                    return new(fieldType, () => { Emit(MetaOpcode.LoadLocal, record); Emit(MetaOpcode.RecordGet, parent.Type, field); }, () =>
                    {
                        var updated = CaptureTop(fieldType); Emit(MetaOpcode.LoadLocal, record); Emit(MetaOpcode.LoadLocal, updated);
                        Emit(MetaOpcode.RecordSet, parent.Type, field); parent.Store();
                    });
                }
                if (target is MetaIndexSyntax index)
                {
                    var parent = Expression(index.Target); var definition = _s._types[parent]; var collection = CaptureTop(parent);
                    if (definition.Kind is not (MetaTypeKind.Array or MetaTypeKind.List)) Fail("E210", target.Span);
                    Same(Expression(index.Index, 3), 3, index.Span); var position = CaptureTop(3); var itemType = definition.Args[0];
                    return new(itemType, () =>
                    {
                        Emit(MetaOpcode.LoadLocal, collection); Emit(MetaOpcode.LoadLocal, position);
                        if (definition.Kind == MetaTypeKind.Array) Emit(MetaOpcode.ArrayGet, parent); else Intrinsic(0x102, parent, target.Span);
                    }, () =>
                    {
                        var updated = CaptureTop(itemType); Emit(MetaOpcode.LoadLocal, collection); Emit(MetaOpcode.LoadLocal, position); Emit(MetaOpcode.LoadLocal, updated);
                        if (definition.Kind == MetaTypeKind.Array) Emit(MetaOpcode.ArraySet, parent); else Intrinsic(0x103, parent, target.Span);
                    });
                }
                Fail("E210", target.Span); return null!;
            }
            private sealed record PreparedLValue(int Type, Action Read, Action Store);
        }

        private bool Comparable(int type, HashSet<int> seen)
        {
            if (!seen.Add(type)) return true;
            var definition = _types[type];
            return definition.Kind is MetaTypeKind.Unit or MetaTypeKind.Bool or MetaTypeKind.Byte or MetaTypeKind.Int or MetaTypeKind.Float or MetaTypeKind.String or MetaTypeKind.Enum ||
                definition.Kind is MetaTypeKind.Struct or MetaTypeKind.NativeDto && definition.Fields.All(f => Comparable(f.TypeId, seen)) ||
                definition.Kind is MetaTypeKind.Option or MetaTypeKind.Result && definition.Variants.All(v => v.PayloadTypeId == null || Comparable(v.PayloadTypeId.Value, seen));
        }
    }
}
