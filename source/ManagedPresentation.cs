using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Patchwork
{
    // Exact typed members only. No supplied IL, scripts, XAML or assemblies.
    public static class ManagedPresentation
    {
        public static bool Parse(PatchOperation op, Dictionary<string, object> raw)
        {
            if (op.Kind == "managedEnumGuardNull")
            {
                var values = Json.Array(raw, "values");
                if (values.Count < 1 || values.Count > 16 || values.Any(x => !(x is int))) throw new InvalidDataException("Enum guard needs 1 to 16 integer values.");
                op.EnumValues = values.Cast<int>().Distinct().ToList(); return true;
            }
            if (op.Kind == "managedEnumFilter")
            {
                op.ElementGetter = Json.String(raw, "elementGetter");
                if (op.ElementGetter.Length > 2048 || !raw.TryGetValue("value", out op.Value) || !(op.Value is int)) throw new InvalidDataException("Enum filter needs an exact getter and integer value.");
                if (raw.ContainsKey("condition")) ManagedSelectors.ParseCondition(op, raw);
                return true;
            }
            if (op.Kind != "managedUiVisibility") return false;
            op.UiField = Json.String(raw, "field", "");
            if (op.UiField.Length > 2048) throw new InvalidDataException("UI field signature is too long.");
            object parameter;
            if (raw.TryGetValue("fromParameter", out parameter))
            { if (!(parameter is bool)) throw new InvalidDataException("fromParameter must be boolean."); op.UiFromParameter = (bool)parameter; }
            return true;
        }
        static void AppendAtReturns(MethodDefinition method, Func<List<Instruction>> code)
        {
            var exits = method.Body.Instructions.Where(x => x.OpCode == OpCodes.Ret).ToList();
            if (exits.Count < 1 || exits.Count > 100) throw new InvalidDataException("Expected bounded normal exits.");
            var il = method.Body.GetILProcessor();
            foreach (var exit in exits)
            {
                // Keep branch targets pointing at the start of the new code.
                exit.OpCode = OpCodes.Nop; exit.Operand = null; var cursor = exit;
                foreach (var next in code().Concat(new[] { Instruction.Create(OpCodes.Ret) })) { il.InsertAfter(cursor, next); cursor = next; }
            }
        }
        static void Filter(ModuleDefinition module, MethodDefinition method, PatchOperation op, List<MethodDefinition> touched)
        {
            var list = method.ReturnType as GenericInstanceType;
            if (method.IsStatic || method.Parameters.Count != 0 || method.HasGenericParameters || list == null || !new[] { "System.Collections.Generic.List`1", "System.Collections.Generic.IEnumerable`1", "System.Collections.Generic.IReadOnlyList`1" }.Contains(list.ElementType.FullName)) throw new InvalidDataException("Enum filters require an instance collection getter.");
            var item = list.GenericArguments[0]; var getter = ManagedSelectors.Method(module, op.ElementGetter);
            if (item.IsValueType || !getter.IsPublic || !getter.IsGetter || getter.IsStatic || getter.Parameters.Count != 0 || getter.HasGenericParameters || !ManagedSelectors.Inherits(item, getter.DeclaringType) || !getter.ReturnType.Resolve().IsEnum) throw new InvalidDataException("Enum filter getter does not match its item.");
            var enumeration = getter.ReturnType.Resolve();
            if (enumeration.Fields.Single(x => x.Name == "value__").FieldType.FullName != "System.Int32" || !enumeration.Fields.Any(x => x.HasConstant && Convert.ToInt32(x.Constant) == (int)op.Value)) throw new InvalidDataException("Enum filter value is not declared.");
            string name = "PatchworkFilter_" + method.MetadataToken.ToInt32().ToString("x8");
            if (method.DeclaringType.Methods.Any(x => x.Name == name)) throw new InvalidDataException("Enum filter helper already exists.");
            var predicate = new MethodDefinition(name, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, module.TypeSystem.Boolean);
            predicate.Parameters.Add(new ParameterDefinition("item", ParameterAttributes.None, module.ImportReference(item)));
            var nullItem = Instruction.Create(OpCodes.Ldc_I4_0);
            foreach (var instruction in new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Brfalse, nullItem), Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Callvirt, module.ImportReference(getter)), Instruction.Create(OpCodes.Ldc_I4, (int)op.Value), Instruction.Create(OpCodes.Ceq), Instruction.Create(OpCodes.Ret), nullItem, Instruction.Create(OpCodes.Ret) }) predicate.Body.Instructions.Add(instruction);
            method.DeclaringType.Methods.Add(predicate); touched.Add(predicate);
            var functionType = module.GetTypeReferences().FirstOrDefault(x => x.FullName == "System.Func`2");
            if (functionType == null) throw new InvalidDataException("Target Func reference is missing.");
            var function = new GenericInstanceType(functionType); function.GenericArguments.Add(module.ImportReference(item)); function.GenericArguments.Add(module.TypeSystem.Boolean);
            var constructor = new MethodReference(".ctor", module.TypeSystem.Void, function) { HasThis = true };
            constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.Object)); constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.IntPtr));
            AppendAtReturns(method, () => {
                var end = Instruction.Create(OpCodes.Nop);
                var code = new List<Instruction> { Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Brfalse, end) };
                if (op.Condition.Count != 0) { code.AddRange(ManagedSelectors.Condition(module, method, op)); code.Add(Instruction.Create(OpCodes.Brtrue, end)); }
                code.AddRange(new[] { Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Ldftn, predicate), Instruction.Create(OpCodes.Newobj, constructor), Instruction.Create(OpCodes.Call, ManagedSelectors.Linq(module, "Where", 2, item)) });
                if (list.ElementType.FullName != "System.Collections.Generic.IEnumerable`1") code.Add(Instruction.Create(OpCodes.Call, ManagedSelectors.Linq(module, "ToList", 1, item)));
                code.Add(end); return code;
            });
        }
        static MethodReference Setter(ModuleDefinition module, TypeReference receiver, string ownerName, string methodName, string parameterName)
        {
            TypeDefinition owner = receiver.Resolve();
            for (int depth = 0; owner != null && depth < 40; depth++, owner = owner.BaseType == null ? null : owner.BaseType.Resolve())
                if (owner.FullName == ownerName)
                {
                    var setter = owner.Methods.SingleOrDefault(x => x.Name == methodName && x.IsPublic && !x.IsStatic && x.Parameters.Count == 1 && x.Parameters[0].ParameterType.FullName == parameterName && x.ReturnType.FullName == "System.Void");
                    if (setter != null) return module.ImportReference(setter);
                }
            throw new InvalidDataException("UI receiver does not expose the expected WinUI setter: " + methodName);
        }
        static void Visibility(ModuleDefinition module, MethodDefinition method, PatchOperation op)
        {
            if (method.IsStatic || method.HasGenericParameters || method.ReturnType.FullName != "System.Void" || method.Parameters.Count > 8 || op.UiFromParameter && (method.Parameters.Count != 1 || method.Parameters[0].ParameterType.FullName != "System.Boolean")) throw new InvalidDataException("UI visibility requires an instance void hook or a boolean setter.");
            FieldDefinition field = null; TypeReference receiver = method.DeclaringType;
            if (!String.IsNullOrEmpty(op.UiField))
            {
                field = method.DeclaringType.Fields.SingleOrDefault(x => x.FullName == op.UiField && !x.IsStatic);
                if (field == null) throw new InvalidDataException("UI hook field must belong to the hook type."); receiver = field.FieldType;
            }
            var visibility = Setter(module, receiver, "Microsoft.UI.Xaml.UIElement", "set_Visibility", "Microsoft.UI.Xaml.Visibility");
            var hitTest = Setter(module, receiver, "Microsoft.UI.Xaml.UIElement", "set_IsHitTestVisible", "System.Boolean");
            var width = op.UiFromParameter ? null : Setter(module, receiver, "Microsoft.UI.Xaml.FrameworkElement", "set_Width", "System.Double");
            var height = op.UiFromParameter ? null : Setter(module, receiver, "Microsoft.UI.Xaml.FrameworkElement", "set_Height", "System.Double");
            var valueType = visibility.Parameters[0].ParameterType.Resolve();
            if (!valueType.IsEnum || Convert.ToInt32(valueType.Fields.Single(x => x.Name == "Visible").Constant) != 0 || Convert.ToInt32(valueType.Fields.Single(x => x.Name == "Collapsed").Constant) != 1) throw new InvalidDataException("Unexpected WinUI visibility values.");
            AppendAtReturns(method, () => {
                var code = new List<Instruction>(); var end = Instruction.Create(OpCodes.Nop);
                Action load = () => { code.Add(Instruction.Create(OpCodes.Ldarg_0)); if (field != null) code.Add(Instruction.Create(OpCodes.Ldfld, field)); };
                if (field != null) { load(); code.Add(Instruction.Create(OpCodes.Brfalse, end)); }
                load(); code.Add(Instruction.Create(op.UiFromParameter ? OpCodes.Ldarg_1 : OpCodes.Ldc_I4_1)); code.Add(Instruction.Create(OpCodes.Callvirt, visibility));
                load(); if (op.UiFromParameter) { code.Add(Instruction.Create(OpCodes.Ldarg_1)); code.Add(Instruction.Create(OpCodes.Ldc_I4_0)); code.Add(Instruction.Create(OpCodes.Ceq)); } else code.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                code.Add(Instruction.Create(OpCodes.Callvirt, hitTest));
                if (!op.UiFromParameter) foreach (var setter in new[] { width, height }) { load(); code.Add(Instruction.Create(OpCodes.Ldc_R8, 0.0)); code.Add(Instruction.Create(OpCodes.Callvirt, setter)); }
                code.Add(end); return code;
            });
        }
        public static bool Transform(ModuleDefinition module, MethodDefinition method, PatchOperation op, List<MethodDefinition> touched)
        {
            if (op.Kind == "managedEnumFilter") Filter(module, method, op, touched);
            else if (op.Kind == "managedUiVisibility") Visibility(module, method, op);
            else if (op.Kind == "managedEnumGuardNull")
            {
                if (method.IsStatic || method.HasGenericParameters || method.Parameters.Count != 1 || method.ReturnType.IsValueType || method.ReturnType.IsGenericParameter || method.ReturnType is TypeSpecification || method.ReturnType.FullName == "System.Void" || !method.Parameters[0].ParameterType.Resolve().IsEnum) throw new InvalidDataException("Enum guard requires a single enum argument and reference result.");
                var enumeration = method.Parameters[0].ParameterType.Resolve();
                if (enumeration.Fields.Single(x => x.Name == "value__").FieldType.FullName != "System.Int32" || op.EnumValues.Any(value => !enumeration.Fields.Any(x => x.HasConstant && Convert.ToInt32(x.Constant) == value))) throw new InvalidDataException("Enum guard value is not declared.");
                var first = method.Body.Instructions[0]; var empty = Instruction.Create(OpCodes.Ldnull); var code = new List<Instruction>();
                foreach (int value in op.EnumValues) { code.Add(Instruction.Create(OpCodes.Ldarg_1)); code.Add(Instruction.Create(OpCodes.Ldc_I4, value)); code.Add(Instruction.Create(OpCodes.Beq, empty)); }
                code.Add(Instruction.Create(OpCodes.Br, first)); code.Add(empty); code.Add(Instruction.Create(OpCodes.Ret));
                foreach (var instruction in code) method.Body.GetILProcessor().InsertBefore(first, instruction);
            }
            else return false;
            return true;
        }
    }
}
