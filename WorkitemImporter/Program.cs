using System;
using System.Configuration;
using System.Linq;
using WorkitemImporter.Infrastructure;

namespace WorkitemImporter
{
    static class Program
    {
        static void Main(string[] args)
        {
            // Initialise the application settings and configuration
            var config = Infrastructure.Configuration.Instance.Initialise(args);
            if (config == null)
            {
                Environment.ExitCode = -1;
                return;
            }

            var queries = ConfigurationManager.AppSettings[Const.JiraQueries]
                .GetParts(Environment.NewLine)
                .Trim().RemoveComments();

            if (!queries.EmptyIfNull().Any())
            {
                Console.WriteLine($"Add a Jira query in appSettings to sync. Example: project = 'projectname' and status not in (done) and type = epic and sprint is empty");
                return;
            }

            new Sync().Process(queries, config.Mode);

            Console.WriteLine("Sync complete");
        }
    }
}
