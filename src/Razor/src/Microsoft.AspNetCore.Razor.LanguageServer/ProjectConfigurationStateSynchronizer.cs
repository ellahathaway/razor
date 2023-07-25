﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT license. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.LanguageServer.Common;
using Microsoft.AspNetCore.Razor.LanguageServer.ProjectSystem;
using Microsoft.AspNetCore.Razor.ProjectSystem;
using Microsoft.AspNetCore.Razor.Serialization;
using Microsoft.AspNetCore.Razor.Utilities;
using Microsoft.CodeAnalysis.Razor;
using Microsoft.CodeAnalysis.Razor.ProjectSystem;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Razor.LanguageServer;

internal class ProjectConfigurationStateSynchronizer : IProjectConfigurationFileChangeListener
{
    private readonly ProjectSnapshotManagerDispatcher _projectSnapshotManagerDispatcher;
    private readonly RazorProjectService _projectService;
    private readonly ILogger _logger;
    private readonly Dictionary<string, ProjectKey> _configurationToProjectMap;
    internal readonly Dictionary<ProjectKey, DelayedProjectInfo> ProjectInfoMap;

    public ProjectConfigurationStateSynchronizer(
        ProjectSnapshotManagerDispatcher projectSnapshotManagerDispatcher,
        RazorProjectService projectService,
        ILoggerFactory loggerFactory)
    {
        _projectSnapshotManagerDispatcher = projectSnapshotManagerDispatcher;
        _projectService = projectService;
        _logger = loggerFactory.CreateLogger<ProjectConfigurationStateSynchronizer>();
        _configurationToProjectMap = new Dictionary<string, ProjectKey>(FilePathComparer.Instance);
        ProjectInfoMap = new Dictionary<ProjectKey, DelayedProjectInfo>();
    }

    internal int EnqueueDelay { get; set; } = 250;

    public void ProjectConfigurationFileChanged(ProjectConfigurationFileChangeEventArgs args)
    {
        if (args is null)
        {
            throw new ArgumentNullException(nameof(args));
        }

        _projectSnapshotManagerDispatcher.AssertDispatcherThread();

        switch (args.Kind)
        {
            case RazorFileChangeKind.Changed:
                {
                    var configurationFilePath = FilePathNormalizer.Normalize(args.ConfigurationFilePath);
                    if (!args.TryDeserialize(out var projectRazorJson))
                    {
                        if (!_configurationToProjectMap.TryGetValue(configurationFilePath, out var lastAssociatedProjectKey))
                        {
                            // Could not resolve an associated project file, noop.
                            _logger.LogWarning("Failed to deserialize configuration file after change for an unknown project. Configuration file path: '{0}'", configurationFilePath);
                            return;
                        }
                        else
                        {
                            _logger.LogWarning("Failed to deserialize configuration file after change for project '{0}': '{1}'", lastAssociatedProjectKey.Id, configurationFilePath);
                        }

                        // We found the last associated project file for the configuration file. Reset the project since we can't
                        // accurately determine its configurations.

                        EnqueueUpdateProject(lastAssociatedProjectKey, projectRazorJson: null);
                        return;
                    }

                    if (!_configurationToProjectMap.TryGetValue(configurationFilePath, out var associatedProjectKey))
                    {
                        _logger.LogWarning("Found no project key for configuration file. Assuming new project. Configuration file path: '{0}'", configurationFilePath);

                        AddProject(configurationFilePath, projectRazorJson);
                        return;
                    }

                    _logger.LogInformation("Project configuration file changed for project '{0}': '{1}'", associatedProjectKey.Id, configurationFilePath);

                    EnqueueUpdateProject(associatedProjectKey, projectRazorJson);
                    break;
                }
            case RazorFileChangeKind.Added:
                {
                    var configurationFilePath = FilePathNormalizer.Normalize(args.ConfigurationFilePath);
                    if (!args.TryDeserialize(out var projectRazorJson))
                    {
                        // Given that this is the first time we're seeing this configuration file if we can't deserialize it
                        // then we have to noop.
                        _logger.LogWarning("Failed to deserialize configuration file on configuration added event. Configuration file path: '{0}'", configurationFilePath);
                        return;
                    }

                    AddProject(configurationFilePath, projectRazorJson);
                    break;
                }
            case RazorFileChangeKind.Removed:
                {
                    var configurationFilePath = FilePathNormalizer.Normalize(args.ConfigurationFilePath);
                    if (!_configurationToProjectMap.TryGetValue(configurationFilePath, out var projectFilePath))
                    {
                        // Failed to deserialize the initial project configuration file on add so we can't remove the configuration file because it doesn't exist in the list.
                        _logger.LogWarning("Failed to resolve associated project on configuration removed event. Configuration file path: '{0}'", configurationFilePath);
                        return;
                    }

                    _configurationToProjectMap.Remove(configurationFilePath);

                    _logger.LogInformation("Project configuration file removed for project '{0}': '{1}'", projectFilePath, configurationFilePath);

                    EnqueueUpdateProject(projectFilePath, projectRazorJson: null);
                    break;
                }
        }

        void AddProject(string configurationFilePath, ProjectRazorJson projectRazorJson)
        {
            var projectFilePath = FilePathNormalizer.Normalize(projectRazorJson.FilePath);
            var intermediateOutputPath = Path.GetDirectoryName(configurationFilePath).AssumeNotNull();
            var rootNamespace = projectRazorJson.RootNamespace;

            var projectKey = _projectService.AddProject(projectFilePath, intermediateOutputPath, rootNamespace);
            _configurationToProjectMap[configurationFilePath] = projectKey;

            _logger.LogInformation("Project configuration file added for project '{0}': '{1}'", projectFilePath, configurationFilePath);
            EnqueueUpdateProject(projectKey, projectRazorJson);
        }

        void UpdateProject(ProjectKey projectKey, ProjectRazorJson? projectRazorJson)
        {
            if (projectRazorJson is null)
            {
                ResetProject(projectKey);
                return;
            }

            var projectWorkspaceState = projectRazorJson.ProjectWorkspaceState ?? ProjectWorkspaceState.Default;
            var documents = projectRazorJson.Documents;
            _projectService.UpdateProject(
                projectKey,
                projectRazorJson.Configuration,
                projectRazorJson.RootNamespace,
                projectWorkspaceState,
                documents);
        }

        async Task UpdateAfterDelayAsync(ProjectKey projectKey)
        {
            await Task.Delay(EnqueueDelay).ConfigureAwait(true);

            var delayedProjectInfo = ProjectInfoMap[projectKey];
            UpdateProject(projectKey, delayedProjectInfo.ProjectRazorJson);
        }

        void EnqueueUpdateProject(ProjectKey projectKey, ProjectRazorJson? projectRazorJson)
        {
            if (!ProjectInfoMap.ContainsKey(projectKey))
            {
                ProjectInfoMap[projectKey] = new DelayedProjectInfo();
            }

            var delayedProjectInfo = ProjectInfoMap[projectKey];
            delayedProjectInfo.ProjectRazorJson = projectRazorJson;

            if (delayedProjectInfo.ProjectUpdateTask is null || delayedProjectInfo.ProjectUpdateTask.IsCompleted)
            {
                delayedProjectInfo.ProjectUpdateTask = UpdateAfterDelayAsync(projectKey);
            }
        }

        void ResetProject(ProjectKey projectKey)
        {
            _projectService.UpdateProject(
                projectKey,
                configuration: null,
                rootNamespace: null,
                ProjectWorkspaceState.Default,
                Array.Empty<DocumentSnapshotHandle>());
        }
    }

    internal class DelayedProjectInfo
    {
        public Task? ProjectUpdateTask { get; set; }

        public ProjectRazorJson? ProjectRazorJson { get; set; }
    }
}
