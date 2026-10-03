using System;
using System.Collections.Generic;

namespace OpenUGD
{
    /// <summary>
    /// A container operation failed: a service graph that could not be built, a contract that could not be
    /// resolved, a type that could not be activated, a boot step that threw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Build failures arrive all at once.</b> <see cref="ContextBuilder.BuildAsync"/> validates the whole
    /// graph before it constructs anything and reports every problem it found in one message, each as its own
    /// indented entry — most naming the file and line the registration was written at, many ending in the
    /// fix. The first line is a count, not the failure; read the list under it.
    /// </para>
    /// <para>
    /// <b>It is not the only exception the container throws.</b> A bad argument is still an
    /// <see cref="ArgumentNullException"/> or <see cref="ArgumentException"/>, using a context after
    /// <see cref="Context.Dispose"/> is still an <see cref="ObjectDisposedException"/>, and if tearing down
    /// a half-built context fails as well the builder throws an <see cref="AggregateException"/> whose first
    /// inner exception is the original failure.
    /// </para>
    /// <para>
    /// <b><see cref="Path"/> is the dependency chain a failure happened on</b>: the loop of a circular
    /// dependency, or the chain of services being constructed when a constructor or a registration factory
    /// failed. It is empty — never <c>null</c> — for every other failure.
    /// </para>
    /// </remarks>
    public sealed class ContextException : Exception
    {
        private static readonly Type[] NoPath = new Type[0];

        /// <summary>
        /// Creates a failure with no dependency chain, which is the usual case: <see cref="Path"/> is empty.
        /// </summary>
        /// <param name="message">
        /// The diagnostic. Container messages are deliberately multi-line — the problem, where it was
        /// registered, and what to do about it — so do not flatten one into a single log line.
        /// </param>
        public ContextException(string message) : base(message) => Path = NoPath;

        /// <summary>
        /// Creates a failure that wraps the exception some code the container invoked threw, so the message
        /// can say what the container was doing when it surfaced. <see cref="Path"/> is empty.
        /// </summary>
        /// <param name="message">
        /// The diagnostic: what the container was doing when the inner exception surfaced, plus the
        /// registration site or boot phase where there is one.
        /// </param>
        /// <param name="innerException">
        /// The original exception — what a constructor, a registration factory or a boot step threw.
        /// The container never swallows it; this is where it stays.
        /// </param>
        public ContextException(string message, Exception innerException) : base(message, innerException) =>
            Path = NoPath;

        /// <summary>
        /// Creates a failure that carries the dependency chain it happened on — a circular dependency, or
        /// the services being constructed when construction failed.
        /// </summary>
        /// <param name="message">The diagnostic. It already renders the chain; <paramref name="path"/> is
        /// the machine-readable copy.</param>
        /// <param name="path">
        /// The chain, in dependency order. <c>null</c> is stored as the empty chain, so <see cref="Path"/>
        /// never returns <c>null</c>.
        /// </param>
        public ContextException(string message, IReadOnlyList<Type> path) : base(message) => Path = path ?? NoPath;

        /// <summary>
        /// Creates a failure that carries both a dependency chain and the exception underneath it.
        /// </summary>
        /// <param name="message">The diagnostic.</param>
        /// <param name="path">The chain, in dependency order; <c>null</c> becomes the empty chain.</param>
        /// <param name="innerException">The original exception.</param>
        public ContextException(string message, IReadOnlyList<Type> path, Exception innerException)
            : base(message, innerException) => Path = path ?? NoPath;

        /// <summary>
        /// The dependency chain that failed, or an empty list — never <c>null</c>, so it can be iterated
        /// without a guard. Non-empty for a circular dependency and for a failure during construction.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For a cycle it lists the types around the loop in dependency order, with the type that closes the
        /// loop repeated at the end: <c>A, B, A</c> for two services that need each other, and <c>T, T</c>
        /// for a service that needs itself.
        /// </para>
        /// <para>
        /// For a constructor or a registration factory that threw, or a factory that returned <c>null</c> or
        /// the wrong type, it lists the services that were being constructed at that moment, from the one the
        /// build was constructing down to the one that failed, which is last: <c>A, B, C</c> when
        /// constructing <c>A</c> needed <c>B</c>, which needed <c>C</c>, which threw. It is just <c>C</c>
        /// when nothing was waiting for <c>C</c>.
        /// </para>
        /// <para>
        /// Each entry is the type its registration named, so a contract added with <c>As</c> never appears —
        /// though the interface a factory was registered under does.
        /// </para>
        /// <para>
        /// The same chain is already rendered into <see cref="Exception.Message"/>. This exists so a test or
        /// a tool can assert on it without parsing the text.
        /// </para>
        /// </remarks>
        public IReadOnlyList<Type> Path { get; }
    }
}
