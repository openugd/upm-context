using System.Collections.Generic;
using NUnit.Framework;

namespace OpenUGD.Samples.Collections.Tests
{
    /// <summary>The sample prints what its README says it prints. Plain C#: no engine call.</summary>
    [TestFixture]
    public class CollectionsSampleTests
    {
        [Test]
        public void TheSamplePrintsWhatItsReadmeShows()
        {
            var lines = new List<string>();

            CollectionsSample.Run(lines.Add);

            Assert.AreEqual(string.Join("\n",
                "release build: 2 tickables",
                "Debug menu:",
                "  [Audio] 1.50 s played",
                "  [Network] 3 packets sent",
                "  cheats: none",
                "development build: 2 tickables",
                "Debug menu:",
                "  [Audio] 1.50 s played",
                "  [Network] 3 packets sent",
                "  cheats: god mode, unlock all levels",
                "one Connection behind IConnection and in both lists: True",
                "level loop: 1 tickable, game loop: 2 tickables"), string.Join("\n", lines));
        }

        [Test]
        public void ListsFollowTheOrderTheModulesWereInstalledIn()
        {
            using (var scope = Lifetime.Eternal.DefineNested("test"))
            {
                var builder = Context.CreateBuilder(scope);
                CheatsModule.Install(builder.Services);
                NetworkModule.Install(builder.Services);
                AudioModule.Install(builder.Services);
                var context = builder.Build();

                var tickables = context.Resolve<IReadOnlyList<ITickable>>();
                Assert.IsInstanceOf<Connection>(tickables[0]);
                Assert.IsInstanceOf<AudioMixer>(tickables[1]);
                Assert.AreEqual("god mode", context.Resolve<IReadOnlyList<ICheat>>()[0].Name);
            }
        }

        [Test]
        public void TheElementTypeItselfIsNotRegistered()
        {
            using (var scope = Lifetime.Eternal.DefineNested("test"))
            {
                var builder = Context.CreateBuilder(scope);
                AudioModule.Install(builder.Services);
                var context = builder.Build();

                ITickable single;
                Assert.IsFalse(context.TryResolve(out single), "Contributing to a list claims no contract.");
            }
        }
    }
}
