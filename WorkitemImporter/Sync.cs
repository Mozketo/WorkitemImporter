using Atlassian.Jira;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WorkitemImporter
{
    sealed class Sync
    {
        public VstsConfig VstsConfig { get; }
        public JiraConfig JiraConfig { get; }

        public Sync(VstsConfig vstsConfig, JiraConfig jiraConfig)
        {
            JiraConfig = jiraConfig;
            VstsConfig = vstsConfig;
        }

        public void Process()
        {
            //string vstsUrl = $"https://{VstsConfig.Url}.visualstudio.com";
            var connection = new VssConnection(new Uri(VstsConfig.Url), new VssBasicCredential(string.Empty, VstsConfig.PersonalAccessToken));
            var witClient = connection.GetClient<WorkItemTrackingHttpClient>();

            var jiraConn = Jira.CreateRestClient(JiraConfig.Url, JiraConfig.UserId, JiraConfig.Password);
            var issues = (from i in jiraConn.Issues.Queryable orderby i.Created select i).ToList();

            foreach (var issue in issues)
            {
                var all = witClient.GetClassificationNodeAsync(VstsConfig.Project, TreeStructureGroup.Iterations, null, 10).Result;
            }
        }
    }
}
