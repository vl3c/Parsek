using System.Collections.Generic;

namespace Parsek
{
    /// <summary>
    /// The one implementation of "a committed-list index moved" for every int-keyed
    /// collection that mirrors the store's committed recording list by position (engine
    /// ghost slots, held ghosts, map-presence dicts). A delete drops the removed key and
    /// shifts everything above it down; an insert shifts everything at or above the
    /// inserted index up and leaves that slot empty.
    /// </summary>
    internal static class IndexShift
    {
        internal static int AfterDelete(int index, int removedIndex)
        {
            return index > removedIndex ? index - 1 : index;
        }

        internal static int AfterInsert(int index, int insertedIndex)
        {
            return index >= insertedIndex ? index + 1 : index;
        }

        internal static void DictAfterDelete<T>(Dictionary<int, T> dict, int removedIndex)
        {
            if (dict == null || dict.Count == 0) return;
            var keys = new List<int>(dict.Keys);
            keys.Sort();
            foreach (int key in keys)
            {
                if (key < removedIndex) continue;
                var value = dict[key];
                dict.Remove(key);
                if (key > removedIndex)
                    dict[key - 1] = value;
            }
        }

        internal static void DictAfterInsert<T>(Dictionary<int, T> dict, int insertedIndex)
        {
            if (dict == null || dict.Count == 0) return;
            var keys = new List<int>(dict.Keys);
            // Descending so key k moves into k+1 only after k+1 has itself moved on.
            keys.Sort((a, b) => b.CompareTo(a));
            foreach (int key in keys)
            {
                if (key < insertedIndex) continue;
                var value = dict[key];
                dict.Remove(key);
                dict[key + 1] = value;
            }
        }

        internal static void SetAfterDelete(HashSet<int> set, int removedIndex)
        {
            if (set == null || set.Count == 0) return;
            var items = new List<int>(set);
            set.Clear();
            foreach (int item in items)
            {
                if (item > removedIndex) set.Add(item - 1);
                else if (item < removedIndex) set.Add(item);
            }
        }

        internal static void SetAfterInsert(HashSet<int> set, int insertedIndex)
        {
            if (set == null || set.Count == 0) return;
            var items = new List<int>(set);
            set.Clear();
            foreach (int item in items)
                set.Add(AfterInsert(item, insertedIndex));
        }
    }
}
