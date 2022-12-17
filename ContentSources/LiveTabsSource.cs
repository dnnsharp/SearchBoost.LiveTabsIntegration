using DnnSharp.SearchBoost.Core.Behaviors;
using DnnSharp.SearchBoost.Core.ContentSource;
using DnnSharp.SearchBoost.Core.Indexing;
using DnnSharp.SearchBoost.Core.Search;
using DnnSharp.SearchBoost.Core.Services;
using DnnSharp.Common;
using DnnSharp.Common.Dnn;
using DnnSharp.Common2.Services.Dnn;
using DotNetNuke.Entities.Modules;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Web;
using System.Xml.Linq;
using System.Threading;

namespace DnnSharp.SearchBoost.LiveTabsIntegration.ContentSources {
    public class LiveTabsSource : IContentSource {
        private readonly IIndexingLoggerService loggerService;
        private readonly IModuleService moduleService;

        public LiveTabsSource(IIndexingLoggerService loggerService, IModuleService moduleService) {
            this.loggerService = loggerService;
            this.moduleService = moduleService;
        }

        public IEnumerable<IndexingJob> Query(SearchBehavior behavior, DateTimeOffset? since, CancellationToken cancellationToken) {
            ICollection<LiveModuleData> liveTabs = new List<LiveModuleData>();
            SqlConnection connection = null;
            SqlCommand command = null;
            try {
                string connectionString = DnnConfig.ConnStr;
                connection = new SqlConnection(connectionString);
                command = connection.CreateCommand();

                if (cancellationToken.IsCancellationRequested)
                    yield break;

                List<int> liveTabsInCurrentPortal = new List<int>();
                foreach (ModuleInfo moduleInfo in ModuleController.Instance.GetModules(behavior.PortalId)) {
                    if (moduleInfo.ModuleControl.ControlTitle == "Live Tabs")
                        liveTabsInCurrentPortal.Add(moduleInfo.ModuleID);
                }

                if (!liveTabsInCurrentPortal.Any())
                    yield break;

                command.CommandText = SqlUtil.ReplaceDbOwnerAndPrefix(@"SELECT ModuleId, TabId, TabName, Content, EmbeddedModule, CreatedBy, PublishedOn, TabHeader FROM {databaseOwner}[{objectQualifier}LiveTabs_Tab] LT
                                        WHERE [Version] = (SELECT MAX([Version]) FROM {databaseOwner}[{objectQualifier}LiveTabs_Tab] LT2 WHERE LT2.TabId = LT.TabId AND LT2.ModuleId = LT.ModuleId)
                                        AND ModuleId in (" + string.Join(",", liveTabsInCurrentPortal) + ")");
                connection.Open();
                SqlDataReader reader = command.ExecuteReader();

                while (reader.Read()) {
                    try {
                        if (cancellationToken.IsCancellationRequested)
                            yield break;

                        string tabName = (string)reader["TabName"];
                        string tabHeader = reader["TabHeader"] as string;

                        if (!string.IsNullOrWhiteSpace(tabHeader))
                            tabName = Utils.GetPlainTextContent(tabHeader);

                        liveTabs.Add(new LiveModuleData {
                            ModuleId = (int)reader["ModuleId"],
                            Number = (int)reader["TabId"],
                            Name = tabName,
                            ContentSourceId= "LiveTabs",
                            Content = reader["Content"] as string,
                            EmbeddedModules = Utils.GetModuleId(reader["EmbeddedModule"] as string),
                            CreatedBy = (int)reader["CreatedBy"],
                            PublishedOn = reader["PublishedOn"] as DateTime?
                        });
                    } catch (Exception ex) {
                        loggerService.Error(behavior.Id, () => $"Could not index content for LiveTabs: Id: {reader["ModuleId"]}, TabName: {reader["TabName"]}, TabHeader: {reader["TabHeader"]}.", ex);
                    }
                }
                reader.Close();
            } catch (Exception ex) {
                loggerService.Error(behavior.Id, () => "Could not get LiveTabs data from sql tables. Exception: ", ex);
                yield break;
            } finally {
                if (connection != null)
                    connection.Dispose();
                if (command != null)
                    command.Dispose();
            }

            foreach (var job in Utils.GetJobs(behavior, liveTabs, since, loggerService, moduleService))
                yield return job;
        }

        public string ComputeResultUrl(SbSearchResult searchResult, SearchContext searchContext) {
            return new GenericUrlResolver() {
                SearchResult = searchResult,
                SearchContext = searchContext
            }.GetUrlForSearchResult() + "#" + HttpUtility.UrlEncode(searchResult.ItemPath);
        }
    }

}
