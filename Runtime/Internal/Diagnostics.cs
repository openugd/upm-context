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
            var nearestDistance = int.MaxValue;

            foreach (var candidate in candidates)
            {
                if (candidate == missing) continue;

                if (missing.IsAssignableFrom(candidate))
                {
                    return "'" + Display(candidate) + "' is registered and does implement '" +
                           Display(missing) + "', but was not registered as it. Add .As<" + name +
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
