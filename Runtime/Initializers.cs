using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenUGD
{
    /// <summary>
    /// The two ordered stages of the async boot that <see cref="ContextBuilder.BuildAsync" /> runs once
    /// every service exists. Every step of <see cref="Awake" /> completes before the first step of
    /// <see cref="Initialize" /> begins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Construction and member injection have already finished by the time any phase runs, so every service
    /// exists and its dependencies are wired — bar an <c>[Inject(Optional = true)]</c> member nothing was
    /// registered for, which still holds whatever it was initialised with. The phases exist for the work a
    /// constructor cannot do: anything awaitable, and anything that needs another service to be ready
    /// rather than merely to exist.
    /// </para>
    /// <para>
    /// <b>Order within a phase is dependency rank, not registration order.</b> Rank is the longest path
    /// from a service down to a leaf, counting what a constructor takes as a parameter and what a
    /// registration factory resolves while it runs, so everything a service is built out of has finished
    /// the phase before that service starts it. An <c>[Inject]</c> member is not counted — it is assigned
    /// after every constructor has returned, so a service may well boot before the collaborator it holds
    /// through one. Steps added to an <see cref="InitializerCollection" /> are outside the dependency graph
    /// and run ahead of every service in their phase.
    /// </para>
    /// <para>
    /// <b>A phase that throws fails the build.</b> Everything constructed so far is disposed in reverse
    /// construction order and no <see cref="Context" /> is returned; the original exception is wrapped in a
    /// <see cref="ContextException" /> naming the step and the phase, with the original as its
    /// <see cref="Exception.InnerException" />. A cancellation propagates unwrapped.
    /// </para>
    /// </remarks>
    public enum BootPhase
    {
        /// <summary>
        /// Runs first, and is what <see cref="IAwakeService" /> enrols in. For the work the
        /// <see cref="Initialize" /> phase is entitled to assume is done — loading a save, opening a
        /// connection, restoring state.
        /// </summary>
        Awake = 0,

        // 1 is deliberately unassigned, so that a phase between these two could be added later without
        // renumbering Initialize.

        /// <summary>
        /// Runs last, and is what <see cref="IInitializeService" /> enrols in. Nothing else in the boot runs
        /// after it, so a step here may assume the whole container is warm.
        /// </summary>
        Initialize = 2
    }

    /// <summary>
    /// How the steps that share a dependency rank are run. Ranks themselves are always sequential — that is
    /// what makes rank ordering a guarantee rather than a hint — so this only ever affects steps of one
    /// rank: the services the graph shows to be independent of each other, and the steps of an
    /// <see cref="InitializerCollection" />, which are unranked and therefore all share the one rank that
    /// runs ahead of the services.
    /// </summary>
    /// <remarks>
    /// Chosen through <see cref="InitializerCollection.Mode" />, read once when
    /// <see cref="ContextBuilder.BuildAsync" /> reaches the boot, and applied to both phases alike.
    /// </remarks>
    public enum StartupMode
    {
        /// <summary>
        /// The default. Steps of one rank start together and the rank ends when the last of them finishes.
        /// Every task in a rank is awaited even after one has already faulted, so teardown never runs while
        /// a boot step is still touching the objects it is about to dispose.
        /// </summary>
        Parallel,

        /// <summary>
        /// One step at a time: rank by rank, and within a rank in the order the steps were collected, which
        /// for services is registration order. Slower, but it makes a boot log reproducible and is what to
        /// reach for when a step turns out to be less independent than its declared dependencies claim.
        /// </summary>
        Sequential
    }

    /// <summary>
    /// Opt-in enrolment in <see cref="BootPhase.Awake" />. Implementing it on a registered service is all
    /// the composition root has to say: <see cref="ContextBuilder.BuildAsync" /> awakens it.
    /// </summary>
    /// <remarks>
    /// Enrolment is by what the constructed instance actually is, not by the contract it was registered as,
    /// so a factory returning a subtype that implements this is enrolled too. A service may implement this
    /// and <see cref="IInitializeService" /> both, and then runs once in each phase.
    /// </remarks>
    public interface IAwakeService
    {
        /// <summary>
        /// Awaited during <see cref="BootPhase.Awake" />, after every service has been constructed and
        /// injected and after every service of a lower dependency rank has finished awakening. Called at
        /// most once, because a <see cref="ContextBuilder" /> builds at most once.
        /// </summary>
        /// <param name="cancellationToken">
        /// Cancelled when the context's <see cref="OpenUGD.Lifetime" /> terminates or when the token passed
        /// to <see cref="ContextBuilder.BuildAsync" /> is cancelled. Observing it aborts the whole build,
        /// so an implementation that ignores it delays the failure rather than avoiding it.
        /// </param>
        /// <returns>
        /// A task that completes when this service is ready. <c>null</c> is accepted and treated as an
        /// already-completed task. Throwing, or returning a faulted task, fails the build: everything
        /// constructed is disposed and no <see cref="Context" /> escapes.
        /// </returns>
        Task AwakeAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Opt-in enrolment in <see cref="BootPhase.Initialize" />, the last phase of the boot. Same enrolment
    /// rules as <see cref="IAwakeService" />, one phase later.
    /// </summary>
    /// <remarks>
    /// Use this rather than <see cref="IAwakeService" /> when the work needs other services to be ready and
    /// not merely constructed — rank ordering guarantees your dependencies awoke first, but only a later
    /// phase guarantees that services you do <i>not</i> depend on are ready too.
    /// </remarks>
    public interface IInitializeService
    {
        /// <summary>
        /// Awaited during <see cref="BootPhase.Initialize" />, after the whole <see cref="BootPhase.Awake" />
        /// phase and after every service of a lower dependency rank in this phase. Called at most once.
        /// </summary>
        /// <param name="cancellationToken">
        /// Cancelled when the context's <see cref="OpenUGD.Lifetime" /> terminates or when the token passed
        /// to <see cref="ContextBuilder.BuildAsync" /> is cancelled.
        /// </param>
        /// <returns>
        /// A task that completes when this service is ready; <c>null</c> counts as completed. Throwing, or
        /// returning a faulted task, fails the build and disposes everything already constructed.
        /// </returns>
        Task InitializeAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// The boot steps of a <see cref="ContextBuilder" /> that are not services: a lambda that has to run at
    /// a known point in the boot but has no object to belong to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Services do not belong here.</b> <see cref="IAwakeService" /> and <see cref="IInitializeService" />
    /// enrol on their own, which keeps the statement of when a service runs next to the code that runs,
    /// rather than at the composition root where it drifts.
    /// </para>
    /// <para>
    /// <b>Order.</b> Every step added here runs before every service of the same phase: these are
    /// infrastructure that phase may rely on, and they have no place in the dependency graph to be ranked
    /// by. Among themselves they share the one rank, so they are started in the order they were added but
    /// run concurrently under <see cref="StartupMode.Parallel" />; only <see cref="StartupMode.Sequential" />
    /// makes each of them finish before the next begins.
    /// </para>
    /// <para>
    /// <b>Lifetime.</b> The collection belongs to one builder and is sealed the moment that builder starts
    /// building, so a step added afterwards throws instead of silently never running.
    /// </para>
    /// </remarks>
    public sealed class InitializerCollection
    {
        internal readonly struct Entry
        {
            internal readonly BootPhase Phase;
            internal readonly Func<Context, CancellationToken, Task> Step;
            internal readonly string Name;

            internal Entry(BootPhase phase, Func<Context, CancellationToken, Task> step, string name)
            {
                Phase = phase;
                Step = step;
                Name = name;
            }
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private bool _sealed;

        /// <summary>
        /// Whether steps sharing a dependency rank run concurrently or one at a time. Defaults to
        /// <see cref="StartupMode.Parallel" /> and governs both phases, services and explicit steps
        /// alike. Read once, when <see cref="ContextBuilder.BuildAsync" /> reaches the boot; setting it
        /// after that changes nothing, and a builder never boots twice.
        /// </summary>
        public StartupMode Mode { get; set; } = StartupMode.Parallel;

        /// <summary>
        /// Appends a boot step to <paramref name="phase" />, to run ahead of that phase's services.
        /// </summary>
        /// <param name="phase">Which of the two stages of the boot the step belongs to.</param>
        /// <param name="step">
        /// The work to do. It is handed the context being built — fully constructed and resolvable, but not
        /// yet returned to the caller — and a token cancelled when the context's
        /// <see cref="OpenUGD.Lifetime" /> terminates or the token given to
        /// <see cref="ContextBuilder.BuildAsync" /> is cancelled. It may return <c>null</c> in place of an
        /// already-completed task. Throwing fails the build, which disposes everything constructed so far
        /// and lets no <see cref="Context" /> escape.
        /// </param>
        /// <param name="name">
        /// [used for diagnostics] Identifies the step in the <see cref="ContextException" /> raised when it
        /// throws. Defaults to the phase plus the step's position in this collection, counted across every
        /// phase, which tells a reader where to look but nothing about what the step was doing — worth a few
        /// words at the call site.
        /// </param>
        /// <returns>This collection, so steps can be chained.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="step" /> is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">
        /// The owning <see cref="ContextBuilder" /> has already started building — the collection is sealed
        /// at the top of <see cref="ContextBuilder.BuildAsync" /> — so this step could never run. Create
        /// another builder rather than reusing a spent one.
        /// </exception>
        public InitializerCollection Add(BootPhase phase, Func<Context, CancellationToken, Task> step,
            string name = null)
        {
            if (step == null) throw new ArgumentNullException(nameof(step));
            if (_sealed)
            {
                throw new InvalidOperationException(
                    "This InitializerCollection belongs to a ContextBuilder that has already built its " +
                    "Context, so adding a step here would silently never run. Create another builder.");
            }

            _entries.Add(new Entry(phase, step, name ?? phase + " initializer #" + _entries.Count));
            return this;
        }

        internal IReadOnlyList<Entry> Entries => _entries;

        internal void Seal() => _sealed = true;
    }
}
