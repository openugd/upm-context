using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace OpenUGD
{
    /// <summary>
    /// The registrations a <see cref="ContextBuilder"/> will compile into a <see cref="Context"/>. Reached
    /// as <see cref="ContextBuilder.Services"/>; it has no public constructor, because a set of
    /// registrations only means anything against the builder that owns it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing is validated while you register.</b> <see cref="Add"/> records an entry and returns.
    /// Whether the type can be activated, whether a contract is actually implemented, whether two
    /// registrations claim the same contract, and whether every dependency exists are all decided by
    /// <see cref="ContextBuilder.BuildAsync"/>, which reports every problem it found at once instead of the
    /// first one. Each entry remembers the file and line it was written at, so those messages point back at
    /// the registration rather than at the container.
    /// </para>
    /// <para>
    /// <b>One registration per contract.</b> Two entries claiming the same contract is a build error, not a
    /// last-one-wins overwrite. To supply a default only when nothing else does, use <c>TryAdd</c>; to
    /// override something, register it in a child context
    /// (<c>Context.CreateBuilder(parent: builtContext)</c>), where a local registration shadows the
    /// parent's.
    /// </para>
    /// <para>
    /// <b>Every registration is a singleton of its context</b>, constructed once during the build and shared
    /// by every contract it answers to, so adding contracts with <see cref="Registration.As"/> never
    /// multiplies instances. A service that implements <see cref="IDisposable"/> is disposed when the
    /// context's <see cref="Lifetime"/> terminates, in reverse construction order, and once even if several
    /// registrations return it — except an instance handed over ready-made, which the context does not own
    /// and never disposes, and an object a parent context holds, which stays the parent's.
    /// </para>
    /// <para>
    /// <b>Sealed once the builder has built.</b> Registering afterwards throws instead of silently doing
    /// nothing, because the plan has already been compiled and the new entry could never take effect.
    /// </para>
    /// <para>
    /// <b>Not thread-safe</b>, and deliberately not: registration is single-threaded setup code that runs
    /// once, and a lock on every <see cref="Add"/> would buy nothing.
    /// </para>
    /// </remarks>
    public sealed class ServiceCollection : IEnumerable<Registration>
    {
        internal sealed class Entry
        {
            internal Type Implementation;
            internal Func<Context, object> Factory;
            internal object Instance;
            internal readonly List<Type> Contracts = new List<Type>(1);
            internal string File;
            internal int Line;

            internal string Site => File == null ? null : File + ":" + Line;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly HashSet<Type> _contracts = new HashSet<Type>();
        private readonly Context _parent;
        private bool _sealed;

        internal ServiceCollection(Context parent) => _parent = parent;

        /// <summary>
        /// Registers <paramref name="implementation"/> under its own type and returns a handle for adding
        /// further contracts to that same registration.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The by-<see cref="Type"/> primitive; prefer the generic <c>Add&lt;TImpl&gt;</c> extension unless
        /// the type is only known at run time.
        /// </para>
        /// <para>
        /// <b>The self-registration is permanent.</b> <see cref="Registration.As"/> adds contracts to this
        /// entry, it never replaces the one made here, so <c>Add(typeof(Alpha)).As(typeof(IAlpha))</c> leaves
        /// both <c>Alpha</c> and <c>IAlpha</c> resolving to the one instance. Both names are claimed, so
        /// registering <c>Alpha</c> a second time anywhere in this collection is the duplicate the build
        /// rejects.
        /// </para>
        /// <para>
        /// Without a <paramref name="factory"/> the container constructs the type itself, using the
        /// constructor marked <see cref="InjectAttribute"/> if there is one and otherwise the greediest
        /// public constructor whose parameters are all registered. Members marked
        /// <see cref="InjectAttribute"/> are filled after every service in the context has been constructed,
        /// which is why two services may hold each other through members but not through constructors.
        /// </para>
        /// <para>
        /// <b>Managed code stripping.</b> <paramref name="implementation"/> is annotated for Unity's linker,
        /// so passing <c>typeof(X)</c> keeps <c>X</c>'s constructors in a stripped build. A
        /// <see cref="Type"/> the linker cannot trace back to a <c>typeof</c> — read from data, or held in an
        /// unannotated field — does not keep them: put <see cref="InjectAttribute"/> on the constructor the
        /// container should call, or list the type in a <c>link.xml</c> under <c>Assets</c>.
        /// </para>
        /// </remarks>
        /// <param name="implementation">
        /// The type to construct, and the registration's first contract. Not checked here: without a
        /// <paramref name="factory"/>, an interface, an abstract or an open generic type, or a type deriving
        /// from <c>UnityEngine.Object</c> — which only Unity can create — is reported by the build, together
        /// with the site below. With one, nothing is constructed by reflection, so an interface is a
        /// perfectly good registration.
        /// </param>
        /// <param name="factory">
        /// Called once during the build instead of invoking a constructor, with the <see cref="Context"/>
        /// under construction — which may be resolved from. It must return a non-<c>null</c> instance
        /// assignable to <paramref name="implementation"/>; either failure is a
        /// <see cref="ContextException"/> at build time. <c>null</c> means "construct it yourself".
        /// </param>
        /// <param name="file">[compiler-supplied] The call site's file, used to point diagnostics at this
        /// line. Do not pass it.</param>
        /// <param name="line">[compiler-supplied] The call site's line. Do not pass it.</param>
        /// <returns>
        /// A handle to the new entry, for <see cref="Registration.As"/>. It is a struct over this collection,
        /// so chaining allocates nothing.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="implementation"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The owning <see cref="ContextBuilder"/> has already built its <see cref="Context"/>, so this
        /// registration could never take effect.
        /// </exception>
        public Registration Add([DynamicallyAccessedMembers(Trimming.Constructors)] Type implementation,
            Func<Context, object> factory = null,
            [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
        {
            if (implementation == null) throw new ArgumentNullException(nameof(implementation));
            return AddEntry(implementation, factory, file, line);
        }

        /// The unannotated path, for a registration whose type the container never constructs by reflection:
        /// a factory registration needs no constructor kept, and routing it through the annotated Add above
        /// would make the linker warn (IL2087) about a type argument it cannot vouch for.
        internal Registration AddEntry(Type implementation, Func<Context, object> factory, string file, int line)
        {
            ThrowIfSealed();

            var entry = new Entry { Implementation = implementation, Factory = factory, File = file, Line = line };
            entry.Contracts.Add(implementation);
            _contracts.Add(implementation);
            _entries.Add(entry);
            return new Registration(this, _entries.Count - 1);
        }

        /// <summary>
        /// Whether <paramref name="contract"/> would resolve in the context being built: <c>true</c> if a
        /// registration here already claims it, or if the parent context can supply it. This is the question
        /// <c>TryAdd</c> asks, so that a child never shadows a service its parent already provides.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Answered from what has been registered <i>so far</i> — a later <see cref="Add"/> changes the
        /// answer, which is why a conditional default belongs at the end of a registration block.
        /// </para>
        /// <para>
        /// A <c>null</c> contract answers <c>false</c> rather than throwing: this is a question, not an
        /// argument check.
        /// </para>
        /// <para>
        /// Two blind spots, both deliberate. A claimed contract counts even if the implementation does not
        /// actually implement it — that is the build's job to report, not this method's. And
        /// <see cref="Context"/> and <see cref="Lifetime"/>, which every built context supplies
        /// automatically, answer <c>false</c> in a root collection; a child collection sees them through its
        /// parent.
        /// </para>
        /// </remarks>
        /// <param name="contract">The contract to look for. <c>null</c> is allowed and answers
        /// <c>false</c>.</param>
        /// <returns>Whether the contract is already available, here or from the parent context.</returns>
        public bool Contains(Type contract) =>
            contract != null &&
            (_contracts.Contains(contract) || (_parent != null && _parent.CanResolve(contract)));

        /// <summary>
        /// Walks the registrations in the order they were added, one <see cref="Registration"/> handle per
        /// entry. For inspecting a graph — logging it, asserting on it in a test — since a registration can
        /// be added to but never removed or replaced.
        /// </summary>
        /// <remarks>
        /// The handles are structs over the live collection, not a snapshot, and iteration walks by index:
        /// an entry added while enumerating is yielded too, and modifying the collection mid-iteration does
        /// not throw the way a <c>List&lt;T&gt;</c> enumerator would.
        /// </remarks>
        /// <returns>An enumerator over the registrations, in registration order.</returns>
        public IEnumerator<Registration> GetEnumerator()
        {
            for (var i = 0; i < _entries.Count; i++) yield return new Registration(this, i);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        internal Registration AddExternal(Type contract, object instance, string file, int line)
        {
            ThrowIfSealed();

            var entry = new Entry {
                Implementation = instance.GetType(), Instance = instance, File = file, Line = line
            };
            entry.Contracts.Add(contract);
            _contracts.Add(contract);
            _entries.Add(entry);
            return new Registration(this, _entries.Count - 1);
        }

        internal void Bind(int index, Type contract)
        {
            ThrowIfSealed();
            var entry = _entries[index];
            if (!entry.Contracts.Contains(contract)) entry.Contracts.Add(contract);
            _contracts.Add(contract);
        }

        internal Entry EntryAt(int index) => _entries[index];

        internal Entry[] Snapshot() => _entries.ToArray();

        internal void Seal() => _sealed = true;

        internal void ThrowIfSealed()
        {
            if (!_sealed) return;

            throw new InvalidOperationException(
                "This ServiceCollection belongs to a ContextBuilder that has already built its Context and " +
                "can no longer be changed. Registering here would silently do nothing, which is why it " +
                "throws instead. Use Context.CreateBuilder(parent: builtContext) for a child scope.");
        }
    }

    /// <summary>
    /// A handle to one entry in a <see cref="ServiceCollection"/>: what <see cref="ServiceCollection.Add"/>
    /// hands back so more contracts can be pointed at the registration it just made.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two fields — the collection and the entry's index — so a <c>.As&lt;A&gt;().As&lt;B&gt;()</c> chain
    /// allocates nothing. The handle <i>refers</i> to the entry rather than carrying it: everything it does
    /// goes back through the collection, so two copies of the same handle are the same registration.
    /// </para>
    /// <para>
    /// <b><c>default(Registration)</c> refers to nothing.</b> The API never hands one out; it comes from an
    /// uninitialized field or an explicit <c>default</c>. <see cref="Services"/> and
    /// <see cref="Implementation"/> return <c>null</c> for it, and <see cref="As"/> throws rather than
    /// quietly doing nothing.
    /// </para>
    /// </remarks>
    public readonly struct Registration
    {
        private readonly ServiceCollection _services;
        private readonly int _index;

        internal Registration(ServiceCollection services, int index)
        {
            _services = services;
            _index = index;
        }

        /// <summary>
        /// The collection this registration was made in, or <c>null</c> for <c>default(Registration)</c>.
        /// It is what lets a fluent block start the next registration without going back to
        /// <see cref="ContextBuilder.Services"/> — see the <c>Add&lt;TImpl&gt;</c> extension in
        /// <see cref="RegistrationExtensions"/>.
        /// </summary>
        public ServiceCollection Services => _services;

        /// <summary>
        /// The concrete type behind this registration: the type the container will construct, or — for an
        /// instance handed over ready-made — that instance's run-time type, which may be more derived than
        /// the contract it was registered under. <c>null</c> for <c>default(Registration)</c>.
        /// </summary>
        public Type Implementation => _services == null ? null : _services.EntryAt(_index).Implementation;

        /// <summary>
        /// Points <paramref name="contract"/> at this registration: one more type that resolves to this one
        /// instance.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>It adds a contract; it does not replace one.</b> Whatever the registration already answered to
        /// — its own implementation type from <see cref="ServiceCollection.Add"/>, or the type argument from
        /// <c>AddInstance</c> — stays resolvable, and every contract on the entry hands out the same
        /// singleton. That is what makes <c>Add&lt;Alpha&gt;().As&lt;IAlpha&gt;()</c> resolve <c>Alpha</c>
        /// and <c>IAlpha</c> to one object instead of two, and it is also why both names are claimed: a
        /// second registration of <c>Alpha</c> in this collection is then a duplicate the build rejects.
        /// </para>
        /// <para>
        /// Adding a contract this registration already has is a no-op, not an error.
        /// </para>
        /// <para>
        /// Assignability is not checked here. A contract the implementation does not implement is reported by
        /// <see cref="ContextBuilder.BuildAsync"/>, naming the file and line of the registration, alongside
        /// every other problem in the graph.
        /// </para>
        /// </remarks>
        /// <param name="contract">
        /// The type to resolve to this instance — usually an interface, but any base type the implementation
        /// is assignable to will do.
        /// </param>
        /// <returns>This same handle, so contracts chain: <c>.As(a).As(b)</c>.</returns>
        /// <exception cref="InvalidOperationException">
        /// This is <c>default(Registration)</c> and refers to no registration, or the owning
        /// <see cref="ContextBuilder"/> has already built its <see cref="Context"/>.
        /// </exception>
        /// <exception cref="ArgumentNullException"><paramref name="contract"/> is <c>null</c>.</exception>
        public Registration As(Type contract)
        {
            if (_services == null)
            {
                throw new InvalidOperationException(
                    "default(Registration) does not refer to a registration. Obtain one from " +
                    "ServiceCollection.Add.");
            }

            if (contract == null) throw new ArgumentNullException(nameof(contract));

            _services.Bind(_index, contract);
            return this;
        }
    }
}
