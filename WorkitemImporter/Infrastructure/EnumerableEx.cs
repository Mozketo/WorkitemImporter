using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WorkitemImporter.Infrastructure
{
    public static class EnumerableEx
    {
        public static IEnumerable<string> Trim(this IEnumerable<string> items) => items.Select(i => i.Trim()).RemoveEmpty();

        public static IEnumerable<string> RemoveEmpty(this IEnumerable<string> items) => items.Where(i => !i.IsNullOrEmpty());
    }
}
