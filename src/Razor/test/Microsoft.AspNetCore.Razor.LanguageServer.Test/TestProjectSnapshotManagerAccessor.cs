﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT license. See License.txt in the project root for license information.

using Microsoft.CodeAnalysis.Razor.ProjectSystem;
using Microsoft.CodeAnalysis.Razor.Workspaces;

namespace Microsoft.AspNetCore.Razor.LanguageServer.Test;

internal class TestProjectSnapshotManagerAccessor(ProjectSnapshotManagerBase instance) : IProjectSnapshotManagerAccessor
{
    public ProjectSnapshotManagerBase Instance => instance;
}
