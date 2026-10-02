using System;
using System.Collections;
using System.Collections.Generic;

namespace OpenUGD
{
    /// <summary>
    /// The read side of configuration: a flat, case-insensitive map from <c>string</c> to <c>string</c>
    /// in which an absent key reads as <c>null</c> instead of throwing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Keys are paths.</b> Nesting is spelled out in the key itself, with <c>:</c> as the separator, so
    /// a JSON document, a settings object and a plain dictionary all flatten into the same shape:
    /// <c>Save:Slot</c>, <c>Servers:0:Url</c>. Sequence elements become their index. There is no tree here
    /// to walk — <see cref="ConfigurationExtensions.GetSection"/> only rewrites prefixes.
    /// </para>
    /// <para>
    /// <b>Absent and null are the same thing.</b> The indexer answers <c>null</c> for a key nobody wrote,
    /// for a key whose provider stored a JSON <c>null</c>, and for a key an override masked with
    /// <c>null</c>; enumeration skips all three. One rule and no third state, which is why
    /// <see cref="ConfigurationExtensions.TryGet"/> can report presence with a bare null check.
    /// </para>
    /// <para>
    /// <b>Values are raw text.</b> Nothing is parsed, trimmed or normalised on the way in or out; typing
    /// happens at the far end, in <see cref="ConfigurationExtensions.Bind"/>.
    /// </para>
    /// <para>
    /// <b>Where it comes from.</b> Every <see cref="Context"/> can resolve an <see cref="IConfiguration"/>
    /// even when none was registered, so any service may take one as a constructor dependency. During
    /// registration the same map is <see cref="ContextBuilder.Configuration"/> — readable, and writable,
    /// before <see cref="ContextBuilder.BuildAsync"/>, so a factory can branch on a value while the graph
    /// is still being described. A child builder starts with a copy of its parent's values.
    /// </para>
    /// </remarks>
    public interface IConfiguration : IEnumerable<KeyValuePair<string, string>>
    {
        /// <summary>
        /// The value configured at <paramref name="key"/>, or <c>null</c> if nothing is. Absence is not an
        /// error: choosing a default is the caller's job, not the map's.
        /// </summary>
        /// <param name="key">
        /// The full path from the root of this view, <c>:</c>-separated — not a single segment.
        /// <see cref="ConfigurationManager"/> compares it case-insensitively and rejects <c>null</c>.
        /// </param>
        /// <value>The raw, unparsed value, or <c>null</c> when the key is absent or was stored null.</value>
        string this[string key] { get; }
    }

    /// <summary>
    /// The writable configuration of a <see cref="ContextBuilder"/>: provider values underneath, explicit
    /// overrides on top, the override winning regardless of which was written first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two layers, one map.</b> The providers —
    /// <see cref="ConfigurationManagerExtensions.AddJson"/>,
    /// <see cref="ConfigurationManagerExtensions.AddObject"/> and
    /// <see cref="ConfigurationManagerExtensions.AddDictionary"/> — write the lower layer; the indexer's
    /// setter writes the upper one. That is what lets a test pin <c>configuration["Save:Slot"] = "3"</c>
    /// and still load the shipped JSON afterwards, and it is how a child context overrides a value it
    /// inherited from its parent. Within a layer the last write to a key wins, so provider call order is
    /// provider precedence.
    /// </para>
    /// <para>
    /// <b>Masking.</b> Setting a key to <c>null</c> is how you remove one: the override is recorded, hides
    /// whatever a provider supplied, and reads back as absent. There is deliberately no <c>Remove</c> —
    /// removing an override would resurrect the provider value underneath it, which is never what the
    /// caller meant.
    /// </para>
    /// <para>
    /// <b>Keys</b> are compared with <see cref="StringComparer.OrdinalIgnoreCase"/>, so <c>save:slot</c>
    /// and <c>Save:Slot</c> are one key. The spelling first written is the one kept, which matters only
    /// when enumerating.
    /// </para>
    /// <para>
    /// <b>Thread safety.</b> None. Populate it on one thread before
    /// <see cref="ContextBuilder.BuildAsync"/>; concurrent writes are not safe against each other or
    /// against a read.
    /// </para>
    /// </remarks>
    public sealed class ConfigurationManager : IConfiguration
    {
        private readonly Dictionary<string, string> _providers =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, string> _overrides =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Reads the effective value at <paramref name="key"/> — override first, then provider — or writes
        /// an override that shadows whatever any provider supplies for that key, before or after this call.
        /// </summary>
        /// <remarks>
        /// The setter never touches the provider layer, so a value written here survives a later
        /// <c>Add*</c> of the same key. Writing <c>null</c> is legal and masks the provider value.
        /// </remarks>
        /// <param name="key">The full <c>:</c>-separated path. Compared case-insensitively.</param>
        /// <value>
        /// On read: the override if one was set — including a <c>null</c> one — otherwise the provider
        /// value, otherwise <c>null</c>. The string is exactly what was stored.
        /// </value>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="key"/> is <c>null</c>, on the getter and on the setter alike.
        /// </exception>
        public string this[string key]
        {
            get
            {
                if (key == null) throw new ArgumentNullException(nameof(key));
                string value;
                if (_overrides.TryGetValue(key, out value)) return value;
                return _providers.TryGetValue(key, out value) ? value : null;
            }
            set
            {
                if (key == null) throw new ArgumentNullException(nameof(key));
                _overrides[key] = value;
            }
        }

        /// <summary>
        /// Walks every key that currently has a value: provider entries no override shadows, then the
        /// overrides. Keys stored with a <c>null</c> value are skipped, so a key appears at most once and
        /// no pair ever carries a null value.
        /// </summary>
        /// <remarks>
        /// Lazy, and a live view rather than a snapshot — nothing is copied, and each pair is produced as
        /// you step. Do not write while a walk is in progress: the two layers are two separate
        /// <see cref="Dictionary{TKey,TValue}"/> passes, so a key added during the override pass throws
        /// <see cref="InvalidOperationException"/>, while one added during the provider pass does not and
        /// is quietly picked up by the pass that follows. Materialise the sequence first if you mean to
        /// write while reading. Order within each layer is the underlying dictionary's, and is not a
        /// guarantee.
        /// </remarks>
        /// <returns>An enumerator over the effective key/value pairs.</returns>
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            foreach (var pair in _providers)
            {
                if (pair.Value != null && !_overrides.ContainsKey(pair.Key)) yield return pair;
            }

            foreach (var pair in _overrides)
            {
                if (pair.Value != null) yield return pair;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        internal void SetProviderValue(string key, string value) => _providers[key] = value;
    }
}
