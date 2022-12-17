using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using DnnSharp.Common;
using DnnSharp.Common.Dnn;
using DnnSharp.Common2.Services.Dnn;
using DnnSharp.SearchBoost.Core.Behaviors;
using DnnSharp.SearchBoost.Core.ContentSource;
using DnnSharp.SearchBoost.Core.Indexing;
using DnnSharp.SearchBoost.Core.Search;
using DnnSharp.SearchBoost.Core.Services;
using DotNetNuke.Entities.Modules;

namespace DnnSharp.SearchBoost.LiveTabsIntegration.ContentSources {
    public class LiveAccordionSource : IContentSource {
        private readonly IIndexingLoggerService loggerService;
        private readonly IModuleService moduleService;

        public LiveAccordionSource(IIndexingLoggerService loggerService, IModuleService moduleService) {
            this.loggerService = loggerService;
            this.moduleService = moduleService;
        }

        public IEnumerable<IndexingJob> Query(SearchBehavior behavior, DateTimeOffset? since, CancellationToken cancellationToken) {
            ICollection<LiveModuleData> liveAccordions = new List<LiveModuleData>();
            SqlConnection connection = null;
            SqlCommand command = null;
            try {
                string connectionString = DnnConfig.ConnStr;
                connection = new SqlConnection(connectionString);
                command = connection.CreateCommand();

                if (cancellationToken.IsCancellationRequested)
                    yield break;

                List<int> liveAccordionInCurrentPortal = new List<int>();
                foreach (ModuleInfo moduleInfo in ModuleController.Instance.GetModules(behavior.PortalId)) {
                    if (moduleInfo.ModuleControl.ControlTitle == "Live Accordion")
                        liveAccordionInCurrentPortal.Add(moduleInfo.ModuleID);
                }

                if (!liveAccordionInCurrentPortal.Any())
                    yield break;

                command.CommandText = SqlUtil.ReplaceDbOwnerAndPrefix(@"SELECT ModuleId, PaneId, PaneName, Content, EmbeddedModule, CreatedBy, PublishedOn, PaneHeader FROM {databaseOwner}[{objectQualifier}LiveAccordion_Pane] LP
                                        WHERE [Version] = (SELECT MAX([Version]) FROM {databaseOwner}[{objectQualifier}LiveAccordion_Pane] LP2 WHERE LP2.PaneId = LP.PaneId AND LP2.ModuleId = LP.ModuleId)
                                        AND ModuleId IN (" + string.Join(",", liveAccordionInCurrentPortal) + ")");
                connection.Open();
                SqlDataReader reader = command.ExecuteReader();

                while (reader.Read()) {
                    try {
                        if (cancellationToken.IsCancellationRequested)
                            yield break;

                        string paneName = (string)reader["PaneName"];
                        string paneHeader = reader["PaneHeader"] as string;

                        if (!string.IsNullOrWhiteSpace(paneHeader))
                            paneName = Utils.GetPlainTextContent(paneHeader);

                        liveAccordions.Add(new LiveModuleData {
                            ModuleId = (int)reader["ModuleId"],
                            Number = (int)reader["PaneId"],
                            Name = paneName,
                            ContentSourceId = "LiveAccordion",
                            Content = reader["Content"] as string,
                            EmbeddedModules = Utils.GetModuleId(reader["EmbeddedModule"] as string),
                            CreatedBy = (int)reader["CreatedBy"],
                            PublishedOn = reader["PublishedOn"] as DateTime?
                        });
                    } catch (Exception ex) {
                        loggerService.Error(behavior.Id, () => $"Could not index content for LiveAccordion: Id: {reader["ModuleId"]}, PaneName: {reader["PaneName"]}, PaneHeader: {reader["PaneHeader"]}.", ex);
                    }
                }
                reader.Close();
            } catch (Exception ex) {
                loggerService.Error(behavior.Id, () => "Could not get LiveAccordion data from sql tables. Exception: ", ex);
                yield break;
            } finally {
                if (connection != null)
                    connection.Dispose();
                if (command != null)
                    command.Dispose();
            }

            foreach (var job in Utils.GetJobs(behavior, liveAccordions, since, loggerService, moduleService))
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
