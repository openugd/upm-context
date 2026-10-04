using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace OpenUGD.Tests
{
    /// <summary>
    /// Ownership goes by the object, not by the registration: an object that answers to several
    /// registrations is injected, booted and disposed at most once, an object handed to <c>AddInstance</c> is
    /// never disposed, and a child never disposes, injects or boots an object its parent holds.
    /// </summary>
    [TestFixture]
    public class ContextOwnershipTests : ContextFixture
    {
        private static ContextBuilder Register(ContextBuilder builder)
        {
            builder.Services.Add<ParentTag>().As<ITag>();
            return builder;
        }

        [Test]
        public void AForwardingFactoryDoesNotInjectBootOrDisposeItsTargetASecondTime()
        {
            var builder = Register(NewBuilder());
            builder.Services.Add<Counted>();
            builder.Services.Add<ICounted>(c => c.Resolve<Counted>());
            var context = Build(builder);

            var counted = context.Resolve<Counted>();
            Assert.AreSame(counted, context.Resolve<ICounted>());
            AssertOnce(counted);

            context.Dispose();

            Assert.AreEqual(1, counted.Disposed, "One object, one Dispose - however many registrations name it.");
        }

        [Test]
        public void AForwardingFactoryRegisteredBeforeItsTargetChangesNothing()
        {
            var builder = Register(NewBuilder());
            builder.Services.Add<ICounted>(c => c.Resolve<Counted>());
            builder.Services.Add<Counted>();
            var context = Build(builder);

            var counted = context.Resolve<Counted>();
            AssertOnce(counted);

            context.Dispose();

            Assert.AreEqual(1, counted.Disposed);
        }

        [Test]
        public void OneObjectHandedToAddInstanceTwiceIsInjectedAndBootedOnceAndNeverDisposed()
        {
            var counted = new Counted();
            var builder = Register(NewBuilder());
            builder.Services.AddInstance<ICounted>(counted);
            builder.Services.AddInstance(counted);
            var context = Build(builder);

            AssertOnce(counted);

            context.Dispose();

            Assert.AreEqual(0, counted.Disposed, "An AddInstance object belongs to the caller.");
        }

        [Test]
        public void AFactoryReturningAnAddInstanceObjectDoesNotMakeTheContextItsOwner()
        {
            var counted = new Counted();
            var builder = Register(NewBuilder());
            builder.Services.AddInstance(counted);
            builder.Services.Add<ICounted>(c => c.Resolve<Counted>());
            var context = Build(builder);

            AssertOnce(counted);

            context.Dispose();

            Assert.AreEqual(0, counted.Disposed);
        }

        [Test]
        public void AFactoryRegisteredBeforeTheAddInstanceItReturnsDoesNotOwnItEither()
        {
            var counted = new Counted();
            var builder = Register(NewBuilder());
            builder.Services.Add<ICounted>(c => counted);
            builder.Services.AddInstance(counted);
            var context = Build(builder);

            AssertOnce(counted);

            context.Dispose();

            Assert.AreEqual(0, counted.Disposed,
                "The AddInstance registration decides ownership, wherever it sits in the registrations.");
        }

        [Test]
        public void TwoFactoriesReturningOneObjectDisposeItOnce()
        {
            var shared = new Counted();
            var builder = Register(NewBuilder());
            builder.Services.Add<ICounted>(c => shared);
            builder.Services.Add<ICountedAlias>(c => shared);
            var context = Build(builder);

            AssertOnce(shared);

            context.Dispose();

            Assert.AreEqual(1, shared.Disposed, "A factory made it, so the context owns it - once.");
        }

        [Test]
        public void AChildNeverDisposesInjectsOrBootsAnObjectItsParentHolds()
        {
            var parentBuilder = Register(NewBuilder("parent"));
            parentBuilder.Services.Add<Counted>();
            var parent = Build(parentBuilder);
            var counted = parent.Resolve<Counted>();
            AssertOnce(counted);

            var childBuilder = Context.CreateBuilder(NewDefinition("child").Lifetime, parent);
            childBuilder.Services.Add<ChildTag>().As<ITag>();                        // shadows the parent's
            childBuilder.Services.AddInstance<ICounted>(counted);                    // handed over again
            childBuilder.Services.Add<ICountedAlias>(c => counted);                  // returned by a factory
            childBuilder.Services.Add<ICountedThird>(c => c.Resolve<Counted>());     // resolved as inherited
            var child = Build(childBuilder);

            Assert.AreSame(counted, child.Resolve<ICounted>());
            Assert.AreSame(counted, child.Resolve<ICountedAlias>());
            Assert.AreSame(counted, child.Resolve<ICountedThird>());
            AssertOnce(counted);
            Assert.IsInstanceOf<ParentTag>(counted.Tag,
                "Re-injecting the parent's object would have pointed it at the child's ITag, which dies " +
                "with the child.");

            child.Dispose();

            Assert.AreEqual(0, counted.Disposed, "The parent's object outlives the child.");

            parent.Dispose();

            Assert.AreEqual(1, counted.Disposed, "...and dies, once, with the parent.");
        }

        [Test]
        public void AChildNeverDisposesAnObjectAGrandparentHoldsEvenWhenItsParentShadowsIt()
        {
            var grandparentBuilder = Register(NewBuilder("grandparent"));
            grandparentBuilder.Services.Add<Counted>().As<ICounted>();
            var grandparent = Build(grandparentBuilder);
            var counted = grandparent.Resolve<Counted>();

            // The parent shadows every contract the grandparent's object answers to, so that object is
            // nowhere in the parent's own table.
            var parentBuilder = Context.CreateBuilder(NewDefinition("parent").Lifetime, grandparent);
            parentBuilder.Services.AddInstance(new Counted()).As<ICounted>();
            var parent = Build(parentBuilder);

            var childBuilder = Context.CreateBuilder(NewDefinition("child").Lifetime, parent);
            childBuilder.Services.Add<ICountedAlias>(c => counted);
            var child = Build(childBuilder);

            Assert.AreSame(counted, child.Resolve<ICountedAlias>());
            AssertOnce(counted);

            child.Dispose();

            Assert.AreEqual(0, counted.Disposed);
        }

        private static void AssertOnce(Counted counted)
        {
            Assert.AreEqual(1, counted.Injected, "injected once");
            Assert.AreEqual(1, counted.Awoken, "awoken once");
            Assert.AreEqual(1, counted.Initialized, "initialized once");
        }

        // ===== fixtures =====

        public interface ITag { }
        public interface ICounted { }
        public interface ICountedAlias { }
        public interface ICountedThird { }

        public sealed class ParentTag : ITag { }
        public sealed class ChildTag : ITag { }

        public sealed class Counted : ICounted, ICountedAlias, ICountedThird, IDisposable, IAwakeService,
            IInitializeService
        {
            private ITag _tag;

            public int Injected;
            public int Awoken;
            public int Initialized;
            public int Disposed;

            [Inject]
            public ITag Tag
            {
                get => _tag;
                set
                {
                    _tag = value;
                    Injected++;
                }
            }

            public Task AwakeAsync(CancellationToken cancellationToken)
            {
                Awoken++;
                return Task.CompletedTask;
            }

            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                Initialized++;
                return Task.CompletedTask;
            }

            public void Dispose() => Disposed++;
        }
    }
}
