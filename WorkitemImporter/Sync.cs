using Atlassian.Jira;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
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
            var connection = new VssConnection(new Uri(VstsConfig.Url), new VssBasicCredential(string.Empty, VstsConfig.PersonalAccessToken));
            var witClient = connection.GetClient<WorkItemTrackingHttpClient>();

            var jql = "project = 'LT Excelens' and status not in (done)";

            var jiraConn = Jira.CreateRestClient(JiraConfig.Url, JiraConfig.UserId, JiraConfig.Password);

            // Perhaps a sync Open sprints + backlog only?

            int take = 20;
            var issues = jiraConn.Issues.GetIssuesFromJqlAsync(jql, startAt: 0, maxIssues: take).Result;
            var sprints = issues.Select(i => i.CustomFields["Sprint"].Values.FirstOrDefault()).Where(i => i != null).Distinct().ToList();

            // Get the existing iterations defined in VSTS
            var iterations = witClient.GetClassificationNodeAsync(VstsConfig.Project, TreeStructureGroup.Iterations, null, 10).Result;

            foreach (var sprint in sprints)
            {
                if (!iterations.Children.Any(i => i.Name.Equals(sprint, StringComparison.OrdinalIgnoreCase)))
                {
                    // Add any missing VSTS iterations to map the Jira tickets to 
                    var workItemClassificationNode = witClient.CreateOrUpdateClassificationNodeAsync(new WorkItemClassificationNode() { Name = sprint, }, VstsConfig.Project, TreeStructureGroup.Iterations).Result;
                }
            }

            foreach (var issue in issues)
            {
                // Create the JSON necessary to create/update the WorkItem in VSTS
                string title = $"{issue.Key} {issue.Summary}";
                title = title.Substring(0, Math.Min(title.Length, 128));

                var document = new JsonPatchDocument();
                document.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Title", Value = title });
                if (!string.IsNullOrEmpty(issue.Description))
                {
                    document.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Description", Value = issue.Description });
                }

                var jiraUser = jiraConn.Users.SearchUsersAsync(issue.Reporter).Result.FirstOrDefault();
                if (jiraUser != null)
                {
                    document.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.CreatedBy", Value = jiraUser.Email });
                }

                //    // Create/Update the workitem in VSTS
                //    workItem = witClient.CreateWorkItemAsync(document, project, workItemType).Result;
                //    workItem = witClient.UpdateWorkItemAsync(document, id).Result;
            }
        }
    }
}
