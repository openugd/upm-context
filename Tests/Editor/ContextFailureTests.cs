using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace OpenUGD.Tests
{
    [TestFixture]
    public class ContextFailureTests
    {
        private List<Lifetime.Definition> _definitions;
        private List<Context> _contexts;

        [SetUp]
        public void SetUp()
        {
            _definitions = new List<Lifetime.Definition>();
            _contexts = new List<Context>();
            Probe.Constructed = 0;
        }

        [TearDown]
        public void TearDown()
        {
            for (var i = _contexts.Count - 1; i >= 0; i--)
            {
                try
                {
                    _contexts[i].Dispose();
                }
                catch (Exception)
                {
                }
            }

            for (var i = _definitions.Count - 1; i >= 0; i--)
            {
                try
                {
                    _definitions[i].Terminate();
                }
                catch (Exception)
                {
                }
            }

            _contexts.Clear();
            _definitions.Clear();
        }

        private Lifetime.Definition NewDefinition(string id)
        {
            var definition = Lifetime.Eternal.DefineNested(id);
            _definitions.Add(definition);
            return definition;
        }

        private ContextBuilder NewBuilder(string id = "root") =>
            Context.CreateBuilder(lifetime: NewDefinition(id).Lifetime);

        private Context Build(ContextBuilder builder)
        {
            var context = RunSync(() => builder.BuildAsync());
            _contexts.Add(context);
            return context;
        }

        /// Starts the build with no synchronization context, so its continuations never queue to a context
        /// whose thread is about to block here waiting for them - which, under Unity's, would deadlock.
        private static Context RunSync(Func<Task<Context>> start, int timeoutMilliseconds = 15000)
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                var task = start();
                try
                {
                    if (!task.Wait(timeoutMilliseconds))
                    {
                        Assert.Fail("Operation did not complete within " + timeoutMilliseconds + " ms.");
                    }
                }
                catch (AggregateException)
                {
                }

                return task.GetAwaiter().GetResult();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        /// Runs a build that is expected to fail and returns the exception exactly as an `await` would
        /// deliver it - unwrapped, with its own type intact.
        private static TException FailToBuild<TException>(ContextBuilder builder) where TException : Exception
        {
            var exception = Assert.Catch(() => RunSync(() => builder.BuildAsync()));
            Assert.IsInstanceOf<TException>(exception,
                "Expected " + typeof(TException).Name + " but got: " + exception);
            return (TException)exception;
        }

        private static string ThisFile([CallerFilePath] string file = null) => file;

        private static int Line([CallerLineNumber] int line = 0) => line;

        // ===== missing bindings =====

        [Test]
        public void AMissingBindingUsesMicrosoftsExactWording()
        {
            var builder = NewBuilder();
            builder.Services.Add<NeedsClock>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains(
                "Unable to resolve service for type '" + typeof(IClock).FullName +
                "' while attempting to activate '" + typeof(NeedsClock).FullName + "'",
                error.Message);
        }

        [Test]
        public void AMissingBindingNamesTheFileAndLineOfTheRegistration()
        {
            var builder = NewBuilder();
            builder.Services.Add<NeedsClock>(); var registrationLine = Line();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains(ThisFile(), error.Message);
            StringAssert.Contains(registrationLine.ToString(), error.Message);
        }

        [Test]
        public void AMissingBindingSuggestsTheImplementationThatIsRegisteredButNotAsTheContract()
        {
            var builder = NewBuilder();
            builder.Services.Add<SystemClock>();
            builder.Services.Add<NeedsClock>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains(typeof(SystemClock).FullName, error.Message);
            StringAssert.Contains(".As<IClock>()", error.Message);
        }

        [Test]
        public void EveryProblemIsReportedAtOnceRatherThanOneRecompileAtATime()
        {
            var builder = NewBuilder();
            builder.Services.Add<NeedsClock>();
            builder.Services.Add<NeedsSession>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains(typeof(IClock).FullName, error.Message);
            StringAssert.Contains(typeof(ISession).FullName, error.Message);
            StringAssert.Contains("2 problems", error.Message);
        }

        [Test]
        public void NothingIsConstructedWhenValidationFails()
        {
            var builder = NewBuilder();
            builder.Services.Add<Probe>();
            builder.Services.Add<NeedsClock>();

            FailToBuild<ContextException>(builder);

            Assert.AreEqual(0, Probe.Constructed,
                "Validation runs before construction, so a graph that does not validate has no side effects.");
        }

        [Test]
        public void NothingIsConstructedWhenTheGraphHasACycle()
        {
            var builder = NewBuilder();
            builder.Services.Add<Probe>();
            builder.Services.Add<CycleA>();
            builder.Services.Add<CycleB>();
            builder.Services.Add<CycleC>();

            FailToBuild<ContextException>(builder);

            Assert.AreEqual(0, Probe.Constructed,
                "A constructor cycle is a validation error, so it is rejected before anything is constructed.");
        }

        [Test]
        public void AnUnresolvableInjectMemberFailsTheBuild()
        {
            var builder = NewBuilder();
            builder.Services.Add<NeedsClockByMember>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains(typeof(IClock).FullName, error.Message);
            StringAssert.Contains("[Inject]", error.Message);
        }

        // ===== cycles =====

        [Test]
        public void ACycleIsReportedWithItsActualPathInsteadOfAStackOverflow()
        {
            var builder = NewBuilder();
            builder.Services.Add<CycleA>();
            builder.Services.Add<CycleB>();
            builder.Services.Add<CycleC>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("circular dependency", error.Message);

            var path = error.Path;
            Assert.IsNotNull(path);
            Assert.GreaterOrEqual(path.Count, 3, "The path must name every participant of the cycle.");

            var participants = new HashSet<Type>(path);
            CollectionAssert.AreEquivalent(
                new[] { typeof(CycleA), typeof(CycleB), typeof(CycleC) }, participants);

            for (var i = 0; i < path.Count - 1; i++)
            {
                Assert.IsTrue(DependsOn(path[i], path[i + 1]),
                    "'" + path[i].Name + "' does not actually depend on '" + path[i + 1].Name +
                    "', so the reported path is not the real cycle.");
            }
        }

        [Test]
        public void ASelfDependencyIsACycle()
        {
            var builder = NewBuilder();
            builder.Services.Add<SelfCycle>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("circular dependency", error.Message);
            CollectionAssert.Contains(error.Path, typeof(SelfCycle));
        }

        [Test]
        public void ACycleThroughAFactoryIsCaughtWhileConstructingAndStillReportsAPath()
        {
            var builder = NewBuilder();
            builder.Services.Add<FactoryLoopA>(c => new FactoryLoopA(c.Resolve<FactoryLoopB>()));
            builder.Services.Add<FactoryLoopB>(c => new FactoryLoopB(c.Resolve<FactoryLoopA>()));

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("circular dependency", error.Message);
            CollectionAssert.Contains(error.Path, typeof(FactoryLoopA));
            CollectionAssert.Contains(error.Path, typeof(FactoryLoopB));
        }

        [Test]
        public void MutualInjectMembersAreNotACycle()
        {
            var builder = NewBuilder();
            builder.Services.Add<MemberLoopA>();
            builder.Services.Add<MemberLoopB>();

            var context = Build(builder);

            Assert.AreSame(context.Resolve<MemberLoopB>(), context.Resolve<MemberLoopA>().B);
            Assert.AreSame(context.Resolve<MemberLoopA>(), context.Resolve<MemberLoopB>().A);
        }

        // ===== registration mistakes =====

        [Test]
        public void AnAsMismatchIsAValidationErrorNamingBothTypesInFull()
        {
            var builder = NewBuilder();
            builder.Services.Add<SystemClock>().As(typeof(ISession));

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains(typeof(SystemClock).FullName, error.Message);
            StringAssert.Contains(typeof(ISession).FullName, error.Message);
        }

        [Test]
        public void RegisteringOneContractTwiceIsAValidationError()
        {
            var builder = NewBuilder();
            builder.Services.Add<SystemClock>().As<IClock>();
            builder.Services.Add<OtherClock>().As<IClock>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("registered twice", error.Message);
            StringAssert.Contains(typeof(SystemClock).FullName, error.Message);
            StringAssert.Contains(typeof(OtherClock).FullName, error.Message);
            StringAssert.Contains("TryAdd", error.Message);
        }

        [Test]
        public void AnAmbiguousConstructorChoiceIsAValidationError()
        {
            var builder = NewBuilder();
            builder.Services.Add<SystemClock>().As<IClock>();
            builder.Services.Add<Session>().As<ISession>();
            builder.Services.Add<Ambiguous>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("ambiguous", error.Message);
            StringAssert.Contains("[Inject]", error.Message);
        }

        [Test]
        public void RegisteringAnAbstractTypeIsAValidationError()
        {
            var builder = NewBuilder();
            builder.Services.Add(typeof(AbstractService));

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("abstract", error.Message);
        }

        [Test]
        public void RegisteringAnInterfaceWithoutAFactoryIsAValidationError()
        {
            var builder = NewBuilder();
            builder.Services.Add(typeof(IClock));

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("interface", error.Message);
        }

        [Test]
        public void RegisteringAnOpenGenericIsAValidationError()
        {
            var builder = NewBuilder();
            builder.Services.Add(typeof(OpenGeneric<>));

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("generic", error.Message);
        }

        [Test]
        public void AnInjectPropertyWithoutASetterIsAValidationError()
        {
            var builder = NewBuilder();
            builder.Services.Add<SystemClock>().As<IClock>();
            builder.Services.Add<UnwritableMember>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("no setter", error.Message);
        }

        [Test]
        public void ATypeWithNoPublicConstructorIsAValidationError()
        {
            var builder = NewBuilder();
            builder.Services.Add<PrivateConstructor>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("no public instance constructor", error.Message);
        }

        [Test]
        public void TheNoPublicConstructorErrorNamesStrippingAsAPossibleCause()
        {
            var builder = NewBuilder();
            builder.Services.Add<PrivateConstructor>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("managed code stripping", error.Message);
            StringAssert.Contains("Put [Inject] on the constructor", error.Message);
            Assert.Less(error.Message.IndexOf("registered at", StringComparison.Ordinal),
                error.Message.IndexOf("managed code stripping", StringComparison.Ordinal),
                "The registration site belongs to the problem line, before the advice.");
        }

        [Test]
        public void InstantiateNamesStrippingWhenThereIsNoPublicConstructor()
        {
            var context = Build(NewBuilder());

            var error = Assert.Throws<ContextException>(() => context.Instantiate<PrivateConstructor>());

            StringAssert.Contains("no public instance constructor", error.Message);
            StringAssert.Contains("managed code stripping", error.Message);
        }

        [Test]
        public void ATypeLeftWithNoConstructorAtAllIsBlamedOnStripping()
        {
            // No C# class can be written without an instance constructor, so the case is only reachable in a
            // stripped player; the wording is checked on the helper both error sites share.
            StringAssert.Contains("no instance constructor at all", Diagnostics.StrippingHint(0));
            StringAssert.Contains("has removed them", Diagnostics.StrippingHint(0));
            StringAssert.Contains("If its source declares a public constructor", Diagnostics.StrippingHint(1));
        }

        [Test]
        public void TwoInjectMarkedConstructorsAreAValidationError()
        {
            var builder = NewBuilder();
            builder.Services.Add<SystemClock>().As<IClock>();
            builder.Services.Add<Session>().As<ISession>();
            builder.Services.Add<TwiceMarked>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("[Inject]", error.Message);
            StringAssert.Contains("Exactly one", error.Message);
        }

        [Test]
        public void AnInjectMarkedConstructorWithAMissingDependencyIsReportedAgainstThatConstructor()
        {
            var builder = NewBuilder();
            builder.Services.Add<MarkedNeedsClock>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("Unable to resolve service for type '" + typeof(IClock).FullName + "'",
                error.Message);
        }

        // ===== atomicity =====

        [Test]
        public void AConstructorThatThrowsTearsDownEverythingConstructedSoFar()
        {
            var log = new List<string>();
            var builder = NewBuilder();
            builder.Services.AddInstance(log);
            builder.Services.Add<DisposableOne>();
            builder.Services.Add<DisposableTwo>();
            builder.Services.Add<ThrowingConstructor>();

            var error = FailToBuild<ContextException>(builder);

            Assert.IsInstanceOf<InvalidOperationException>(error.InnerException,
                "The original exception must be preserved as the InnerException.");
            CollectionAssert.AreEqual(new[] { "dispose:two", "dispose:one" }, log,
                "Everything constructed before the failure is disposed, in reverse construction order.");
        }

        [Test]
        public void AFailedBuildTerminatesTheScopeItWasBuildingSoNothingHalfBuiltEscapes()
        {
            var builder = NewBuilder();
            builder.Services.Add<NeedsClock>();

            FailToBuild<ContextException>(builder);

            Assert.IsTrue(builder.Lifetime.IsTerminated,
                "A failed build must leave no live scope behind.");
        }

        [Test]
        public void ABootStepThatThrowsNamesTheStepAndThePhaseAndTearsTheContextDown()
        {
            var log = new List<string>();
            var builder = NewBuilder();
            builder.Services.AddInstance(log);
            builder.Services.Add<DisposableOne>();
            builder.Services.Add<ThrowingAwakeService>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains(typeof(ThrowingAwakeService).Name, error.Message);
            StringAssert.Contains("Awake", error.Message);
            Assert.IsInstanceOf<InvalidOperationException>(error.InnerException);
            CollectionAssert.AreEqual(new[] { "dispose:one" }, log);
        }

        [Test]
        public void AnInitializerThatThrowsIsReportedWithItsName()
        {
            var builder = NewBuilder();
            builder.Initializers.Add(BootPhase.Initialize,
                (c, ct) => { throw new InvalidOperationException("boom"); }, "warm-the-catalog");

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("warm-the-catalog", error.Message);
            StringAssert.Contains("Initialize", error.Message);
        }

        [Test]
        public void AFactoryThatReturnsNullIsAnError()
        {
            var builder = NewBuilder();
            builder.Services.Add<SystemClock>(c => null);

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("returned null", error.Message);
        }

        [Test]
        public void ATeardownFailureIsReportedAlongsideTheOriginalFailure()
        {
            var builder = NewBuilder();
            builder.Services.Add<ThrowingDisposable>();
            builder.Services.Add<ThrowingAwakeService>();

            var error = FailToBuild<AggregateException>(builder);

            Assert.AreEqual(2, error.InnerExceptions.Count);
            Assert.IsInstanceOf<ContextException>(error.InnerExceptions[0],
                "The first inner exception is the original failure.");
            Assert.IsInstanceOf<InvalidOperationException>(error.InnerExceptions[1],
                "The second is the teardown failure: a single throwing Dispose arrives as itself.");
            Assert.AreEqual("dispose failed", error.InnerExceptions[1].Message);
        }

        [Test]
        public void DisposeRethrowsASingleServiceFailureAsItselfAfterDisposingTheRest()
        {
            var log = new List<string>();
            var builder = NewBuilder();
            builder.Services.AddInstance(log);
            builder.Services.Add<DisposableOne>();
            builder.Services.Add<ThrowingDisposable>();
            var context = Build(builder);

            var error = Assert.Throws<InvalidOperationException>(() => context.Dispose());

            Assert.AreEqual("dispose failed", error.Message);
            CollectionAssert.Contains(log, "dispose:one",
                "A service that fails to shut down must not leave its siblings undisposed.");
        }

        [Test]
        public void SeveralTeardownFailuresArriveAsOneAggregateInTheOrderTheyFailed()
        {
            var builder = NewBuilder();
            builder.Services.Add<ThrowingDisposable>();
            builder.Services.Add<SecondThrowingDisposable>();
            builder.Services.Add<ThrowingAwakeService>();

            var error = FailToBuild<AggregateException>(builder);

            Assert.AreEqual(2, error.InnerExceptions.Count);
            Assert.IsInstanceOf<ContextException>(error.InnerExceptions[0]);
            var teardown = error.InnerExceptions[1] as AggregateException;
            Assert.IsNotNull(teardown, "Two throwing Dispose calls are reported as one AggregateException.");
            Assert.AreEqual(new[] { "second dispose failed", "dispose failed" },
                teardown.InnerExceptions.Select(e => e.Message).ToArray(),
                "Teardown runs in reverse construction order, and the failures keep that order.");
        }

        // ===== cancellation =====

        [Test]
        public void ACancelledTokenAbortsTheBuildBeforeAnythingIsConstructed()
        {
            var builder = NewBuilder();
            builder.Services.Add<Probe>();

            var source = new CancellationTokenSource();
            source.Cancel();

            Assert.Catch<OperationCanceledException>(() => RunSync(() => builder.BuildAsync(source.Token)));

            Assert.AreEqual(0, Probe.Constructed);
            Assert.IsTrue(builder.Lifetime.IsTerminated);
        }

        [Test]
        public void CancellationDuringABootStepPropagatesUnwrappedAndTearsDown()
        {
            var log = new List<string>();
            var builder = NewBuilder();
            builder.Services.AddInstance(log);
            builder.Services.Add<DisposableOne>();

            var source = new CancellationTokenSource();
            builder.Initializers.Add(BootPhase.Awake, async (c, ct) => {
                source.Cancel();
                await Task.Delay(5000, ct);
            }, "cancels-itself");

            Assert.Catch<OperationCanceledException>(() => RunSync(() => builder.BuildAsync(source.Token)),
                "Cancellation must not be wrapped in a ContextException.");

            CollectionAssert.AreEqual(new[] { "dispose:one" }, log);
        }

        [Test]
        public void TerminatingTheScopeMidBootCancelsTheBuild()
        {
            var definition = NewDefinition("boot");
            var builder = Context.CreateBuilder(lifetime: definition.Lifetime);
            builder.Initializers.Add(BootPhase.Awake, async (c, ct) => {
                definition.Terminate();
                await Task.Delay(5000, ct);
            }, "ends-the-scope");

            Assert.Catch<OperationCanceledException>(() => RunSync(() => builder.BuildAsync()));
        }

        // ===== resolve-time failures =====

        [Test]
        public void ResolveThrowsForAMissingServiceInsteadOfReturningNull()
        {
            var context = Build(NewBuilder());

            var error = Assert.Throws<ContextException>(() => context.Resolve<IClock>());

            StringAssert.Contains(typeof(IClock).FullName, error.Message);
        }

        [Test]
        public void ADisposedContextRefusesToHandOutDisposedServices()
        {
            var builder = NewBuilder();
            builder.Services.Add<SystemClock>().As<IClock>();
            var context = Build(builder);

            context.Dispose();

            object service;
            Assert.Throws<ObjectDisposedException>(() => context.Resolve<IClock>());
            Assert.Throws<ObjectDisposedException>(() => context.TryResolve(typeof(IClock), out service));
            Assert.Throws<ObjectDisposedException>(() => context.Instantiate<NeedsClock>());
            Assert.Throws<ObjectDisposedException>(() => context.Inject(new NeedsClockByMember()));
        }

        [Test]
        public void ABuilderWithADisposedParentIsBornTerminatedAndNeverBuilds()
        {
            var context = Build(NewBuilder());
            context.Dispose();

            var builder = Context.CreateBuilder(parent: context);
            builder.Services.Add<Probe>(); // registering still works, as on any terminated lifetime

            Assert.IsTrue(builder.Lifetime.IsTerminated,
                "Born terminated, like Lifetime.DefineNested on a terminated lifetime.");
            Assert.Catch<OperationCanceledException>(() => RunSync(() => builder.BuildAsync()));
            Assert.AreEqual(0, Probe.Constructed);
        }

        [Test]
        public void ABuilderOnATerminatedLifetimeIsBornTerminatedAndNeverBuilds()
        {
            var definition = NewDefinition("dead");
            definition.Terminate();

            var builder = Context.CreateBuilder(definition.Lifetime);
            builder.Services.Add<Probe>();

            Assert.IsTrue(builder.Lifetime.IsTerminated);
            var error = Assert.Catch<OperationCanceledException>(() => RunSync(() => builder.BuildAsync()));
            StringAssert.Contains("ended before BuildAsync", error.Message);
            Assert.AreEqual(0, Probe.Constructed);
        }

        [Test]
        public void ABuilderWithALiveLifetimeButADisposedParentIsBornTerminated()
        {
            var parent = Build(NewBuilder("parent"));
            parent.Dispose();

            var builder = Context.CreateBuilder(NewDefinition("alive").Lifetime, parent);

            Assert.IsTrue(builder.Lifetime.IsTerminated, "A child never outlives its parent, whatever it was given.");
        }

        [Test]
        public void InstantiateRefusesATypeItCannotActivate()
        {
            var context = Build(NewBuilder());

            var error = Assert.Throws<ContextException>(() => context.Instantiate<IClock>());

            StringAssert.Contains("interface", error.Message);
        }

        [Test]
        public void InstantiateReportsTheParameterItCouldNotSupply()
        {
            var context = Build(NewBuilder());

            var error = Assert.Throws<ContextException>(() => context.Instantiate<NeedsClock>());

            StringAssert.Contains(typeof(IClock).FullName, error.Message);
        }

        [Test]
        public void InjectReportsAMemberItCouldNotResolve()
        {
            var context = Build(NewBuilder());

            var error = Assert.Throws<ContextException>(() => context.Inject(new NeedsClockByMember()));

            StringAssert.Contains(typeof(IClock).FullName, error.Message);
            StringAssert.Contains("[Inject]", error.Message);
        }

        [Test]
        public void ContextExceptionPathIsNeverNull()
        {
            Assert.IsNotNull(new ContextException("x").Path);
            Assert.IsNotNull(new ContextException("x", new Exception()).Path);
            Assert.AreEqual(0, new ContextException("x").Path.Count);
        }

        // ===== package invariants =====

        [Test]
        public void DebugInstanceEnumerationIsNotPublicApi()
        {
            var members = typeof(Context).GetMembers(BindingFlags.Public | BindingFlags.Instance |
                                                     BindingFlags.Static);

            Assert.IsFalse(members.Any(m => m.Name == "Instances"),
                "The instance table is a debugging aid, not API: a public one becomes a supported way to " +
                "program against the container by accident.");
        }

        [Test]
        public void ThePackageReferencesNoUnityAssembly()
        {
            var referenced = typeof(Context).Assembly.GetReferencedAssemblies();

            foreach (var reference in referenced)
            {
                Assert.IsFalse(reference.Name.StartsWith("UnityEngine", StringComparison.Ordinal),
                    "com.openugd.context must build with no engine present: " + reference.Name);
                Assert.IsFalse(reference.Name.StartsWith("UnityEditor", StringComparison.Ordinal),
                    "com.openugd.context must build with no engine present: " + reference.Name);
            }
        }

        // ===== fixtures =====

        private static bool DependsOn(Type type, Type dependency)
        {
            foreach (var constructor in type.GetConstructors())
            {
                foreach (var parameter in constructor.GetParameters())
                {
                    if (parameter.ParameterType.IsAssignableFrom(dependency)) return true;
                }
            }

            return false;
        }

        public interface IClock { }
        public interface ISession { }

        public sealed class SystemClock : IClock { }
        public sealed class OtherClock : IClock { }
        public sealed class Session : ISession { }

        public abstract class AbstractService { }

        public sealed class OpenGeneric<T> { }

        public sealed class Probe
        {
            public static int Constructed;
            public Probe() { Constructed++; }
        }

        public sealed class NeedsClock
        {
            public NeedsClock(IClock clock) { }
        }

        public sealed class NeedsSession
        {
            public NeedsSession(ISession session) { }
        }

        public sealed class NeedsClockByMember
        {
            [Inject] public IClock Clock;
        }

        public sealed class MarkedNeedsClock
        {
            [Inject]
            public MarkedNeedsClock(IClock clock) { }

            public MarkedNeedsClock() { }
        }

        public sealed class TwiceMarked
        {
            [Inject]
            public TwiceMarked(IClock clock) { }

            [Inject]
            public TwiceMarked(ISession session) { }
        }

        public sealed class Ambiguous
        {
            public Ambiguous(IClock clock) { }
            public Ambiguous(ISession session) { }
        }

        public sealed class PrivateConstructor
        {
            private PrivateConstructor() { }
        }

        public sealed class UnwritableMember
        {
            [Inject] public IClock Clock => null;
        }

        public sealed class CycleA
        {
            public CycleA(CycleB b) { }
        }

        public sealed class CycleB
        {
            public CycleB(CycleC c) { }
        }

        public sealed class CycleC
        {
            public CycleC(CycleA a) { }
        }

        public sealed class SelfCycle
        {
            public SelfCycle(SelfCycle self) { }
        }

        public sealed class FactoryLoopA
        {
            public FactoryLoopA(FactoryLoopB b) { }
        }

        public sealed class FactoryLoopB
        {
            public FactoryLoopB(FactoryLoopA a) { }
        }

        public sealed class MemberLoopA
        {
            [Inject] public MemberLoopB B;
        }

        public sealed class MemberLoopB
        {
            [Inject] public MemberLoopA A;
        }

        public sealed class DisposableOne : IDisposable
        {
            private readonly List<string> _log;
            public DisposableOne(List<string> log) { _log = log; }
            public void Dispose() { _log.Add("dispose:one"); }
        }

        public sealed class DisposableTwo : IDisposable
        {
            private readonly List<string> _log;
            public DisposableTwo(List<string> log, DisposableOne one) { _log = log; }
            public void Dispose() { _log.Add("dispose:two"); }
        }

        public sealed class ThrowingConstructor
        {
            public ThrowingConstructor(DisposableTwo two)
            {
                throw new InvalidOperationException("constructor failed");
            }
        }

        public sealed class ThrowingDisposable : IDisposable
        {
            public void Dispose() { throw new InvalidOperationException("dispose failed"); }
        }

        public sealed class SecondThrowingDisposable : IDisposable
        {
            public void Dispose() { throw new InvalidOperationException("second dispose failed"); }
        }

        public sealed class ThrowingAwakeService : IAwakeService
        {
            public Task AwakeAsync(CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("awake failed");
            }
        }
    }
}
