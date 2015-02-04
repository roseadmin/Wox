﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Wox.Core.Exception;
using Wox.Core.UI;
using Wox.Core.UserSettings;
using Wox.Infrastructure;
using Wox.Infrastructure.Http;
using Wox.Infrastructure.Logger;
using Wox.Plugin;

namespace Wox.Core.Plugin
{
    /// <summary>
    /// The entry for managing Wox plugins
    /// </summary>
    public static class PluginManager
    {
        public const string ActionKeywordWildcardSign = "*";
        private static List<PluginMetadata> pluginMetadatas;
        private static List<KeyValuePair<PluginMetadata, IInstantSearch>> instantSearches;


        public static String DebuggerMode { get; private set; }
        public static IPublicAPI API { get; private set; }

        private static List<PluginPair> plugins = new List<PluginPair>();

        /// <summary>
        /// Directories that will hold Wox plugin directory
        /// </summary>
        private static List<string> pluginDirectories = new List<string>();

        private static void SetupPluginDirectories()
        {
            pluginDirectories.Add(PluginDirectory);
            MakesurePluginDirectoriesExist();
        }

        public static string PluginDirectory
        {
            get { return Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Plugins"); }
        }

        private static void MakesurePluginDirectoriesExist()
        {
            foreach (string pluginDirectory in pluginDirectories)
            {
                if (!Directory.Exists(pluginDirectory))
                {
                    try
                    {
                        Directory.CreateDirectory(pluginDirectory);
                    }
                    catch (System.Exception e)
                    {
                        Log.Error(e.Message);
                    }
                }
            }
        }

        /// <summary>
        /// Load and init all Wox plugins
        /// </summary>
        public static void Init(IPublicAPI api)
        {
            if (api == null) throw new WoxCritialException("api is null");

            SetupPluginDirectories();
            API = api;
            plugins.Clear();

            pluginMetadatas = PluginConfig.Parse(pluginDirectories);
            plugins.AddRange(new CSharpPluginLoader().LoadPlugin(pluginMetadatas));
            plugins.AddRange(new JsonRPCPluginLoader<PythonPlugin>().LoadPlugin(pluginMetadatas));

            //load plugin i18n languages
            ResourceMerger.ApplyPluginLanguages();

            foreach (PluginPair pluginPair in plugins)
            {
                PluginPair pair = pluginPair;
                ThreadPool.QueueUserWorkItem(o =>
                {
                    Stopwatch sw = new Stopwatch();
                    sw.Start();
                    pair.Plugin.Init(new PluginInitContext()
                    {
                        CurrentPluginMetadata = pair.Metadata,
                        Proxy = HttpProxy.Instance,
                        API = API
                    });
                    sw.Stop();
                    DebugHelper.WriteLine(string.Format("Plugin init:{0} - {1}", pair.Metadata.Name, sw.ElapsedMilliseconds));
                    pair.InitTime = sw.ElapsedMilliseconds;
                });
            }

            ThreadPool.QueueUserWorkItem(o =>
            {
                LoadInstantSearches();
            });
        }

        public static void InstallPlugin(string path)
        {
            PluginInstaller.Install(path);
        }

        public static void Query(Query query)
        {
            if (!string.IsNullOrEmpty(query.RawQuery.Trim()))
            {
                QueryDispatcher.QueryDispatcher.Dispatch(query);
            }
        }

        public static List<PluginPair> AllPlugins
        {
            get
            {
                return plugins.OrderBy(o => o.Metadata.Name).ToList();
            }
        }

        public static bool IsUserPluginQuery(Query query)
        {
            if (string.IsNullOrEmpty(query.RawQuery)) return false;
            var strings = query.RawQuery.Split(' ');
            if (strings.Length == 1) return false;

            var actionKeyword = strings[0].Trim();
            if (string.IsNullOrEmpty(actionKeyword)) return false;

            return plugins.Any(o => o.Metadata.PluginType == PluginType.User && o.Metadata.ActionKeyword == actionKeyword);
        }

        public static bool IsSystemPlugin(PluginMetadata metadata)
        {
            return metadata.ActionKeyword == ActionKeywordWildcardSign;
        }

        public static void ActivatePluginDebugger(string path)
        {
            DebuggerMode = path;
        }

        public static bool IsInstantSearch(string query)
        {
            return LoadInstantSearches().Any(o => o.Value.IsInstantSearch(query));
        }

        public static bool IsInstantSearchPlugin(PluginMetadata pluginMetadata)
        {
            //todo:to improve performance, any instant search plugin that takes long than 200ms will not consider a instant plugin anymore
            return pluginMetadata.Language.ToUpper() == AllowedLanguage.CSharp &&
                   LoadInstantSearches().Any(o => o.Key.ID == pluginMetadata.ID);
        }

        internal static void ExecutePluginQuery(PluginPair pair, Query query)
        {
            try
            {
                Stopwatch sw = new Stopwatch();
                sw.Start();
                List<Result> results = pair.Plugin.Query(query) ?? new List<Result>();
                results.ForEach(o =>
                {
                    o.PluginID = pair.Metadata.ID;
                });
                sw.Stop();
                DebugHelper.WriteLine(string.Format("Plugin query: {0} - {1}", pair.Metadata.Name, sw.ElapsedMilliseconds));
                pair.QueryCount += 1;
                if (pair.QueryCount == 1)
                {
                    pair.AvgQueryTime = sw.ElapsedMilliseconds;
                }
                else
                {
                    pair.AvgQueryTime = (pair.AvgQueryTime + sw.ElapsedMilliseconds) / 2;
                }
                API.PushResults(query, pair.Metadata, results);
            }
            catch (System.Exception e)
            {
                throw new WoxPluginException(pair.Metadata.Name, e);
            }
        }

        private static List<KeyValuePair<PluginMetadata, IInstantSearch>> LoadInstantSearches()
        {
            if (instantSearches != null) return instantSearches;

            instantSearches = new List<KeyValuePair<PluginMetadata, IInstantSearch>>();
            List<PluginMetadata> CSharpPluginMetadatas = pluginMetadatas.Where(o => o.Language.ToUpper() == AllowedLanguage.CSharp.ToUpper()).ToList();

            foreach (PluginMetadata metadata in CSharpPluginMetadatas)
            {
                try
                {
                    Assembly asm = Assembly.Load(AssemblyName.GetAssemblyName(metadata.ExecuteFilePath));
                    List<Type> types = asm.GetTypes().Where(o => o.IsClass && !o.IsAbstract && o.GetInterfaces().Contains(typeof(IInstantSearch))).ToList();
                    if (types.Count == 0)
                    {
                        continue;
                    }

                    foreach (Type type in types)
                    {
                        instantSearches.Add(new KeyValuePair<PluginMetadata, IInstantSearch>(metadata, Activator.CreateInstance(type) as IInstantSearch));
                    }
                }
                catch (System.Exception e)
                {
                    Log.Error(string.Format("Couldn't load plugin {0}: {1}", metadata.Name, e.Message));
#if (DEBUG)
                    {
                        throw;
                    }
#endif
                }
            }

            return instantSearches;
        }

        /// <summary>
        /// get specified plugin, return null if not found
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public static PluginPair GetPlugin(string id)
        {
            return AllPlugins.FirstOrDefault(o => o.Metadata.ID == id);
        }
    }
}
