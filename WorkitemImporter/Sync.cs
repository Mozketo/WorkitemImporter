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
using WorkitemImporter.JiraAgile;

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
        public IEnumerable<JiraSprint> ActiveSprints { get; private set; }

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
                Console.WriteLine($" Fetching chunk {index + 1}, retrieving {issues.Count()} items.");
                return issues;
            }

            var vssConnection = new VssConnection(new Uri(Vsts.Url), new VssBasicCredential(string.Empty, Vsts.PersonalAccessToken));
            var jiraConn = Atlassian.Jira.Jira.CreateRestClient((string)Jira.Url, (string)Jira.UserId, (string)Jira.Password);

            var boards = jiraConn.Boards(System.Configuration.ConfigurationManager.AppSettings[Const.JiraProject]).AsEmptyIfNull();
            if (boards.Count() != 1) Console.WriteLine($"{boards.Count()} found, so unable to determine active sprints for the Jira Project");
            if (boards.Any())
            {
                ActiveSprints = jiraConn.Sprints(boards.First().Id);
                Console.WriteLine($"Active sprints: {string.Join(", ", ActiveSprints.Select(s => s.Name))}");
            }

            foreach (var jql in jiraQueries)
            {
                Console.WriteLine($"Processing '{jql}'");
                int index = 0;
                var issues = fetch(jiraConn, jql, index, take);
                while (issues.Any())
                {
                    SyncEpicForIssues(vssConnection, jiraConn, issues);
                    SyncToVsts(vssConnection, issues);
                    issues = fetch(jiraConn, jql, ++index, take);
                }
            }
        }

        /// <summary>
        /// Identify the unique sprints in the issues and create in VSTS. If a sprint has already been processed it will be skipped.
        /// </summary>
        void SyncSprintsForIssues(VssConnection connection, IEnumerable<Issue> issues, string project)
        {
            var witClient = connection.GetClient<WorkItemTrackingHttpClient>();
            var sprints = issues.SelectMany(i => i.PreferredSprint(ActiveSprints)) // CustomFields["Sprint"].Values.LastOrDefault()).Where(i => i != null)
                .Where(i => i != null)
                .Distinct().Select(i => i.Replace("/", "-"));
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
                            Console.WriteLine($"Creating VSTS iteration {sprint}");
                        }
                    }
                });
            }
        }

        /// <summary>
        /// For the issues provided ensure that the Epics are created in VSTS before processing. Ignores Epics already sync'd.
        /// </summary>
        void SyncEpicForIssues(VssConnection connection, Jira jira, IEnumerable<Issue> issues)
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

                string issueSprint = issue.PreferredSprint(ActiveSprints).FirstOrDefault();
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

    public static class IssueEx
    {
        public static IEnumerable<string> PreferredSprint(this Issue issue, IEnumerable<JiraSprint> activeSprints)
        {
            var issueSprints = issue.CustomFields["Sprint"].Values.EmptyIfNull();
            var result = issueSprints.Intersect(activeSprints.Select(a => a.Name));
            return result;
        }
    }
}
