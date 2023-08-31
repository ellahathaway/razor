﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT license. See License.txt in the project root for license information.

using System.Buffers;
using System.Collections.Immutable;
using System.IO;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.ProjectSystem;
using Microsoft.AspNetCore.Razor.Serialization;
using Microsoft.AspNetCore.Razor.Serialization.Json;
using Microsoft.AspNetCore.Razor.Serialization.MessagePack.Formatters.TagHelpers;
using Microsoft.AspNetCore.Razor.Serialization.MessagePack.Resolvers;
using Microsoft.AspNetCore.Razor.Test.Common;
using Microsoft.AspNetCore.Razor.Utilities;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.AspNetCore.Razor.ProjectEngineHost.Test.Serialization;

public class SerializerValidationTest(ITestOutputHelper testOutput) : TestBase(testOutput)
{
    [Theory]
    [InlineData("Kendo.Mvc.Examples.project.razor.json")]
    [InlineData("project.razor.json")]
    public void VerifyMessagePack_RazorProjectInfo(string resourceName)
    {
        // Arrange
        var resourceBytes = RazorTestResources.GetResourceBytes(resourceName, "Benchmarking");

        // Read tag helpers from JSON
        var originalProjectInfo = DeserializeProjectInfoFromJsonBytes(resourceBytes);

        // Act

        var options = MessagePackSerializerOptions.Standard.WithResolver(
            CompositeResolver.Create(
                RazorProjectInfoResolver.Instance,
                StandardResolver.Instance));

        // Serialize to message pack
        var writer = new ArrayBufferWriter<byte>(initialCapacity: resourceBytes.Length);

        using (var cachingOptions = new CachingOptions(options))
        {
            MessagePackSerializer.Serialize(writer, originalProjectInfo, cachingOptions);
        }

        // Deserialize from message pack
        RazorProjectInfo actualProjectInfo;

        using (var cachingOptions = new CachingOptions(options))
        {
            actualProjectInfo = MessagePackSerializer.Deserialize<RazorProjectInfo>(writer.WrittenMemory, cachingOptions);
        }

        // Assert
        Assert.Equal(originalProjectInfo.SerializedFilePath, actualProjectInfo.SerializedFilePath);
        Assert.Equal(originalProjectInfo.FilePath, actualProjectInfo.FilePath);
        Assert.Equal(originalProjectInfo.Configuration, actualProjectInfo.Configuration);
        Assert.Equal(originalProjectInfo.RootNamespace, actualProjectInfo.RootNamespace);
        Assert.Equal(originalProjectInfo.ProjectWorkspaceState, actualProjectInfo.ProjectWorkspaceState);
        Assert.Equal<DocumentSnapshotHandle>(originalProjectInfo.Documents, actualProjectInfo.Documents);
    }

    [Theory]
    [InlineData("Kendo.Mvc.Examples.taghelpers.json")]
    [InlineData("taghelpers.json")]
    public void VerifyMessagePack_TagHelpers(string resourceName)
    {
        // Arrange
        var resourceBytes = RazorTestResources.GetResourceBytes(resourceName, "Benchmarking");

        // Read tag helpers from JSON
        var originalTagHelpers = ReadTagHelpersFromJsonBytes(resourceBytes);

        // Act

        var options = MessagePackSerializerOptions.Standard.WithResolver(
            CompositeResolver.Create(
                TagHelperDeltaResultResolver.Instance,
                StandardResolver.Instance));

        // Serialize to message pack
        var writer = new ArrayBufferWriter<byte>(initialCapacity: resourceBytes.Length);

        using (var cachingOptions = new CachingOptions(options))
        {
            MessagePackSerializer.Serialize(writer, originalTagHelpers, cachingOptions);
        }

        // Deserialize from message pack
        ImmutableArray<TagHelperDescriptor> actualTagHelpers;

        using (var cachingOptions = new CachingOptions(options))
        {
            actualTagHelpers = MessagePackSerializer.Deserialize<ImmutableArray<TagHelperDescriptor>>(writer.WrittenMemory, cachingOptions);
        }

        // Assert
        Assert.Equal(originalTagHelpers, actualTagHelpers, TagHelperChecksumComparer.Instance);
    }

    [Theory]
    [InlineData("Kendo.Mvc.Examples.project.razor.json")]
    [InlineData("project.razor.json")]
    public void VerifyJson_RazorProjectInfo(string resourceName)
    {
        // Arrange
        var resourceBytes = RazorTestResources.GetResourceBytes(resourceName, "Benchmarking");

        // Read tag helpers from JSON
        var originalProjectInfo = DeserializeProjectInfoFromJsonBytes(resourceBytes);

        // Act

        // Serialize to JSON
        var jsonText = JsonDataConvert.SerializeObject(originalProjectInfo, ObjectWriters.WriteProperties);

        // Deserialize from message pack
        var actualProjectInfo = JsonDataConvert.DeserializeObject(jsonText, ObjectReaders.ReadProjectInfoFromProperties);
        Assert.NotNull(actualProjectInfo);

        // Assert
        Assert.Equal(originalProjectInfo.SerializedFilePath, actualProjectInfo.SerializedFilePath);
        Assert.Equal(originalProjectInfo.FilePath, actualProjectInfo.FilePath);
        Assert.Equal(originalProjectInfo.Configuration, actualProjectInfo.Configuration);
        Assert.Equal(originalProjectInfo.RootNamespace, actualProjectInfo.RootNamespace);
        Assert.Equal(originalProjectInfo.ProjectWorkspaceState, actualProjectInfo.ProjectWorkspaceState);
        Assert.Equal<DocumentSnapshotHandle>(originalProjectInfo.Documents, actualProjectInfo.Documents);
    }

    [Theory]
    [InlineData("Kendo.Mvc.Examples.taghelpers.json", "Benchmarking")]
    [InlineData("taghelpers.json", "Benchmarking")]
    [InlineData("BlazorServerApp.TagHelpers.json")]
    public void VerifyJson_TagHelpers(string resourceName, string? folderName = null)
    {
        // Arrange
        var resourceBytes = RazorTestResources.GetResourceBytes(resourceName, folderName);

        // Read tag helpers from JSON
        var originalTagHelpers = ReadTagHelpersFromJsonBytes(resourceBytes);

        // Act

        // Serialize to JSON
        var jsonText = JsonDataConvert.SerializeData(
            dataWriter => dataWriter.WriteArray(originalTagHelpers, ObjectWriters.Write));

        // Deserialize from JSON
        var actualTagHelpers = JsonDataConvert.DeserializeData(jsonText,
            r => r.ReadImmutableArray(
                static r => ObjectReaders.ReadTagHelper(r, useCache: false)));

        // Assert
        Assert.Equal(originalTagHelpers, actualTagHelpers, TagHelperChecksumComparer.Instance);
    }

    private static RazorProjectInfo DeserializeProjectInfoFromJsonBytes(byte[] resourceBytes)
    {
        using var stream = new MemoryStream(resourceBytes);
        using var streamReader = new StreamReader(stream);

        var originalProjectInfo = JsonDataConvert.DeserializeObject(streamReader, ObjectReaders.ReadProjectInfoFromProperties);
        Assert.NotNull(originalProjectInfo);

        return originalProjectInfo;
    }

    private static ImmutableArray<TagHelperDescriptor> ReadTagHelpersFromJsonBytes(byte[] resourceBytes)
    {
        using var stream = new MemoryStream(resourceBytes);
        using var streamReader = new StreamReader(stream);

        return JsonDataConvert.DeserializeData(streamReader,
            static r => r.ReadImmutableArray(
                static r => ObjectReaders.ReadTagHelper(r, useCache: false)));
    }
}
