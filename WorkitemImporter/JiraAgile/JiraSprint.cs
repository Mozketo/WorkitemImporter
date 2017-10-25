using System;

namespace WorkitemImporter.JiraAgile
{
    public sealed class JiraSprint
    {
        public int Id { get; set; }
        public string Self { get; set; }
        public string State { get; set; }
        public string Name { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public int OriginBoardId { get; set; }
        public string Goal { get; set; }
    }
}
