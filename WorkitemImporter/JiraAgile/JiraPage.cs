using System.Collections.Generic;

namespace WorkitemImporter.JiraAgile
{
    public sealed class JiraPage<T>
    {
        public int MaxResults { get; set; }
        public int StartAt { get; set; }
        public bool IsLast { get; set; }
        public List<T> Values { get; set; }
    }
}
