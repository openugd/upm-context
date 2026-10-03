using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace OpenUGD.Tests
{
    /// <summary>
    /// The boot: what counts towards a step's rank, how explicit steps are ordered, and how a failing or
    /// cancelled step is reported.
    /// </summary>
    [TestFixture]
    public class ContextBootTests : ContextFixture
    {
        // ===== a step's own cancellation (CX-12) =====

        [Test]
        public void AStepCancelledByATokenOfItsOwnFailsTheBuildInsteadOfCancellingIt()
        {
            var builder = NewBuilder();
            builder.Initializers.Add(BootPhase.Awake, async (c, ct) => {
                using (var timeout = new CancellationTokenSource())
                {
                    timeout.Cancel();
                    await Task.Delay(5000, timeout.Token);
                }
            }, "load-with-timeout");

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains("load-with-timeout", error.Message);
            StringAssert.Contains("Awake", error.Message);
            Assert.IsInstanceOf<OperationCanceledException>(error.InnerException,
                "The step's own cancellation is kept as the InnerException.");
            Assert.IsTrue(builder.Lifetime.IsTerminated, "A failed build still tears down.");
        }

        [Test]
        public void AServiceThrowingOperationCanceledWhileTheBuildIsNotCancelledIsNamed()
        {
            var builder = NewBuilder();
            builder.Services.Add<GivesUp>();

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains(typeof(GivesUp).Name + ".InitializeAsync", error.Message);
            Assert.IsInstanceOf<OperationCanceledException>(error.InnerException);
        }

        [Test]
        public void AStepObservingTheBuildTokenStillCancelsTheBuild()
        {
            var builder = NewBuilder();
            var source = new CancellationTokenSource();
            builder.Initializers.Add(BootPhase.Awake, async (c, ct) => {
                source.Cancel();
                await Task.Delay(5000, ct);
            }, "observes-the-build-token");

            var error = FailToBuild<OperationCanceledException>(builder, source.Token);

            Assert.IsNotInstanceOf<ContextException>(error);
        }

        // ===== several concurrent failures (CX-11) =====

        [Test]
        public void EveryFailingStepOfAParallelRankIsReported()
        {
            var builder = NewBuilder();
            builder.Services.Add<FailsFirst>();
            builder.Services.Add<FailsSecond>();
            builder.Services.Add<Succeeds>();
            builder.Initializers.Mode = StartupMode.Parallel;

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains(typeof(FailsFirst).Name + ".AwakeAsync", error.Message);
            StringAssert.Contains(typeof(FailsSecond).Name + ".AwakeAsync", error.Message);
            StringAssert.Contains("first failed", error.Message);
            StringAssert.Contains("second failed", error.Message);

            var all = error.InnerException as AggregateException;
            Assert.IsNotNull(all, "Several failures arrive as an AggregateException under the ContextException.");
            Assert.AreEqual(2, all.InnerExceptions.Count);

            var first = all.InnerExceptions[0] as ContextException;
            var second = all.InnerExceptions[1] as ContextException;
            Assert.IsNotNull(first, "Each step keeps the ContextException that names it.");
            Assert.IsNotNull(second);
            StringAssert.Contains(typeof(FailsFirst).Name, first.Message, "Boot order: registration order.");
            StringAssert.Contains(typeof(FailsSecond).Name, second.Message);
            Assert.AreEqual("first failed", first.InnerException.Message);
            Assert.AreEqual("second failed", second.InnerException.Message);
            Assert.IsTrue(builder.Lifetime.IsTerminated);
        }

        [Test]
        public void ASingleFailureInAParallelRankIsReportedAsItself()
        {
            var builder = NewBuilder();
            builder.Services.Add<Succeeds>();
            builder.Services.Add<FailsSecond>();
            builder.Initializers.Mode = StartupMode.Parallel;

            var error = FailToBuild<ContextException>(builder);

            StringAssert.Contains(typeof(FailsSecond).Name + ".AwakeAsync", error.Message);
            Assert.IsInstanceOf<InvalidOperationException>(error.InnerException,
                "One failure is not wrapped in an aggregate: its InnerException is the step's own exception.");
        }

        // ===== explicit steps (CX-10) =====

        [Test]
        public void ExplicitStepsRunOneAtATimeInAddOrderEvenUnderParallel()
        {
            var builder = NewBuilder();
            var log = new Log();
            builder.Initializers.Mode = StartupMode.Parallel;
            builder.Initializers
                .Add(BootPhase.Awake, async (c, ct) => {
                    log.Add("first:enter");
                    await Task.Delay(20, ct); // long enough that an overlapping step would start meanwhile
                    log.Add("first:exit");
                }, "first")
                .Add(BootPhase.Awake, async (c, ct) => {
                    log.Add("second:enter");
                    await Task.Delay(20, ct);
                    log.Add("second:exit");
                }, "second")
                .Add(BootPhase.Awake, (c, ct) => {
                    log.Add("third");
                    return Task.CompletedTask;
                }, "third");

            Build(builder);

            CollectionAssert.AreEqual(
                new[] { "first:enter", "first:exit", "second:enter", "second:exit", "third" }, log.Entries,
                "A later explicit step may rely on an earlier one, so they never overlap.");
        }

        [Test]
        public void ExplicitStepsStillRunAheadOfEveryServiceOfTheirPhase()
        {
            var builder = NewBuilder();
            var log = new Log();
            builder.Services.AddInstance(log);
            builder.Services.Add<LogsAwake>();
            builder.Initializers.Mode = StartupMode.Parallel;
            builder.Initializers
                .Add(BootPhase.Awake, async (c, ct) => {
                    await Task.Yield();
                    log.Add("step:a");
                }, "a")
                .Add(BootPhase.Awake, (c, ct) => {
                    log.Add("step:b");
                    return Task.CompletedTask;
                }, "b");

            Build(builder);

            CollectionAssert.AreEqual(new[] { "step:a", "step:b", "service" }, log.Entries);
        }

        // ===== what counts towards rank (CX-9) =====

        [Test]
        public void AServiceBootsAfterACollaboratorItHoldsThroughAMemberWhateverTheOrder(
            [Values(StartupMode.Sequential, StartupMode.Parallel)] StartupMode mode)
        {
            var builder = NewBuilder();
            builder.Initializers.Mode = mode;
            builder.Services.Add<HoldsLateReady>(); // registered first, and it holds the other through a member
            builder.Services.Add<LateReady>();

            var context = Build(builder);

            Assert.IsTrue(context.Resolve<HoldsLateReady>().SawReady,
                "The holder must not boot before what it holds through an [Inject] member.");
        }

        [Test]
        public void AnAddInstanceObjectBootsAfterWhatItHoldsThroughAMember()
        {
            var holder = new HoldsLateReady();
            var builder = NewBuilder();
            builder.Services.AddInstance(holder);
            builder.Services.Add<LateReady>();

            Build(builder);

            Assert.IsTrue(holder.SawReady);
        }

        [Test]
        public void AServiceBootsAfterAnAddInstanceObjectItHoldsThroughAMember()
        {
            var late = new LateReady();
            var builder = NewBuilder();
            builder.Services.Add<HoldsLateReady>();
            builder.Services.AddInstance(late);

            var context = Build(builder);

            Assert.IsTrue(late.Ready);
            Assert.IsTrue(context.Resolve<HoldsLateReady>().SawReady);
        }

        [Test]
        public void AMemberCycleSharesOneRankAfterEverythingItDependsOn()
        {
            foreach (var bFirst in new[] { true, false })
            {
                var log = new Log();
                var builder = NewBuilder(bFirst ? "b-first" : "a-first");
                builder.Services.AddInstance(log);
                if (bFirst) builder.Services.Add<CycleB>();
                builder.Services.Add<CycleA>();
                if (!bFirst) builder.Services.Add<CycleB>();
                builder.Services.Add<CycleLeaf>(); // registered last; only CycleA holds it

                Build(builder);

                // Both wait for what one of them holds outside the cycle, then go in registration order.
                CollectionAssert.AreEqual(
                    bFirst
                        ? new[] { "leaf:enter", "leaf:exit", "b", "a" }
                        : new[] { "leaf:enter", "leaf:exit", "a", "b" },
                    log.Entries, (bFirst ? "B registered first" : "A registered first") + ": " + log);
            }
        }

        [Test]
        public void AConstructorDependencyInsideAMemberCycleStillBootsFirst()
        {
            var log = new Log();
            var builder = NewBuilder();
            builder.Services.AddInstance(log);
            builder.Services.Add<BuiltFromInner>(); // takes Inner in its constructor; Inner holds it back
            builder.Services.Add<Inner>();

            Build(builder);

            CollectionAssert.AreEqual(new[] { "inner:enter", "inner:exit", "outer" }, log.Entries);
        }

        [Test]
        public void AForwardingFactoryBootsItsTargetAtTheTargetsOwnRank()
        {
            // The forwarding registration ranks one above its target, but the object is the target's: it must
            // boot where the target ranks, ahead of whatever takes the target, not alongside it.
            var log = new Log();
            var builder = NewBuilder();
            builder.Services.AddInstance(log);
            builder.Services.Add<IForwarded>(c => c.Resolve<Forwarded>());
            builder.Services.Add<NeedsForwarded>();
            builder.Services.Add<Forwarded>();
            builder.Initializers.Mode = StartupMode.Parallel;

            Build(builder);

            CollectionAssert.AreEqual(new[] { "forwarded:enter", "forwarded:exit", "needs" }, log.Entries);
        }

        // ===== fixtures =====

        public sealed class FailsFirst : IAwakeService
        {
            public async Task AwakeAsync(CancellationToken cancellationToken)
            {
                await Task.Yield();
                throw new InvalidOperationException("first failed");
            }
        }

        public sealed class FailsSecond : IAwakeService
        {
            public async Task AwakeAsync(CancellationToken cancellationToken)
            {
                await Task.Yield();
                throw new InvalidOperationException("second failed");
            }
        }

        public sealed class Succeeds : IAwakeService
        {
            public async Task AwakeAsync(CancellationToken cancellationToken)
            {
                await Task.Yield();
            }
        }

        public sealed class LogsAwake : IAwakeService
        {
            private readonly Log _log;
            public LogsAwake(Log log) { _log = log; }

            public Task AwakeAsync(CancellationToken cancellationToken)
            {
                _log.Add("service");
                return Task.CompletedTask;
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

        public sealed class CycleLeaf : IAwakeService
        {
            [Inject] public Log Log;

            public async Task AwakeAsync(CancellationToken ct)
            {
                Log.Add("leaf:enter");
                await Task.Delay(20, ct);
                Log.Add("leaf:exit");
            }
        }

        public sealed class CycleA : IAwakeService
        {
            [Inject] public Log Log;
            [Inject] public CycleB B;
            [Inject] public CycleLeaf Leaf;

            public Task AwakeAsync(CancellationToken ct)
            {
                Log.Add("a");
                return Task.CompletedTask;
            }
        }

        public sealed class CycleB : IAwakeService
        {
            [Inject] public Log Log;
            [Inject] public CycleA A;

            public Task AwakeAsync(CancellationToken ct)
            {
                Log.Add("b");
                return Task.CompletedTask;
            }
        }

        public sealed class Inner : IAwakeService
        {
            [Inject] public Log Log;
            [Inject] public BuiltFromInner Outer;

            public async Task AwakeAsync(CancellationToken ct)
            {
                Log.Add("inner:enter");
                await Task.Delay(20, ct);
                Log.Add("inner:exit");
            }
        }

        public sealed class BuiltFromInner : IAwakeService
        {
            private readonly Log _log;
            public BuiltFromInner(Inner inner, Log log) { _log = log; }

            public Task AwakeAsync(CancellationToken ct)
            {
                _log.Add("outer");
                return Task.CompletedTask;
            }
        }

        public interface IForwarded { }

        public sealed class Forwarded : IForwarded, IAwakeService
        {
            private readonly Log _log;
            public Forwarded(Log log) { _log = log; }

            public async Task AwakeAsync(CancellationToken ct)
            {
                _log.Add("forwarded:enter");
                await Task.Delay(20, ct);
                _log.Add("forwarded:exit");
            }
        }

        public sealed class NeedsForwarded : IAwakeService
        {
            private readonly Log _log;
            public NeedsForwarded(Forwarded forwarded, Log log) { _log = log; }

            public Task AwakeAsync(CancellationToken ct)
            {
                _log.Add("needs");
                return Task.CompletedTask;
            }
        }

        public sealed class GivesUp : IInitializeService
        {
            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                throw new OperationCanceledException("gave up on its own");
            }
        }
    }
}
