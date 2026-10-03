using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace OpenUGD
{
    /// <summary>
    /// The typed way to register: generic spellings of <see cref="ServiceCollection.Add"/> plus the two
    /// registrations it cannot express — an object you already have, and a default that yields to whatever
    /// is registered already.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are the methods to reach for. <see cref="ServiceCollection.Add"/> itself is for types only
    /// known at run time. All of them capture the call site, so a build failure names the line the
    /// registration was written on.
    /// </para>
    /// <para>
    /// <b>Managed code stripping.</b> The type parameter of every method here that has the container
    /// construct the type is annotated for Unity's linker, so the constructors of a type written at the call
    /// site — <c>Add&lt;SaveService&gt;()</c> — survive a stripped build with no further work. A generic
    /// method of your own that forwards its type parameter here breaks that chain unless its own type
    /// parameter carries the same annotation; see the package README.
    /// </para>
    /// </remarks>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registers <typeparamref name="TImpl"/> as itself, for the container to construct: one instance
        /// per context, shared by every contract added with <c>As</c>, disposed with the context if it
        /// implements <see cref="IDisposable"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The container picks the constructor marked <see cref="InjectAttribute"/> if there is one, and
        /// otherwise the greediest public constructor whose parameters are all registered — a tie between
        /// two equally greedy satisfiable constructors is a build error rather than a coin toss. Members
        /// marked <see cref="InjectAttribute"/> are filled after every service exists, so they may be
        /// cyclic where constructor parameters may not.
        /// </para>
        /// <para>
        /// Nothing is checked at this call. An unconstructible type, a missing dependency or a contract
        /// claimed twice surfaces from <see cref="ContextBuilder.BuildAsync"/>, with this call's file and
        /// line attached.
        /// </para>
        /// </remarks>
        /// <typeparam name="TImpl">
        /// The concrete type to construct. It is the registration's first contract, and
        /// <see cref="Registration.As"/> adds more without taking it away.
        /// </typeparam>
        /// <param name="services">The collection to register in, normally
        /// <see cref="ContextBuilder.Services"/>.</param>
        /// <param name="file">[compiler-supplied] The call site's file, for diagnostics. Do not pass
        /// it.</param>
        /// <param name="line">[compiler-supplied] The call site's line. Do not pass it.</param>
        /// <returns>A handle to the new registration, for adding contracts to it.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="services"/> is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">The owning builder has already built its
        /// context.</exception>
        public static Registration Add<[DynamicallyAccessedMembers(Trimming.Constructors)] TImpl>(
            this ServiceCollection services,
            [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
            where TImpl : class
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            return services.Add(typeof(TImpl), null, file, line);
        }

        /// <summary>
        /// Registers <typeparamref name="TImpl"/> with a factory: during the build the container calls
        /// <paramref name="factory"/> once instead of invoking a constructor, then treats what comes back
        /// exactly like any other registration — one instance, member-injected, disposed with the context.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For everything a constructor cannot express: a type from another assembly, a
        /// <c>MonoBehaviour</c> pulled out of the scene, a choice between two implementations made from a
        /// setting.
        /// </para>
        /// <para>
        /// The <see cref="Context"/> handed to the factory is the one being built, and resolving from it is
        /// the supported way to get dependencies — services taken that way are still constructed first and
        /// still rank this one correctly for the boot phases. The cost is that a factory's dependencies are
        /// invisible until it runs, so a dependency cycle through one cannot be caught by the pre-build
        /// check; it surfaces during construction, as a <see cref="ContextException"/> whose
        /// <see cref="ContextException.Path"/> names the loop.
        /// </para>
        /// <para>
        /// The factory must return a non-<c>null</c> instance assignable to <typeparamref name="TImpl"/>;
        /// both failures are build errors that name the registration site. Returning a <i>subtype</i> is
        /// fine and is fully supported: member injection and boot-phase enrolment both go by what the object
        /// actually is, not by what it was registered as.
        /// </para>
        /// <para>
        /// <b>Returning an object that already has an owner is fine too.</b> A forwarding factory,
        /// <c>Add&lt;IFoo&gt;(c =&gt; c.Resolve&lt;Foo&gt;())</c>, returns another registration's object; a
        /// factory may also return an <c>AddInstance</c> object, or an object a parent context holds. That
        /// object keeps its owner: it is not injected, booted or disposed a second time, an
        /// <c>AddInstance</c> object is never disposed, and a parent's object is left entirely to the parent.
        /// </para>
        /// </remarks>
        /// <typeparam name="TImpl">The registered type, and the registration's first contract. What the
        /// factory returns must be assignable to it.</typeparam>
        /// <param name="services">The collection to register in.</param>
        /// <param name="factory">
        /// Builds the instance, once, during the build. Receives the context under construction. If it
        /// throws, the exception propagates out of the build unwrapped — unlike a constructor's, which the
        /// container catches to say which service it belonged to — and everything constructed so far is
        /// disposed.
        /// </param>
        /// <param name="file">[compiler-supplied] The call site's file, for diagnostics. Do not pass
        /// it.</param>
        /// <param name="line">[compiler-supplied] The call site's line. Do not pass it.</param>
        /// <returns>A handle to the new registration, for adding contracts to it.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="services"/> or
        /// <paramref name="factory"/> is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">The owning builder has already built its
        /// context.</exception>
        public static Registration Add<TImpl>(this ServiceCollection services, Func<Context, TImpl> factory,
            [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
            where TImpl : class
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (factory == null) throw new ArgumentNullException(nameof(factory));

            // The factory constructs the instance in your code, where the linker can see the constructor,
            // so nothing has to be kept for this registration: the unannotated path.
            return services.AddEntry(typeof(TImpl), factory, file, line);
        }

        /// <summary>
        /// Registers an object that already exists, under <typeparamref name="TContract"/> and nothing else.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The concrete type is not registered.</b> This is the one place the container does not add a
        /// self-registration, because it has only your word for what the object is a contract for. Its
        /// run-time type resolves only if you add it, with
        /// <c>AddInstance&lt;IClock&gt;(clock).As&lt;SystemClock&gt;()</c>.
        /// <see cref="Registration.As"/> behaves as it does everywhere else — it adds contracts, and every
        /// one of them hands out this same object.
        /// </para>
        /// <para>
        /// <b>The context does not own it.</b> The instance is never constructed and never disposed here,
        /// even when it implements <see cref="IDisposable"/>, and even when a factory registration returns it
        /// as well — handing over an object you already hold means its lifetime is someone else's, and
        /// disposing it with a scope that merely borrowed it would be the wrong default. Tie it to a
        /// <see cref="Lifetime"/> yourself if it needs one.
        /// </para>
        /// <para>
        /// <b>It is still injected into</b>, and booted if it implements a boot phase — once, however many
        /// registrations name it. Members marked <see cref="InjectAttribute"/> on the instance are filled
        /// during the build, by its run-time type, exactly as for a service the container built. A missing
        /// binding for a non-optional member fails the build even though the object was handed over
        /// ready-made. The exception is an object a parent context already holds: the parent injected and
        /// booted it, so a child hands it out and leaves it alone.
        /// </para>
        /// </remarks>
        /// <typeparam name="TContract">
        /// The contract to register under. Usually an interface, and typically less derived than
        /// <paramref name="instance"/>.
        /// </typeparam>
        /// <param name="services">The collection to register in.</param>
        /// <param name="instance">The object to hand over. Must not be <c>null</c>: a registration that
        /// resolves to nothing is a wiring bug, not a feature.</param>
        /// <param name="file">[compiler-supplied] The call site's file, for diagnostics. Do not pass
        /// it.</param>
        /// <param name="line">[compiler-supplied] The call site's line. Do not pass it.</param>
        /// <returns>A handle to the new registration, for adding contracts to it.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="services"/> or
        /// <paramref name="instance"/> is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">The owning builder has already built its
        /// context.</exception>
        public static Registration AddInstance<TContract>(this ServiceCollection services, TContract instance,
            [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
            where TContract : class
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            return services.AddExternal(typeof(TContract), instance, file, line);
        }

        /// <summary>
        /// Registers <typeparamref name="TImpl"/> as <typeparamref name="TContract"/> only if nothing
        /// supplies that contract yet — how a module offers a default without fighting whoever else
        /// registered one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The test is <see cref="ServiceCollection.Contains"/>, so it also sees the parent context: a child
        /// scope will not shadow a service its parent already provides, and the two keep resolving to the
        /// same object.
        /// </para>
        /// <para>
        /// <b>It answers for the moment it is called.</b> Once it has put the default in, a registration of
        /// the same contract added <i>afterwards</i> collides with it and the build rejects the pair.
        /// Defaults therefore belong after the registrations they defer to.
        /// </para>
        /// <para>
        /// <b>Only <typeparamref name="TContract"/> is tested.</b> When it does register, it registers the
        /// way everything else does — <typeparamref name="TImpl"/> under itself, plus the contract — so a
        /// second registration of <typeparamref name="TImpl"/> elsewhere still collides at build time.
        /// </para>
        /// </remarks>
        /// <typeparam name="TContract">The contract to supply a default for.</typeparam>
        /// <typeparam name="TImpl">The implementation to register, if the contract is still free.</typeparam>
        /// <param name="services">The collection to register in.</param>
        /// <param name="file">[compiler-supplied] The call site's file, for diagnostics. Do not pass
        /// it.</param>
        /// <param name="line">[compiler-supplied] The call site's line. Do not pass it.</param>
        /// <returns>
        /// <c>true</c> if it registered; <c>false</c> if the contract was already available and nothing was
        /// added. There is deliberately no <see cref="Registration"/> to return in the second case — a
        /// caller that wants to add contracts should use <c>Add</c> and own the registration.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="services"/> is <c>null</c>.</exception>
        /// <exception cref="InvalidOperationException">
        /// The owning builder has already built its context <i>and</i> this call would have registered; a
        /// call that finds the contract already supplied returns <c>false</c> without touching the sealed
        /// collection.
        /// </exception>
        public static bool TryAdd<TContract, [DynamicallyAccessedMembers(Trimming.Constructors)] TImpl>(
            this ServiceCollection services,
            [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
            where TImpl : class, TContract
        {
            if (services == null) throw new ArgumentNullException(nameof(services));
            if (services.Contains(typeof(TContract))) return false;

            services.Add(typeof(TImpl), null, file, line).As(typeof(TContract));
            return true;
        }
    }
}
