using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
    public class UIElement { public Visibility Visibility { get; set; } public bool IsHitTestVisible { get; set; } }
    public class FrameworkElement : UIElement { public double Width { get; set; } public double Height { get; set; } }
}
namespace Patchwork
{
    public enum PresentationMode { All, SecureCore, P2P, Tor }
    public class PresentationItem { public PresentationMode Mode { get; set; } }
    public class PresentationFixture : Microsoft.UI.Xaml.FrameworkElement
    {
        public Microsoft.UI.Xaml.FrameworkElement Button = new Microsoft.UI.Xaml.FrameworkElement { Width = 20, Height = 20, IsHitTestVisible = true };
        public List<PresentationItem> Values = new List<PresentationItem>();
        public bool Paid { get; set; }
        public int Calls;
        public List<PresentationItem> Modes { get { return Values; } }
        public void Hook(int branch) { Calls++; if (branch == 1) return; Calls++; }
        public void Restrict(bool value) { Calls++; }
        public PresentationItem Find(PresentationMode mode) { Calls++; return new PresentationItem { Mode = mode }; }
        public static bool EnsureLinq() { return new[] { new PresentationItem() }.Where(x => x.Mode == PresentationMode.All).ToList().Count == 1; }
    }
    public static class PresentationTests
    {
        static void Assert(bool value) { if (!value) throw new Exception("Presentation verification failed."); }
        static PatchOperation Operation(ModuleDefinition module, string methodName, string kind, string field = "")
        {
            var type = module.Types.Single(x => x.FullName == "Patchwork.PresentationFixture"); var method = type.Methods.Single(x => x.Name == methodName);
            var raw = new Dictionary<string, object> { { "method", method.FullName }, { "field", field }, { "elementGetter", "Patchwork.PresentationMode Patchwork.PresentationItem::get_Mode()" }, { "value", 0 } };
            var op = new PatchOperation { File = "Fixture.dll", Kind = kind, Method = method.FullName }; ManagedPatches.Parse(op, raw); return op;
        }
        public static void Runtime(string root)
        {
            byte[] original = File.ReadAllBytes(Assembly.GetExecutingAssembly().Location); List<PatchOperation> ops;
            using (var module = ModuleDefinition.ReadModule(new MemoryStream(original)))
            {
                var filter = Operation(module, "get_Modes", "managedEnumFilter"); filter.Condition.Add("System.Boolean Patchwork.PresentationFixture::get_Paid()");
                var hide = Operation(module, "Hook", "managedUiVisibility", "Microsoft.UI.Xaml.FrameworkElement Patchwork.PresentationFixture::Button");
                var restriction = Operation(module, "Restrict", "managedUiVisibility"); restriction.UiFromParameter = true; ops = new List<PatchOperation> { filter, hide, restriction };
                var guard = new PatchOperation { File = "Fixture.dll", Kind = "managedEnumGuardNull", Method = module.Types.Single(x => x.FullName == "Patchwork.PresentationFixture").Methods.Single(x => x.Name == "Find").FullName, EnumValues = new List<int> { 1, 2, 3 } }; ops.Add(guard);
            }
            string before, after; byte[] output = ManagedPatches.Transform(original, Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), ops, out before, out after);
            var assembly = Assembly.Load(output); var fixtureType = assembly.GetType("Patchwork.PresentationFixture"); var itemType = assembly.GetType("Patchwork.PresentationItem"); var fixture = Activator.CreateInstance(fixtureType);
            var values = (System.Collections.IList)fixtureType.GetField("Values").GetValue(fixture);
            for (int i = 0; i < 4; i++) { var item = Activator.CreateInstance(itemType); itemType.GetProperty("Mode").SetValue(item, Enum.ToObject(itemType.GetProperty("Mode").PropertyType, i), null); values.Add(item); } values.Add(null);
            var filtered = (System.Collections.IList)fixtureType.GetProperty("Modes").GetValue(fixture, null); Assert(filtered.Count == 1 && Object.ReferenceEquals(filtered[0], values[0])); Assert(values.Count == 5);
            fixtureType.GetProperty("Paid").SetValue(fixture, true, null); Assert(Object.ReferenceEquals(fixtureType.GetProperty("Modes").GetValue(fixture, null), values));
            fixtureType.GetProperty("Paid").SetValue(fixture, false, null); values.Clear(); Assert(((System.Collections.IList)fixtureType.GetProperty("Modes").GetValue(fixture, null)).Count == 0); fixtureType.GetField("Values").SetValue(fixture, null); Assert(fixtureType.GetProperty("Modes").GetValue(fixture, null) == null);
            fixtureType.GetMethod("Hook").Invoke(fixture, new object[] { 1 }); var button = fixtureType.GetField("Button").GetValue(fixture); var uiType = button.GetType(); Assert(uiType.GetProperty("Visibility").GetValue(button, null).ToString() == "Collapsed" && (double)uiType.GetProperty("Width").GetValue(button, null) == 0 && !(bool)uiType.GetProperty("IsHitTestVisible").GetValue(button, null));
            fixtureType.GetField("Button").SetValue(fixture, null); fixtureType.GetMethod("Hook").Invoke(fixture, new object[] { 0 }); Assert((int)fixtureType.GetField("Calls").GetValue(fixture) == 3);
            fixtureType.GetMethod("Restrict").Invoke(fixture, new object[] { true }); Assert(fixtureType.GetProperty("Visibility").GetValue(fixture, null).ToString() == "Collapsed"); fixtureType.GetMethod("Restrict").Invoke(fixture, new object[] { false }); Assert(fixtureType.GetProperty("Visibility").GetValue(fixture, null).ToString() == "Visible" && (bool)fixtureType.GetProperty("IsHitTestVisible").GetValue(fixture, null));
            var enumType = assembly.GetType("Patchwork.PresentationMode"); Assert(fixtureType.GetMethod("Find").Invoke(fixture, new object[] { Enum.ToObject(enumType, 1) }) == null); Assert(fixtureType.GetMethod("Find").Invoke(fixture, new object[] { Enum.ToObject(enumType, 0) }) != null);
        }
        public static void Validation(string root)
        {
            byte[] original = File.ReadAllBytes(Assembly.GetExecutingAssembly().Location); string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            using (var module = ModuleDefinition.ReadModule(new MemoryStream(original)))
            {
                var invalidEnum = Operation(module, "get_Modes", "managedEnumFilter"); invalidEnum.Value = 99; Reject(() => Transform(original, directory, invalidEnum));
                var nonUi = Operation(module, "Hook", "managedUiVisibility", "System.Int32 Patchwork.PresentationFixture::Calls"); Reject(() => Transform(original, directory, nonUi));
                var wrongParameter = Operation(module, "Hook", "managedUiVisibility"); wrongParameter.UiFromParameter = true; Reject(() => Transform(original, directory, wrongParameter));
                var staticHook = Operation(module, "EnsureLinq", "managedUiVisibility"); Reject(() => Transform(original, directory, staticHook));
                var foreign = Operation(module, "Hook", "managedUiVisibility", "System.Int32 Other::Calls"); Reject(() => Transform(original, directory, foreign));
            }
        }
        static void Transform(byte[] bytes, string root, PatchOperation op) { string before, after; ManagedPatches.Transform(bytes, root, new List<PatchOperation> { op }, out before, out after); }
        static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Invalid presentation operation accepted."); }
    }
}
