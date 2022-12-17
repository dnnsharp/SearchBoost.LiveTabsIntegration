using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DnnSharp.Common;
using DnnSharp.Common2.Services.Dnn;
using DnnSharp.SearchBoost.Core.Behaviors;
using DnnSharp.SearchBoost.Core.Indexing;
using DnnSharp.SearchBoost.Core.Services;
using DotNetNuke.Entities.Modules;
using DotNetNuke.Entities.Tabs;
using DotNetNuke.Security.Permissions;

namespace DnnSharp.SearchBoost.LiveTabsIntegration {

    class LiveModuleData {
        /// <summary>
        /// The id of TabLive module
        /// </summary>
        public int ModuleId { get; set; }
        /// <summary>
        /// Pane/Tab number in Pane/TabLive module
        /// </summary>
        public int Number { get; set; }

        /// <summary>
        /// Name of the pane/tab in live accordion/tabs
        /// </summary>
        public string Name { get; set; }

        public string ContentSourceId { get; set; }

        public string Content { get; set; }
        public IEnumerable<int> EmbeddedModules { get; set; }

        public int CreatedBy { get; set; }
        public DateTime? PublishedOn { get; set; }
    }

    internal class Utils {

        /// <summary>
        /// Return jobs based on the liveModuleData
        /// </summary>
        /// <param name="behavior"></param>
        /// <param name="liveTabs"></param>
        /// <param name="since"></param>
        /// <param name="loggerService"></param>
        /// <param name="moduleService"></param>
        /// <returns></returns>
        internal static IEnumerable<IndexingJob> GetJobs(
            SearchBehavior behavior,
            ICollection<LiveModuleData> liveTabs,
            DateTimeOffset? since,
            IIndexingLoggerService loggerService,
            IModuleService moduleService
        ) {
            IEnumerable<int> portalsToIndex = behavior.SourcePortals.Select(x => x.PortalId)
                .Concat(behavior.SourceTabs.Select(x => x.PortalId))
                .Concat(behavior.SourceModules.Select(x => x.PortalId))
                .Distinct();

            Dictionary<int, BehaviorSourceTab> tabsToIndex = behavior.SourceTabs
                .GroupBy(x => x.TabId, x => x)
                .ToDictionary(x => x.Key, x => x.First());

            IEnumerable<int> modulesToIndex = behavior.SourceModules.Select(x => x.ModuleId);

            foreach (LiveModuleData liveTab in liveTabs) {
                ModuleInfo liveTabModuleInfo = moduleService.GetModule(liveTab.ModuleId, -1, false);
                if (liveTabModuleInfo.IsDeleted)
                    continue;

                if (!IsModuleSearchTarget(liveTabModuleInfo, portalsToIndex, tabsToIndex, modulesToIndex))
                    continue;

                IndexingJob job = ToIndexingJob(behavior, since, liveTab, liveTabModuleInfo, loggerService, moduleService);

                if (job is null || behavior.Settings.GetSearchModsExclusions().Any(searchExclusion => searchExclusion.IsExcluded(job)))
                    continue;

                yield return job;
            }
        }


        // Create IndexingJob
        static IndexingJob ToIndexingJob(SearchBehavior behavior,
            DateTimeOffset? since,
            LiveModuleData liveModuleData,
            ModuleInfo liveModuleInfo,
            IIndexingLoggerService loggerService,
            IModuleService moduleService
        ) {
            if (behavior is null)
                throw new ArgumentNullException(nameof(behavior));

            TabInfo tabInfo = TabController.Instance.GetTab(liveModuleInfo.TabID, liveModuleInfo.PortalID, false);
            if (tabInfo == null) {
                loggerService.Debug(behavior.Id, () => "LiveAccordion indexing: no tabinfo found for LiveAccordion module with ModuleId: " + liveModuleInfo.ModuleID);
                return null;
            }
            string tabPath = "/" + tabInfo.TabPath.Replace("//", "/").Trim('/').ToLower() + "/";

            IndexingJob job = new IndexingJob();
            job.ContentSourceId = liveModuleData.ContentSourceId;
            job.Due = DateTimeOffset.Now;
            job.Action = "add";
            job.Behavior = behavior;
            job.Priority = ePriorityIndexingJob.Low;
            job.BehaviorId = behavior.Id;
            job.PortalId = liveModuleInfo.PortalID;
            job.TabId = liveModuleInfo.TabID;
            job.ModuleId = liveModuleData.ModuleId;
            job.ItemId = liveModuleData.ModuleId.ToString() + "-" + liveModuleData.Number.ToString();

            // set metadata
            job.Metadata.Type = liveModuleData.ContentSourceId.ToLower();
            job.Metadata.SubType = "";
            job.Metadata.Url = "";
            job.Metadata.Title = liveModuleData.Name;

            job.Metadata.AuthorId = liveModuleData.CreatedBy;
            if (liveModuleData.PublishedOn.HasValue)
                job.Metadata.DatePublished = liveModuleData.PublishedOn.Value.ToUniversalTime();

            job.Metadata.ItemId = job.ItemId;
            job.Metadata.ItemPath = liveModuleData.ModuleId.ToString() + liveModuleData.Number.ToString() + "-" + GetUrlForTabName(liveModuleData.Name);

            job.Metadata.ContainerId = tabInfo.TabID.ToString();
            job.Metadata.ContainerName = string.IsNullOrEmpty(tabInfo.Title) ? tabInfo.TabName : tabInfo.Title;
            job.Metadata.ContainerPath = tabPath;

            // determine the security 
            var tabRoles = new List<PermissionInfoBase>(tabInfo.TabPermissions.ToList());
            var roleNames = new List<string>();
            var deniedRoles = new List<string>();
            var allowedUsers = new List<string>();
            var deniedUsers = new List<string>();

            if (liveModuleInfo.InheritViewPermissions) {
                foreach (var perm in tabRoles) {
                    if (perm.AllowAccess) {
                        if (!string.IsNullOrEmpty(perm.RoleName) && !roleNames.Contains(perm.RoleName))
                            roleNames.Add(perm.RoleName);
                        else if (perm.UserID > 0 && !allowedUsers.Contains(perm.UserID.ToString()))
                            allowedUsers.Add(perm.UserID.ToString());
                    } else {
                        if (!string.IsNullOrEmpty(perm.RoleName) && !deniedRoles.Contains(perm.RoleName))
                            deniedRoles.Add(perm.RoleName);
                        else if (perm.UserID > 0 && !allowedUsers.Contains(perm.UserID.ToString()))
                            deniedUsers.Add(perm.UserID.ToString());
                    }
                }
            } else {
                foreach (var perm in liveModuleInfo.ModulePermissions.ToList()) {
                    // only add module roles that also are allowed for the page
                    if (tabRoles.Exists(x => string.Equals(x.PermissionName, "All Users", StringComparison.InvariantCultureIgnoreCase)) || tabRoles.Contains(perm))
                        if (perm.AllowAccess) {
                            if (!string.IsNullOrEmpty(perm.RoleName) && !roleNames.Contains(perm.RoleName))
                                roleNames.Add(perm.RoleName);
                            else if (perm.UserID > 0 && !allowedUsers.Contains(perm.UserID.ToString()))
                                allowedUsers.Add(perm.UserID.ToString());
                        } else {
                            if (!string.IsNullOrEmpty(perm.RoleName) && !deniedRoles.Contains(perm.RoleName))
                                deniedRoles.Add(perm.RoleName);
                            else if (perm.UserID > 0 && !allowedUsers.Contains(perm.UserID.ToString()))
                                deniedUsers.Add(perm.UserID.ToString());
                        }
                }
            }

            loggerService.Debug(job.Behavior.Id, () => $"Allowed roles were determined to: {string.Join(",", roleNames ?? new List<string>())}");
            loggerService.Debug(job.Behavior.Id, () => $"Denied roles were determined to: {string.Join(",", deniedRoles ?? new List<string>())}");
            loggerService.Debug(job.Behavior.Id, () => $"Allowed users were determined to: {string.Join(",", allowedUsers ?? new List<string>())}");
            loggerService.Debug(job.Behavior.Id, () => $"Denied users were determined to: {string.Join(",", deniedUsers ?? new List<string>())}");

            job.Metadata.Roles = roleNames;
            job.Metadata.DeniedRoles = deniedRoles;
            job.Metadata.AllowedUsers = allowedUsers;
            job.Metadata.DeniedUsers = deniedUsers;
            job.Metadata.IgnoreSecurity = behavior.Settings.IgnoreDNNSecurity.GetValueOrDefault(false);

            // determine the content based on the embedded modules
            string embeddedModulesContent = "";
            foreach (int embeddedModuleId in liveModuleData.EmbeddedModules) {
                ModuleInfo module = moduleService.GetModule(embeddedModuleId, -1, false);
                if (module != null) {
                    try {
                        var controller = DotNetNuke.Framework.Reflection.CreateObject(module.DesktopModule.BusinessControllerClass, module.DesktopModule.BusinessControllerClass);
                        foreach (var searchItemInfo in moduleService.GetSearchItems(module, since))
                            embeddedModulesContent += " " + searchItemInfo.Body;
                    } catch (Exception ex) {
                        loggerService.Error(job.Behavior.Id, () => $"Could not index module: ModuleID: {module.ModuleID}; ModuleTitle: {module.ModuleTitle}.", ex);
                    }
                }
            }

            // finally set the content
            job.Contents = Encoding.UTF8.GetBytes(GetPlainTextContent(liveModuleData.Content) + " " + embeddedModulesContent);
            job.ContentType = "text/plain";

            return job;
        }

        //code copied from DnnModules
        static bool IsModuleSearchTarget(ModuleInfo module, IEnumerable<int> portals,
            Dictionary<int, BehaviorSourceTab> tabs, IEnumerable<int> modules) {
            if (portals.Contains(module.PortalID))
                return true;

            if (modules.Contains(module.ModuleID))
                return true;

            return IsModuleSearchTarget(module, tabs);
        }

        //code copied from DnnModules
        static bool IsModuleSearchTarget(ModuleInfo module, Dictionary<int, BehaviorSourceTab> tabs) {
            if (tabs.ContainsKey(module.TabID)) {
                if (tabs[module.TabID].IncludeModules)
                    return true;
            }

            // check parent tabs
            var tab = TabController.Instance.GetTab(module.TabID, module.PortalID, false);

            while (tab != null) {
                tab = TabController.Instance.GetTab(tab.ParentId, tab.PortalID, false);
                if (tab != null && tabs.ContainsKey(tab.TabID) && tabs[tab.TabID].IncludeChildTabs)
                    return true;
            }

            return false;
        }

        static string GetUrlForTabName(string tabName) {
            // replace non alpha-numeric chars and also skipping -:
            tabName = new Regex("[^a-zA-Z0-9 -]").Replace(tabName, "");
            return tabName.Replace(" ", "-").ToLower();
        }


        internal static IEnumerable<int> GetModuleId(string columnEmbeddedModule) {
            List<int> toReturn = new List<int>();
            if (columnEmbeddedModule.IsNullOrEmpty())
                return toReturn;

            XElement root = XElement.Parse(columnEmbeddedModule);
            foreach (string moduleId in root.Descendants("ModuleId").Select(elem => elem.Value)) {
                int module = -1;
                int.TryParse(moduleId, out module);
                toReturn.Add(module);
            }

            return toReturn;
        }

        internal static string GetPlainTextContent(string content) {
            if (content == null)
                return null;

            string removedTags = Regex.Replace(content, @"&lt;(.|\n)*?&gt;", " ");
            string removedCodedSpaces = removedTags.Replace(@"&amp;nbsp;", " ");
            string cleanedWhiteSpace = Regex.Replace(removedCodedSpaces, @"\s+", " ");
            return cleanedWhiteSpace.Trim();
        }
    }
}
