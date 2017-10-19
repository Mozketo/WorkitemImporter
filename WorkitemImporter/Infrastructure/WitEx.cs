using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WorkitemImporter.Infrastructure
{
    public static class WitEx
    {
        public static int? GetWorkItemIdByTitleAsync(this WorkItemTrackingHttpClient witClient, string project, string title)
        {
            var query = new Wiql { Query = $"Select [System.Id] from WorkItems Where [System.TeamProject] = '{project}' AND [System.Title] CONTAINS '{title}'" };
            var qResult = witClient.QueryByWiqlAsync(query).Result;
            return qResult.WorkItems.AsEmptyIfNull().FirstOrDefault()?.Id;
        }

        public static WorkItem GetWorkItemByTitleAsync(this WorkItemTrackingHttpClient witClient, string project, string title)
        {
            var query = new Wiql { Query = $"Select [System.Id] from WorkItems Where [System.TeamProject] = '{project}' AND [System.Title] CONTAINS '{title}'" };
            var qResult = witClient.QueryByWiqlAsync(query).Result;
            var id = qResult.WorkItems.AsEmptyIfNull().FirstOrDefault()?.Id;
            return id != null
                ? witClient.GetWorkItemAsync(id.Value).Result
                : null;
        }
    }
}
