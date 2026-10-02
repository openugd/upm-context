using System;
using System.ComponentModel;

namespace OpenUGD
{
    /// <summary>
    /// Marks a field or property for injection after construction, or marks the one constructor the container
    /// must use.
    /// </summary>
    /// <remarks>
    /// Constructor injection is the primary mechanism and needs no attribute: the container picks the
    /// greediest constructor it can satisfy. Marking a constructor overrides that choice. Member injection
    /// exists for objects the container did not construct — a <c>MonoBehaviour</c>, a widget, anything handed
    /// to <see cref="Context.Inject" /> — and for breaking a cycle between two services that would otherwise
    /// have to hold each other through their constructors.
    /// <para>
    /// <b>Managed code stripping keeps what this marks.</b> Unity's linker treats the attribute as a
    /// <c>Preserve</c> attribute, so a marked field, property or constructor survives every stripping level,
    /// and so does the attribute itself, which the container reads at run time. A type with a marked member
    /// is therefore kept even when nothing else uses it. Marking a constructor is also how to keep it when
    /// the type reaches the container in a way the linker cannot follow, such as through a generic helper
    /// of your own; see the package README.
    /// </para>
    /// </remarks>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Constructor,
        AllowMultiple = false, Inherited = true)]
    public class InjectAttribute : Internal.PreserveAttribute
    {
        /// <summary>
        /// When <c>true</c>, the member is injected only if its contract is registered; when nothing is
        /// registered the member is left exactly as it was, and neither the build nor
        /// <see cref="Context.Inject" /> fails. Defaults to <c>false</c>, so a missing binding is an error —
        /// the container's whole point.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Use this for a collaborator whose absence has a defined meaning, not to silence a build error. A
        /// localization service is the archetype: with one, text is translated; without one, the raw key is
        /// shown. Something the object cannot work without is not optional, and marking it so only moves the
        /// failure to a <c>NullReferenceException</c> somewhere unrelated.
        /// </para>
        /// <para>
        /// The member keeps whatever it already held, so a field initializer survives as a fallback:
        /// <c>[Inject(Optional = true)] private IClock _clock = SystemClock.Instance;</c>.
        /// </para>
        /// <para>
        /// Only reference types and <see cref="Nullable{T}" /> may be optional. On any other value type an
        /// unsatisfied member would be left at <c>0</c> or <c>false</c>, which is indistinguishable from an
        /// injected value, so it is rejected as an error.
        /// </para>
        /// </remarks>
        public bool Optional { get; set; }
    }
}

namespace OpenUGD.Internal
{
    /// <summary>
    /// The base of <see cref="InjectAttribute" />, and nothing else: it is what makes Unity's linker keep
    /// every <c>[Inject]</c> member, and the attribute on it, under managed code stripping. Not meant to be
    /// used directly, and it cannot be applied — its constructor is protected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unity's linker recognises a <c>Preserve</c> attribute by its simple name, or by a base class with that
    /// name, in any assembly and any namespace, so the package needs no reference to <c>UnityEngine</c>. It
    /// also keeps the instances of such attributes in the build, which is what lets the container still
    /// find <c>[Inject]</c> at run time.
    /// </para>
    /// <para>
    /// It lives in its own namespace so that a file importing both <c>OpenUGD</c> and
    /// <c>UnityEngine.Scripting</c> can still write <c>[Preserve]</c> without an ambiguity. To keep an
    /// element of your own, use <c>UnityEngine.Scripting.PreserveAttribute</c>.
    /// </para>
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public abstract class PreserveAttribute : Attribute
    {
        /// <summary>For <see cref="InjectAttribute" /> only.</summary>
        protected PreserveAttribute()
        {
        }
    }
}
