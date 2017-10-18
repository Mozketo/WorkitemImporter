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
using System.Configuration;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WorkitemImporter.Infrastructure;

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

        public void Process(IEnumerable<string> jiraQueries, bool previewMode = false)
        {
            int numberOfChunks(int total, int size)
            {
                if (size == 0) return 0;
                var result = Math.Floor(((decimal)total / size));
                return (int)Math.Max(result, 1);
            }

            const int take = 25;
            var jiraConn = Jira.CreateRestClient(JiraConfig.Url, JiraConfig.UserId, JiraConfig.Password);

            foreach (var jql in jiraQueries)
            {
                var issues = jiraConn.Issues.GetIssuesFromJqlAsync(jql, startAt: 0, maxIssues: take).Result;
                Console.WriteLine($"Fetching {issues.TotalItems} from Jira '{jql}'");
                var chunks = Enumerable.Range(1, numberOfChunks(issues.TotalItems, take));
                foreach (var index in chunks)
                {
                    SyncToVsts(issues, previewMode);
                    issues = jiraConn.Issues.GetIssuesFromJqlAsync(jql, startAt: index * take, maxIssues: take).Result;
                    Console.WriteLine($"  Fetching chunk {index * take}, retrieving {issues.Count()} items");
                }
            }

            //var jql = "project = 'LT Excelens' and status not in (done)";

            //{
            //    // Before uploading issues to VSTS ensure all Epics are in place for wiring up
            //    var epics = jiraConn.Issues.GetIssuesFromJqlAsync($"{jql} and type = epic and sprint is empty", startAt: 0, maxIssues: take).Result;
            //    //SyncToVsts(epics);
            //}

            //{
            //    jql = $"{jql} and type != epic";
            //    var issues = jiraConn.Issues.GetIssuesFromJqlAsync(jql, startAt: 0, maxIssues: take).Result;
            //    var chunks = Enumerable.Range(1, (int)Math.Floor(((decimal)issues.TotalItems / take)));
            //    foreach (var index in chunks)
            //    {
            //        SyncToVsts(issues);
            //        issues = jiraConn.Issues.GetIssuesFromJqlAsync(jql, startAt: index * take, maxIssues: take).Result;
            //    }
            //}
        }

        void SyncToVsts(IEnumerable<Issue> issues, bool previewMode = false)
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
                AddField(doc, "System.CreatedBy", issue.Reporter.AsJiraUserToVsts());
                AddField(doc, "System.AssignedTo", issue.Assignee.AsJiraUserToVsts());
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

                //Console.WriteLine($"Syncing, {issue.Key}, reporter, {issue.Reporter}");

                if (!previewMode)
                {
                    // Create/Update the workitem in VSTS
                    var issueType = issue.Type.ToVsts();
                    var workItem = existingWorkItemId.exists
                        ? witClient.UpdateWorkItemAsync(doc, existingWorkItemId.id.Value).Result
                        : witClient.CreateWorkItemAsync(doc, VstsConfig.Project, issueType, bypassRules: true).Result;
                }
            }
        }
    }

    public static class JiraEx
    {
        static Dictionary<string, string> Priority;
        static Dictionary<string, string> Status;
        static Dictionary<string, string> IssueType;
        static Dictionary<string, string> Users;

        static JiraEx()
        {
            Priority = ConfigurationManager.AppSettings[Const.JiraMapPriority].ToDictionary();
            Status = ConfigurationManager.AppSettings[Const.JiraMapStatus].ToDictionary();
            IssueType = ConfigurationManager.AppSettings[Const.JiraMapType].ToDictionary();
            Users = ConfigurationManager.AppSettings[Const.JiraMapUsers].ToDictionary();
        }

        /// <summary>
        /// VSTS: New, Active, Closed, Removed, Resolved.
        /// JIRA: To Do, In Progress, Dev Complete, In Testing, Done.
        /// </summary>
        public static string ToVsts(this IssueStatus issueStatus)
        {
            if (issueStatus == null) return String.Empty;
            if (!Status.ContainsKey(issueStatus.ToString()))
            {
                Console.WriteLine($"Cannot map {nameof(issueStatus)} {issueStatus.ToString()}");
                return issueStatus.ToString();
            }
            return Status[issueStatus.ToString()];
        }

        public static string ToVsts(this IssuePriority issuePriority)
        {
            if (issuePriority == null) return String.Empty;
            if (!Priority.ContainsKey(issuePriority.ToString()))
            {
                Console.WriteLine($"Cannot map {nameof(issuePriority)} {issuePriority.ToString()}");
                return issuePriority.ToString();
            }
            return Priority[issuePriority.ToString()];
        }

        public static string ToVsts(this IssueType issueType)
        {
            if (issueType == null) return String.Empty;
            if (!IssueType.ContainsKey(issueType.ToString()))
            {
                Console.WriteLine($"Cannot map {nameof(issueType)} {issueType.ToString()}");
                return issueType.ToString();
            }
            return IssueType[issueType.ToString()];
        }

        public static string AsJiraUserToVsts(this string user)
        {
            if (user.IsNullOrEmpty()) return string.Empty;
            if (!Users.ContainsKey(user))
            {
                Console.WriteLine($"Cannot map {nameof(user)} {user}");
                return user;
            }
            return Users[user];
        }
    }
}
