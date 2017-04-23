﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.FindUsages;
using Microsoft.CodeAnalysis.LanguageServices;
using Microsoft.CodeAnalysis.Remote;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.Editor.FindUsages
{
    internal abstract partial class AbstractFindUsagesService : IFindUsagesService
    {
        public async Task FindImplementationsAsync(
            Document document, int position, IFindUsagesContext context)
        {
            var cancellationToken = context.CancellationToken;
            var tuple = await FindUsagesHelpers.FindImplementationsAsync(
                document, position, cancellationToken).ConfigureAwait(false);
            if (tuple == null)
            {
                await context.ReportMessageAsync(
                    EditorFeaturesResources.Cannot_navigate_to_the_symbol_under_the_caret).ConfigureAwait(false);
                return;
            }

            var message = tuple.Value.message;

            if (message != null)
            {
                await context.ReportMessageAsync(message).ConfigureAwait(false);
                return;
            }

            await context.SetSearchTitleAsync(
                string.Format(EditorFeaturesResources._0_implementations,
                FindUsagesHelpers.GetDisplayName(tuple.Value.symbol))).ConfigureAwait(false);

            var project = tuple.Value.project;
            foreach (var implementation in tuple.Value.implementations)
            {
                var definitionItem = await implementation.ToClassifiedDefinitionItemAsync(
                    project.Solution, includeHiddenLocations: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                await context.OnDefinitionFoundAsync(definitionItem).ConfigureAwait(false);
            }
        }

        public async Task FindReferencesAsync(
            Document document, int position, IFindUsagesContext context)
        {
            var definitionTrackingContext = new DefinitionTrackingContext(context);

            // NOTE: All ConFigureAwaits in this method need to pass 'true' so that
            // we return to the caller's context.  that's so the call to 
            // CallThirdPartyExtensionsAsync will happen on the UI thread.  We need
            // this to maintain the threading guarantee we had around that method
            // from pre-Roslyn days.
            if (!await TryFindReferencesInRemoteProcessAsync(
                    document, position, definitionTrackingContext).ConfigureAwait(true))
            {
                await TryFindReferencesInCurrentProcessAsync(
                    document, position, definitionTrackingContext).ConfigureAwait(true);
            }

            // After the FAR engine is done call into any third party extensions to see
            // if they want to add results.
            await CallThirdPartyExtensionsAsync(
                document.Project.Solution, definitionTrackingContext, context).ConfigureAwait(true);
        }

        private async Task CallThirdPartyExtensionsAsync(
            Solution solution,
            DefinitionTrackingContext definitionTrackingContext,
            IFindUsagesContext underlyingContext)
        {
            var cancellationToken = definitionTrackingContext.CancellationToken;
            var factory = solution.Workspace.Services.GetService<IDefinitionsAndReferencesFactory>();

            foreach (var definition in definitionTrackingContext.GetDefinitions())
            {
                var item = factory.GetThirdPartyDefinitionItem(solution, definition, cancellationToken);
                if (item != null)
                {
                    // ConfigureAwait(true) because we want to come back on the 
                    // same thread after calling into extensions.
                    await underlyingContext.OnDefinitionFoundAsync(item).ConfigureAwait(true);
                }
            }
        }

        private async Task<bool> TryFindReferencesInRemoteProcessAsync(
            Document document, int position, IFindUsagesContext context)
        {
            var cancellationToken = context.CancellationToken;

            var callback = new FindUsagesCallback(document.Project.Solution, context);
            using (var session = await TryGetRemoteSessionAsync(
                document.Project.Solution, callback, cancellationToken).ConfigureAwait(false))
            {
                if (session == null)
                {
                    return false;
                }

                await session.InvokeAsync(nameof(IRemoteSymbolFinder.FindReferencesAsync),
                    document.Id, position).ConfigureAwait(false);
                return true;
            }
        }

        // Internal so it can be used by the remote process.
        internal static async Task TryFindReferencesInCurrentProcessAsync(
            Document document, int position, IFindUsagesContext context)
        {
            // First, see if we're on a literal.  If so search for literals in the solution with
            // the same value.
            var found = await TryFindLiteralReferencesAsync(
                document, position, context).ConfigureAwait(false);
            if (found)
            {
                return;
            }

            // Wasn't a literal.  Try again as a symbol.
            await FindSymbolReferencesAsync(
                document, position, context).ConfigureAwait(false);
        }

        private static async Task<RemoteHostClient.Session> TryGetRemoteSessionAsync(
            Solution solution, object callback, CancellationToken cancellationToken)
        {
            var outOfProcessAllowed = solution.Workspace.Options.GetOption(FindUsagesOptions.OutOfProcessAllowed);
            if (!outOfProcessAllowed)
            {
                return null;
            }

            var client = await solution.Workspace.TryGetRemoteHostClientAsync(cancellationToken).ConfigureAwait(false);
            if (client == null)
            {
                return null;
            }

            return await client.TryCreateCodeAnalysisServiceSessionAsync(
                solution, callback, cancellationToken).ConfigureAwait(false);
        }

        private static async Task FindSymbolReferencesAsync(
            Document document, int position, IFindUsagesContext context)
        {
            var cancellationToken = context.CancellationToken;
            cancellationToken.ThrowIfCancellationRequested();

            // Find the symbol we want to search and the solution we want to search in.
            var symbolAndProject = await FindUsagesHelpers.GetRelevantSymbolAndProjectAtPositionAsync(
                document, position, cancellationToken).ConfigureAwait(false);
            if (symbolAndProject == null)
            {
                return;
            }

            await FindSymbolReferencesAsync(
                context, symbolAndProject?.symbol, symbolAndProject?.project, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Public helper that we use from features like ObjectBrowser which start with a symbol
        /// and want to push all the references to it into the Streaming-Find-References window.
        /// </summary>
        public static async Task FindSymbolReferencesAsync(
            IFindUsagesContext context, ISymbol symbol, Project project, CancellationToken cancellationToken)
        {
            await context.SetSearchTitleAsync(string.Format(EditorFeaturesResources._0_references,
                FindUsagesHelpers.GetDisplayName(symbol))).ConfigureAwait(false);

            var progressAdapter = new FindReferencesProgressAdapter(project.Solution, context);

            // Now call into the underlying FAR engine to find reference.  The FAR
            // engine will push results into the 'progress' instance passed into it.
            // We'll take those results, massage them, and forward them along to the 
            // FindReferencesContext instance we were given.
            await SymbolFinder.FindReferencesAsync(
                SymbolAndProjectId.Create(symbol, project.Id),
                project.Solution,
                progressAdapter,
                documents: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        private async static Task<bool> TryFindLiteralReferencesAsync(
            Document document, int position, IFindUsagesContext context)
        {
            var cancellationToken = context.CancellationToken;
            cancellationToken.ThrowIfCancellationRequested();

            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken).ConfigureAwait(false);
            var syntaxFacts = document.GetLanguageService<ISyntaxFactsService>();

            // Currently we only support FAR for numbers, strings and characters.  We don't
            // bother with true/false/null as those are likely to have way too many results
            // to be useful.
            var token = await syntaxTree.GetTouchingTokenAsync(
                position,
                t => syntaxFacts.IsNumericLiteral(t) ||
                     syntaxFacts.IsCharacterLiteral(t) ||
                     syntaxFacts.IsStringLiteral(t),
                cancellationToken).ConfigureAwait(false);

            if (token.RawKind == 0)
            {
                return false;
            }

            // Searching for decimals not supported currently.  Our index can only store 64bits
            // for numeric values, and a decimal won't fit within that.
            var tokenValue = token.Value;
            if (tokenValue == null || tokenValue is decimal)
            {
                return false;
            }

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            var symbol = semanticModel.GetSymbolInfo(token.Parent).Symbol ?? semanticModel.GetDeclaredSymbol(token.Parent);

            // Numeric labels are available in VB.  In that case we want the normal FAR engine to
            // do the searching.  For these literals we want to find symbolic results and not 
            // numeric matches.
            if (symbol is ILabelSymbol)
            {
                return false;
            }

            // Use the literal to make the title.  Trim literal if it's too long.
            var title = syntaxFacts.ConvertToSingleLine(token.Parent).ToString();
            if (title.Length >= 10)
            {
                title = title.Substring(0, 10) + "...";
            }

            var searchTitle = string.Format(EditorFeaturesResources._0_references, title);
            await context.SetSearchTitleAsync(searchTitle).ConfigureAwait(false);

            var solution = document.Project.Solution;

            // There will only be one 'definition' that all matching literal reference.
            // So just create it now and report to the context what it is.
            var definition = DefinitionItem.CreateNonNavigableItem(
                ImmutableArray.Create(TextTags.StringLiteral),
                ImmutableArray.Create(new TaggedText(TextTags.Text, searchTitle)));

            await context.OnDefinitionFoundAsync(definition).ConfigureAwait(false);

            var progressAdapter = new FindLiteralsProgressAdapter(context, definition);

            // Now call into the underlying FAR engine to find reference.  The FAR
            // engine will push results into the 'progress' instance passed into it.
            // We'll take those results, massage them, and forward them along to the 
            // FindUsagesContext instance we were given.
            await SymbolFinder.FindLiteralReferencesAsync(
                tokenValue, solution, progressAdapter, cancellationToken).ConfigureAwait(false);

            return true;
        }
    }
}