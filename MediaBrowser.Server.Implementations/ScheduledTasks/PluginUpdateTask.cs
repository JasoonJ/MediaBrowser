﻿using MediaBrowser.Common.ScheduledTasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Updates;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Net;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MediaBrowser.Server.Implementations.ScheduledTasks
{
    /// <summary>
    /// Plugin Update Task
    /// </summary>
    public class PluginUpdateTask : IScheduledTask
    {
        /// <summary>
        /// The _logger
        /// </summary>
        private readonly ILogger _logger;

        private readonly IInstallationManager _installationManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="PluginUpdateTask" /> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="installationManager">The installation manager.</param>
        public PluginUpdateTask(ILogger logger, IInstallationManager installationManager)
        {
            _logger = logger;
            _installationManager = installationManager;
        }

        /// <summary>
        /// Creates the triggers that define when the task will run
        /// </summary>
        /// <returns>IEnumerable{BaseTaskTrigger}.</returns>
        public IEnumerable<ITaskTrigger> GetDefaultTriggers()
        {
            return new ITaskTrigger[] { 
            
                // 1:30am
                new DailyTrigger { TimeOfDay = TimeSpan.FromHours(1.5) },

                new IntervalTrigger { Interval = TimeSpan.FromHours(2)}
            };
        }

        /// <summary>
        /// Update installed plugins
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="progress">The progress.</param>
        /// <returns>Task.</returns>
        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            progress.Report(0);

            var packagesToInstall = (await _installationManager.GetAvailablePluginUpdatesWithoutRegistrationInfo(true, cancellationToken).ConfigureAwait(false)).ToList();

            progress.Report(10);

            var numComplete = 0;

            // Create tasks for each one
            var tasks = packagesToInstall.Select(i => Task.Run(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await _installationManager.InstallPackage(i, new Progress<double>(), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // InstallPackage has it's own inner cancellation token, so only throw this if it's ours
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                }
                catch (HttpException ex)
                {
                    _logger.ErrorException("Error downloading {0}", ex, i.name);
                }
                catch (IOException ex)
                {
                    _logger.ErrorException("Error updating {0}", ex, i.name);
                }

                // Update progress
                lock (progress)
                {
                    numComplete++;
                    double percent = numComplete;
                    percent /= packagesToInstall.Count;

                    progress.Report((90 * percent) + 10);
                }
            }));

            cancellationToken.ThrowIfCancellationRequested();

            await Task.WhenAll(tasks).ConfigureAwait(false);

            progress.Report(100);
        }

        /// <summary>
        /// Gets the name of the task
        /// </summary>
        /// <value>The name.</value>
        public string Name
        {
            get { return "Check for plugin updates"; }
        }

        /// <summary>
        /// Gets the description.
        /// </summary>
        /// <value>The description.</value>
        public string Description
        {
            get { return "Downloads and installs updates for plugins that are configured to update automatically."; }
        }

        public string Category
        {
            get { return "Application"; }
        }
    }
}