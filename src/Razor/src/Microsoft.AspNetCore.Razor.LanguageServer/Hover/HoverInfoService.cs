﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT license. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Legacy;
using Microsoft.AspNetCore.Razor.Language.Syntax;
using Microsoft.AspNetCore.Razor.LanguageServer.Extensions;
using Microsoft.AspNetCore.Razor.LanguageServer.Tooltip;
using Microsoft.CodeAnalysis.Razor.Tooltip;
using Microsoft.CodeAnalysis.Razor.Workspaces.Extensions;
using Microsoft.VisualStudio.Editor.Razor;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Microsoft.VisualStudio.Text.Adornments;
using VisualStudioMarkupKind = Microsoft.VisualStudio.LanguageServer.Protocol.MarkupKind;

namespace Microsoft.AspNetCore.Razor.LanguageServer.Hover;

internal sealed class HoverInfoService(
    LSPTagHelperTooltipFactory lspTagHelperTooltipFactory,
    VSLSPTagHelperTooltipFactory vsLspTagHelperTooltipFactory) : IHoverInfoService
{
    private readonly LSPTagHelperTooltipFactory _lspTagHelperTooltipFactory = lspTagHelperTooltipFactory;
    private readonly VSLSPTagHelperTooltipFactory _vsLspTagHelperTooltipFactory = vsLspTagHelperTooltipFactory;

    public async Task<VSInternalHover?> GetHoverInfoAsync(string documentFilePath, RazorCodeDocument codeDocument, SourceLocation location, VSInternalClientCapabilities clientCapabilities, CancellationToken cancellationToken)
    {
        if (codeDocument is null)
        {
            throw new ArgumentNullException(nameof(codeDocument));
        }

        var syntaxTree = codeDocument.GetSyntaxTree();

        var owner = syntaxTree.Root.FindInnermostNode(location.AbsoluteIndex);
        if (owner is null)
        {
            Debug.Fail("Owner should never be null.");
            return null;
        }

        // For cases where the point in the middle of an attribute,
        // such as <any tes$$t=""></any>
        // the node desired is the *AttributeSyntax
        if (owner.Kind is SyntaxKind.MarkupTextLiteral)
        {
            owner = owner.Parent;
        }

        var position = new Position(location.LineIndex, location.CharacterIndex);
        var tagHelperDocumentContext = codeDocument.GetTagHelperContext();

        // We want to find the parent tag, but looking up ancestors in the tree can find other things,
        // for example when hovering over a start tag, the first ancestor is actually the element it
        // belongs to, or in other words, the exact same tag! To work around this we just make sure we
        // only check nodes that are at a different location in the file.
        var ownerStart = owner.SpanStart;

        if (HtmlFacts.TryGetElementInfo(owner, out var containingTagNameToken, out var attributes, closingForwardSlashOrCloseAngleToken: out _) &&
            containingTagNameToken.Span.IntersectsWith(location.AbsoluteIndex))
        {
            if (owner is MarkupStartTagSyntax or MarkupEndTagSyntax &&
                containingTagNameToken.Content.Equals(SyntaxConstants.TextTagName, StringComparison.OrdinalIgnoreCase))
            {
                // It's possible for there to be a <Text> component that is in scope, and would be found by the GetTagHelperBinding
                // call below, but a text tag, regardless of casing, inside C# code, is always just a text tag, not a component.
                return null;
            }

            // Hovering over HTML tag name
            var ancestors = owner.Ancestors().Where(n => n.SpanStart != ownerStart);
            var (parentTag, parentIsTagHelper) = TagHelperFacts.GetNearestAncestorTagInfo(ancestors);
            var stringifiedAttributes = TagHelperFacts.StringifyAttributes(attributes);
            var binding = TagHelperFacts.GetTagHelperBinding(
                tagHelperDocumentContext,
                containingTagNameToken.Content,
                stringifiedAttributes,
                parentTag: parentTag,
                parentIsTagHelper: parentIsTagHelper);

            if (binding is null)
            {
                // No matching tagHelpers, it's just HTML
                return null;
            }
            else if (binding.IsAttributeMatch)
            {
                // Hovered over a HTML tag name but the binding matches an attribute
                return null;
            }
            else
            {
                Debug.Assert(binding.Descriptors.Any());

                var range = containingTagNameToken.GetRange(codeDocument.Source);

                var result = await ElementInfoToHoverAsync(documentFilePath, binding.Descriptors, range, clientCapabilities, cancellationToken).ConfigureAwait(false);
                return result;
            }
        }

        if (HtmlFacts.TryGetAttributeInfo(owner, out containingTagNameToken, out _, out var selectedAttributeName, out var selectedAttributeNameLocation, out attributes) &&
            selectedAttributeNameLocation?.IntersectsWith(location.AbsoluteIndex) == true)
        {
            // When finding parents for attributes, we make sure to find the parent of the containing tag, otherwise these methods
            // would return the parent of the attribute, which is not helpful, as its just going to be the containing element
            var containingTag = containingTagNameToken.Parent;
            var ancestors = containingTag.Ancestors().Where<SyntaxNode>(n => n.SpanStart != containingTag.SpanStart);
            var (parentTag, parentIsTagHelper) = TagHelperFacts.GetNearestAncestorTagInfo(ancestors);

            // Hovering over HTML attribute name
            var stringifiedAttributes = TagHelperFacts.StringifyAttributes(attributes);

            var binding = TagHelperFacts.GetTagHelperBinding(
                tagHelperDocumentContext,
                containingTagNameToken.Content,
                stringifiedAttributes,
                parentTag: parentTag,
                parentIsTagHelper: parentIsTagHelper);

            if (binding is null)
            {
                // No matching TagHelpers, it's just HTML
                return null;
            }
            else
            {
                Debug.Assert(binding.Descriptors.Any());
                var tagHelperAttributes = TagHelperFacts.GetBoundTagHelperAttributes(
                    tagHelperDocumentContext,
                    selectedAttributeName.AssumeNotNull(),
                    binding);

                // Grab the first attribute that we find that intersects with this location. That way if there are multiple attributes side-by-side aka hovering over:
                //      <input checked| minimized />
                // Then we take the left most attribute (attributes are returned in source order).
                var attribute = attributes.First(a => a.Span.IntersectsWith(location.AbsoluteIndex));
                if (attribute is MarkupTagHelperAttributeSyntax thAttributeSyntax)
                {
                    attribute = thAttributeSyntax.Name;
                }
                else if (attribute is MarkupMinimizedTagHelperAttributeSyntax thMinimizedAttribute)
                {
                    attribute = thMinimizedAttribute.Name;
                }
                else if (attribute is MarkupTagHelperDirectiveAttributeSyntax directiveAttribute)
                {
                    attribute = directiveAttribute.Name;
                }
                else if (attribute is MarkupMinimizedTagHelperDirectiveAttributeSyntax miniDirectiveAttribute)
                {
                    attribute = miniDirectiveAttribute;
                }

                var attributeName = attribute.GetContent();
                var range = attribute.GetRange(codeDocument.Source);

                // Include the @ in the range
                switch (attribute.Parent.Kind)
                {
                    case SyntaxKind.MarkupTagHelperDirectiveAttribute:
                        var directiveAttribute = (MarkupTagHelperDirectiveAttributeSyntax)attribute.Parent;
                        range.Start.Character -= directiveAttribute.Transition.FullWidth;
                        attributeName = "@" + attributeName;
                        break;
                    case SyntaxKind.MarkupMinimizedTagHelperDirectiveAttribute:
                        var minimizedAttribute = (MarkupMinimizedTagHelperDirectiveAttributeSyntax)containingTag;
                        range.Start.Character -= minimizedAttribute.Transition.FullWidth;
                        attributeName = "@" + attributeName;
                        break;
                }

                var attributeHoverModel = AttributeInfoToHover(tagHelperAttributes, range, attributeName, clientCapabilities);

                return attributeHoverModel;
            }
        }

        return null;
    }

    private VSInternalHover? AttributeInfoToHover(ImmutableArray<BoundAttributeDescriptor> boundAttributes, Range range, string attributeName, VSInternalClientCapabilities clientCapabilities)
    {
        var descriptionInfos = boundAttributes.SelectAsArray(boundAttribute =>
        {
            var isIndexer = TagHelperMatchingConventions.SatisfiesBoundAttributeIndexer(attributeName.AsSpan(), boundAttribute);
            return BoundAttributeDescriptionInfo.From(boundAttribute, isIndexer);
        });

        var attrDescriptionInfo = new AggregateBoundAttributeDescription(descriptionInfos);

        var isVSClient = clientCapabilities.SupportsVisualStudioExtensions;
        if (isVSClient && _vsLspTagHelperTooltipFactory.TryCreateTooltip(attrDescriptionInfo, out ContainerElement? classifiedTextElement))
        {
            var vsHover = new VSInternalHover
            {
                Contents = Array.Empty<SumType<string, MarkedString>>(),
                Range = range,
                RawContent = classifiedTextElement,
            };

            return vsHover;
        }
        else
        {
            var hoverContentFormat = GetHoverContentFormat(clientCapabilities);

            if (!_lspTagHelperTooltipFactory.TryCreateTooltip(attrDescriptionInfo, hoverContentFormat, out var vsMarkupContent))
            {
                return null;
            }

            var markupContent = new MarkupContent()
            {
                Value = vsMarkupContent.Value,
                Kind = vsMarkupContent.Kind,
            };

            var hover = new VSInternalHover
            {
                Contents = markupContent,
                Range = range,
            };

            return hover;
        }
    }

    private async Task<VSInternalHover?> ElementInfoToHoverAsync(string documentFilePath, IEnumerable<TagHelperDescriptor> descriptors, Range range, VSInternalClientCapabilities clientCapabilities, CancellationToken cancellationToken)
    {
        var descriptionInfos = descriptors.SelectAsArray(BoundElementDescriptionInfo.From);
        var elementDescriptionInfo = new AggregateBoundElementDescription(descriptionInfos);

        var isVSClient = clientCapabilities.SupportsVisualStudioExtensions;
        if (isVSClient)
        {
            var classifiedTextElement = await _vsLspTagHelperTooltipFactory.TryCreateTooltipContainerAsync(documentFilePath, elementDescriptionInfo, cancellationToken).ConfigureAwait(false);
            if (classifiedTextElement is not null)
            {
                var vsHover = new VSInternalHover
                {
                    Contents = Array.Empty<SumType<string, MarkedString>>(),
                    Range = range,
                    RawContent = classifiedTextElement,
                };

                return vsHover;
            }
        }

        var hoverContentFormat = GetHoverContentFormat(clientCapabilities);

        var vsMarkupContent = await _lspTagHelperTooltipFactory.TryCreateTooltipAsync(documentFilePath, elementDescriptionInfo, hoverContentFormat, cancellationToken).ConfigureAwait(false);
        if (vsMarkupContent is null)
        {
            return null;
        }

        var markupContent = new MarkupContent()
        {
            Value = vsMarkupContent.Value,
            Kind = vsMarkupContent.Kind,
        };

        var hover = new VSInternalHover
        {
            Contents = markupContent,
            Range = range
        };

        return hover;
    }

    private static VisualStudioMarkupKind GetHoverContentFormat(ClientCapabilities clientCapabilities)
    {
        var hoverContentFormat = clientCapabilities.TextDocument?.Hover?.ContentFormat;
        var hoverKind = hoverContentFormat?.Contains(VisualStudioMarkupKind.Markdown) == true ? VisualStudioMarkupKind.Markdown : VisualStudioMarkupKind.PlainText;
        return hoverKind;
    }
}
