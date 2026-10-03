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

        // ===== fixtures =====

        public sealed class GivesUp : IInitializeService
        {
            public Task InitializeAsync(CancellationToken cancellationToken)
            {
                throw new OperationCanceledException("gave up on its own");
            }
        }
    }
}
