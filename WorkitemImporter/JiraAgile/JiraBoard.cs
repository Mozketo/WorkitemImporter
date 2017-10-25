namespace WorkitemImporter.JiraAgile
{
    public sealed class JiraBoard
    {
        public int Id { get; set; }
        public string Self { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
    }
}
