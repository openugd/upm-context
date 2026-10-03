using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace OpenUGD.Tests
{
    /// <summary>
    /// When a context's scope ends, and what ending it does to a build in progress.
    /// </summary>
    [TestFixture]
    public class ContextScopeTests : ContextFixture
    {
        // ===== ending the scope while a boot step runs (CX-2) =====

        [Test]
        public void EndingTheScopeMidStepCancelsFirstAndDisposesOnlyAfterTheStepHasFinished()
        {
            var definition = NewDefinition("boot");
            var builder = Context.CreateBuilder(definition.Lifetime);
            var log = new Log();
            builder.Services.AddInstance(log);
            builder.Services.Add<LogsDispose>();
            builder.Initializers.Add(BootPhase.Awake, async (c, ct) => {
                ct.Register(() => log.Add("cancelled"));
                definition.Terminate();
                log.Add("step:after-end");
                await Task.Yield(); // still running after the scope ended
                log.Add("step:exit");
                ct.ThrowIfCancellationRequested();
            }, "ends-the-scope");

            FailToBuild<OperationCanceledException>(builder);

            CollectionAssert.AreEqual(new[] { "cancelled", "step:after-end", "step:exit", "disposed" },
                log.Entries, "Cancel first; dispose only once the step in flight has finished.");
            Assert.IsTrue(builder.Lifetime.IsTerminated);
        }

        [Test]
        public void ALifetimeActionAServiceRegisteredAlsoWaitsForTheStepInFlight()
        {
            var definition = NewDefinition("boot");
            var builder = Context.CreateBuilder(definition.Lifetime);
            var log = new Log();
            builder.Services.AddInstance(log);
            builder.Services.Add<RegistersCleanup>();
            builder.Initializers.Add(BootPhase.Awake, async (c, ct) => {
                definition.Terminate();
                await Task.Yield();
                log.Add("step:exit");
            }, "ends-the-scope");

            FailToBuild<OperationCanceledException>(builder);

            CollectionAssert.AreEqual(new[] { "step:exit", "cleanup" }, log.Entries,
                "The context's Lifetime itself must not end while a boot step is running.");
        }

        [Test]
        public void DisposingTheContextFromInsideABootStepDefersTheTeardown()
        {
            var builder = NewBuilder();
            var log = new Log();
            builder.Services.AddInstance(log);
            builder.Services.Add<LogsDispose>();
            builder.Initializers.Add(BootPhase.Awake, async (c, ct) => {
                ct.Register(() => log.Add("cancelled"));
                c.Dispose();
                await Task.Yield();
                log.Add("step:exit");
            }, "disposes-the-context");

            FailToBuild<OperationCanceledException>(builder);

            CollectionAssert.AreEqual(new[] { "cancelled", "step:exit", "disposed" }, log.Entries);
            Assert.IsTrue(builder.Lifetime.IsTerminated);
        }

        [Test]
        public void DisposingTheContextFromARegistrationFactoryCancelsTheBuildAndTearsDownAfterConstruction()
        {
            // Review of CX-2: the docs promise the factory case as well as the boot-step one.
            var builder = NewBuilder();
            var log = new Log();
            builder.Services.AddInstance(log);
            builder.Services.Add<LogsDispose>();
            builder.Services.Add<DisposesTheContext>(c => {
                c.Dispose();
                return new DisposesTheContext();
            });
            builder.Services.Add<LogsConstruction>(); // constructed after the factory has run

            FailToBuild<OperationCanceledException>(builder);

            CollectionAssert.AreEqual(new[] { "constructed", "disposed" }, log.Entries,
                "Nothing is disposed under the construction still in progress.");
            Assert.IsTrue(builder.Lifetime.IsTerminated);
        }

        [Test]
        public void EndingTheScopeDuringAParallelRankWaitsForEveryStepOfTheRank()
        {
            var definition = NewDefinition("boot");
            var builder = Context.CreateBuilder(definition.Lifetime);
            var log = new Log();
            var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            builder.Services.AddInstance(log);
            builder.Services.AddInstance(ended);
            builder.Services.Add<LogsDispose>();
            builder.Services.Add<EndsTheScope>();
            builder.Services.Add<IgnoresTheToken>();
            builder.Services.AddInstance(definition);
            builder.Initializers.Mode = StartupMode.Parallel;

            FailToBuild<OperationCanceledException>(builder);

            AssertRanBefore(log, "ignores:exit", "disposed");
        }

        [Test]
        public void ALifetimeActionAServiceRegistersStillRunsBetweenItsOwnDisposalAndItsDependencys()
        {
            var builder = NewBuilder();
            var log = new Log();
            builder.Services.AddInstance(log);
            builder.Services.Add<Dependency>();
            builder.Services.Add<Dependent>();
            var context = Build(builder);

            context.Dispose();

            CollectionAssert.AreEqual(new[] { "dependent:dispose", "dependent:cleanup", "dependency:dispose" },
                log.Entries, "Teardown stays in reverse order of construction, actions and disposals interleaved.");
        }

        // ===== the boot token (CX-13) =====

        [Test]
        public void TheBootTokenIsCancelledFirstWhenTheContextEndsWhetherOrNotTheCallerPassedOne(
            [Values(false, true)] bool callerToken, [Values(false, true)] bool endedByOuterLifetime)
        {
            var definition = NewDefinition("outer");
            var builder = Context.CreateBuilder(definition.Lifetime);
            var log = new Log();
            builder.Services.AddInstance(log);
            builder.Services.Add<LogsDispose>();
            builder.Services.Add<KeepsItsToken>();
            var caller = new CancellationTokenSource();
            var context = Build(builder, callerToken ? caller.Token : default(CancellationToken));

            var token = context.Resolve<KeepsItsToken>().Token;
            Assert.IsFalse(token.IsCancellationRequested, "A live context's boot token is not cancelled.");
            token.Register(() => log.Add("cancelled"));

            if (endedByOuterLifetime) definition.Terminate();
            else context.Dispose();

            CollectionAssert.AreEqual(new[] { "cancelled", "disposed" }, log.Entries,
                "Work a boot step left running hears of the end before anything it uses is disposed.");
        }

        [Test]
        public void CancellingTheCallersTokenAfterTheBuildCancelsNothing()
        {
            var builder = NewBuilder();
            builder.Services.Add<KeepsItsToken>();
            var caller = new CancellationTokenSource();
            var context = Build(builder, caller.Token);
            var token = context.Resolve<KeepsItsToken>().Token;

            caller.Cancel();

            Assert.IsFalse(token.IsCancellationRequested,
                "The caller's token abandons a build; it does not reach into a live context.");
            Assert.IsFalse(context.Lifetime.IsTerminated);
        }

        [Test]
        public void TheCallersTokenStillAbandonsTheBuild()
        {
            var builder = NewBuilder();
            var log = new Log();
            builder.Services.AddInstance(log);
            builder.Services.Add<LogsDispose>();
            var caller = new CancellationTokenSource();
            CancellationToken seen = default(CancellationToken);
            builder.Initializers.Add(BootPhase.Awake, async (c, ct) => {
                seen = ct;
                caller.Cancel();
                await Task.Delay(5000, ct);
            }, "cancels-the-caller");

            FailToBuild<OperationCanceledException>(builder, caller.Token);

            Assert.IsTrue(seen.IsCancellationRequested);
            CollectionAssert.AreEqual(new[] { "disposed" }, log.Entries);
        }

        // ===== a child and its parent (CX-3) =====

        [Test]
        public void AChildGivenALifetimeOfItsOwnStillEndsWithItsParentAndBeforeItsParentsServices()
        {
            var parentDefinition = NewDefinition("parent");
            var parentBuilder = Context.CreateBuilder(parentDefinition.Lifetime);
            var log = new Log();
            parentBuilder.Services.AddInstance(log);
            parentBuilder.Services.Add<ParentService>();
            var parent = Build(parentBuilder);

            var childBuilder = Context.CreateBuilder(NewDefinition("unrelated").Lifetime, parent);
            childBuilder.Services.Add<ChildService>();
            var child = Build(childBuilder);

            parentDefinition.Terminate();

            Assert.IsTrue(child.Lifetime.IsTerminated, "A child must never outlive the parent it borrows from.");
            Assert.Throws<ObjectDisposedException>(() => child.Resolve<ChildService>());
            CollectionAssert.AreEqual(new[] { "child:dispose", "parent:dispose" }, log.Entries,
                "The child goes first, while the parent's services it holds are still alive.");
        }

        [Test]
        public void AChildGivenALifetimeOfItsOwnStillEndsWithThatLifetime()
        {
            var parent = Build(NewBuilder("parent"));
            var own = NewDefinition("own");
            var child = Build(Context.CreateBuilder(own.Lifetime, parent));

            own.Terminate();

            Assert.IsTrue(child.Lifetime.IsTerminated);
            Assert.IsFalse(parent.Lifetime.IsTerminated);
        }

        [Test]
        public void AParentDisposedBeforeBuildAsyncCancelsTheChildsBuild()
        {
            var parentBuilder = NewBuilder("parent");
            var log = new Log();
            parentBuilder.Services.AddInstance(log);
            var parent = Build(parentBuilder);

            var childBuilder = Context.CreateBuilder(NewDefinition("unrelated").Lifetime, parent);
            childBuilder.Services.Add<LogsConstruction>();

            parent.Dispose();

            Assert.IsTrue(childBuilder.Lifetime.IsTerminated);
            var error = FailToBuild<OperationCanceledException>(childBuilder);
            StringAssert.Contains("parent Context has been disposed", error.Message);
            CollectionAssert.IsEmpty(log.Entries, "Nothing is constructed for a child of a dead parent.");
        }

        [Test]
        public void AParentDisposedWhileTheChildBootsCancelsTheChildsBuild()
        {
            var parentBuilder = NewBuilder("parent");
            var log = new Log();
            parentBuilder.Services.AddInstance(log);
            var parent = Build(parentBuilder);

            var childBuilder = Context.CreateBuilder(NewDefinition("unrelated").Lifetime, parent);
            childBuilder.Services.Add<LogsDispose>();
            childBuilder.Initializers.Add(BootPhase.Awake, async (c, ct) => {
                parent.Dispose();
                await Task.Yield();
                log.Add("step:exit");
                ct.ThrowIfCancellationRequested();
            }, "disposes-the-parent");

            FailToBuild<OperationCanceledException>(childBuilder);

            CollectionAssert.AreEqual(new[] { "step:exit", "disposed" }, log.Entries);
            Assert.IsTrue(childBuilder.Lifetime.IsTerminated);
        }

        [Test]
        public void ADisposedChildLeavesNothingBehindInItsParentsLifetimeOrItsOwn()
        {
            var parent = Build(NewBuilder("parent"));
            var own = NewDefinition("own");

            var weak = OnAThreadOfItsOwn(() => BuildAndDisposeAChild(parent, own.Lifetime));
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.IsFalse(weak.IsAlive,
                "Neither the parent's lifetime nor the child's own may keep a disposed child's scope reachable.");
            GC.KeepAlive(parent);
            GC.KeepAlive(own);
        }

        [Test]
        public void AContextIsReachableOnlyThroughTheLifetimeItWasCreatedOn()
        {
            // Review of CX-2: linking the scope instead of nesting it must not root it somewhere else. A
            // context on a lifetime that nothing else holds is collected with that lifetime, as a nested
            // scope would be - it is not kept, with every service it built, for the life of the process.
            var weak = OnAThreadOfItsOwn(BuildOnALifetimeNothingHolds);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.IsFalse(weak.IsAlive,
                "A context must not be reachable from anything but the lifetime it was created on.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference BuildOnALifetimeNothingHolds()
        {
            // The vacuous intersection is attached to nothing, so only this frame holds it.
            var outer = Lifetime.Intersection();
            var builder = Context.CreateBuilder(outer.Lifetime);
            builder.Services.Add<KeepsItsToken>();
            var context = RunSync(StartWithoutContext(() => builder.BuildAsync()));
            return new WeakReference(context.Lifetime);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference BuildAndDisposeAChild(Context parent, Lifetime own)
        {
            var child = RunSync(StartWithoutContext(() => Context.CreateBuilder(own, parent).BuildAsync()));
            var weak = new WeakReference(child.Lifetime);
            child.Dispose();
            return weak;
        }

        /// Runs the set-up of a retention test on a thread of its own, so that nothing it created is
        /// referenced from a live stack afterwards: a conservative collector, such as the Boehm GC of Unity's
        /// editor, treats anything on a live stack that looks like a pointer as a root.
        private static T OnAThreadOfItsOwn<T>(Func<T> setUp)
        {
            var result = default(T);
            Exception failure = null;
            var thread = new Thread(() => {
                try
                {
                    result = setUp();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }) { IsBackground = true, Name = "retention-set-up" };

            thread.Start();
            Assert.IsTrue(thread.Join(30000), "the set-up thread did not finish");
            if (failure != null) Assert.Fail("the set-up threw: " + failure);
            return result;
        }

        // ===== fixtures =====

        public sealed class ParentService : IDisposable
        {
            private readonly Log _log;
            public ParentService(Log log) { _log = log; }
            public void Dispose() => _log.Add("parent:dispose");
        }

        public sealed class ChildService : IDisposable
        {
            private readonly Log _log;
            public ChildService(Log log, ParentService parent) { _log = log; }
            public void Dispose() => _log.Add("child:dispose");
        }

        public sealed class LogsConstruction
        {
            public LogsConstruction(Log log) => log.Add("constructed");
        }

        public sealed class DisposesTheContext { }

        public sealed class KeepsItsToken : IAwakeService
        {
            public CancellationToken Token;

            public Task AwakeAsync(CancellationToken cancellationToken)
            {
                Token = cancellationToken;
                return Task.CompletedTask;
            }
        }

        public sealed class LogsDispose : IDisposable
        {
            private readonly Log _log;
            public LogsDispose(Log log) { _log = log; }
            public void Dispose() => _log.Add("disposed");
        }

        public sealed class RegistersCleanup
        {
            public RegistersCleanup(Lifetime lifetime, Log log) => lifetime.AddAction(() => log.Add("cleanup"));
        }

        public sealed class EndsTheScope : IAwakeService
        {
            private readonly Lifetime.Definition _definition;
            private readonly TaskCompletionSource<bool> _ended;

            public EndsTheScope(Lifetime.Definition definition, TaskCompletionSource<bool> ended)
            {
                _definition = definition;
                _ended = ended;
            }

            public async Task AwakeAsync(CancellationToken ct)
            {
                await Task.Yield();
                _definition.Terminate();
                _ended.TrySetResult(true);
                ct.ThrowIfCancellationRequested();
            }
        }

        public sealed class IgnoresTheToken : IAwakeService
        {
            private readonly Log _log;
            private readonly TaskCompletionSource<bool> _ended;

            public IgnoresTheToken(Log log, TaskCompletionSource<bool> ended)
            {
                _log = log;
                _ended = ended;
            }

            public async Task AwakeAsync(CancellationToken ct)
            {
                await _ended.Task;
                await Task.Delay(20); // deliberately not observing the token
                _log.Add("ignores:exit");
            }
        }

        public sealed class Dependency : IDisposable
        {
            private readonly Log _log;
            public Dependency(Log log) { _log = log; }
            public void Dispose() => _log.Add("dependency:dispose");
        }

        public sealed class Dependent : IDisposable
        {
            private readonly Log _log;

            public Dependent(Dependency dependency, Lifetime lifetime, Log log)
            {
                _log = log;
                lifetime.AddAction(() => log.Add("dependent:cleanup"));
            }

            public void Dispose() => _log.Add("dependent:dispose");
        }
    }
}
