﻿// -----------------------------------------------------------------------
//  <copyright file="PerformanceCountersManager.cs" company="Hibernating Rhinos LTD">
//      Copyright (c) Hibernating Rhinos LTD. All rights reserved.
//  </copyright>
// -----------------------------------------------------------------------
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Threading.Tasks;
using Raven.Abstractions.Logging;

namespace Raven.Database.Util
{
    public class PerformanceCountersManager : IDisposable
    {
        private static readonly ILog log = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// The performance counter category name for RavenDB counters.
        /// </summary>
        public const string CategoryName = "RavenDB 2.0";

       // REVIEW: Is this long enough to determine if it *would* hang forever?
        private static readonly TimeSpan PerformanceCounterWaitTimeout = TimeSpan.FromSeconds(2);

        public PerformanceCountersManager()
        {
            InitNoOpCounters();
        }

        [PerformanceCounter(Name = "# docs / sec", CounterType = PerformanceCounterType.RateOfCountsPerSecond32, Description = "Number of documents per second.")]
        public IPerformanceCounter DocsPerSecond { get; private set; }

        [PerformanceCounter(Name = "# docs indexed / sec", CounterType = PerformanceCounterType.RateOfCountsPerSecond32, Description = "Number of documents indexed per second.")]
        public IPerformanceCounter IndexedPerSecond { get; private set; }

        [PerformanceCounter(Name = "# docs reduced / sec", CounterType = PerformanceCounterType.RateOfCountsPerSecond32, Description = "Number of documents reduced per second.")]
        public IPerformanceCounter ReducedPerSecond { get; private set; }

        [PerformanceCounter(Name = "# req / sec", CounterType = PerformanceCounterType.RateOfCountsPerSecond32, Description = "Number of requests per second.")]
        public IPerformanceCounter RequestsPerSecond { get; private set; }

        [PerformanceCounter(Name = "# of concurrent requests", CounterType = PerformanceCounterType.NumberOfItems32, Description = "Number of concurrent requests.")]
        public IPerformanceCounter ConcurrentRequests { get; private set; }

        public void Setup(string name)
        {
            try
            {
                InstallCounters(name);
                SetCounterProperties(GetPerformanceCounterName(name));
            }
            catch (UnauthorizedAccessException e)
            {
                log.WarnException("Could not setup performance counters properly because of access permissions, perf counters will not be used", e);
            }
            catch (SecurityException e)
            {
                log.WarnException("Could not setup performance counters properly because of access permissions, perf counters will not be used", e);
            }
        }

        private void InstallCounters(string name)
        {
            if (IsValidCategory(name) == false)
            {
                var counterCreationData = CounterProperties.Select(p =>
                {
                    var attribute = GetPerformanceCounterAttribute(p);
                    return new CounterCreationData(attribute.Name, attribute.Description, attribute.CounterType);
                }).ToArray();

                var createDataCollection = new CounterCreationDataCollection(counterCreationData);

                PerformanceCounterCategory.Create(CategoryName, "RavenDB Performance Counters", PerformanceCounterCategoryType.MultiInstance, createDataCollection);
                PerformanceCounter.CloseSharedResources(); // http://blog.dezfowler.com/2007/08/net-performance-counter-problems.html
            }
        }

        private void SetCounterProperties(string instanceName)
        {
            var canLoadCounters = true;

            foreach (var property in CounterProperties)
            {
                PerformanceCounterAttribute attribute = GetPerformanceCounterAttribute(property);
                if (attribute == null)
                    continue;

                IPerformanceCounter counter = null;
                if (canLoadCounters)
                {
                    counter = LoadCounter(CategoryName, attribute.Name, instanceName, isReadOnly: false);

                    if (counter == null)
                    {
                        // We failed to load the counter so skip the rest
                        canLoadCounters = false;
                    }
                }

                counter = counter ?? NoOpCounter;

                // Initialize the counter sample
                counter.NextValue();

                property.SetValue(this, counter, null);
            }
        }

        private void InitNoOpCounters()
        {
            // Set all the counter properties to no-op by default.
            // These will get reset to real counters when/if the Initialize method is called.
            foreach (var property in CounterProperties)
            {
                property.SetValue(this, new NoOpPerformanceCounter(), null);
            }
        }

        private readonly static PropertyInfo[] CounterProperties = GetCounterPropertyInfo();
        private readonly static IPerformanceCounter NoOpCounter = new NoOpPerformanceCounter();

        internal static PropertyInfo[] GetCounterPropertyInfo()
        {
            return typeof (PerformanceCountersManager)
                .GetProperties()
                .Where(p => p.PropertyType == typeof (IPerformanceCounter))
                .ToArray();
        }

        internal static PerformanceCounterAttribute GetPerformanceCounterAttribute(PropertyInfo property)
        {
            return property.GetCustomAttributes(typeof(PerformanceCounterAttribute), false)
                    .Cast<PerformanceCounterAttribute>()
                    .SingleOrDefault();
        }

        private IPerformanceCounter LoadCounter(string categoryName, string counterName, string instanceName, bool isReadOnly)
        {
            // See http://msdn.microsoft.com/en-us/library/356cx381.aspx for the list of exceptions
            // and when they are thrown. 
            try
            {
                return new PerformanceCounterWrapper(new PerformanceCounter(categoryName, counterName, instanceName, isReadOnly));
            }
            catch (InvalidOperationException ex)
            {
                log.Warn("Performance counter failed to load: " + ex.GetBaseException());
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                log.Warn("Performance counter failed to load: " + ex.GetBaseException());
                return null;
            }
            catch (Win32Exception ex)
            {
                log.Warn("Performance counter failed to load: " + ex.GetBaseException());
                return null;
            }
            catch (PlatformNotSupportedException ex)
            {
                log.Warn("Performance counter failed to load: " + ex.GetBaseException());
                return null;
            }
        }

        private bool IsValidCategory(string instanceName)
        {
            if (PerformanceCounterExistsSlow(instanceName))
                return false;

            foreach (var counter in CounterProperties)
            {
                try
                {
                    new PerformanceCounter(CategoryName, counter.Name, instanceName, readOnly: true).Close();
                }
                catch (InvalidOperationException)
                {
                    PerformanceCounterCategory.Delete(CategoryName);
                    return false;
                }
            }
            return true;
        }

        private string GetPerformanceCounterName(string name)
        {
            // dealing with names who are very long (there is a limit of 80 chars for counter name)
            return name.Length > 70 ? name.Remove(70) : name;
        }

        private void UnloadCounters()
        {
            foreach (var property in CounterProperties)
            {
                var counter = property.GetValue(this, null) as IPerformanceCounter;
                counter.Close();
                counter.RemoveInstance();
            }
        }

        public void Dispose()
        {
            UnloadCounters();
        }

        private static bool PerformanceCounterExistsSlow(string counterName)
        {
            // Fire this off on an separate thread
            var task = Task.Factory.StartNew(() => PerformanceCounterExists(counterName));

            if (!task.Wait(PerformanceCounterWaitTimeout))
            {
                // If it timed out then throw
                throw new OperationCanceledException();
            }

            return task.Result;
        }

        private static bool PerformanceCounterExists(string counterName)
        {
            return PerformanceCounterCategory.Exists(CategoryName) &&
                   PerformanceCounterCategory.CounterExists(counterName, CategoryName);
        }
    }
}