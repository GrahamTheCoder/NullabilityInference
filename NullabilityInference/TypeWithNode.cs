﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Microsoft.CodeAnalysis;

namespace NullabilityInference
{
    /// <summary>
    /// Pairs a C# type with a nullability node that can be used to infer the nullability.
    /// </summary>
    [DebuggerDisplay("{Type}{Node}")]
    public readonly struct TypeWithNode
    {
        public readonly ITypeSymbol? Type;
        public readonly NullabilityNode Node;

        public TypeWithNode(ITypeSymbol? type, NullabilityNode node)
        {
            this.Type = type;
            this.Node = node;
        }
    }
}
