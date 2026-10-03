using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace OpenUGD
{
    /// <summary>
    /// Anything that carries a scope. Implemented by <see cref="Context" /> so a collaborator can ask for
    /// the scope alone, rather than taking the whole container to reach one property of it.
    /// </summary>
    public interface ILifetimeProvider
    {
        /// <summary>
        /// The scope this object lives in, and the one anything derived from it should nest inside.
        /// </summary>
        Lifetime Lifetime { get; }
    }

    /// <summary>
    /// The built container: a fixed set of singletons, resolvable by contract, that die together when its
    /// <see cref="Lifetime" /> terminates. Obtained only from <see cref="ContextBuilder.BuildAsync" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three methods do the work</b>, plus <see cref="Dispose" />, which ends the scope.
    /// <see cref="TryResolve" /> answers, <see cref="Instantiate" /> builds something the container will not
    /// own, <see cref="Inject" /> fills in an object the container did not build. The ergonomics —
    /// <c>Resolve&lt;T&gt;</c>, <c>TryResolve&lt;T&gt;</c>, <c>Instantiate&lt;T&gt;</c> — live in
    /// <see cref="ContextExtensions" />, so a project can add its own without forking the package.
    /// </para>
    /// <para>
    /// <b>Everything already exists.</b> By the time a <see cref="Context" /> is handed out, every
    /// registered service has been constructed, injected and booted; resolution is then a dictionary lookup
    /// and an array read — no construction, no reflection, no allocation, no lock. That is what makes
    /// concurrent resolution safe, and what lets <see cref="StartupMode.Parallel" /> run a whole dependency
    /// rank at once.
    /// </para>
    /// <para>
    /// <b>Scope, not service lifetimes.</b> There is no transient and no scoped registration. A one-off
    /// object is <see cref="Instantiate" /> and belongs to the caller; a narrower scope is a child context
    /// on a shorter <see cref="OpenUGD.Lifetime" />, made with <see cref="CreateBuilder" />. A child sees
    /// its parent's registrations and shadows whatever it re-registers, but a parent-registered singleton
    /// is always constructed and cached in the parent, so it can never become captive in a scope shorter
    /// than its own.
    /// </para>
    /// <para>
    /// <b>Teardown.</b> Terminating the <see cref="Lifetime" /> — directly, through an ancestor lifetime, or
    /// via <see cref="Dispose" /> — disposes every <see cref="IDisposable" /> the context constructed, in
    /// reverse construction order. Instances handed over with <c>AddInstance</c> were not constructed here
    /// and are not disposed here; nor is anything inherited from a parent, which the parent still owns.
    /// </para>
    /// <para>
    /// <b>Ownership goes by the object, not by the registration.</b> One object may answer to several
    /// registrations — a factory that forwards to another service, <c>Add&lt;IFoo&gt;(c =&gt;
    /// c.Resolve&lt;Foo&gt;())</c>; the same object handed to <c>AddInstance</c> twice; a factory returning an
    /// <c>AddInstance</c> object; a child registering an object its parent already holds. Such an object is
    /// injected, booted and disposed at most once, by the context that first holds it, and only if that
    /// context made it: an <c>AddInstance</c> object is never disposed, and an object of any ancestor context
    /// is never injected, booted or disposed by a child, which only hands it out.
    /// </para>
    /// <para>
    /// <b>Engine-free.</b> Nothing in this file references <c>UnityEngine</c>. A context can be built and
    /// asserted on in a plain unit test, a console app or a headless build.
    /// </para>
    /// </remarks>
    public sealed class Context : IServiceProvider, ILifetimeProvider, IDisposable
    {
        private readonly ContextScope _scope;
        private readonly Lifetime _lifetime;
        private readonly Context _parent;
        private readonly Dictionary<Type, int> _map;
        private readonly object[] _instances;
        private ContextPlan _plan;

        internal Context(ContextScope scope, Context parent, ContextPlan plan)
        {
            _scope = scope;
            _lifetime = scope.Lifetime;
            _parent = parent;
            _map = plan.Map;
            _instances = plan.Instances;
            _plan = plan;
        }

        /// <summary>
        /// Starts describing a context. This is the entry point to the whole package: there is no public
        /// constructor, and <see cref="ContextBuilder.BuildAsync" /> is the only way a
        /// <see cref="Context" /> comes into existence.
        /// </summary>
        /// <remarks>
        /// <b>A lifetime or a parent that has already ended</b> gives a builder that is born terminated, the
        /// same rule <see cref="OpenUGD.Lifetime.DefineNested" /> follows for a terminated lifetime, so code
        /// racing a scope's end does not have to test it first: its <see cref="ContextBuilder.Lifetime" /> is
        /// already terminated, registering on it still works, and <see cref="ContextBuilder.BuildAsync" />
        /// throws <see cref="OperationCanceledException" /> without constructing anything — exactly what it
        /// does when the scope ends a moment after this call.
        /// </remarks>
        /// <param name="lifetime">
        /// The scope the new context lives within. The context gets a lifetime of its own that ends when this
        /// one ends, so disposing the context does not touch this lifetime, while terminating this lifetime
        /// does dispose the context — or, if it is still being built, cancels the build and disposes what it
        /// constructed once the boot steps in flight have finished. Defaults to
        /// <paramref name="parent" />'s lifetime, or to <see cref="OpenUGD.Lifetime.Eternal" /> when there
        /// is no parent either — which is a process-long scope, so pass one for anything shorter.
        /// </param>
        /// <param name="parent">
        /// The context to inherit registrations from, or <c>null</c> for a root. A child resolves whatever
        /// the parent can and shadows any contract it registers itself; inherited singletons stay the
        /// parent's, built and disposed there. A child also ends when its parent ends, whatever
        /// <paramref name="lifetime" /> it was given — before the parent's own services are disposed — so it
        /// never outlives the services it hands out.
        /// </param>
        /// <returns>
        /// A single-use builder: calling <see cref="ContextBuilder.BuildAsync" /> on it a second time
        /// throws instead of rebuilding.
        /// </returns>
        public static ContextBuilder CreateBuilder(Lifetime lifetime = null, Context parent = null) =>
            new ContextBuilder(lifetime, parent);

        /// <summary>
        /// The scope every singleton in this context is tied to: when it terminates, they are disposed and
        /// this context stops answering. It ends when the lifetime passed to <see cref="CreateBuilder" /> ends
        /// and, for a child, when its parent ends, so it can end earlier than either but never later — except
        /// that an end arriving while the context is still being built waits for the boot steps in flight to
        /// finish.
        /// </summary>
        /// <remarks>
        /// This is the lifetime to hand to anything whose life should match the context's — a subscription,
        /// a nested scope, a cancellation token via <c>AsCancellationToken</c>. It is also resolvable, so a
        /// service can simply take a <see cref="OpenUGD.Lifetime" /> as a constructor parameter.
        /// </remarks>
        public Lifetime Lifetime => _lifetime;

        /// <summary>
        /// The context this one inherits registrations from, or <c>null</c> if this is a root. Present for
        /// walking the chain while debugging; every contract the parent could answer was copied into this
        /// context when it was built, so ordinary code never needs to reach through here to resolve.
        /// </summary>
        public Context Parent => _parent;

        /// <summary>
        /// Looks up the singleton registered for <paramref name="contract" />. The primitive every other
        /// resolve in the package is built on, and the one that reports an unregistered contract as a
        /// <c>false</c> rather than as the exception <c>Resolve&lt;T&gt;</c> would throw.
        /// </summary>
        /// <remarks>
        /// A hit is a dictionary lookup and an array read — no allocation, no reflection, no construction:
        /// everything was built during <see cref="ContextBuilder.BuildAsync" />. Inherited contracts cost
        /// exactly the same as local ones, because a child copies the parent's reference rather than
        /// forwarding the call. Use <c>Resolve&lt;T&gt;</c> from <see cref="ContextExtensions" /> when the
        /// absence of a service is a bug you want reported as one.
        /// </remarks>
        /// <param name="contract">
        /// The contract to look up — the implementation type itself, or any type it was registered
        /// <c>As</c>. Base classes and interfaces that were not registered do not match: lookup is by exact
        /// type, not by assignability.
        /// </param>
        /// <param name="service">The singleton, or <c>null</c> when this returns <c>false</c>.</param>
        /// <returns><c>true</c> if the contract is registered here or inherited from an ancestor.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="contract" /> is <c>null</c>.</exception>
        /// <exception cref="ObjectDisposedException">
        /// This context's <see cref="Lifetime" /> has terminated, so every service it created has already
        /// been disposed and handing one out would hand out a disposed object.
        /// </exception>
        public bool TryResolve(Type contract, out object service)
        {
            if (contract == null) throw new ArgumentNullException(nameof(contract));
            if (_lifetime.IsTerminated) throw Disposed();

            int slot;
            if (_map.TryGetValue(contract, out slot))
            {
                var plan = _plan;
                service = plan == null ? _instances[slot] : plan.Acquire(this, slot);
                if (service != null) return true;
            }

            service = null;
            return false;
        }

        /// <summary>
        /// Constructs a fresh object that the container does <i>not</i> own: unregistered, uncached, and
        /// never disposed by this context. This is what replaces a transient service lifetime.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Ownership is the caller's.</b> Nothing here is tracked, so an <see cref="IDisposable" />
        /// result is yours to dispose — deliberately, since a container that tracks disposable transients
        /// is a well-known way to leak. Register the lifetime yourself if you want the context to end it.
        /// </para>
        /// <para>
        /// <b>How a constructor is chosen.</b> As for a registration: if exactly one constructor is marked
        /// <c>[Inject]</c> it is used; otherwise the widest public constructor that can be fully satisfied,
        /// and two equally wide ones that both can be are an error, not a choice. Unlike a registration,
        /// this choice is made now rather than validated at build time, so a failure surfaces here, at the
        /// call site.
        /// </para>
        /// <para>
        /// <b>Cost.</b> Reflection, on every call. The per-type metadata is cached and the cache is locked,
        /// so this is safe to call concurrently, but it is not the thing to do in a hot loop.
        /// </para>
        /// <para>
        /// <b>Managed code stripping.</b> <paramref name="type"/> is annotated for Unity's linker, so
        /// <c>Instantiate(typeof(X))</c> keeps <c>X</c>'s constructors in a stripped build. A
        /// <see cref="Type"/> the linker cannot trace back to a <c>typeof</c> does not keep them: put
        /// <see cref="InjectAttribute"/> on the constructor to call, or give the field or parameter that
        /// carries the type the same annotation.
        /// </para>
        /// </remarks>
        /// <param name="type">
        /// The concrete type to construct. Interfaces, abstract classes, value types, arrays and open
        /// generics are rejected — there is no binding step here, this is the type that gets built.
        /// </param>
        /// <param name="args">
        /// Values to use in place of resolved services. Each constructor parameter takes the first unused
        /// argument assignable to it, and anything left over is resolved from this context; extra arguments
        /// are ignored and <c>null</c> entries never match. Matching is by type, not by position.
        /// </param>
        /// <returns>The new instance, with its <c>[Inject]</c> members already filled in.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="type" /> is <c>null</c>.</exception>
        /// <exception cref="ObjectDisposedException">
        /// This context's <see cref="Lifetime" /> has terminated.
        /// </exception>
        /// <exception cref="ContextException">
        /// <paramref name="type" /> cannot be activated; it has more than one <c>[Inject]</c> constructor;
        /// two equally wide public constructors can both be satisfied;
        /// no public constructor could be satisfied from <paramref name="args" /> and this context — the
        /// message names the parameter that could not be supplied, with the suggestion a build error would
        /// make (a registered type that implements its contract, or the nearest registered name); the
        /// constructor threw, in which case it is the <see cref="Exception.InnerException" />; or member
        /// injection failed, as for <see cref="Inject" />.
        /// </exception>
        public object Instantiate([DynamicallyAccessedMembers(Trimming.Constructors)] Type type,
            object[] args = null)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (_lifetime.IsTerminated) throw Disposed();

            var instance = Activation.Instantiate(this, type, args);
            Inject(instance);
            return instance;
        }

        /// <summary>
        /// Fills in the <c>[Inject]</c> fields and properties of an object this container did not build —
        /// a <c>MonoBehaviour</c>, a view, anything handed to you already constructed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Members are collected from the whole inheritance chain, base class first, private ones included,
        /// and assigned by contract. A member marked <c>[Inject(Optional = true)]</c> whose contract is not
        /// registered is left exactly as it was, so a field initializer survives as a fallback; any other
        /// unsatisfied member is an error naming the member and its contract, followed — when the container
        /// can find one — by a registered type that implements that contract but was never registered as it,
        /// or by the nearest registered name.
        /// </para>
        /// <para>
        /// Nothing is remembered: calling this twice injects twice, and the object is not retained by the
        /// context, so it is neither disposed nor kept alive by it.
        /// </para>
        /// </remarks>
        /// <param name="target">The object to fill in. Its runtime type decides which members are injected,
        /// so a subclass's members are found even when the caller holds a base-typed reference.</param>
        /// <exception cref="ArgumentNullException"><paramref name="target" /> is <c>null</c>.</exception>
        /// <exception cref="ObjectDisposedException">
        /// This context's <see cref="Lifetime" /> has terminated.
        /// </exception>
        /// <exception cref="ContextException">
        /// A non-optional <c>[Inject]</c> member has no registered contract — the message names the member
        /// and, where it can find one, suggests a contract to use instead — or the type's metadata for
        /// injection is invalid: a <c>readonly</c> or <c>const</c> field, an indexer, a property with no
        /// setter, or <c>Optional</c> on a non-nullable value type.
        /// </exception>
        public void Inject(object target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (_lifetime.IsTerminated) throw Disposed();

            var type = target.GetType();
            string error;
            var members = Activation.GetInjectMembers(type, out error);
            if (error != null) throw new ContextException(error);

            for (var i = 0; i < members.Length; i++)
            {
                var member = members[i];
                object value;
                if (!TryResolve(member.Contract, out value))
                {
                    // Optional: leave the member exactly as it was, so a field initializer survives as a
                    // fallback and the caller's `?.` is the whole contract.
                    if (member.Optional) continue;

                    throw new ContextException(Diagnostics.UnableToResolveMember(member, type, null, Contracts));
                }

                member.SetValue(target, value);
            }
        }

        /// <summary>
        /// Terminates this context's <see cref="Lifetime" />, which disposes every <see cref="IDisposable" />
        /// singleton it constructed, in reverse construction order, and makes every later resolve throw.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Idempotent: a second call does nothing. It is also not the only way a context ends — terminating
        /// any ancestor lifetime does the same thing, which is the usual arrangement, and is why a context
        /// is safe to simply forget once its scope is defined.
        /// </para>
        /// <para>
        /// Called on the context still being built — from a registration factory or a boot step — it cancels
        /// the build instead: <see cref="ContextBuilder.BuildAsync" /> throws
        /// <see cref="OperationCanceledException" />, and the teardown runs once the boot steps in flight
        /// have finished, never under them.
        /// </para>
        /// <para>
        /// What is <i>not</i> disposed: an instance handed over ready-made, which this context never owned,
        /// even when a factory registration returns it too; and any object an ancestor context holds,
        /// whether inherited from <see cref="Parent" /> or registered here again, which that ancestor still
        /// owns and will dispose with its own lifetime. An object registered here under several contracts or
        /// registrations is disposed once. Child contexts nested in this one are terminated with it.
        /// </para>
        /// </remarks>
        /// <exception cref="Exception">
        /// Exactly one service threw while disposing: that exception, rethrown with its original stack
        /// trace. Every other service was still disposed — one that fails to shut down cannot leave its
        /// siblings undisposed.
        /// </exception>
        /// <exception cref="AggregateException">
        /// Two or more services threw while disposing, in the order they failed. Every service was still
        /// disposed.
        /// </exception>
        public void Dispose() => _scope.End();

        /// <summary>
        /// The BCL service-locator face of <see cref="TryResolve" />, for library code that takes an
        /// <see cref="IServiceProvider" />. Returns <c>null</c> for an unregistered contract, as that
        /// interface requires, rather than throwing the way <c>Resolve&lt;T&gt;</c> would.
        /// </summary>
        /// <param name="serviceType">The contract to look up.</param>
        /// <returns>The singleton, or <c>null</c> if nothing is registered for it.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="serviceType" /> is <c>null</c>; the
        /// null-means-absent rule does not extend to the argument.</exception>
        /// <exception cref="ObjectDisposedException">
        /// This context's <see cref="Lifetime" /> has terminated.
        /// </exception>
        object IServiceProvider.GetService(Type serviceType)
        {
            object service;
            TryResolve(serviceType, out service);
            return service;
        }

        internal void EndBuild() => _plan = null;

        /// Every object this context hands out, by slot - its own, its automatic Context and Lifetime, and
        /// what it inherited. Read while building a child, to tell an ancestor's objects from the child's.
        internal object[] Table => _instances;

        internal bool CanResolve(Type contract) => _map.ContainsKey(contract);

        internal IEnumerable<Type> Contracts => _map.Keys;

        internal IEnumerable<KeyValuePair<Type, object>> Instances
        {
            get
            {
                foreach (var pair in _map)
                {
                    yield return new KeyValuePair<Type, object>(pair.Key, _instances[pair.Value]);
                }
            }
        }

        private ObjectDisposedException Disposed() =>
            new ObjectDisposedException(nameof(Context),
                "This Context has been disposed: its Lifetime terminated, so every service it created has " +
                "already been disposed and handing one out would hand out a disposed object. Capture what " +
                "you need in a field before the scope ends, or check Lifetime.IsTerminated first.");
    }
}
