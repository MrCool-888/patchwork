using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
namespace Patchwork
{
    public class ModuleFixture { public string Result(bool replace) { return "original"; } public int Calls; public void Hook() { Calls++; } }
    public static class ModuleTests
    {
        static byte[] Client()
        {
            using (var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("TestClient_" + Guid.NewGuid().ToString("N"), new Version(1, 0)), "TestClient", ModuleKind.Dll)) {
                var module = assembly.MainModule; var type = new TypeDefinition("Tests", "Client", Mono.Cecil.TypeAttributes.Public, module.TypeSystem.Object); module.Types.Add(type);
                var entry = new MethodDefinition("Run", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Object);
                entry.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object)); entry.Parameters.Add(new ParameterDefinition(new ArrayType(module.TypeSystem.Object))); type.Methods.Add(entry);
                var no = Instruction.Create(OpCodes.Ldnull); var code = entry.Body.Instructions;
                code.Add(Instruction.Create(OpCodes.Ldarg_1)); code.Add(Instruction.Create(OpCodes.Ldlen)); code.Add(Instruction.Create(OpCodes.Brfalse, no));
                code.Add(Instruction.Create(OpCodes.Ldarg_1)); code.Add(Instruction.Create(OpCodes.Ldc_I4_0)); code.Add(Instruction.Create(OpCodes.Ldelem_Ref)); code.Add(Instruction.Create(OpCodes.Unbox_Any, module.TypeSystem.Boolean)); code.Add(Instruction.Create(OpCodes.Brfalse, no));
                code.Add(Instruction.Create(OpCodes.Ldstr, "client")); code.Add(Instruction.Create(OpCodes.Ret)); code.Add(no); code.Add(Instruction.Create(OpCodes.Ret));
                using (var output = new MemoryStream()) { assembly.Write(output); return output.ToArray(); }
            }
        }
        static PatchOperation Operation(byte[] client, string method, string mode)
        {
            return new PatchOperation { Kind = "managedEmbeddedHook", Method = method, ModuleData = Convert.ToBase64String(client), ModuleSha256 = PatchEngine.Hash(client), EntryType = "Tests.Client", EntryMethod = "Run", HookMode = mode };
        }
        public static void Runtime(string root)
        {
            byte[] bytes = File.ReadAllBytes(Assembly.GetExecutingAssembly().Location), client = Client(); string before, after;
            var result = Operation(client, "System.String Patchwork.ModuleFixture::Result(System.Boolean)", "fallback");
            var hook = Operation(client, "System.Void Patchwork.ModuleFixture::Hook()", "after");
            byte[] changed = ManagedPatches.Transform(bytes, Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), new List<PatchOperation> { result, hook }, out before, out after);
            var type = Assembly.Load(changed).GetType("Patchwork.ModuleFixture"); var instance = Activator.CreateInstance(type);
            if ((string)type.GetMethod("Result").Invoke(instance, new object[] { false }) != "original" || (string)type.GetMethod("Result").Invoke(instance, new object[] { true }) != "client") throw new Exception("Client fallback did not preserve original behavior.");
            type.GetMethod("Hook").Invoke(instance, null); if ((int)type.GetField("Calls").GetValue(instance) != 1) throw new Exception("After hook replaced original body.");
            using (var stream = new MemoryStream(changed)) using (var module = ModuleDefinition.ReadModule(stream)) if (module.Resources.Count(x => x.Name.StartsWith("Patchwork.ClientCode.")) != 1) throw new Exception("Client resource duplicated.");
        }
        static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid module accepted."); }
        public static void Validation(string root)
        {
            byte[] bytes = File.ReadAllBytes(Assembly.GetExecutingAssembly().Location), client = Client(); string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            Action<PatchOperation> transform = operation => { string before, after; ManagedPatches.Transform(bytes, directory, new List<PatchOperation> { operation }, out before, out after); };
            var op = Operation(client, "System.String Patchwork.ModuleFixture::Result(System.Boolean)", "fallback"); op.ModuleSha256 = new string('0',64); Reject(() => transform(op));
            op = Operation(client, "System.String Patchwork.ModuleFixture::Result(System.Boolean)", "after"); Reject(() => transform(op));
            op = Operation(client, "System.Void Patchwork.ModuleFixture::Hook()", "fallback"); Reject(() => transform(op));
            op = Operation(client, "System.Void Patchwork.ModuleFixture::Hook()", "after"); op.EntryMethod = "Missing"; Reject(() => transform(op));
        }
    }
}
