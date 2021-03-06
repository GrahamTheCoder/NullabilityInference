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

using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ICSharpCode.NullabilityInference
{
    /// <summary>
    /// Rewrites a C# syntax tree by replacing nullable reference type syntax with that inferred by our analysis.
    /// </summary>
    internal sealed class InferredNullabilitySyntaxRewriter : CSharpSyntaxRewriter
    {
        private readonly SemanticModel semanticModel;
        private readonly SyntaxToNodeMapping mapping;
        private readonly CancellationToken cancellationToken;

        public InferredNullabilitySyntaxRewriter(SemanticModel semanticModel, SyntaxToNodeMapping mapping, CancellationToken cancellationToken)
        {
            this.semanticModel = semanticModel;
            this.mapping = mapping;
            this.cancellationToken = cancellationToken;
        }

        public override SyntaxNode? VisitNullableType(NullableTypeSyntax node)
        {
            var elementType = node.ElementType.Accept(this);
            if (elementType == null)
                return null;
            var symbolInfo = semanticModel.GetSymbolInfo(node);
            if (symbolInfo.Symbol is ITypeSymbol { IsReferenceType: true }) {
                // Remove existing nullable reference types
                return elementType.WithTrailingTrivia(node.GetTrailingTrivia());
            } else {
                return node.ReplaceNode(node.ElementType, elementType);
            }
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            return HandleTypeName(node, base.VisitIdentifierName(node));
        }

        public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
        {
            return HandleTypeName(node, base.VisitGenericName(node));
        }

        public override SyntaxNode? VisitPredefinedType(PredefinedTypeSyntax node)
        {
            return HandleTypeName(node, base.VisitPredefinedType(node));
        }

        public override SyntaxNode? VisitQualifiedName(QualifiedNameSyntax node)
        {
            return HandleTypeName(node, base.VisitQualifiedName(node));
        }

        private SyntaxNode? HandleTypeName(TypeSyntax node, SyntaxNode? newNode)
        {
            if (!GraphBuildingSyntaxVisitor.CanBeMadeNullableSyntax(node)) {
                return newNode;
            }
            var symbolInfo = semanticModel.GetSymbolInfo(node, cancellationToken);
            if (symbolInfo.Symbol is ITypeSymbol ty && ty.CanBeMadeNullable() && newNode is TypeSyntax newTypeSyntax) {
                var nullNode = mapping[node];
                if (nullNode.NullType == NullType.Nullable) {
                    return SyntaxFactory.NullableType(
                        elementType: newTypeSyntax.WithoutTrailingTrivia(),
                        questionToken: SyntaxFactory.Token(SyntaxKind.QuestionToken)
                    ).WithTrailingTrivia(newTypeSyntax.GetTrailingTrivia());
                }
            }
            return newNode;
        }

        public override SyntaxNode? VisitArrayType(ArrayTypeSyntax node)
        {
            var newNode = base.VisitArrayType(node);
            if (GraphBuildingSyntaxVisitor.CanBeMadeNullableSyntax(node) && newNode is TypeSyntax newTypeSyntax) {
                var nullNode = mapping[node];
                if (nullNode.NullType == NullType.Nullable) {
                    return SyntaxFactory.NullableType(
                        elementType: newTypeSyntax.WithoutTrailingTrivia(),
                        questionToken: SyntaxFactory.Token(SyntaxKind.QuestionToken)
                    ).WithTrailingTrivia(newTypeSyntax.GetTrailingTrivia());
                }
            }
            return newNode;
        }
    }
}
