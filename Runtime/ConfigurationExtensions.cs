using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace OpenUGD
{
    /// <summary>
    /// Reading and typed binding over <see cref="IConfiguration"/>: presence tests, prefix views, and the
    /// projection of a flat key/value map onto a plain settings object.
    /// </summary>
    /// <remarks>
    /// Everything here is written against the <see cref="IConfiguration"/> indexer and enumerator alone,
    /// so it applies equally to a <see cref="ConfigurationManager"/> still being populated during
    /// registration, to a section view of one, and to any other implementation.
    /// </remarks>
    public static class ConfigurationExtensions
    {
        /// <summary>
        /// Reads a key and reports whether it was configured, for the "use this only if it is there"
        /// branch that a bare indexer read otherwise turns into a null check at every call site.
        /// </summary>
        /// <param name="configuration">The map to read.</param>
        /// <param name="key">The full <c>:</c>-separated path.</param>
        /// <param name="value">
        /// The raw value on success, <c>null</c> on failure — so it is safe to ignore the return value and
        /// test this instead.
        /// </param>
        /// <returns>
        /// <c>true</c> if a non-null value is configured. A key stored with a <c>null</c> value is
        /// indistinguishable from a missing one and yields <c>false</c>.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="configuration"/> or <paramref name="key"/> is <c>null</c>.
        /// </exception>
        public static bool TryGet(this IConfiguration configuration, string key, out string value)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (key == null) throw new ArgumentNullException(nameof(key));

            value = configuration[key];
            return value != null;
        }

        /// <summary>
        /// Narrows the map to the keys under <paramref name="prefix"/>, with that prefix and its <c>:</c>
        /// stripped from every key, so a component can be written as though it owned the root.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The result is a live view, not a copy: it holds the source and re-reads it, so anything written
        /// afterwards — including an override on a <see cref="ConfigurationManager"/> — shows through, and
        /// taking a section allocates one small wrapper rather than a filtered dictionary. Sections
        /// compose: <c>GetSection("A").GetSection("B")</c> sees the same keys as <c>GetSection("A:B")</c>.
        /// </para>
        /// <para>
        /// A view is read-only, because it is an <see cref="IConfiguration"/>; there is no section-scoped
        /// write. Enumeration matches the prefix case-insensitively, while the indexer inherits whatever
        /// comparison the source uses.
        /// </para>
        /// </remarks>
        /// <param name="configuration">The map to narrow.</param>
        /// <param name="prefix">
        /// The section path, without a trailing <c>:</c>. <c>null</c> or empty means "no narrowing", and
        /// the source is returned unwrapped — so the caller can get back the very instance it passed in.
        /// </param>
        /// <returns>
        /// A view over the matching keys, or <paramref name="configuration"/> itself for an empty prefix.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="configuration"/> is <c>null</c>.
        /// </exception>
        public static IConfiguration GetSection(this IConfiguration configuration, string prefix)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            return string.IsNullOrEmpty(prefix) ? configuration : new Section(configuration, prefix + ":");
        }

        /// <summary>
        /// Creates a <typeparamref name="T"/> and fills it from the configuration — the one-line form of
        /// <see cref="Bind"/> for a settings type you own.
        /// </summary>
        /// <remarks>
        /// Always returns an instance, never <c>null</c>: a section with no keys at all yields a
        /// default-constructed object, so field initialisers stand as the defaults. Binding is per member,
        /// so a partially configured section leaves the remaining members at their initial values.
        /// </remarks>
        /// <typeparam name="T">
        /// A settings type: a class with a public parameterless constructor and public read/write fields
        /// or properties. Plain data — nothing is injected into it, and it is not registered anywhere.
        /// </typeparam>
        /// <param name="configuration">The map to read.</param>
        /// <param name="section">
        /// The path to bind from, without a trailing <c>:</c>; <c>null</c> or empty binds from the root.
        /// </param>
        /// <returns>The newly created, newly bound instance. A fresh object on every call.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="configuration"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="ContextException">
        /// <typeparamref name="T"/> or a nested settings type has no public parameterless constructor, or a
        /// configured value cannot be read as its member's type. The message names the key and the type.
        /// </exception>
        public static T Get<T>(this IConfiguration configuration, string section = null)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            var target = Create(typeof(T));
            configuration.Bind(section, target);
            return (T)target;
        }

        /// <summary>
        /// Fills the public read/write fields and properties of an existing object from the keys under
        /// <paramref name="section"/>, recursing into nested settings objects.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What is bound.</b> Public instance fields that are neither <c>readonly</c> nor <c>const</c>,
        /// and public instance properties that have a public getter, a public setter, and no index
        /// parameters. Everything else — private, static, get-only, indexed — is invisible here. A
        /// member's key is <c>section:MemberName</c>, matched however the source compares keys
        /// (case-insensitively, for <see cref="ConfigurationManager"/>).
        /// </para>
        /// <para>
        /// <b>A missing key changes nothing.</b> The member keeps whatever it already held, so a field
        /// initialiser is the default and a partial override file is a legitimate one. It also means
        /// <see cref="Bind"/> can be called repeatedly with different sections to layer values onto one
        /// object.
        /// </para>
        /// <para>
        /// <b>Nested objects.</b> A member with no key of its own is still bound when it is a non-abstract
        /// class other than <c>string</c>, is not <see cref="IEnumerable"/>, and at least one key starts
        /// with <c>section:MemberName:</c>. A fresh instance is then constructed and bound, replacing
        /// whatever the member held. With no such key the member is left alone rather than overwritten
        /// with an empty object.
        /// </para>
        /// <para>
        /// <b>Collections get no nested walk.</b> Anything <see cref="IEnumerable"/> is barred from the
        /// rule above, so the <c>Servers:0</c>, <c>Servers:1</c> keys that
        /// <see cref="ConfigurationManagerExtensions.AddJson"/> produces from a JSON array have no member
        /// to land on. A key on the member itself — plain <c>Servers</c> — is not skipped, though: it goes
        /// to the conversion below, which fails for a list. Read the elements by index through the
        /// indexer, or carry the list as one delimited string and split it yourself.
        /// </para>
        /// <para>
        /// <b>Conversion.</b> <c>string</c> is taken verbatim; an enum parses by name, case-insensitively,
        /// or by its underlying number; a <see cref="Guid"/> parses by shape, no culture involved;
        /// <see cref="TimeSpan"/>, <see cref="DateTime"/> and <see cref="DateTimeOffset"/> parse under
        /// <see cref="CultureInfo.InvariantCulture"/>, the two date types with
        /// <see cref="DateTimeStyles.RoundtripKind"/>; a <see cref="Uri"/> is built as
        /// <see cref="UriKind.RelativeOrAbsolute"/>; anything else goes through
        /// <see cref="System.Convert.ChangeType(object, Type, IFormatProvider)"/>, again invariant. Culture
        /// is pinned on purpose: a configuration file must not read differently on a device set to a
        /// comma-decimal locale.
        /// </para>
        /// <para>
        /// <b>Nullable members.</b> A <see cref="Nullable{T}"/> is converted as its underlying type.
        /// Nullability describes the member's own domain, not absence — absence is already spelled "no
        /// key", and an empty string is not a null, it is a value that fails to parse.
        /// </para>
        /// <para>
        /// <b>Failure is loud, and not transactional.</b> An unparseable value throws, quoting the value,
        /// the key and the target type, with the underlying parse exception as the inner one; members
        /// bound before it keep their new values.
        /// </para>
        /// </remarks>
        /// <param name="configuration">The map to read.</param>
        /// <param name="section">
        /// The path to bind from, without a trailing <c>:</c>; <c>null</c> or empty binds from the root.
        /// </param>
        /// <param name="target">The object to fill, mutated in place.</param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="configuration"/> or <paramref name="target"/> is <c>null</c>.
        /// </exception>
        /// <exception cref="ContextException">
        /// A configured value cannot be read as its member's type, or a nested settings type has no public
        /// parameterless constructor.
        /// </exception>
        public static void Bind(this IConfiguration configuration, string section, object target)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (target == null) throw new ArgumentNullException(nameof(target));

            var prefix = string.IsNullOrEmpty(section) ? string.Empty : section + ":";
            var members = Activation.GetBindableMembers(target.GetType());

            for (var i = 0; i < members.Length; i++)
            {
                var member = members[i];
                var key = prefix + member.Name;

                var raw = configuration[key];
                if (raw != null)
                {
                    member.SetValue(target, Convert(raw, member.Contract, key));
                    continue;
                }

                // A member with no key of its own may still be a nested section.
                if (!IsComposite(member.Contract) || !HasChildren(configuration, key)) continue;

                var child = Create(member.Contract);
                configuration.Bind(key, child);
                member.SetValue(target, child);
            }
        }

        private static bool IsComposite(Type type) =>
            type.IsClass && type != typeof(string) && !type.IsAbstract && !typeof(IEnumerable).IsAssignableFrom(type);

        private static bool HasChildren(IConfiguration configuration, string key)
        {
            var head = key + ":";
            foreach (var pair in configuration)
            {
                if (pair.Key.StartsWith(head, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        internal static object Create(Type type)
        {
            var constructor = type.GetConstructor(Type.EmptyTypes);
            if (constructor == null)
            {
                throw new ContextException(
                    "'" + Diagnostics.Display(type) + "' cannot be bound from configuration: it has no " +
                    "public parameterless constructor. Settings types are plain data.");
            }

            return constructor.Invoke(null);
        }

        private static object Convert(string raw, Type target, string key)
        {
            var underlying = Nullable.GetUnderlyingType(target) ?? target;

            try
            {
                if (underlying == typeof(string)) return raw;
                if (underlying.IsEnum) return Enum.Parse(underlying, raw, true);
                if (underlying == typeof(Guid)) return Guid.Parse(raw);
                if (underlying == typeof(TimeSpan)) return TimeSpan.Parse(raw, CultureInfo.InvariantCulture);
                if (underlying == typeof(DateTime))
                    return DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                if (underlying == typeof(DateTimeOffset))
                    return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                if (underlying == typeof(Uri)) return new Uri(raw, UriKind.RelativeOrAbsolute);

                return System.Convert.ChangeType(raw, underlying, CultureInfo.InvariantCulture);
            }
            catch (Exception exception)
            {
                throw new ContextException(
                    "The configuration value '" + raw + "' at key '" + key + "' cannot be read as '" +
                    Diagnostics.Display(target) + "'.", exception);
            }
        }

        private sealed class Section : IConfiguration
        {
            private readonly IConfiguration _source;
            private readonly string _prefix;

            internal Section(IConfiguration source, string prefix)
            {
                _source = source;
                _prefix = prefix;
            }

            public string this[string key] => _source[_prefix + key];

            public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
            {
                foreach (var pair in _source)
                {
                    if (!pair.Key.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)) continue;

                    yield return new KeyValuePair<string, string>(
                        pair.Key.Substring(_prefix.Length), pair.Value);
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
