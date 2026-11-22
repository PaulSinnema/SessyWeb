namespace SessyCommon.Extensions
{
    public static class EnumerableExtensions
    {
        /// <summary>First item per key, as a dictionary; later duplicates are ignored.</summary>
        public static Dictionary<TKey, T> FirstPer<T, TKey>(this IEnumerable<T> items, Func<T, TKey> key) where TKey : notnull
            => items.DistinctBy(key).ToDictionary(key);

        /// <summary>A value from the first item per key, as a dictionary; later duplicates are ignored.</summary>
        public static Dictionary<TKey, TValue> FirstPer<T, TKey, TValue>(this IEnumerable<T> items, Func<T, TKey> key, Func<T, TValue> value) where TKey : notnull
            => items.DistinctBy(key).ToDictionary(key, value);
    }
}
