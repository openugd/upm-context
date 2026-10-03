using System;
using System.Diagnostics.CodeAnalysis;

namespace OpenUGD
{
    /// <summary>
    /// Typed resolution and activation over two of the primitives <see cref="Context"/> exposes,
    /// <see cref="Context.TryResolve"/> and <see cref="Context.Instantiate"/>: the throwing counterpart of
    /// the first, generic spellings of both.
    /// </summary>
    /// <remarks>
    /// Every method here refuses a <c>null</c> context with an <see cref="ArgumentNullException"/> rather
    /// than a <see cref="NullReferenceException"/> from the extension-method call, and every one of them
    /// throws <see cref="ObjectDisposedException"/> on a context whose <see cref="Lifetime"/> has
    /// terminated — including <c>TryResolve</c>, because "the service is not registered" and "the scope is
    /// over" are different answers and only the first one is a <c>false</c>.
    /// </remarks>
    public static class ContextExtensions
    {
        /// <summary>
        /// Resolves the service registered as <typeparamref name="T"/>, or throws. Use this wherever a
        /// missing binding is a bug; use <see cref="TryResolve{T}"/> where an absence has a defined meaning.
        /// </summary>
        /// <remarks>
        /// Returns the context's singleton, not a new object: two calls hand back the same instance, and so
        /// does a resolve of any other contract the same registration answers to. A contract inherited from
        /// a parent context resolves to the parent's instance, which the parent still owns and disposes.
        /// </remarks>
        /// <typeparam name="T">The contract to resolve. Must be a registered contract — a type the
        /// implementation merely happens to implement does not resolve unless it was added with
        /// <c>As</c>.</typeparam>
        /// <param name="context">The context to resolve from.</param>
        /// <returns>The registered instance.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <c>null</c>.</exception>
        /// <exception cref="ContextException">
        /// Nothing is registered as <typeparamref name="T"/>. When a registered type does implement
        /// <typeparamref name="T"/> the message says so and suggests adding <c>.As&lt;T&gt;()</c> to that
        /// registration; failing that it names the closest registered contract, when one is close enough
        /// to be worth naming.
        /// </exception>
        /// <exception cref="ObjectDisposedException">The context has been disposed.</exception>
        public static T Resolve<T>(this Context context) => (T)Resolve(context, typeof(T));

        /// <summary>
        /// Resolves the service registered as <paramref name="contract"/>, or throws. The
        /// <see cref="Type"/>-based form, for a contract only known at run time; the caller casts.
        /// </summary>
        /// <remarks>
        /// See <see cref="Resolve{T}(Context)"/>: the same singleton, the same diagnostics.
        /// </remarks>
        /// <param name="context">The context to resolve from.</param>
        /// <param name="contract">The contract to resolve.</param>
        /// <returns>The registered instance, never <c>null</c> — a contract that resolves to <c>null</c> is
        /// reported as unresolvable instead.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> or
        /// <paramref name="contract"/> is <c>null</c>.</exception>
        /// <exception cref="ContextException">
        /// Nothing is registered as <paramref name="contract"/>.
        /// </exception>
        /// <exception cref="ObjectDisposedException">The context has been disposed.</exception>
        public static object Resolve(this Context context, Type contract)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (contract == null) throw new ArgumentNullException(nameof(contract));

            object service;
            if (context.TryResolve(contract, out service)) return service;

            throw new ContextException(Diagnostics.UnableToResolve(contract, null, null, null, context.Contracts));
        }

        /// <summary>
        /// Resolves the service registered as <typeparamref name="T"/>, or reports that there is none. The
        /// non-throwing half of the pair, for a collaborator whose absence the caller has a plan for.
        /// </summary>
        /// <remarks>
        /// Only absence is reported as <c>false</c>. A disposed context still throws, because that is not an
        /// absent service, it is a scope that has ended — and the object it would hand back would already
        /// have been disposed.
        /// </remarks>
        /// <typeparam name="T">The contract to resolve.</typeparam>
        /// <param name="context">The context to resolve from.</param>
        /// <param name="service">Receives the registered instance, or <c>default(T)</c> when this returns
        /// <c>false</c>.</param>
        /// <returns>Whether <typeparamref name="T"/> is registered in this context or inherited from a
        /// parent.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <c>null</c>.</exception>
        /// <exception cref="ObjectDisposedException">The context has been disposed.</exception>
        public static bool TryResolve<T>(this Context context, out T service)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            object resolved;
            if (context.TryResolve(typeof(T), out resolved))
            {
                service = (T)resolved;
                return true;
            }

            service = default(T);
            return false;
        }

        /// <summary>
        /// Constructs a new <typeparamref name="T"/>, filling its constructor parameters and its
        /// <see cref="InjectAttribute"/> members from the context, and hands it to the caller.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Nothing is registered and nothing is cached.</b> Every call constructs a fresh object; the
        /// context does not remember it, and a later <c>Resolve&lt;T&gt;</c> still fails unless
        /// <typeparamref name="T"/> was registered. This is the factory side of the container, for the
        /// short-lived objects a scope produces many of.
        /// </para>
        /// <para>
        /// <b>The instance is yours, including its disposal.</b> An <see cref="IDisposable"/> made here is
        /// <i>not</i> registered on the context's <see cref="Lifetime"/> — the container disposes only what
        /// it owns. Register it on a lifetime yourself if it needs releasing.
        /// </para>
        /// <para>
        /// The constructor is chosen per call, by the build's rule: the one marked
        /// <see cref="InjectAttribute"/> if there is one, otherwise the widest public constructor whose
        /// parameters can all be supplied. Two equally wide constructors that both can be are an error, not
        /// a choice.
        /// </para>
        /// </remarks>
        /// <typeparam name="T">The concrete type to construct. An interface, an abstract type, a value type,
        /// an open generic or a type deriving from <c>UnityEngine.Object</c> is a failure, not a
        /// resolution.</typeparam>
        /// <param name="context">The context to resolve the dependencies from.</param>
        /// <returns>The new instance.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <c>null</c>.</exception>
        /// <exception cref="ContextException">
        /// <typeparamref name="T"/> cannot be activated, or no constructor of it could be satisfied — the
        /// message names the parameter that could not be supplied — or two equally wide constructors could
        /// both be, or its constructor threw, in which case the original exception is the
        /// <see cref="Exception.InnerException"/>.
        /// </exception>
        /// <exception cref="ObjectDisposedException">The context has been disposed.</exception>
        public static T Instantiate<[DynamicallyAccessedMembers(Trimming.Constructors)] T>(this Context context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return (T)context.Instantiate(typeof(T), null);
        }

        /// <summary>
        /// Constructs a new <typeparamref name="T"/> from a mix of arguments you supply and services the
        /// context resolves — for the parameters a container cannot know, such as the model an object is
        /// being built for.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Matching is by type, not by position.</b> Each constructor parameter, left to right, takes the
        /// first argument assignable to it that no earlier parameter has taken; whatever is left unmatched
        /// is resolved from the context. So the order of <paramref name="args"/> only matters between
        /// arguments of the same type.
        /// </para>
        /// <para>
        /// A <c>null</c> entry in <paramref name="args"/> never matches anything — it cannot be typed — so
        /// the parameter it was meant for is resolved from the context instead, and fails there if nothing
        /// is registered. To pass an intentional <c>null</c>, take it through a wrapper or a factory
        /// delegate. Arguments that match nothing are ignored rather than reported.
        /// </para>
        /// <para>
        /// Constructor selection is as in <see cref="Instantiate{T}(Context)"/>, with the arguments counting
        /// towards what "can be satisfied" means. If a constructor is marked <see cref="InjectAttribute"/>
        /// it is the only one tried, so an argument that fits no parameter of it does not fall through to
        /// another constructor.
        /// </para>
        /// <para>
        /// The instance is the caller's, exactly as in <see cref="Instantiate{T}(Context)"/>: unregistered,
        /// uncached, and never disposed by the context.
        /// </para>
        /// </remarks>
        /// <typeparam name="T">The concrete type to construct.</typeparam>
        /// <param name="context">The context to resolve the remaining dependencies from.</param>
        /// <param name="args">
        /// The values to prefer over resolution. Passing none reaches the other overload, which allocates no
        /// array.
        /// </param>
        /// <returns>The new instance.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is <c>null</c>.</exception>
        /// <exception cref="ContextException">
        /// <typeparamref name="T"/> cannot be activated, no constructor could be satisfied from these
        /// arguments and this context, two equally wide constructors both could be, or its constructor
        /// threw.
        /// </exception>
        /// <exception cref="ObjectDisposedException">The context has been disposed.</exception>
        public static T Instantiate<[DynamicallyAccessedMembers(Trimming.Constructors)] T>(this Context context,
            params object[] args)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            return (T)context.Instantiate(typeof(T), args);
        }
    }
}
