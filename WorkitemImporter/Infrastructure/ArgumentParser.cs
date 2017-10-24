using NDesk.Options;
using System;
using System.Reflection;

namespace WorkitemImporter.Infrastructure
{
    public enum ProcessingMode
    {
        ReadWrite = 0,
        ReadOnly
    }

    public sealed class Configuration
    {
        private static readonly Configuration instance = new Configuration();

        // Explicit static constructor to tell C# compiler
        // not to mark type as beforefieldinit
        static Configuration() { }

        private Configuration() { }

        public static Configuration Instance { get { return instance; } }

        public ProcessingMode Mode { get; private set; }
        public VstsConfig Vsts { get; } = new VstsConfig();
        public JiraConfig Jira { get; } = new JiraConfig();

        public Configuration Initialise(string[] args)
        {
            bool showHelp = false;

            var p = new OptionSet {
                { "vsts-url=", "VSTS URL", v => Vsts.Url = v},
                { "vsts-project=", "VSTS project name", v => Vsts.Project = v},
                { "vsts-pat=",  "VSTS Personal Access Token", v => Vsts.PersonalAccessToken = v },
                { "jira-url=", "Jira URL", v => Jira.Url = v},
                { "jira-user=",  "Jira User ID", v => Jira.UserId = v },
                { "jira-password=", "Jira password", v => Jira.Password = v},
                { "jira-project=", "Jira project", v => Jira.Project = v},
                { "r|readonly=", "Read-only mode", v => Mode = v.AsBoolean() ? ProcessingMode.ReadOnly : ProcessingMode.ReadWrite },
                { "h|help",  "show this message and exit", v => showHelp = v != null },
            };

            try
            {
                p.Parse(args);
            }
            catch (OptionException e)
            {
                Console.Write($"WorkitemImporter {Assembly.GetExecutingAssembly().GetName().Version}: ");
                Console.WriteLine(e.Message);
                Console.WriteLine("Try `myconsole --help' for more information.");
                return null;
            }

            var check = new Action<Func<bool>, string>((Func<bool> f, string message) =>
            {
                if (!f()) return;
                if (!showHelp)
                {
                    Console.WriteLine("One or more arguments are missing:");
                    showHelp = true;
                }
                Console.WriteLine($" {message}");
            });

            check(() => Vsts.Url.IsNullOrEmpty(), $"VSTS {nameof(Vsts.Url)}");
            check(() => Vsts.Project.IsNullOrEmpty(), $"VSTS {nameof(Vsts.Project)}");
            check(() => Vsts.PersonalAccessToken.IsNullOrEmpty(), $"VSTS {nameof(Vsts.PersonalAccessToken)}");
            check(() => Jira.Url.IsNullOrEmpty(), $"Jira {nameof(Jira.Url)}");
            check(() => Jira.UserId.IsNullOrEmpty(), $"Jira {nameof(Jira.UserId)}");
            check(() => Jira.Password.IsNullOrEmpty(), $"Jira {nameof(Jira.Password)}");
            check(() => Jira.Project.IsNullOrEmpty(), $"Jira {nameof(Jira.Project)}");

            if (showHelp)
            {
                ShowHelp(p);
                return null;
            }

            return this;
        }

        public void ShowHelp(OptionSet p)
        {
            Console.WriteLine("Usage: WorkitemImporter [OPTIONS]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            p.WriteOptionDescriptions(Console.Out);
        }
    }
}
