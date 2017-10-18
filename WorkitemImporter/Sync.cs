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
            IEnumerable<WorkItemClassificationNode> GetIterations()
            {
                var iterations = witClient.GetClassificationNodeAsync(VstsConfig.Project, TreeStructureGroup.Iterations, null, 10).Result;
                return iterations.Children;
            }

            foreach (var sprint in sprints)
            {
                if (!GetIterations().Any(i => i.Name.Equals(sprint, StringComparison.OrdinalIgnoreCase)))
                {
                    // Add any missing VSTS iterations to map the Jira tickets to 
                    var workItemClassificationNode = witClient.CreateOrUpdateClassificationNodeAsync(new WorkItemClassificationNode() { Name = sprint, }, VstsConfig.Project, TreeStructureGroup.Iterations).Result;
                }
            }

            void AddProp(JsonPatchDocument doc, string path, object value)
            {
                if (value is null) return;
                if (value is string && string.IsNullOrEmpty(value.ToString())) return;
                doc.Add(new JsonPatchOperation { Operation = Operation.Add, Path = path, Value = value.ToString() });
            }

            foreach (var issue in issues)
            {
                // Create the JSON necessary to create/update the WorkItem in VSTS
                //string title = $"{issue.Key} {issue.Summary}";
                //title = title.Substring(0, Math.Min(title.Length, 128));

                var doc = new JsonPatchDocument();

                AddProp(doc, "/fields/System.Title", issue.Summary);
                AddProp(doc, "/fields/System.Description", issue.Description);

                var jiraUser = jiraConn.Users.SearchUsersAsync(issue.Reporter).Result.FirstOrDefault();
                if (jiraUser != null)
                {
                    AddProp(doc, "/fields/System.CreatedBy", jiraUser.Email);
                }

                string issueSprint = issue.CustomFields["Sprint"].Values.FirstOrDefault();
                if (!string.IsNullOrEmpty(issueSprint))
                {
                    var iteration = GetIterations().FirstOrDefault(i => i.Name.Equals(issueSprint, StringComparison.OrdinalIgnoreCase));
                    AddProp(doc, "/Fields/System.IterationPath", iteration?.Name);
                }

                AddProp(doc, "/Fields/System.CreatedDate", issue.Created);
                AddProp(doc, "/Fields/System.ChangedDate", issue.Updated);
                AddProp(doc, "/Fields/System.BoardColumn", "");
                AddProp(doc, "/Fields/System.BoardColumnDone", issue.Resolution);
                AddProp(doc, "/Fields/System.State", issue.Status);
                AddProp(doc, "/Fields/Microsoft.VSTS.Common.Priority", issue.Priority);

                //    // Create/Update the workitem in VSTS
                var workItem = witClient.CreateWorkItemAsync(doc, VstsConfig.Project, "User Story").Result;
                //    workItem = witClient.UpdateWorkItemAsync(document, id).Result;
            }
        }
    }
}
