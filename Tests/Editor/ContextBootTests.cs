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

        public sealed class GivesUp : IInitializeService
        {
            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                throw new OperationCanceledException("gave up on its own");
            }
        }
    }
}
