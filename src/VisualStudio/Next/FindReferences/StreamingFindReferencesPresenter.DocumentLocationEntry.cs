﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editor;
using Microsoft.CodeAnalysis.Editor.Implementation.IntelliSense.QuickInfo;
using Microsoft.CodeAnalysis.Editor.Implementation.ReferenceHighlighting;
using Microsoft.CodeAnalysis.Editor.Shared.Extensions;
using Microsoft.CodeAnalysis.Editor.Shared.Preview;
using Microsoft.CodeAnalysis.FindReferences;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Text.Shared.Extensions;
using Microsoft.VisualStudio.LanguageServices.Implementation.Extensions;
using Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell.TableControl;
using Microsoft.VisualStudio.Shell.TableManager;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;

namespace Microsoft.VisualStudio.LanguageServices.FindReferences
{
    internal partial class StreamingFindReferencesPresenter
    {
        private class DocumentLocationEntry : Entry
        {
            private readonly TableDataSourceFindReferencesContext _context;
            private readonly VisualStudioWorkspaceImpl _workspace;

            private readonly DocumentLocation _documentLocation;
            private readonly bool _isDefinitionLocation;
            private readonly object _boxedProjectGuid;
            private readonly SourceText _sourceText;
            private readonly TaggedTextAndHighlightSpan _taggedLineParts;

            public DocumentLocationEntry(
                TableDataSourceFindReferencesContext context,
                VisualStudioWorkspaceImpl workspace,
                RoslynDefinitionBucket definitionBucket,
                DocumentLocation documentLocation,
                bool isDefinitionLocation,
                Guid projectGuid,
                SourceText sourceText,
                TaggedTextAndHighlightSpan taggedLineParts)
                : base(definitionBucket)
            {
                _context = context;

                _workspace = workspace;
                _documentLocation = documentLocation;
                _isDefinitionLocation = isDefinitionLocation;
                _boxedProjectGuid = projectGuid;
                _sourceText = sourceText;
                _taggedLineParts = taggedLineParts;
            }

            private StreamingFindReferencesPresenter Presenter => _context.Presenter;

            private Document Document => _documentLocation.Document;
            private TextSpan SourceSpan => _documentLocation.SourceSpan;

            protected override object GetValueWorker(string keyName)
            {
                switch (keyName)
                {
                case StandardTableKeyNames.DocumentName:
                    return Document.FilePath;
                case StandardTableKeyNames.Line:
                    return _sourceText.Lines.GetLinePosition(SourceSpan.Start).Line;
                case StandardTableKeyNames.Column:
                    return _sourceText.Lines.GetLinePosition(SourceSpan.Start).Character;
                case StandardTableKeyNames.ProjectName:
                    return Document.Project.Name;
                case StandardTableKeyNames.ProjectGuid:
                    return _boxedProjectGuid;
                case StandardTableKeyNames.Text:
                    return _sourceText.Lines.GetLineFromPosition(SourceSpan.Start).ToString().Trim();
                }

                return null;
            }

            public override bool TryCreateColumnContent(string columnName, out FrameworkElement content)
            {
                if (columnName == StandardTableColumnDefinitions2.LineText)
                {
                    var inlines = GetHighlightedInlines(Presenter, _taggedLineParts, _isDefinitionLocation);
                    var textBlock = inlines.ToTextBlock(Presenter._typeMap);

                    LazyToolTip.AttachTo(textBlock, CreateDisposableToolTip);

                    content = textBlock;
                    return true;
                }

                content = null;
                return false;
            }

            private static IList<System.Windows.Documents.Inline> GetHighlightedInlines(
                StreamingFindReferencesPresenter presenter,
                TaggedTextAndHighlightSpan taggedTextAndHighlight,
                bool isDefinition)
            {
                var propertyId = isDefinition
                    ? DefinitionHighlightTag.TagId
                    : ReferenceHighlightTag.TagId;

                var properties = presenter._formatMapService
                                          .GetEditorFormatMap("text")
                                          .GetProperties(propertyId);
                var highlightBrush = properties["Background"] as Brush;

                var lineParts = taggedTextAndHighlight.TaggedText;
                var inlines = lineParts.ToInlines(
                    presenter._typeMap,
                    runCallback: (run, taggedText, position) =>
                    {
                        if (highlightBrush != null)
                        {
                            if (position == taggedTextAndHighlight.HighlightSpan.Start)
                            {
                                run.SetValue(
                                    System.Windows.Documents.TextElement.BackgroundProperty,
                                    highlightBrush);
                            }
                        }
                    });

                return inlines;
            }

            private DisposableToolTip CreateDisposableToolTip()
            {
                Presenter.AssertIsForeground();

                // Create a new buffer that we'll show a preview for.  We can't search for an 
                // existing buffer because:
                //   1. the file may not be open.
                //   2. our results may not be in sync with what's actually in the editor.
                var textBuffer = CreateNewBuffer();

                // Create the actual tooltip around the region of that text buffer we want to show.
                var toolTip = new ToolTip
                {
                    Content = CreateToolTipContent(textBuffer),
                    Background = (Brush)Application.Current.Resources[EnvironmentColors.ToolWindowBackgroundBrushKey]
                };

                // Create a preview workspace for this text buffer and open it's corresponding 
                // document.  That way we'll get nice things like classification as well as the
                // reference highlight span.
                var newDocument = Document.WithText(textBuffer.AsTextContainer().CurrentText);
                var workspace = new PreviewWorkspace(newDocument.Project.Solution);
                workspace.OpenDocument(newDocument.Id);

                return new DisposableToolTip(toolTip, workspace);
            }

            private ContentControl CreateToolTipContent(ITextBuffer textBuffer)
            {
                var regionSpan = this.GetRegionSpanForReference();
                var snapshotSpan = textBuffer.CurrentSnapshot.GetSpan(regionSpan);

                var contentType = Presenter._contentTypeRegistryService.GetContentType(
                    IProjectionBufferFactoryServiceExtensions.RoslynPreviewContentType);

                var roleSet = Presenter._textEditorFactoryService.CreateTextViewRoleSet(
                    TextViewRoles.PreviewRole,
                    PredefinedTextViewRoles.Analyzable,
                    PredefinedTextViewRoles.Document,
                    PredefinedTextViewRoles.Editable);

                var content = new ElisionBufferDeferredContent(
                    snapshotSpan,
                    Presenter._projectionBufferFactoryService,
                    Presenter._editorOptionsFactoryService,
                    Presenter._textEditorFactoryService,
                    contentType,
                    roleSet);

                var element = content.Create();
                return element;
            }

            private ITextBuffer CreateNewBuffer()
            {
                Presenter.AssertIsForeground();

                // is it okay to create buffer from threads other than UI thread?
                var contentTypeService = Document.Project.LanguageServices.GetService<IContentTypeLanguageService>();
                var contentType = contentTypeService.GetDefaultContentType();

                var textBuffer = Presenter._textBufferFactoryService.CreateTextBuffer(
                    _sourceText.ToString(), contentType);

                // Create an appropriate highlight span on that buffer for the reference.
                var key = _isDefinitionLocation
                    ? PredefinedPreviewTaggerKeys.DefinitionHighlightingSpansKey
                    : PredefinedPreviewTaggerKeys.ReferenceHighlightingSpansKey;
                textBuffer.Properties.RemoveProperty(key);
                textBuffer.Properties.AddProperty(key, new NormalizedSnapshotSpanCollection(
                    SourceSpan.ToSnapshotSpan(textBuffer.CurrentSnapshot)));

                return textBuffer;
            }



            private Span GetRegionSpanForReference()
            {
                const int AdditionalLineCountPerSide = 3;

                var referenceSpan = this.SourceSpan;
                var lineNumber = _sourceText.Lines.GetLineFromPosition(referenceSpan.Start).LineNumber;
                var firstLineNumber = Math.Max(0, lineNumber - AdditionalLineCountPerSide);
                var lastLineNumber = Math.Min(_sourceText.Lines.Count - 1, lineNumber + AdditionalLineCountPerSide);

                return Span.FromBounds(
                    _sourceText.Lines[firstLineNumber].Start,
                    _sourceText.Lines[lastLineNumber].End);
            }
        }
    }
}
