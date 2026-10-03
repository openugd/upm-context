using System;
using System.Collections.Generic;
using System.Text;

namespace OpenUGD
{
    internal static class Diagnostics
    {
        internal static string UnableToResolve(Type missing, Type activating, string requirement,
            string site, IEnumerable<Type> candidates)
        {
            var builder = new StringBuilder();
            builder.Append("Unable to resolve service for type '").Append(Display(missing)).Append('\'');
            if (activating != null)
            {
                builder.Append(" while attempting to activate '").Append(Display(activating)).Append('\'');
            }

            builder.Append('.');

            if (requirement != null) builder.Append("\n      required by ").Append(requirement).Append('.');
            if (site != null) builder.Append("\n      registered at ").Append(site);

            var suggestion = Suggest(missing, candidates);
            if (suggestion != null) builder.Append("\n      ").Append(suggestion);

            return builder.ToString();
        }

        internal static string UnableToResolveMember(Member member, Type owner, string site,
            IEnumerable<Type> candidates) =>
            UnableToResolve(member.Contract, owner,
                "the [Inject] " + member.Kind + " '" + Display(member.DeclaringType) + "." + member.Name + "'",
                site, candidates);

        internal static string Suggest(Type missing, IEnumerable<Type> candidates)
        {
            if (candidates == null) return null;

            var name = missing.Name;
            Type sameName = null;
            Type nearest = null;
            Type automatic = null;
            var nearestDistance = int.MaxValue;

            foreach (var candidate in candidates)
            {
                if (candidate == missing) continue;

                // Everything is an object, so "registered as object" is never the fix.
                if (missing != typeof(object) && missing.IsAssignableFrom(candidate))
                {
                    // Context and Lifetime are supplied by the context itself: there is no registration to
                    // add a contract to, so .As<> would be advice nobody can follow.
                    if (candidate == typeof(Context) || candidate == typeof(Lifetime))
                    {
                        if (automatic == null) automatic = candidate;
                        continue;
                    }

                    return "'" + Display(candidate) + "' is registered and does implement '" +
                           Display(missing) + "', but was not registered as it. Add .As<" + CSharpName(missing) +
                           ">() to its registration.";
                }

                if (candidate.Name == name)
                {
                    if (sameName == null) sameName = candidate;
                    continue;
                }

                var distance = Distance(name, candidate.Name);
                if (distance >= nearestDistance) continue;

                nearestDistance = distance;
                nearest = candidate;
            }

            if (automatic != null)
            {
                return "'" + Display(automatic) + "' implements '" + Display(missing) + "', and every context " +
                       "supplies one, but it cannot be registered as anything else. Take '" + Display(automatic) +
                       "' itself instead.";
            }

            if (sameName != null)
            {
                return "A different type with the same name is registered: '" + Display(sameName) +
                       "'. Check the namespace.";
            }

            var threshold = Math.Max(2, name.Length / 4);
            return nearest != null && nearestDistance <= threshold
                ? "Did you mean '" + Display(nearest) + "'?"
                : null;
        }

        internal static string Report(List<string> problems)
        {
            var builder = new StringBuilder();
            builder.Append("The Context could not be built. ")
                .Append(problems.Count)
                .Append(problems.Count == 1 ? " problem was" : " problems were")
                .Append(" found while validating the service graph, before anything was constructed:");

            for (var i = 0; i < problems.Count; i++)
            {
                builder.Append("\n  - ").Append(problems[i]);
            }

            return builder.ToString();
        }

        internal static string Display(Type type)
        {
            if (type == null) return "<null>";
            if (!type.IsGenericType) return type.FullName ?? type.Name;

            var definition = type.GetGenericTypeDefinition().FullName ?? type.Name;
            var tick = definition.IndexOf('`');
            if (tick >= 0) definition = definition.Substring(0, tick);

            var builder = new StringBuilder(definition).Append('<');
            var arguments = type.GetGenericArguments();
            for (var i = 0; i < arguments.Length; i++)
            {
                if (i != 0) builder.Append(", ");
                builder.Append(Display(arguments[i]));
            }

            return builder.Append('>').ToString();
        }

        /// How C# source names <paramref name="type"/> where its namespace is imported: <c>IRepo&lt;Item&gt;</c>,
        /// never the metadata name <c>IRepo`1</c>, so a suggested <c>.As&lt;...&gt;()</c> can be pasted as is.
        internal static string CSharpName(Type type)
        {
            if (!type.IsGenericType) return type.Name;

            var name = type.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);

            var arguments = type.GetGenericArguments();
            var names = new string[arguments.Length];
            for (var i = 0; i < arguments.Length; i++) names[i] = CSharpName(arguments[i]);
            return name + "<" + string.Join(", ", names) + ">";
        }

        internal static string Signature(System.Reflection.ParameterInfo[] parameters)
        {
            var names = new string[parameters.Length];
            for (var i = 0; i < parameters.Length; i++) names[i] = Display(parameters[i].ParameterType);
            return string.Join(", ", names);
        }

        internal static string Path(IReadOnlyList<Type> path)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < path.Count; i++)
            {
                if (i != 0) builder.Append(" -> ");
                builder.Append(Display(path[i]));
            }

            return builder.ToString();
        }

        internal static string Where(string site) => site == null ? string.Empty : "\n      registered at " + site;

        /// What an exception from user code was, on one line of a container message: its type and message.
        /// The exception itself stays the InnerException.
        internal static string Describe(Exception exception) =>
            exception.GetType().Name + ": " + exception.Message;

        /// Appended to "has no public instance constructor": in a player build that is as likely to mean the
        /// linker removed the constructor as that the type never had one, and the two need different fixes.
        internal static string StrippingHint(int constructorsFound) =>
            (constructorsFound == 0
                ? "\n      It has no instance constructor at all, which every class compiled from C# has, so " +
                  "managed code stripping has removed them from this build."
                : "\n      If its source declares a public constructor, managed code stripping has removed it " +
                  "from this build.") +
            " Unity's linker keeps the constructors of a type named at the call that registers or " +
            "instantiates it - Add<T>(), TryAdd<TContract, T>(), Instantiate<T>(), Add(typeof(T)) - but not " +
            "of a type passed on by a generic method of your own, or carried in a Type it cannot trace. Put " +
            "[Inject] on the constructor the container should call; the com.openugd.context README has the " +
            "details under \"Managed code stripping\".";

        private static int Distance(string a, string b)
        {
            if (a == b) return 0;
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++) previous[j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    var best = current[j - 1] + 1;
                    if (previous[j] + 1 < best) best = previous[j] + 1;
                    if (previous[j - 1] + cost < best) best = previous[j - 1] + cost;
                    current[j] = best;
                }

                var swap = previous;
                previous = current;
                current = swap;
            }

            return previous[b.Length];
        }
    }
}
