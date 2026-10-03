using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenUGD
{
    /// <summary>
    /// Describes a <see cref="Context" /> and then builds it. Obtained from
    /// <see cref="Context.CreateBuilder" />; single-use, because the plan it compiles is only valid for the
    /// one context it produces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two things to fill in and one to call.</b> <see cref="Services" /> holds the registrations,
    /// <see cref="Initializers" /> the boot steps that belong to no service; then
    /// <see cref="BuildAsync" /> — or <see cref="Build" /> from code that cannot await, for a graph whose
    /// boot steps all complete synchronously. Registration order does not matter for constructor dependencies — one is
    /// always constructed and booted before whatever takes it — and otherwise decides only the order in
    /// which services of the same dependency rank boot (see <see cref="BootPhase" />).
    /// </para>
    /// <para>
    /// <b>Nothing is validated until you build.</b> Adding a registration only records it. The whole graph
    /// is then checked in one pass and every problem is reported together, most of them naming the file and
    /// line of the registration that caused it, before a single object is constructed.
    /// </para>
    /// <para>
    /// <b>The scope exists from the start.</b> <see cref="Lifetime" /> is created by the constructor, not
    /// by the build, so it can be handed out while registrations are still being written. If the lifetime
    /// the builder was created on — or its parent context — ends during the build, the build is cancelled;
    /// if either had ended before, the builder is born terminated and the build is cancelled before it
    /// constructs anything.
    /// </para>
    /// </remarks>
    public sealed class ContextBuilder
    {
        private readonly ContextScope _scope;
        private readonly Context _parent;
        private bool _built;

        internal ContextBuilder(Lifetime lifetime, Context parent)
        {
            // A child ends with its parent whatever lifetime it was given, since it hands out the parent's
            // services. A lifetime or a parent that has already ended yields a builder born terminated, as
            // Lifetime.DefineNested does: registering still works, and BuildAsync is cancelled.
            var parentLifetime = parent != null ? parent.Lifetime : null;
            _scope = new ContextScope(lifetime ?? parentLifetime ?? Lifetime.Eternal);
            if (lifetime != null && parentLifetime != null && lifetime != parentLifetime) _scope.Link(parentLifetime);

            _parent = parent;

            Services = new ServiceCollection(parent);
            Initializers = new InitializerCollection();
        }

        /// <summary>
        /// The scope the built context will have: a lifetime of its own that ends when the lifetime given to
        /// <see cref="Context.CreateBuilder" /> ends or, for a child, when its <see cref="Parent" /> does —
        /// already alive and usable before the build, unless one of those had already ended.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The definition that owns it is never handed out, so it ends only through
        /// <see cref="Context.Dispose" />, through a failed <see cref="BuildAsync" />, or when the lifetime it
        /// was created on or the parent ends.
        /// </para>
        /// <para>
        /// <b>Never while a boot step runs.</b> An end that arrives during the build — that lifetime ending,
        /// or <see cref="Context.Dispose" /> called from a factory or a boot step — first cancels the token the
        /// boot steps were given, and this lifetime ends, disposing what the build had constructed, only once
        /// every step in flight has finished. Until then it still reads as alive. Before and after the build
        /// it ends at once, as a nested lifetime would.
        /// </para>
        /// </remarks>
        public Lifetime Lifetime => _scope.Lifetime;

        /// <summary>
        /// The context whose registrations the built context will inherit, or <c>null</c> for a root.
        /// Whatever is registered here shadows the parent's binding for the same contract.
        /// </summary>
        public Context Parent => _parent;

        /// <summary>
        /// The registrations. Add to it with <c>Add</c>, <c>AddInstance</c> and <c>TryAdd</c>; each entry is
        /// a singleton of the context being built, and <c>As</c> adds a contract rather than replacing one.
        /// </summary>
        /// <remarks>
        /// Sealed by <see cref="BuildAsync" />: registering afterwards throws an
        /// <see cref="InvalidOperationException" /> rather than quietly doing nothing.
        /// </remarks>
        public ServiceCollection Services { get; }

        /// <summary>
        /// The boot steps that are not services. Empty and usually left that way: a service opts into a
        /// phase by implementing <see cref="IAwakeService" /> or <see cref="IInitializeService" />, so this
        /// is only for work that has no object of its own to hang on.
        /// </summary>
        /// <remarks>
        /// Also carries <see cref="InitializerCollection.Mode" />, which decides whether the services of a
        /// dependency rank boot concurrently or one at a time. The steps added here always run one at a
        /// time, in the order they were added.
        /// </remarks>
        public InitializerCollection Initializers { get; }

        /// <summary>
        /// Validates the graph, constructs everything in dependency order, runs the two boot phases, and
        /// returns the finished container — or disposes everything it made and rethrows.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Validation first, construction second.</b> Every contract, constructor, injected member and
        /// cycle is checked before a single object exists, and every problem found is reported in one
        /// <see cref="ContextException" /> rather than one per run. Only then does construction start, each
        /// dependency built before whatever needs it; member injection runs after every constructor has
        /// returned, which is why <c>[Inject]</c> members are allowed to be cyclic and constructor
        /// parameters are not.
        /// </para>
        /// <para>
        /// <b>A failed build is atomic.</b> Whatever went wrong — a validation error, a throwing
        /// constructor, a factory returning the wrong type, a boot step, a cancellation — the context's
        /// <see cref="Lifetime" /> is terminated, which disposes exactly what had been constructed, in
        /// reverse construction order, and the original exception is rethrown. No half-built
        /// <see cref="Context" /> is ever returned, and none is reachable from anywhere else, because this
        /// is the only place one is handed out. The teardown runs only after every boot step in flight has
        /// finished — a concurrent rank is awaited whole — so it never disposes what a step is still using.
        /// </para>
        /// <para>
        /// <b>Await it; never block on it.</b> A graph whose boot steps all complete synchronously is
        /// finished before this method returns, but blocking on the task — <c>.Result</c>,
        /// <c>.Wait()</c>, <c>GetAwaiter().GetResult()</c> — deadlocks under a single-threaded
        /// <see cref="SynchronizationContext" />, such as Unity's, as soon as one step really awaits: its
        /// continuation is queued to the very thread that is blocked waiting for it. Continuations of the
        /// build itself return to the caller's synchronization context. From code that cannot await, call
        /// <see cref="Build" />, which never blocks.
        /// </para>
        /// <para>
        /// <b>Single use.</b> The <see cref="Services" /> and <see cref="Initializers" /> collections are
        /// sealed here, and a second call throws rather than rebuilding. For another scope, make a child
        /// with <c>Context.CreateBuilder(lifetime, parent: context)</c>.
        /// </para>
        /// </remarks>
        /// <param name="cancellationToken">
        /// Abandons the build: cancelling it cancels the token every boot step receives, the build starts no
        /// further boot step, and it tears down the same way a failure does. It is listened to
        /// only while the build runs; cancelling it after <c>BuildAsync</c> has returned changes nothing.
        /// The boot steps never see this token itself — they get one token that behaves the same whether or
        /// not this one was passed (see <see cref="IAwakeService.AwakeAsync" />).
        /// </param>
        /// <returns>The built context, with every singleton constructed, injected and booted.</returns>
        /// <exception cref="InvalidOperationException">
        /// This builder has already built its context.
        /// </exception>
        /// <exception cref="ContextException">
        /// The graph did not validate; a registration factory returned <c>null</c> or an instance of the
        /// wrong type; or a constructor, a registration factory, an <c>[Inject]</c> property setter or a boot
        /// step threw, and those carry the original as their <see cref="Exception.InnerException" />. A failure while constructing names
        /// the registration site and the chain of services being constructed, which is also its
        /// <see cref="ContextException.Path" />; a boot step's names the step and where it was registered.
        /// A boot step that throws an
        /// <see cref="OperationCanceledException" /> while the build is not cancelled has failed, and is
        /// reported here too. When several steps of one rank fail together under
        /// <see cref="StartupMode.Parallel" />, a single <see cref="ContextException" /> names every one of
        /// them, and its <see cref="Exception.InnerException" /> is an <see cref="AggregateException" />
        /// holding each step's own <see cref="ContextException" />, in boot order.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken" /> was cancelled; the lifetime the builder was created on ended,
        /// or the parent context was disposed, before or during the build; or the context was disposed from
        /// inside its own build.
        /// </exception>
        /// <exception cref="AggregateException">
        /// The build failed <i>and</i> tearing down what had been constructed failed as well. The first
        /// inner exception is the original failure and the second is what the teardown threw: the one
        /// exception a service threw while disposing, or an <see cref="AggregateException" /> when several
        /// did. Call <see cref="AggregateException.Flatten" /> for the leaves.
        /// </exception>
        public Task<Context> BuildAsync(CancellationToken cancellationToken = default(CancellationToken)) =>
            BuildCoreAsync(cancellationToken, null);

        /// <summary>
        /// Builds the context without awaiting — validation, construction, injection and both boot phases,
        /// on the calling thread — for a graph whose boot steps all complete synchronously, from code that
        /// cannot await: a constructor, a property, a Unity message.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>It never blocks.</b> Blocking on <see cref="BuildAsync" /> deadlocks under a single-threaded
        /// <see cref="SynchronizationContext" />, such as Unity's, once a boot step really awaits. This
        /// method does not wait: if a boot step returns a task that has not completed, it throws a
        /// <see cref="ContextException" /> naming that step and where it was registered, and abandons the
        /// build, exactly as <see cref="Context.Dispose" /> called from inside the build would. The build's
        /// token is cancelled at once, no further step starts, and what the build had constructed is disposed
        /// once the step in flight has finished — on whatever thread that step resumes on, never under it.
        /// If that step then fails, or the teardown does, nothing awaits the abandoned build, so .NET reports
        /// that only through <see cref="TaskScheduler.UnobservedTaskException" />.
        /// </para>
        /// <para>
        /// Everything else is <see cref="BuildAsync" />'s — the validation, the atomic failure, the single
        /// use — with its exceptions thrown here directly. For a graph with a step that may await, await
        /// <see cref="BuildAsync" /> instead.
        /// </para>
        /// </remarks>
        /// <returns>The built context, with every singleton constructed, injected and booted.</returns>
        /// <exception cref="InvalidOperationException">
        /// This builder has already built its context.
        /// </exception>
        /// <exception cref="ContextException">
        /// A boot step did not complete synchronously; or anything <see cref="BuildAsync" /> reports as a
        /// <see cref="ContextException" />.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// The lifetime the builder was created on ended, or the parent context was disposed, before or
        /// during the build; or the context was disposed from inside its own build.
        /// </exception>
        /// <exception cref="AggregateException">
        /// The build failed and tearing down what had been constructed failed as well, as for
        /// <see cref="BuildAsync" />.
        /// </exception>
        public Context Build()
        {
            var pending = new List<BootStep>();
            var build = BuildCoreAsync(default(CancellationToken), pending);

            // Finished: nothing was waited for, so reading the result here neither blocks nor deadlocks.
            if (build.IsCompleted) return build.GetAwaiter().GetResult();

            // Abandoned exactly as Context.Dispose from inside the build would: cancelled now, torn down once
            // the steps in flight finish. A step that resumed on another thread may have finished the build
            // in the meantime; then nobody holds the context, and this ends it at once.
            var failure = new ContextException(NotSynchronous(pending));
            try
            {
                _scope.End();
            }
            catch (Exception teardown)
            {
                throw new AggregateException(
                    "The Context could not be built synchronously, and tearing down what had already been " +
                    "constructed failed as well. The first inner exception is the original failure, the second " +
                    "is the teardown; call Flatten() for the leaves.", failure, teardown);
            }

            throw failure;
        }

        private static string NotSynchronous(List<BootStep> pending)
        {
            var message = new StringBuilder();
            if (pending.Count == 0)
            {
                message.Append("A boot step did not complete synchronously.");
            }
            else if (pending.Count == 1)
            {
                message.Append("The boot step '").Append(pending[0].Name).Append("' did not complete synchronously.");
                if (pending[0].Site != null) message.Append("\n      registered at ").Append(pending[0].Site);
            }
            else
            {
                message.Append(pending.Count).Append(" boot steps did not complete synchronously:");
                for (var i = 0; i < pending.Count; i++)
                {
                    message.Append("\n      '").Append(pending[i].Name).Append('\'');
                    if (pending[i].Site != null) message.Append(", registered at ").Append(pending[i].Site);
                }
            }

            return message.Append("\n      ContextBuilder.Build never waits, because waiting on the calling thread ")
                .Append("deadlocks under a single-threaded SynchronizationContext such as Unity's. Await ")
                .Append("BuildAsync for a graph with asynchronous boot steps. The build has been abandoned: its ")
                .Append("token is cancelled, and what it constructed is disposed once the steps in flight finish.")
                .ToString();
        }

        private async Task<Context> BuildCoreAsync(CancellationToken cancellationToken, List<BootStep> pending)
        {
            if (_built)
            {
                throw new InvalidOperationException(
                    "This ContextBuilder has already built its Context. A builder is single-use: call " +
                    "Context.CreateBuilder again, or Context.CreateBuilder(parent: context) for a child.");
            }

            _built = true;
            Services.Seal();
            Initializers.Seal();

            // One token for the boot steps whether or not the caller passed one: cancelled when the build is
            // abandoned and, once the context is live, when it ends - before anything is disposed. The
            // caller's token is only ever a way to abandon the build, so it is wired in for the build alone.
            var token = _scope.BeginBuild();
            var registration = default(CancellationTokenRegistration);

            try
            {
                // The scope ended before the build began: the lifetime this builder was created on, or its
                // parent, has ended since - or had already ended at CreateBuilder.
                if (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(
                        "The Context's scope ended before BuildAsync was called: the lifetime given to " +
                        "Context.CreateBuilder has terminated, or the parent Context has been disposed. " +
                        "Nothing was constructed.", token);
                }

                if (cancellationToken.CanBeCanceled) registration = cancellationToken.Register(_scope.CancelBuild);
                token.ThrowIfCancellationRequested();

                var plan = PlanBuilder.Build(this);
                var context = plan.CreateContext(_scope, _parent);

                plan.ConstructAll(context);
                plan.InjectAll(context);
                context.EndBuild();

                plan.CollectBootSteps(Initializers);
                await plan.RunPhasesAsync(context, Initializers.Mode, token, pending);

                token.ThrowIfCancellationRequested();
                _scope.CompleteBuild(token);
                return context;
            }
            catch (Exception failure)
            {
                // Every boot step has finished by now - a concurrent rank is awaited whole - so the teardown
                // cannot dispose anything a step is still using.
                try
                {
                    _scope.FailBuild();
                }
                catch (Exception teardown)
                {
                    throw new AggregateException(
                        "The Context failed to build, and tearing down what had already been constructed " +
                        "failed as well. The first inner exception is the original failure, the second is " +
                        "the teardown; call Flatten() for the leaves.", failure, teardown);
                }

                throw;
            }
            finally
            {
                registration.Dispose();
            }
        }
    }
}
