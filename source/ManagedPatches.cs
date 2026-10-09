using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Patchwork
{
    // Patch files describe a small set of IL edits; they cannot supply code, assemblies or commands.
    public static class ManagedPatches
    {
        static readonly string[] Kinds = { "managedReturn", "managedBooleanCall", "managedSuppressCall", "managedOverrideBoolean", "managedOverrideBooleanArgument", "managedConditionalCall", "managedConditionalBooleanCall", "managedConditionalProjection", "managedOverrideBooleanSetter", "managedOverrideConditionalBooleanSetter" };
        public static void Parse(PatchOperation op, Dictionary<string, object> raw)
        {
            if (!Kinds.Contains(op.Kind)) throw new InvalidDataException("Unknown operation: " + op.Kind);
            if (!op.File.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Managed operations require a .dll file.");
            op.Method = Json.String(raw, "method");
            if (String.IsNullOrWhiteSpace(op.Method) || op.Method.Length > 2048) throw new InvalidDataException("A complete managed method signature is required.");
            if (ManagedSelectors.Parse(op, raw)) return;
            if (op.Kind == "managedReturn")
            {
                op.ReturnType = Json.String(raw, "returnType");
                if (!new[] { "boolean", "int32", "enum", "string", "void", "timeSpanZero" }.Contains(op.ReturnType)) throw new InvalidDataException("Unsupported managed return type.");
                if (op.ReturnType != "void" && op.ReturnType != "timeSpanZero" && !raw.TryGetValue("value", out op.Value)) throw new InvalidDataException("A return value is required.");
                if (op.ReturnType == "boolean" && !(op.Value is bool) || op.ReturnType == "string" && !(op.Value is string) || (op.ReturnType == "int32" || op.ReturnType == "enum") && !(op.Value is int)) throw new InvalidDataException("Return value has the wrong type.");
            }
            else if (op.Kind == "managedOverrideBoolean" || op.Kind == "managedOverrideBooleanArgument")
            {
                op.Type = Json.String(raw, "type");
                if (!raw.TryGetValue("value", out op.Value) || !(op.Value is bool)) throw new InvalidDataException("Boolean override needs a boolean value.");
            }
            else
            {
                op.CalledMethod = Json.String(raw, "calledMethod"); object count;
                if (!raw.TryGetValue("count", out count) || !(count is int) || (int)count < 1 || (int)count > 1000) throw new InvalidDataException("An exact call count is required.");
                op.Count = (int)count;
                if (op.Kind == "managedBooleanCall" && (!raw.TryGetValue("value", out op.Value) || !(op.Value is bool))) throw new InvalidDataException("Boolean call needs a boolean value.");
            }
        }
        public static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types)
        {
            foreach (var type in types) { yield return type; foreach (var nested in Types(type.NestedTypes)) yield return nested; }
        }
        static string Describe(MethodDefinition method)
        {
            var text = new StringBuilder(); text.AppendLine(method.FullName);
            if (!method.HasBody) text.AppendLine("  (no method body)");
            else foreach (var instruction in method.Body.Instructions) text.AppendLine("  " + instruction);
            return text.ToString();
        }
        public static void Compare(byte[] before, byte[] after, string root, out string beforeText, out string afterText)
        {
            using (var resolver = Resolver(root))
            using (var leftStream = new MemoryStream(before, false))
            using (var rightStream = new MemoryStream(after, false))
            using (var left = ModuleDefinition.ReadModule(leftStream, new ReaderParameters { AssemblyResolver = resolver, InMemory = true }))
            using (var right = ModuleDefinition.ReadModule(rightStream, new ReaderParameters { AssemblyResolver = resolver, InMemory = true }))
            {
                var oldMethods = Types(left.Types).SelectMany(x => x.Methods).GroupBy(x => x.FullName).ToDictionary(x => x.Key, x => String.Join("\n", x.Select(Describe).OrderBy(s => s, StringComparer.Ordinal)));
                var newMethods = Types(right.Types).SelectMany(x => x.Methods).GroupBy(x => x.FullName).ToDictionary(x => x.Key, x => String.Join("\n", x.Select(Describe).OrderBy(s => s, StringComparer.Ordinal)));
                var oldText = new StringBuilder(); var newText = new StringBuilder();
                foreach (string name in oldMethods.Keys.Union(newMethods.Keys).OrderBy(x => x, StringComparer.Ordinal))
                {
                    string oldValue, newValue;
                    if (!oldMethods.TryGetValue(name, out oldValue)) oldValue = name + "\n  (method not present)\n";
                    if (!newMethods.TryGetValue(name, out newValue)) newValue = name + "\n  (method not present)\n";
                    if (oldValue == newValue) continue;
                    oldText.AppendLine(oldValue); newText.AppendLine(newValue);
                }
                beforeText = oldText.Length == 0 ? "Method bodies are unchanged; this file remains tracked for restoration." : oldText.ToString();
                afterText = newText.Length == 0 ? "Method bodies are unchanged; this file remains tracked for restoration." : newText.ToString();
            }
        }
        static DefaultAssemblyResolver Resolver(string root)
        {
            var resolver = new DefaultAssemblyResolver(); resolver.AddSearchDirectory(root);
            // Resolve forwarded framework types when inspecting .NET 8 assemblies from a Framework host.
            string shared = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared");
            if (Directory.Exists(shared)) foreach (string family in Directory.GetDirectories(shared))
                foreach (string version in Directory.GetDirectories(family).OrderByDescending(x => x)) resolver.AddSearchDirectory(version);
            return resolver;
        }
        public static byte[] Transform(byte[] original, string root, List<PatchOperation> operations, out string beforeText, out string afterText)
        {
            using (var resolver = Resolver(root))
            using (var input = new MemoryStream(original, false))
            using (var module = ModuleDefinition.ReadModule(input, new ReaderParameters { AssemblyResolver = resolver, InMemory = true }))
            {
                if ((module.Attributes & ModuleAttributes.ILOnly) == 0) throw new InvalidDataException("Mixed native/managed assemblies are not supported.");
                var all = Types(module.Types).ToList();
                var before = new StringBuilder(); var after = new StringBuilder();
                var seen = new HashSet<string>(); var returns = new HashSet<string>(); var edits = new HashSet<string>();
                var touched = new List<MethodDefinition>();
                foreach (var op in operations)
                {
                    // Shared UI gates can be required by two independently selected patches.
                    if (!seen.Add(Json.Serializer.Serialize(op))) continue;
                    var matches = all.SelectMany(x => x.Methods).Where(x => x.FullName == op.Method).ToList();
                    if (matches.Count != 1) throw new InvalidDataException("Managed method signature did not match exactly: " + op.Method);
                    var method = matches[0];
                    if (!method.HasBody) throw new InvalidDataException("Cannot edit a method without IL: " + op.Method);
                    bool isOverride = op.Kind == "managedOverrideBoolean" || op.Kind == "managedOverrideBooleanArgument" || op.Kind == "managedOverrideBooleanSetter" || op.Kind == "managedOverrideConditionalBooleanSetter";
                    string key = isOverride ? op.Type + "::" + method.Name : method.FullName;
                    if (op.Kind == "managedReturn" || op.Kind == "managedConditionalProjection" || isOverride)
                    {
                        if (!returns.Add(key) || edits.Contains(key)) throw new InvalidOperationException("Conflicting managed method edits: " + key);
                    }
                    else
                    {
                        if (returns.Contains(key) || !edits.Add(key + "|" + op.CalledMethod)) throw new InvalidOperationException("Conflicting managed call edits: " + key);
                        edits.Add(key);
                    }
                    if (!touched.Contains(method) && !isOverride) { before.AppendLine(Describe(method)); touched.Add(method); }
                    if (ManagedSelectors.Transform(module, method, op, touched, before)) { }
                    else if (op.Kind == "managedReturn") SetReturn(method, op);
                    else if (isOverride)
                    {
                        var type = all.SingleOrDefault(x => x.FullName == op.Type);
                        bool argument = op.Kind == "managedOverrideBooleanArgument";
                        bool signature = argument ? method.Parameters.Count == 1 && method.Parameters[0].ParameterType.FullName == "System.Boolean" && method.ReturnType.FullName == "System.Void" : method.Parameters.Count == 0 && method.ReturnType.FullName == "System.Boolean";
                        if (type == null || !method.IsVirtual || method.IsFinal || method.IsStatic || !signature) throw new InvalidDataException("Invalid boolean override target.");
                        var ancestor = type.BaseType; bool inherits = false;
                        for (int depth = 0; ancestor != null && depth < 40; depth++)
                        {
                            if (ancestor.FullName == method.DeclaringType.FullName) { inherits = true; break; }
                            var local = all.SingleOrDefault(x => x.FullName == ancestor.FullName); ancestor = local == null ? null : local.BaseType;
                        }
                        if (!inherits || type.Methods.Any(x => x.Name == method.Name && x.Parameters.Count == method.Parameters.Count)) throw new InvalidDataException("Override must target an inherited, unmodified method.");
                        before.AppendLine(type.FullName + " inherits\n" + Describe(method));
                        var flags = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig;
                        if (method.IsSpecialName) flags |= MethodAttributes.SpecialName;
                        var replacement = new MethodDefinition(method.Name, flags, argument ? module.TypeSystem.Void : module.TypeSystem.Boolean);
                        if (argument) { replacement.Parameters.Add(new ParameterDefinition(method.Parameters[0].Name, ParameterAttributes.None, module.TypeSystem.Boolean)); replacement.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); }
                        replacement.Body.Instructions.Add(Instruction.Create((bool)op.Value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
                        if (argument) replacement.Body.Instructions.Add(Instruction.Create(OpCodes.Call, method));
                        replacement.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                        replacement.Body.MaxStackSize = argument ? 2 : 1; type.Methods.Add(replacement); touched.Add(replacement);
                    }
                    else EditCalls(method, op);
                }
                foreach (var method in touched) ExpandBranches(method);
                module.Attributes &= ~ModuleAttributes.StrongNameSigned;
                using (var output = new MemoryStream())
                {
                    module.Write(output, new WriterParameters { WriteSymbols = false });
                    byte[] result = output.ToArray();
                    if (result.Length > 8 * 1024 * 1024) throw new InvalidDataException("The patched assembly would exceed 8 MB.");
                    // Independently re-read the generated PE and every edited method before it reaches disk.
                    using (var verify = ModuleDefinition.ReadModule(new MemoryStream(result), new ReaderParameters { AssemblyResolver = resolver }))
                        foreach (var method in touched)
                        {
                            var check = Types(verify.Types).SelectMany(x => x.Methods).Single(x => x.FullName == method.FullName);
                            if (!check.HasBody || check.Body.Instructions.Count == 0) throw new InvalidDataException("Generated method did not verify.");
                            after.AppendLine(Describe(check));
                        }
                    beforeText = before.ToString(); afterText = after.ToString(); return result;
                }
            }
        }
        static void SetReturn(MethodDefinition method, PatchOperation op)
        {
            string actual = method.ReturnType.FullName;
            bool valid = op.ReturnType == "boolean" && actual == "System.Boolean" || op.ReturnType == "int32" && actual == "System.Int32" || op.ReturnType == "string" && actual == "System.String" || op.ReturnType == "void" && actual == "System.Void" || op.ReturnType == "timeSpanZero" && actual == "System.TimeSpan";
            if (op.ReturnType == "enum") valid = method.ReturnType.Resolve().IsEnum;
            if (!valid) throw new InvalidDataException("Return type mismatch: " + method.FullName);
            method.Body = new MethodBody(method) { InitLocals = false, MaxStackSize = 1 };
            var code = method.Body.Instructions;
            if (op.ReturnType == "boolean") code.Add(Instruction.Create((bool)op.Value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
            else if (op.ReturnType == "int32" || op.ReturnType == "enum") code.Add(Instruction.Create(OpCodes.Ldc_I4, (int)op.Value));
            else if (op.ReturnType == "string") code.Add(Instruction.Create(OpCodes.Ldstr, (string)op.Value));
            else if (op.ReturnType == "timeSpanZero") code.Add(Instruction.Create(OpCodes.Ldsfld, new FieldReference("Zero", method.ReturnType, method.ReturnType)));
            code.Add(Instruction.Create(OpCodes.Ret));
        }
        static void EditCalls(MethodDefinition method, PatchOperation op)
        {
            var calls = method.Body.Instructions.Where(x => (x.OpCode == OpCodes.Call || x.OpCode == OpCodes.Callvirt) && x.Operand is MethodReference && ((MethodReference)x.Operand).FullName == op.CalledMethod).ToList();
            if (calls.Count != op.Count) throw new InvalidDataException("Expected " + op.Count + " calls in " + op.Method + "; found " + calls.Count + ".");
            var processor = method.Body.GetILProcessor();
            foreach (var instruction in calls)
            {
                var called = (MethodReference)instruction.Operand;
                if (called.CallingConvention == MethodCallingConvention.VarArg || called.HasGenericParameters) throw new InvalidDataException("Variable-argument and generic calls are unsupported.");
                if (op.Kind == "managedBooleanCall" && (called.ReturnType.FullName != "System.Boolean" || called.Parameters.Count != 0)) throw new InvalidDataException("Only parameterless boolean calls can be replaced.");
                if (op.Kind == "managedSuppressCall" && called.ReturnType.FullName != "System.Void") throw new InvalidDataException("Only void calls can be suppressed.");
                var index = method.Body.Instructions.IndexOf(instruction);
                if (index > 0 && method.Body.Instructions[index - 1].OpCode.OpCodeType == OpCodeType.Prefix) throw new InvalidDataException("Prefixed calls cannot be edited.");
                int pops = called.Parameters.Count + (called.HasThis ? 1 : 0);
                // Keep the original instruction object, so every branch and exception boundary remains valid.
                instruction.OpCode = pops > 0 ? OpCodes.Pop : OpCodes.Nop; instruction.Operand = null;
                var cursor = instruction;
                for (int i = 1; i < pops; i++) { var pop = Instruction.Create(OpCodes.Pop); processor.InsertAfter(cursor, pop); cursor = pop; }
                if (op.Kind == "managedBooleanCall") processor.InsertAfter(cursor, Instruction.Create((bool)op.Value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
            }
        }
        static void ExpandBranches(MethodDefinition method)
        {
            var shortCodes = new[] { OpCodes.Br_S, OpCodes.Brfalse_S, OpCodes.Brtrue_S, OpCodes.Beq_S, OpCodes.Bge_S, OpCodes.Bge_Un_S, OpCodes.Bgt_S, OpCodes.Bgt_Un_S, OpCodes.Ble_S, OpCodes.Ble_Un_S, OpCodes.Blt_S, OpCodes.Blt_Un_S, OpCodes.Bne_Un_S, OpCodes.Leave_S };
            var longCodes = new[] { OpCodes.Br, OpCodes.Brfalse, OpCodes.Brtrue, OpCodes.Beq, OpCodes.Bge, OpCodes.Bge_Un, OpCodes.Bgt, OpCodes.Bgt_Un, OpCodes.Ble, OpCodes.Ble_Un, OpCodes.Blt, OpCodes.Blt_Un, OpCodes.Bne_Un, OpCodes.Leave };
            foreach (var instruction in method.Body.Instructions)
                for (int i = 0; i < shortCodes.Length; i++) if (instruction.OpCode == shortCodes[i]) { instruction.OpCode = longCodes[i]; break; }
        }
    }
}
