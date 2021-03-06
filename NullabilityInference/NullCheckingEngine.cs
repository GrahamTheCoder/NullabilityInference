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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ICSharpCode.NullabilityInference
{
    public sealed class NullCheckingEngine
    {
        private readonly CSharpCompilation compilation;
        private readonly TypeSystem typeSystem;

        public TypeSystem TypeSystem => typeSystem;

        /// <summary>
        /// Creates a new NullCheckingEngine instance for the specified compilation.
        /// Note: for Roslyn's flow analysis to be useful to the inference, the given compilation should have as many reference types
        /// annotated as nullable as possible. This can be accomplished by using <see cref="AllNullableSyntaxRewriter.MakeAllReferenceTypesNullable"/>.
        /// </summary>
        public NullCheckingEngine(CSharpCompilation compilation)
        {
            this.compilation = compilation;
            this.typeSystem = new TypeSystem(compilation);
        }

        /// <summary>
        /// Constructs the null-type flow graph and infers nullabilities for the nodes.
        /// </summary>
        public void Analyze(CancellationToken cancellationToken)
        {
            Parallel.ForEach(compilation.SyntaxTrees,
                new ParallelOptions { CancellationToken = cancellationToken },
                t => CreateNodes(t, cancellationToken));

            Parallel.ForEach(compilation.SyntaxTrees,
                new ParallelOptions { CancellationToken = cancellationToken },
                t => CreateEdges(t, cancellationToken));

            MaximumFlow.Compute(typeSystem.AllNodes, typeSystem.NullableNode, typeSystem.NonNullNode, cancellationToken);

            // Run non-null with ignoreEdgesWithoutCapacity before nullable so that errors
            // are reported as close to non-null as possible.
            typeSystem.NonNullNode.NullType = NullType.Infer;
            InferNonNull(typeSystem.NonNullNode, ignoreEdgesWithoutCapacity: true);
            typeSystem.NullableNode.NullType = NullType.Infer;
            InferNullable(typeSystem.NullableNode, ignoreEdgesWithoutCapacity: false);

            // There's going to be a bunch of remaining nodes where either choice would work.
            // For parameters, prefer marking those as nullable:
            foreach (var paramNode in typeSystem.NodesInInputPositions) {
                InferNullable(paramNode.ReplacedWith);
            }
            foreach (var node in typeSystem.AllNodes) {
                // Finally, anything left over is inferred to be non-null:
                if (node.NullType == NullType.Infer) {
                    if (node.ReplacedWith.NullType != NullType.Infer)
                        node.NullType = node.ReplacedWith.NullType;
                    else
                        node.NullType = NullType.NonNull;
                }
                Debug.Assert(node.NullType == node.ReplacedWith.NullType);
            }
        }

        /// <summary>
        /// Returns new syntax trees where the inferred nullability has been inserted.
        /// </summary>
        public ParallelQuery<SyntaxTree> ConvertSyntaxTrees(CancellationToken cancellationToken)
        {
            return compilation.SyntaxTrees.AsParallel().WithCancellation(cancellationToken).Select(syntaxTree => {
                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var rewriter = new InferredNullabilitySyntaxRewriter(semanticModel, typeSystem.GetMapping(syntaxTree), cancellationToken);
                var newRoot = rewriter.Visit(syntaxTree.GetRoot());
                return syntaxTree.WithRootAndOptions(newRoot, syntaxTree.Options);
            });
        }

        private void InferNonNull(NullabilityNode node, bool ignoreEdgesWithoutCapacity = false)
        {
            if (node.NullType != NullType.Infer) {
                return;
            }
            node.NullType = NullType.NonNull;
            foreach (var edge in node.IncomingEdges) {
                if (ignoreEdgesWithoutCapacity == false || edge.Capacity > 0) {
                    InferNonNull(edge.Source, ignoreEdgesWithoutCapacity);
                }
            }
        }

        private void InferNullable(NullabilityNode node, bool ignoreEdgesWithoutCapacity = false)
        {
            if (node.NullType != NullType.Infer) {
                return;
            }
            node.NullType = NullType.Nullable;
            foreach (var edge in node.OutgoingEdges) {
                if (ignoreEdgesWithoutCapacity == false || edge.Capacity > 0) {
                    InferNullable(edge.Target, ignoreEdgesWithoutCapacity);
                }
            }
        }

        private void CreateNodes(SyntaxTree syntaxTree, CancellationToken cancellationToken)
        {
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var tsBuilder = new TypeSystem.Builder(typeSystem);
            var visitor = new NodeBuildingSyntaxVisitor(semanticModel, tsBuilder, cancellationToken);
            visitor.Visit(syntaxTree.GetRoot(cancellationToken));
            lock (typeSystem) {
                typeSystem.RegisterNodes(syntaxTree, visitor.Mapping);
                tsBuilder.Flush(typeSystem);
            }
        }

        private void CreateEdges(SyntaxTree syntaxTree, CancellationToken cancellationToken)
        {
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var tsBuilder = new TypeSystem.Builder(typeSystem);
            var visitor = new EdgeBuildingSyntaxVisitor(semanticModel, typeSystem, tsBuilder, typeSystem.GetMapping(syntaxTree), cancellationToken);
            visitor.Visit(syntaxTree.GetRoot(cancellationToken));
            lock (typeSystem) {
                tsBuilder.Flush(typeSystem);
            }
        }

#if DEBUG
        /// <summary>
        /// Exports the type graph in a form suitable for visualization.
        /// </summary>
        public GraphVizGraph ExportTypeGraph()
        {
            return ExportTypeGraph(n => n.NullType != NullType.Oblivious && n.ReplacedWith == n);
        }

        /// <summary>
        /// Exports a subset of the type graph in a form suitable for visualization.
        /// </summary>
        public GraphVizGraph ExportTypeGraph(Predicate<NullabilityNode> nodeFilter)
        {
            if (nodeFilter == null)
                throw new ArgumentNullException("includeInGraph");
            GraphVizGraph graph = new GraphVizGraph { rankdir = "BT" };
            List<NullabilityEdge> graphEdges = new List<NullabilityEdge>();
            foreach (NullabilityNode node in typeSystem.AllNodes) {
                foreach (NullabilityEdge edge in node.IncomingEdges) {
                    if (nodeFilter(edge.Source) || nodeFilter(edge.Target)) {
                        graphEdges.Add(edge);
                    }
                }
            }
            // Select nodes based on include filter
            IEnumerable<NullabilityNode> includedNodes = typeSystem.AllNodes.Where(n => nodeFilter(n));
            // Add nodes necessary for selected edges
            includedNodes = includedNodes.Concat(graphEdges.Select(g => g.Source)).Concat(graphEdges.Select(g => g.Target)).Distinct();
            var nodeIds = new Dictionary<NullabilityNode, string>();
            foreach (NullabilityNode node in includedNodes) {
                string nodeId = $"n{nodeIds.Count}";
                nodeIds.Add(node, nodeId);
                GraphVizNode gvNode = new GraphVizNode(nodeId) { label = node.Name, fontsize = 32 };
                if (node is SpecialNullabilityNode) {
                    gvNode.fontsize = 24;
                } else {
                    gvNode.fontsize = 12;
                    gvNode.margin = "0.05,0.05";
                    gvNode.height = 0;
                    gvNode.shape = "box";
                }
                if (node.Location != null) {
                    gvNode.label += $"\n{node.Location.EndPosToString()}";
                }
                //gvNode.label += $"\n{node.NullType}";
                graph.AddNode(gvNode);
            }
            foreach (NullabilityEdge edge in graphEdges) {
                var gvEdge = new GraphVizEdge(nodeIds[edge.Source], nodeIds[edge.Target]);
                gvEdge.label = edge.Label;
                gvEdge.fontsize = 8;
                if (edge.IsError)
                    gvEdge.color = "red";
                else if (edge.Capacity == 0)
                    gvEdge.color = "yellow";
                graph.AddEdge(gvEdge);
            }
            return graph;
        }
#endif
    }
}
