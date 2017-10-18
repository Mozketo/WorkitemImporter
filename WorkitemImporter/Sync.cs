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
                //SyncToVsts(epics);
            }

            {
                jql = $"{jql} and type != epic";
                var issues = jiraConn.Issues.GetIssuesFromJqlAsync(jql, startAt: 0, maxIssues: take).Result;
                var chunks = Enumerable.Range(1, (int)Math.Floor(((decimal)issues.TotalItems / take)));
                foreach (var index in chunks)
                {
                    SyncToVsts(issues);
                    issues = jiraConn.Issues.GetIssuesFromJqlAsync(jql, startAt: index * take, maxIssues: take).Result;
                }
            }
        }

        void SyncToVsts(IEnumerable<Issue> issues)
        {
            if (!issues.AsEmptyIfNull().Any()) return;

            var connection = new VssConnection(new Uri(VstsConfig.Url), new VssBasicCredential(string.Empty, VstsConfig.PersonalAccessToken));
            var witClient = connection.GetClient<WorkItemTrackingHttpClient>();
            //var x = witClient.GetWorkItemAsync(49).Result;

            // Get the existing iterations defined in VSTS
            IEnumerable<WorkItemClassificationNode> GetIterations()
            {
                var iterations = witClient.GetClassificationNodeAsync(VstsConfig.Project, TreeStructureGroup.Iterations, null, 10).Result;
                return iterations.Children;
            }

            var sprints = issues.Select(i => i.CustomFields["Sprint"].Values.FirstOrDefault()).Where(i => i != null).Distinct().ToList();
            foreach (var sprint in sprints)
            {
                var sprintName = sprint.Replace("/", "");
                if (!GetIterations().Any(i => i.Name.Equals(sprintName, StringComparison.OrdinalIgnoreCase)))
                {
                    // Add any missing VSTS iterations to map the Jira tickets to 
                    var workItemClassificationNode = witClient.CreateOrUpdateClassificationNodeAsync(new WorkItemClassificationNode() { Name = sprintName, }, VstsConfig.Project, TreeStructureGroup.Iterations).Result;
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

                AddField(doc, "Microsoft.VSTS.Scheduling.StoryPoints", issue.CustomFields["Story Points"]?.Values.FirstOrDefault());
                AddField(doc, "System.State", issue.Status.ToVsts());
                AddField(doc, "Microsoft.VSTS.Common.Priority", issue.Priority.ToVsts());
                AddField(doc, "System.Tags", string.Join(";", issue.Labels));

                if (!existingWorkItemId.exists)
                {
                    AddField(doc, "System.CreatedDate", issue.Created);
                    AddField(doc, "System.ChangedDate", issue.Updated);
                    AddField(doc, "System.History", $"Import from Jira {DateTime.Now} (NZ). Original Jira ID: {issue.Key}");
                }

                var epicLink = issue.CustomFields["Epic Link"]?.Values.FirstOrDefault();
                if (!string.IsNullOrEmpty(epicLink))
                {
                    var epic = WorkItemExists(epicLink);
                    if (epic.exists)
                    {
                        var targetWorkItem = witClient.GetWorkItemAsync(epic.id.Value).Result;
                        doc.Add(new JsonPatchOperation()
                        {
                            Operation = Operation.Add,
                            Path = "/relations/-",
                            Value = new
                            {
                                rel = "System.LinkTypes.Hierarchy-Reverse",
                                url = targetWorkItem.Url,
                                attributes = new { comment = "Link supplied via Jira import" }
                            }
                        });
                    }
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
            bool eq(IssuePriority a, string b) => a.ToString().Equals(b, StringComparison.OrdinalIgnoreCase);
            if (eq(issue, "P1")) return "1";
            if (eq(issue, "P2")) return "2";
            if (eq(issue, "P3")) return "3";
            if (eq(issue, "P4")) return "4";
            if (eq(issue, "P5")) return "4";
            if (eq(issue, "Critical")) return "2";
            if (eq(issue, "Major")) return "2";
            if (eq(issue, "Minor")) return "3";
            if (eq(issue, "Trivial")) return "4";
            return "4";
        }

        public static string ToVsts(this IssueType issue)
        {
            bool eq(IssueType a, string b) => a.ToString().Equals(b, StringComparison.OrdinalIgnoreCase);
            if (eq(issue, "Story")) return "User Story";
            if (eq(issue, "Epic")) return "Feature";
            if (eq(issue, "Bug")) return "Bug";
            if (eq(issue, "Sub-task")) return "Task";
            return "Task";
        }
    }
}
