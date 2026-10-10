using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Patchwork
{
    // Generates Color/Brush resources only. Never loads patch-supplied XAML, types or URIs.
    public static class ManagedThemes
    {
        public static bool Parse(PatchOperation op, Dictionary<string, object> raw)
        {
            if (op.Kind != "managedThemeResources") return false;
            var resources = Json.Array(raw, "resources"); if (resources.Count < 1 || resources.Count > 100) throw new InvalidDataException("Theme operations need 1 to 100 resources.");
            var keys = new HashSet<string>();
            foreach (var entry in resources)
            {
                var item = Json.Object(entry); var resource = new ThemeResource { Theme = Json.String(item, "theme"), Key = Json.String(item, "key"), Type = Json.String(item, "type"), Value = Json.String(item, "value") };
                if (resource.Theme != "Light" && resource.Theme != "Dark" || resource.Type != "color" && resource.Type != "brush" || !Regex.IsMatch(resource.Key, "^[A-Za-z][A-Za-z0-9_]{0,79}\\z") || !keys.Add(resource.Theme + "." + resource.Key)) throw new InvalidDataException("Invalid theme resource.");
                if (resource.Value.StartsWith("$", StringComparison.Ordinal)) PatchBundle.ValidateId(resource.Value.Substring(1)); else resource.Value = ThemeOptions.Color(resource.Value);
                op.Resources.Add(resource);
            }
            return true;
        }
        public static string Xaml(PatchOperation op)
        {
            var text = new StringBuilder("<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><ResourceDictionary.ThemeDictionaries>");
            foreach (var group in op.Resources.GroupBy(x => x.Theme))
            {
                text.Append("<ResourceDictionary x:Key=\"").Append(group.Key).Append("\">");
                foreach (var resource in group)
                {
                    string color = ThemeOptions.Color(resource.Value);
                    if (resource.Type == "color") text.Append("<Color x:Key=\"").Append(resource.Key).Append("\">").Append(color).Append("</Color>");
                    else text.Append("<SolidColorBrush x:Key=\"").Append(resource.Key).Append("\" Color=\"").Append(color).Append("\"/>");
                }
                text.Append("</ResourceDictionary>");
            }
            return text.Append("</ResourceDictionary.ThemeDictionaries></ResourceDictionary>").ToString();
        }
        public static bool Transform(ModuleDefinition module, MethodDefinition method, PatchOperation op)
        {
            if (op.Kind != "managedThemeResources") return false;
            if (method.IsStatic || method.Parameters.Count != 0 || method.ReturnType.FullName != "System.Void") throw new InvalidDataException("Theme hook needs a parameterless instance void method.");
            var calls = method.Body.Instructions.Select(x => x.Operand).OfType<MethodReference>().ToList();
            Func<string, string, MethodReference> member = (owner, name) => { var matches = calls.Where(x => x.DeclaringType.FullName == owner && x.Name == name).GroupBy(x => x.FullName).Select(x => x.First()).ToList(); if (matches.Count != 1) throw new InvalidDataException("Theme hook must already merge WinUI dictionaries: " + name); return matches[0]; };
            var current = member("Microsoft.UI.Xaml.Application", "get_Current"); var resources = member("Microsoft.UI.Xaml.Application", "get_Resources"); var merged = member("Microsoft.UI.Xaml.ResourceDictionary", "get_MergedDictionaries");
            var add = calls.FirstOrDefault(x => x.Name == "Add" && x.DeclaringType.FullName == "System.Collections.Generic.ICollection`1<Microsoft.UI.Xaml.ResourceDictionary>");
            if (add == null) throw new InvalidDataException("Theme merge Add reference was not found.");
            var dictionary = resources.ReturnType; var reader = dictionary.Resolve().Module.GetType("Microsoft.UI.Xaml.Markup.XamlReader");
            if (reader == null) throw new InvalidDataException("WinUI XamlReader was not found.");
            var load = reader.Methods.Single(x => x.IsStatic && x.Name == "Load" && x.Parameters.Count == 1 && x.Parameters[0].ParameterType.FullName == "System.String");
            string xaml = Xaml(op); var il = method.Body.GetILProcessor();
            foreach (var exit in method.Body.Instructions.Where(x => x.OpCode == OpCodes.Ret).ToList())
            {
                exit.OpCode = OpCodes.Nop; var cursor = exit;
                var code = new[] { Instruction.Create(OpCodes.Call, current), Instruction.Create(OpCodes.Callvirt, resources), Instruction.Create(OpCodes.Callvirt, merged), Instruction.Create(OpCodes.Ldstr, xaml), Instruction.Create(OpCodes.Call, module.ImportReference(load)), Instruction.Create(OpCodes.Castclass, dictionary), Instruction.Create(OpCodes.Callvirt, add), Instruction.Create(OpCodes.Ret) };
                foreach (var instruction in code) { il.InsertAfter(cursor, instruction); cursor = instruction; }
            }
            return true;
        }
    }
}
