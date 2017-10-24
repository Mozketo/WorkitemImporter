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

        public VstsConfig Vsts { get; } = Configuration.Instance.Vsts;
        public JiraConfig Jira { get; } = Configuration.Instance.Jira;
        public ProcessingMode Mode { get; }

        public Sync(ProcessingMode mode)
        {
            Mode = mode;
        }

        public void Process(IEnumerable<string> jiraQueries)
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

            var vssConnection = new VssConnection(new Uri(Vsts.Url), new VssBasicCredential(string.Empty, Vsts.PersonalAccessToken));
            var jiraConn = Atlassian.Jira.Jira.CreateRestClient((string)Jira.Url, (string)Jira.UserId, (string)Jira.Password);

            foreach (var jql in jiraQueries)
            {
                Console.WriteLine($"Processing '{jql}'");
                var issues = fetch(jiraConn, jql, 0, take);
                var chunks = Enumerable.Range(1, numberOfChunks(issues.TotalItems, take));
                foreach (var index in chunks)
                {
                    SyncEpicForIssues(vssConnection, jiraConn, issues);
                    SyncToVsts(vssConnection, issues);
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
                InvokeIfNotProcessed($"sprint-{sprint}", () =>
                {
                    var iterations = witClient.GetClassificationNodeAsync(project, TreeStructureGroup.Iterations, null, 10).Result.Children;
                    if (!iterations.Any(i => i.Name.Equals(sprint, StringComparison.OrdinalIgnoreCase)))
                    {
                        // Add any missing VSTS iterations to map the Jira tickets to 
                        if (Mode == ProcessingMode.ReadWrite)
                        {
                            var unused = witClient.CreateOrUpdateClassificationNodeAsync(new WorkItemClassificationNode() { Name = sprint, }, Vsts.Project, TreeStructureGroup.Iterations).Result;
                        }
                    }
                });
            }
        }

        /// <summary>
        /// For the issues provided ensure that the Epics are created in VSTS before processing. Ignores Epics already sync'd.
        /// </summary>
        void SyncEpicForIssues(VssConnection connection, Atlassian.Jira.Jira jira, IEnumerable<Issue> issues)
        {
            var epics = issues.Select(i => i.CustomFields["Epic Link"]?.Values.FirstOrDefault())
                .EmptyIfNull().Trim().Distinct().ToList();
            foreach (var epic in epics)
            {
                InvokeIfNotProcessed(epic, () =>
                {
                    var issue = jira.Issues.GetIssueAsync(epic).Result;
                    SyncToVsts(connection, new[] { issue });
                });
            }
        }

        void SyncToVsts(VssConnection connection, IEnumerable<Issue> issues)
        {
            if (!issues.AsEmptyIfNull().Any()) return;

            SyncSprintsForIssues(connection, issues, Vsts.Project);

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
                var existingWorkItemId = witClient.GetWorkItemIdByTitleAsync(Vsts.Project, $"{issue.Key.ToString()}");
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
                        var parent = witClient.GetWorkItemByTitleAsync(Vsts.Project, epicLinkName);
                        AddRelationship(doc, "System.LinkTypes.Hierarchy-Reverse", parent); // Set Epic as parent-child relationship
                    }

                    // Link to parents (Jira sub-tasks)
                    if (!issue.ParentIssueKey.IsNullOrEmpty())
                    {
                        var parent = witClient.GetWorkItemByTitleAsync(Vsts.Project, issue.ParentIssueKey);
                        AddRelationship(doc, "System.LinkTypes.Hierarchy-Reverse", parent); // Set Epic as parent-child relationship
                    }
                }

                string issueSprint = issue.CustomFields["Sprint"].Values.FirstOrDefault();
                if (!string.IsNullOrEmpty(issueSprint))
                {
                    var iterations = witClient.GetClassificationNodeAsync(Vsts.Project, TreeStructureGroup.Iterations, null, 10).Result.Children;
                    var iteration = iterations.FirstOrDefault(i => i.Name.Equals(issueSprint, StringComparison.OrdinalIgnoreCase));
                    AddField(doc, "System.IterationID", iteration?.Id);
                }

                // Create/Update the workitem in VSTS
                var issueType = issue.Type.ToVsts();
                var workItem = isNew
                    ? witClient.CreateWorkItemAsync(doc, Vsts.Project, issueType, bypassRules: true, validateOnly: Mode == ProcessingMode.ReadOnly).Result
                    : witClient.UpdateWorkItemAsync(doc, existingWorkItemId.Value, bypassRules: true, validateOnly: Mode == ProcessingMode.ReadOnly).Result;
            }
        }
    }
}
