using System; using System.Reflection;
class R {
  static void Main(string[] a) {
    var asm = Assembly.Load(System.IO.File.ReadAllBytes(a[0]));
    var t = asm.GetType("AceTool");
    foreach (var nm in new string[]{"H_MAIN","H_KAMI","H_REQ","H_TOK"}) {
      var f = t.GetField(nm, BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public);
      Console.WriteLine(nm + " = " + (f == null ? "(const 已内联)" : f.GetValue(null).ToString()));
    }
    var m = t.GetMethod("ILIntact", BindingFlags.Static|BindingFlags.NonPublic);
    Console.WriteLine("ILIntact = " + m.Invoke(null, null));
  }
}
