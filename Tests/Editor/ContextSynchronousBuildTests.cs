using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace OpenUGD.Tests
{
    /// <summary>
    /// Building from code that cannot await, and building under a single-threaded synchronization context
    /// such as Unity's (audit CX-20, CX-22): <c>Build</c> never blocks, and an awaited build's continuations
    /// come back to the calling thread without deadlocking it.
    /// </summary>
    [TestFixture]
    public class ContextSynchronousBuildTests : ContextFixture
    {
        private static string ThisFile([CallerFilePath] string file = null) => file;

        private static int Line([CallerLineNumber] int line = 0) => line;

        // ===== Build =====

        [Test]
        public void BuildReturnsTheContextWhenEveryStepCompletesSynchronously()
        {
            var log = new Log();
            var builder = NewBuilder();
            builder.Services.AddInstance(log);
            builder.Services.Add<AwakesAtOnce>();
            builder.Initializers.Add(BootPhase.Initialize, (c, ct) => null, "returns-null");

            var context = builder.Build();

            CollectionAssert.AreEqual(new[] { "awake" }, log.Entries);
            Assert.IsNotNull(context.Resolve<AwakesAtOnce>());
            context.Dispose();
        }

        [Test]
        public void AChildContextCanBeBuiltFromASynchronousCallSite()
        {
            var parent = NewBuilder("parent").Build();
            var child = Context.CreateBuilder(NewDefinition("child").Lifetime, parent);
            child.Services.AddInstance(new Log());

            var context = child.Build();

            Assert.AreSame(parent, context.Parent);
            parent.Dispose();
            Assert.IsTrue(context.Lifetime.IsTerminated);
        }

        [Test]
        public void BuildThrowsForAStepThatDoesNotCompleteSynchronouslyAndTearsDownOnlyAfterIt()
        {
            var log = new Log();
            var gate = new TaskCompletionSource<bool>();
            var token = default(CancellationToken);
            var builder = NewBuilder();
            builder.Services.AddInstance(log);
            builder.Services.Add<LogsDispose>();
            var line = Line() + 1;
            builder.Initializers.Add(BootPhase.Awake, async (c, ct) => {
                token = ct;
                await gate.Task;
                log.Add("step:end");
            }, "waits-for-the-gate");

            var error = Assert.Throws<ContextException>(() => builder.Build());

            StringAssert.Contains("The boot step 'waits-for-the-gate' did not complete synchronously", error.Message);
            StringAssert.Contains("registered at " + ThisFile() + ":" + line, error.Message);
            StringAssert.Contains("Await BuildAsync", error.Message);
            Assert.IsTrue(token.IsCancellationRequested, "The abandoned build's token is cancelled at once.");
            Assert.IsFalse(builder.Lifetime.IsTerminated, "Nothing is disposed while the step is still running.");
            CollectionAssert.IsEmpty(log.Entries);

            gate.SetResult(true);

            WaitUntil(() => builder.Lifetime.IsTerminated);
            CollectionAssert.AreEqual(new[] { "step:end", "dispose" }, log.Entries,
                "The teardown runs once the step in flight has finished, never under it.");
        }

        [Test]
        public void AnAbandonedBuildStartsNoFurtherStepEvenOfTheSameRank()
        {
            var log = new Log();
            var gate = new TaskCompletionSource<bool>();
            var builder = NewBuilder();
            builder.Services.AddInstance(log);
            builder.Services.AddInstance(gate);
            builder.Services.Add<AwaitsTheGate>();
            builder.Services.Add<AwakesAtOnce>(); // same rank, registered after it

            Assert.Throws<ContextException>(() => builder.Build());
            gate.SetResult(true);

            WaitUntil(() => builder.Lifetime.IsTerminated);
            CollectionAssert.IsEmpty(log.Entries, "The step after the unfinished one never ran.");
        }

        [Test]
        public void BuildNamesEveryUnfinishedStepOfAParallelRank()
        {
            var gate = new TaskCompletionSource<bool>();
            var builder = NewBuilder();
            builder.Initializers.Mode = StartupMode.Parallel;
            builder.Services.AddInstance(gate);
            builder.Services.Add<AwaitsTheGate>(); var first = Line();
            builder.Services.Add<AlsoAwaitsTheGate>(); var second = Line();

            var error = Assert.Throws<ContextException>(() => builder.Build());

            StringAssert.Contains("2 boot steps did not complete synchronously", error.Message);
            StringAssert.Contains("'" + typeof(AwaitsTheGate).FullName + ".AwakeAsync', registered at " +
                                  ThisFile() + ":" + first, error.Message);
            StringAssert.Contains("'" + typeof(AlsoAwaitsTheGate).FullName + ".AwakeAsync', registered at " +
                                  ThisFile() + ":" + second, error.Message);

            gate.SetResult(true);
            WaitUntil(() => builder.Lifetime.IsTerminated);
        }

        [Test]
        public void BuildThrowsAFailureAsItselfRatherThanThroughATask()
        {
            var builder = NewBuilder();
            builder.Initializers.Add(BootPhase.Awake, (c, ct) => throw new InvalidOperationException("no"), "fails");

            var error = Assert.Throws<ContextException>(() => builder.Build());

            StringAssert.Contains("'fails' threw InvalidOperationException: no", error.Message);
            Assert.IsTrue(builder.Lifetime.IsTerminated);
        }

        [Test]
        public void BuildReportsAGraphThatDoesNotValidate()
        {
            var builder = NewBuilder();
            builder.Services.Add<NeedsMissing>();

            var error = Assert.Throws<ContextException>(() => builder.Build());

            StringAssert.Contains("could not be built", error.Message);
        }

        [Test]
        public void BuildIsSingleUseLikeBuildAsync()
        {
            var builder = NewBuilder();
            builder.Build().Dispose();

            Assert.Throws<InvalidOperationException>(() => builder.Build());
            Assert.Throws<InvalidOperationException>(() => RunSync(builder.BuildAsync()));
        }

        [Test]
        public void BuildOnABuilderBornTerminatedIsCancelled()
        {
            var definition = NewDefinition("dead");
            definition.Terminate();

            Assert.Throws<OperationCanceledException>(() => Context.CreateBuilder(definition.Lifetime).Build());
        }

        // ===== a single-threaded synchronization context =====

        [Test]
        public void AnAwaitedBuildComesBackToTheCallingThreadUnderASingleThreadedContext()
        {
            var threads = new ThreadLog();
            var builder = NewBuilder();
            builder.Services.AddInstance(threads);
            builder.Services.Add<AwaitsTwice>();
            builder.Initializers.Add(BootPhase.Initialize, (c, ct) => {
                threads.Add("initialize");
                return Task.CompletedTask;
            }, "records-its-thread");

            var pump = new SingleThreadedContext();
            Task<Context> build;
            using (pump.Install())
            {
                build = builder.BuildAsync();
                Assert.IsFalse(build.IsCompleted, "The service awaits, so the build cannot have finished yet.");
                pump.RunUntil(() => build.IsCompleted);
            }

            Assert.AreEqual(TaskStatus.RanToCompletion, build.Status, "Nothing deadlocked and nothing failed.");
            CollectionAssert.AreEqual(new[] { "awake:start", "awake:after-delay", "awake:after-run", "initialize" },
                threads.Names);
            foreach (var thread in threads.Threads)
            {
                Assert.AreEqual(pump.ThreadId, thread,
                    "Every continuation of the build, and of a step that awaited, ran on the calling thread.");
            }

            build.GetAwaiter().GetResult().Dispose();
        }

        [Test]
        public void BuildUnderASingleThreadedContextThrowsInsteadOfDeadlocking()
        {
            var threads = new ThreadLog();
            var builder = NewBuilder();
            builder.Services.AddInstance(threads);
            builder.Services.Add<LogsDisposeThread>();
            builder.Initializers.Add(BootPhase.Awake, async (c, ct) => {
                await Task.Yield();
                threads.Add("step:end");
            }, "yields");

            var pump = new SingleThreadedContext();
            using (pump.Install())
            {
                Assert.Throws<ContextException>(() => builder.Build(),
                    "Build returns at once - the step's continuation is queued to this very thread.");
                Assert.IsFalse(builder.Lifetime.IsTerminated);

                pump.RunUntil(() => builder.Lifetime.IsTerminated);
            }

            CollectionAssert.AreEqual(new[] { "step:end", "dispose" }, threads.Names);
            CollectionAssert.AreEqual(new[] { pump.ThreadId, pump.ThreadId }, threads.Threads,
                "The abandoned build finished, and tore down, on the thread the step resumed on.");
        }

        [Test]
        public void BlockingOnBuildAsyncUnderASingleThreadedContextCannotFinish()
        {
            // Why Build exists, and why the docs say never to block: the continuation is queued to the one
            // thread that is blocked waiting for it.
            var builder = NewBuilder();
            builder.Initializers.Add(BootPhase.Awake, async (c, ct) => await Task.Yield(), "yields");

            var pump = new SingleThreadedContext();
            using (pump.Install())
            {
                var build = builder.BuildAsync();

                Assert.IsFalse(build.Wait(300), "Blocking the context's only thread keeps the build from finishing.");

                pump.RunUntil(() => build.IsCompleted);
                build.GetAwaiter().GetResult().Dispose();
            }
        }

        // ===== helpers =====

        private static void WaitUntil(Func<bool> condition, int timeoutMilliseconds = 10000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) Assert.Fail("Timed out after " + timeoutMilliseconds + " ms.");
                Thread.Sleep(1);
            }
        }

        /// A synchronization context with one thread - the one that runs <see cref="RunUntil"/> - and a
        /// queue, like Unity's: whatever is posted to it waits until that thread pumps.
        private sealed class SingleThreadedContext : SynchronizationContext
        {
            private readonly BlockingCollection<KeyValuePair<SendOrPostCallback, object>> _queue =
                new BlockingCollection<KeyValuePair<SendOrPostCallback, object>>();

            internal readonly int ThreadId = Thread.CurrentThread.ManagedThreadId;

            public override void Post(SendOrPostCallback callback, object state) =>
                _queue.Add(new KeyValuePair<SendOrPostCallback, object>(callback, state));

            public override void Send(SendOrPostCallback callback, object state) =>
                throw new NotSupportedException("Not used by these tests.");

            public override SynchronizationContext CreateCopy() => this;

            internal IDisposable Install()
            {
                Assert.AreEqual(ThreadId, Thread.CurrentThread.ManagedThreadId);
                return new Installed(this);
            }

            internal void RunUntil(Func<bool> condition, int timeoutMilliseconds = 10000)
            {
                Assert.AreEqual(ThreadId, Thread.CurrentThread.ManagedThreadId, "Pump on the context's thread.");

                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
                while (!condition())
                {
                    if (DateTime.UtcNow > deadline)
                        Assert.Fail("Timed out after " + timeoutMilliseconds + " ms: something is deadlocked.");

                    KeyValuePair<SendOrPostCallback, object> work;
                    if (_queue.TryTake(out work, 10)) work.Key(work.Value);
                }
            }

            private sealed class Installed : IDisposable
            {
                private readonly SynchronizationContext _previous = Current;

                internal Installed(SynchronizationContext context) => SetSynchronizationContext(context);

                public void Dispose() => SetSynchronizationContext(_previous);
            }
        }

        public sealed class ThreadLog
        {
            private readonly object _sync = new object();
            private readonly List<string> _names = new List<string>();
            private readonly List<int> _threads = new List<int>();

            public void Add(string name)
            {
                lock (_sync)
                {
                    _names.Add(name);
                    _threads.Add(Thread.CurrentThread.ManagedThreadId);
                }
            }

            public List<string> Names
            {
                get { lock (_sync) return new List<string>(_names); }
            }

            public List<int> Threads
            {
                get { lock (_sync) return new List<int>(_threads); }
            }
        }

        public sealed class AwakesAtOnce : IAwakeService
        {
            private readonly Log _log;
            public AwakesAtOnce(Log log) { _log = log; }

            public Task AwakeAsync(CancellationToken cancellationToken)
            {
                _log.Add("awake");
                return Task.CompletedTask;
            }
        }

        public sealed class LogsDispose : IDisposable
        {
            private readonly Log _log;
            public LogsDispose(Log log) { _log = log; }
            public void Dispose() { _log.Add("dispose"); }
        }

        public sealed class LogsDisposeThread : IDisposable
        {
            private readonly ThreadLog _log;
            public LogsDisposeThread(ThreadLog log) { _log = log; }
            public void Dispose() { _log.Add("dispose"); }
        }

        public sealed class AwaitsTheGate : IAwakeService
        {
            private readonly TaskCompletionSource<bool> _gate;
            public AwaitsTheGate(TaskCompletionSource<bool> gate) { _gate = gate; }
            public Task AwakeAsync(CancellationToken cancellationToken) => _gate.Task;
        }

        public sealed class AlsoAwaitsTheGate : IAwakeService
        {
            private readonly TaskCompletionSource<bool> _gate;
            public AlsoAwaitsTheGate(TaskCompletionSource<bool> gate) { _gate = gate; }
            public Task AwakeAsync(CancellationToken cancellationToken) => _gate.Task;
        }

        public sealed class AwaitsTwice : IAwakeService
        {
            private readonly ThreadLog _threads;
            public AwaitsTwice(ThreadLog threads) { _threads = threads; }

            public async Task AwakeAsync(CancellationToken cancellationToken)
            {
                _threads.Add("awake:start");
                await Task.Delay(1, cancellationToken);
                _threads.Add("awake:after-delay");
                await Task.Run(() => { }, cancellationToken);
                _threads.Add("awake:after-run");
            }
        }

        public interface IMissing { }

        public sealed class NeedsMissing
        {
            public NeedsMissing(IMissing missing) { }
        }
    }
}
