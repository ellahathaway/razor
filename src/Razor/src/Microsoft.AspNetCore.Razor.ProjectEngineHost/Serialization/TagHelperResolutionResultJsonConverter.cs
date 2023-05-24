﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT license. See License.txt in the project root for license information.

namespace Microsoft.AspNetCore.Razor.ProjectEngineHost.Serialization;

internal partial class TagHelperResolutionResultJsonConverter : ObjectJsonConverter<TagHelperResolutionResult>
{
    public static readonly TagHelperResolutionResultJsonConverter Instance = new();

    private TagHelperResolutionResultJsonConverter()
    {
    }

    protected override TagHelperResolutionResult ReadFromProperties(JsonDataReader reader)
    {
        var descriptors = reader.ReadArrayOrEmpty(nameof(TagHelperResolutionResult.Descriptors),
            static r => ObjectReaders.ReadTagHelper(r, useCache: true));

        return new(descriptors);
    }

    protected override void WriteProperties(JsonDataWriter writer, TagHelperResolutionResult value)
    {
        writer.WriteArrayIfNotNullOrEmpty(nameof(value.Descriptors), value.Descriptors, ObjectWriters.Write);
    }
}
