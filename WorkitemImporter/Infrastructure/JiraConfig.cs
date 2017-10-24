using System;

namespace WorkitemImporter.Infrastructure
{
    public sealed class JiraConfig
    {
        public string Project { get; set; }
        public string UserId { get; set; }
        public string Password { get; set; }
        string url;
        public string Url
        {
            get { return url; }
            set
            {
                url = value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? value
                    : $"https://{value}.atlassian.net";
            }
        }
    }
}
