﻿// Copyright (c) 2020 Daniel Grunwald
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace ICSharpCode.NullabilityInference.Tests.NullabilityInference
{
    public class NullabilityTestHelper
    {
        private static readonly string refAsmPath = @"c:\program files\dotnet\packs\Microsoft.NETCore.App.Ref\3.1.0\ref\netcoreapp3.1";
        private static readonly Lazy<IEnumerable<MetadataReference>> defaultReferences = new Lazy<IEnumerable<MetadataReference>>(delegate {
            return new[]
            {
                MetadataReference.CreateFromFile(Path.Combine(refAsmPath, "System.dll")),
                MetadataReference.CreateFromFile(Path.Combine(refAsmPath, "System.Collections.dll")),
                MetadataReference.CreateFromFile(Path.Combine(refAsmPath, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(Path.Combine(refAsmPath, "System.Linq.dll")),
                MetadataReference.CreateFromFile(Path.Combine(refAsmPath, "System.Threading.dll")),
            };
        });

        static NullabilityTestHelper()
        {
            Debug.Listeners.Insert(0, new TestTraceListener());
        }

        private class TestTraceListener : DefaultTraceListener
        {
            public override void Fail(string message, string detailMessage)
            {
                throw new InvalidOperationException(message + " " + detailMessage);
            }
        }

        protected static (CSharpCompilation, NullCheckingEngine) CompileAndAnalyze(string program, CancellationToken cancellationToken = default)
        {
            var syntaxTree = SyntaxFactory.ParseSyntaxTree(program, new CSharpParseOptions(LanguageVersion.CSharp8), cancellationToken: cancellationToken);
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
            var compilation = CSharpCompilation.Create("test", new[] { syntaxTree }, defaultReferences.Value, options);
            compilation = AllNullableSyntaxRewriter.MakeAllReferenceTypesNullable(compilation, cancellationToken);
            string allNullableText = compilation.SyntaxTrees.Single().GetText().ToString();
            foreach (var diag in compilation.GetDiagnostics(cancellationToken)) {
                Assert.False(diag.Severity == DiagnosticSeverity.Error, diag.ToString());
            }
            var engine = new NullCheckingEngine(compilation);
            engine.Analyze(cancellationToken);
            return (compilation, engine);
        }

        protected static void AssertNullabilityInference(string expectedProgram, string inputProgram = null, CancellationToken cancellationToken = default)
        {
            inputProgram ??= Regex.Replace(expectedProgram, "(?<![?])[?](?![?.])", "");
            var (_, engine) = CompileAndAnalyze(inputProgram, cancellationToken);
            var newSyntax = engine.ConvertSyntaxTrees(cancellationToken).Single();
            string outputProgram = newSyntax.GetText(cancellationToken).ToString();
            // engine.ExportTypeGraph().Show();
            Assert.Equal(expectedProgram, outputProgram);
        }

        protected static bool HasPathFromParameterToReturnType(string program)
        {
            var (compilation, engine) = CompileAndAnalyze(program);
            var programClass = compilation.GetTypeByMetadataName("Program");
            Assert.False(programClass == null, "Could not find 'Program' in test");
            var testMethod = (IMethodSymbol)programClass!.GetMembers("Test").Single();
            var parameterNode = engine.TypeSystem.GetSymbolType(testMethod.Parameters.Single()).Node;
            var returnNode = engine.TypeSystem.GetSymbolType(testMethod).Node;
            // engine.ExportTypeGraph().Show();
            return ReachableNodes(parameterNode, n => n.Successors).Contains(returnNode);
        }

        private static HashSet<T> ReachableNodes<T>(T root, Func<T, IEnumerable<T>> successors)
        {
            var visited = new HashSet<T>();
            Visit(root);
            return visited;

            void Visit(T node)
            {
                if (visited.Add(node)) {
                    foreach (var successor in successors(node)) {
                        Visit(successor);
                    }
                }
            }
        }
    }
}
