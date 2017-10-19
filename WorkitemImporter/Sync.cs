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
        static HashSet<string> Processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static Action<string, Action> InvokeIfNotProcessed = new Action<string, Action>((string key, Action a) =>
        {
            if (Processed.Contains(key)) return;
            a();
            Processed.Add(key);
        });

        public VstsConfig VstsConfig { get; }
        public JiraConfig JiraConfig { get; }

        public Sync(VstsConfig vstsConfig, JiraConfig jiraConfig)
        {
            JiraConfig = jiraConfig;
            VstsConfig = vstsConfig;
        }

        public void Process(IEnumerable<string> jiraQueries, bool previewMode = false)
        {
            const int take = 25;

            IPagedQueryResult<Issue> fetch(Jira jira, string jql, int index, int size)
            {
                int startAt = index * size;
                var issues = jira.Issues.GetIssuesFromJqlAsync(jql, startAt: startAt, maxIssues: size).Result;
                Console.WriteLine($" Fetching next chunk of {startAt}, retrieving {issues.Count()} items.");
                return issues;
            }

            int numberOfChunks(int total, int size)
            {
                if (size == 0) return 0;
                var result = Math.Floor(((decimal)total / size));
                return (int)Math.Max(result, 1);
            }

            var vssConnection = new VssConnection(new Uri(VstsConfig.Url), new VssBasicCredential(string.Empty, VstsConfig.PersonalAccessToken));
            var jiraConn = Jira.CreateRestClient(JiraConfig.Url, JiraConfig.UserId, JiraConfig.Password);

            foreach (var jql in jiraQueries)
            {
                Console.WriteLine($"Processing '{jql}'");
                var issues = fetch(jiraConn, jql, 0, take);
                var chunks = Enumerable.Range(1, numberOfChunks(issues.TotalItems, take));
                foreach (var index in chunks)
                {
                    SyncEpicForIssues(vssConnection, jiraConn, issues, previewMode);
                    SyncToVsts(vssConnection, issues, previewMode);
                    issues = fetch(jiraConn, jql, index, take);
                }
            }
        }

        /// <summary>
        /// Identify the unique sprints in the issues and create in VSTS. If a sprint has already been processed it will be skipped.
        /// </summary>
        void SyncSprintsForIssues(VssConnection connection, IEnumerable<Issue> issues, string project)
        {
            var witClient = connection.GetClient<WorkItemTrackingHttpClient>();
            var sprints = issues.Select(i => i.CustomFields["Sprint"].Values.FirstOrDefault()).Where(i => i != null)
                .Distinct().Select(i => i.Replace("/", string.Empty));
            foreach (var sprint in sprints)
            {
                InvokeIfNotProcessed(sprint, () =>
                {
                    var iterations = witClient.GetClassificationNodeAsync(project, TreeStructureGroup.Iterations, null, 10).Result.Children;
                    if (!iterations.Any(i => i.Name.Equals(sprint, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Add any missing VSTS iterations to map the Jira tickets to 
                        var unused = witClient.CreateOrUpdateClassificationNodeAsync(new WorkItemClassificationNode() { Name = sprint, }, VstsConfig.Project, TreeStructureGroup.Iterations).Result;
                    }
                });
            }
        }

        /// <summary>
        /// For the issues provided ensure that the Epics are created in VSTS before processing. Ignores Epics already sync'd.
        /// </summary>
        void SyncEpicForIssues(VssConnection connection, Jira jira, IEnumerable<Issue> issues, bool previewMode)
        {
            var epics = issues.Select(i => i.CustomFields["Epic Link"]?.Values.FirstOrDefault())
                .EmptyIfNull().Trim().Distinct().ToList();
            foreach (var epic in epics)
            {
                InvokeIfNotProcessed(epic, () =>
                {
                    var issue = jira.Issues.GetIssueAsync(epic).Result;
                    SyncToVsts(connection, new[] { issue }, previewMode);
                });
            }
        }

        void SyncToVsts(VssConnection connection, IEnumerable<Issue> issues, bool previewMode)
        {
            if (!issues.AsEmptyIfNull().Any()) return;

            if (!previewMode)
            {
                SyncSprintsForIssues(connection, issues, VstsConfig.Project);
            }

            void AddField(JsonPatchDocument doc, string path, object value)
            {
                if (value is null) return;
                if (value is string && string.IsNullOrEmpty(value.ToString())) return;
                doc.Add(new JsonPatchOperation { Operation = Operation.Add, Path = $"/fields/{path}", Value = value });
            }

            void AddRelationship(JsonPatchDocument doc, string rel, WorkItem parent)
            {
                if (rel.IsNullOrEmpty()) return;
                if (parent == null) return;
                doc.Add(new JsonPatchOperation()
                {
                    Operation = Operation.Add,
                    Path = "/relations/-",
                    Value = new { rel, url = parent.Url, attributes = new { comment = "Link supplied via Jira import" } }
                });
            }

            var witClient = connection.GetClient<WorkItemTrackingHttpClient>();
            foreach (var issue in issues)
            {
                var existingWorkItemId = witClient.GetWorkItemIdByTitleAsync(VstsConfig.Project, $"{issue.Key.ToString()}");
                bool isNew = !existingWorkItemId.HasValue;

                var doc = new JsonPatchDocument();

                AddField(doc, "System.Title", $"{issue.Key.ToString()} {issue.Summary}");
                AddField(doc, "System.Description", issue.Description);
                AddField(doc, "System.CreatedBy", issue.Reporter.AsJiraUserToVsts());
                AddField(doc, "System.AssignedTo", issue.Assignee.AsJiraUserToVsts());
                AddField(doc, "Microsoft.VSTS.Scheduling.StoryPoints", issue.CustomFields["Story Points"]?.Values.FirstOrDefault());
                AddField(doc, "System.State", issue.Status.ToVsts());
                AddField(doc, "Microsoft.VSTS.Common.Priority", issue.Priority.ToVsts());
                AddField(doc, "System.Tags", string.Join(";", issue.Labels));

                if (isNew)
                {
                    AddField(doc, "System.CreatedDate", issue.Created);
                    AddField(doc, "System.ChangedDate", issue.Updated);
                    AddField(doc, "System.History", $"Import from Jira {DateTime.Now} (NZ). Original Jira ID: {issue.Key}");

                    // Link epics up
                    var epicLinkName = issue.CustomFields["Epic Link"]?.Values.FirstOrDefault();
                    if (!epicLinkName.IsNullOrEmpty())
                    {
                        var parent = witClient.GetWorkItemByTitleAsync(VstsConfig.Project, epicLinkName);
                        AddRelationship(doc, "System.LinkTypes.Hierarchy-Reverse", parent); // Set Epic as parent-child relationship
                    }

                    // Link to parents (Jira sub-tasks)
                    if (!issue.ParentIssueKey.IsNullOrEmpty())
                    {
                        var parent = witClient.GetWorkItemByTitleAsync(VstsConfig.Project, issue.ParentIssueKey);
                        AddRelationship(doc, "System.LinkTypes.Hierarchy-Reverse", parent); // Set Epic as parent-child relationship
                    }
                }

                string issueSprint = issue.CustomFields["Sprint"].Values.FirstOrDefault();
                if (!string.IsNullOrEmpty(issueSprint))
                {
                    var iterations = witClient.GetClassificationNodeAsync(VstsConfig.Project, TreeStructureGroup.Iterations, null, 10).Result.Children;
                    var iteration = iterations.FirstOrDefault(i => i.Name.Equals(issueSprint, StringComparison.OrdinalIgnoreCase));
                    AddField(doc, "System.IterationID", iteration?.Id);
                }

                if (!previewMode)
                {
                    // Create/Update the workitem in VSTS
                    var issueType = issue.Type.ToVsts();
                    var workItem = isNew
                        ? witClient.CreateWorkItemAsync(doc, VstsConfig.Project, issueType, bypassRules: true).Result
                        : witClient.UpdateWorkItemAsync(doc, existingWorkItemId.Value).Result;
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
