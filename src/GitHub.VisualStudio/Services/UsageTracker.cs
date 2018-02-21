﻿using System;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using GitHub.Helpers;
using GitHub.Logging;
using GitHub.Models;
using GitHub.Settings;
using Serilog;
using Task = System.Threading.Tasks.Task;

namespace GitHub.Services
{
    public sealed class UsageTracker : IUsageTracker, IDisposable
    {
        static readonly ILogger log = LogManager.ForContext<UsageTracker>();
        readonly IGitHubServiceProvider gitHubServiceProvider;

        bool initialized;
        IMetricsService client;
        IUsageService service;
        IConnectionManager connectionManager;
        IPackageSettings userSettings;
        IVSServices vsservices;
        IDisposable timer;
        bool firstTick = true;

        [ImportingConstructor]
        public UsageTracker(
            IGitHubServiceProvider gitHubServiceProvider,
            IUsageService service)
        {
            this.gitHubServiceProvider = gitHubServiceProvider;
            this.service = service;
            timer = StartTimer();            
        }

        public void Dispose()
        {
            timer?.Dispose();
        }

        public async Task IncrementCounter(Expression<Func<UsageModel, int>> counter)
        {
            await Initialize();
            var data = await service.ReadLocalData();
            var usage = await GetCurrentReport(data);
            // because Model is a struct, it needs to be boxed in order for reflection to work
            object model = usage;
            var property = (MemberExpression)counter.Body;
            var propertyInfo = (PropertyInfo)property.Member;
            log.Verbose("Increment counter {Name}", propertyInfo.Name);
            var value = (int)propertyInfo.GetValue(model);
            propertyInfo.SetValue(model, value + 1);

            //TODO:  Understand changes here
            //usage.Model = (UsageModel)model;
            //await service.WriteLocalData(data);
        }

        IDisposable StartTimer()
        {
            return service.StartTimer(TimerTick, TimeSpan.FromMinutes(3), TimeSpan.FromHours(8));
        }

        async Task Initialize()
        {
            // The services needed by the usage tracker are loaded when they are first needed to
            // improve the startup time of the extension.
            if (!initialized)
            {
                await ThreadingHelper.SwitchToMainThreadAsync();

                client = gitHubServiceProvider.TryGetService<IMetricsService>();
                connectionManager = gitHubServiceProvider.GetService<IConnectionManager>();
                userSettings = gitHubServiceProvider.GetService<IPackageSettings>();
                vsservices = gitHubServiceProvider.GetService<IVSServices>();
                initialized = true;
            }
        }

        async Task TimerTick()
        {
            await Initialize();

            if (client == null || !userSettings.CollectMetrics)
            {
                timer.Dispose();
                timer = null;
                return;
            }

            var data = await service.ReadLocalData();
            var changed = false;

            if (firstTick)
            {
                var current = await GetCurrentReport(data);
                current.NumberOfStartups++;
                changed = true;
                firstTick = false;
            }

            for (var i = data.Reports.Count - 1; i >= 0; --i)
            {
                if (data.Reports[i].Date.Date != DateTimeOffset.Now.Date)
                {
                    try
                    {
                        await client.PostUsage(data.Reports[i]);
                        data.Reports.RemoveAt(i);
                        changed = true;
                    }
                    catch (Exception ex)
                    {
                        log.Error(ex, "Failed to send metrics");
                    }
                }
            }

            if (changed)
            {
                await service.WriteLocalData(data);
            }
        }

        async Task<UsageModel> GetCurrentReport(UsageData data)
        {
            var current = data.Reports.FirstOrDefault(x => x.Date.Date == DateTimeOffset.Now.Date);

            if (current.Equals(UsageModel.Default))
            {
                var guid = await service.GetUserGuid();
                current = UsageModel.Create(guid);
                data.Reports.Add(current);
            }

            current.Lang = CultureInfo.InstalledUICulture.IetfLanguageTag;
            current.CurrentLang = CultureInfo.CurrentCulture.IetfLanguageTag;
            current.AppVersion = AssemblyVersionInformation.Version;
            current.VSVersion = vsservices.VSVersion;

            if (connectionManager.Connections.Any(x => x.HostAddress.IsGitHubDotCom()))
            {
                current.IsGitHubUser |= true;
            }

            if (connectionManager.Connections.Any(x => !x.HostAddress.IsGitHubDotCom()))
            {
                current.IsEnterpriseUser |= true;
            }

            return current;
        }
    }
}
