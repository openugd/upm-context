using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace OpenUGD.Tests
{
    /// <summary>
    /// Several modules contributing to one thing (decision 2d): <c>AsElementOf&lt;T&gt;</c> contributions
    /// resolve only as <c>IReadOnlyList&lt;T&gt;</c>, in registration order, local to the context, empty when
    /// none, built after every element.
    /// </summary>
    [TestFixture]
    public class ContextCollectionTests : ContextFixture
    {
        [Test]
        public void ContributionsResolveAsOneListInRegistrationOrder()
        {
            var builder = NewBuilder();
            builder.Services.Add<Menu>();
            builder.Services.Add<FpsPanel>().AsElementOf<IPanel>();
            builder.Services.Add<MemoryPanel>().AsElementOf<IPanel>();
            var context = Build(builder);

            var panels = context.Resolve<Menu>().Panels;

            CollectionAssert.AreEqual(new IPanel[] { context.Resolve<FpsPanel>(), context.Resolve<MemoryPanel>() },
                panels);
            Assert.AreSame(panels, context.Resolve<IReadOnlyList<IPanel>>(), "Built once, handed to everyone.");
            Assert.AreSame(panels, context.Resolve<Menu>().Member, "An [Inject] member gets the same list.");
        }

        [Test]
        public void TheElementTypeItselfStaysUnregistered()
        {
            var builder = NewBuilder();
            builder.Services.Add<FpsPanel>().AsElementOf<IPanel>();
            var context = Build(builder);

            IPanel panel;
            Assert.IsFalse(context.TryResolve(out panel), "A contribution claims no single contract.");
        }

        [Test]
        public void AListNothingContributesToIsEmpty()
        {
            var builder = NewBuilder();
            builder.Services.Add<Menu>();
            var context = Build(builder);

            Assert.AreEqual(0, context.Resolve<Menu>().Panels.Count);
            Assert.AreEqual(0, context.Resolve<IReadOnlyList<Menu>>().Count, "Even one nobody asked for.");
        }

        [Test]
        public void AListHoldsOnlyItsOwnContextsContributions()
        {
            var parentBuilder = NewBuilder("parent");
            parentBuilder.Services.Add<FpsPanel>().AsElementOf<IPanel>();
            var parent = Build(parentBuilder);

            var child = Context.CreateBuilder(parent: parent);
            child.Services.Add<MemoryPanel>().AsElementOf<IPanel>();
            child.Services.Add<Menu>();
            var built = Build(child);
            var empty = Build(Context.CreateBuilder(parent: parent));

            CollectionAssert.AreEqual(new IPanel[] { built.Resolve<MemoryPanel>() }, built.Resolve<Menu>().Panels);
            Assert.AreEqual(0, empty.Resolve<IReadOnlyList<IPanel>>().Count, "A child never sees its parent's list.");
            Assert.AreEqual(1, parent.Resolve<IReadOnlyList<IPanel>>().Count);
        }

        [TestCase(StartupMode.Sequential)]
        [TestCase(StartupMode.Parallel)]
        public void WhateverTakesTheListBootsAfterEveryElement(StartupMode mode)
        {
            var log = new Log();
            var builder = NewBuilder();
            builder.Initializers.Mode = mode;
            builder.Services.AddInstance(log);
            builder.Services.Add<BootingMenu>();
            builder.Services.Add<BootingPanel>().AsElementOf<IPanel>();
            builder.Services.Add<OtherBootingPanel>().AsElementOf<IPanel>();

            Build(builder);

            AssertRanBefore(log, "panel", "menu");
            AssertRanBefore(log, "other", "menu");
        }

        [Test]
        public void AnElementThatTakesItsOwnListIsACycle()
        {
            var builder = NewBuilder();
            builder.Services.Add<FpsPanel>().AsElementOf<IPanel>();
            builder.Services.Add<NeedsPanels>().AsElementOf<IPanel>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("circular dependency", error.Message);
            CollectionAssert.Contains(error.Path, typeof(NeedsPanels));
        }

        [Test]
        public void AnElementWhoseFactoryResolvesItsOwnListIsACycle()
        {
            var builder = NewBuilder();
            builder.Services.Add<FpsPanel>(c => {
                c.Resolve<IReadOnlyList<IPanel>>();
                return new FpsPanel();
            }).AsElementOf<IPanel>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("circular dependency", error.Message,
                "A factory hides the edge from validation; construction still catches it.");
            StringAssert.Contains("the factory registered for '" + typeof(FpsPanel).FullName + "' resolves",
                error.Message);
            CollectionAssert.AreEqual(new[] { typeof(FpsPanel), typeof(IReadOnlyList<IPanel>), typeof(FpsPanel) },
                error.Path);
        }

        [Test]
        public void WhateverHoldsTheListThroughAMemberBootsAfterEveryElement()
        {
            var log = new Log();
            var builder = NewBuilder();
            builder.Services.AddInstance(log);
            builder.Services.Add<BootingHolder>(); // registered first: only the member edge orders it last
            builder.Services.Add<BootingPanel>().AsElementOf<IPanel>();
            builder.Services.Add<OtherBootingPanel>().AsElementOf<IPanel>();

            Build(builder);

            AssertRanBefore(log, "panel", "holder");
            AssertRanBefore(log, "other", "holder");
        }

        [Test]
        public void AParentsListDoesNotCountAsAvailableInAChild()
        {
            var parentBuilder = NewBuilder("parent");
            parentBuilder.Services.Add<FpsPanel>().AsElementOf<IPanel>();
            var parent = Build(parentBuilder);

            var child = Context.CreateBuilder(parent: parent);

            Assert.IsFalse(child.Services.Contains(typeof(IReadOnlyList<IPanel>)),
                "The parent's list is local to the parent, so TryAdd in the child must not count on it.");
            Assert.IsTrue(child.Services.Contains(typeof(FpsPanel)), "An ordinary registration is still inherited.");
        }

        [Test]
        public void AContributionTheElementTypeDoesNotFitIsAValidationError()
        {
            var builder = NewBuilder();
            builder.Services.Add<Menu>().AsElementOf(typeof(IPanel));
            builder.Services.Add<FpsPanel>().AsElementOf(typeof(int));

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("cannot be an element of '" + typeof(IPanel).FullName + "': it does not implement",
                error.Message);
            StringAssert.Contains("cannot be an element of 'System.Int32': it is a value type", error.Message);
        }

        [Test]
        public void RegisteringTheListItselfAsWellIsAValidationError()
        {
            var builder = NewBuilder();
            builder.Services.AddInstance<IReadOnlyList<IPanel>>(new IPanel[0]);
            builder.Services.Add<FpsPanel>().AsElementOf<IPanel>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("is also registered as an ordinary contract", error.Message);
        }

        [Test]
        public void ASuggestionNeverAsksToAddAsToACollection()
        {
            var builder = NewBuilder();
            builder.Services.Add<FpsPanel>().AsElementOf<IPanel>();
            builder.Services.Add<Menu>();
            builder.Services.Add<NeedsCollection>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("IReadOnlyCollection", error.Message);
            StringAssert.DoesNotContain(".As<", error.Message, "A collection has no registration to add .As<> to.");
        }

        public interface IPanel { }

        public sealed class NeedsCollection
        {
            public NeedsCollection(IReadOnlyCollection<IPanel> panels) { }
        }

        public sealed class FpsPanel : IPanel { }

        public sealed class MemoryPanel : IPanel { }

        public sealed class Menu
        {
            [Inject] public IReadOnlyList<IPanel> Member;
            public readonly IReadOnlyList<IPanel> Panels;
            public Menu(IReadOnlyList<IPanel> panels) { Panels = panels; }
        }

        public sealed class NeedsPanels : IPanel
        {
            public NeedsPanels(IReadOnlyList<IPanel> panels) { }
        }

        public abstract class Booting : IAwakeService
        {
            private readonly Log _log;
            private readonly string _name;
            protected Booting(Log log, string name) { _log = log; _name = name; }

            public async Task AwakeAsync(CancellationToken cancellationToken)
            {
                await Task.Yield();
                _log.Add(_name);
            }
        }

        public sealed class BootingMenu : Booting
        {
            public BootingMenu(Log log, IReadOnlyList<IPanel> panels) : base(log, "menu") { }
        }

        public sealed class BootingHolder : Booting
        {
            [Inject] public IReadOnlyList<IPanel> Panels;
            public BootingHolder(Log log) : base(log, "holder") { }
        }

        public sealed class BootingPanel : Booting, IPanel
        {
            public BootingPanel(Log log) : base(log, "panel") { }
        }

        public sealed class OtherBootingPanel : Booting, IPanel
        {
            public OtherBootingPanel(Log log) : base(log, "other") { }
        }
    }
}
