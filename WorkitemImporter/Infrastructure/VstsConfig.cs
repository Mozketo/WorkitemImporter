using System;

namespace WorkitemImporter.Infrastructure
{
    public sealed class VstsConfig
    {
        string url;
        public string Url
        {
            get { return url; }
            set
            {
                url = value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? value
                    : $"https://{value}.visualstudio.com";
            }
        }

        public string PersonalAccessToken { get; set; }
        public string Project { get; set; }
    }
}
