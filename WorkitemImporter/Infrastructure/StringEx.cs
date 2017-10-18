using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WorkitemImporter.Infrastructure
{
    public static class StringEx
    {
        public static bool EqualsIgnoreCase(string original, string compare)
        {
            return original.Equals(compare, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Convenience method, same as calling String.IsNullOrEmpty(string)
        /// <para />
        /// Indicates whether a specified string is null or empty.
        /// </summary>
        /// <param name="value">string to check</param>
        /// <returns>true if the string is neither null or ""</returns>
        public static bool IsNullOrEmpty(this string value) => String.IsNullOrEmpty(value);

        /// <summary>
        /// Returns parts of the given value separated with the given separator. Empty items are excluded if excludeEmptyParts is true.
        /// </summary>
        /// <param name="value">the value for which the parts are calculated</param>
        /// <param name="separator">separator which separates the parts in the given value</param>
        /// <param name="excludeEmptyParts">true to exclude empty parts</param>
        /// <returns>seq of parts within the given value</returns>
        public static IEnumerable<string> GetParts(this string value, string separator, bool excludeEmptyParts)
        {
            if (value.IsNullOrEmpty() || separator.IsNullOrEmpty())
                return Enumerable.Empty<string>();

            var options = excludeEmptyParts ? StringSplitOptions.RemoveEmptyEntries : StringSplitOptions.None;
            var res = value.Split(new[] { separator }, options);
            return res;
        }

        /// <summary>
        /// Returns parts of the given value separated with the given separator. Empty items are excluded.
        /// </summary>
        /// <param name="value">the value for which the parts are calculated</param>
        /// <param name="separator">separator which separates the parts in the given value</param>
        /// <returns>seq of parts within the given value</returns>
        public static IEnumerable<string> GetParts(this string value, string separator) => value.GetParts(separator, excludeEmptyParts: true);
    }
}
