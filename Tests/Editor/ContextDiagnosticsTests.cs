using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;

namespace OpenUGD.Tests
{
    /// <summary>
    /// One standard for every failure the container reports: what failed, where it was
    /// registered, the chain that led to it, and a fix where there is one.
    /// </summary>
    [TestFixture]
    public class ContextDiagnosticsTests : ContextFixture
    {
        private static string ThisFile([CallerFilePath] string file = null) => file;

        private static int Line([CallerLineNumber] int line = 0) => line;

        private static string Name(Type type) => type.FullName;

        // ===== construction =====

        [Test]
        public void AThrowingFactoryIsWrappedWithItsSiteAndTheChainThatLedToIt()
        {
            var log = new Log();
            var builder = NewBuilder();
            builder.Services.AddInstance(log);
            builder.Services.Add<Disposes>();
            builder.Services.Add<NeedsGauge>();
            builder.Services.Add<IGauge>(c => throw new InvalidOperationException("no gauge")); var line = Line();

            var error = FailToBuild<ContextException>(builder);

            Assert.IsInstanceOf<InvalidOperationException>(error.InnerException,
                "What the factory threw is kept as the InnerException.");
            Assert.AreEqual("no gauge", error.InnerException.Message);
            StringAssert.Contains(
                "The factory registered for '" + Name(typeof(IGauge)) + "' threw InvalidOperationException: no gauge",
                error.Message);
            StringAssert.Contains("registered at " + ThisFile() + ":" + line, error.Message);
            StringAssert.Contains(Name(typeof(NeedsGauge)) + " -> " + Name(typeof(IGauge)), error.Message);
            CollectionAssert.AreEqual(new[] { typeof(NeedsGauge), typeof(IGauge) }, error.Path);
            CollectionAssert.AreEqual(new[] { "dispose" }, log.Entries,
                "A throwing factory still tears down what was constructed before it.");
        }

        [Test]
        public void AConstructorThatThrowsDeepInAChainCarriesTheWholeChain()
        {
            var builder = NewBuilder();
            builder.Services.Add<Top>();
            builder.Services.Add<Middle>();
            builder.Services.Add<Bottom>(); var line = Line();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains(
                "The constructor of '" + Name(typeof(Bottom)) + "' threw InvalidOperationException: bottom failed",
                error.Message);
            StringAssert.Contains("registered at " + ThisFile() + ":" + line, error.Message);
            StringAssert.Contains(
                "while constructing " + Name(typeof(Top)) + " -> " + Name(typeof(Middle)) + " -> " + Name(typeof(Bottom)),
                error.Message);
            CollectionAssert.AreEqual(new[] { typeof(Top), typeof(Middle), typeof(Bottom) }, error.Path);
            Assert.IsInstanceOf<InvalidOperationException>(error.InnerException);
        }

        [Test]
        public void AFailureInsideAFactoryIsReportedOnceForTheServiceThatFailed()
        {
            var builder = NewBuilder();
            builder.Services.Add<IGauge>(c => new Gauge(c.Resolve<Bottom>()));
            builder.Services.Add<Bottom>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.StartsWith("The constructor of '" + Name(typeof(Bottom)) + "' threw", error.Message,
                "The innermost failure is the one reported, not the factory it travelled through.");
            Assert.IsInstanceOf<InvalidOperationException>(error.InnerException,
                "Reported once: the InnerException is the original, not another ContextException.");
            CollectionAssert.AreEqual(new[] { typeof(IGauge), typeof(Bottom) }, error.Path);
        }

        [Test]
        public void AFactoryReturningNullCarriesTheChainToo()
        {
            var builder = NewBuilder();
            builder.Services.Add<NeedsGauge>();
            builder.Services.Add<IGauge>(c => null);

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("returned null", error.Message);
            CollectionAssert.AreEqual(new[] { typeof(NeedsGauge), typeof(IGauge) }, error.Path);
        }

        [Test]
        public void AFactoryThatSwallowsAFailureDoesNotTurnTheRetryIntoACycle()
        {
            var builder = NewBuilder();
            builder.Services.Add<IGauge>(c => {
                try
                {
                    c.Resolve<Bottom>();
                }
                catch (ContextException)
                {
                }

                return new Gauge(null);
            });
            builder.Services.Add<Bottom>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.StartsWith("The constructor of '" + Name(typeof(Bottom)) + "' threw", error.Message);
            StringAssert.DoesNotContain("circular", error.Message);
        }

        [Test]
        public void ACycleThroughAFactoryNamesTheFactoryThatHidIt()
        {
            var builder = NewBuilder();
            builder.Services.Add<LoopA>(c => new LoopA(c.Resolve<LoopB>())); var line = Line();
            builder.Services.Add<LoopB>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("circular dependency", error.Message);
            StringAssert.Contains(
                "the factory registered for '" + Name(typeof(LoopA)) + "' resolves '" + Name(typeof(LoopB)) +
                "' (registered at " + ThisFile() + ":" + line + ")", error.Message);
            StringAssert.DoesNotContain("the constructor of", error.Message,
                "LoopB takes LoopA as a declared parameter, which is not what hid the cycle.");
            CollectionAssert.AreEqual(new[] { typeof(LoopA), typeof(LoopB), typeof(LoopA) }, error.Path);
        }

        [Test]
        public void ACycleThroughAConstructorThatResolvesFromItsContextIsNotBlamedOnAFactory()
        {
            var builder = NewBuilder();
            builder.Services.Add<ResolvesInBody>();
            builder.Services.Add<NeedsResolver>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("circular dependency", error.Message,
                "The cycle is reported as itself, not wrapped in 'the constructor threw'.");
            StringAssert.Contains(
                "the constructor of '" + Name(typeof(ResolvesInBody)) + "' resolves '" + Name(typeof(NeedsResolver)) +
                "' from the Context while it runs", error.Message);
            StringAssert.DoesNotContain("factory", error.Message, "No factory is involved.");
            CollectionAssert.AreEqual(
                new[] { typeof(ResolvesInBody), typeof(NeedsResolver), typeof(ResolvesInBody) }, error.Path);
        }

        [Test]
        public void AnInjectSetterThatThrowsIsReportedWithItsMemberAndSite()
        {
            var builder = NewBuilder();
            builder.Services.Add<IGauge>(c => new Gauge(null));
            builder.Services.Add<RefusesGauge>(); var line = Line();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("The setter of the [Inject] property '" + Name(typeof(RefusesGauge)) +
                                  ".Gauge' threw InvalidOperationException: no thanks", error.Message);
            StringAssert.Contains("registered at " + ThisFile() + ":" + line, error.Message);
            Assert.IsInstanceOf<InvalidOperationException>(error.InnerException);

            var context = Build(NewBuilder("inject"));
            var injected = Assert.Throws<ContextException>(() => context.Inject(new NeedsGaugeMember()));
            StringAssert.Contains(Name(typeof(IGauge)), injected.Message, "Context.Inject still reports a missing one.");
        }

        // ===== boot =====

        [Test]
        public void ABootStepThatThrowsIsNamedWithTheRegistrationOfItsService()
        {
            var builder = NewBuilder();
            builder.Services.Add<FailsToAwake>(); var line = Line();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains(
                "'" + Name(typeof(FailsToAwake)) + ".AwakeAsync' threw InvalidOperationException: awake failed",
                error.Message);
            StringAssert.Contains("Awake phase", error.Message);
            StringAssert.Contains("registered at " + ThisFile() + ":" + line, error.Message);
        }

        [Test]
        public void AnInitializerThatThrowsIsNamedWithTheLineThatAddedIt()
        {
            var builder = NewBuilder();
            var line = Line() + 1; // the line a multi-line call starts on
            builder.Initializers.Add(BootPhase.Initialize,
                (c, ct) => throw new InvalidOperationException("warm-up failed"), "warm-up");

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("'warm-up' threw InvalidOperationException: warm-up failed", error.Message);
            StringAssert.Contains("registered at " + ThisFile() + ":" + line, error.Message);
        }

        [Test]
        public void EveryFailingStepOfAParallelRankIsNamedWithItsSite()
        {
            var builder = NewBuilder();
            builder.Initializers.Mode = StartupMode.Parallel;
            builder.Services.Add<FailsToAwake>(); var first = Line();
            builder.Services.Add<AlsoFailsToAwake>(); var second = Line();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("'" + Name(typeof(FailsToAwake)) + ".AwakeAsync', registered at " + ThisFile() +
                                  ":" + first + ": InvalidOperationException: awake failed", error.Message);
            StringAssert.Contains("'" + Name(typeof(AlsoFailsToAwake)) + ".AwakeAsync', registered at " +
                                  ThisFile() + ":" + second, error.Message);
        }

        // ===== suggestions =====

        [Test]
        public void ASuggestionNeverAsksToAddAsToAContractTheContextSuppliesItself()
        {
            var builder = NewBuilder();
            builder.Services.Add<NeedsLifetimeProvider>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.DoesNotContain(".As<ILifetimeProvider>()", error.Message,
                "Context is supplied automatically; there is no registration to add .As<> to.");
            StringAssert.Contains("Take '" + Name(typeof(Context)) + "' itself instead", error.Message);
        }

        [Test]
        public void ASuggestionNeverAsksToRegisterSomethingAsObject()
        {
            var builder = NewBuilder();
            builder.Services.Add<Dial>();
            builder.Services.Add<NeedsObject>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("Unable to resolve service for type 'System.Object'", error.Message);
            StringAssert.DoesNotContain(".As<Object>()", error.Message);
        }

        [Test]
        public void ASuggestionNamesAGenericContractTheWayCSharpWritesIt()
        {
            var builder = NewBuilder();
            builder.Services.Add<GaugeStore>();
            builder.Services.Add<NeedsStore>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("Add .As<IStore<Gauge>>() to its registration.", error.Message,
                "A suggestion that can be pasted, not the metadata name IStore`1.");
        }

        // ===== Instantiate =====

        [Test]
        public void InstantiateSuggestsTheRegistrationThatImplementsTheMissingContract()
        {
            var builder = NewBuilder();
            builder.Services.Add<Gauge>(c => new Gauge(null));
            var context = Build(builder);

            var error = Assert.Throws<ContextException>(() => context.Instantiate<NeedsGauge>());

            StringAssert.Contains(
                "Unable to resolve service for type '" + Name(typeof(IGauge)) + "' while attempting to activate '" +
                Name(typeof(NeedsGauge)) + "'", error.Message);
            StringAssert.Contains("required by the constructor parameter 'gauge'", error.Message);
            StringAssert.Contains("Add .As<IGauge>() to its registration", error.Message,
                "Instantiate gives the same suggestion the build would.");
        }

        [Test]
        public void InstantiateReportsAThrowingConstructorAsTheBuildDoes()
        {
            var context = Build(NewBuilder());

            var error = Assert.Throws<ContextException>(() => context.Instantiate<Bottom>());

            StringAssert.StartsWith(
                "The constructor of '" + Name(typeof(Bottom)) + "' threw InvalidOperationException: bottom failed",
                error.Message, "What was thrown is on the first line, as in a build failure.");
            Assert.IsInstanceOf<InvalidOperationException>(error.InnerException);
        }

        [Test]
        public void InstantiateRejectsTwoEquallyWideConstructorsItCouldSatisfyAsTheBuildDoes()
        {
            var builder = NewBuilder();
            builder.Services.Add<IGauge>(c => new Gauge(null));
            builder.Services.Add<Dial>().As<IDial>();
            var context = Build(builder);

            var error = Assert.Throws<ContextException>(() => context.Instantiate<EitherWay>());

            StringAssert.Contains("ambiguous", error.Message);
            StringAssert.Contains("(" + Name(typeof(IGauge)) + ")", error.Message);
            StringAssert.Contains("(" + Name(typeof(IDial)) + ")", error.Message);
            StringAssert.Contains("[Inject]", error.Message);
        }

        [Test]
        public void InstantiateStillTakesTheOnlyEquallyWideConstructorItCanSatisfy()
        {
            var builder = NewBuilder();
            builder.Services.Add<Dial>().As<IDial>();
            var context = Build(builder);

            Assert.IsNotNull(context.Instantiate<EitherWay>());
        }

        [Test]
        public void EquallyWideConstructorsKeepTheOrderReflectionListsThemIn()
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var reflected = typeof(ManyConstructors).GetConstructors(all)
                .Where(c => c.GetParameters().Length == 1).ToArray();

            var sorted = Activation.GetMetadata(typeof(ManyConstructors)).Constructors;

            CollectionAssert.AreEqual(reflected, sorted.Take(reflected.Length).ToArray(),
                "Widest first, and ties in reflection order: an unstable sort shuffles them.");
            Assert.AreEqual(0, sorted[sorted.Length - 1].GetParameters().Length);
        }

        // ===== engine objects =====

        [Test]
        public void ATypeDerivingFromUnityEngineObjectIsNeverConstructedByReflection()
        {
            var builder = NewBuilder();
            builder.Services.Add(FakeEngine.Behaviour); var line = Line();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("'Game.FakeBehaviour' cannot be activated because it derives from UnityEngine.Object",
                error.Message);
            StringAssert.Contains("registered at " + ThisFile() + ":" + line, error.Message);
            StringAssert.Contains("AddInstance, or a factory", error.Message);
            StringAssert.DoesNotContain("new T(", error.Message, "A constructor call is exactly what cannot work.");
        }

        [Test]
        public void InstantiateRefusesATypeDerivingFromUnityEngineObject()
        {
            var context = Build(NewBuilder());

            var error = Assert.Throws<ContextException>(() => context.Instantiate(FakeEngine.Behaviour));

            StringAssert.Contains("derives from UnityEngine.Object", error.Message);
            StringAssert.Contains("Context.Inject", error.Message);
        }

        [Test]
        [Category("RequiresUnity")]
        public void ARealMonoBehaviourOrScriptableObjectIsNeverConstructedByReflection()
        {
            var builder = NewBuilder();
            builder.Services.Add<RealBehaviour>();
            builder.Services.Add<RealSettings>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("'" + Name(typeof(RealBehaviour)) + "' cannot be activated", error.Message);
            StringAssert.Contains("'" + Name(typeof(RealSettings)) + "' cannot be activated", error.Message);

            var context = Build(NewBuilder("instantiate"));
            Assert.Throws<ContextException>(() => context.Instantiate<RealSettings>());
        }

        // ===== fixtures =====

        /// Types named as Unity's are, made at run time, so a headless test can have a UnityEngine.Object
        /// subclass without the engine: UnityEngine.Object, UnityEngine.Component, Game.FakeBehaviour.
        private static class FakeEngine
        {
            private static Type _behaviour;

            internal static Type Behaviour => _behaviour ?? (_behaviour =
                Emit("Game.FakeBehaviour", Emit("UnityEngine.Component", Emit("UnityEngine.Object"))));
        }

        public sealed class RealBehaviour : UnityEngine.MonoBehaviour { }

        public sealed class RealSettings : UnityEngine.ScriptableObject { }

        public interface IGauge { }

        public sealed class Gauge : IGauge
        {
            public Gauge(Bottom bottom) { }
        }

        public sealed class RefusesGauge
        {
            [Inject]
            public IGauge Gauge
            {
                set => throw new InvalidOperationException("no thanks");
            }
        }

        public sealed class NeedsGaugeMember
        {
            [Inject] public IGauge Gauge;
        }

        public sealed class NeedsGauge
        {
            public NeedsGauge(IGauge gauge) { }
        }

        public sealed class Disposes : IDisposable
        {
            private readonly Log _log;
            public Disposes(Log log) { _log = log; }
            public void Dispose() { _log.Add("dispose"); }
        }

        public sealed class Top
        {
            public Top(Middle middle) { }
        }

        public sealed class Middle
        {
            public Middle(Bottom bottom) { }
        }

        public sealed class Bottom
        {
            public Bottom() { throw new InvalidOperationException("bottom failed"); }
        }

        public sealed class LoopA
        {
            public LoopA(LoopB b) { }
        }

        public sealed class LoopB
        {
            public LoopB(LoopA a) { }
        }

        public sealed class FailsToAwake : IAwakeService
        {
            public System.Threading.Tasks.Task AwakeAsync(System.Threading.CancellationToken cancellationToken) =>
                throw new InvalidOperationException("awake failed");
        }

        public sealed class AlsoFailsToAwake : IAwakeService
        {
            public System.Threading.Tasks.Task AwakeAsync(System.Threading.CancellationToken cancellationToken) =>
                throw new InvalidOperationException("also failed");
        }

        public interface IDial { }

        public sealed class Dial : IDial { }

        public sealed class EitherWay
        {
            public EitherWay(IGauge gauge) { }
            public EitherWay(IDial dial) { }
        }

        public sealed class NeedsLifetimeProvider
        {
            public NeedsLifetimeProvider(ILifetimeProvider provider) { }
        }

        public sealed class NeedsObject
        {
            public NeedsObject(object anything) { }
        }

        public sealed class Box<T> { }

        public interface IStore<T> { }

        public sealed class GaugeStore : IStore<Gauge> { }

        public sealed class NeedsStore
        {
            public NeedsStore(IStore<Gauge> store) { }
        }

        public sealed class ManyConstructors
        {
            public ManyConstructors() { }
            public ManyConstructors(Box<byte> value) { }
            public ManyConstructors(Box<sbyte> value) { }
            public ManyConstructors(Box<short> value) { }
            public ManyConstructors(Box<ushort> value) { }
            public ManyConstructors(Box<int> value) { }
            public ManyConstructors(Box<uint> value) { }
            public ManyConstructors(Box<long> value) { }
            public ManyConstructors(Box<ulong> value) { }
            public ManyConstructors(Box<float> value) { }
            public ManyConstructors(Box<double> value) { }
            public ManyConstructors(Box<decimal> value) { }
            public ManyConstructors(Box<char> value) { }
            public ManyConstructors(Box<bool> value) { }
            public ManyConstructors(Box<string> value) { }
            public ManyConstructors(Box<object> value) { }
            public ManyConstructors(Box<DateTime> value) { }
            public ManyConstructors(Box<TimeSpan> value) { }
            public ManyConstructors(Box<Guid> value) { }
            public ManyConstructors(Box<Version> value) { }
            public ManyConstructors(Box<Uri> value) { }
        }

        public sealed class ResolvesInBody
        {
            public ResolvesInBody(Context context) { context.Resolve<NeedsResolver>(); }
        }

        public sealed class NeedsResolver
        {
            public NeedsResolver(ResolvesInBody resolver) { }
        }
    }
}
