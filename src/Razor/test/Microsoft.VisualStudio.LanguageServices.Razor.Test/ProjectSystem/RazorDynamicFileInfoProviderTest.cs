﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT license. See License.txt in the project root for license information.

#nullable disable

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Telemetry;
using Microsoft.AspNetCore.Razor.Test.Common.Editor;
using Microsoft.AspNetCore.Razor.Test.Common.ProjectSystem;
using Microsoft.AspNetCore.Razor.Test.Common.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Razor.ProjectSystem;
using Microsoft.CodeAnalysis.Razor.Workspaces;
using Microsoft.VisualStudio.Razor.DynamicFiles;
using Moq;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;
using static Microsoft.VisualStudio.Razor.DynamicFiles.RazorDynamicFileInfoProvider;

namespace Microsoft.VisualStudio.LanguageServices.Razor.ProjectSystem;

public class RazorDynamicFileInfoProviderTest : WorkspaceTestBase
{
    private readonly RazorDynamicFileInfoProvider _provider;
    private readonly TestAccessor _testAccessor;
    private readonly IRazorDocumentServiceProviderFactory _documentServiceFactory;
    private readonly LSPEditorFeatureDetector _editorFeatureDetector;
    private readonly TestProjectSnapshotManager _projectSnapshotManager;
    private readonly ProjectSnapshot _project;
    private readonly ProjectId _projectId;
    private readonly DocumentSnapshot _document1;
    private readonly DocumentSnapshot _document2;
    private readonly IDynamicDocumentContainer _lspDocumentContainer;

    public RazorDynamicFileInfoProviderTest(ITestOutputHelper testOutput)
        : base(testOutput)
    {
        _documentServiceFactory = new RazorDocumentServiceProviderFactory();
        _editorFeatureDetector = Mock.Of<LSPEditorFeatureDetector>(MockBehavior.Strict);
        _projectSnapshotManager = new TestProjectSnapshotManager(ProjectEngineFactoryProvider, new TestProjectSnapshotManagerDispatcher())
        {
            AllowNotifyListeners = true
        };
        var hostProject = new HostProject("C:\\project.csproj", "C:\\obj", RazorConfiguration.Default, rootNamespace: "TestNamespace");
        _projectSnapshotManager.ProjectAdded(hostProject);
        var hostDocument1 = new HostDocument("C:\\document1.razor", "document1.razor", FileKinds.Component);
        _projectSnapshotManager.DocumentAdded(hostProject.Key, hostDocument1, new EmptyTextLoader(hostDocument1.FilePath));
        var hostDocument2 = new HostDocument("C:\\document2.razor", "document2.razor", FileKinds.Component);
        _projectSnapshotManager.DocumentAdded(hostProject.Key, hostDocument2, new EmptyTextLoader(hostDocument2.FilePath));
        _project = _projectSnapshotManager.GetSnapshot(hostProject);
        _document1 = (DocumentSnapshot)_project.GetDocument(hostDocument1.FilePath);
        _document2 = (DocumentSnapshot)_project.GetDocument(hostDocument2.FilePath);

        var languageServerFeatureOptions = new TestLanguageServerFeatureOptions(includeProjectKeyInGeneratedFilePath: true);
        var filePathService = new FilePathService(languageServerFeatureOptions);
        var projectSnapshotManagerAccessor = Mock.Of<IProjectSnapshotManagerAccessor>(a => a.Instance == _projectSnapshotManager, MockBehavior.Strict);

        var projectConfigurationFilePathStore = Mock.Of<ProjectConfigurationFilePathStore>(MockBehavior.Strict);
        var fallbackProjectManager = new FallbackProjectManager(projectConfigurationFilePathStore, languageServerFeatureOptions, projectSnapshotManagerAccessor, WorkspaceProvider, NoOpTelemetryReporter.Instance);

        _provider = new RazorDynamicFileInfoProvider(_documentServiceFactory, _editorFeatureDetector, filePathService, WorkspaceProvider, fallbackProjectManager);
        _testAccessor = _provider.GetTestAccessor();
        _provider.Initialize(_projectSnapshotManager);

        var lspDocumentContainer = new Mock<IDynamicDocumentContainer>(MockBehavior.Strict);
        lspDocumentContainer.Setup(c => c.SetSupportsDiagnostics(true)).Verifiable();
        lspDocumentContainer.Setup(container => container.GetTextLoader(It.IsAny<string>())).Returns(new EmptyTextLoader(string.Empty));
        _lspDocumentContainer = lspDocumentContainer.Object;

        _projectId = ProjectId.CreateNewId();
        Workspace.TryApplyChanges(Workspace.CurrentSolution.AddProject(ProjectInfo.Create(
           _projectId,
           new VersionStamp(),
           "Project",
           "Assembly",
           LanguageNames.CSharp,
           filePath: _project.FilePath).WithCompilationOutputInfo(new CompilationOutputInfo().WithAssemblyPath(Path.Combine(_project.IntermediateOutputPath, "project.dll")))));
    }

    [Fact]
    public void UpdateLSPFileInfo_UnknownFile_Noops()
    {
        // Arrange
        _provider.Updated += (sender, args) => throw new XunitException("Should not have been called.");

        // Act & Assert
        var documentContainer = new Mock<IDynamicDocumentContainer>(MockBehavior.Strict);
        documentContainer.Setup(c => c.SetSupportsDiagnostics(true)).Verifiable();
        _provider.UpdateLSPFileInfo(new Uri("C:/this/does/not/exist.razor"), documentContainer.Object);
    }

    [Fact]
    public async Task GetDynamicFileInfoAsync_IncludesProjectToken()
    {
        // Arrange
        var info = await _testAccessor.GetDynamicFileInfoAsync(_projectId, _document1.FilePath, DisposalToken);

        Assert.Equal(@"C:\document1.razor.fJcYlbdqjCXiWYY1.ide.g.cs", info.FilePath);
    }

    [Fact]
    public async Task UpdateLSPFileInfo_Updates()
    {
        // Arrange
        await _testAccessor.GetDynamicFileInfoAsync(_projectId, _document1.FilePath, DisposalToken);
        var called = false;
        _provider.Updated += (sender, args) => called = true;

        // Act
        _provider.UpdateLSPFileInfo(new Uri(_document1.FilePath), _lspDocumentContainer);

        // Assert
        Assert.True(called);
    }

    [Fact]
    public async Task UpdateLSPFileInfo_ProjectRemoved_Noops()
    {
        // Arrange
        await _testAccessor.GetDynamicFileInfoAsync(_projectId, _document1.FilePath, DisposalToken);
        var called = false;
        _provider.Updated += (sender, args) => called = true;
        _projectSnapshotManager.ProjectRemoved(_project.Key);

        // Act
        _provider.UpdateLSPFileInfo(new Uri(_document1.FilePath), _lspDocumentContainer);

        // Assert
        Assert.False(called);
    }

    [Fact]
    public async Task UpdateLSPFileInfo_SolutionClosing_ClearsAllDocuments()
    {
        // Arrange
        await _testAccessor.GetDynamicFileInfoAsync(_projectId, _document1.FilePath, DisposalToken);
        await _testAccessor.GetDynamicFileInfoAsync(_projectId, _document2.FilePath, DisposalToken);
        _provider.Updated += (sender, documentFilePath) => throw new InvalidOperationException("Should not have been called!");

        _projectSnapshotManager.SolutionClosed();
        _projectSnapshotManager.DocumentClosed(_project.Key, _document1.FilePath, new EmptyTextLoader(string.Empty));

        // Act & Assert
        _provider.UpdateLSPFileInfo(new Uri(_document2.FilePath), _lspDocumentContainer);
        _provider.UpdateLSPFileInfo(new Uri(_document1.FilePath), _lspDocumentContainer);
    }
}
