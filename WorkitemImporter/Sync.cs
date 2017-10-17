using Atlassian.Jira;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
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

        public async Task ProcessAsync()
        {
            var connection = new VssConnection(new Uri(VstsConfig.Url), new VssBasicCredential(string.Empty, VstsConfig.PersonalAccessToken));
            var witClient = connection.GetClient<WorkItemTrackingHttpClient>();

            var jql = "project = 'LT Excelens' and status not in (done)";

            var jiraConn = Jira.CreateRestClient(JiraConfig.Url, JiraConfig.UserId, JiraConfig.Password);
            //var issues = await jiraConn.Issues.GetIssuesFromJqlAsync(jql, startAt: 0);
            var issues = (from i in jiraConn.Issues.Queryable
                          where i.Project.Equals(JiraConfig.Project)
                          orderby i.Created
                          select i).ToList();

            // Perhaps a sync Open sprints + backlog only?

            foreach (var issue in issues)
            {
                // Get the existing iterations defined in VSTS
                var all = witClient.GetClassificationNodeAsync(VstsConfig.Project, TreeStructureGroup.Iterations, null, 10).Result;

                // Navigate in the existing iterations and if necessary, create new values:
                var workItemClassificationNode = witClient.CreateOrUpdateClassificationNodeAsync(new WorkItemClassificationNode() { Name = iterationName, }, projectName, TreeStructureGroup.Iterations, parentPath).Result;

                // Create the JSON necessary to create/ update the WorkItem in VSTS JsonPatchDocument document = new JsonPatchDocument();
                string title = String.Format("[{0}] [{1}] {2}" + , DateTime.Now.ToLongTimeString(), issue.Key, issue.Summary);
                title = title.Substring(0, Math.Min(title.Length, 128));
                document.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Title", Value = title });
                if (issue.Description != null)
                {
                    document.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.Description", Value = issue.Description });
                }

                JiraUser user = jiraConn.Users.SearchUsersAsync(issue.Reporter).Result.FirstOrDefault();
                if (user != null)
                {
                    document.Add(new JsonPatchOperation { Operation = Operation.Add, Path = "/fields/System.CreatedBy", Value = user.Email });
                }

                // Create/Update the workitem in VSTS
                workItem = witClient.CreateWorkItemAsync(document, project, workItemType).Result;
                workItem = witClient.UpdateWorkItemAsync(document, id).Result;
            }
        }
    }
}
