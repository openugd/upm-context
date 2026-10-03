using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
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
            var context = RunSync(StartWithoutContext(() => builder.BuildAsync(cancellationToken)));
            Assert.IsNotNull(context, "BuildAsync must never hand back a null Context.");
            _contexts.Add(context);
            return context;
        }

        /// Runs a build that is expected to fail and returns the exception exactly as an `await` would
        /// deliver it - unwrapped, with its own type intact.
        protected static TException FailToBuild<TException>(ContextBuilder builder,
            CancellationToken cancellationToken = default(CancellationToken)) where TException : Exception
        {
            var task = StartWithoutContext(() => builder.BuildAsync(cancellationToken));
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
        /// schedules its continuations on the thread pool rather than on a context whose thread is about to
        /// block in <see cref="RunSync{T}"/> - which, under Unity's, would deadlock.
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

        private static ModuleBuilder _module;
        private static int _emitted;

        /// Makes a public class with a public parameterless constructor at run time: for a type no earlier
        /// run has seen (and cached), or one named like a type this assembly cannot declare. With
        /// <paramref name="fieldAttribute"/>, it also gets a public object field carrying that attribute.
        /// A <paramref name="name"/> of <c>null</c> gets a fresh one.
        protected static Type Emit(string name = null, Type parent = null, ConstructorInfo fieldAttribute = null)
        {
            if (_module == null)
            {
                var assembly = AssemblyBuilder.DefineDynamicAssembly(
                    new AssemblyName("OpenUGD.Tests.Emitted"), AssemblyBuilderAccess.Run);
                _module = assembly.DefineDynamicModule("OpenUGD.Tests.Emitted");
            }

            var type = _module.DefineType(name ?? "OpenUGD.Tests.Emitted" + Interlocked.Increment(ref _emitted),
                TypeAttributes.Public | TypeAttributes.Class, parent);
            type.DefineDefaultConstructor(MethodAttributes.Public);
            if (fieldAttribute != null)
            {
                type.DefineField("Member", typeof(object), FieldAttributes.Public)
                    .SetCustomAttribute(new CustomAttributeBuilder(fieldAttribute, new object[0]));
            }

            return type.CreateTypeInfo().AsType();
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
