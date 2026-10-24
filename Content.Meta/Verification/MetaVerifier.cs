// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0
using System.Globalization;
using System.Text;
using Content.Meta.Bytecode;

namespace Content.Meta.Verification;

public static class MetaVerifier
{
    public const int PolicyVersion = 1;

    public static VerifiedArtifact Verify(byte[] bytes, MetaVerificationCatalog? catalog = null, MetaLimits? limits = null)
    {
        limits ??= new MetaLimits();
        catalog ??= new MetaVerificationCatalog();
        // Loader owns fresh storage: callers can mutate input bytes or compiler DTOs after admission.
        var module = MetaBytecodeLoader.Load(bytes, limits);
        ValidateMetadata(module, catalog, limits);
        var instructions = new MetaInstruction[module.Functions.Length][];
        var ranges = module.Functions.OrderBy(function => function.CodeOffset).ToArray();
        var cursor = 0;
        foreach (var function in ranges)
        {
            Require(function.CodeOffset == cursor && function.CodeLength > 0 && function.CodeLength <= module.Code.Length - cursor, "Function ranges overlap or leave holes.");
            instructions[function.Id] = MetaInstructionCodec.Decode(module.Code.AsSpan(cursor, function.CodeLength));
            cursor = checked(cursor + function.CodeLength);
        }
        Require(cursor == module.Code.Length, "Unowned executable data.");
        var steps = 0;
        foreach (var function in module.Functions)
        {
            ValidateFunction(module, function, instructions[function.Id], limits, ref steps);
        }
        ValidateDebug(module, instructions);
        return new VerifiedArtifact(module, Convert.ToHexStringLower(bytes.AsSpan(24, 32)), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), instructions);
    }

    private static void ValidateMetadata(MetaModule m, MetaVerificationCatalog catalog, MetaLimits limits)
    {
        var manifest = m.Manifest;
        Require(manifest.FormatVersion == 1 && manifest.LanguageVersion == "1.0" && manifest.RuntimeAbi == "1.0" && manifest.GasPolicyVersion == 1, "Unsupported manifest policy or ABI.");
        Symbol(manifest.PackageName); Symbol(manifest.PackageVersion); Symbol(manifest.CompilerBuild); Symbol(manifest.RootModule);
        Hash(manifest.SourceTreeHash); Hash(manifest.LockHash); Hash(manifest.ApiCatalogHash);
        Require(manifest.ApiCatalogHash == catalog.Hash, "API catalog hash mismatch.");
        Require(manifest.RequiredScopes.Distinct(StringComparer.Ordinal).Count() == manifest.RequiredScopes.Length, "Duplicate scope.");
        foreach (var scope in manifest.RequiredScopes) Symbol(scope);
        Require(m.Types.Length is >= 6 && m.Types.Length <= limits.Types && m.Functions.Length <= limits.Functions && m.Constants.Length <= limits.Constants && m.Imports.Length <= limits.Imports && m.Globals.Length <= limits.Globals, "Table size limit.");
        Require(m.Links.Length <= 64 && m.Watches.Length <= 64 && m.Watches.Sum(watch => (long) watch.Capacity) <= limits.EventCapacity, "Link/watch/event capacity limit.");
        var primitives = MetaTypeDef.Primitives;
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < m.Types.Length; index++)
        {
            var type = m.Types[index];
            Require(type.Id == index && names.Add(type.Name), "Invalid type ID or duplicate name.");
            Symbol(type.Name);
            Require(!type.Name.StartsWith("meta.", StringComparison.Ordinal) || type.ContractHash != null, "Reserved native type namespace.");
            foreach (var arg in type.Args) Type(m, arg);
            foreach (var field in type.Fields) { Symbol(field.Name); Type(m, field.TypeId); }
            foreach (var variant in type.Variants) { Symbol(variant.Name); Require(variant.Tag is >= 0 and <= ushort.MaxValue, "Variant tag range."); if (variant.PayloadTypeId is { } payload) Type(m, payload); }
            Require(type.Fields.Length <= 64 && type.Fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() == type.Fields.Length, "Invalid fields.");
            Require(type.Variants.Select(variant => variant.Tag).Distinct().Count() == type.Variants.Length && type.Variants.Select(variant => variant.Name).Distinct(StringComparer.Ordinal).Count() == type.Variants.Length, "Duplicate variant.");
            if (index < 6)
            {
                Require(MetaCanonicalJson.Serialize(type).AsSpan().SequenceEqual(MetaCanonicalJson.Serialize(primitives[index])), "Primitive type redefinition.");
                continue;
            }
            Require(type.Kind >= MetaTypeKind.Array, "Scalar type outside primitive prefix.");
            var arity = type.Kind == MetaTypeKind.Map ? 2 : type.Kind is MetaTypeKind.Array or MetaTypeKind.List or MetaTypeKind.Option or MetaTypeKind.Result ? 1 : 0;
            Require(type.Args.Length == arity, "Type argument count.");
            Require(type.Kind is MetaTypeKind.Struct or MetaTypeKind.NativeDto || type.Fields.Length == 0, "Unexpected fields.");
            Require(type.Kind is MetaTypeKind.Option or MetaTypeKind.Result or MetaTypeKind.Enum || type.Variants.Length == 0, "Unexpected variants.");
            if (type.Kind == MetaTypeKind.Map) Require(m.Types[type.Args[0]].Kind is MetaTypeKind.Int or MetaTypeKind.Byte or MetaTypeKind.Bool or MetaTypeKind.String or MetaTypeKind.Enum, "Invalid map key.");
            if (type.Kind == MetaTypeKind.Option)
                Require(type.Variants.Length == 2 && type.Variants[0] == new MetaVariantDef("none", 0, null) && type.Variants[1] == new MetaVariantDef("some", 1, type.Args[0]), "Option layout.");
            if (type.Kind == MetaTypeKind.Result)
            {
                var error = Array.FindIndex(m.Types, candidate => candidate.Name == "meta.system.Error" && candidate.Kind == MetaTypeKind.NativeDto);
                Require(error >= 0 && type.Variants.Length == 2 && type.Variants[0] == new MetaVariantDef("ok", 0, type.Args[0]) && type.Variants[1] == new MetaVariantDef("err", 1, error), "Result layout.");
            }
            if (type.Kind == MetaTypeKind.Enum) Require(type.Variants.Length > 0 && type.Variants.All(variant => variant.PayloadTypeId == null), "Enum layout.");
            if (type.Kind is MetaTypeKind.NativeDto or MetaTypeKind.Capability or MetaTypeKind.Opaque || type.ContractHash != null)
            {
                Require(type.ContractHash != null, "Native type missing contract hash."); Hash(type.ContractHash!);
                var expected = catalog.NativeTypes.SingleOrDefault(candidate => candidate.Name == type.Name);
                Require(expected != null && MetaCanonicalJson.Serialize(type).AsSpan().SequenceEqual(MetaCanonicalJson.Serialize(expected)), "Native type is absent from trusted catalog or differs from ABI.");
                if (type.Kind == MetaTypeKind.NativeDto) Require(type.Fields.All(field => field.Readonly), "Native DTO fields must be readonly.");
            }
            else Require(type.ContractHash == null, "User type contract hash.");
        }
        // Walk every edge, including references through mutable containers; user recursive layouts are forbidden.
        var active = new bool[m.Types.Length];
        var depths = new int[m.Types.Length];
        var sizes = new int[m.Types.Length];
        int Layout(int id, int depth)
        {
            Require(depth <= 16 && !active[id], "Cyclic or deeply nested type graph.");
            if (depths[id] != 0) { Require(depth + depths[id] - 1 <= 16, "Type graph depth."); return sizes[id]; }
            var type = m.Types[id];
            active[id] = true;
            var edges = type.Args.Concat(type.Fields.Select(field => field.TypeId)).Concat(type.Variants.Where(variant => variant.PayloadTypeId != null).Select(variant => variant.PayloadTypeId!.Value)).ToArray();
            foreach (var edge in edges) Layout(edge, depth + 1);
            active[id] = false;
            depths[id] = 1 + (edges.Length == 0 ? 0 : edges.Max(edge => depths[edge]));
            Require(depth + depths[id] - 1 <= 16, "Type graph depth.");
            var size = type.Kind is MetaTypeKind.Struct or MetaTypeKind.NativeDto ? type.Fields.Sum(field => sizes[field.TypeId]) : 16;
            Require(size <= 4096, "Flat value layout limit.");
            sizes[id] = size;
            return size;
        }
        for (var id = 0; id < m.Types.Length; id++) Layout(id, 0);
        for (var index = 0; index < m.Constants.Length; index++) ValidateConstant(m, index, catalog, limits);
        for (var index = 0; index < m.Functions.Length; index++)
        {
            var function = m.Functions[index];
            Require(function.Id == index && function.Locals.Length <= limits.LocalsPerFunction && function.MaxStack <= limits.OperandSlots, "Function limit or ID.");
            Symbol(function.Name); Type(m, function.ReturnType);
            Require(function.ParameterTypes.Length <= function.Locals.Length, "Parameter slots missing.");
            for (var local = 0; local < function.Locals.Length; local++)
            {
                var slot = function.Locals[local]; Type(m, slot.TypeId);
                Require(slot.Parameter == (local < function.ParameterTypes.Length), "Parameter prefix mismatch.");
                if (slot.Parameter) Require(!slot.Mutable && slot.TypeId == function.ParameterTypes[local], "Parameter signature mismatch.");
            }
            if (function.Initializer) Require(manifest.InitializerFunctionId == index && function.ReturnType == 0 && !function.Async && function.ParameterTypes.Length == 0, "Initializer signature.");
        }
        if (manifest.MainFunctionId is { } main)
        {
            Require(main >= 0 && main < m.Functions.Length, "Invalid main function.");
            Require(m.Functions[main].ReturnType == 0 && m.Functions[main].ParameterTypes.Length == 0 && !m.Functions[main].Initializer, "Main signature.");
        }
        if (manifest.InitializerFunctionId is { } initializer) Require(initializer >= 0 && initializer < m.Functions.Length && m.Functions[initializer].Initializer, "Invalid initializer ID.");
        Require(m.Globals.Length == 0 || manifest.InitializerFunctionId != null, "Globals require initializer.");
        for (var index = 0; index < m.Globals.Length; index++)
        {
            Require(m.Globals[index].Id == index, "Invalid global ID."); Type(m, m.Globals[index].TypeId); Symbol(m.Globals[index].Name);
        }
        foreach (var import in m.Imports)
        {
            Symbol(import.Name); Hash(import.ContractHash);
            Require(import.ParameterTypes.Length <= 64, "Host import parameter limit.");
            foreach (var parameter in import.ParameterTypes) Type(m, parameter);
            if (import.ReturnType is { } result) Type(m, result);
            if (import.CapabilityTypeId is { } capability) { Type(m, capability); Require(m.Types[capability].Kind == MetaTypeKind.Capability, "Import receiver is not a capability."); }
            Require(import.Kind is "Library" or "BuiltinModule" or "HostMethod" or "HostEvent", "Unknown import kind.");
            if (import.Kind is "Library" or "BuiltinModule") Require(import.Effect == "None" && import.CapabilityTypeId == null && import.ReturnType == null && import.ParameterTypes.Length == 0, "Module import shape.");
            else Require(import.CapabilityTypeId != null && import.ReturnType != null && (import.Kind == "HostEvent" ? import.Effect == "Event" && import.ReturnType == 0 : import.Effect is "Read" or "Await"), "Host import shape.");
            if (import.Kind == "HostMethod") Require(m.Types[import.ReturnType!.Value].Kind == MetaTypeKind.Result, "Host methods must return result<T>.");
            if (import.Kind == "Library") { Require(import.Version != null && import.ContentHash != null, "Library lock missing."); Hash(import.ContentHash!); }
            else Require(import.Version == null && import.ContentHash == null, "Unexpected import lock.");
            Require(catalog.Imports.Any(expected => MetaCanonicalJson.Serialize(import).AsSpan().SequenceEqual(MetaCanonicalJson.Serialize(expected))), "Import ABI is absent from trusted catalog.");
        }
        for (var index = 0; index < m.Links.Length; index++)
        {
            var link = m.Links[index];
            Require(link.Id == index, "Invalid link ID."); Symbol(link.SlotName); Type(m, link.CapabilityTypeId); Hash(link.ContractHash);
            Require(m.Types[link.CapabilityTypeId].Kind == MetaTypeKind.Capability && m.Types[link.CapabilityTypeId].ContractHash == link.ContractHash, "Link contract mismatch.");
        }
        foreach (var watch in m.Watches)
        {
            Require(watch.LinkId >= 0 && watch.LinkId < m.Links.Length && watch.EventImportId >= 0 && watch.EventImportId < m.Imports.Length && watch.FunctionId >= 0 && watch.FunctionId < m.Functions.Length, "Watch reference.");
            var import = m.Imports[watch.EventImportId]; var function = m.Functions[watch.FunctionId];
            Require(import.Kind == "HostEvent" && import.CapabilityTypeId == m.Links[watch.LinkId].CapabilityTypeId && function.ReturnType == 0 && !function.Initializer && function.ParameterTypes.SequenceEqual(import.ParameterTypes), "Watch handler signature.");
            var policy = catalog.Events.SingleOrDefault(contract => contract.Name == import.Name);
            Require(policy != null && watch.Delivery == policy.Delivery && watch.Capacity > 0 && watch.Capacity <= Math.Min(policy.Capacity, limits.EventCapacity), "Watch delivery policy.");
        }
    }

    private static void ValidateConstant(MetaModule m, int index, MetaVerificationCatalog catalog, MetaLimits limits)
    {
        var constant = m.Constants[index]; Type(m, constant.TypeId);
        var type = m.Types[constant.TypeId];
        Require(constant.Kind.ToString() == type.Kind.ToString(), "Constant kind/type mismatch.");
        Require(constant.Kind == MetaConstantKind.Struct || constant.Items.Length == 0, "Unexpected constant items.");
        var text = constant.Value;
        switch (constant.Kind)
        {
            case MetaConstantKind.Unit: Require(text == null, "Invalid unit constant."); break;
            case MetaConstantKind.Bool: Require(text is "true" or "false", "Invalid bool constant."); break;
            case MetaConstantKind.Byte: Require(byte.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var b) && text == b.ToString(CultureInfo.InvariantCulture), "Invalid byte constant."); break;
            case MetaConstantKind.Int: Require(long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) && text == integer.ToString(CultureInfo.InvariantCulture), "Invalid int constant."); break;
            case MetaConstantKind.Float:
                Require(text?.Length == 16 && text.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') && ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _), "Invalid float bits.");
                var bits = ulong.Parse(text!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                Require(double.IsFinite(BitConverter.UInt64BitsToDouble(bits)) && bits != 0x8000000000000000, "Noncanonical float.");
                break;
            case MetaConstantKind.String: Require(text != null && Encoding.UTF8.GetByteCount(text) <= limits.StringBytes, "String constant size."); break;
            case MetaConstantKind.Enum:
                Require(ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var tag) && text == tag.ToString(CultureInfo.InvariantCulture) && type.Variants.Any(variant => variant.Tag == tag) && (type.ContractHash == null || catalog.ConstructibleEnums.Contains(type.Name, StringComparer.Ordinal)), "Invalid enum constant.");
                break;
            case MetaConstantKind.Struct:
                Require(text == null && type.ContractHash == null && constant.Items.Length == type.Fields.Length, "Invalid struct constant.");
                for (var field = 0; field < constant.Items.Length; field++) Require(constant.Items[field] >= 0 && constant.Items[field] < index && m.Constants[constant.Items[field]].TypeId == type.Fields[field].TypeId, "Constant struct reference.");
                break;
            default: throw new MetaFormatException("Forbidden constant kind.");
        }
    }

    private readonly record struct Slot(int Type, int? Tag = null, int Local = -1);
    private sealed record Flow(List<Slot> Stack, bool[] Assigned, bool[] Written, Slot?[] Values, bool[] GlobalsAssigned, bool[] GlobalsWritten)
    {
        public Flow Copy() => new(Stack.ToList(), Assigned.ToArray(), Written.ToArray(), Values.ToArray(), GlobalsAssigned.ToArray(), GlobalsWritten.ToArray());
    }

    private static void ValidateFunction(MetaModule m, MetaFunctionDef f, MetaInstruction[] code, MetaLimits limits, ref int work)
    {
        Require(code.Length > 0, "Empty function.");
        var stateBytes = checked((long) code.Length * (128L + f.Locals.Length * 32L + m.Globals.Length * 2L + f.MaxStack * 24L));
        Require(stateBytes <= limits.VerificationMemoryBytes, "Verification state memory limit.");
        var byOffset = code.Select((instruction, index) => (instruction, index)).ToDictionary(pair => pair.instruction.Offset, pair => pair.index);
        foreach (var instruction in code) ValidateOperands(m, f, instruction, byOffset);
        var entry = new Flow([], f.Locals.Select(local => local.Parameter).ToArray(), f.Locals.Select(local => local.Parameter).ToArray(), new Slot?[f.Locals.Length], Enumerable.Repeat(!f.Initializer, m.Globals.Length).ToArray(), Enumerable.Repeat(!f.Initializer, m.Globals.Length).ToArray());
        var states = new Flow?[code.Length]; states[0] = entry;
        var queue = new Queue<int>(); queue.Enqueue(0);
        var maximum = 0;
        while (queue.TryDequeue(out var index))
        {
            work = checked(work + 1 + f.Locals.Length + m.Globals.Length + states[index]!.Stack.Count);
            Require(work <= limits.VerificationSteps, "Verification work limit.");
            var state = states[index]!.Copy();
            var instruction = code[index]; var op = instruction.Opcode;
            var a = checked((int) instruction.A); var b = instruction.B;
            Slot Pop(int? expected = null)
            {
                Require(state.Stack.Count > 0, "Operand stack underflow.");
                var slot = state.Stack[^1]; state.Stack.RemoveAt(state.Stack.Count - 1);
                Require(expected == null || slot.Type == expected, "Operand type mismatch.");
                return slot;
            }
            void Push(int type, int? tag = null, int local = -1) => state.Stack.Add(new Slot(type, tag, local));
            void Call(int[] parameters, int result)
            {
                for (var parameter = parameters.Length - 1; parameter >= 0; parameter--) Pop(parameters[parameter]);
                if (result != 0) Push(result);
            }
            switch (op)
            {
                case MetaOpcode.Const:
                    var constant = m.Constants[a]; Push(constant.TypeId, constant.Kind == MetaConstantKind.Enum ? int.Parse(constant.Value!, CultureInfo.InvariantCulture) : null); break;
                case MetaOpcode.Unit: Push(0); break;
                case MetaOpcode.LoadLocal: Require(state.Assigned[a], "Read of uninitialized local."); var value = state.Values[a]; Push(f.Locals[a].TypeId, value?.Tag, a); break;
                case MetaOpcode.StoreLocal:
                    var localValue = Pop(f.Locals[a].TypeId); Require(f.Locals[a].Mutable || !state.Written[a], "Write to immutable local."); state.Assigned[a] = state.Written[a] = true; state.Values[a] = localValue; break;
                case MetaOpcode.ResetLocal: state.Assigned[a] = state.Written[a] = false; state.Values[a] = null; break;
                case MetaOpcode.LoadGlobal: Require(state.GlobalsAssigned[a], "Read of uninitialized global."); Push(m.Globals[a].TypeId); break;
                case MetaOpcode.StoreGlobal: Pop(m.Globals[a].TypeId); Require(m.Globals[a].Mutable || !state.GlobalsWritten[a], "Write to immutable global."); state.GlobalsAssigned[a] = state.GlobalsWritten[a] = true; break;
                case MetaOpcode.Pop: Pop(); break;
                case MetaOpcode.Dup: var duplicate = Pop(); state.Stack.Add(duplicate); state.Stack.Add(duplicate); break;
                case >= MetaOpcode.IAdd and <= MetaOpcode.IShr:
                    Pop(3); if (op is not (MetaOpcode.INeg or MetaOpcode.INot)) Pop(3); Push(3); break;
                case >= MetaOpcode.FAdd and <= MetaOpcode.FNeg:
                    Pop(4); if (op != MetaOpcode.FNeg) Pop(4); Push(4); break;
                case >= MetaOpcode.Eq and <= MetaOpcode.Ge: Pop(a); Pop(a); Push(1); break;
                case MetaOpcode.Not: Pop(1); Push(1); break;
                case MetaOpcode.BrFalse: Pop(1); break;
                case MetaOpcode.BrVariant: Require(state.Stack.Count > 0 && state.Stack[^1].Type == a, "Variant branch operand."); break;
                case MetaOpcode.Call or MetaOpcode.CallAsync: Call(m.Functions[a].ParameterTypes, m.Functions[a].ReturnType); break;
                case MetaOpcode.Return:
                    if (f.ReturnType != 0) Pop(f.ReturnType);
                    Require(state.Stack.Count == 0, "Return leaves operand values.");
                    if (f.Initializer) Require(state.GlobalsAssigned.All(assigned => assigned), "Initializer leaves globals uninitialized.");
                    break;
                case MetaOpcode.RecordNew: foreach (var field in m.Types[a].Fields.Reverse()) Pop(field.TypeId); Push(a); break;
                case MetaOpcode.RecordGet: Pop(a); Push(m.Types[a].Fields[b].TypeId); break;
                case MetaOpcode.RecordSet: Pop(m.Types[a].Fields[b].TypeId); Pop(a); Push(a); break;
                case MetaOpcode.VariantNew:
                    var variant = m.Types[a].Variants.Single(candidate => candidate.Tag == b); if (variant.PayloadTypeId is { } payload) Pop(payload); Push(a, (int) b); break;
                case MetaOpcode.VariantTag: Pop(a); Push(3); break;
                case MetaOpcode.VariantPayload:
                    var variantValue = Pop(a); Require(variantValue.Tag == b, "Variant payload is not tag-refined."); Push(m.Types[a].Variants.Single(candidate => candidate.Tag == b).PayloadTypeId!.Value); break;
                case MetaOpcode.ArrayNew: Pop(m.Types[a].Args[0]); Pop(3); Push(a); break;
                case MetaOpcode.ArrayLen: Pop(a); Push(3); break;
                case MetaOpcode.ArrayGet: Pop(3); Pop(a); Push(m.Types[a].Args[0]); break;
                case MetaOpcode.ArraySet: Pop(m.Types[a].Args[0]); Pop(3); Pop(a); break;
                case MetaOpcode.ArrayLiteral: for (var item = 0u; item < b; item++) Pop(m.Types[a].Args[0]); Push(a); break;
                case MetaOpcode.CallIntrinsic: var signature = MetaIntrinsics.Resolve(m, instruction.A, instruction.B); Call(signature.Parameters, signature.ReturnType); break;
                case MetaOpcode.LoadLink: Push(m.Links[a].CapabilityTypeId); break;
                case MetaOpcode.HostRead or MetaOpcode.HostAwait:
                    var import = m.Imports[a]; for (var parameter = import.ParameterTypes.Length - 1; parameter >= 0; parameter--) Pop(import.ParameterTypes[parameter]); Pop(import.CapabilityTypeId); if (import.ReturnType != 0) Push(import.ReturnType!.Value); break;
            }
            maximum = Math.Max(maximum, state.Stack.Count);
            Require(maximum <= limits.OperandSlots, "Operand stack limit.");
            if (op is MetaOpcode.Return or MetaOpcode.Trap) continue;
            if (op == MetaOpcode.Br) { Merge(byOffset[a], state); continue; }
            if (op == MetaOpcode.BrFalse) Merge(byOffset[a], state.Copy());
            if (op == MetaOpcode.BrVariant)
            {
                var target = state.Copy(); var top = target.Stack[^1]; target.Stack[^1] = top with { Tag = (int) b };
                if (top.Local >= 0) target.Values[top.Local] = top with { Tag = (int) b };
                Merge(byOffset[checked((int) instruction.C)], target);
                var variants = m.Types[a].Variants;
                if (variants.Length == 2)
                {
                    var other = variants.Single(variant => variant.Tag != b).Tag;
                    state.Stack[^1] = state.Stack[^1] with { Tag = other };
                    if (top.Local >= 0) state.Values[top.Local] = top with { Tag = other };
                }
            }
            Require(index + 1 < code.Length, "Control flow falls out of function.");
            Merge(index + 1, state);

            void Merge(int destination, Flow incoming)
            {
                var existing = states[destination];
                if (existing == null) { states[destination] = incoming.Copy(); queue.Enqueue(destination); return; }
                Require(existing.Stack.Count == incoming.Stack.Count, "CFG stack height mismatch.");
                var changed = false;
                for (var slot = 0; slot < existing.Stack.Count; slot++)
                {
                    Require(existing.Stack[slot].Type == incoming.Stack[slot].Type, "CFG stack type mismatch.");
                    var merged = existing.Stack[slot] with { Tag = existing.Stack[slot].Tag == incoming.Stack[slot].Tag ? existing.Stack[slot].Tag : null, Local = existing.Stack[slot].Local == incoming.Stack[slot].Local ? existing.Stack[slot].Local : -1 };
                    if (existing.Stack[slot] != merged) { existing.Stack[slot] = merged; changed = true; }
                }
                for (var local = 0; local < existing.Assigned.Length; local++)
                {
                    var assigned = existing.Assigned[local] && incoming.Assigned[local]; var written = existing.Written[local] || incoming.Written[local];
                    var merged = existing.Values[local] == incoming.Values[local] ? existing.Values[local] : null;
                    if (assigned != existing.Assigned[local] || written != existing.Written[local] || merged != existing.Values[local]) changed = true;
                    existing.Assigned[local] = assigned; existing.Written[local] = written; existing.Values[local] = merged;
                }
                for (var global = 0; global < existing.GlobalsAssigned.Length; global++)
                {
                    var assigned = existing.GlobalsAssigned[global] && incoming.GlobalsAssigned[global]; var written = existing.GlobalsWritten[global] || incoming.GlobalsWritten[global];
                    if (assigned != existing.GlobalsAssigned[global] || written != existing.GlobalsWritten[global]) changed = true;
                    existing.GlobalsAssigned[global] = assigned; existing.GlobalsWritten[global] = written;
                }
                if (changed) queue.Enqueue(destination);
            }
        }
        Require(maximum == f.MaxStack, $"Declared maxStack {f.MaxStack} differs from computed {maximum} in {f.Name}.");
    }

    private static void ValidateOperands(MetaModule m, MetaFunctionDef f, MetaInstruction i, Dictionary<int, int> boundaries)
    {
        var a = i.A; var b = i.B; var op = i.Opcode;
        void Index(uint value, int count) => Require(value < count, "Instruction index out of range.");
        MetaTypeDef OperandType() { Index(a, m.Types.Length); return m.Types[a]; }
        void Branch(uint target) { Require(target <= int.MaxValue && boundaries.ContainsKey((int) target), "Branch target is not an instruction boundary."); if (f.Initializer) Require(target > i.Offset, "Initializer CFG must be acyclic."); }
        switch (op)
        {
            case MetaOpcode.Const: Index(a, m.Constants.Length); break;
            case MetaOpcode.LoadLocal or MetaOpcode.StoreLocal or MetaOpcode.ResetLocal:
                Index(a, f.Locals.Length); if (op == MetaOpcode.ResetLocal) Require(!f.Locals[a].Parameter, "Cannot reset a parameter."); if (op == MetaOpcode.StoreLocal) Require(!f.Locals[a].Parameter, "Cannot write a parameter."); break;
            case MetaOpcode.LoadGlobal or MetaOpcode.StoreGlobal: Index(a, m.Globals.Length); if (op == MetaOpcode.StoreGlobal && !f.Initializer) Require(m.Globals[a].Mutable, "Cannot write immutable global."); break;
            case >= MetaOpcode.Eq and <= MetaOpcode.Ge:
                var comparable = OperandType().Kind;
                Require(op is MetaOpcode.Eq or MetaOpcode.Ne ? IsComparable(m, (int) a) : comparable is MetaTypeKind.Byte or MetaTypeKind.Int or MetaTypeKind.Float or MetaTypeKind.String, "Unsupported comparison type."); break;
            case MetaOpcode.Br or MetaOpcode.BrFalse: Branch(a); break;
            case MetaOpcode.BrVariant: Variant(); Branch(i.C); break;
            case MetaOpcode.Call or MetaOpcode.CallAsync:
                Index(a, m.Functions.Length); Require(!m.Functions[a].Initializer && (op == MetaOpcode.CallAsync ? f.Async && m.Functions[a].Async : !m.Functions[a].Async), "Invalid async call effect."); break;
            case MetaOpcode.RecordNew or MetaOpcode.RecordGet or MetaOpcode.RecordSet:
                var record = OperandType(); Require(record.Kind is MetaTypeKind.Struct or MetaTypeKind.NativeDto, "Not a record type.");
                if (op != MetaOpcode.RecordGet) Require(record.Kind == MetaTypeKind.Struct && record.ContractHash == null, "Native record construction/mutation is forbidden.");
                if (op != MetaOpcode.RecordNew) { Index(b, record.Fields.Length); if (op == MetaOpcode.RecordSet) Require(!record.Fields[b].Readonly, "Readonly field mutation."); } break;
            case MetaOpcode.VariantNew or MetaOpcode.VariantPayload: Variant(); if (op == MetaOpcode.VariantPayload) Require(m.Types[a].Variants.Single(variant => variant.Tag == b).PayloadTypeId != null, "Variant has no payload."); if (op == MetaOpcode.VariantNew && m.Types[a].ContractHash != null) throw new MetaFormatException("Native enum construction must use trusted constants."); break;
            case MetaOpcode.VariantTag: Require(OperandType().Kind is MetaTypeKind.Enum or MetaTypeKind.Option or MetaTypeKind.Result, "Not a variant type."); break;
            case >= MetaOpcode.ArrayNew and <= MetaOpcode.ArrayLiteral: Require(OperandType().Kind == MetaTypeKind.Array, "Not an array type."); Require(op != MetaOpcode.ArrayLiteral || b <= 65536, "Array literal limit."); break;
            case MetaOpcode.CallIntrinsic:
                var signature = MetaIntrinsics.Resolve(m, a, b);
                if (f.Initializer) Require(signature.Effect is not ("Log" or "ReadClock") && (a < 0x0100 || a is 0x0100 or 0x0110), "Initializer intrinsic effect."); break;
            case MetaOpcode.LoadLink: Index(a, m.Links.Length); break;
            case MetaOpcode.HostRead or MetaOpcode.HostAwait:
                Index(a, m.Imports.Length); Require(m.Imports[a].Kind == "HostMethod" && m.Imports[a].Effect == (op == MetaOpcode.HostRead ? "Read" : "Await") && (op != MetaOpcode.HostAwait || f.Async), "Host call effect mismatch."); break;
            case MetaOpcode.Yield: break;
            case MetaOpcode.Trap: Require(a <= 7, "Unknown VM fault ID."); break;
        }
        if (f.Initializer) Require(op is not (MetaOpcode.Call or MetaOpcode.CallAsync or MetaOpcode.HostRead or MetaOpcode.HostAwait or MetaOpcode.LoadLink or MetaOpcode.Yield), "Forbidden initializer effect.");
        void Variant() { var type = OperandType(); Require(type.Kind is MetaTypeKind.Enum or MetaTypeKind.Option or MetaTypeKind.Result && type.Variants.Any(variant => variant.Tag == b), "Invalid variant type/tag."); }
    }

    private static bool IsComparable(MetaModule module, int typeId)
    {
        var type = module.Types[typeId];
        return type.Kind switch
        {
            MetaTypeKind.Unit or MetaTypeKind.Bool or MetaTypeKind.Byte or MetaTypeKind.Int or MetaTypeKind.Float or MetaTypeKind.String or MetaTypeKind.Enum => true,
            MetaTypeKind.Struct or MetaTypeKind.NativeDto => type.Fields.All(field => IsComparable(module, field.TypeId)),
            MetaTypeKind.Option or MetaTypeKind.Result => type.Variants.All(variant => variant.PayloadTypeId is not { } payload || IsComparable(module, payload)),
            _ => false
        };
    }

    private static void ValidateDebug(MetaModule m, MetaInstruction[][] instructions)
    {
        if (m.Debug is not { } debug) return;
        var boundaries = instructions.Select(function => function.Select(instruction => instruction.Offset).ToHashSet()).ToArray();
        for (var id = 0; id < debug.Files.Length; id++)
        {
            var file = debug.Files[id]; Require(file.Id == id && Encoding.UTF8.GetByteCount(file.Path) <= 192 && file.Path.Length > 0 && !file.Path.StartsWith('/') && !file.Path.Contains('\\') && !file.Path.Contains(':') && file.Path.Split('/').All(part => part is not ("" or "." or "..")), "Unsafe debug virtual path."); Hash(file.SourceHash);
        }
        foreach (var span in debug.Spans)
        {
            Require(span.FunctionId >= 0 && span.FunctionId < instructions.Length && span.FileId >= 0 && span.FileId < debug.Files.Length && span.Utf8Start >= 0 && span.Utf8Length >= 0 && (long) span.Utf8Start + span.Utf8Length <= int.MaxValue, "Invalid debug span.");
            Require(boundaries[span.FunctionId].Contains(span.PcOffset), "Debug PC is not an instruction boundary.");
        }
        foreach (var local in debug.Locals) { Require(local.FunctionId >= 0 && local.FunctionId < m.Functions.Length && local.LocalId >= 0 && local.LocalId < m.Functions[local.FunctionId].Locals.Length, "Invalid debug local."); Symbol(local.Name); }
    }

    private static void Symbol(string value) => Require(value != null && value.Length > 0 && Encoding.UTF8.GetByteCount(value) <= 128 && !value.Any(char.IsControl), "Invalid symbol.");
    private static void Hash(string value) => Require(value != null && value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'), "Invalid hash.");
    private static void Type(MetaModule m, int id) => Require(id >= 0 && id < m.Types.Length, "Type reference out of range.");
    private static void Require(bool condition, string message) { if (!condition) throw new MetaFormatException(message); }
}
