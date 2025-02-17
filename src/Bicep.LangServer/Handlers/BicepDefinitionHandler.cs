// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Azure.Deployments.Core.Definitions.Schema;
using Azure.Deployments.Core.Entities;
using Bicep.Core;
using Bicep.Core.Features;
using Bicep.Core.FileSystem;
using Bicep.Core.Navigation;
using Bicep.Core.Parsing;
using Bicep.Core.Registry;
using Bicep.Core.Semantics;
using Bicep.Core.SourceCode;
using Bicep.Core.Syntax;
using Bicep.Core.TypeSystem;
using Bicep.Core.Workspaces;
using Bicep.LanguageServer.CompilationManager;
using Bicep.LanguageServer.Completions;
using Bicep.LanguageServer.Extensions;
using Bicep.LanguageServer.Providers;
using Bicep.LanguageServer.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace Bicep.LanguageServer.Handlers
{
    public class BicepDefinitionHandler : DefinitionHandlerBase
    {
        private readonly ISymbolResolver symbolResolver;
        private readonly ICompilationManager compilationManager;
        private readonly IFileResolver fileResolver;
        private readonly ILanguageServerFacade languageServer;
        private readonly IModuleDispatcher moduleDispatcher;
        private readonly IFeatureProviderFactory featureProviderFactory;

        public BicepDefinitionHandler(
            ISymbolResolver symbolResolver,
            ICompilationManager compilationManager,
            IFileResolver fileResolver,
            ILanguageServerFacade languageServer,
            IModuleDispatcher moduleDispatcher,
            IFeatureProviderFactory featureProviderFactory) : base()
        {
            this.symbolResolver = symbolResolver;
            this.compilationManager = compilationManager;
            this.fileResolver = fileResolver;
            this.languageServer = languageServer;
            this.moduleDispatcher = moduleDispatcher;
            this.featureProviderFactory = featureProviderFactory;
        }

        public override Task<LocationOrLocationLinks> Handle(DefinitionParams request, CancellationToken cancellationToken)
        {
            var context = this.compilationManager.GetCompilation(request.TextDocument.Uri);
            if (context is null)
            {
                return Task.FromResult(new LocationOrLocationLinks());
            }

            var result = this.symbolResolver.ResolveSymbol(request.TextDocument.Uri, request.Position);

            // No parent Symbol: ad hoc syntax matching
            var response = result switch
            {
                null => HandleUnboundSymbolLocation(request, context),

                { Symbol: ParameterAssignmentSymbol param } => HandleParameterAssignment(request, result, context, param),

                // Used for the declaration ONLY of a wildcard import. Other syntax that resolves to a wildcard import will be handled by HandleDeclaredDefinitionLocation
                { Origin: WildcardImportSyntax, Symbol: WildcardImportSymbol wildcardImport }
                    => HandleWildcardImportDeclaration(context, request, result, wildcardImport),

                { Symbol: ImportedSymbol imported } => HandleImportedSymbolLocation(request, result, context, imported),

                { Symbol: DeclaredSymbol declaration } => HandleDeclaredDefinitionLocation(request, result, declaration),

                // Object property: currently only used for module param goto
                { Origin: ObjectPropertySyntax } => HandleObjectPropertyLocation(request, result, context),

                // Used for module (name), variable, wildcard import, or resource property access
                { Symbol: PropertySymbol } => HandlePropertyLocation(request, result, context),

                _ => new(),
            };

            return Task.FromResult(response);
        }

        protected override DefinitionRegistrationOptions CreateRegistrationOptions(DefinitionCapability capability, ClientCapabilities clientCapabilities) => new()
        {
            DocumentSelector = DocumentSelectorFactory.CreateForBicepAndParams()
        };

        private LocationOrLocationLinks HandleUnboundSymbolLocation(DefinitionParams request, CompilationContext context)
        {
            int offset = PositionHelper.GetOffset(context.LineStarts, request.Position);
            var matchingNodes = SyntaxMatcher.FindNodesMatchingOffset(context.Compilation.SourceFileGrouping.EntryPoint.ProgramSyntax, offset);
            { // Definition handler for a non symbol bound to implement module path goto.
                // try to resolve module path syntax from given offset using tail matching.
                if (SyntaxMatcher.IsTailMatch<ModuleDeclarationSyntax, StringSyntax, Token>(
                     matchingNodes,
                     (moduleSyntax, stringSyntax, token) => moduleSyntax.Path == stringSyntax && token.Type == TokenType.StringComplete)
                 && matchingNodes[^3] is ModuleDeclarationSyntax moduleDeclarationSyntax
                 && matchingNodes[^2] is StringSyntax stringToken
                 && context.Compilation.SourceFileGrouping.TryGetSourceFile(moduleDeclarationSyntax).IsSuccess(out var sourceFile)
                 && this.moduleDispatcher.TryGetModuleReference(moduleDeclarationSyntax, request.TextDocument.Uri.ToUriEncoded()).IsSuccess(out var moduleReference))
                {
                    return HandleModuleReference(context, stringToken, sourceFile, moduleReference);
                }
            }
            { // Definition handler for a non symbol bound to implement import path goto.
                // try to resolve import path syntax from given offset using tail matching.
                if (SyntaxMatcher.IsTailMatch<CompileTimeImportDeclarationSyntax, CompileTimeImportFromClauseSyntax, StringSyntax, Token>(
                     matchingNodes,
                     (_, fromClauseSyntax, stringSyntax, token) => fromClauseSyntax.Path == stringSyntax && token.Type == TokenType.StringComplete)
                 && matchingNodes[^4] is CompileTimeImportDeclarationSyntax importDeclarationSyntax
                 && matchingNodes[^2] is StringSyntax stringToken
                 && context.Compilation.SourceFileGrouping.TryGetSourceFile(importDeclarationSyntax).IsSuccess(out var sourceFile)
                 && this.moduleDispatcher.TryGetModuleReference(importDeclarationSyntax, request.TextDocument.Uri.ToUriEncoded()).IsSuccess(out var moduleReference))
                {
                    // goto beginning of the module file.
                    return GetFileDefinitionLocation(
                        GetModuleSourceLinkUri(sourceFile, moduleReference),
                        stringToken,
                        context,
                        new() { Start = new(0, 0), End = new(0, 0) });
                }
            }
            {  // Definition handler for a non symbol bound to implement load* functions file argument path goto.
                if (SyntaxMatcher.IsTailMatch<StringSyntax, Token>(
                        matchingNodes,
                        (stringSyntax, token) => !stringSyntax.IsInterpolated() && token.Type == TokenType.StringComplete)
                    && matchingNodes[^2] is StringSyntax stringToken
                    && context.Compilation.GetEntrypointSemanticModel().GetDeclaredType(stringToken) is { } stringType
                    && stringType.ValidationFlags.HasFlag(TypeSymbolValidationFlags.IsStringFilePath)
                    && stringToken.TryGetLiteralValue() is { } stringTokenValue
                    && fileResolver.TryResolveFilePath(context.Compilation.SourceFileGrouping.EntryPoint.FileUri, stringTokenValue) is { } fileUri
                    && fileResolver.FileExists(fileUri))
                {
                    return GetFileDefinitionLocation(
                        fileUri,
                        stringToken,
                        context,
                        new() { Start = new(0, 0), End = new(0, 0) });
                }
            }
            {
                if (SyntaxMatcher.GetTailMatch<UsingDeclarationSyntax, StringSyntax, Token>(matchingNodes) is (var @using, var path, _) &&
                    @using.Path == path &&
                    context.Compilation.SourceFileGrouping.TryGetSourceFile(@using).IsSuccess(out var sourceFile))
                {
                    return GetFileDefinitionLocation(
                        sourceFile.FileUri,
                        path,
                        context,
                        new() { Start = new(0, 0), End = new(0, 0) });
                }
            }

            // all other unbound syntax nodes return no
            return new();
        }

        private LocationOrLocationLinks HandleModuleReference(CompilationContext context, StringSyntax stringToken, ISourceFile sourceFile, ArtifactReference moduleReference)
        {
            // Return the correct link format so our language client can display the sources
            return GetFileDefinitionLocation(
                GetModuleSourceLinkUri(sourceFile, moduleReference),
                stringToken,
                context,
                new() { Start = new(0, 0), End = new(0, 0) });
        }

        private Uri GetModuleSourceLinkUri(ISourceFile sourceFile, ArtifactReference moduleReference)
        {
            if (!this.CanClientAcceptRegistryContent() || !moduleReference.IsExternal)
            {
                // the client doesn't support the bicep-cache scheme or we're dealing with a local module
                // just use the file URI
                return sourceFile.FileUri;
            }

            // this path is specific to clients that indicate to the server that they can handle bicep-cache document URIs
            // the client expectation when the user navigates to a file with a bicep-cache:// URI is to request file content
            // via the textDocument/bicepCache LSP request implemented in the BicepRegistryCacheRequestHandler.

            var sourceFilePath = sourceFile.FileUri.AbsolutePath;

            if (moduleDispatcher.TryGetModuleSources(moduleReference) is SourceArchive sourceArchive)
            {
                // We have Bicep source code available.
                // Replace the local cached JSON name (always main.json) with the actual source entrypoint filename (e.g.
                //   myentrypoint.bicep) so clients know to request the bicep instead of json, and so they know to use the
                //   bicep language server to display the code.
                //   e.g. "path/main.json" -> "path/myentrypoint.bicep"
                // The "path/myentrypoint.bicep" path is virtual (doesn't actually exist).
                var entrypointFilename = Path.GetFileName(sourceArchive.EntrypointPath);
                sourceFilePath = Path.Join(Path.GetDirectoryName(sourceFilePath), entrypointFilename);
            }

            // The file path and fully qualified reference may contain special characters (like :) that need to be url-encoded.
            sourceFilePath = WebUtility.UrlEncode(sourceFilePath);
            var fullyQualifiedReference = WebUtility.UrlEncode(moduleReference.FullyQualifiedReference);

            // Encode the source file path as a path and the fully qualified reference as a fragment.
            // VsCode will pass it to our language client, which will respond by requesting the source to display via
            //   a textDocument/bicepCache request (see BicepCacheHandler)
            // Example: bicep-cache:br:myregistry.azurecr.io/myrepo:v1#/Users/MyUserName/.bicep/br/registry.azurecr.io/myrepo/v1$/main.json (encoded)
            //   or if source is available:
            // Example: bicep-cache:br:myregistry.azurecr.io/myrepo:v1#/Users/MyUserName/.bicep/br/registry.azurecr.io/myrepo/v1$/entrypoint.bicep (encoded)
            return new Uri($"bicep-cache:{fullyQualifiedReference}#{sourceFilePath}");
        }

        private LocationOrLocationLinks HandleWildcardImportDeclaration(CompilationContext context, DefinitionParams request, SymbolResolutionResult result, WildcardImportSymbol wildcardImport)
        {
            if (context.Compilation.SourceFileGrouping.TryGetSourceFile(wildcardImport.EnclosingDeclaration).IsSuccess(out var sourceFile) &&
                wildcardImport.TryGetModuleReference().IsSuccess(out var moduleReference))
            {
                return GetFileDefinitionLocation(
                    GetModuleSourceLinkUri(sourceFile, moduleReference),
                    wildcardImport.DeclaringSyntax,
                    context,
                    new() { Start = new(0, 0), End = new(0, 0) });
            }

            return new();
        }

        private static LocationOrLocationLinks HandleDeclaredDefinitionLocation(DefinitionParams request, SymbolResolutionResult result, DeclaredSymbol declaration)
        {
            return new(new LocationOrLocationLink(new LocationLink
            {
                // source of the link. Underline only the symbolic name
                OriginSelectionRange = (result.Origin is ITopLevelNamedDeclarationSyntax named ? named.Name : result.Origin).ToRange(result.Context.LineStarts),
                TargetUri = request.TextDocument.Uri,

                // entire span of the declaredSymbol
                TargetRange = declaration.DeclaringSyntax.ToRange(result.Context.LineStarts),
                TargetSelectionRange = declaration.NameSource.ToRange(result.Context.LineStarts)
            }));
        }

        private LocationOrLocationLinks HandleObjectPropertyLocation(DefinitionParams request, SymbolResolutionResult result, CompilationContext context)
        {
            int offset = PositionHelper.GetOffset(context.LineStarts, request.Position);
            var matchingNodes = SyntaxMatcher.FindNodesMatchingOffset(context.Compilation.SourceFileGrouping.EntryPoint.ProgramSyntax, offset);
            // matchingNodes[0] should be ProgramSyntax
            if (matchingNodes[1] is ModuleDeclarationSyntax moduleDeclarationSyntax)
            {
                // capture the property accesses leading to this specific property access
                var propertyAccesses = matchingNodes.OfType<ObjectPropertySyntax>().ToList();
                // only two level of traversals: mod { params: { <outputName1>: ...}}
                if (propertyAccesses.Count == 2 &&
                    propertyAccesses[0].TryGetKeyText() is { } propertyType &&
                    propertyAccesses[1].TryGetKeyText() is { } propertyName)
                {
                    // underline only the key of the object property access
                    return GetModuleSymbolLocation(
                        propertyAccesses.Last().Key,
                        context,
                        moduleDeclarationSyntax,
                        propertyType,
                        propertyName);
                }
            }

            return new();
        }

        private LocationOrLocationLinks HandlePropertyLocation(DefinitionParams request, SymbolResolutionResult result, CompilationContext context)
        {
            var semanticModel = context.Compilation.GetEntrypointSemanticModel();

            // Find the underlying VariableSyntax being accessed
            var syntax = result.Origin;
            var propertyAccesses = new List<IdentifierSyntax>();
            while (syntax is PropertyAccessSyntax propertyAccessSyntax)
            {
                // since we are traversing bottom up, add this access to the beginning of the list
                propertyAccesses.Insert(0, propertyAccessSyntax.PropertyName);
                syntax = propertyAccessSyntax.BaseExpression;
            }

            if (syntax is VariableAccessSyntax ancestor
                && semanticModel.GetSymbolInfo(ancestor) is DeclaredSymbol ancestorSymbol)
            {
                // If the symbol is a module, we need to redirect the user to the module file
                // note: module.name doesn't follow this: it should refer to the declaration of the module in the current file, like regular variable and resource property accesses
                if (propertyAccesses.Count == 2
                && ancestorSymbol.DeclaringSyntax is ModuleDeclarationSyntax moduleDeclarationSyntax)
                {
                    // underline only the last property access
                    return GetModuleSymbolLocation(
                        propertyAccesses.Last(),
                        context,
                        moduleDeclarationSyntax,
                        propertyAccesses[0].IdentifierName,
                        propertyAccesses[1].IdentifierName);
                }

                // The user should be redirected to the import target file if the symbol is a wildcard import
                if (propertyAccesses.Count == 1 && ancestorSymbol is WildcardImportSymbol wildcardImport)
                {
                    if (wildcardImport.TryGetSemanticModel() is SemanticModel importedTargetBicepModel &&
                        importedTargetBicepModel.Root.TypeDeclarations.Where(type => LanguageConstants.IdentifierComparer.Equals(type.Name, propertyAccesses.Single().IdentifierName)).FirstOrDefault() is { } originalDeclaration)
                    {
                        var range = PositionHelper.GetNameRange(importedTargetBicepModel.SourceFile.LineStarts, originalDeclaration.DeclaringSyntax);

                        return new(new LocationOrLocationLink(new LocationLink
                        {
                            // source of the link. Underline only the symbolic name
                            OriginSelectionRange = result.Origin.ToRange(context.LineStarts),
                            TargetUri = importedTargetBicepModel.SourceFile.FileUri,

                            // entire span of the declaredSymbol
                            TargetRange = range,
                            TargetSelectionRange = range
                        }));
                    }

                    return GetArmSourceTemplateInfo(context, wildcardImport.EnclosingDeclaration) switch
                    {
                        (Template template, Uri localFileUri) when template.Definitions?.TryGetValue(propertyAccesses.Single().IdentifierName, out var definition) == true && ToRange(definition) is { } range
                            => new(new LocationOrLocationLink(new LocationLink
                            {
                                OriginSelectionRange = result.Origin.ToRange(context.LineStarts),
                                TargetUri = localFileUri,
                                TargetRange = range,
                                TargetSelectionRange = range,
                            })),
                        _ => new(),
                    };
                }

                // Otherwise, we redirect user to the specified module, variable, or resource declaration
                if (GetObjectSyntaxFromDeclaration(ancestorSymbol.DeclaringSyntax) is ObjectSyntax objectSyntax
                    && ObjectSyntaxExtensions.TryGetPropertyByNameRecursive(objectSyntax, propertyAccesses) is ObjectPropertySyntax resultingSyntax)
                {
                    // underline only the last property access
                    return new(new LocationOrLocationLink(new LocationLink
                    {
                        OriginSelectionRange = propertyAccesses.Last().ToRange(result.Context.LineStarts),
                        TargetUri = request.TextDocument.Uri,
                        TargetRange = resultingSyntax.ToRange(result.Context.LineStarts),
                        TargetSelectionRange = resultingSyntax.ToRange(result.Context.LineStarts)
                    }));
                }
            }

            return new();
        }

        private LocationOrLocationLinks HandleParameterAssignment(DefinitionParams request, SymbolResolutionResult result, CompilationContext context, ParameterAssignmentSymbol param)
        {
            if (param.NameSource is not { } nameSyntax)
            {
                return new();
            }

            var paramsModel = context.Compilation.GetEntrypointSemanticModel();
            if (!paramsModel.Root.TryGetBicepFileSemanticModelViaUsing().IsSuccess(out var usingModel) ||
                usingModel is not SemanticModel bicepModel)
            {
                return new();
            }

            if (bicepModel.Root.ParameterDeclarations
                .FirstOrDefault(x => x.DeclaringParameter.Name.NameEquals(param.Name)) is not ParameterSymbol parameterSymbol)
            {
                return new();
            }

            var range = PositionHelper.GetNameRange(bicepModel.SourceFile.LineStarts, parameterSymbol.DeclaringSyntax);
            var documentUri = bicepModel.SourceFile.FileUri;

            return new(new LocationOrLocationLink(new LocationLink
            {
                // source of the link. Underline only the symbolic name
                OriginSelectionRange = nameSyntax.ToRange(context.LineStarts),
                TargetUri = documentUri,

                // entire span of the declaredSymbol
                TargetRange = range,
                TargetSelectionRange = range
            }));
        }

        private LocationOrLocationLinks HandleImportedSymbolLocation(DefinitionParams request, SymbolResolutionResult result, CompilationContext context, ImportedSymbol imported)
        {
            // source of the link. Underline only the symbolic name
            var originSelectionRange = result.Origin.ToRange(context.LineStarts);

            if (imported.TryGetSemanticModel() is SemanticModel bicepModel &&
                bicepModel.Root.Declarations.Where(type => LanguageConstants.IdentifierComparer.Equals(type.Name, imported.OriginalSymbolName)).FirstOrDefault() is { } originalDeclaration)
            {
                // entire span of the declaredSymbol
                var targetRange = PositionHelper.GetNameRange(bicepModel.SourceFile.LineStarts, originalDeclaration.DeclaringSyntax);

                return new(new LocationOrLocationLink(new LocationLink
                {
                    OriginSelectionRange = originSelectionRange,
                    TargetUri = bicepModel.SourceFile.FileUri,
                    TargetRange = targetRange,
                    TargetSelectionRange = targetRange,
                }));
            }

            var (armTemplate, armTemplateUri) = GetArmSourceTemplateInfo(context, imported.EnclosingDeclaration);

            if (armTemplateUri is not null && imported.OriginalSymbolName is string nonNullName)
            {
                if (imported.Kind == Core.Semantics.SymbolKind.TypeAlias &&
                    armTemplate?.Definitions?.TryGetValue(nonNullName, out var originalTypeDefintion) is true &&
                    ToRange(originalTypeDefintion) is Range typeDefintionRange)
                {
                    return new(new LocationOrLocationLink(new LocationLink
                    {
                        OriginSelectionRange = originSelectionRange,
                        TargetUri = armTemplateUri,
                        TargetRange = typeDefintionRange,
                        TargetSelectionRange = typeDefintionRange,
                    }));
                }

                if (imported.Kind == Core.Semantics.SymbolKind.Variable)
                {
                    if (armTemplate?.Variables?.TryGetValue(nonNullName, out var variableDeclaration) is true && ToRange(variableDeclaration) is Range variableDefinitionRange)
                    {
                        return new(new LocationOrLocationLink(new LocationLink
                        {
                            OriginSelectionRange = originSelectionRange,
                            TargetUri = armTemplateUri,
                            TargetRange = variableDefinitionRange,
                            TargetSelectionRange = variableDefinitionRange,
                        }));
                    }

                    if (armTemplate?.Variables?.TryGetValue("copy", out var copyVariablesDeclaration) is true &&
                        copyVariablesDeclaration.Value is JArray copyVariablesArray &&
                        copyVariablesArray.Where(e => e is JObject objectElement &&
                            objectElement.TryGetValue("name", StringComparison.OrdinalIgnoreCase, out var nameToken) &&
                            nameToken is JValue { Value: string nameString } &&
                            StringComparer.OrdinalIgnoreCase.Equals(nameString, nonNullName))
                            .FirstOrDefault() is JToken copyVariableToken &&
                        ToRange(copyVariableToken) is Range copyVariableDefinitionRange)
                    {
                        return new(new LocationOrLocationLink(new LocationLink
                        {
                            OriginSelectionRange = originSelectionRange,
                            TargetUri = armTemplateUri,
                            TargetRange = copyVariableDefinitionRange,
                            TargetSelectionRange = copyVariableDefinitionRange,
                        }));
                    }
                }
            }

            return new();
        }

        private static (Template?, Uri?) GetArmSourceTemplateInfo(CompilationContext context, IArtifactReferenceSyntax foreignTemplateReference)
            => context.Compilation.SourceFileGrouping.TryGetSourceFile(foreignTemplateReference).TryUnwrap() switch
            {
                TemplateSpecFile templateSpecFile => (templateSpecFile.MainTemplateFile.Template, templateSpecFile.FileUri),
                ArmTemplateFile armTemplateFile => (armTemplateFile.Template, armTemplateFile.FileUri),
                _ => (null, null),
            };

        private static Range? ToRange(JTokenMetadata jToken)
            => jToken.LineNumber.HasValue && jToken.LinePosition.HasValue
                ? new(jToken.LineNumber.Value - 1, jToken.LinePosition.Value, jToken.LineNumber.Value - 1, jToken.LinePosition.Value)
                : null;

        private static Range? ToRange(IJsonLineInfo jsonLineInfo)
            => jsonLineInfo.LineNumber > 0
                ? new(jsonLineInfo.LineNumber - 1, jsonLineInfo.LinePosition, jsonLineInfo.LineNumber - 1, jsonLineInfo.LinePosition)
                : null;

        private LocationOrLocationLinks GetModuleSymbolLocation(
            SyntaxBase underlinedSyntax,
            CompilationContext context,
            ModuleDeclarationSyntax moduleDeclarationSyntax,
            string propertyType,
            string propertyName)
        {
            if (context.Compilation.SourceFileGrouping.TryGetSourceFile(moduleDeclarationSyntax).IsSuccess(out var sourceFile) && sourceFile is BicepFile bicepFile
            && context.Compilation.GetSemanticModel(bicepFile) is SemanticModel moduleModel)
            {
                switch (propertyType)
                {
                    case LanguageConstants.ModuleOutputsPropertyName:
                        if (moduleModel.Root.OutputDeclarations
                            .FirstOrDefault(d => string.Equals(d.Name, propertyName)) is OutputSymbol outputSymbol)
                        {
                            return GetFileDefinitionLocation(
                                bicepFile.FileUri,
                                underlinedSyntax,
                                context,
                                outputSymbol.DeclaringOutput.Name.ToRange(bicepFile.LineStarts));
                        }
                        break;
                    case LanguageConstants.ModuleParamsPropertyName:
                        if (moduleModel.Root.ParameterDeclarations
                            .FirstOrDefault(d => string.Equals(d.Name, propertyName)) is ParameterSymbol parameterSymbol)
                        {
                            return GetFileDefinitionLocation(
                                bicepFile.FileUri,
                                underlinedSyntax,
                                context,
                                parameterSymbol.DeclaringParameter.Name.ToRange(bicepFile.LineStarts));
                        }
                        break;
                }

            }

            return new();
        }

        private LocationOrLocationLinks GetFileDefinitionLocation(
            Uri fileUri,
            SyntaxBase originalSelectionSyntax,
            CompilationContext context,
            Range targetRange)
        {
            return new LocationOrLocationLinks(new LocationOrLocationLink(new LocationLink
            {
                OriginSelectionRange = originalSelectionSyntax.ToRange(context.LineStarts),
                TargetUri = DocumentUri.From(fileUri),
                TargetRange = targetRange,
                TargetSelectionRange = targetRange
            }));
        }

        private static ObjectSyntax? GetObjectSyntaxFromDeclaration(SyntaxBase syntax) => syntax switch
        {
            ResourceDeclarationSyntax resourceDeclarationSyntax when resourceDeclarationSyntax.TryGetBody() is ObjectSyntax objectSyntax => objectSyntax,
            ModuleDeclarationSyntax moduleDeclarationSyntax when moduleDeclarationSyntax.TryGetBody() is ObjectSyntax objectSyntax => objectSyntax,
            VariableDeclarationSyntax variableDeclarationSyntax when variableDeclarationSyntax.Value is ObjectSyntax objectSyntax => objectSyntax,
            _ => null,
        };

        // True if the client knows how (like our vscode extension) to handle the "bicep-cache:" schema
        private bool CanClientAcceptRegistryContent()
        {
            if (this.languageServer.ClientSettings.InitializationOptions is not JObject obj ||
                obj.Property("enableRegistryContent") is not { } property ||
                property.Value.Type != JTokenType.Boolean)
            {
                return false;
            }

            return property.Value.Value<bool>();
        }
    }
}
