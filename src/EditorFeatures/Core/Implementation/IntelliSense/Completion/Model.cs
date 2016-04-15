﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Text;
using Microsoft.VisualStudio.Text;
using Roslyn.Utilities;
using Microsoft.CodeAnalysis.Editor.Shared.Options;

namespace Microsoft.CodeAnalysis.Editor.Implementation.IntelliSense.Completion
{
    internal class Model
    {
        private readonly DisconnectedBufferGraph _disconnectedBufferGraph;
        public ITextSnapshot TriggerSnapshot { get { return _disconnectedBufferGraph.SubjectBufferSnapshot; } }

        public IList<CompletionItem> TotalItems { get; }
        public IList<CompletionItem> FilteredItems { get; }

        public ImmutableArray<CompletionItemFilter> CompletionItemFilters { get; }
        public ImmutableDictionary<CompletionItemFilter, bool> FilterState { get; }
        public IReadOnlyDictionary<CompletionItem, string> CompletionItemToFilterText { get; }

        public CompletionItem SelectedItem { get; }
        public bool IsHardSelection { get; }
        public bool IsUnique { get; }

        // The CompletionItem the model will use to represent selecting
        // and interacting with the builder. This CompletionItem includes
        // the language specific default tracking span for completion
        // as determined by CompletionUtilities for that language.
        // All models always have a DefaultBuilder set.
        public CompletionItem DefaultBuilder { get; }

        // The builder, if any, provided by the model's completionproviders.
        public CompletionItem Builder { get; }
        public CompletionTriggerInfo TriggerInfo { get; }
        public bool UseSuggestionCompletionMode { get; }

        // When committing a completion item, the span replaced ends at this point.
        public ITrackingPoint CommitTrackingSpanEndPoint { get; }

        public bool DismissIfEmpty { get; }

        private Model(
            DisconnectedBufferGraph disconnectedBufferGraph,
            IList<CompletionItem> totalItems,
            IList<CompletionItem> filteredItems,
            CompletionItem selectedItem,
            ImmutableArray<CompletionItemFilter> completionItemFilters,
            ImmutableDictionary<CompletionItemFilter, bool> filterState,
            IReadOnlyDictionary<CompletionItem, string> completionItemToFilterText,
            bool isHardSelection,
            bool isUnique,
            bool useSuggestionCompletionMode,
            CompletionItem builder,
            CompletionItem defaultBuilder,
            CompletionTriggerInfo triggerInfo,
            ITrackingPoint commitSpanEndPoint,
            bool dismissIfEmpty)
        {
            Contract.ThrowIfFalse(totalItems.Count != 0, "Must have at least one item.");

            _disconnectedBufferGraph = disconnectedBufferGraph;
            this.TotalItems = totalItems;
            this.FilteredItems = filteredItems;
            this.FilterState = filterState;
            this.SelectedItem = selectedItem;
            this.CompletionItemFilters = completionItemFilters;
            this.CompletionItemToFilterText = completionItemToFilterText;
            this.IsHardSelection = isHardSelection;
            this.IsUnique = isUnique;
            this.UseSuggestionCompletionMode = useSuggestionCompletionMode;
            this.Builder = builder;
            this.DefaultBuilder = defaultBuilder;
            this.TriggerInfo = triggerInfo;
            this.CommitTrackingSpanEndPoint = commitSpanEndPoint;
            this.DismissIfEmpty = dismissIfEmpty;
        }

        public static Model CreateModel(
            DisconnectedBufferGraph disconnectedBufferGraph,
            TextSpan defaultTrackingSpanInSubjectBuffer,
            ImmutableArray<CompletionItem> totalItems,
            CompletionItem selectedItem,
            bool isHardSelection,
            bool isUnique,
            bool useSuggestionCompletionMode,
            CompletionItem builder,
            CompletionTriggerInfo triggerInfo,
            ICompletionService completionService,
            Workspace workspace)
        {
            var updatedTotalItems = totalItems;
            CompletionItem updatedSelectedItem = selectedItem;
            CompletionItem updatedBuilder = builder;
            CompletionItem updatedDefaultBuilder = GetDefaultBuilder(defaultTrackingSpanInSubjectBuffer);

            // Get the set of actual filters used by all the completion items 
            // that are in the list.
            var actualFiltersSeen = new HashSet<CompletionItemFilter>();
            foreach (var item in totalItems)
            {
                foreach (var filter in item.Filters)
                {
                    actualFiltersSeen.Add(filter);
                }
            }

            // The set of filters we'll want to show the user are the filters that are actually
            // used by our completion items.  i.e. there's no reason to show the "field" filter
            // if none of completion items is actually a field.
            var actualItemFilters = CompletionItemFilter.AllFilters.Where(actualFiltersSeen.Contains)
                                                                   .ToImmutableArray();

            // By default we do not filter anything out.
            ImmutableDictionary<CompletionItemFilter, bool> filterState = null;
             
            if (completionService != null &&
                workspace != null &&
                workspace.Kind != WorkspaceKind.Interactive && // TODO (https://github.com/dotnet/roslyn/issues/5107): support in interactive
                workspace.Options.GetOption(InternalFeatureOnOffOptions.Snippets) &&
                triggerInfo.TriggerReason != CompletionTriggerReason.Snippets)
            {
                // In order to add snippet expansion notes to completion item descriptions, update
                // all of the provided CompletionItems to DescriptionModifyingCompletionItem which will proxy
                // requests to the original completion items and add the snippet expansion note to
                // the description if necessary. We won't do this if the list was triggered to show
                // snippet shortcuts.

                var updatedTotalItemsBuilder = ImmutableArray.CreateBuilder<CompletionItem>();
                updatedSelectedItem = null;

                foreach (var item in totalItems)
                {
                    var updatedItem = new DescriptionModifyingCompletionItem(item, completionService, workspace);
                    updatedTotalItemsBuilder.Add(updatedItem);

                    if (item == selectedItem)
                    {
                        updatedSelectedItem = updatedItem;
                    }
                }

                updatedTotalItems = updatedTotalItemsBuilder.AsImmutable();

                updatedBuilder = null;
                if (builder != null)
                {
                    updatedBuilder = new DescriptionModifyingCompletionItem(builder, completionService, workspace);
                }

                updatedDefaultBuilder = new DescriptionModifyingCompletionItem(
                    GetDefaultBuilder(defaultTrackingSpanInSubjectBuffer),
                    completionService,
                    workspace);
            }

            var completionItemToFilterText= new Dictionary<CompletionItem, string>();

            return new Model(
                disconnectedBufferGraph,
                updatedTotalItems,
                updatedTotalItems,
                updatedSelectedItem,
                actualItemFilters,
                filterState,
                completionItemToFilterText,
                isHardSelection,
                isUnique,
                useSuggestionCompletionMode,
                updatedBuilder,
                updatedDefaultBuilder,
                triggerInfo,
                GetDefaultTrackingSpanEnd(defaultTrackingSpanInSubjectBuffer, disconnectedBufferGraph),
                completionService.DismissIfEmpty);
        }

        private static ITrackingPoint GetDefaultTrackingSpanEnd(
            TextSpan defaultTrackingSpanInSubjectBuffer,
            DisconnectedBufferGraph disconnectedBufferGraph)
        {
            var viewSpan = disconnectedBufferGraph.GetSubjectBufferTextSpanInViewBuffer(defaultTrackingSpanInSubjectBuffer);
            return disconnectedBufferGraph.ViewSnapshot.Version.CreateTrackingPoint(
                viewSpan.TextSpan.End,
                PointTrackingMode.Positive);
        }

        private static CompletionItem GetDefaultBuilder(TextSpan defaultTrackingSpanInSubjectBuffer)
        {
            return new CompletionItem(null, "", defaultTrackingSpanInSubjectBuffer, isBuilder: true);
        }

        public bool IsSoftSelection
        {
            get
            {
                return !this.IsHardSelection;
            }
        }

        public Model WithFilteredItems(IList<CompletionItem> filteredItems)
        {
            return new Model(_disconnectedBufferGraph, TotalItems, filteredItems,
                filteredItems.FirstOrDefault(), CompletionItemFilters, FilterState, CompletionItemToFilterText, IsHardSelection, 
                IsUnique, UseSuggestionCompletionMode, Builder, DefaultBuilder, 
                TriggerInfo, CommitTrackingSpanEndPoint, DismissIfEmpty);
        }

        public Model WithSelectedItem(CompletionItem selectedItem)
        {
            return selectedItem == this.SelectedItem
                ? this
                : new Model(_disconnectedBufferGraph, TotalItems, FilteredItems,
                     selectedItem, CompletionItemFilters, FilterState, CompletionItemToFilterText, IsHardSelection, IsUnique, 
                     UseSuggestionCompletionMode, Builder, DefaultBuilder, TriggerInfo, 
                     CommitTrackingSpanEndPoint, DismissIfEmpty);
        }

        public Model WithHardSelection(bool isHardSelection)
        {
            return isHardSelection == this.IsHardSelection
                ? this
                : new Model(_disconnectedBufferGraph, TotalItems, FilteredItems,
                    SelectedItem, CompletionItemFilters, FilterState, CompletionItemToFilterText, isHardSelection, IsUnique,
                    UseSuggestionCompletionMode, Builder, DefaultBuilder, TriggerInfo,
                    CommitTrackingSpanEndPoint, DismissIfEmpty);
        }

        public Model WithIsUnique(bool isUnique)
        {
            return isUnique == this.IsUnique
                ? this
                : new Model(_disconnectedBufferGraph, TotalItems, FilteredItems,
                    SelectedItem, CompletionItemFilters, FilterState, CompletionItemToFilterText, IsHardSelection, isUnique,
                    UseSuggestionCompletionMode, Builder, DefaultBuilder, TriggerInfo,
                    CommitTrackingSpanEndPoint, DismissIfEmpty);
        }

        public Model WithBuilder(CompletionItem builder)
        {
            return builder == this.Builder
                ? this
                 : new Model(_disconnectedBufferGraph, TotalItems, FilteredItems,
                    SelectedItem, CompletionItemFilters, FilterState, CompletionItemToFilterText, IsHardSelection, IsUnique, 
                    UseSuggestionCompletionMode, builder, DefaultBuilder, TriggerInfo,
                    CommitTrackingSpanEndPoint, DismissIfEmpty);
        }

        public Model WithUseSuggestionCompletionMode(bool useSuggestionCompletionMode)
        {
            return useSuggestionCompletionMode == this.UseSuggestionCompletionMode
                ? this
                : new Model(_disconnectedBufferGraph, TotalItems, FilteredItems,
                    SelectedItem, CompletionItemFilters, FilterState, CompletionItemToFilterText, IsHardSelection, IsUnique,
                    useSuggestionCompletionMode, Builder, DefaultBuilder, TriggerInfo,
                    CommitTrackingSpanEndPoint, DismissIfEmpty);
        }

        internal Model WithTrackingSpanEnd(ITrackingPoint trackingSpanEnd)
        {
            return new Model(_disconnectedBufferGraph, TotalItems, FilteredItems,
                SelectedItem, CompletionItemFilters, FilterState, CompletionItemToFilterText, IsHardSelection, IsUnique, 
                UseSuggestionCompletionMode, Builder, DefaultBuilder, TriggerInfo,
                trackingSpanEnd, DismissIfEmpty);
        }

        internal Model WithFilterState(ImmutableDictionary<CompletionItemFilter, bool> filterState)
        {
            return new Model(_disconnectedBufferGraph, TotalItems, FilteredItems,
                SelectedItem, CompletionItemFilters, filterState, CompletionItemToFilterText, IsHardSelection, IsUnique,
                UseSuggestionCompletionMode, Builder, DefaultBuilder, TriggerInfo,
                CommitTrackingSpanEndPoint, DismissIfEmpty);
        }

        internal Model WithCompletionItemToFilterText(IReadOnlyDictionary<CompletionItem, string> completionItemToFilterText)
        {
            return new Model(_disconnectedBufferGraph, TotalItems, FilteredItems,
                SelectedItem, CompletionItemFilters, FilterState, completionItemToFilterText, IsHardSelection, IsUnique,
                UseSuggestionCompletionMode, Builder, DefaultBuilder, TriggerInfo,
                CommitTrackingSpanEndPoint, DismissIfEmpty);
        }

        internal SnapshotSpan GetCurrentSpanInSnapshot(ViewTextSpan originalSpan, ITextSnapshot textSnapshot)
        {
            var start = _disconnectedBufferGraph.ViewSnapshot.CreateTrackingPoint(originalSpan.TextSpan.Start, PointTrackingMode.Negative).GetPosition(textSnapshot);
            var end = Math.Max(start, this.CommitTrackingSpanEndPoint.GetPosition(textSnapshot));
            return new SnapshotSpan(
                textSnapshot, Span.FromBounds(start, end));
        }

        internal string GetCurrentTextInSnapshot(
            ViewTextSpan originalSpan,
            ITextSnapshot textSnapshot,
            int? endPoint = null)
        {
            var currentSpan = GetCurrentSpanInSnapshot(originalSpan, textSnapshot);

            var startPosition = currentSpan.Start;
            var endPosition = endPoint.HasValue ? endPoint.Value : currentSpan.End;

            // TODO(cyrusn): What to do if the span is empty, or the end comes before the start.
            // Can that even happen?  Not sure, so we'll just be resilient just in case.
            return startPosition <= endPosition
                ? textSnapshot.GetText(Span.FromBounds(startPosition, endPosition))
                : string.Empty;
        }

        internal string GetCurrentTextInSnapshot(
            TextSpan originalSpan,
            ITextSnapshot textSnapshot,
            Dictionary<TextSpan, string> textSpanToTextCache,
            int? endPoint = null)
        {
            string currentSnapshotText;
            if (!textSpanToTextCache.TryGetValue(originalSpan, out currentSnapshotText))
            {
                var viewSpan = GetSubjectBufferFilterSpanInViewBuffer(originalSpan);
                currentSnapshotText = GetCurrentTextInSnapshot(viewSpan, textSnapshot, endPoint);
                textSpanToTextCache[originalSpan] = currentSnapshotText;
            }

            return currentSnapshotText;
        }

        internal ViewTextSpan GetSubjectBufferFilterSpanInViewBuffer(TextSpan filterSpan)
        {
            return _disconnectedBufferGraph.GetSubjectBufferTextSpanInViewBuffer(filterSpan);
        }
    }
}
