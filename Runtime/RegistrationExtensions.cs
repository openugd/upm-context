using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace OpenUGD
{
    /// <summary>
    /// What keeps a registration block reading as one chain: the generic spelling of
    /// <see cref="Registration.As"/>, and a bridge back to the owning <see cref="ServiceCollection"/> so the
    /// next service can be registered without breaking out to <see cref="ContextBuilder.Services"/> again.
    /// </summary>
    public static class RegistrationExtensions
    {
        /// <summary>
        /// Points <typeparamref name="TContract"/> at this registration — the generic spelling of
        /// <see cref="Registration.As"/>, with exactly the same meaning: it <b>adds</b> a contract. Whatever
        /// the registration already answered to stays resolvable, and every contract on it hands out the one
        /// same instance.
        /// </summary>
        /// <remarks>
        /// Assignability is checked by <see cref="ContextBuilder.BuildAsync"/>, not here, so a contract the
        /// implementation does not implement is reported with the registration's file and line rather than
        /// at this call. Adding a contract the registration already has does nothing.
        /// </remarks>
        /// <typeparam name="TContract">The type to resolve to this instance.</typeparam>
        /// <param name="registration">The registration to add the contract to.</param>
        /// <returns>The same handle, so contracts chain: <c>.As&lt;IA&gt;().As&lt;IB&gt;()</c>.</returns>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="registration"/> is <c>default(Registration)</c> and refers to no registration, or
        /// the owning <see cref="ContextBuilder"/> has already built its <see cref="Context"/>.
        /// </exception>
        public static Registration As<TContract>(this Registration registration) =>
            registration.As(typeof(TContract));

        /// <summary>
        /// Starts a <b>new</b> registration in the collection this one belongs to, for
        /// <typeparamref name="TImpl"/> under itself.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It does not touch the registration it is called on. The pair is easy to mix up and the difference
        /// is the whole point: <c>As&lt;T&gt;</c> adds a contract to the current service,
        /// <c>Add&lt;T&gt;</c> moves on to the next one. It exists so a registration block never has to
        /// break its chain:
        /// </para>
        /// <code>
        /// builder.Services
        ///     .Add&lt;Alpha&gt;().As&lt;IAlpha&gt;()
        ///     .Add&lt;Beta&gt;().As&lt;IBeta&gt;();
        /// </code>
        /// <para>
        /// The new registration behaves exactly as <see cref="ServiceCollection.Add"/> made it — constructed
        /// by the container, one instance per context, disposed with the context if it implements
        /// <see cref="IDisposable"/> — and the site recorded for diagnostics is this call, not the
        /// registration it chained off.
        /// </para>
        /// </remarks>
        /// <typeparam name="TImpl">The concrete type to register, and its first contract.</typeparam>
        /// <param name="registration">Any registration from the collection to add to; used only to find that
        /// collection.</param>
        /// <param name="file">[compiler-supplied] The call site's file, for diagnostics. Do not pass
        /// it.</param>
        /// <param name="line">[compiler-supplied] The call site's line. Do not pass it.</param>
        /// <returns>
        /// A handle to the <i>new</i> registration — not to <paramref name="registration"/>.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="registration"/> is <c>default(Registration)</c> and so names no collection, or
        /// the owning <see cref="ContextBuilder"/> has already built its <see cref="Context"/>.
        /// </exception>
        public static Registration Add<[DynamicallyAccessedMembers(Trimming.Constructors)] TImpl>(
            this Registration registration,
            [CallerFilePath] string file = null, [CallerLineNumber] int line = 0)
            where TImpl : class
        {
            var services = registration.Services;
            if (services == null)
            {
                throw new InvalidOperationException(
                    "default(Registration) does not refer to a ServiceCollection. Obtain a Registration from " +
                    "ServiceCollection.Add.");
            }

            return services.Add(typeof(TImpl), null, file, line);
        }
    }
}
