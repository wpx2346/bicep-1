// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Bicep.Core.FileSystem;
using Bicep.Core.Samples;
using Bicep.Core.Semantics;
using Bicep.Core.Text;
using Bicep.Core.UnitTests;
using Bicep.Core.UnitTests.Assertions;
using Bicep.Core.UnitTests.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Bicep.Core.IntegrationTests.Semantics
{
    [TestClass]
    public class ParamsSemanticModelTests
    {
        private static ServiceBuilder Services => new ServiceBuilder()
            .WithEmptyAzResources()
            .WithEnvironmentVariables(
                ("stringEnvVariableName", "test"),
                ("intEnvVariableName", "100"),
                ("boolEnvironmentVariable", "true")
            );

        [NotNull]
        public TestContext? TestContext { get; set; }

        private SemanticModel CreateSemanticModel(ServiceBuilder services, string paramsFilePath)
        {
            var configuration = BicepTestConstants.BuiltInConfiguration;
            var sourceFileGrouping = services.Build().BuildSourceFileGrouping(PathHelper.FilePathToFileUrl(paramsFilePath));
            var compilation = services.Build().BuildCompilation(sourceFileGrouping);

            return compilation.GetEntrypointSemanticModel();
        }

        [DataTestMethod]
        [BaselineData_Bicepparam.TestData()]
        [TestCategory(BaselineHelper.BaselineTestCategory)]
        public void ProgramsShouldProduceExpectedDiagnostic(BaselineData_Bicepparam baselineData)
        {
            var data = baselineData.GetData(TestContext);

            var model = CreateSemanticModel(Services, data.Parameters.OutputFilePath);

            // use a deterministic order
            var diagnostics = model.GetAllDiagnostics()
                .OrderBy(x => x.Span.Position)
                .ThenBy(x => x.Span.Length)
                .ThenBy(x => x.Message, StringComparer.Ordinal);

            var sourceTextWithDiags = OutputHelper.AddDiagsToSourceText(data.Parameters.EmbeddedFile.Contents, "\n", diagnostics,
                diag => OutputHelper.GetDiagLoggingString(data.Parameters.EmbeddedFile.Contents, data.OutputFolder.OutputFolderPath, diag));

            data.Diagnostics.WriteToOutputFolder(sourceTextWithDiags);
            data.Diagnostics.ShouldHaveExpectedValue();
        }

        [DataTestMethod]
        [BaselineData_Bicepparam.TestData()]
        [TestCategory(BaselineHelper.BaselineTestCategory)]
        public void ProgramsShouldProduceExpectedUserDeclaredSymbols(BaselineData_Bicepparam baselineData)
        {
            var data = baselineData.GetData(TestContext);

            var model = CreateSemanticModel(Services, data.Parameters.OutputFilePath);

            var symbols = SymbolCollector
                .CollectSymbols(model)
                .OfType<ParameterAssignmentSymbol>();

            string getLoggingString(ParameterAssignmentSymbol symbol)
            {
                (_, var startChar) = TextCoordinateConverter.GetPosition(model.SourceFile.LineStarts, symbol.DeclaringParameterAssignment.Span.Position);

                return $"{symbol.Kind} {symbol.Name}. Type: {symbol.Type}. Declaration start char: {startChar}, length: {symbol.DeclaringParameterAssignment.Span.Length}";
            }

            var sourceTextWithDiags = OutputHelper.AddDiagsToSourceText(data.Parameters.EmbeddedFile.Contents, "\n", symbols, symb => symb.NameSource.Span, getLoggingString);

            data.Symbols.WriteToOutputFolder(sourceTextWithDiags);
            data.Symbols.ShouldHaveExpectedValue();
        }
    }
}
