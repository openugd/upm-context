using System;
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
    /// <see cref="BuildAsync" />. Registration order does not matter for constructor dependencies — one is
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
    /// by the build, so it can be handed out while registrations are still being written — and terminating
    /// it aborts a build in progress.
    /// </para>
    /// </remarks>
    public sealed class ContextBuilder
    {
        private readonly Lifetime.Definition _definition;
        private readonly Context _parent;
        private bool _built;

        internal ContextBuilder(Lifetime lifetime, Context parent)
        {
            if (parent != null && parent.Lifetime.IsTerminated)
            {
                throw new InvalidOperationException(
                    "The parent Context has been disposed, so a child could only inherit disposed services.");
            }

            var root = lifetime ?? (parent != null ? parent.Lifetime : Lifetime.Eternal);

            // DefineNested on a terminated lifetime returns a scope that is already terminated. A builder
            // on one could only ever fail, so say so here, where the dead lifetime was passed in.
            if (root.IsTerminated)
            {
                throw new InvalidOperationException(
                    "The lifetime passed to Context.CreateBuilder has already terminated, so the context " +
                    "could never be built. Create the builder from a live lifetime.");
            }

            _definition = root.DefineNested(nameof(Context));
            _parent = parent;

            Services = new ServiceCollection(parent);
            Initializers = new InitializerCollection();
        }

        /// <summary>
        /// The scope the built context will have: a fresh definition nested inside the lifetime given to
        /// <see cref="Context.CreateBuilder" />, already alive and usable before the build.
        /// </summary>
        /// <remarks>
        /// Terminating this aborts a build in progress and disposes whatever it had constructed, because the
        /// build's cancellation token is derived from it. The definition that owns it is never handed out,
        /// so afterwards it ends only through <see cref="Context.Dispose" />, through a failed
        /// <see cref="BuildAsync" />, or with an ancestor lifetime.
        /// </remarks>
        public Lifetime Lifetime => _definition.Lifetime;

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
        /// Also carries <see cref="InitializerCollection.Mode" />, which decides whether the steps of a
        /// dependency rank run concurrently or one at a time — for every step of the boot, not just the
        /// ones added here.
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
        /// is the only place one is handed out.
        /// </para>
        /// <para>
        /// <b>It is not necessarily asynchronous.</b> A graph with no boot phases completes synchronously,
        /// and the returned task is already finished; the method is awaitable so that a boot failure is
        /// observable at all, which is the point.
        /// </para>
        /// <para>
        /// <b>Single use.</b> The <see cref="Services" /> and <see cref="Initializers" /> collections are
        /// sealed here, and a second call throws rather than rebuilding. For another scope, make a child
        /// with <c>Context.CreateBuilder(lifetime, parent: context)</c>.
        /// </para>
        /// </remarks>
        /// <param name="cancellationToken">
        /// Checked before each dependency rank of each phase and passed to every boot step, linked with a
        /// token derived from <see cref="Lifetime" /> so that terminating the scope also aborts the build.
        /// Cancelling tears down the same way a failure does.
        /// </param>
        /// <returns>The built context, with every singleton constructed, injected and booted.</returns>
        /// <exception cref="InvalidOperationException">
        /// This builder has already built its context.
        /// </exception>
        /// <exception cref="ContextException">
        /// The graph did not validate; a registration factory returned <c>null</c> or an instance of the
        /// wrong type; or a constructor or a boot step threw, and those last two carry the original as their
        /// <see cref="Exception.InnerException" />. A boot step that throws an
        /// <see cref="OperationCanceledException" /> while the build is not cancelled has failed, and is
        /// reported here too. An exception thrown by a registration factory itself is
        /// not wrapped: it propagates as it was thrown, after the same teardown.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken" /> or the context's <see cref="Lifetime" /> was cancelled.
        /// </exception>
        /// <exception cref="AggregateException">
        /// The build failed <i>and</i> tearing down what had been constructed failed as well. The first
        /// inner exception is the original failure and the second is what the teardown threw: the one
        /// exception a service threw while disposing, or an <see cref="AggregateException" /> when several
        /// did. Call <see cref="AggregateException.Flatten" /> for the leaves.
        /// </exception>
        public async Task<Context> BuildAsync(CancellationToken cancellationToken = default(CancellationToken))
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

            CancellationTokenSource linked = null;
            var token = _definition.Lifetime.AsCancellationToken();
            if (cancellationToken.CanBeCanceled)
            {
                linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
                token = linked.Token;
            }

            try
            {
                token.ThrowIfCancellationRequested();

                var plan = PlanBuilder.Build(this);
                var context = plan.CreateContext(_definition, _parent);

                plan.ConstructAll(context);
                plan.InjectAll(context);
                context.EndBuild();

                plan.CollectBootSteps(Initializers);
                await plan.RunPhasesAsync(context, Initializers.Mode, token);

                token.ThrowIfCancellationRequested();
                return context;
            }
            catch (Exception failure)
            {
                try
                {
                    _definition.Terminate();
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
                if (linked != null) linked.Dispose();
            }
        }
    }
}
