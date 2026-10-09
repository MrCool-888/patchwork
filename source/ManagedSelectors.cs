using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Patchwork
{
    // Typed, bounded transformations. Patch files supply member names, never IL or executable payloads.
    public static class ManagedSelectors
    {
        public static bool Parse(PatchOperation op, Dictionary<string, object> raw)
        {
            if (op.Kind == "managedOverrideBooleanSetter" || op.Kind == "managedOverrideConditionalBooleanSetter")
            {
                op.Type = Json.String(raw, "type"); op.SetterMethod = Json.String(raw, "setterMethod");
                if (!raw.TryGetValue("value", out op.Value) || !(op.Value is bool)) throw new InvalidDataException("Boolean setter override needs a boolean value.");
                if (op.Kind == "managedOverrideConditionalBooleanSetter") ParseCondition(op, raw);
                return true;
            }
            if (op.Kind != "managedConditionalCall" && op.Kind != "managedConditionalBooleanCall" && op.Kind != "managedConditionalProjection") return false;
            ParseCondition(op, raw);
            if (op.Kind == "managedConditionalCall" || op.Kind == "managedConditionalBooleanCall")
            {
                op.CalledMethod = Json.String(raw, "calledMethod");
                if (op.Kind == "managedConditionalBooleanCall") { if (!raw.TryGetValue("value", out op.Value) || !(op.Value is bool)) throw new InvalidDataException("Conditional boolean call needs a boolean value."); }
                else op.ReplacementMethod = Json.String(raw, "replacementMethod");
                object count;
                if (!raw.TryGetValue("count", out count) || !(count is int) || (int)count < 1 || (int)count > 1000) throw new InvalidDataException("An exact call count is required.");
                op.Count = (int)count;
            }
            else
            {
                op.SourceMethod = Json.String(raw, "sourceMethod");
                var mappings = Json.Array(raw, "mappings");
                if (mappings.Count < 1 || mappings.Count > 16) throw new InvalidDataException("Projections need 1 to 16 getter/setter mappings.");
                foreach (object mapping in mappings)
                {
                    var pair = Json.Object(mapping);
                    op.Mappings.Add(new Dictionary<string, string> { { "getter", Json.String(pair, "getter") }, { "setter", Json.String(pair, "setter") } });
                }
            }
            return true;
        }
        static void ParseCondition(PatchOperation op, Dictionary<string, object> raw)
        {
            var chain = Json.Array(raw, "condition");
            if (chain.Count < 1 || chain.Count > 8 || chain.Any(x => !(x is string) || ((string)x).Length > 2048)) throw new InvalidDataException("Conditions need 1 to 8 field/getter signatures.");
            op.Condition = chain.Cast<string>().ToList();
        }
        static TypeDefinition Type(ModuleDefinition module, string name)
        {
            var local = ManagedPatches.Types(module.Types).SingleOrDefault(x => x.FullName == name);
            if (local != null) return local;
            var reference = module.GetTypeReferences().FirstOrDefault(x => x.FullName == name);
            if (reference == null) throw new InvalidDataException("Type signature was not found: " + name);
            return reference.Resolve();
        }
        static string Owner(string signature)
        {
            int end = signature.IndexOf("::", StringComparison.Ordinal);
            if (end < 1) throw new InvalidDataException("A full member signature is required.");
            int start = signature.LastIndexOf(' ', end);
            return signature.Substring(start + 1, end - start - 1);
        }
        static MethodDefinition Method(ModuleDefinition module, string signature)
        {
            var matches = Type(module, Owner(signature)).Methods.Where(x => x.FullName == signature).ToList();
            if (matches.Count != 1) throw new InvalidDataException("Method signature was not found: " + signature);
            return matches[0];
        }
        static bool Inherits(TypeReference receiver, TypeReference owner)
        {
            for (int depth = 0; receiver != null && depth < 40; depth++)
            {
                if (receiver.FullName == owner.FullName || receiver.GetElementType().FullName == owner.FullName) return true;
                var definition = receiver.Resolve();
                if (definition.Interfaces.Any(x => x.InterfaceType.FullName == owner.FullName)) return true;
                receiver = definition.BaseType;
            }
            return false;
        }
        static List<Instruction> Condition(ModuleDefinition module, MethodDefinition host, PatchOperation op)
        {
            if (host.IsStatic) throw new InvalidDataException("Conditions require an instance method.");
            var code = new List<Instruction> { Instruction.Create(OpCodes.Ldarg_0) };
            TypeReference current = host.DeclaringType;
            foreach (string member in op.Condition)
            {
                if (current.IsValueType)
                {
                    var local = new VariableDefinition(module.ImportReference(current)); host.Body.Variables.Add(local); host.Body.InitLocals = true;
                    code.Add(Instruction.Create(OpCodes.Stloc, local)); code.Add(Instruction.Create(OpCodes.Ldloca, local));
                }
                var owner = Type(module, Owner(member));
                if (!Inherits(current, owner)) throw new InvalidDataException("Condition member does not match its receiver.");
                if (member.IndexOf('(') >= 0)
                {
                    var getter = Method(module, member);
                    if (getter.IsStatic || getter.Parameters.Count != 0 || !getter.IsGetter || getter.HasGenericParameters) throw new InvalidDataException("Conditions permit only instance property getters.");
                    code.Add(Instruction.Create(current.IsValueType ? OpCodes.Call : OpCodes.Callvirt, module.ImportReference(getter))); current = getter.ReturnType;
                }
                else
                {
                    var field = owner.Fields.SingleOrDefault(x => x.FullName == member);
                    if (field == null || field.IsStatic || !field.IsPublic && field.DeclaringType != host.DeclaringType && !field.IsFamily) throw new InvalidDataException("Condition field is missing or inaccessible.");
                    code.Add(Instruction.Create(OpCodes.Ldfld, module.ImportReference(field))); current = field.FieldType;
                }
            }
            if (current.FullName != "System.Boolean") throw new InvalidDataException("Condition must end in a boolean getter or field.");
            return code;
        }
        static void InsertAfter(MethodDefinition method, Instruction original, IEnumerable<Instruction> instructions)
        {
            var il = method.Body.GetILProcessor(); var cursor = original;
            foreach (var next in instructions) { il.InsertAfter(cursor, next); cursor = next; }
        }
        static GenericInstanceMethod Linq(ModuleDefinition module, string name, int argumentCount, params TypeReference[] arguments)
        {
            var existing = module.GetMemberReferences().OfType<MethodReference>().FirstOrDefault(x => x.DeclaringType.FullName == "System.Linq.Enumerable" && x.Name == name && x.Parameters.Count == argumentCount && x.GenericParameters.Count == arguments.Length && (name != "Select" || x.Parameters[1].ParameterType.FullName.StartsWith("System.Func`2", StringComparison.Ordinal)));
            if (existing == null) throw new InvalidDataException("Compatible LINQ method was not found: " + name);
            var generic = new GenericInstanceMethod(module.ImportReference(existing));
            foreach (var argument in arguments) generic.GenericArguments.Add(module.ImportReference(argument));
            return generic;
        }
        static void ConditionalCall(ModuleDefinition module, MethodDefinition method, PatchOperation op)
        {
            bool boolean = op.Kind == "managedConditionalBooleanCall";
            var replacement = boolean ? null : Method(module, op.ReplacementMethod);
            var calls = method.Body.Instructions.Where(x => (x.OpCode == OpCodes.Call || x.OpCode == OpCodes.Callvirt) && x.Operand is MethodReference && ((MethodReference)x.Operand).FullName == op.CalledMethod).ToList();
            if (calls.Count != op.Count) throw new InvalidDataException("Expected " + op.Count + " calls; found " + calls.Count + ".");
            foreach (var instruction in calls)
            {
                var called = (MethodReference)instruction.Operand;
                if (!called.HasThis || called.Parameters.Count != 0 || called.HasGenericParameters || (boolean ? called.ReturnType.FullName != "System.Boolean" : replacement.IsStatic || replacement.Parameters.Count != 0 || called.ReturnType.FullName != replacement.ReturnType.FullName || called.DeclaringType.FullName != replacement.DeclaringType.FullName || replacement.HasGenericParameters)) throw new InvalidDataException("Conditional redirects need matching parameterless instance signatures.");
                int index = method.Body.Instructions.IndexOf(instruction);
                if (index > 0 && method.Body.Instructions[index - 1].OpCode.OpCodeType == OpCodeType.Prefix) throw new InvalidDataException("Prefixed calls cannot be edited.");
                var originalCall = Instruction.Create(instruction.OpCode, called); var end = Instruction.Create(OpCodes.Nop);
                var code = Condition(module, method, op);
                code.Add(Instruction.Create(OpCodes.Brtrue, originalCall));
                if (boolean) { code.Add(Instruction.Create(OpCodes.Pop)); code.Add(Instruction.Create((bool)op.Value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0)); }
                else code.Add(Instruction.Create(OpCodes.Callvirt, module.ImportReference(replacement)));
                code.Add(Instruction.Create(OpCodes.Br, end)); code.Add(originalCall); code.Add(end);
                instruction.OpCode = OpCodes.Nop; instruction.Operand = null; InsertAfter(method, instruction, code);
            }
        }
        static void Projection(ModuleDefinition module, MethodDefinition method, PatchOperation op, List<MethodDefinition> touched)
        {
            var source = Method(module, op.SourceMethod);
            var destinationList = method.ReturnType as GenericInstanceType; var sourceList = source.ReturnType as GenericInstanceType;
            if (method.IsStatic || method.Parameters.Count != 0 || source.IsStatic || source.Parameters.Count != 0 || !Inherits(method.DeclaringType, source.DeclaringType) || destinationList == null || sourceList == null || destinationList.ElementType.FullName != "System.Collections.Generic.IReadOnlyList`1" || sourceList.ElementType.FullName != "System.Collections.Generic.IReadOnlyList`1") throw new InvalidDataException("Projection requires parameterless read-only list getters.");
            var input = sourceList.GenericArguments[0]; var output = destinationList.GenericArguments[0]; var outputType = output.Resolve();
            var constructor = outputType.Methods.SingleOrDefault(x => x.IsConstructor && !x.IsStatic && x.IsPublic && x.Parameters.Count == 0);
            if (output.IsValueType || constructor == null || outputType.IsAbstract) throw new InvalidDataException("Projection destination needs a public parameterless class constructor.");
            string name = "PatchworkProject_" + method.MetadataToken.ToInt32().ToString("x8");
            if (method.DeclaringType.Methods.Any(x => x.Name == name)) throw new InvalidDataException("Projection helper already exists.");
            var projector = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, module.ImportReference(output));
            projector.Parameters.Add(new ParameterDefinition("item", ParameterAttributes.None, module.ImportReference(input)));
            projector.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj, module.ImportReference(constructor)));
            var setters = new HashSet<string>();
            foreach (var mapping in op.Mappings)
            {
                var getter = Method(module, mapping["getter"]); var setter = Method(module, mapping["setter"]);
                if (!getter.IsGetter || getter.IsStatic || !getter.IsPublic || getter.Parameters.Count != 0 || !setter.IsSetter || setter.IsStatic || !setter.IsPublic || setter.Parameters.Count != 1 || !Inherits(input, getter.DeclaringType) || !Inherits(output, setter.DeclaringType) || getter.ReturnType.FullName != setter.Parameters[0].ParameterType.FullName || !setters.Add(setter.FullName)) throw new InvalidDataException("Projection mapping has incompatible getter/setter signatures.");
                projector.Body.Instructions.Add(Instruction.Create(OpCodes.Dup)); projector.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                projector.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, module.ImportReference(getter)));
                projector.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, module.ImportReference(setter)));
            }
            projector.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); method.DeclaringType.Methods.Add(projector); touched.Add(projector);
            var function = new GenericInstanceType(module.GetTypeReferences().First(x => x.FullName == "System.Func`2")); function.GenericArguments.Add(module.ImportReference(input)); function.GenericArguments.Add(module.ImportReference(output));
            var funcCtor = new MethodReference(".ctor", module.TypeSystem.Void, function) { HasThis = true };
            funcCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object)); funcCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr));
            var first = method.Body.Instructions[0]; var code = Condition(module, method, op);
            code.Add(Instruction.Create(OpCodes.Brtrue, first)); code.Add(Instruction.Create(OpCodes.Ldarg_0)); code.Add(Instruction.Create(OpCodes.Call, module.ImportReference(source)));
            code.Add(Instruction.Create(OpCodes.Ldnull)); code.Add(Instruction.Create(OpCodes.Ldftn, projector)); code.Add(Instruction.Create(OpCodes.Newobj, funcCtor));
            code.Add(Instruction.Create(OpCodes.Call, Linq(module, "Select", 2, input, output))); code.Add(Instruction.Create(OpCodes.Call, Linq(module, "ToList", 1, output))); code.Add(Instruction.Create(OpCodes.Ret));
            var il = method.Body.GetILProcessor(); foreach (var instruction in code) il.InsertBefore(first, instruction);
        }
        static MethodReference Bind(ModuleDefinition module, MethodDefinition method, TypeReference owner)
        {
            var reference = new MethodReference(method.Name, module.ImportReference(method.ReturnType), module.ImportReference(owner)) { HasThis = true, CallingConvention = method.CallingConvention };
            foreach (var parameter in method.Parameters) reference.Parameters.Add(new ParameterDefinition(module.ImportReference(parameter.ParameterType)));
            return reference;
        }
        static void SetterOverride(ModuleDefinition module, MethodDefinition method, PatchOperation op, List<MethodDefinition> touched, StringBuilder before)
        {
            var type = Type(module, op.Type); var setter = Method(module, op.SetterMethod);
            if (type.Module != module || !method.IsVirtual || method.IsFinal || method.IsStatic || method.Parameters.Count != 1 || method.Parameters[0].ParameterType.FullName != "System.Boolean" || method.ReturnType.FullName != "System.Void" || !setter.IsSetter || setter.IsStatic || setter.Parameters.Count != 1 || setter.Parameters[0].ParameterType.FullName != "System.Boolean" || !Inherits(type, method.DeclaringType) || !Inherits(type, setter.DeclaringType) || type.Methods.Any(x => x.Name == method.Name && x.Parameters.Count == 1)) throw new InvalidDataException("Invalid boolean setter override.");
            TypeReference ancestor = type.BaseType; MethodReference inherited = null;
            for (int depth = 0; ancestor != null && depth < 40; depth++)
            {
                var definition = ancestor.Resolve(); var implementation = definition.Methods.FirstOrDefault(x => x.Name == method.Name && x.IsVirtual && x.Parameters.Count == 1 && x.Parameters[0].ParameterType.FullName == "System.Boolean");
                if (implementation != null) { inherited = Bind(module, implementation, ancestor); break; }
                ancestor = definition.BaseType;
            }
            if (inherited == null) throw new InvalidDataException("Inherited implementation was not found.");
            before.AppendLine(type.FullName + " inherits " + inherited.FullName);
            var replacement = new MethodDefinition(method.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig, module.TypeSystem.Void);
            replacement.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, module.TypeSystem.Boolean));
            replacement.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); replacement.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1)); replacement.Body.Instructions.Add(Instruction.Create(OpCodes.Call, inherited));
            // Attach before validating the condition so its receiver is the actual derived type.
            type.Methods.Add(replacement);
            var end = Instruction.Create(OpCodes.Ret);
            if (op.Kind == "managedOverrideConditionalBooleanSetter")
            {
                foreach (var instruction in Condition(module, replacement, op)) replacement.Body.Instructions.Add(instruction);
                replacement.Body.Instructions.Add(Instruction.Create(OpCodes.Brfalse, end));
            }
            replacement.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); replacement.Body.Instructions.Add(Instruction.Create((bool)op.Value ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0)); replacement.Body.Instructions.Add(Instruction.Create(OpCodes.Call, module.ImportReference(setter))); replacement.Body.Instructions.Add(end);
            touched.Add(replacement);
        }
        public static bool Transform(ModuleDefinition module, MethodDefinition method, PatchOperation op, List<MethodDefinition> touched, StringBuilder before)
        {
            if (op.Kind == "managedConditionalCall" || op.Kind == "managedConditionalBooleanCall") ConditionalCall(module, method, op);
            else if (op.Kind == "managedConditionalProjection") Projection(module, method, op, touched);
            else if (op.Kind == "managedOverrideBooleanSetter" || op.Kind == "managedOverrideConditionalBooleanSetter") SetterOverride(module, method, op, touched, before);
            else return false;
            return true;
        }
    }
}
