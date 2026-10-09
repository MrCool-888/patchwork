using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Patchwork
{
    public static class ManagedTests
    {
        class Fixture
        {
            public string Root;
            public PatchBundle Bundle;
            public List<Dictionary<string, object>> Ops = new List<Dictionary<string, object>>();
            public Fixture(string root)
            {
                Root = root; Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "app.version"), "1");
                using (var module = ModuleDefinition.CreateModule("Fixture" + Guid.NewGuid().ToString("N"), ModuleKind.Dll))
                {
                    var baseType = new TypeDefinition("Fixture", "Base", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object); module.Types.Add(baseType);
                    var derived = new TypeDefinition("Fixture", "Derived", TypeAttributes.Public | TypeAttributes.Class, baseType); module.Types.Add(derived);
                    var captured = new FieldDefinition("Captured", FieldAttributes.Public, module.TypeSystem.Boolean); baseType.Fields.Add(captured);
                    var flag = new FieldDefinition("Flag", FieldAttributes.Public | FieldAttributes.Static, module.TypeSystem.Boolean); baseType.Fields.Add(flag);
                    var ctor = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void); baseType.Methods.Add(ctor);
                    ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes)))); ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    var dctor = new MethodDefinition(".ctor", ctor.Attributes, module.TypeSystem.Void); derived.Methods.Add(dctor);
                    dctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); dctor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, ctor)); dctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    var gate = new MethodDefinition("Gate", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Boolean); baseType.Methods.Add(gate); gate.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0)); gate.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    var choice = new MethodDefinition("Choice", gate.Attributes, module.TypeSystem.Boolean); baseType.Methods.Add(choice); choice.Body.Instructions.Add(Instruction.Create(OpCodes.Call, gate)); choice.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    var restricted = new MethodDefinition("get_Restricted", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.SpecialName, module.TypeSystem.Boolean); baseType.Methods.Add(restricted); restricted.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1)); restricted.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    var accept = new MethodDefinition("Accept", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot, module.TypeSystem.Void); baseType.Methods.Add(accept); accept.Parameters.Add(new ParameterDefinition("paid", ParameterAttributes.None, module.TypeSystem.Boolean)); accept.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); accept.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1)); accept.Body.Instructions.Add(Instruction.Create(OpCodes.Stfld, captured)); accept.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    var side = new MethodDefinition("SideEffect", MethodAttributes.Public | MethodAttributes.Static, module.TypeSystem.Void); baseType.Methods.Add(side); side.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1)); side.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, flag)); side.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    var caller = new MethodDefinition("Caller", side.Attributes, module.TypeSystem.Void); baseType.Methods.Add(caller); caller.Body.Instructions.Add(Instruction.Create(OpCodes.Call, side)); caller.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    var delay = new MethodDefinition("Delay", gate.Attributes, module.ImportReference(typeof(TimeSpan))); baseType.Methods.Add(delay); delay.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_R8, 120.0)); delay.Body.Instructions.Add(Instruction.Create(OpCodes.Call, module.ImportReference(typeof(TimeSpan).GetMethod("FromSeconds", new[] { typeof(double) })))); delay.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    module.Write(Path.Combine(root, "Fixture.dll"));
                    Add("managedBooleanCall", choice, "calledMethod", gate.FullName, "count", 1, "value", true);
                    Add("managedOverrideBoolean", restricted, "type", derived.FullName, "value", false);
                    Add("managedOverrideBooleanArgument", accept, "type", derived.FullName, "value", true);
                    Add("managedSuppressCall", caller, "calledMethod", side.FullName, "count", 1);
                    Add("managedReturn", delay, "returnType", "timeSpanZero");
                }
                Parse();
            }
            void Add(string kind, MethodDefinition method, params object[] fields)
            {
                var op = new Dictionary<string, object> { { "kind", kind }, { "file", "Fixture.dll" }, { "method", method.FullName } };
                for (int i = 0; i < fields.Length; i += 2) op[(string)fields[i]] = fields[i + 1]; Ops.Add(op);
            }
            public void Parse()
            {
                foreach (var op in Ops) op["sha256"] = PatchEngine.Hash(File.ReadAllBytes(Path.Combine(Root, "Fixture.dll")));
                Bundle = PatchBundle.Parse(Json.Pretty(new Dictionary<string, object> {
                    { "schemaVersion", 1 }, { "id", "fixture-pack" }, { "appId", "fixture" }, { "appName", "Fixture" }, { "appVersion", "1" }, { "versionFile", "app.version" }, { "versionSha256", PatchEngine.Hash(Encoding.UTF8.GetBytes("1")) },
                    { "patches", new object[] { new Dictionary<string, object> { { "id", "fixture-patch" }, { "name", "Fixture patch" }, { "description", "Runtime semantics test" }, { "operations", Ops } } } }
                }));
            }
        }
        static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        static void Reject(Action action, string message)
        {
            try { action(); } catch (Exception error) { Assert(error.Message.IndexOf(message, StringComparison.OrdinalIgnoreCase) >= 0, "Unexpected rejection: " + error.Message); return; }
            throw new Exception("Expected rejection: " + message);
        }
        public static void RuntimeRoundTrip(string root)
        {
            var fixture = new Fixture(Path.Combine(root, "managed-runtime")); var engine = new PatchEngine(Path.Combine(fixture.Root, "data"));
            byte[] original = File.ReadAllBytes(Path.Combine(fixture.Root, "Fixture.dll"));
            var plan = engine.Preview(fixture.Bundle, fixture.Root, new[] { "fixture-patch" });
            Assert(File.ReadAllBytes(Path.Combine(fixture.Root, "Fixture.dll")).SequenceEqual(original), "Preview mutated DLL.");
            var assembly = System.Reflection.Assembly.Load(plan.Files[0].AfterBytes);
            var type = assembly.GetType("Fixture.Base"); var derived = assembly.GetType("Fixture.Derived"); var instance = Activator.CreateInstance(derived);
            Assert((bool)type.GetMethod("Choice").Invoke(null, null), "Boolean call was not replaced.");
            Assert(!(bool)type.GetMethod("get_Restricted").Invoke(instance, null), "Inherited virtual getter did not dispatch to override.");
            type.GetMethod("Accept").Invoke(instance, new object[] { false }); Assert((bool)type.GetField("Captured").GetValue(instance), "Argument override did not dispatch.");
            type.GetMethod("Caller").Invoke(null, null); Assert(!(bool)type.GetField("Flag").GetValue(null), "Suppressed call still executed.");
            Assert((TimeSpan)type.GetMethod("Delay").Invoke(null, null) == TimeSpan.Zero, "Zero delay return failed.");
            var journal = engine.Apply(plan); engine.Restore(journal); Assert(File.ReadAllBytes(Path.Combine(fixture.Root, "Fixture.dll")).SequenceEqual(original), "DLL restore changed bytes.");
        }
        public static void Rejections(string root)
        {
            var fixture = new Fixture(Path.Combine(root, "managed-rejections")); var engine = new PatchEngine(Path.Combine(fixture.Root, "data"));
            fixture.Ops[0]["count"] = 2; fixture.Parse(); Reject(() => engine.Preview(fixture.Bundle, fixture.Root, new[] { "fixture-patch" }), "Expected 2 calls");
            fixture.Ops[0]["count"] = 1; fixture.Ops[0]["value"] = "true"; Reject(fixture.Parse, "boolean value");
            fixture.Ops[0]["value"] = true; fixture.Ops[0]["kind"] = "managedExecuteScript"; Reject(fixture.Parse, "Unknown operation");
            fixture.Ops[0]["kind"] = "managedBooleanCall"; fixture.Parse();
            fixture.Bundle.Patches[0].Operations.Add(new PatchOperation { Kind = "managedReturn", File = "Fixture.dll", Sha256 = fixture.Bundle.Patches[0].Operations[0].Sha256, Method = fixture.Bundle.Patches[0].Operations[0].Method, ReturnType = "boolean", Value = false });
            Reject(() => engine.Preview(fixture.Bundle, fixture.Root, new[] { "fixture-patch" }), "Conflicting managed");
            fixture.Parse(); fixture.Bundle.Patches[0].Operations.Add(fixture.Bundle.Patches[0].Operations[0]);
            engine.Preview(fixture.Bundle, fixture.Root, new[] { "fixture-patch" }); // Identical shared gates merge.
            fixture.Bundle.Patches[0].Operations[0].Method = "System.Boolean Missing::Gate()";
            Reject(() => engine.Preview(fixture.Bundle, fixture.Root, new[] { "fixture-patch" }), "signature did not match");
        }
        public static void WorkerRequests(string root)
        {
            var fixture = new Fixture(Path.Combine(root, "managed-worker")); var engine = new PatchEngine(Path.Combine(fixture.Root, "data"));
            var plan = engine.Preview(fixture.Bundle, fixture.Root, new[] { "fixture-patch" });
            string job = Worker.CreateJob(engine, fixture.Bundle, plan, null);
            Assert(Worker.Execute(job, engine.DataRoot) == 0, "Apply worker failed.");
            Assert(engine.History().Single().State == "Applied", "Worker did not journal apply.");
            string restore = Worker.CreateJob(engine, null, null, engine.History().Single());
            Assert(Worker.Execute(restore, engine.DataRoot) == 0 && engine.History().Single().State == "Restored", "Restore worker failed.");
            plan = engine.Preview(fixture.Bundle, fixture.Root, new[] { "fixture-patch" }); job = Worker.CreateJob(engine, fixture.Bundle, plan, null);
            File.AppendAllText(Path.Combine(fixture.Root, "app.version"), "changed");
            Assert(Worker.Execute(job, engine.DataRoot) == 1, "Stale worker request was accepted.");
            Assert(PatchEngine.Hash(File.ReadAllBytes(Path.Combine(fixture.Root, "Fixture.dll"))) == plan.Files[0].BeforeHash, "Rejected worker changed DLL.");
            Reject(() => Worker.Execute(Path.Combine(fixture.Root, "escape.json"), engine.DataRoot), "request location");
        }
    }
}
