using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace OpenUGD
{
    /// The one shape AsElementOf contributions resolve as: IReadOnlyList&lt;T&gt; of a reference type T, built
    /// as a T[] in registration order.
    internal static class CollectionContract
    {
        private static readonly ConcurrentDictionary<Type, Array> Empty = new ConcurrentDictionary<Type, Array>();

        private static readonly Func<Type, Array> NewEmpty = contract => {
            var element = ElementOf(contract);
            return element == null ? null : Array.CreateInstance(element, 0);
        };

        /// T, when <paramref name="contract"/> is IReadOnlyList&lt;T&gt; of a reference type; otherwise null.
        internal static Type ElementOf(Type contract)
        {
            if (!contract.IsGenericType || contract.GetGenericTypeDefinition() != typeof(IReadOnlyList<>)) return null;

            var element = contract.GetGenericArguments()[0];
            return element.IsValueType || element.ContainsGenericParameters ? null : element;
        }

        internal static Type Of(Type element) => typeof(IReadOnlyList<>).MakeGenericType(element);

        /// What a context hands out for a collection contract nothing contributes to - one shared, empty T[],
        /// made once per contract - or null when <paramref name="contract"/> is not a collection contract.
        internal static Array EmptyFor(Type contract) => contract.IsGenericType ? Empty.GetOrAdd(contract, NewEmpty) : null;
    }
}
