using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Patchwork
{
    // A bounded filter/factory operation: member signatures only, no supplied IL or payloads.
    public static class ManagedCollections
    {
        static List<string> Chain(Dictionary<string, object> raw, string key)
        {
            var values = Json.Array(raw, key);
            if (values.Count < 1 || values.Count > 8 || values.Any(x => !(x is string) || ((string)x).Length > 2048)) throw new InvalidDataException("Member chains need 1 to 8 signatures.");
            return values.Cast<string>().ToList();
        }
        public static bool Parse(PatchOperation op, Dictionary<string, object> raw)
        {
            if (op.Kind != "managedEnumerableFactory") return false;
            op.Condition = Chain(raw, "condition"); op.SourceChain = Chain(raw, "source"); op.FilterChain = Chain(raw, "filter");
            op.FactoryChain = Chain(raw, "factory"); op.ArgumentChain = Chain(raw, "argument");
            op.ElementGetter = Json.String(raw, "elementGetter"); op.FactoryMethod = Json.String(raw, "factoryMethod");
            if (op.ElementGetter.Length > 2048 || op.FactoryMethod.Length > 2048) throw new InvalidDataException("Member signature is too long.");
            return true;
        }
        static TypeReference Substitute(TypeReference type, GenericInstanceType instance)
        {
            var parameter = type as GenericParameter;
            if (parameter != null && parameter.Type == GenericParameterType.Type && instance != null) return instance.GenericArguments[parameter.Position];
            var generic = type as GenericInstanceType;
            if (generic == null) return type;
            var result = new GenericInstanceType(generic.ElementType);
            foreach (var argument in generic.GenericArguments) result.GenericArguments.Add(Substitute(argument, instance));
            return result;
        }
        static TypeReference Receiver(TypeReference current, TypeDefinition owner)
        {
            for (int i = 0; current != null && i < 40; i++)
            {
                var definition = current.Resolve();
                if (definition.FullName == owner.FullName) return current;
                if (definition.Interfaces.Any(x => x.InterfaceType.FullName == owner.FullName)) return owner;
                current = Substitute(definition.BaseType, current as GenericInstanceType);
            }
            throw new InvalidDataException("Member does not match its receiver.");
        }
        static List<Instruction> ChainCode(ModuleDefinition module, MethodDefinition host, List<string> members, bool source, bool condition, List<MethodDefinition> touched, out TypeReference result)
        {
            var code = new List<Instruction> { Instruction.Create(OpCodes.Ldarg_0) }; result = host.DeclaringType;
            foreach (string signature in members)
            {
                if (result.IsValueType) throw new InvalidDataException("Collection chains require reference receivers.");
                var owner = ManagedSelectors.Type(module, ManagedSelectors.Owner(signature)); var receiver = Receiver(result, owner);
                if (signature.IndexOf('(') >= 0)
                {
                    var getter = ManagedSelectors.Method(module, signature);
                    if (getter.IsStatic || getter.Parameters.Count != 0 || getter.HasGenericParameters || !getter.IsPublic && getter.DeclaringType != host.DeclaringType && !getter.IsFamily || !getter.IsGetter && !(source && signature == members.Last())) throw new InvalidDataException("Only accessible getters and a parameterless source method are allowed.");
                    code.Add(Instruction.Create(OpCodes.Callvirt, ManagedSelectors.Bind(module, getter, receiver))); result = Substitute(getter.ReturnType, receiver as GenericInstanceType);
                }
                else
                {
                    var field = owner.Fields.SingleOrDefault(x => x.FullName == signature);
                    if (field == null || field.IsStatic) throw new InvalidDataException("Missing collection field.");
                    // Reading an inherited private boolean requires an accessor on its own type.
                    if (field.IsPrivate && owner != host.DeclaringType)
                    {
                        if (!condition || members.Count != 1 || field.FieldType.FullName != "System.Boolean" || owner.Module != module) throw new InvalidDataException("Inaccessible collection field.");
                        string name = "PatchworkRead_" + field.MetadataToken.ToInt32().ToString("x8");
                        var accessor = owner.Methods.SingleOrDefault(x => x.Name == name);
                        if (accessor == null)
                        {
                            accessor = new MethodDefinition(name, MethodAttributes.Assembly | MethodAttributes.HideBySig, module.TypeSystem.Boolean);
                            accessor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); accessor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldfld, field)); accessor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); owner.Methods.Add(accessor); touched.Add(accessor);
                        }
                        code.Add(Instruction.Create(OpCodes.Call, ManagedSelectors.Bind(module, accessor, receiver)));
                    }
                    else
                    {
                        if (!field.IsPublic && owner != host.DeclaringType && !field.IsFamily && !field.IsFamilyOrAssembly) throw new InvalidDataException("Inaccessible collection field.");
                        code.Add(Instruction.Create(OpCodes.Ldfld, new FieldReference(field.Name, module.ImportReference(field.FieldType), module.ImportReference(receiver))));
                    }
                    result = Substitute(field.FieldType, receiver as GenericInstanceType);
                }
            }
            return code;
        }
        static MethodReference DelegateConstructor(ModuleDefinition module, TypeReference input, TypeReference output)
        {
            var type = new GenericInstanceType(module.GetTypeReferences().First(x => x.FullName == "System.Func`2")); type.GenericArguments.Add(module.ImportReference(input)); type.GenericArguments.Add(module.ImportReference(output));
            var ctor = new MethodReference(".ctor", module.TypeSystem.Void, type) { HasThis = true }; ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object)); ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr)); return ctor;
        }
        public static bool Transform(ModuleDefinition module, MethodDefinition method, PatchOperation op, List<MethodDefinition> touched)
        {
            if (op.Kind != "managedEnumerableFactory") return false;
            var destination = method.ReturnType as GenericInstanceType;
            if (method.IsStatic || method.Parameters.Count != 0 || destination == null || destination.ElementType.FullName != "System.Collections.Generic.IEnumerable`1") throw new InvalidDataException("Factory replacement needs a parameterless instance enumerable method.");
            TypeReference conditionType, sourceType, filterType, factoryType, argumentType;
            var condition = ChainCode(module, method, op.Condition, false, true, touched, out conditionType);
            var source = ChainCode(module, method, op.SourceChain, true, false, touched, out sourceType);
            var filter = ChainCode(module, method, op.FilterChain, false, false, touched, out filterType);
            var factory = ChainCode(module, method, op.FactoryChain, false, false, touched, out factoryType);
            var argument = ChainCode(module, method, op.ArgumentChain, false, false, touched, out argumentType);
            var enumerable = sourceType as GenericInstanceType;
            if (conditionType.FullName != "System.Boolean" || filterType.FullName != "System.String" || argumentType.FullName != "System.Boolean" || enumerable == null || enumerable.ElementType.FullName != "System.Collections.Generic.IEnumerable`1") throw new InvalidDataException("Factory chain has the wrong type.");
            var input = enumerable.GenericArguments[0]; var output = destination.GenericArguments[0];
            var getter = ManagedSelectors.Method(module, op.ElementGetter); var factoryMethod = ManagedSelectors.Method(module, op.FactoryMethod);
            if (!getter.IsGetter || getter.IsStatic || !getter.IsPublic || getter.Parameters.Count != 0 || getter.ReturnType.FullName != "System.String" || !ManagedSelectors.Inherits(input, getter.DeclaringType) || factoryMethod.IsStatic || !factoryMethod.IsPublic || factoryMethod.HasGenericParameters || factoryMethod.Parameters.Count != 2 || factoryMethod.Parameters[0].ParameterType.FullName != input.FullName || factoryMethod.Parameters[1].ParameterType.FullName != "System.Boolean" || !ManagedSelectors.Inherits(factoryType, factoryMethod.DeclaringType) || !ManagedSelectors.Inherits(factoryMethod.ReturnType, output)) throw new InvalidDataException("Incompatible element getter or factory signature.");
            string suffix = method.MetadataToken.ToInt32().ToString("x8");
            var predicate = new MethodDefinition("PatchworkFilter_" + suffix, MethodAttributes.Private | MethodAttributes.HideBySig, module.TypeSystem.Boolean);
            predicate.Parameters.Add(new ParameterDefinition("item", ParameterAttributes.None, module.ImportReference(input)));
            predicate.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1)); predicate.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, module.ImportReference(getter)));
            foreach (var instruction in filter) predicate.Body.Instructions.Add(instruction);
            predicate.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_5));
            // Keep the target runtime's String/StringComparison identities, not the Framework host's.
            var comparison = module.GetTypeReferences().FirstOrDefault(x => x.FullName == "System.StringComparison");
            if (comparison == null) throw new InvalidDataException("StringComparison reference was not found.");
            var equals = new MethodReference("Equals", module.TypeSystem.Boolean, module.TypeSystem.String); equals.Parameters.Add(new ParameterDefinition(module.TypeSystem.String)); equals.Parameters.Add(new ParameterDefinition(module.TypeSystem.String)); equals.Parameters.Add(new ParameterDefinition(module.ImportReference(comparison)));
            predicate.Body.Instructions.Add(Instruction.Create(OpCodes.Call, equals)); predicate.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            var projector = new MethodDefinition("PatchworkFactory_" + suffix, MethodAttributes.Private | MethodAttributes.HideBySig, module.ImportReference(output)); projector.Parameters.Add(new ParameterDefinition("item", ParameterAttributes.None, module.ImportReference(input)));
            foreach (var instruction in factory) projector.Body.Instructions.Add(instruction); projector.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1)); foreach (var instruction in argument) projector.Body.Instructions.Add(instruction);
            projector.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, module.ImportReference(factoryMethod))); projector.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            if (method.DeclaringType.Methods.Any(x => x.Name == predicate.Name || x.Name == projector.Name)) throw new InvalidDataException("Factory helpers already exist.");
            method.DeclaringType.Methods.Add(predicate); method.DeclaringType.Methods.Add(projector); touched.Add(predicate); touched.Add(projector);
            var first = method.Body.Instructions[0]; var code = condition; code.Add(Instruction.Create(OpCodes.Brtrue, first)); code.AddRange(source);
            code.Add(Instruction.Create(OpCodes.Ldarg_0)); code.Add(Instruction.Create(OpCodes.Ldftn, predicate)); code.Add(Instruction.Create(OpCodes.Newobj, DelegateConstructor(module, input, module.TypeSystem.Boolean))); code.Add(Instruction.Create(OpCodes.Call, ManagedSelectors.Linq(module, "Where", 2, input)));
            code.Add(Instruction.Create(OpCodes.Ldarg_0)); code.Add(Instruction.Create(OpCodes.Ldftn, projector)); code.Add(Instruction.Create(OpCodes.Newobj, DelegateConstructor(module, input, output))); code.Add(Instruction.Create(OpCodes.Call, ManagedSelectors.Linq(module, "Select", 2, input, output))); code.Add(Instruction.Create(OpCodes.Ret));
            foreach (var instruction in code) method.Body.GetILProcessor().InsertBefore(first, instruction);
            return true;
        }
    }
}
