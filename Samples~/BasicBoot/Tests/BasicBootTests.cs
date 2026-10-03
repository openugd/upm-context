using System;
using System.Collections;
using System.Diagnostics;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace OpenUGD.Samples.BasicBoot.Tests
{
    /// <summary>
    /// Builds the sample's graph with a real <see cref="GameSettings"/> asset in EditMode. The boot awaits a delay, so
    /// the test yields editor frames until the build has finished instead of blocking on it, which would deadlock
    /// under Unity's synchronization context.
    /// </summary>
    [TestFixture]
    [Category("RequiresUnity")]
    public class BasicBootTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

        private Lifetime.Definition _scope;
        private GameSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _scope = Lifetime.Eternal.DefineNested("test");
            _settings = ScriptableObject.CreateInstance<GameSettings>();
            _settings.PlayerName = "Ada";
            _settings.StartingCoins = 7;
        }

        [TearDown]
        public void TearDown()
        {
            _scope.Terminate();
            Object.DestroyImmediate(_settings);
        }

        private static IEnumerator WaitFor(Task task)
        {
            var clock = Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                if (clock.Elapsed > Timeout) Assert.Fail("The build did not finish within " + Timeout + ".");
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator TheWalletStartsWithTheCoinsTheSettingsGive()
        {
            var builder = Context.CreateBuilder(_scope);
            GameInstaller.Register(builder.Services, _settings);

            var build = builder.BuildAsync();
            yield return WaitFor(build);
            var context = build.GetAwaiter().GetResult();

            Assert.AreSame(_settings, context.Resolve<GameSettings>());
            Assert.AreEqual(7, context.Resolve<Wallet>().Coins);
        }

        [UnityTest]
        public IEnumerator EndingTheContextSavesOnceMoreAndLeavesTheSettingsAlone()
        {
            var builder = Context.CreateBuilder(_scope);
            GameInstaller.Register(builder.Services, _settings);

            var build = builder.BuildAsync();
            yield return WaitFor(build);
            var context = build.GetAwaiter().GetResult();
            var store = context.Resolve<ISaveStore>();
            context.Resolve<Wallet>().Earn(5);

            LogAssert.Expect(LogType.Log, "Autosave: final save, 12 coins");
            _scope.Terminate();

            Assert.AreEqual(12, store.Coins, "Autosave is disposed before the store it writes to.");
            Assert.IsTrue(_settings != null, "The context never destroys an object it was handed.");
        }

        [Test]
        public void TheGraphValidatesBeforeAnythingBoots()
        {
            // No settings registered: the build fails at validation, naming every service that needs them.
            var builder = Context.CreateBuilder(_scope);
            builder.Services
                .Add<MemorySaveStore>().As<ISaveStore>()
                .Add<Wallet>()
                .Add<Autosave>();

            var error = Assert.Throws<ContextException>(() => builder.Build());

            StringAssert.Contains("3 problems were found", error.Message);
            StringAssert.Contains("before anything was constructed", error.Message);
        }
    }
}
