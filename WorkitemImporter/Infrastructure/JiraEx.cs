using Atlassian.Jira;
using System;
using System.Collections.Generic;
using System.Configuration;
using WorkitemImporter.JiraAgile;

namespace WorkitemImporter.Infrastructure
{
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

        static string Map(this IDictionary<string, string> items, string value)
        {
            if (value == null) return String.Empty;
            if (!items.ContainsKey(value))
            {
                Console.WriteLine($"Cannot map {value}");
                return value;
            }
            return items[value];
        }

        /// <summary>
        /// VSTS: New, Active, Closed, Removed, Resolved.
        /// JIRA: To Do, In Progress, Dev Complete, In Testing, Done.
        /// </summary>
        public static string ToVsts(this IssueStatus issueStatus)
        {
            return Status.Map(issueStatus.ToString());
        }

        public static string ToVsts(this IssuePriority issuePriority)
        {
            return Priority.Map(issuePriority.ToString());
        }

        public static string ToVsts(this IssueType issueType)
        {
            return IssueType.Map(issueType.ToString());
        }

        public static string AsJiraUserToVsts(this string user)
        {
            return Users.Map(user);
        }
    }

    public static class JiraEx2
    {
        /// <summary>
        /// Return all boards for a project. Note: doesn't yet support paging
        /// https://docs.atlassian.com/jira-software/REST/cloud/#agile/1.0/board-getAllBoards
        /// </summary>
        public static IEnumerable<JiraBoard> Boards(this Jira jiraConn, string project)
        {
            var restClient = jiraConn.RestClient.RestSharpClient;
            var request = new RestSharp.RestRequest(RestSharp.Method.GET)
            {
                Resource = "/rest/agile/1.0/board"
            };
            request.AddParameter("projectKeyOrId", project);
            var response = restClient.Execute<JiraPage<JiraBoard>>(request);
            return response.Data.Values;
        }

        /// <summary>
        /// Return all sprints for a project. Note: doesn't yet support paging.
        /// https://docs.atlassian.com/jira-software/REST/cloud/#agile/1.0/board/{boardId}/sprint-getAllSprints
        /// </summary>
        public static IEnumerable<JiraSprint> Sprints(this Jira jiraConn, int boardId, string sprintState = "active")
        {
            var restClient = jiraConn.RestClient.RestSharpClient;
            var request = new RestSharp.RestRequest(RestSharp.Method.GET)
            {
                Resource = $"/rest/agile/1.0/board/{boardId}/sprint"
            };
            request.AddParameter("state", sprintState);
            var response = restClient.Execute<JiraPage<JiraSprint>>(request);
            return response.Data.Values;
        }
    }
}
