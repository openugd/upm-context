using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace OpenUGD.Tests
{
    /// <summary>
    /// `[Inject(Optional = true)]` — the one deliberate hole in "a missing binding is an error". These tests
    /// pin down that it is a hole of exactly the declared size: the member is left alone, nothing else
    /// changes, and it cannot be applied where the absence would be invisible.
    /// </summary>
    [TestFixture]
    public class ContextOptionalInjectionTests
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

        private static ContextException FailToBuild(ContextBuilder builder)
        {
            var exception = Assert.Catch(() => RunSync(() => builder.BuildAsync()));
            Assert.IsInstanceOf<ContextException>(exception,
                "Expected ContextException but got: " + exception);
            return (ContextException)exception;
        }

        // ===== the service graph =====

        [Test]
        public void AnUnresolvableOptionalMemberDoesNotFailTheBuild()
        {
            var builder = NewBuilder();
            builder.Services.Add<Greeter>();

            var greeter = Build(builder).Resolve<Greeter>();

            Assert.IsNull(greeter.Localization);
        }

        [Test]
        public void AnUnresolvableRequiredMemberStillFailsTheBuild()
        {
            var builder = NewBuilder();
            builder.Services.Add<StrictGreeter>();

            var error = FailToBuild(builder);

            StringAssert.Contains("Unable to resolve service for type", error.Message);
            StringAssert.Contains("ILocalization", error.Message);
        }

        [Test]
        public void AResolvableOptionalMemberIsInjectedLikeAnyOther()
        {
            var builder = NewBuilder();
            builder.Services.Add<EnglishLocalization>().As<ILocalization>();
            builder.Services.Add<Greeter>();

            var greeter = Build(builder).Resolve<Greeter>();

            Assert.IsInstanceOf<EnglishLocalization>(greeter.Localization);
        }

        [Test]
        public void AnOptionalMemberKeepsWhateverItAlreadyHeld()
        {
            var builder = NewBuilder();
            builder.Services.Add<GreeterWithFallback>();

            var greeter = Build(builder).Resolve<GreeterWithFallback>();

            Assert.AreSame(EnglishLocalization.Instance, greeter.Localization,
                "An unsatisfied optional member must be left untouched, so a field initializer survives.");
        }

        [Test]
        public void AResolvableOptionalMemberOverwritesItsFallback()
        {
            var builder = NewBuilder();
            builder.Services.Add<SwedishLocalization>().As<ILocalization>();
            builder.Services.Add<GreeterWithFallback>();

            var greeter = Build(builder).Resolve<GreeterWithFallback>();

            Assert.IsInstanceOf<SwedishLocalization>(greeter.Localization);
        }

        [Test]
        public void AnOptionalPropertyBehavesLikeAnOptionalField()
        {
            var builder = NewBuilder();
            builder.Services.Add<GreeterByProperty>();

            var greeter = Build(builder).Resolve<GreeterByProperty>();

            Assert.IsNull(greeter.Localization);
        }

        [Test]
        public void AnOptionalMemberDeclaredOnABaseClassIsStillOptional()
        {
            var builder = NewBuilder();
            builder.Services.Add<DerivedGreeter>();

            var greeter = Build(builder).Resolve<DerivedGreeter>();

            Assert.IsNull(greeter.Localization);
        }

        [Test]
        public void AnOptionalMemberIsSatisfiedFromTheParentContext()
        {
            var parentBuilder = NewBuilder("parent");
            parentBuilder.Services.Add<EnglishLocalization>().As<ILocalization>();
            var parent = Build(parentBuilder);

            var childBuilder = Context.CreateBuilder(NewDefinition("child").Lifetime, parent);
            childBuilder.Services.Add<Greeter>();
            var child = Build(childBuilder);

            Assert.IsInstanceOf<EnglishLocalization>(child.Resolve<Greeter>().Localization);
        }

        [Test]
        public void OneOptionalMemberDoesNotSuppressAnotherRequiredOne()
        {
            var builder = NewBuilder();
            builder.Services.Add<MixedGreeter>();

            var error = FailToBuild(builder);

            StringAssert.Contains("IClock", error.Message);
            StringAssert.DoesNotContain("ILocalization", error.Message,
                "The optional member must not be reported as a missing binding.");
        }

        // ===== Context.Inject, the path widgets and MonoBehaviours take =====

        [Test]
        public void InjectLeavesAnUnresolvableOptionalMemberAlone()
        {
            var context = Build(NewBuilder());
            var target = new GreeterWithFallback();

            Assert.DoesNotThrow(() => context.Inject(target));
            Assert.AreSame(EnglishLocalization.Instance, target.Localization);
        }

        [Test]
        public void InjectStillThrowsForAnUnresolvableRequiredMember()
        {
            var context = Build(NewBuilder());

            var error = Assert.Throws<ContextException>(() => context.Inject(new StrictGreeter()));

            StringAssert.Contains("ILocalization", error.Message);
        }

        [Test]
        public void InjectAssignsAnOptionalMemberWhenItIsRegistered()
        {
            var builder = NewBuilder();
            builder.Services.Add<EnglishLocalization>().As<ILocalization>();
            var context = Build(builder);

            var target = new Greeter();
            context.Inject(target);

            Assert.IsInstanceOf<EnglishLocalization>(target.Localization);
        }

        [Test]
        public void InstantiateAppliesOptionalMemberInjection()
        {
            var context = Build(NewBuilder());

            var greeter = context.Instantiate<Greeter>();

            Assert.IsNull(greeter.Localization);
        }

        // ===== where Optional is refused =====

        [Test]
        public void OptionalOnANonNullableValueTypeIsAnError()
        {
            var builder = NewBuilder();
            builder.Services.Add<OptionalIntMember>();

            var error = FailToBuild(builder);

            StringAssert.Contains("non-nullable value type", error.Message);
            StringAssert.Contains("_retries", error.Message);
        }

        [Test]
        public void OptionalOnANullableValueTypeIsAllowed()
        {
            var builder = NewBuilder();
            builder.Services.Add<OptionalNullableIntMember>();

            var target = Build(builder).Resolve<OptionalNullableIntMember>();

            Assert.IsFalse(target.Retries.HasValue);
        }

        [Test]
        public void OptionalOnAConstructorIsAnError()
        {
            var builder = NewBuilder();
            builder.Services.Add<OptionalConstructor>();

            var error = FailToBuild(builder);

            StringAssert.Contains("has no meaning on a constructor", error.Message);
        }

        [Test]
        public void OptionalDoesNotExcuseAReadonlyField()
        {
            var builder = NewBuilder();
            builder.Services.Add<OptionalReadonlyMember>();

            var error = FailToBuild(builder);

            StringAssert.Contains("readonly", error.Message);
        }

        // ===== constructors need no Optional: a second constructor already says it =====

        [Test]
        public void TheNarrowConstructorIsChosenWhenTheDependencyIsMissing()
        {
            var builder = NewBuilder();
            builder.Services.Add<SystemClock>().As<IClock>();
            builder.Services.Add<Overloaded>();

            var overloaded = Build(builder).Resolve<Overloaded>();

            Assert.IsNull(overloaded.Localization);
        }

        [Test]
        public void TheWideConstructorIsChosenWhenTheDependencyIsRegistered()
        {
            var builder = NewBuilder();
            builder.Services.Add<SystemClock>().As<IClock>();
            builder.Services.Add<EnglishLocalization>().As<ILocalization>();
            builder.Services.Add<Overloaded>();

            var overloaded = Build(builder).Resolve<Overloaded>();

            Assert.IsInstanceOf<EnglishLocalization>(overloaded.Localization);
        }

        // ===== fixtures =====

        public interface ILocalization
        {
            string Get(string key);
        }

        public interface IClock
        {
            DateTime Now { get; }
        }

        public class EnglishLocalization : ILocalization
        {
            public static readonly EnglishLocalization Instance = new EnglishLocalization();
            public string Get(string key) => key;
        }

        public class SwedishLocalization : ILocalization
        {
            public string Get(string key) => key;
        }

        public class SystemClock : IClock
        {
            public DateTime Now => DateTime.UtcNow;
        }

        public class Greeter
        {
            [Inject(Optional = true)] private ILocalization _localization;

            public ILocalization Localization => _localization;
        }

        public class StrictGreeter
        {
            [Inject] private ILocalization _localization;

            public ILocalization Localization => _localization;
        }

        public class GreeterWithFallback
        {
            [Inject(Optional = true)] private ILocalization _localization = EnglishLocalization.Instance;

            public ILocalization Localization => _localization;
        }

        public class GreeterByProperty
        {
            [Inject(Optional = true)] public ILocalization Localization { get; private set; }
        }

        public class BaseGreeter
        {
            [Inject(Optional = true)] private ILocalization _localization;

            public ILocalization Localization => _localization;
        }

        public class DerivedGreeter : BaseGreeter
        {
        }

        public class MixedGreeter
        {
            [Inject(Optional = true)] private ILocalization _localization;
            [Inject] private IClock _clock;

            public ILocalization Localization => _localization;
            public IClock Clock => _clock;
        }

        public class OptionalIntMember
        {
            [Inject(Optional = true)] private int _retries;

            public int Retries => _retries;
        }

        public class OptionalNullableIntMember
        {
            [Inject(Optional = true)] private int? _retries;

            public int? Retries => _retries;
        }

        public class OptionalReadonlyMember
        {
            [Inject(Optional = true)] private readonly ILocalization _localization = null;

            public ILocalization Localization => _localization;
        }

        public class OptionalConstructor
        {
            [Inject(Optional = true)]
            public OptionalConstructor()
            {
            }
        }

        public class Overloaded
        {
            public Overloaded(IClock clock) : this(clock, null)
            {
            }

            public Overloaded(IClock clock, ILocalization localization)
            {
                Clock = clock;
                Localization = localization;
            }

            public IClock Clock { get; }
            public ILocalization Localization { get; }
        }
    }
}
