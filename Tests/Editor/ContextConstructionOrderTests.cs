using System; using System.Collections.Generic; using OpenUGD; using NUnit.Framework;
namespace OpenUGD.Tests {
  public class ContextConstructionOrderTests {
    public static List<string> Built = new List<string>();
    public class Innocent { public Innocent() { Built.Add("Innocent"); } }
    public class CA { public CA(CB b) { Built.Add("CA"); } }
    public class CB { public CB(CA a) { Built.Add("CB"); } }
    [Test] public void NothingIsConstructedWhenTheGraphHasACycle() {
      Built.Clear();
      var d = Lifetime.Eternal.DefineNested("probe");
      var b = Context.CreateBuilder(d.Lifetime);
      b.Services.Add<Innocent>(); b.Services.Add<CA>(); b.Services.Add<CB>();
      Assert.Catch<Exception>(() => b.BuildAsync().GetAwaiter().GetResult());
      d.Terminate();
      Assert.IsEmpty(Built, "C3a: validation must reject the graph BEFORE constructing anything; built: " + string.Join(",", Built));
    }
  }
}
