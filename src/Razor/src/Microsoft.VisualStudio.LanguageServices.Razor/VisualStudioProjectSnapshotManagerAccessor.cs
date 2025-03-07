﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT license. See License.txt in the project root for license information.

using System.ComponentModel.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Razor;
using Microsoft.CodeAnalysis.Razor.ProjectSystem;
using Microsoft.CodeAnalysis.Razor.Workspaces;
using Microsoft.VisualStudio.Threading;

namespace Microsoft.VisualStudio.LanguageServices.Razor;

[System.Composition.Shared]
[Export(typeof(IProjectSnapshotManagerAccessor))]
[method: ImportingConstructor]
internal sealed class VisualStudioProjectSnapshotManagerAccessor(
    [Import(typeof(VisualStudioWorkspace))] Workspace workspace,
    ProjectSnapshotManagerDispatcher dispatcher,
    JoinableTaskContext joinableTaskContext)
    : IProjectSnapshotManagerAccessor
{
    private readonly Workspace _workspace = workspace;
    private readonly ProjectSnapshotManagerDispatcher _dispatcher = dispatcher;
    private readonly JoinableTaskFactory _jtf = joinableTaskContext.Factory;

    private ProjectSnapshotManagerBase? _projectManager;

    public ProjectSnapshotManagerBase Instance
    {
        get
        {
            if (_projectManager is { } projectManager)
            {
                return projectManager;
            }

            if (_dispatcher.IsDispatcherThread)
            {
                return _projectManager ??= Create();
            }

            // The JTF.Run isn't great, but it should go away with IProjectSnapshotManagerAccessor.
            // ProjectSnapshotManager must be created on the dispatcher scheduler because it calls the
            // Initialize() method of any IProjectSnapshotChangeTrigger its created with.

            return _jtf.Run(async () =>
            {
                await _dispatcher.DispatcherScheduler;

                return _projectManager ??= Create();
            });

            ProjectSnapshotManagerBase Create()
            {
                _dispatcher.AssertDispatcherThread();

                return (ProjectSnapshotManagerBase)_workspace.Services
                    .GetLanguageServices(RazorLanguage.Name)
                    .GetRequiredService<IProjectSnapshotManager>();
            }
        }
    }
}
