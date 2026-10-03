using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace OpenUGD.Tests
{
    /// <summary>
    /// The per-type metadata cache behind construction and injection (audit CX-29): a cached type is looked up
    /// without a lock, and reading a new type - reflection, which may run the type's own code - holds up
    /// nobody else.
    /// </summary>
    [TestFixture]
    public class ActivationCacheTests : ContextFixture
    {
        [Test]
        public void ALookupNeverWaitsForAnotherTypeBeingRead()
        {
            var cached = typeof(ActivationCacheTests);
            Activation.GetMetadata(cached);
            var slow = Emit(fieldAttribute: typeof(SlowInjectAttribute).GetConstructor(System.Type.EmptyTypes));
            var fresh = Emit();

            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                SlowInjectAttribute.Entered = entered;
                SlowInjectAttribute.Release = release;
                try
                {
                    var reading = Task.Run(() => Activation.GetMetadata(slow));
                    Assert.IsTrue(entered.Wait(10000), "Reading the slow type never reached its attribute.");

                    Assert.IsTrue(Task.Run(() => Activation.GetMetadata(cached)).Wait(2000),
                        "A cache hit waited for another type to be read: the lookup takes a lock the read holds.");
                    Assert.IsTrue(Task.Run(() => Activation.GetMetadata(fresh)).Wait(2000),
                        "Reading one type waited for another: reflection runs under a lock.");

                    release.Set();
                    Assert.IsTrue(reading.Wait(10000), "The slow read never finished.");
                }
                finally
                {
                    release.Set();
                    SlowInjectAttribute.Entered = null;
                    SlowInjectAttribute.Release = null;
                }
            }

            Assert.AreEqual(1, Activation.GetMetadata(slow).Members.Length);
            Assert.AreSame(Activation.GetMetadata(slow), Activation.GetMetadata(slow), "Read once, then cached.");
        }

        /// An [Inject] whose constructor - which reflection runs while reading the member - blocks while a
        /// test holds it.
        public sealed class SlowInjectAttribute : InjectAttribute
        {
            internal static ManualResetEventSlim Entered;
            internal static ManualResetEventSlim Release;

            public SlowInjectAttribute()
            {
                var entered = Entered;
                var release = Release;
                if (entered == null || release == null) return;

                entered.Set();
                release.Wait(10000);
            }
        }
    }
}
