using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace OpenUGD.Tests
{
    /// <summary>
    /// What the ownership, scope and boot-order fixtures share: scopes and contexts that are always torn
    /// down, a synchronous runner that keeps Unity's synchronization context out of the way, and a
    /// thread-safe log.
    /// </summary>
    public abstract class ContextFixture
    {
        private List<Lifetime.Definition> _definitions;
        private List<Context> _contexts;

        [SetUp]
        public void SetUpScopes()
        {
            _definitions = new List<Lifetime.Definition>();
            _contexts = new List<Context>();
        }

        [TearDown]
        public void TearDownScopes()
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

        protected Lifetime.Definition NewDefinition(string name)
        {
            var definition = Lifetime.Eternal.DefineNested(name);
            _definitions.Add(definition);
            return definition;
        }

        protected ContextBuilder NewBuilder(string name = "root") =>
            Context.CreateBuilder(lifetime: NewDefinition(name).Lifetime);

        protected Context Build(ContextBuilder builder,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var context = RunSync(builder.BuildAsync(cancellationToken));
            Assert.IsNotNull(context, "BuildAsync must never hand back a null Context.");
            _contexts.Add(context);
            return context;
        }

        /// Runs a build that is expected to fail and returns the exception exactly as an `await` would
        /// deliver it - unwrapped, with its own type intact.
        protected static TException FailToBuild<TException>(ContextBuilder builder,
            CancellationToken cancellationToken = default(CancellationToken)) where TException : Exception
        {
            var task = builder.BuildAsync(cancellationToken);
            var exception = Assert.Catch(() => RunSync(task));
            Assert.IsInstanceOf<TException>(exception,
                "Expected " + typeof(TException).Name + " but got: " + exception);
            return (TException)exception;
        }

        protected static T RunSync<T>(Task<T> task, int timeoutMilliseconds = 15000)
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
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

        /// Runs <paramref name="start"/> with no synchronization context, so that the build it starts
        /// schedules its continuations on the thread pool, as it would under a plain test runner.
        protected static Task<T> StartWithoutContext<T>(Func<Task<T>> start)
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                return start();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        protected static void AssertRanBefore(Log log, string earlier, string later)
        {
            var entries = log.Entries;
            var first = entries.IndexOf(earlier);
            var second = entries.IndexOf(later);
            Assert.IsTrue(first >= 0, "'" + earlier + "' never ran. Log: [" + string.Join(", ", entries) + "]");
            Assert.IsTrue(second >= 0, "'" + later + "' never ran. Log: [" + string.Join(", ", entries) + "]");
            Assert.IsTrue(first < second,
                "'" + earlier + "' must come before '" + later + "'. Log: [" + string.Join(", ", entries) + "]");
        }

        public sealed class Log
        {
            private readonly object _sync = new object();
            private readonly List<string> _entries = new List<string>();

            public void Add(string entry)
            {
                lock (_sync) _entries.Add(entry);
            }

            public int Count(string entry)
            {
                lock (_sync)
                {
                    var count = 0;
                    for (var i = 0; i < _entries.Count; i++)
                    {
                        if (_entries[i] == entry) count++;
                    }

                    return count;
                }
            }

            public List<string> Entries
            {
                get { lock (_sync) return new List<string>(_entries); }
            }

            public override string ToString() => "[" + string.Join(", ", Entries) + "]";
        }
    }
}
