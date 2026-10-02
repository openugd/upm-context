using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace OpenUGD.Tests
{
    [TestFixture]
    public class ContextTests
    {
        private List<Lifetime.Definition> _definitions;
        private List<Context> _contexts;

        [SetUp]
        public void SetUp()
        {
            _definitions = new List<Lifetime.Definition>();
            _contexts = new List<Context>();
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

        private Lifetime NewLifetime(string id) => NewDefinition(id).Lifetime;

        private static T RunSync<T>(Func<Task<T>> start, int timeoutMilliseconds = 15000)
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

        private Context Build(ContextBuilder builder)
        {
            var context = RunSync(() => builder.BuildAsync());
            Assert.IsNotNull(context, "BuildAsync must never hand back a null Context.");
            _contexts.Add(context);
            return context;
        }

        private ContextBuilder NewBuilder(string id = "root") =>
            Context.CreateBuilder(lifetime: NewLifetime(id));

        private static void AssertRanBefore(BootLog log, string earlier, string later)
        {
            var entries = log.Entries;
            var first = entries.IndexOf(earlier);
            var second = entries.IndexOf(later);
            Assert.IsTrue(first >= 0, "'" + earlier + "' never ran. Log: [" + string.Join(", ", entries) + "]");
            Assert.IsTrue(second >= 0, "'" + later + "' never ran. Log: [" + string.Join(", ", entries) + "]");
            Assert.IsTrue(first < second,
                "'" + earlier + "' must run before '" + later + "'. Log: [" + string.Join(", ", entries) + "]");
        }

        // ===== registration =====

        [Test]
        public void Add_RegistersTheImplementationUnderItsOwnType()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>();

            var context = Build(builder);

            Assert.IsNotNull(context.Resolve<Alpha>());
        }

        [Test]
        public void AddWithFactory_UsesTheFactoryAndStillRegistersUnderTheImplementation()
        {
            var builder = NewBuilder();
            var made = new Alpha();
            builder.Services.Add<Alpha>(c => made);

            var context = Build(builder);

            Assert.AreSame(made, context.Resolve<Alpha>());
        }

        [Test]
        public void AddWithFactory_CanResolveOtherServicesFromTheContextItIsHanded()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();
            builder.Services.Add<AlphaHolder>(c => new AlphaHolder(c.Resolve<IAlpha>()));

            var context = Build(builder);

            Assert.AreSame(context.Resolve<IAlpha>(), context.Resolve<AlphaHolder>().Alpha);
        }

        [Test]
        public void AddWithFactory_MayRegisterAnInterfaceItCannotActivateItself()
        {
            var builder = NewBuilder();
            builder.Services.Add<IAlpha>(c => new Alpha());

            var context = Build(builder);

            Assert.IsInstanceOf<Alpha>(context.Resolve<IAlpha>());
        }

        [Test]
        public void AddInstance_RegistersTheExactInstanceUnderTheGivenContract()
        {
            var builder = NewBuilder();
            var instance = new Alpha();
            builder.Services.AddInstance<IAlpha>(instance);

            var context = Build(builder);

            Assert.AreSame(instance, context.Resolve<IAlpha>());
        }

        [Test]
        public void AddInstance_IsNeverDisposedByTheContextThatDidNotCreateIt()
        {
            var definition = NewDefinition("borrowed");
            var builder = Context.CreateBuilder(lifetime: definition.Lifetime);
            var probe = new DisposableProbe();
            builder.Services.AddInstance<IProbe>(probe);
            Build(builder);

            definition.Terminate();

            Assert.IsFalse(probe.Disposed,
                "The context disposes what it created. An instance handed to AddInstance was created by " +
                "the caller and stays the caller's.");
        }

        [Test]
        public void AddByType_RegistersAndActivatesTheType()
        {
            var builder = NewBuilder();
            builder.Services.Add(typeof(Alpha));

            var context = Build(builder);

            Assert.IsInstanceOf<Alpha>(context.Resolve(typeof(Alpha)));
        }

        [Test]
        public void AddByType_WithAFactoryUsesTheFactory()
        {
            var builder = NewBuilder();
            var made = new Alpha();
            builder.Services.Add(typeof(Alpha), c => made);

            var context = Build(builder);

            Assert.AreSame(made, context.Resolve(typeof(Alpha)));
        }

        [Test]
        public void TryAdd_DoesNotReplaceAContractThatIsAlreadyRegistered()
        {
            var builder = NewBuilder();
            var existing = new Alpha();
            builder.Services.AddInstance<IAlpha>(existing);
            builder.Services.TryAdd<IAlpha, OtherAlpha>();

            var context = Build(builder);

            Assert.AreSame(existing, context.Resolve<IAlpha>());
        }

        [Test]
        public void TryAdd_RegistersWhenTheContractIsAbsent()
        {
            var builder = NewBuilder();
            builder.Services.TryAdd<IAlpha, Alpha>();

            var context = Build(builder);

            var byContract = context.Resolve<IAlpha>();
            Assert.IsInstanceOf<Alpha>(byContract);
            Assert.AreSame(byContract, context.Resolve<Alpha>());
        }

        [Test]
        public void TryAdd_ReportsWhetherItRegistered()
        {
            var builder = NewBuilder();

            Assert.IsTrue(builder.Services.TryAdd<IAlpha, Alpha>());
            Assert.IsFalse(builder.Services.TryAdd<IAlpha, OtherAlpha>());
        }

        [Test]
        public void TryAdd_DoesNotShadowAServiceTheParentAlreadyProvides()
        {
            var parentBuilder = NewBuilder("parent");
            parentBuilder.Services.Add<ParentTag>().As<ITag>();
            var parent = Build(parentBuilder);

            var childBuilder = Context.CreateBuilder(lifetime: NewLifetime("child"), parent: parent);
            Assert.IsFalse(childBuilder.Services.TryAdd<ITag, ChildTag>());
            var child = Build(childBuilder);

            Assert.AreSame(parent.Resolve<ITag>(), child.Resolve<ITag>());
        }

        [Test]
        public void Contains_ReportsWhetherAContractIsRegistered()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();

            Assert.IsTrue(builder.Services.Contains(typeof(IAlpha)));
            Assert.IsTrue(builder.Services.Contains(typeof(Alpha)));
            Assert.IsFalse(builder.Services.Contains(typeof(IBeta)));
        }

        [Test]
        public void ServiceCollection_EnumeratesItsRegistrations()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>();
            builder.Services.Add<Beta>();

            var implementations = builder.Services.Select(r => r.Implementation).ToList();

            CollectionAssert.Contains(implementations, typeof(Alpha));
            CollectionAssert.Contains(implementations, typeof(Beta));
        }

        [Test]
        public void Registration_PointsBackAtTheCollectionThatOwnsIt()
        {
            var builder = NewBuilder();
            var registration = builder.Services.Add<Alpha>();

            Assert.AreSame(builder.Services, registration.Services);
            Assert.AreEqual(typeof(Alpha), registration.Implementation);
        }

        // ===== the .As<>() chain =====

        [Test]
        public void As_AddsAContractWithoutRemovingTheSelfRegistration()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();

            var context = Build(builder);

            var byContract = context.Resolve<IAlpha>();
            var bySelf = context.Resolve<Alpha>();
            Assert.AreSame(byContract, bySelf,
                "Add<A>().As<I>() must leave A resolvable, and it must be the SAME singleton.");
        }

        [Test]
        public void As_CanAddTwoContractsToOneSingleton()
        {
            var builder = NewBuilder();
            builder.Services.Add<TwoFaced>().As<IAlpha>().As<IBeta>();

            var context = Build(builder);

            var self = context.Resolve<TwoFaced>();
            Assert.AreSame(self, context.Resolve<IAlpha>());
            Assert.AreSame(self, context.Resolve<IBeta>());
        }

        [Test]
        public void As_WorksAfterAddInstance()
        {
            var builder = NewBuilder();
            var instance = new TwoFaced();
            builder.Services.AddInstance<IAlpha>(instance).As<IBeta>();

            var context = Build(builder);

            Assert.AreSame(instance, context.Resolve<IAlpha>());
            Assert.AreSame(instance, context.Resolve<IBeta>());
        }

        [Test]
        public void As_WorksAfterTheFactoryOverload()
        {
            var builder = NewBuilder();
            var made = new TwoFaced();
            builder.Services.Add<TwoFaced>(c => made).As<IAlpha>().As<IBeta>();

            var context = Build(builder);

            Assert.AreSame(made, context.Resolve<IAlpha>());
            Assert.AreSame(made, context.Resolve<IBeta>());
        }

        [Test]
        public void RegistrationAdd_BridgesBackSoOneChainNeverBreaks()
        {
            var builder = NewBuilder();
            builder.Services
                .Add<Alpha>().As<IAlpha>()
                .Add<Beta>().As<IBeta>();

            var context = Build(builder);

            Assert.IsInstanceOf<Alpha>(context.Resolve<IAlpha>());
            Assert.IsInstanceOf<Beta>(context.Resolve<IBeta>());
        }

        [Test]
        public void AsByType_IsAvailableOnTheCoreStruct()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As(typeof(IAlpha));

            var context = Build(builder);

            Assert.AreSame(context.Resolve<Alpha>(), context.Resolve(typeof(IAlpha)));
        }

        // ===== constructor selection =====

        [Test]
        public void ASingleAccessibleConstructorIsUsed()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();
            builder.Services.Add<SingleConstructor>();

            var context = Build(builder);

            Assert.AreSame(context.Resolve<IAlpha>(), context.Resolve<SingleConstructor>().Alpha);
        }

        [Test]
        public void TheGreediestConstructorWhoseParametersAreAllRegisteredIsChosen()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();
            builder.Services.Add<Beta>().As<IBeta>();
            builder.Services.Add<Greedy>();

            var context = Build(builder);

            var greedy = context.Resolve<Greedy>();
            Assert.AreEqual(2, greedy.ChosenArity);
            Assert.IsNotNull(greedy.Alpha);
            Assert.IsNotNull(greedy.Beta);
        }

        [Test]
        public void AConvenienceParameterlessConstructorDoesNotDisableInjection()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();
            builder.Services.Add<Greedy>();

            var context = Build(builder);

            var greedy = context.Resolve<Greedy>();
            Assert.AreEqual(1, greedy.ChosenArity);
            Assert.IsNotNull(greedy.Alpha);
        }

        [Test]
        public void AnInjectMarkedConstructorWinsOutrightOverTheGreediestOne()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();
            builder.Services.Add<Beta>().As<IBeta>();
            builder.Services.Add<MarkedConstructor>();

            var context = Build(builder);

            Assert.AreEqual(1, context.Resolve<MarkedConstructor>().ChosenArity);
        }

        [Test]
        public void AServiceCanTakeItsOwnLifetimeAsAConstructorParameter()
        {
            var builder = NewBuilder();
            builder.Services.Add<ScopedService>();

            var context = Build(builder);

            Assert.AreSame(context.Lifetime, context.Resolve<ScopedService>().Lifetime);
        }

        [Test]
        public void AServiceCanTakeTheContextAsAConstructorParameter()
        {
            var builder = NewBuilder();
            builder.Services.Add<ContextAware>();

            var context = Build(builder);

            Assert.AreSame(context, context.Resolve<ContextAware>().Context);
        }

        // ===== member [Inject] =====

        [Test]
        public void Inject_FillsMarkedFieldsAndPropertiesOnAnObjectTheContextDidNotConstruct()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();
            builder.Services.Add<Beta>().As<IBeta>();

            var context = Build(builder);

            var target = new MemberInjectionTarget();
            context.Inject(target);

            Assert.AreSame(context.Resolve<IAlpha>(), target.AlphaField);
            Assert.AreSame(context.Resolve<IBeta>(), target.BetaProperty);
        }

        [Test]
        public void Inject_LeavesUnmarkedMembersAlone()
        {
            // OVERRIDDEN (fixture only): the blind version registered IAlpha alone, but the target also
            // carries an [Inject] IBeta property, and an [Inject] member that cannot be resolved is an
            // error in this package - that is the whole point. IBeta is registered so the test can make
            // the claim it is actually about: an UNMARKED member is never written to.
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();
            builder.Services.Add<Beta>().As<IBeta>();

            var context = Build(builder);

            var target = new MemberInjectionTarget();
            context.Inject(target);

            Assert.IsNull(target.UnmarkedField, "An unmarked field must never be written to.");
        }

        [Test]
        public void Inject_MarkedMembersAreFilledOnServicesTheContainerConstructs()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();
            builder.Services.Add<Beta>().As<IBeta>();
            builder.Services.Add<MemberInjectionTarget>();

            var context = Build(builder);

            var service = context.Resolve<MemberInjectionTarget>();
            Assert.AreSame(context.Resolve<IAlpha>(), service.AlphaField);
            Assert.AreSame(context.Resolve<IBeta>(), service.BetaProperty);
        }

        [Test]
        public void Inject_FillsTheMembersOfAnInstanceHandedToAddInstance()
        {
            // Registering an object hands it to the container for wiring - the container still never
            // disposes it. The alternative, silently ignoring a marked member, is the defect being fixed.
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();
            builder.Services.Add<Beta>().As<IBeta>();
            var instance = new MemberInjectionTarget();
            builder.Services.AddInstance(instance);

            var context = Build(builder);

            Assert.AreSame(context.Resolve<IAlpha>(), instance.AlphaField);
        }

        [Test]
        public void AnInjectMarkedConstructorMayBeNonPublic()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();
            builder.Services.Add<HiddenConstructor>();

            var context = Build(builder);

            Assert.IsNotNull(context.Resolve<HiddenConstructor>().Alpha);
        }

        [Test]
        public void Inject_FillsPrivateMembersAndBaseClassMembers()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();
            builder.Services.Add<Beta>().As<IBeta>();
            builder.Services.Add<DerivedInjectionTarget>();

            var context = Build(builder);

            var service = context.Resolve<DerivedInjectionTarget>();
            Assert.AreSame(context.Resolve<IAlpha>(), service.BaseAlpha);
            Assert.AreSame(context.Resolve<IBeta>(), service.DerivedBeta);
        }

        [Test]
        public void TwoServicesMayHoldEachOtherThroughInjectMembers()
        {
            var builder = NewBuilder();
            builder.Services.Add<PingService>();
            builder.Services.Add<PongService>();

            var context = Build(builder);

            var ping = context.Resolve<PingService>();
            var pong = context.Resolve<PongService>();
            Assert.AreSame(pong, ping.Pong);
            Assert.AreSame(ping, pong.Ping);
        }

        // ===== Instantiate =====

        [Test]
        public void Instantiate_ResolvesConstructorDependenciesFromTheContext()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();

            var context = Build(builder);

            var made = context.Instantiate<AlphaHolder>();

            Assert.AreSame(context.Resolve<IAlpha>(), made.Alpha);
        }

        [Test]
        public void Instantiate_ReturnsAFreshInstanceEveryTimeAndRegistersNothing()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();

            var context = Build(builder);

            var first = context.Instantiate<AlphaHolder>();
            var second = context.Instantiate<AlphaHolder>();

            Assert.AreNotSame(first, second);

            AlphaHolder resolved;
            Assert.IsFalse(context.TryResolve(out resolved),
                "Instantiate must not add a registration as a side effect.");
        }

        [Test]
        public void Instantiate_AcceptsExplicitArgumentsAlongsideResolvedOnes()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();

            var context = Build(builder);

            var made = context.Instantiate<MixedArguments>("level-01");

            Assert.AreEqual("level-01", made.Name);
            Assert.AreSame(context.Resolve<IAlpha>(), made.Alpha);
        }

        [Test]
        public void Instantiate_HandsOwnershipToTheCallerAndNotToTheContext()
        {
            var definition = NewDefinition("owns-nothing");
            var builder = Context.CreateBuilder(lifetime: definition.Lifetime);
            var context = Build(builder);

            var orphan = context.Instantiate<DisposableProbe>();

            definition.Terminate();

            Assert.IsFalse(orphan.Disposed,
                "The context must not dispose objects it merely instantiated - the caller owns them.");
        }

        [Test]
        public void Instantiate_ByTypeIsTheCorePrimitive()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();

            var context = Build(builder);

            var made = context.Instantiate(typeof(MixedArguments), new object[] { "boss-fight" });

            Assert.IsInstanceOf<MixedArguments>(made);
            Assert.AreEqual("boss-fight", ((MixedArguments)made).Name);
        }

        [Test]
        public void Instantiate_FillsInjectMembersToo()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();
            builder.Services.Add<Beta>().As<IBeta>();

            var context = Build(builder);

            var made = context.Instantiate<MemberInjectionTarget>();

            Assert.AreSame(context.Resolve<IAlpha>(), made.AlphaField);
        }

        // ===== singleton identity, parent caching =====

        [Test]
        public void ARegisteredServiceIsASingletonWithinItsContext()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();

            var context = Build(builder);

            Assert.AreSame(context.Resolve<IAlpha>(), context.Resolve<IAlpha>());
            Assert.AreSame(context.Resolve<IAlpha>(), context.Resolve<Alpha>());
        }

        [Test]
        public void OneSingletonIsSharedByEveryDependentThatAsksForIt()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();
            builder.Services.Add<AlphaHolder>();
            builder.Services.Add<SingleConstructor>();

            var context = Build(builder);

            Assert.AreSame(context.Resolve<AlphaHolder>().Alpha, context.Resolve<SingleConstructor>().Alpha);
        }

        [Test]
        public void AParentSingletonFirstRequestedThroughAChildIsCachedInTheParent()
        {
            var parentDefinition = NewDefinition("parent");
            var parentBuilder = Context.CreateBuilder(lifetime: parentDefinition.Lifetime);
            parentBuilder.Services.Add<ParentTag>().As<ITag>();
            parentBuilder.Services.Add<TagHolder>();
            var parent = Build(parentBuilder);

            var childDefinition = parentDefinition.Lifetime.DefineNested("child");
            _definitions.Add(childDefinition);
            var childBuilder = Context.CreateBuilder(lifetime: childDefinition.Lifetime, parent: parent);
            childBuilder.Services.Add<ChildTag>().As<ITag>();
            var child = Build(childBuilder);

            var throughChild = child.Resolve<TagHolder>();

            Assert.IsInstanceOf<ParentTag>(throughChild.Tag,
                "A parent-registered singleton must be built with the PARENT's registrations.");
            Assert.AreSame(throughChild, parent.Resolve<TagHolder>());

            childDefinition.Terminate();

            Assert.AreSame(throughChild, parent.Resolve<TagHolder>(),
                "The parent's singleton must survive the child's scope.");
        }

        // ===== child contexts =====

        [Test]
        public void ARootContextHasANullParentAndAChildPointsAtIts()
        {
            var parent = Build(NewBuilder("parent"));
            var child = Build(Context.CreateBuilder(lifetime: NewLifetime("child"), parent: parent));

            Assert.IsNull(parent.Parent);
            Assert.AreSame(parent, child.Parent);
        }

        [Test]
        public void AChildSeesTheParentRegistrations()
        {
            var parentBuilder = NewBuilder("parent");
            parentBuilder.Services.Add<Alpha>().As<IAlpha>();
            var parent = Build(parentBuilder);

            var child = Build(Context.CreateBuilder(lifetime: NewLifetime("child"), parent: parent));

            Assert.AreSame(parent.Resolve<IAlpha>(), child.Resolve<IAlpha>());
        }

        [Test]
        public void AChildRegistrationShadowsTheParentWithoutDisturbingIt()
        {
            var parentBuilder = NewBuilder("parent");
            parentBuilder.Services.Add<ParentTag>().As<ITag>();
            var parent = Build(parentBuilder);

            var childBuilder = Context.CreateBuilder(lifetime: NewLifetime("child"), parent: parent);
            childBuilder.Services.Add<ChildTag>().As<ITag>();
            var child = Build(childBuilder);

            Assert.IsInstanceOf<ChildTag>(child.Resolve<ITag>());
            Assert.IsInstanceOf<ParentTag>(parent.Resolve<ITag>());
        }

        [Test]
        public void AChildResolvesItsOwnContextAndLifetimeNotTheParents()
        {
            var parent = Build(NewBuilder("parent"));
            var child = Build(Context.CreateBuilder(lifetime: NewLifetime("child"), parent: parent));

            Assert.AreSame(child, child.Resolve<Context>());
            Assert.AreSame(child.Lifetime, child.Resolve<Lifetime>());
            Assert.AreNotSame(parent.Lifetime, child.Resolve<Lifetime>());
        }

        [Test]
        public void AChildWithNoLifetimeOfItsOwnIsNestedInItsParent()
        {
            var parentDefinition = NewDefinition("parent");
            var parent = Build(Context.CreateBuilder(lifetime: parentDefinition.Lifetime));
            var child = Build(Context.CreateBuilder(parent: parent));

            parentDefinition.Terminate();

            Assert.IsTrue(child.Lifetime.IsTerminated,
                "A child must never outlive the parent whose services it borrows.");
        }

        [Test]
        public void AChildsInstancesDieWithItsOwnLifetimeWhileTheParentSurvives()
        {
            var parentDefinition = NewDefinition("parent");
            var parentBuilder = Context.CreateBuilder(lifetime: parentDefinition.Lifetime);
            parentBuilder.Services.Add<DisposableProbe>().As<IProbe>();
            var parent = Build(parentBuilder);
            var parentProbe = (DisposableProbe)parent.Resolve<IProbe>();

            var childDefinition = parentDefinition.Lifetime.DefineNested("child");
            _definitions.Add(childDefinition);
            var childBuilder = Context.CreateBuilder(lifetime: childDefinition.Lifetime, parent: parent);
            childBuilder.Services.Add<OtherDisposableProbe>().As<IProbe>();
            var child = Build(childBuilder);
            var childProbe = (OtherDisposableProbe)child.Resolve<IProbe>();

            childDefinition.Terminate();

            Assert.IsTrue(childProbe.Disposed, "A child's own singletons die with the child's Lifetime.");
            Assert.IsFalse(parentProbe.Disposed, "The parent's singletons must be untouched.");
            Assert.AreSame(parentProbe, parent.Resolve<IProbe>());
        }

        [Test]
        public void SingletonsAreDisposedInReverseConstructionOrder()
        {
            var definition = NewDefinition("lifo");
            var builder = Context.CreateBuilder(lifetime: definition.Lifetime);
            var log = new BootLog();
            builder.Services.AddInstance(log);
            builder.Services.Add<FirstDisposable>();
            builder.Services.Add<SecondDisposable>();
            var context = Build(builder);

            Assert.IsNotNull(context.Resolve<SecondDisposable>());

            definition.Terminate();

            AssertRanBefore(log, "dispose:second", "dispose:first");
        }

        [Test]
        public void DisposingAContextThatOwnsItsScopeTerminatesThatScope()
        {
            var builder = Context.CreateBuilder();
            builder.Services.Add<DisposableProbe>().As<IProbe>();
            var context = Build(builder);
            var probe = (DisposableProbe)context.Resolve<IProbe>();

            Assert.IsFalse(context.Lifetime.IsTerminated);

            context.Dispose();

            Assert.IsTrue(context.Lifetime.IsTerminated);
            Assert.IsTrue(probe.Disposed);
        }

        [Test]
        public void AContextsLifetimeIsAliveOnceBuiltAndDiesWithTheScopeItWasGiven()
        {
            var definition = NewDefinition("given");
            var context = Build(Context.CreateBuilder(lifetime: definition.Lifetime));

            Assert.IsNotNull(context.Lifetime);
            Assert.IsFalse(context.Lifetime.IsTerminated);

            definition.Terminate();

            Assert.IsTrue(context.Lifetime.IsTerminated);
        }

        [Test]
        public void DisposingAContextDoesNotTerminateTheLifetimeItWasGiven()
        {
            var definition = NewDefinition("outer");
            var context = Build(Context.CreateBuilder(lifetime: definition.Lifetime));

            context.Dispose();

            Assert.IsTrue(context.Lifetime.IsTerminated);
            Assert.IsFalse(definition.Lifetime.IsTerminated,
                "A context owns a scope NESTED in the one it was handed; it never ends the caller's.");
        }

        // ===== the boot pipeline =====

        [Test]
        public void ServicesEnrolInTheBootPipelineAutomaticallyWithoutTouchingInitializers()
        {
            var builder = NewBuilder();
            var log = new BootLog();
            builder.Services.AddInstance(log);
            builder.Services.Add<RankLeaf>();

            Build(builder);

            CollectionAssert.Contains(log.Entries, "leaf:awake:enter");
            CollectionAssert.Contains(log.Entries, "leaf:initialize");
        }

        [Test]
        public void AwakeRunsInDependencyRankOrderNotRegistrationOrder()
        {
            var builder = NewBuilder();
            var log = new BootLog();
            builder.Services.AddInstance(log);
            builder.Services.Add<RankTop>();
            builder.Services.Add<RankMid>();
            builder.Services.Add<RankLeaf>();

            Build(builder);

            AssertRanBefore(log, "leaf:awake:exit", "mid:awake:enter");
            AssertRanBefore(log, "mid:awake:exit", "top:awake:enter");
        }

        [Test]
        public void InitializeRunsInDependencyRankOrderToo()
        {
            var builder = NewBuilder();
            var log = new BootLog();
            builder.Services.AddInstance(log);
            builder.Services.Add<RankTop>();
            builder.Services.Add<RankMid>();
            builder.Services.Add<RankLeaf>();

            Build(builder);

            AssertRanBefore(log, "leaf:initialize", "mid:initialize");
            AssertRanBefore(log, "mid:initialize", "top:initialize");
        }

        [Test]
        public void AFactorysHiddenDependenciesStillRankItCorrectly()
        {
            var builder = NewBuilder();
            var log = new BootLog();
            builder.Services.AddInstance(log);
            builder.Services.Add<FactoryTop>(c => new FactoryTop(c.Resolve<RankLeaf>(), log));
            builder.Services.Add<RankLeaf>();

            Build(builder);

            AssertRanBefore(log, "leaf:awake:exit", "factory-top:awake");
        }

        [Test]
        public void EveryAwakeCompletesBeforeAnyInitialize()
        {
            var builder = NewBuilder();
            var log = new BootLog();
            builder.Services.AddInstance(log);
            builder.Services.Add<RankTop>();
            builder.Services.Add<RankMid>();
            builder.Services.Add<RankLeaf>();

            Build(builder);

            // The highest-ranked awake against the lowest-ranked initialize: the phase boundary, not rank
            // order, is what puts one before the other.
            AssertRanBefore(log, "top:awake:exit", "leaf:initialize");
        }

        [Test]
        public void InitializersRunInTheirDeclaredPhase()
        {
            var builder = NewBuilder();
            var log = new BootLog();
            builder.Services.AddInstance(log);
            builder.Initializers
                .Add(BootPhase.Initialize, (c, ct) => { log.Add("step:initialize"); return Task.CompletedTask; },
                    "late")
                .Add(BootPhase.Awake, (c, ct) => { log.Add("step:awake"); return Task.CompletedTask; },
                    "early");

            Build(builder);

            AssertRanBefore(log, "step:awake", "step:initialize");
        }

        [Test]
        public void AnInitializerStepReceivesTheContextBeingBuilt()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();
            object seen = null;
            builder.Initializers.Add(BootPhase.Initialize, (c, ct) => {
                seen = c.Resolve<IAlpha>();
                return Task.CompletedTask;
            }, "resolve-step");

            var context = Build(builder);

            Assert.AreSame(context.Resolve<IAlpha>(), seen);
        }

        [Test]
        public void AnInitializerRunsBeforeTheServicesOfTheSamePhase()
        {
            var builder = NewBuilder();
            var log = new BootLog();
            builder.Services.AddInstance(log);
            builder.Services.Add<RankLeaf>();
            builder.Initializers.Add(BootPhase.Awake, (c, ct) => { log.Add("step:awake"); return Task.CompletedTask; });

            Build(builder);

            AssertRanBefore(log, "step:awake", "leaf:awake:enter");
        }

        [Test]
        public void ParallelModeRunsSameRankStepsConcurrently()
        {
            var builder = NewBuilder();
            var rendezvous = new Rendezvous(2);
            builder.Services.AddInstance(rendezvous);
            builder.Services.Add<ParallelA>();
            builder.Services.Add<ParallelB>();
            builder.Initializers.Mode = StartupMode.Parallel;

            Build(builder);

            Assert.AreEqual(2, rendezvous.MaxConcurrent,
                "Two services of equal rank must awake concurrently in StartupMode.Parallel.");
        }

        [Test]
        public void SequentialIsTheDefaultStartupMode()
        {
            var builder = NewBuilder();

            Assert.AreEqual(StartupMode.Sequential, builder.Initializers.Mode);
            Assert.AreEqual(StartupMode.Sequential, default(StartupMode),
                "default(StartupMode) must be the default mode too, so a zero-initialised setting means it.");
        }

        [Test]
        public void WithinARankTheDefaultBootOrderIsRegistrationOrder()
        {
            var bFirst = new BootLog();
            var builder = NewBuilder("b-first");
            builder.Services.AddInstance(bFirst);
            builder.Services.Add<OrderedB>();
            builder.Services.Add<OrderedA>();
            Build(builder);

            var aFirst = new BootLog();
            builder = NewBuilder("a-first");
            builder.Services.AddInstance(aFirst);
            builder.Services.Add<OrderedA>();
            builder.Services.Add<OrderedB>();
            Build(builder);

            CollectionAssert.AreEqual(new[] { "b:enter", "b:exit", "a:enter", "a:exit" }, bFirst.Entries);
            CollectionAssert.AreEqual(new[] { "a:enter", "a:exit", "b:enter", "b:exit" }, aFirst.Entries);
        }

        [Test]
        public void ACollaboratorHeldThroughAMemberHasBootedWhenRegisteredFirstInTheSameRank()
        {
            // A member is not a rank edge, so only registration order puts the collaborator first - which
            // works because the default mode runs a rank one step at a time. Under Parallel the holder would
            // start while the collaborator is still awaiting.
            var builder = NewBuilder();
            builder.Services.Add<LateReady>();
            builder.Services.Add<HoldsLateReady>();

            var context = Build(builder);

            Assert.IsTrue(context.Resolve<HoldsLateReady>().SawReady);
        }

        [Test]
        public void SequentialModeNeverOverlapsTwoSteps()
        {
            var builder = NewBuilder();
            var overlap = new OverlapProbe();
            builder.Services.AddInstance(overlap);
            builder.Services.Add<SequentialA>();
            builder.Services.Add<SequentialB>();
            builder.Initializers.Mode = StartupMode.Sequential;

            Build(builder);

            Assert.AreEqual(2, overlap.Entries, "Both services must still run.");
            Assert.AreEqual(1, overlap.MaxConcurrent,
                "StartupMode.Sequential must never have two steps in flight at once.");
        }

        // ===== synchronous completion =====

        [Test]
        public void BuildAsyncCompletesSynchronouslyWhenNothingIsAsync()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();
            builder.Services.Add<AlphaHolder>();

            var task = builder.BuildAsync();

            Assert.IsTrue(task.IsCompleted,
                "With no IAwakeService, no IInitializeService and no async initializer, BuildAsync must " +
                "complete synchronously.");
            var context = task.GetAwaiter().GetResult();
            _contexts.Add(context);
            Assert.IsNotNull(context.Resolve<AlphaHolder>());
        }

        [Test]
        public void BuildAsyncStaysSynchronousWithInitializersThatCompleteSynchronously()
        {
            var builder = NewBuilder();
            var ran = false;
            builder.Initializers.Add(BootPhase.Initialize, (c, ct) => {
                ran = true;
                return Task.CompletedTask;
            }, "sync-step");

            var task = builder.BuildAsync();

            Assert.IsTrue(task.IsCompleted);
            Assert.IsTrue(ran);
            _contexts.Add(task.GetAwaiter().GetResult());
        }

        [Test]
        public void AChildContextCanBeBuiltFromASynchronousCallSite()
        {
            var parent = Build(NewBuilder("parent"));

            var childBuilder = Context.CreateBuilder(lifetime: NewLifetime("child"), parent: parent);
            childBuilder.Services.Add<Alpha>().As<IAlpha>();

            var task = childBuilder.BuildAsync();

            Assert.IsTrue(task.IsCompleted, "Opening a child scope must not require an await.");
            var child = task.GetAwaiter().GetResult();
            _contexts.Add(child);
            Assert.IsNotNull(child.Resolve<IAlpha>());
        }

        [Test]
        public void BuildAsyncIsAsynchronousWhenAServiceIsAsync()
        {
            var builder = NewBuilder();
            builder.Services.AddInstance(new BootLog());
            builder.Services.Add<SlowAwakeService>();

            var context = Build(builder);

            Assert.IsTrue(context.Resolve<SlowAwakeService>().Awoken);
        }

        // ===== single use =====

        [Test]
        public void ASecondBuildAsyncThrows()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>();
            Build(builder);

            Assert.Catch(() => {
                var second = builder.BuildAsync();
                if (second != null) second.GetAwaiter().GetResult();
            }, "A builder must be usable exactly once.");
        }

        [Test]
        public void RegisteringAServiceAfterBuildThrowsInsteadOfSilentlyDoingNothing()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>();
            Build(builder);

            Assert.Catch(() => builder.Services.Add<Beta>(),
                "A registration added after Build must throw, not be silently dropped.");
        }

        // ===== resolution primitives =====

        [Test]
        public void TryResolveReportsFalseAndYieldsNullForAnUnregisteredContract()
        {
            var context = Build(NewBuilder());

            object service;
            Assert.IsFalse(context.TryResolve(typeof(IAlpha), out service));
            Assert.IsNull(service);

            IAlpha typed;
            Assert.IsFalse(context.TryResolve(out typed));
            Assert.IsNull(typed);
        }

        [Test]
        public void TryResolveReportsTrueAndYieldsTheSingletonForARegisteredContract()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();
            var context = Build(builder);

            IAlpha typed;
            Assert.IsTrue(context.TryResolve(out typed));
            Assert.AreSame(context.Resolve<IAlpha>(), typed);

            object untyped;
            Assert.IsTrue(context.TryResolve(typeof(IAlpha), out untyped));
            Assert.AreSame(typed, untyped);
        }

        [Test]
        public void ContextIsAnIServiceProvider()
        {
            var builder = NewBuilder();
            builder.Services.Add<Alpha>().As<IAlpha>();
            var context = Build(builder);

            var provider = (IServiceProvider)context;

            Assert.AreSame(context.Resolve<IAlpha>(), provider.GetService(typeof(IAlpha)));
            Assert.IsNull(provider.GetService(typeof(IBeta)));
        }

        // ===== cost =====

        // Everything is constructed during BuildAsync, so a resolve is a dictionary lookup, a null test
        // and an array load: no lazy-construction branch, no lock, no allocation - including for a service
        // inherited from a parent, whose instance is copied into this context's table at build time.
        [Test]
        [Category("Allocation")]
        public void ResolveAllocatesNothingInTheSteadyState()
        {
            var parentBuilder = NewBuilder("parent");
            parentBuilder.Services.Add<Alpha>().As<IAlpha>();
            var parent = Build(parentBuilder);

            var childBuilder = Context.CreateBuilder(lifetime: NewLifetime("child"), parent: parent);
            childBuilder.Services.Add<Beta>().As<IBeta>();
            var child = Build(childBuilder);

            const int warmup = 2000;
            const int measured = 20000;

            for (var i = 0; i < warmup; i++)
            {
                child.Resolve<IBeta>();
                child.Resolve<IAlpha>();
            }

            long before;
            try
            {
                before = GC.GetAllocatedBytesForCurrentThread();
            }
            catch (NotImplementedException)
            {
                Assert.Ignore("GC.GetAllocatedBytesForCurrentThread is not implemented on this runtime.");
                return;
            }

            for (var i = 0; i < measured; i++)
            {
                child.Resolve<IBeta>();
                child.Resolve<IAlpha>();
            }

            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            const long tolerance = 8 * 1024;
            Assert.IsTrue(allocated <= tolerance,
                "Resolve allocated " + allocated + " bytes over " + (measured * 2) +
                " lookups; steady-state resolution must be allocation-free (tolerance " + tolerance + " B).");
        }

        // ===== fixtures =====

        public interface IAlpha { }
        public interface IBeta { }
        public interface ITag { }
        public interface IProbe { }

        public sealed class Alpha : IAlpha { }
        public sealed class OtherAlpha : IAlpha { }
        public sealed class Beta : IBeta { }
        public sealed class TwoFaced : IAlpha, IBeta { }
        public sealed class ParentTag : ITag { }
        public sealed class ChildTag : ITag { }

        public sealed class AlphaHolder
        {
            public AlphaHolder(IAlpha alpha) { Alpha = alpha; }
            public IAlpha Alpha { get; }
        }

        public sealed class TagHolder
        {
            public TagHolder(ITag tag) { Tag = tag; }
            public ITag Tag { get; }
        }

        public sealed class SingleConstructor
        {
            public SingleConstructor(IAlpha alpha) { Alpha = alpha; }
            public IAlpha Alpha { get; }
        }

        public sealed class ScopedService
        {
            public ScopedService(Lifetime lifetime) { Lifetime = lifetime; }
            public Lifetime Lifetime { get; }
        }

        public sealed class ContextAware
        {
            public ContextAware(Context context) { Context = context; }
            public Context Context { get; }
        }

        public sealed class Greedy
        {
            public Greedy() { ChosenArity = 0; }
            public Greedy(IAlpha alpha) { ChosenArity = 1; Alpha = alpha; }
            public Greedy(IAlpha alpha, IBeta beta) { ChosenArity = 2; Alpha = alpha; Beta = beta; }

            public int ChosenArity { get; }
            public IAlpha Alpha { get; }
            public IBeta Beta { get; }
        }

        public sealed class MarkedConstructor
        {
            [Inject]
            public MarkedConstructor(IAlpha alpha) { ChosenArity = 1; }
            public MarkedConstructor(IAlpha alpha, IBeta beta) { ChosenArity = 2; }

            public int ChosenArity { get; }
        }

        public sealed class HiddenConstructor
        {
            [Inject]
            private HiddenConstructor(IAlpha alpha) { Alpha = alpha; }

            public IAlpha Alpha { get; }
        }

        public sealed class MixedArguments
        {
            public MixedArguments(IAlpha alpha, string name) { Alpha = alpha; Name = name; }
            public IAlpha Alpha { get; }
            public string Name { get; }
        }

        public sealed class MemberInjectionTarget
        {
            [Inject] public IAlpha AlphaField;
            [Inject] public IBeta BetaProperty { get; set; }
            public IAlpha UnmarkedField;
        }

        public class BaseInjectionTarget
        {
            [Inject] private IAlpha _alpha;
            public IAlpha BaseAlpha => _alpha;
        }

        public sealed class DerivedInjectionTarget : BaseInjectionTarget
        {
            [Inject] public IBeta DerivedBeta { get; private set; }
        }

        public sealed class PingService
        {
            [Inject] public PongService Pong;
        }

        public sealed class PongService
        {
            [Inject] public PingService Ping;
        }

        public class DisposableProbe : IProbe, IDisposable
        {
            public bool Disposed { get; private set; }
            public void Dispose() { Disposed = true; }
        }

        public sealed class OtherDisposableProbe : IProbe, IDisposable
        {
            public bool Disposed { get; private set; }
            public void Dispose() { Disposed = true; }
        }

        public sealed class FirstDisposable : IDisposable
        {
            private readonly BootLog _log;
            public FirstDisposable(BootLog log) { _log = log; }
            public void Dispose() { _log.Add("dispose:first"); }
        }

        public sealed class SecondDisposable : IDisposable
        {
            private readonly BootLog _log;
            public SecondDisposable(BootLog log, FirstDisposable first) { _log = log; }
            public void Dispose() { _log.Add("dispose:second"); }
        }

        public sealed class BootLog
        {
            private readonly object _sync = new object();
            private readonly List<string> _entries = new List<string>();

            public void Add(string entry)
            {
                lock (_sync) _entries.Add(entry);
            }

            public List<string> Entries
            {
                get { lock (_sync) return new List<string>(_entries); }
            }
        }

        public sealed class RankLeaf : IAwakeService, IInitializeService
        {
            private readonly BootLog _log;
            public RankLeaf(BootLog log) { _log = log; }

            public async Task AwakeAsync(CancellationToken ct)
            {
                _log.Add("leaf:awake:enter");
                await Task.Yield();
                _log.Add("leaf:awake:exit");
            }

            public Task InitializeAsync(CancellationToken ct)
            {
                _log.Add("leaf:initialize");
                return Task.CompletedTask;
            }
        }

        public sealed class RankMid : IAwakeService, IInitializeService
        {
            private readonly BootLog _log;
            public RankMid(BootLog log, RankLeaf leaf) { _log = log; }

            public async Task AwakeAsync(CancellationToken ct)
            {
                _log.Add("mid:awake:enter");
                await Task.Yield();
                _log.Add("mid:awake:exit");
            }

            public Task InitializeAsync(CancellationToken ct)
            {
                _log.Add("mid:initialize");
                return Task.CompletedTask;
            }
        }

        public sealed class RankTop : IAwakeService, IInitializeService
        {
            private readonly BootLog _log;
            public RankTop(BootLog log, RankMid mid) { _log = log; }

            public async Task AwakeAsync(CancellationToken ct)
            {
                _log.Add("top:awake:enter");
                await Task.Yield();
                _log.Add("top:awake:exit");
            }

            public Task InitializeAsync(CancellationToken ct)
            {
                _log.Add("top:initialize");
                return Task.CompletedTask;
            }
        }

        public sealed class FactoryTop : IAwakeService
        {
            private readonly BootLog _log;
            public FactoryTop(RankLeaf leaf, BootLog log) { _log = log; }

            public Task AwakeAsync(CancellationToken ct)
            {
                _log.Add("factory-top:awake");
                return Task.CompletedTask;
            }
        }

        public sealed class SlowAwakeService : IAwakeService
        {
            public bool Awoken { get; private set; }

            public async Task AwakeAsync(CancellationToken ct)
            {
                await Task.Delay(1, ct).ConfigureAwait(false);
                Awoken = true;
            }
        }

        public sealed class Rendezvous
        {
            private readonly TaskCompletionSource<bool> _all =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly object _sync = new object();
            private readonly int _expected;
            private int _arrived;
            private int _current;
            private int _max;

            public Rendezvous(int expected) { _expected = expected; }

            public int MaxConcurrent { get { lock (_sync) return _max; } }

            public async Task ArriveAsync()
            {
                lock (_sync)
                {
                    _current++;
                    if (_current > _max) _max = _current;
                }

                if (Interlocked.Increment(ref _arrived) >= _expected) _all.TrySetResult(true);

                await Task.WhenAny(_all.Task, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);

                lock (_sync) _current--;
            }
        }

        public sealed class ParallelA : IAwakeService
        {
            private readonly Rendezvous _rendezvous;
            public ParallelA(Rendezvous rendezvous) { _rendezvous = rendezvous; }
            public Task AwakeAsync(CancellationToken ct) { return _rendezvous.ArriveAsync(); }
        }

        public sealed class ParallelB : IAwakeService
        {
            private readonly Rendezvous _rendezvous;
            public ParallelB(Rendezvous rendezvous) { _rendezvous = rendezvous; }
            public Task AwakeAsync(CancellationToken ct) { return _rendezvous.ArriveAsync(); }
        }

        public sealed class OverlapProbe
        {
            private readonly object _sync = new object();
            private int _current;
            private int _max;
            private int _entries;

            public int MaxConcurrent { get { lock (_sync) return _max; } }
            public int Entries { get { lock (_sync) return _entries; } }

            public async Task StepAsync()
            {
                lock (_sync)
                {
                    _entries++;
                    _current++;
                    if (_current > _max) _max = _current;
                }

                await Task.Yield();
                await Task.Yield();

                lock (_sync) _current--;
            }
        }

        public sealed class OrderedA : IAwakeService
        {
            private readonly BootLog _log;
            public OrderedA(BootLog log) { _log = log; }

            public async Task AwakeAsync(CancellationToken ct)
            {
                _log.Add("a:enter");
                await Task.Delay(20, ct); // long enough that an overlapping step would start meanwhile
                _log.Add("a:exit");
            }
        }

        public sealed class OrderedB : IAwakeService
        {
            private readonly BootLog _log;
            public OrderedB(BootLog log) { _log = log; }

            public async Task AwakeAsync(CancellationToken ct)
            {
                _log.Add("b:enter");
                await Task.Delay(20, ct); // long enough that an overlapping step would start meanwhile
                _log.Add("b:exit");
            }
        }

        public sealed class LateReady : IAwakeService
        {
            public volatile bool Ready;

            public async Task AwakeAsync(CancellationToken ct)
            {
                await Task.Delay(20, ct);
                Ready = true;
            }
        }

        public sealed class HoldsLateReady : IAwakeService
        {
            [Inject] public LateReady Collaborator;
            public bool SawReady;

            public Task AwakeAsync(CancellationToken ct)
            {
                SawReady = Collaborator.Ready;
                return Task.CompletedTask;
            }
        }

        public sealed class SequentialA : IAwakeService
        {
            private readonly OverlapProbe _probe;
            public SequentialA(OverlapProbe probe) { _probe = probe; }
            public Task AwakeAsync(CancellationToken ct) { return _probe.StepAsync(); }
        }

        public sealed class SequentialB : IAwakeService
        {
            private readonly OverlapProbe _probe;
            public SequentialB(OverlapProbe probe) { _probe = probe; }
            public Task AwakeAsync(CancellationToken ct) { return _probe.StepAsync(); }
        }
    }
}
