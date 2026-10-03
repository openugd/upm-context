using System;
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

        // ===== fixtures =====

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
