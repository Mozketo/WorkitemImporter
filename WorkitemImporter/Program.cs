using NDesk.Options;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Reflection;
using WorkitemImporter.Infrastructure;

namespace WorkitemImporter
{
    public static class Const
    {
        public const string JiraQueries = "Jira.Queries";
        public const string JiraMapPriority = "Jira.Map.Priority";
        public const string JiraMapStatus = "Jira.Map.Status";
        public const string JiraMapType = "Jira.Map.Type";
        public const string JiraMapUsers = "Jira.Map.Users";
    }

    class Program
    {
        static void Main(string[] args)
        {
            bool showHelp = false;
            bool previewMode = false;
            var vstsConfig = new VstsConfig();
            var jiraConfig = new JiraConfig();

            var p = new OptionSet() {
                { "vsts-url=", "VSTS URL", v => vstsConfig.Url = v},
                { "vsts-project=", "VSTS project name", v => vstsConfig.Project = v},
                { "vsts-pat=",  "VSTS Personal Access Token", v => vstsConfig.PersonalAccessToken = v },
                { "jira-url=", "Jira URL", v => jiraConfig.Url = v},
                { "jira-user=",  "Jira User ID", v => jiraConfig.UserId = v },
                { "jira-password=", "Jira password", v => jiraConfig.Password = v},
                { "jira-project=", "Jira project", v => jiraConfig.Project = v},
                { "p|preview=", "Preview mode", v => previewMode = v != null },
                { "h|help",  "show this message and exit", v => showHelp = v != null },
            };

            List<string> extra;
            try
            {
                extra = p.Parse(args);
            }
            catch (OptionException e)
            {
                Console.Write($"WorkitemImporter {Assembly.GetExecutingAssembly().GetName().Version}: ");
                Console.WriteLine(e.Message);
                Console.WriteLine("Try `myconsole --help' for more information.");
                return;
            }

            if (showHelp)
            {
                ShowHelp(p);
                return;
            }

            var queries = ConfigurationManager.AppSettings[Const.JiraQueries]
                .GetParts(Environment.NewLine).Trim();

            var sync = new Sync(vstsConfig, jiraConfig);
            sync.Process(queries, previewMode);
        }

        static void ShowHelp(OptionSet p)
        {
            Console.WriteLine("Usage: WorkitemImporter [OPTIONS]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            p.WriteOptionDescriptions(Console.Out);
        }
    }

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
