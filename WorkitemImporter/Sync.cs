using Atlassian.Jira;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using RestSharp.Extensions.MonoHttp;
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
            const int take = 25;
            var jiraConn = Jira.CreateRestClient(JiraConfig.Url, JiraConfig.UserId, JiraConfig.Password);
            var jql = "project = 'LT Excelens' and status not in (done)";

            {
                // Before uploading issues to VSTS ensure all Epics are in place for wiring up
                var epics = jiraConn.Issues.GetIssuesFromJqlAsync($"{jql} and type = epic and sprint is empty", startAt: 0, maxIssues: take).Result;
                SyncToVsts(epics);
            }

            {
                var issues = jiraConn.Issues.GetIssuesFromJqlAsync(jql, startAt: 0, maxIssues: take).Result;
                SyncToVsts(issues);
            }
        }

        void SyncToVsts(IEnumerable<Issue> issues)
        {
            var connection = new VssConnection(new Uri(VstsConfig.Url), new VssBasicCredential(string.Empty, VstsConfig.PersonalAccessToken));
            var witClient = connection.GetClient<WorkItemTrackingHttpClient>();
            //var x = witClient.GetWorkItemAsync(14).Result;

            // Get the existing iterations defined in VSTS
            IEnumerable<WorkItemClassificationNode> GetIterations()
            {
                var iterations = witClient.GetClassificationNodeAsync(VstsConfig.Project, TreeStructureGroup.Iterations, null, 10).Result;
                return iterations.Children;
            }

            var sprints = issues.Select(i => i.CustomFields["Sprint"].Values.FirstOrDefault()).Where(i => i != null).Distinct().ToList();
            foreach (var sprint in sprints)
            {
                if (!GetIterations().Any(i => i.Name.Equals(sprint, StringComparison.OrdinalIgnoreCase)))
                {
                    // Add any missing VSTS iterations to map the Jira tickets to 
                    var workItemClassificationNode = witClient.CreateOrUpdateClassificationNodeAsync(new WorkItemClassificationNode() { Name = sprint, }, VstsConfig.Project, TreeStructureGroup.Iterations).Result;
                }
            }

            // Find a WorkItem by title and return, if none found then null
            (bool exists, int? id) WorkItemExists(string jiraKey)
            {
                var query = new Wiql { Query = $"Select [System.Id] from WorkItems Where [System.TeamProject] = '{VstsConfig.Project}' AND [System.Title] CONTAINS '[{jiraKey}]'" };
                var qResult = witClient.QueryByWiqlAsync(query).Result;
                return (qResult.WorkItems.AsEmptyIfNull().Any(), qResult.WorkItems.AsEmptyIfNull().FirstOrDefault()?.Id);
            }

            void AddField(JsonPatchDocument doc, string path, object value)
            {
                if (value is null) return;
                if (value is string && string.IsNullOrEmpty(value.ToString())) return;
                doc.Add(new JsonPatchOperation { Operation = Operation.Add, Path = $"/fields/{path}", Value = value });
            }

            foreach (var issue in issues)
            {
                var existingWorkItemId = WorkItemExists(issue.Key.ToString());

                var doc = new JsonPatchDocument();

                AddField(doc, "System.Title", $"[{issue.Key.ToString()}] {issue.Summary}");
                AddField(doc, "System.Description", issue.Description);

                //var jiraUser = jiraConn.Users.SearchUsersAsync(issue.Reporter).Result.FirstOrDefault();
                //if (jiraUser != null)
                //{
                //AddField(doc, "System.CreatedBy", jiraUser.Email);
                AddField(doc, "System.CreatedBy", issue.Reporter);
                //}

                string issueSprint = issue.CustomFields["Sprint"].Values.FirstOrDefault();
                if (!string.IsNullOrEmpty(issueSprint))
                {
                    var iteration = GetIterations().FirstOrDefault(i => i.Name.Equals(issueSprint, StringComparison.OrdinalIgnoreCase));
                    //AddProp(doc, "/Fields/System.IterationPath", iteration?.Name);
                    AddField(doc, "System.IterationID", iteration?.Id);
                }

                //AddProp(doc, "Microsoft.VSTS.Scheduling.StoryPoints", issue.CustomFields["Story Points"].Values.FirstOrDefault());
                AddField(doc, "System.State", issue.Status.ToVsts());
                AddField(doc, "Microsoft.VSTS.Common.Priority", issue.Priority.ToVsts());
                AddField(doc, "System.Tags", string.Join(";", issue.Labels));

                if (!existingWorkItemId.exists)
                {
                    AddField(doc, "System.CreatedDate", issue.Created);
                    AddField(doc, "System.ChangedDate", issue.Updated);
                    AddField(doc, "System.History", $"Import from Jira {DateTime.Now} (NZ). Original Jira ID: {issue.Key}");
                }

                // Create/Update the workitem in VSTS
                var issueType = issue.Type.ToVsts();
                var workItem = existingWorkItemId.exists
                    ? witClient.UpdateWorkItemAsync(doc, existingWorkItemId.id.Value).Result
                    : witClient.CreateWorkItemAsync(doc, VstsConfig.Project, issueType, bypassRules: true).Result;
            }
        }
    }

    public static class JiraEx
    {
        /// <summary>
        /// VSTS: New, Active, Closed, Removed, Resolved.
        /// JIRA: To Do, In Progress, Dev Complete, In Testing, Done.
        /// </summary>
        public static string ToVsts(this IssueStatus issue)
        {
            bool eq(IssueStatus a, string b) => a.ToString().Equals(b, StringComparison.OrdinalIgnoreCase);
            if (eq(issue, "to do")) return "New";
            if (eq(issue, "In Progress")) return "Active";
            if (eq(issue, "Dev Complete")) return "Active";
            if (eq(issue, "In Testing")) return "Active";
            return "Resolved";
        }

        public static string ToVsts(this IssuePriority issue)
        {
            return issue.ToString().Replace("P", string.Empty);
        }

        public static string ToVsts(this IssueType issue)
        {
            bool eq(IssueType a, string b) => a.ToString().Equals(b, StringComparison.OrdinalIgnoreCase);
            if (eq(issue, "Story")) return "User Story";
            if (eq(issue, "Epic")) return "Epic";
            if (eq(issue, "Bug")) return "Bug";
            if (eq(issue, "Sub-task")) return "Task";
            return "Task";
        }
    }
}
