using Atlassian.Jira;
using System;
using System.Collections.Generic;
using System.Configuration;

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
}
