using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Patchwork
{
    // Client modules are executable code. Their hash and source attribution belong in the preview.
    // Patch application only validates/embeds bytes; it never executes the module.
    public static class ManagedModules
    {
        public static bool Parse(PatchOperation op, Dictionary<string, object> raw)
        {
            if (op.Kind != "managedEmbeddedHook") return false;
            op.ModuleData = Json.String(raw, "moduleBase64"); op.ModuleSha256 = Json.String(raw, "moduleSha256");
            op.EntryType = Json.String(raw, "entryType"); op.EntryMethod = Json.String(raw, "entryMethod"); op.HookMode = Json.String(raw, "mode");
            if (op.ModuleData.Length > 350000 || op.EntryType.Length > 512 || op.EntryMethod.Length > 128 || !new[] { "fallback", "after" }.Contains(op.HookMode)) throw new InvalidDataException("Invalid client module hook.");
            Validate(op); return true;
        }
        static byte[] Validate(PatchOperation op)
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(op.ModuleData); } catch (FormatException) { throw new InvalidDataException("Client module is not base64."); }
            if (bytes.Length < 512 || bytes.Length > 256 * 1024 || PatchEngine.Hash(bytes) != op.ModuleSha256) throw new InvalidDataException("Client module hash/size mismatch.");
            using (var stream = new MemoryStream(bytes, false)) using (var module = ModuleDefinition.ReadModule(stream))
            {
                if ((module.Attributes & ModuleAttributes.ILOnly) == 0 || module.EntryPoint != null || module.Types.Any(x => x.Name == "<Module>" && x.Methods.Any())) throw new InvalidDataException("Only managed library modules without an initializer are supported.");
                var type = module.Types.SingleOrDefault(x => x.FullName == op.EntryType && x.IsPublic && !x.HasGenericParameters && !x.IsInterface);
                var methods = type == null ? new List<MethodDefinition>() : type.Methods.Where(x => x.Name == op.EntryMethod).ToList();
                if (methods.Count != 1 || !methods[0].HasBody || !methods[0].IsPublic || !methods[0].IsStatic || methods[0].HasGenericParameters || methods[0].ReturnType.FullName != "System.Object" || methods[0].Parameters.Count != 2 || methods[0].Parameters[0].ParameterType.FullName != "System.Object" || methods[0].Parameters[1].ParameterType.FullName != "System.Object[]") throw new InvalidDataException("Client entry must be one public static object method(object, object[]).");
            }
            return bytes;
        }
        static MethodReference Import(ModuleDefinition module, System.Reflection.MethodBase member) { return module.ImportReference(member); }
        static MethodDefinition Loader(ModuleDefinition module, PatchOperation op, List<MethodDefinition> touched)
        {
            string name = "Module_" + op.ModuleSha256;
            var existing = module.Types.SingleOrDefault(x => x.Namespace == "Patchwork.ClientCode" && x.Name == name);
            if (existing != null) return existing.Methods.Single(x => x.Name == "Invoke");
            var owner = new TypeDefinition("Patchwork.ClientCode", name, TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
            module.Types.Add(owner);
            string resourceName = "Patchwork.ClientCode." + op.ModuleSha256 + ".dll";
            if (module.Resources.Any(x => x.Name == resourceName)) throw new InvalidDataException("Client resource name conflict.");
            module.Resources.Add(new EmbeddedResource(resourceName, ManifestResourceAttributes.Private, Validate(op)));
            var assemblyType = module.ImportReference(typeof(System.Reflection.Assembly));
            var field = new FieldDefinition("Assembly", FieldAttributes.Private | FieldAttributes.Static | FieldAttributes.InitOnly, assemblyType); owner.Fields.Add(field);
            var cctor = new MethodDefinition(".cctor", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void); owner.Methods.Add(cctor);
            cctor.Body.InitLocals = true; cctor.Body.MaxStackSize = 3;
            var stream = new VariableDefinition(module.ImportReference(typeof(Stream))); var memory = new VariableDefinition(module.ImportReference(typeof(MemoryStream)));
            cctor.Body.Variables.Add(stream); cctor.Body.Variables.Add(memory);
            var code = cctor.Body.Instructions;
            code.Add(Instruction.Create(OpCodes.Call, Import(module, typeof(System.Reflection.Assembly).GetMethod("GetExecutingAssembly"))));
            code.Add(Instruction.Create(OpCodes.Ldstr, resourceName));
            code.Add(Instruction.Create(OpCodes.Callvirt, Import(module, typeof(System.Reflection.Assembly).GetMethod("GetManifestResourceStream", new[] { typeof(string) }))));
            code.Add(Instruction.Create(OpCodes.Stloc, stream));
            code.Add(Instruction.Create(OpCodes.Newobj, Import(module, typeof(MemoryStream).GetConstructor(Type.EmptyTypes)))); code.Add(Instruction.Create(OpCodes.Stloc, memory));
            code.Add(Instruction.Create(OpCodes.Ldloc, stream)); code.Add(Instruction.Create(OpCodes.Ldloc, memory));
            code.Add(Instruction.Create(OpCodes.Callvirt, Import(module, typeof(Stream).GetMethod("CopyTo", new[] { typeof(Stream) }))));
            code.Add(Instruction.Create(OpCodes.Ldloc, memory)); code.Add(Instruction.Create(OpCodes.Callvirt, Import(module, typeof(MemoryStream).GetMethod("ToArray"))));
            code.Add(Instruction.Create(OpCodes.Call, Import(module, typeof(System.Reflection.Assembly).GetMethod("Load", new[] { typeof(byte[]) })))); code.Add(Instruction.Create(OpCodes.Stsfld, field));
            foreach (var local in new[] { stream, memory }) { code.Add(Instruction.Create(OpCodes.Ldloc, local)); code.Add(Instruction.Create(OpCodes.Callvirt, Import(module, typeof(IDisposable).GetMethod("Dispose")))); }
            code.Add(Instruction.Create(OpCodes.Ret)); touched.Add(cctor);
            var invoke = new MethodDefinition("Invoke", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Object);
            foreach (var parameter in new[] { new ParameterDefinition("type", ParameterAttributes.None, module.TypeSystem.String), new ParameterDefinition("method", ParameterAttributes.None, module.TypeSystem.String), new ParameterDefinition("receiver", ParameterAttributes.None, module.TypeSystem.Object), new ParameterDefinition("arguments", ParameterAttributes.None, new ArrayType(module.TypeSystem.Object)) }) invoke.Parameters.Add(parameter);
            owner.Methods.Add(invoke); invoke.Body.MaxStackSize = 7; code = invoke.Body.Instructions;
            code.Add(Instruction.Create(OpCodes.Ldsfld, field)); code.Add(Instruction.Create(OpCodes.Ldarg_0)); code.Add(Instruction.Create(OpCodes.Ldc_I4_1));
            code.Add(Instruction.Create(OpCodes.Callvirt, Import(module, typeof(System.Reflection.Assembly).GetMethod("GetType", new[] { typeof(string), typeof(bool) }))));
            code.Add(Instruction.Create(OpCodes.Ldarg_1)); code.Add(Instruction.Create(OpCodes.Callvirt, Import(module, typeof(Type).GetMethod("GetMethod", new[] { typeof(string) }))));
            code.Add(Instruction.Create(OpCodes.Ldnull)); code.Add(Instruction.Create(OpCodes.Ldc_I4_2)); code.Add(Instruction.Create(OpCodes.Newarr, module.TypeSystem.Object));
            code.Add(Instruction.Create(OpCodes.Dup)); code.Add(Instruction.Create(OpCodes.Ldc_I4_0)); code.Add(Instruction.Create(OpCodes.Ldarg_2)); code.Add(Instruction.Create(OpCodes.Stelem_Ref));
            code.Add(Instruction.Create(OpCodes.Dup)); code.Add(Instruction.Create(OpCodes.Ldc_I4_1)); code.Add(Instruction.Create(OpCodes.Ldarg_3)); code.Add(Instruction.Create(OpCodes.Stelem_Ref));
            code.Add(Instruction.Create(OpCodes.Callvirt, Import(module, typeof(System.Reflection.MethodBase).GetMethod("Invoke", new[] { typeof(object), typeof(object[]) })))); code.Add(Instruction.Create(OpCodes.Ret));
            touched.Add(invoke); return invoke;
        }
        static List<Instruction> Call(ModuleDefinition module, MethodDefinition method, PatchOperation op, MethodReference loader)
        {
            var code = new List<Instruction> { Instruction.Create(OpCodes.Ldstr, op.EntryType), Instruction.Create(OpCodes.Ldstr, op.EntryMethod), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldc_I4, method.Parameters.Count), Instruction.Create(OpCodes.Newarr, module.TypeSystem.Object) };
            for (int i = 0; i < method.Parameters.Count; i++) {
                code.Add(Instruction.Create(OpCodes.Dup)); code.Add(Instruction.Create(OpCodes.Ldc_I4, i)); code.Add(Instruction.Create(OpCodes.Ldarg, method.Parameters[i]));
                if (method.Parameters[i].ParameterType.IsValueType) code.Add(Instruction.Create(OpCodes.Box, module.ImportReference(method.Parameters[i].ParameterType)));
                code.Add(Instruction.Create(OpCodes.Stelem_Ref));
            }
            code.Add(Instruction.Create(OpCodes.Call, loader)); return code;
        }
        public static bool Transform(ModuleDefinition module, MethodDefinition method, PatchOperation op, List<MethodDefinition> touched)
        {
            if (op.Kind != "managedEmbeddedHook") return false;
            Validate(op);
            if (method.IsStatic || method.DeclaringType.IsValueType || method.HasGenericParameters || method.DeclaringType.HasGenericParameters || method.Parameters.Count > 8 || method.Parameters.Any(x => x.ParameterType.IsByReference || x.ParameterType.IsPointer || x.ParameterType.ContainsGenericParameter)) throw new InvalidDataException("Unsupported client hook signature.");
            if (op.HookMode == "fallback" && (method.IsConstructor || method.ReturnType.IsValueType || method.ReturnType.IsByReference || method.ReturnType.IsPointer || method.ReturnType.FullName == "System.Void" || method.ReturnType.ContainsGenericParameter)) throw new InvalidDataException("Fallback hooks require a reference result.");
            if (op.HookMode == "after" && method.ReturnType.FullName != "System.Void") throw new InvalidDataException("After hooks require void.");
            var loader = Loader(module, op, touched); var il = method.Body.GetILProcessor();
            if (op.HookMode == "fallback") {
                var original = method.Body.Instructions[0]; var handled = Instruction.Create(OpCodes.Castclass, module.ImportReference(method.ReturnType));
                var code = Call(module, method, op, loader); code.Add(Instruction.Create(OpCodes.Dup)); code.Add(Instruction.Create(OpCodes.Brtrue, handled)); code.Add(Instruction.Create(OpCodes.Pop)); code.Add(Instruction.Create(OpCodes.Br, original)); code.Add(handled); code.Add(Instruction.Create(OpCodes.Ret));
                foreach (var instruction in code) il.InsertBefore(original, instruction);
            } else {
                foreach (var exit in method.Body.Instructions.Where(x => x.OpCode == OpCodes.Ret).ToList()) {
                    exit.OpCode = OpCodes.Nop; var cursor = exit;
                    foreach (var instruction in Call(module, method, op, loader).Concat(new[] { Instruction.Create(OpCodes.Pop), Instruction.Create(OpCodes.Ret) })) { il.InsertAfter(cursor, instruction); cursor = instruction; }
                }
            }
            method.Body.MaxStackSize = Math.Max(method.Body.MaxStackSize, 12); return true;
        }
    }
}
